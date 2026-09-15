using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class RadioSessionService : IRadioSessionService, IPttTestDrain, IAsyncDisposable
{
    private readonly IRadioController _controller;
    private readonly ISettingsStore _settingsStore;
    private readonly IReadOnlyList<IRadioProtocolFactory> _protocolFactories;
    private readonly ILogger<RadioSessionService> _logger;
    private readonly TimeSpan _unkeyAttemptWaitTimeout;

    // T0-1: deliberately larger than the backends' own SemaphoreAcquireTimeout (10s), so a
    // genuinely wedged backend's own more actionable Critical log wins the race instead of a
    // coin-flip between two simultaneous timeouts.
    private static readonly TimeSpan UnkeyAttemptWaitTimeout = TimeSpan.FromSeconds(15);

    // T0-1: matches Core.Radio/RadioController's own identical constant/pattern for bounding a
    // protocol's DisposeAsync call.
    private static readonly TimeSpan ProtocolDisposeTimeout = TimeSpan.FromSeconds(10);

    public RadioSessionService(
        IRadioController controller, ISettingsStore settingsStore, IEnumerable<IRadioProtocolFactory> protocolFactories,
        ILogger<RadioSessionService> logger)
        : this(controller, settingsStore, protocolFactories, logger, unkeyAttemptWaitTimeoutForTests: null)
    {
    }

    /// <summary>Test-only: lets a test shrink the un-key retry's own per-attempt wait bound so a
    /// hung-attempt regression test doesn't need to wait out the real production timeout.</summary>
    internal RadioSessionService(
        IRadioController controller, ISettingsStore settingsStore, IEnumerable<IRadioProtocolFactory> protocolFactories,
        ILogger<RadioSessionService> logger, TimeSpan? unkeyAttemptWaitTimeoutForTests)
    {
        _controller = controller;
        _settingsStore = settingsStore;
        _protocolFactories = protocolFactories.ToList();
        _logger = logger;
        _unkeyAttemptWaitTimeout = unkeyAttemptWaitTimeoutForTests ?? UnkeyAttemptWaitTimeout;
    }

    public RadioState? LastKnownState => _controller.LastKnownState;

    public RadioCapabilities Capabilities => _controller.Capabilities;

    public string RigId => _controller.RigId;

    public bool IsGenuinelyConnected => _controller.IsGenuinelyConnected;

    public IObservable<RadioState> StateChanges => _controller.StateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _controller.ConnectionEvents;

    public async Task ConnectUsingSettingsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var radioSettings = appSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings)
            ?? new RadioConnectionSettings();
        var spec = radioSettings.ToConnectionSpec();
        Log.ResolvedSpecFromSettings(_logger, radioSettings.BackendId);
        await _controller.ConnectAsync(spec, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="IRadioSessionService.TestConnectionAsync"/>'s own doc comment -- a
    /// fresh, disposable <see cref="IRadioProtocol"/>, never the real <see cref="_controller"/>
    /// session. Same "exactly one factory match" resolution <see cref="IRadioController"/> itself
    /// uses internally (mirrored here rather than shared, since that resolution lives on the
    /// concrete <c>RadioController</c> class, not the <see cref="IRadioController"/> interface this
    /// service is scoped to depend on).</summary>
    public async Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default)
    {
        var matches = _protocolFactories.Where(f => f.CanHandle(spec)).ToList();
        if (matches.Count != 1)
        {
            var message = matches.Count == 0
                ? $"No backend registered for {spec.GetType().Name}."
                : $"{matches.Count} backends all claim {spec.GetType().Name} -- registration is ambiguous.";
            Log.TestConnectionResolutionFailed(_logger, spec.GetType().Name, matches.Count);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, message);
        }

        // Round-1 code-review finding (Tier A Batch 10 chunk 10c, real risk fixed): `Create` used to
        // run OUTSIDE this try -- a throwing factory leaked out of a method whose own interface
        // contract (IRadioSessionService.TestConnectionAsync's doc comment) says it always returns a
        // result, never throws.
        try
        {
            var protocol = matches[0].Create(spec);
            try
            {
                await protocol.PollAsync(ct).ConfigureAwait(false);
                Log.TestConnectionSucceeded(_logger, protocol.RigId);
                return new RadioConnectionTestResult(true, protocol.RigId, protocol.Capabilities, null);
            }
            catch (Exception ex)
            {
                Log.TestConnectionFailed(_logger, spec.GetType().Name, ex);
                return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
            }
            finally
            {
                // Round-1 code-review finding: a throwing DisposeAsync here used to REPLACE whatever
                // the try/catch above already decided to return, masking the real poll failure behind
                // an unrelated teardown error -- the exact hazard RadioController.cs's own
                // DisconnectAsync already guards against for the identical `protocol.DisposeAsync()`
                // call ("a broken backend's own DisposeAsync must not abort the rest of the
                // teardown"). Contained here the same way, not propagated.
                try
                {
                    await protocol.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.TestConnectionDisposeFailed(_logger, spec.GetType().Name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.TestConnectionFailed(_logger, spec.GetType().Name, ex);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
        }
    }

    private readonly object _pttLifetimeGate = new();
    private readonly CancellationTokenSource _pttShutdown = new();
    private TaskCompletionSource? _pttCompletion;
    private TaskCompletionSource<bool>? _pttDrainCompletion;
    // Set by DrainPttTestsAsync, not by DisposeAsync: the host's exit path calls the drain directly,
    // so a gate keyed on the dispose path alone would never close there and a PTT test started
    // during shutdown could key the rig.
    private bool _pttStopping;

    public Task<RadioConnectionTestResult> TestPttAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct = default)
    {
        TaskCompletionSource? completion = null;
        lock (_pttLifetimeGate)
        {
            if (!_pttStopping && _pttCompletion is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pttCompletion = completion;
            }
        }

        if (completion is null)
        {
            Log.TestPttAlreadyInFlight(_logger, spec.GetType().Name);
            return Task.FromResult(new RadioConnectionTestResult(false, null, RadioCapabilities.None,
                "A PTT test or shutdown is already in progress."));
        }
        return RunOwnedPttTestAsync(spec, duration, completion, ct);
    }

    private async Task<RadioConnectionTestResult> RunOwnedPttTestAsync(
        RadioConnectionSpec spec, TimeSpan duration, TaskCompletionSource completion, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _pttShutdown.Token);
            return await TestPttCoreAsync(spec, duration, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_pttLifetimeGate)
            {
                _pttCompletion = null;
                completion.TrySetResult();
            }
        }
    }

    /// <summary>See <see cref="IPttTestDrain.DrainPttTestsAsync"/>. Deliberately not <c>async</c>:
    /// callers rely on repeated calls returning the identical task instance, which an async wrapper
    /// around a cached task would not give them. The core starts OUTSIDE the lock and on this
    /// thread, not through <see cref="Task.Run(System.Func{Task})"/>, so that
    /// <see cref="_pttShutdown"/> is already flagged cancelled by the time this returns; deferring
    /// the prefix to the thread pool let a poll complete first and proceed as though no shutdown
    /// were under way. What that buys is narrower than it looks: <c>CancelAsync</c> flips the token
    /// synchronously but runs registered callbacks asynchronously, so each test's own linked source
    /// still learns of it slightly later. The drain waits for the un-key either way, so a test
    /// admitted just before this point cannot leave the transmitter keyed. Running the prefix on the
    /// caller's thread while holding <see cref="_pttLifetimeGate"/> is what the separate completion
    /// source avoids, since <see cref="RunOwnedPttTestAsync"/>'s own <c>finally</c> takes that same
    /// gate.</summary>
    public Task<bool> DrainPttTestsAsync(TimeSpan? timeout = null)
    {
        TaskCompletionSource<bool> completion;
        Task active;
        lock (_pttLifetimeGate)
        {
            if (_pttDrainCompletion is not null) return _pttDrainCompletion.Task;
            _pttStopping = true;
            active = _pttCompletion?.Task ?? Task.CompletedTask;
            completion = _pttDrainCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Three 15s off attempts, retry delays and 10s protocol disposal must finish before the host
        // starts its separate general DI teardown timeout. Native hangs remain bounded.
        _ = PublishDrainResultAsync(active, timeout ?? TimeSpan.FromSeconds(75), completion);
        return completion.Task;
    }

    /// <summary>Bridges the never-faulting core onto the cached completion. The try/catch is a
    /// backstop only: if the core ever did fault, an uncompleted completion source would hang the
    /// host's exit, which is strictly worse than reporting an unclean drain.</summary>
    private async Task PublishDrainResultAsync(Task active, TimeSpan timeout, TaskCompletionSource<bool> completion)
    {
        try
        {
            completion.TrySetResult(await DrainPttCoreAsync(active, timeout).ConfigureAwait(false));
        }
        catch (Exception)
        {
            completion.TrySetResult(false);
        }
    }

    public ValueTask DisposeAsync() => new(DrainPttTestsAsync());

    /// <summary>Every statement lives inside the one try, and every log call goes through
    /// <see cref="LogSafely"/>, so the returned task can never fault. That matters beyond tidiness:
    /// this instance is a container-owned singleton that is also disposed explicitly by the host, so
    /// a faulted cached task would be re-thrown inside the container's disposal loop and abandon
    /// every disposable it had not reached yet -- including the live radio connection.</summary>
    private async Task<bool> DrainPttCoreAsync(Task active, TimeSpan timeout)
    {
        var drained = false;
        try
        {
            LogSafely(() => Log.TestPttShutdownStarted(_logger));
            var cancellation = CancelPttTestsAsync();
            await Task.WhenAll(active, cancellation).WaitAsync(timeout).ConfigureAwait(false);
            drained = true;
        }
        catch (Exception ex)
        {
            LogSafely(() => Log.TestPttShutdownFailed(_logger, ex));
        }

        // Retain the CTS: an admitted native operation may outlive the timeout and still use it.
        return drained;
    }

    /// <summary>A throwing logger provider must not fault the drain task. Microsoft.Extensions.Logging
    /// aggregates provider exceptions and rethrows them, so an unguarded log call anywhere in the
    /// drain would reintroduce the abandoned-disposal-chain defect this shape exists to prevent.</summary>
    private static void LogSafely(Action log)
    {
        try { log(); }
        catch (Exception) { /* diagnostics only; never worth failing shutdown over */ }
    }

    private async Task CancelPttTestsAsync()
    {
        try { await _pttShutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { LogSafely(() => Log.TestPttShutdownCancellationFailed(_logger, ex)); }
    }

    /// <summary>See <see cref="IRadioSessionService.TestPttAsync"/>'s own doc comment for the safety
    /// contract. Structured as a linear "compute a result, then always un-key/dispose" sequence
    /// rather than nested try/finally, specifically so the un-key-retry-exhausted case can override
    /// an already-computed success result before it's returned -- a `finally` block can't cleanly
    /// replace a value a `return` inside its `try` already committed to.
    ///
    /// <b>T0-1 known limit:</b> the initial <c>PollAsync</c>/<c>SetPttAsync(true)</c> calls below
    /// (this call's OWN key attempt) are still genuinely unbounded if THIS call's own native/COM
    /// operation wedges -- a backend's own semaphore timeout (e.g. Hamlib's) only protects a LATER
    /// caller queued behind an already-wedged call, not the call that is currently doing the
    /// wedging. Only the un-key retry (<see cref="TryUnkeyWithRetryAsync"/>) and the final dispose
    /// are bounded. <see cref="SstvSessionService"/> watchdogs its own key command; this method does
    /// not, unlike that class.</summary>
    private async Task<RadioConnectionTestResult> TestPttCoreAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var matches = _protocolFactories.Where(f => f.CanHandle(spec)).ToList();
            if (matches.Count != 1)
            {
                var message = matches.Count == 0
                    ? $"No backend registered for {spec.GetType().Name}."
                    : $"{matches.Count} backends all claim {spec.GetType().Name} -- registration is ambiguous.";
                Log.TestPttResolutionFailed(_logger, spec.GetType().Name, matches.Count);
                return new RadioConnectionTestResult(false, null, RadioCapabilities.None, message);
            }

            try
            {
                var protocol = matches[0].Create(spec);
                // Round-review finding (radio-safety-critical): must be set BEFORE the SetPttAsync
                // call, not after it returns -- rig_set_ptt can return a non-OK code AFTER the rig
                // has already physically keyed (confirmed against hamlib/src/rig.c: a VFO-revert
                // call after the real PTT-on command can fail and become the returned error). Setting
                // this only on a successful return skipped the un-key retry on exactly that path,
                // the one case blocker #2 exists to cover. Cost of the false-positive (SetPttAsync
                // throws before ever reaching the rig) is one harmless extra PTT-OFF attempt; cost of
                // the false-negative this replaces was a stuck-keyed transmitter.
                var keyAttempted = false;
                RadioConnectionTestResult result;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await protocol.PollAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    if (!protocol.Capabilities.HasFlag(RadioCapabilities.PttControl))
                    {
                        Log.TestPttNoCapability(_logger, protocol.RigId);
                        result = new RadioConnectionTestResult(false, protocol.RigId, protocol.Capabilities, "This rig model has no PTT control.");
                    }
                    else
                    {
                        keyAttempted = true;
                        await protocol.SetPttAsync(true, ct).ConfigureAwait(false);

                        try
                        {
                            await Task.Delay(duration, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected -- the operator clicked Stop, or the dialog closed. Un-keying
                            // below still runs regardless.
                        }

                        Log.TestPttSucceeded(_logger, protocol.RigId);
                        result = new RadioConnectionTestResult(true, protocol.RigId, protocol.Capabilities, null);
                    }
                }
                catch (Exception ex)
                {
                    Log.TestPttFailed(_logger, spec.GetType().Name, ex);
                    result = new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
                }

                if (keyAttempted)
                {
                    var unkeyed = await TryUnkeyWithRetryAsync(protocol).ConfigureAwait(false);
                    if (!unkeyed)
                    {
                        result = result with
                        {
                            Success = false,
                            ErrorMessage = "PTT test finished, but turning PTT back off failed -- check your rig; it may still be transmitting.",
                        };
                    }
                }

                try
                {
                    // T0-1: bounded the same way Core.Radio/RadioController.DisconnectAsync already
                    // bounds its own protocol dispose -- without this, a wedged Hamlib _lock (still
                    // held after the un-key retry above gives up) would hang this call forever,
                    // leaving _testPttInFlight's finally-reset never reached.
                    // CancellationToken.None, not `ct`: dispose must always be attempted regardless
                    // of caller cancellation (same reasoning as the un-key retry above), bounded only
                    // by ProtocolDisposeTimeout itself.
                    await protocol.DisposeAsync().AsTask().WaitAsync(ProtocolDisposeTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Log.TestPttDisposeTimedOut(_logger, spec.GetType().Name, ProtocolDisposeTimeout);
                }
                catch (Exception ex)
                {
                    Log.TestPttDisposeFailed(_logger, spec.GetType().Name, ex);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.TestPttFailed(_logger, spec.GetType().Name, ex);
                return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
            }
        }
        catch (Exception ex)
        {
            Log.TestPttFailed(_logger, spec.GetType().Name, ex);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
        }
    }

    /// <summary>Retries the un-key call up to 3 times, always on <see cref="CancellationToken.None"/>
    /// so neither the caller's own cancellation nor a Stop click can abort it -- see
    /// <see cref="TestPttAsync"/>'s own doc comment for why CAT ("RIG") PTT specifically has no
    /// dispose-time rescue path.
    ///
    /// <b>T0-1:</b> each attempt's WAIT is bounded independently of whatever timeout the specific
    /// backend happens to enforce internally (<see cref="PttUnkeyHelper"/>), so this loop's own
    /// correctness doesn't depend transitively on every current/future <see cref="IRadioProtocol"/>
    /// backend bounding itself. The command itself still runs on <see cref="CancellationToken.None"/>
    /// (never cancelled) either way.</summary>
    private async Task<bool> TryUnkeyWithRetryAsync(IRadioProtocol protocol)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var waitCts = new CancellationTokenSource(_unkeyAttemptWaitTimeout);
            var result = await PttUnkeyHelper.TryUnkeyBoundedAsync(
                protocol.SetPttAsync,
                waitCts.Token,
                onLateFailure: ex => Log.TestPttLateUnkeyAttemptFailed(_logger, protocol.RigId, ex)).ConfigureAwait(false);

            if (result.Success)
            {
                return true;
            }

            if (attempt == maxAttempts)
            {
                Log.TestPttUnkeyFailed(_logger, protocol.RigId, result.Exception!);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
            }
        }

        return false;
    }

    public Task DisconnectAsync() => _controller.DisconnectAsync();

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => _controller.SetFrequencyAsync(hz, ct);

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => _controller.SetModeAsync(mode, ct);

    public Task SetPttAsync(bool tx, CancellationToken ct = default) => _controller.SetPttAsync(tx, ct);

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct = default) => _controller.SetBandwidthAsync(bandwidthHz, ct);

    public async Task<IReadOnlyList<FrequencyPreset>> GetFrequencyPresetsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = appSettings.GetSection(FrequencyPresetsSettings.SectionKey, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings)
            ?? new FrequencyPresetsSettings();
        return section.Presets;
    }

    public async Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(
            appSettings => appSettings.WithSection(
                FrequencyPresetsSettings.SectionKey,
                new FrequencyPresetsSettings { Presets = presets },
                FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings),
            ct).ConfigureAwait(false);
    }

    public async Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = appSettings.GetSection(RadioSafetySettings.SectionKey, RadioSafetySettingsJsonContext.Default.RadioSafetySettings)
            ?? new RadioSafetySettings();
        return new RadioSafetySpec(section.SwrCutoffEnabled, section.SwrCutoffThreshold);
    }

    public async Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(
            appSettings => appSettings.WithSection(
                RadioSafetySettings.SectionKey,
                new RadioSafetySettings { SwrCutoffEnabled = spec.SwrCutoffEnabled, SwrCutoffThreshold = spec.SwrCutoffThreshold },
                RadioSafetySettingsJsonContext.Default.RadioSafetySettings),
            ct).ConfigureAwait(false);
        RaiseSafetySettingsChanged(spec);
    }

    public event Action<RadioSafetySpec>? SafetySettingsChanged;

    public async Task<bool> GetSsbAsPktPreferenceAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = appSettings.GetSection(RadioOperatingPreferencesSettings.SectionKey, RadioOperatingPreferencesSettingsJsonContext.Default.RadioOperatingPreferencesSettings)
            ?? new RadioOperatingPreferencesSettings();
        return section.SsbAsPkt;
    }

    public async Task SaveSsbAsPktPreferenceAsync(bool value, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(
            appSettings => appSettings.WithSection(
                RadioOperatingPreferencesSettings.SectionKey,
                new RadioOperatingPreferencesSettings { SsbAsPkt = value },
                RadioOperatingPreferencesSettingsJsonContext.Default.RadioOperatingPreferencesSettings),
            ct).ConfigureAwait(false);
    }

    // T1-7 (production_audit.md): the disk write above already succeeded by the time this runs --
    // a throwing subscriber (a real one exists, TxControlsPaneViewModel.OnSafetySettingsChanged)
    // used to propagate straight out of SaveSafetySettingsAsync, which OptionsWindowViewModel's own
    // caller wraps in one big multi-section Save/Apply try/catch -- reporting the WHOLE save as
    // failed even though this specific write already landed. Same guarded-raise shape as
    // SstvSessionService.RaiseCapturePausedForTransmitChanged.
    private void RaiseSafetySettingsChanged(RadioSafetySpec spec)
    {
        try
        {
            SafetySettingsChanged?.Invoke(spec);
        }
        catch (Exception ex)
        {
            Log.SafetySettingsChangedHandlerFailed(_logger, ex);
        }
    }

    /// <summary>See <see cref="IRadioSessionService.RequestHamlibLibraryPathAsync"/>.</summary>
    public Task<HamlibLibraryReloadResult?> RequestHamlibLibraryPathAsync(string? overridePath, CancellationToken ct = default)
    {
        var reconfigurable = _protocolFactories.OfType<IHamlibLibraryReconfiguration>().FirstOrDefault();
        return reconfigurable is null
            ? Task.FromResult<HamlibLibraryReloadResult?>(null)
            : ReloadHamlibLibraryAsync(reconfigurable, overridePath, ct);
    }

    private async Task<HamlibLibraryReloadResult?> ReloadHamlibLibraryAsync(IHamlibLibraryReconfiguration reconfigurable, string? overridePath, CancellationToken ct)
    {
        var result = await reconfigurable.ReloadLibraryAsync(overridePath, ct).ConfigureAwait(false);
        if (result.Applied)
        {
            Log.HamlibLibraryReloaded(_logger, result.ResolvedPath ?? "(unknown)");
        }
        else
        {
            Log.HamlibLibraryReloadRejected(_logger, string.Join("; ", result.Attempts));
        }

        return result;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Draining temporary PTT tests before shutdown")]
        public static partial void TestPttShutdownStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "PTT shutdown cancellation callback failed")]
        public static partial void TestPttShutdownCancellationFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Critical, Message = "PTT test cleanup did not complete during shutdown; check the transmitter")]
        public static partial void TestPttShutdownFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Resolved radio spec from settings: backend={BackendId}")]
        public static partial void ResolvedSpecFromSettings(ILogger logger, string backendId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Hamlib library reloaded live: {Path}")]
        public static partial void HamlibLibraryReloaded(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hamlib library reload rejected: {Attempts}")]
        public static partial void HamlibLibraryReloadRejected(ILogger logger, string attempts);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestConnectionAsync: factory resolution failed for {SpecType}: {MatchCount} match(es)")]
        public static partial void TestConnectionResolutionFailed(ILogger logger, string specType, int matchCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "TestConnectionAsync succeeded: rigId={RigId}")]
        public static partial void TestConnectionSucceeded(ILogger logger, string rigId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestConnectionAsync failed for {SpecType}")]
        public static partial void TestConnectionFailed(ILogger logger, string specType, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestConnectionAsync: disposing the throwaway test protocol for {SpecType} failed; the poll result above still stands")]
        public static partial void TestConnectionDisposeFailed(ILogger logger, string specType, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: rejected for {SpecType} -- a PTT test is already in progress")]
        public static partial void TestPttAlreadyInFlight(ILogger logger, string specType);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A SafetySettingsChanged subscriber threw -- the settings write itself already succeeded")]
        public static partial void SafetySettingsChangedHandlerFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: factory resolution failed for {SpecType}: {MatchCount} match(es)")]
        public static partial void TestPttResolutionFailed(ILogger logger, string specType, int matchCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: rig {RigId} reports no PTT control capability -- not attempting to key")]
        public static partial void TestPttNoCapability(ILogger logger, string rigId);

        [LoggerMessage(Level = LogLevel.Information, Message = "TestPttAsync succeeded: rigId={RigId}")]
        public static partial void TestPttSucceeded(ILogger logger, string rigId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync failed for {SpecType}")]
        public static partial void TestPttFailed(ILogger logger, string specType, Exception exception);

        [LoggerMessage(Level = LogLevel.Critical, Message = "TestPttAsync: un-keying rig {RigId} failed after every retry -- it may still be physically transmitting")]
        public static partial void TestPttUnkeyFailed(ILogger logger, string rigId, Exception exception);

        // T0-1: deliberately Warning, not Critical -- this fires for an EARLIER, already-abandoned
        // un-key attempt's own late failure, which can happen even after a LATER attempt already
        // succeeded (e.g. a stale ObjectDisposedException once the protocol is disposed). Reusing
        // TestPttUnkeyFailed's Critical "may still be physically transmitting" wording here would be
        // a false safety alarm on a rig that is demonstrably already un-keyed.
        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: un-key attempt for rig {RigId} finished late and failed (a later attempt already succeeded or the retry loop already gave up)")]
        public static partial void TestPttLateUnkeyAttemptFailed(ILogger logger, string rigId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: disposing the throwaway test protocol for {SpecType} failed; the test result above still stands")]
        public static partial void TestPttDisposeFailed(ILogger logger, string specType, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: disposing the throwaway test protocol for {SpecType} timed out after {Timeout}; the test result above still stands")]
        public static partial void TestPttDisposeTimedOut(ILogger logger, string specType, TimeSpan timeout);
    }
}
