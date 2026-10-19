using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>DRM receive, beside SSTV RX: one <see cref="DrmReceiveWorker"/> per receive session.</summary>
/// <remarks>
/// <para><b>Ownership.</b> <see cref="_drmWorker"/> changes hands only through <c>Interlocked</c>: the publish helper
/// (Start, and the live toggle path, both under <see cref="_rxTransitionGate"/>), Stop (under the gate, or on the drain
/// thread from the maintenance stop) and the dispose backstop (no gate). Whoever takes a worker out detaches its handler
/// and moves it to <see cref="_drmWorkersRetiring"/>.</para>
/// <para><b>Retiring workers</b> drain their queue on their own task. Stop never awaits them (the drain-thread stop
/// path must stay synchronous); the next Start awaits all of them, bounded, before opening capture, and dispose
/// cancels all of them under one bound.</para>
/// <para><b>Toggle.</b> <see cref="_drmRxEnabled"/> and <see cref="_drmToggleGeneration"/> change together under
/// <see cref="_drmToggleLock"/>, after the setting is persisted. Start and file decode seed the flag from settings only
/// if no Set published during their load. A Set that cannot take the gate is applied when the holder releases it
/// (<see cref="ReleaseRxTransitionGate"/>).</para>
/// </remarks>
public sealed partial class SstvSessionService
{
    private readonly IDrmReceiverFactory? _drmReceiverFactory;
    private readonly object _drmToggleLock = new();
    private readonly SemaphoreSlim _drmToggleSetLock = new(1, 1);
    private readonly object _drmRetiringLock = new();
    private readonly List<DrmReceiveWorker> _drmWorkersRetiring = [];
    private DrmReceiveWorker? _drmWorker;
    private long _drmGeneration;
    private volatile bool _drmRxEnabled = DrmRxSettings.DefaultRxEnabled;
    private long _drmToggleGeneration;

    // The toggle generation the worker state last reflected; written under the gate, read just after its release
    // (a spurious reconcile is harmless: the apply is idempotent).
    private long _drmAppliedToggleGeneration;
    private int _drmReconcileScheduled;

    /// <summary>See <see cref="ISstvSessionService.DrmSignalStateChanged"/>.</summary>
    public event Action<DrmSignalState>? DrmSignalStateChanged;

    /// <summary>See <see cref="ISstvSessionService.DrmFileReceived"/>.</summary>
    public event Action<DrmReceivedFile>? DrmFileReceived;

    /// <summary>See <see cref="ISstvSessionService.DrmAlreadyReceived"/>.</summary>
    public event Action<string>? DrmAlreadyReceived;

    /// <inheritdoc/>
    public bool IsDrmReceiveActive => Volatile.Read(ref _drmWorker) is not null;

    /// <inheritdoc/>
    public async Task<bool> GetDrmReceiveEnabledAsync(CancellationToken ct = default)
    {
        AppSettings appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return appSettings.GetSection(DrmRxSettings.SectionKey, DrmRxSettingsJsonContext.Default.DrmRxSettings)?.RxEnabled
            ?? DrmReceiveDefaults.Enabled;
    }

    /// <inheritdoc/>
    public async Task SetDrmReceiveEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Persist first, then publish: a Start whose settings load ran before this persist then always sees the
        // generation move, so its stale seed is skipped or reconciled. Serialized so the flag follows persist order.
        await _drmToggleSetLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await _settingsStore.UpdateAsync(
                    s =>
                    {
                        // An unreadable section is kept verbatim under its .unreadable key, then rewritten.
                        if (!s.TryReadSection(DrmRxSettings.SectionKey, DrmRxSettingsJsonContext.Default.DrmRxSettings, out var current, out var error))
                        {
                            SafeLog(() => Log.DrmRxSectionSetAside(_logger, s.Sections.ContainsKey(DrmRxSettings.SectionKey + AppSettingsSectionExtensions.UnreadableSuffix), error));
                            s = s.SetSectionAside(DrmRxSettings.SectionKey);
                        }

                        return s.WithSection(
                            DrmRxSettings.SectionKey,
                            (current ?? new DrmRxSettings()) with { RxEnabled = enabled },
                            DrmRxSettingsJsonContext.Default.DrmRxSettings);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SafeLog(() => Log.DrmRxTogglePersistFailed(_logger, enabled, ex));
                throw;
            }

            lock (_drmToggleLock)
            {
                _drmRxEnabled = enabled;
                _drmToggleGeneration++;
            }
        }
        finally
        {
            _drmToggleSetLock.Release();
        }

        SafeLog(() => Log.DrmRxToggled(_logger, enabled));

        // A busy gate is fine: its holder applies the change on release.
        if (_rxTransitionGate.Wait(0, CancellationToken.None))
        {
            try
            {
                ApplyDrmToggleLocked();
            }
            finally
            {
                ReleaseRxTransitionGate();
            }
        }
    }

    private long ReadDrmToggleGeneration()
    {
        lock (_drmToggleLock)
            return _drmToggleGeneration;
    }

    // Start's seed: skipped when a Set landed after generationBeforeLoad, so a stale load never overwrites it.
    // Also skipped when the section is unreadable: the toggle then keeps the last persisted or Set value.
    private void SeedDrmToggle(AppSettings appSettings, long generationBeforeLoad)
    {
        if (!TryReadSectionForRx(appSettings, DrmRxSettings.SectionKey, DrmRxSettingsJsonContext.Default.DrmRxSettings, out var section))
            return;

        bool persisted = section?.RxEnabled ?? DrmRxSettings.DefaultRxEnabled;
        lock (_drmToggleLock)
        {
            if (_drmToggleGeneration == generationBeforeLoad)
                _drmRxEnabled = persisted;
        }
    }

    /// <summary>Releases <see cref="_rxTransitionGate"/>; if a toggle change landed while it was held, schedules one
    /// reconcile that takes the gate normally and applies the current value.</summary>
    private void ReleaseRxTransitionGate()
    {
        // Released first: a Set that published while this holder had the gate is then always seen as stale here.
        _rxTransitionGate.Release();
        bool stale = Volatile.Read(ref _drmAppliedToggleGeneration) != ReadDrmToggleGeneration();
        if (!stale || _disposed || Interlocked.CompareExchange(ref _drmReconcileScheduled, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                acquired = await _rxTransitionGate.WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "DRM RX toggle reconcile", ex));
            }

            // Cleared before applying, so a change during the apply schedules a fresh reconcile at release.
            Volatile.Write(ref _drmReconcileScheduled, 0);
            if (!acquired)
            {
                SafeLog(() => Log.DrmReconcileGateTimedOut(_logger));
                return;
            }

            try
            {
                if (!_disposed)
                    ApplyDrmToggleLocked();
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "DRM RX toggle reconcile", ex));
            }
            finally
            {
                ReleaseRxTransitionGate();
            }
        });
    }

    // Caller holds _rxTransitionGate.
    private void ApplyDrmToggleLocked()
    {
        bool enabled;
        lock (_drmToggleLock)
        {
            enabled = _drmRxEnabled;
            Volatile.Write(ref _drmAppliedToggleGeneration, _drmToggleGeneration);
        }

        if (!_isReceiving)
            return;
        if (enabled && Volatile.Read(ref _drmWorker) is null)
            StartDrmWorkerLocked();
        else if (!enabled)
            RetireDrmWorker();
    }

    /// <summary>Builds and publishes a worker at the decoder's rate when DRM RX is on and the rate is supported.
    /// Caller holds <see cref="_rxTransitionGate"/> and capture is open.</summary>
    private void StartDrmWorkerLocked()
    {
        bool enabled;
        lock (_drmToggleLock)
        {
            enabled = _drmRxEnabled;
            Volatile.Write(ref _drmAppliedToggleGeneration, _drmToggleGeneration);
        }

        if (!enabled || _drmReceiverFactory is null)
            return;
        int rate = _decoder.SampleRate;
        if (!_drmReceiverFactory.IsSupportedRate(rate))
        {
            SafeLog(() => Log.DrmRxUnsupportedRate(_logger, rate));
            return;
        }

        DrmReceiveWorker worker;
        try
        {
            worker = new DrmReceiveWorker(
                _drmReceiverFactory.Create(rate), Interlocked.Increment(ref _drmGeneration), _logger,
                RaiseDrmFileReceived, RaiseDrmSignalState, RaiseDrmAlreadyReceived);
            worker.Start();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "DRM RX worker start", ex));
            return;
        }

        PublishDrmWorker(worker);
        SafeLog(() => Log.DrmRxStarted(_logger, worker.Generation, rate));
    }

    /// <summary>Attaches, then publishes with a full fence and re-checks <see cref="_disposed"/>; pairs with
    /// <see cref="DisposeAsync"/>'s <c>_disposed = true; MemoryBarrier</c> and its own Exchange, so one side always
    /// sees the other and exactly one of them stops the worker.</summary>
    private void PublishDrmWorker(DrmReceiveWorker worker)
    {
        _audioEngine.SamplesCaptured += worker.Handler;
        DrmReceiveWorker? previous = Interlocked.Exchange(ref _drmWorker, worker);
        if (previous is not null)
            RetireTaken(previous);
        if (_disposed && Interlocked.CompareExchange(ref _drmWorker, null, worker) == worker)
            RetireTaken(worker);
    }

    /// <summary>Takes the live worker out, retires it without waiting and raises the terminal state. Synchronous:
    /// also called on the drain thread by the maintenance stop.</summary>
    private void RetireDrmWorker()
    {
        DrmReceiveWorker? worker = Interlocked.Exchange(ref _drmWorker, null);
        if (worker is null)
            return;
        RetireTaken(worker);
        RaiseDrmSignalState(DrmSignalState.Inactive(Interlocked.Increment(ref _drmGeneration)));
        SafeLog(() => Log.DrmRxStopped(_logger, worker.Generation));
    }

    private void RetireTaken(DrmReceiveWorker worker)
    {
        _audioEngine.SamplesCaptured -= worker.Handler;
        lock (_drmRetiringLock)
        {
            // Pruned here too, so toggling within one RX session does not accumulate finished workers.
            _drmWorkersRetiring.RemoveAll(w => w.Completion.IsCompleted);
            _drmWorkersRetiring.Add(worker);
        }

        worker.Retire();
    }

    /// <summary>Awaits every retiring worker, bounded by <see cref="_cleanupTimeout"/>; on timeout logs and proceeds,
    /// keeping the stuck ones for the next wait and for dispose.</summary>
    private async Task AwaitRetiringDrmWorkersAsync()
    {
        Task[] pending;
        lock (_drmRetiringLock)
        {
            _drmWorkersRetiring.RemoveAll(w => w.Completion.IsCompleted);
            pending = _drmWorkersRetiring.Select(w => w.Completion).ToArray();
        }

        if (pending.Length == 0)
            return;
        try
        {
            await Task.WhenAll(pending).WaitAsync(_cleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SafeLog(() => Log.DrmRetiringWaitTimedOut(_logger, pending.Length, _cleanupTimeout));
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "DRM RX retiring-worker wait", ex));
        }

        lock (_drmRetiringLock)
            _drmWorkersRetiring.RemoveAll(w => w.Completion.IsCompleted);
    }

    /// <summary>Dispose backstop: takes the live worker (Stop may have timed out on the gate), then cancels it and every
    /// retiring worker and awaits them all under one bound.</summary>
    private async Task StopAllDrmWorkersForDisposeAsync()
    {
        DrmReceiveWorker? live = Interlocked.Exchange(ref _drmWorker, null);
        if (live is not null)
        {
            _audioEngine.SamplesCaptured -= live.Handler;
            lock (_drmRetiringLock)
                _drmWorkersRetiring.Add(live);
        }

        DrmReceiveWorker[] all;
        lock (_drmRetiringLock)
            all = [.. _drmWorkersRetiring];
        if (all.Length == 0)
            return;
        Task[] tasks = all.Select(w => w.Cancel()).ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(_cleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SafeLog(() => Log.DrmRetiringWaitTimedOut(_logger, all.Length, _cleanupTimeout));
        }
    }

    private bool CanDecodeDrmFileAt(int sampleRate) =>
        _drmRxEnabled && _drmReceiverFactory is not null && _drmReceiverFactory.IsSupportedRate(sampleRate);

    /// <summary>A fresh receiver for one file decode at the file's rate, run synchronously on the file-decode thread.</summary>
    private FileDrmReceiver? CreateFileDrmReceiver(int sampleRate)
    {
        try
        {
            return new FileDrmReceiver(this, _drmReceiverFactory!.Create(sampleRate), Interlocked.Increment(ref _drmGeneration));
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "DRM RX file receiver start", ex));
            return null;
        }
    }

    /// <summary>The file-decode DRM pass: <c>Reset</c> before and after, events raised on the file-decode thread, a
    /// receiver failure logged once and the receiver reset, never failing the SSTV decode.</summary>
    private sealed class FileDrmReceiver
    {
        // About the harness's 8 trailing 4096-sample blocks at 48 kHz, so the last frame clears the pipeline.
        private const double TailSeconds = 0.7;

        private readonly SstvSessionService _owner;
        private readonly IDrmReceiver _receiver;
        private readonly long _generation;
        private bool _failureLogged;

        public FileDrmReceiver(SstvSessionService owner, IDrmReceiver receiver, long generation)
        {
            _owner = owner;
            _receiver = receiver;
            _generation = generation;
            _receiver.FileReceived += owner.RaiseDrmFileReceived;
            _receiver.AlreadyReceived += owner.RaiseDrmAlreadyReceived;
            _receiver.SignalStateChanged += s => owner.RaiseDrmSignalState(s with { Generation = _generation });
            Guard(_receiver.Reset);
        }

        public void Push(ReadOnlySpan<float> samples)
        {
            try
            {
                _receiver.Push(samples);
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        public void FinishFile() => Guard(() => _receiver.ZeroFill((int)(_receiver.CaptureRate * TailSeconds)));

        public void Close()
        {
            Guard(_receiver.Reset);
            _owner.RaiseDrmSignalState(DrmSignalState.Inactive(Interlocked.Increment(ref _owner._drmGeneration)));
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        private void Failed(Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                SafeLog(() => Log.DrmFileReceiverFailed(_owner._logger, ex));
            }

            try
            {
                _receiver.Reset();
            }
            catch (Exception)
            {
                // Already logged once; the pass continues on the next chunk.
            }
        }
    }

    // Each raise is isolated: a throwing subscriber must not reach the worker, the drain thread or a file decode.
    private void RaiseDrmSignalState(DrmSignalState state)
    {
        try
        {
            DrmSignalStateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.DrmHandlerFailed(_logger, nameof(DrmSignalStateChanged), ex));
        }
    }

    private void RaiseDrmFileReceived(DrmReceivedFile file)
    {
        SafeLog(() => Log.DrmFileReceived(_logger, file.FileName, file.Data.Length, file.Callsign));
        try
        {
            DrmFileReceived?.Invoke(file);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.DrmHandlerFailed(_logger, nameof(DrmFileReceived), ex));
        }
    }

    private void RaiseDrmAlreadyReceived(string qsstvName)
    {
        try
        {
            DrmAlreadyReceived?.Invoke(qsstvName);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.DrmHandlerFailed(_logger, nameof(DrmAlreadyReceived), ex));
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Unreadable DrmRx settings section kept as DrmRx.unreadable (replaced an earlier copy: {ReplacedEarlier}) before saving the HamDRM receive setting")]
        public static partial void DrmRxSectionSetAside(ILogger logger, bool replacedEarlier, Exception? ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM RX started (worker {Generation}, {SampleRate} Hz)")]
        public static partial void DrmRxStarted(ILogger logger, long generation, int sampleRate);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM RX stopped (worker {Generation})")]
        public static partial void DrmRxStopped(ILogger logger, long generation);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM RX not started: {SampleRate} Hz is below the 8000 Hz minimum or above 192000 Hz")]
        public static partial void DrmRxUnsupportedRate(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Error, Message = "DRM RX toggle (enabled={Enabled}) could not be saved; nothing changed")]
        public static partial void DrmRxTogglePersistFailed(ILogger logger, bool enabled, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM RX toggled: enabled={Enabled}")]
        public static partial void DrmRxToggled(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM RX toggle reconcile timed out waiting for the RX transition gate; the current holder re-schedules it on release")]
        public static partial void DrmReconcileGateTimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} retiring DRM RX worker(s) did not finish within {Timeout}; proceeding")]
        public static partial void DrmRetiringWaitTimedOut(ILogger logger, int count, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM file received: {FileName}, {Bytes} bytes, callsign '{Callsign}'")]
        public static partial void DrmFileReceived(ILogger logger, string fileName, int bytes, string callsign);

        [LoggerMessage(Level = LogLevel.Information, Message = "File decode at {FileRate} Hz: DRM-only pass (SSTV decodes at {DecoderRate} Hz)")]
        public static partial void DrmOnlyFileDecode(ILogger logger, int fileRate, int decoderRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM RX file receiver threw; receiver reset, decode continues")]
        public static partial void DrmFileReceiverFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A {Event} handler threw")]
        public static partial void DrmHandlerFailed(ILogger logger, string @event, Exception ex);
    }
}
