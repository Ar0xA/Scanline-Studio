using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService : ISstvSessionService
{
    private readonly IAudioEngine _audioEngine;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly ISettingsStore _settingsStore;
    private readonly ISstvDecoder _decoder;
    private readonly ISstvEncoder _encoder;
    private readonly IMacroTextResolver _macroTextResolver;
    private readonly IRadioSessionService _radioSession;
    private readonly ILogger<SstvSessionService> _logger;
    private readonly TimeSpan _cleanupTimeout;
    private readonly TimeSpan _playbackStopWaitBudget;
    private readonly TimeSpan _inFlightKeyedTransmitWait;
    private readonly Action<ReadOnlyMemory<float>> _decoderHandler;
    private readonly Action<ReadOnlyMemory<float>> _waterfallHandler;
    // Ultracode audit finding #34's OnDecoderRestartCriticallyOverdue can now call StopReceivingAsync
    // (which writes this) from the audio drain thread, not just from a UI-thread-initiated
    // StartReceivingAsync/StopReceivingAsync call -- volatile for the same reason _pttLocked below
    // already is (this field's own reads/writes now span more than one caller thread with no lock
    // between them).
    private volatile bool _isReceiving;
    private bool _maintenanceWarningActive;

    // See SetPttLockAsync's own doc comment for the full concurrency reasoning. volatile (not a
    // plain bool) since this is written from whatever thread calls SetPttLockAsync and read from
    // PlayWithPttAsync's entry/finally on the caller's own thread -- no dedicated background thread
    // owns this class the way MiniAudioCaptureSession's drain thread does, but the two call paths
    // are still logically concurrent callers with no lock between them.
    private volatile bool _pttLocked;

    // See PlayWithPttAsync's finally block (where this is set) and SetPttLockAsync (where it's
    // consumed) for the full reasoning -- tracks "a lock-covered Transmit/Tune call paused RX and is
    // relying on a later unlock to resume it" across the gap between those two independent calls.
    // Same threading shape/reasoning as _pttLocked immediately above.
    private volatile bool _rxPendingResumeAfterUnlock;

    // Blocker 3 (Tier A Batch 3 chunk 3a): tracks "a PlayWithPttAsync call has PTT keyed RIGHT NOW,
    // mid-transmit" so DisposeAsync can WAIT for that call's own un-key instead of racing it. A plain
    // bool can't be awaited, so this is a TaskCompletionSource: non-null exactly between the point
    // PlayWithPttAsync keys (or inherits an already-keyed) PTT and the point its finally has finished
    // its un-key attempt. Deliberately NOT `volatile` (unlike _pttLocked/_isReceiving above):
    // Interlocked.CompareExchange on a volatile field is CS0420, and this project treats warnings as
    // errors -- every access goes through Volatile.Read/Interlocked instead, same guarantee.
    private TaskCompletionSource? _keyedTransmitCompletion;

    // The one "physically keyed after the call returned" state _pttLocked does NOT cover:
    // TuneAsync's leaveKeyedAfterTune leaves PTT keyed without ever setting _pttLocked (SetPttLockAsync's
    // own doc comment already documents that gap). Tracked so DisposeAsync's shutdown backstop covers
    // it too -- same failure class as blocker 3, one field to close. Same threading shape as _pttLocked.
    private volatile bool _pttLeftKeyedByCall;

    // Tier A Batch 3 chunk 3a round-2 finding: DisposeAsync's backstop only guards
    // _keyedTransmitCompletion state published BEFORE it runs -- a PlayWithPttAsync call that hasn't
    // reached its own publish point yet (e.g. RadioStatusViewModel.TuneAsync's real production call
    // passes CancellationToken.None, so nothing can ever cancel it) could previously key PTT AFTER
    // DisposeAsync had already run its backstop and returned, with no shutdown safety net left to catch
    // it. Read by PlayWithPttAsync's own thread, written by whatever thread calls DisposeAsync -- same
    // threading shape as _pttLocked above.
    private volatile bool _disposed;

    // Hot-path exception rate-limiting (docs/logging-guidelines.md's "Hot-path rule") -- these
    // handlers run on the audio engine's own capture-forwarding path, once per captured chunk;
    // logging every occurrence would turn a logging change into dropped RX samples. First
    // occurrence logs immediately, then only every Nth after that.
    private const int ExceptionLogEveryN = 200;
    private int _decoderExceptionCount;
    private int _waterfallExceptionCount;
    private int _transmitProgressHandlerExceptionCount;

    public SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IMacroTextResolver macroTextResolver,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession,
        ILogger<SstvSessionService> logger)
        : this(audioEngine, deviceEnumerator, settingsStore, decoder, encoder, macroTextResolver,
               waterfall, receivedImage, radioSession, logger,
               cleanupTimeoutForTests: null, playbackStopWaitBudgetForTests: null,
               inFlightKeyedTransmitWaitForTests: null)
    {
    }

    /// <summary>Test-only: lets a test shrink the PTT-safety cleanup budgets (production 5s/5s/3s)
    /// so the blocker-1/blocker-3 regression tests (Tier A Batch 3 chunk 3a) can actually let a
    /// budget EXPIRE without a multi-second-per-test suite -- same shape as
    /// <c>RxDiskLineStagingBuffer</c>'s own <c>disposeDrainTimeoutForTests</c> precedent.</summary>
    internal SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IMacroTextResolver macroTextResolver,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession,
        ILogger<SstvSessionService> logger,
        TimeSpan? cleanupTimeoutForTests,
        TimeSpan? playbackStopWaitBudgetForTests,
        TimeSpan? inFlightKeyedTransmitWaitForTests)
    {
        _cleanupTimeout = cleanupTimeoutForTests ?? CleanupTimeout;
        _playbackStopWaitBudget = playbackStopWaitBudgetForTests ?? PlaybackStopWaitBudget;
        _inFlightKeyedTransmitWait = inFlightKeyedTransmitWaitForTests ?? InFlightKeyedTransmitWait;

        _audioEngine = audioEngine;
        _deviceEnumerator = deviceEnumerator;
        _settingsStore = settingsStore;
        _decoder = decoder;
        _encoder = encoder;
        _macroTextResolver = macroTextResolver;
        Waterfall = waterfall;
        ReceivedImage = receivedImage;
        _radioSession = radioSession;
        _logger = logger;

        // Isolated fan-out (Phase-3 plan decision #3): a throwing/slow handler on one target must
        // never prevent the other from running -- this is what actually fixes the bug the pre-build
        // spike's original IWaterfallSource design would otherwise have reintroduced.
        _decoderHandler = samples =>
        {
            try
            {
                _decoder.PushSamples(samples);
            }
            catch (Exception ex)
            {
                // Deliberately swallowed here, not rethrown into the audio engine's own forwarder
                // (which has no exception isolation of its own -- see IWaterfallSource's doc
                // comment) -- silently continuing beats corrupting the other fan-out target or
                // crashing the drain thread. Rate-limited log per the hot-path rule above.
                var count = Interlocked.Increment(ref _decoderExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    Log.DecoderPushSamplesFailed(_logger, count, ex);
                }
            }
        };
        _waterfallHandler = samples =>
        {
            try
            {
                Waterfall.PushSamples(samples);
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref _waterfallExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    Log.WaterfallPushSamplesFailed(_logger, count, ex);
                }
            }
        };

        // Ultracode audit finding #34: ISstvDecoderMaintenance is an optional side-channel only
        // RestartableSstvDecoder implements (not on ISstvDecoder itself -- see that interface's own
        // doc comment for why). Real decoders wire this up; the various FakeSstvDecoders used by
        // other test projects don't implement it, so this is a no-op there.
        if (_decoder is ISstvDecoderMaintenance maintenance)
        {
            maintenance.RestartOverdue += OnDecoderRestartOverdue;
            maintenance.Restarted += OnDecoderRestarted;
            maintenance.RestartCriticallyOverdue += OnDecoderRestartCriticallyOverdue;
        }
    }

    public IWaterfallSource Waterfall { get; }

    public IReceivedImageBuffer ReceivedImage { get; }

    public IReadOnlyList<SstvModeDefinition> AvailableModes => SstvModeRegistry.All;

    public bool IsReceiving => _isReceiving;

    public bool IsPttLocked => _pttLocked;

    /// <summary>Manual-keying diagnostic aid (e.g. a "PTT lock" button) -- keys PTT immediately and
    /// holds it keyed independent of any <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call,
    /// until unlocked. Operates directly on the PTT line only -- unlike <see cref="PlayWithPttAsync"/>,
    /// this method does NOT itself pause/resume RX capture; if a lock is engaged while a
    /// Transmit/Tune call is skipping its own un-key because of this lock (see
    /// <see cref="PlayWithPttAsync"/>'s own doc comment), THAT call's paused RX is what gets resumed
    /// here on unlock (see <see cref="_rxPendingResumeAfterUnlock"/>) -- not a capture pause owned by
    /// this method itself.
    ///
    /// <b>Idempotent in OUTCOME, not by skipping redundant calls</b> (an audit-fix correction from an
    /// earlier version of this method that short-circuited when the requested state already matched
    /// <see cref="_pttLocked"/> -- a real bug: <see cref="TuneAsync"/>'s <c>leaveKeyedAfterTune</c>
    /// leaves PTT physically keyed without ever setting <see cref="_pttLocked"/>, and a failed
    /// lock-engage leaves it false too -- either way, a subsequent unlock call would have silently
    /// no-op'd on a still-keyed rig with no way to recover via this API at all). Every call now always
    /// issues the underlying <see cref="IRadioSessionService.SetPttAsync"/> command -- confirmed
    /// harmless: every shipped protocol backend's PTT set is an absolute, idempotent command, not a
    /// read-modify-write. Serialized via <see cref="_pttLockGate"/> so two overlapping calls can never
    /// interleave (a real TOCTOU an earlier check-then-act version had).
    ///
    /// <b>Failure behavior is deliberately asymmetric-by-outcome, not by direction</b>: the internal
    /// "locked" flag is only updated AFTER <see cref="IRadioSessionService.SetPttAsync"/> actually
    /// succeeds, in both directions -- so a failed lock-engage leaves <see cref="IsPttLocked"/> false
    /// (correctly reflecting that PTT was never confirmed keyed), and a failed unlock leaves it TRUE
    /// (correctly reflecting that PTT was never confirmed un-keyed, so a caller can safely retry
    /// unlock rather than the lock silently "forgetting" a still-keyed rig). This call's own
    /// <see cref="IRadioSessionService.SetPttAsync"/> failure is NOT swallowed here (unlike
    /// <see cref="PlayWithPttAsync"/>'s best-effort cleanup steps) -- this is a direct, explicit
    /// caller action, not an automatic cleanup path, so the caller needs to know if it failed. (RX
    /// resume-after-unlock IS best-effort/swallowed, deliberately -- a failure to resume monitoring
    /// must not be reported as "unlock failed" when PTT itself was genuinely un-keyed successfully.)
    ///
    /// <b>Known, accepted race (unchanged by this fix, documented not silently left implicit)</b>: an
    /// unlock call racing a Transmit/Tune call's own entry (which already decided not to key because
    /// the lock looked engaged) can un-key PTT out from under an in-flight transmission, sending the
    /// rest of that frame into a dead carrier. No production caller exists yet for this method or
    /// <c>leaveKeyedAfterTune: true</c> (unwired UI), so this is latent, not exercised -- fixing it
    /// fully would need a single shared gate across <see cref="PlayWithPttAsync"/> AND this method,
    /// which would also make an emergency unlock wait behind an in-flight transmission's own gate
    /// hold -- a worse safety property than the current race for what unlock is meant to be (an
    /// escape hatch). Revisit if/when a real caller actually needs this closed.</summary>
    private readonly SemaphoreSlim _pttLockGate = new(1, 1);

    public async Task SetPttLockAsync(bool locked, CancellationToken ct = default)
    {
        await _pttLockGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Round-2 fix: only the ENGAGE direction is rejected post-disposal -- an unlock must stay a
            // valid escape hatch for a rig this class already left keyed (matches _pttLocked's own
            // failed-unlock-stays-true reasoning below; disposal must never remove the one remaining
            // way to un-key a rig that is still physically keyed).
            if (locked)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            await _radioSession.SetPttAsync(locked, ct).ConfigureAwait(false);
            _pttLocked = locked;
            if (!locked)
            {
                // A CONFIRMED un-key invalidates every "still keyed" belief this class holds, not just
                // _pttLocked -- see _pttLeftKeyedByCall's own doc comment. Placed after SetPttAsync
                // succeeded, matching _pttLocked's own deliberate only-on-success rule above.
                _pttLeftKeyedByCall = false;
            }

            Log.PttLockChanged(_logger, locked);

            if (!locked && _rxPendingResumeAfterUnlock)
            {
                _rxPendingResumeAfterUnlock = false;
                try
                {
                    await StartReceivingAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.CleanupStepFailed(_logger, "Resume RX (after unlock)", ex);
                }
                finally
                {
                    // User-reported gap (2026-08-18): the deferred half of PlayWithPttAsync's own
                    // pause -- see CapturePausedForTransmitChanged's own doc comment for why this
                    // fires here too, not just at PlayWithPttAsync's own immediate-resume point.
                    RaiseCapturePausedForTransmitChanged(false);
                }
            }
        }
        finally
        {
            _pttLockGate.Release();
        }
    }

    public event Action<SstvModeDefinition>? ModeDetected
    {
        add => _decoder.ModeDetected += value;
        remove => _decoder.ModeDetected -= value;
    }

    public event Action<SstvModeDefinition>? DecodeRestarted
    {
        add => _decoder.DecodeRestarted += value;
        remove => _decoder.DecodeRestarted -= value;
    }

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded
    {
        add => _decoder.StationIdDecoded += value;
        remove => _decoder.StationIdDecoded -= value;
    }

    /// <summary>See <see cref="ISstvSessionService.TransmitProgressChanged"/> for the full threading
    /// contract. Raised from <see cref="PumpToPlaybackAsync"/> via <see cref="ReportTransmitProgress"/>.</summary>
    public event Action<TransmitProgressInfo>? TransmitProgressChanged;

    public event Action<bool>? CapturePausedForTransmitChanged;

    /// <summary>Auditor-caught (round 1): the raw <c>CapturePausedForTransmitChanged?.Invoke(...)</c>
    /// calls this wraps were previously inline at each call site, unguarded -- unlike this class's own
    /// established convention for the sibling <see cref="TransmitProgressChanged"/> event
    /// (<see cref="ReportTransmitProgress"/>'s own try/catch), a throwing subscriber could propagate
    /// out of <see cref="PlayWithPttAsync"/> (leaving RX permanently stopped, since the `true` raise
    /// sits outside that method's own guarded region by design -- see this method's own call site) or
    /// out of a `finally` block, masking the real exception/original cancellation reason a caller like
    /// <c>TxControlsPaneViewModel</c> distinguishes on. Not rate-limited like
    /// <see cref="ReportTransmitProgress"/>'s own catch (that one is a genuine hot path, ~every 4096
    /// samples; this fires at most twice per transmission -- or once for a stranded deferred-lock
    /// resume, see this method's other call sites -- so a plain per-occurrence Error (matching the
    /// sibling <c>MaintenanceHandlerFailed</c>/<c>TransmitProgressHandlerFailed</c> level) cannot
    /// flood the log).</summary>
    private void RaiseCapturePausedForTransmitChanged(bool paused)
    {
        try
        {
            CapturePausedForTransmitChanged?.Invoke(paused);
        }
        catch (Exception ex)
        {
            Log.CapturePausedHandlerFailed(_logger, paused, ex);
        }
    }

    public async Task<string?> GetOperatorCallsignAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();

        // Auditor code-review finding on Phase 5 (real bug, fixed): a decoded FSK station-ID
        // callsign is ALWAYS normalized by construction (every transmitter, including this port's
        // own, runs StationIdCallsignNormalizer.Normalize before sending) -- comparing it against the
        // operator's RAW stored callsign (e.g. "w1aw") would silently fail the self-filter for any
        // operator whose stored callsign isn't already uppercase/trimmed. Normalizing here, not just
        // storing it normalized in OperatorSettings, keeps the raw-as-typed value available for
        // every OTHER consumer (macros/display/QRZ) that doesn't want it force-uppercased.
        return string.IsNullOrEmpty(operatorSettings.Callsign)
            ? operatorSettings.Callsign
            : StationIdCallsignNormalizer.Normalize(operatorSettings.Callsign);
    }

    /// <summary>See <see cref="ISstvSessionService.SlantPpm"/> / <see cref="ISstvDecoder.SlantPpm"/>.</summary>
    public double? SlantPpm => _decoder.SlantPpm;

    /// <summary>See <see cref="ISstvSessionService.SyncOffsetSamples"/> / <see cref="ISstvDecoder.SyncOffsetSamples"/>.</summary>
    public int? SyncOffsetSamples => _decoder.SyncOffsetSamples;

    /// <summary>See <see cref="ISstvSessionService.SignalPeakLevel"/> / <see cref="ISstvDecoder.SignalPeakLevel"/>.</summary>
    public double SignalPeakLevel => _decoder.SignalPeakLevel;

    /// <summary>See <see cref="ISstvSessionService.IsLevelOverdriven"/> / <see cref="ISstvDecoder.IsLevelOverdriven"/>.</summary>
    public bool IsLevelOverdriven => _decoder.IsLevelOverdriven;

    /// <summary>See <see cref="ISstvSessionService.AutoSlantEnabled"/> / <see cref="ISstvDecoder.AutoSlantEnabled"/>.</summary>
    public bool AutoSlantEnabled => _decoder.AutoSlantEnabled;

    /// <summary>See <see cref="ISstvSessionService.SyncFrequencyCorrectionHz"/> / <see cref="ISstvDecoder.SyncFrequencyCorrectionHz"/>.</summary>
    public double? SyncFrequencyCorrectionHz => _decoder.SyncFrequencyCorrectionHz;

    /// <summary>See <see cref="ISstvSessionService.BufferedSampleCount"/> / <see cref="ISstvDecoder.BufferedSampleCount"/>.</summary>
    public int BufferedSampleCount => _decoder.BufferedSampleCount;

    /// <summary>See <see cref="ISstvSessionService.CaptureOverrunCount"/>. Absorbs the narrow,
    /// documented THROW race <c>MiniAudioEngine.CaptureOverrunCount</c>'s own doc comment describes
    /// (a concurrent <see cref="StopReceivingAsync"/> disposing the capture session between that
    /// property's field read and its underlying native call) -- auditor-caught: this property is
    /// polled every 250ms by <c>RxImagePaneViewModel</c>'s telemetry timer, an ordinary "user clicks
    /// Stop RX mid-poll" interleaving reachable on every real session, and a <c>DispatcherTimer</c>
    /// tick exception has nowhere safe to land (this port's own global unhandled-exception handler
    /// only logs, it doesn't recover). <c>0</c> is not a fallback value here -- it IS the documented
    /// contract value for "capture isn't running", so this doesn't hide a real failure, it just
    /// reaches the same answer a clean read would have found a moment later. <b>Tier A Batch 1
    /// re-audit round 5/6 correction: this catch does NOT absorb everything the same race can do</b>
    /// -- see <see cref="ISstvSessionService.CaptureOverrunCount"/>'s own doc comment (which this
    /// summary's first line already points to) for the BLOCK half of the race this `try`/`catch`
    /// has no way to catch: the underlying read can stall the calling thread (here, the UI thread)
    /// for as long as a concurrent capture-session `Dispose()` holds its own write lock, before
    /// this method's call even reaches the point where it could throw.</summary>
    public int CaptureOverrunCount
    {
        get
        {
            try
            {
                return _audioEngine.CaptureOverrunCount;
            }
            catch (ObjectDisposedException)
            {
                Log.CaptureOverrunCountRaceObserved(_logger);
                return 0;
            }
        }
    }

    public event Action? MaintenanceWarningRaised;

    public event Action? MaintenanceWarningCleared;

    public event Action? MaintenanceCriticalStopRaised;

    /// <summary>See <see cref="ISstvSessionService.RequestReSync"/>.</summary>
    public void RequestReSync()
    {
        Log.ReSyncRequested(_logger);
        _decoder.RequestReSync();
    }

    /// <summary>See <see cref="ISstvSessionService.RequestCorrectSlant"/>.</summary>
    public void RequestCorrectSlant()
    {
        Log.CorrectSlantRequested(_logger);
        _decoder.RequestCorrectSlant();
    }

    /// <summary>See <see cref="ISstvSessionService.ForceMode"/>.</summary>
    public void ForceMode(SstvModeDefinition mode)
    {
        Log.ModeForced(_logger, mode.Id);
        _decoder.ForceMode(mode);
    }

    // These three run synchronously on the audio drain thread, inside the same call stack as
    // ISstvDecoder.PushSamples -- _decoderHandler's own try/catch (constructor, above) wraps the
    // PushSamples call itself, but an exception thrown by one of THESE handlers would otherwise be
    // caught there too and mis-logged as "decoder PushSamples threw" instead of attributing it to the
    // actual maintenance handler that failed. Each gets its own try/catch for that reason.
    private void OnDecoderRestartOverdue()
    {
        try
        {
            _maintenanceWarningActive = true;
            Log.MaintenanceWarningRaised(_logger);
            MaintenanceWarningRaised?.Invoke();
        }
        catch (Exception ex)
        {
            Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartOverdue), ex);
        }
    }

    private void OnDecoderRestarted()
    {
        try
        {
            if (_maintenanceWarningActive)
            {
                _maintenanceWarningActive = false;
                Log.MaintenanceWarningCleared(_logger);
                MaintenanceWarningCleared?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestarted), ex);
        }
    }

    private void OnDecoderRestartCriticallyOverdue()
    {
        try
        {
            // The decoder has ALREADY force-restarted unconditionally by the time this fires (see
            // ISstvDecoderMaintenance's own doc comment) -- this only needs to tear down capture and
            // notify the user, not request another swap. Confirmed safe to call synchronously here
            // (round-3 plan review traced the full MiniAudioEngine/MiniAudioCaptureSession shutdown
            // chain): RestartableSstvDecoder raises this event strictly after releasing its own
            // swap lock, so ResetAgc() re-entering it from inside StopReceivingAsync below never
            // contends for anything already held.
            StopReceivingAsync().GetAwaiter().GetResult();
            _maintenanceWarningActive = false;
            Log.MaintenanceCriticalStop(_logger);
            MaintenanceCriticalStopRaised?.Invoke();
        }
        catch (Exception ex)
        {
            Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartCriticallyOverdue), ex);
        }
    }

    public async Task StartReceivingAsync(CancellationToken ct = default)
    {
        if (_isReceiving)
        {
            return;
        }

        var device = await ResolveDeviceAsync(forCapture: true, ct).ConfigureAwait(false);
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);

        // CW-ID/FSK station-ID subsystem Phase 4 code-review finding: this was documented (Phase 3's
        // AnalogFmSstvDecoder.StationIdDecodeEnabled/NarrowFskHeaderDecoder.StationIdDecodeEnabled
        // doc comments) as "Phase 4 wires this to the live user setting" but nothing ever did --
        // m_fskdecode's port-equivalent field (StationIdSettings.FskIdRxEnabled) was a dead setting.
        // Re-applied on every StartReceivingAsync call (not just once at DI-construction time, unlike
        // the other decoder toggles below the audio-capture start) since ISstvDecoder.StationIdDecodeEnabled
        // is deliberately live-settable -- see that property's own doc comment for why.
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var stationIdSettings = appSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings)
            ?? new StationIdSettings();
        _decoder.StationIdDecodeEnabled = stationIdSettings.FskIdRxEnabled;

        await _audioEngine.StartCaptureAsync(
            device, _decoder.SampleRate, settings.CaptureThreadPriority,
            settings.PeriodSizeInFrames, settings.Periods, settings.CaptureChannelSource, ct).ConfigureAwait(false);

        _audioEngine.SamplesCaptured += _decoderHandler;
        _audioEngine.SamplesCaptured += _waterfallHandler;
        _isReceiving = true;

        // ultracode audit finding #6: legacy resets its AGC (CLVL::Init) at every TX<->RX transition
        // (Sound.cpp:398,443) -- this is that transition point on the RX-resuming side.
        _decoder.ResetAgc();
        Log.RxStarted(_logger, device.Id, _decoder.SampleRate);
    }

    public async Task StopReceivingAsync()
    {
        if (!_isReceiving)
        {
            return;
        }

        _audioEngine.SamplesCaptured -= _decoderHandler;
        _audioEngine.SamplesCaptured -= _waterfallHandler;
        await _audioEngine.StopCaptureAsync().ConfigureAwait(false);
        _isReceiving = false;

        // ultracode audit finding #6: the RX-halting (entering-TX) side of the same transition.
        _decoder.ResetAgc();
        Log.RxStopped(_logger);
    }

    public async Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        Log.TxStarting(_logger, mode.Id, image.Width, image.Height);
        var stationId = await GetStationIdTransmitOptionsAsync(ct).ConfigureAwait(false);

        // Plan-review finding: MUST reuse this SAME resolved stationId for the estimate below, not
        // re-resolve it -- MacroTextResolver's CW-ID text can be time-dependent (DateTime.UtcNow), so
        // two independent resolutions aren't guaranteed to produce the same footer duration, which
        // would make the estimate silently disagree with what EncodeAsync actually emits.
        //
        // Code-review finding: EstimateSampleCount is a real traversal of every scanline segment
        // (its own doc comment says so), not O(1) metadata math -- Task.Run keeps it off whichever
        // thread called TransmitAsync (the caller's own await above may not have yielded at all, e.g.
        // JsonSettingsStore.LoadAsync returns synchronously when no settings file exists yet), so a
        // large image's estimate can't delay PTT keying/RX pause by running inline on the UI thread.
        var totalSamplesEstimate = await Task.Run(() => _encoder.EstimateSampleCount(mode, image, stationId), ct).ConfigureAwait(false);
        await PlayWithPttAsync(_encoder.EncodeAsync(mode, image, stationId, ct), _encoder.SampleRate, ct, totalSamplesEstimate: totalSamplesEstimate).ConfigureAwait(false);
    }

    /// <summary>Resolves the CW-ID/FSK station-ID settings + operator identity into one fully-formed
    /// <see cref="StationIdTransmitOptions"/> -- see that type's own doc comment for why this
    /// resolution lives here (Application layer) rather than inside the pure-DSP encoder. Settings-
    /// boundary validation for WPM/tone-frequency lives here too (Phase 1 code-review finding): a
    /// corrupted/hand-edited settings.json with WPM &lt;= 0 would otherwise make
    /// <c>CwMorseGenerator.MillisecondsPerDotFromWpm</c> return Infinity/negative -- this falls back
    /// to the documented default instead of letting a bad value reach the generator or abort an
    /// in-flight transmission. Also the read-only preview <see cref="ISstvSessionService.GetStationIdTransmitOptionsAsync"/>
    /// exposes (see that member's own doc comment) -- <see cref="TransmitAsync"/> and that preview
    /// path share this exact same resolution, so they can never disagree with each other.</summary>
    public async Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var stationIdSettings = appSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings)
            ?? new StationIdSettings();
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();

        // Main.cpp:6969's !sys.m_CWIDText.IsEmpty() -- checked on the RAW (pre-macro) text, matching
        // legacy exactly (a macro token that resolves to empty would still fire in legacy, since the
        // gate never re-checks after MacroText expansion).
        var cwEnabled = stationIdSettings.CwIdMode == CwIdMode.Cw && !string.IsNullOrEmpty(stationIdSettings.CwText);
        var wpm = stationIdSettings.CwWpm is > 0 ? stationIdSettings.CwWpm.Value : StationIdSettings.DefaultCwWpm;
        var toneFrequencyHz = stationIdSettings.CwToneFrequencyHz is > 0
            ? stationIdSettings.CwToneFrequencyHz.Value
            : StationIdSettings.DefaultCwToneFrequencyHz;
        var nrRstEnabled = stationIdSettings.NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled;

        // Auditor code-review finding on Phase 4 (real, low-severity, and refined in round 2): legacy
        // caps the MACRO-RESOLVED CW-ID text at 77 chars for plain (non-macro) literal text --
        // `MacroText`'s own break condition (`Main.cpp:10829`, `if (n >= (size-1)) break;` with
        // `size = sizeof(bf)-2 = 78`) stops once `n` reaches 77, not 78; `sizeof(bf)-2` is the buffer
        // SIZE passed in, not the actual max character count written. A macro token can overshoot
        // this (`n += l` for a multi-char expansion checked only AFTER appending) -- legacy itself can
        // write past `bf[80]` in that case, a real legacy buffer bug this port has no reason to
        // reproduce; 77 is deliberately chosen as the exact literal-text limit, not an attempt to
        // replicate the macro-overshoot case. An uncapped resolved text doesn't corrupt anything here
        // either way (CwMorseGenerator has no buffer to overrun), just runs a longer Morse tail than
        // legacy would have sent for the same configured literal text -- capped to match legacy's
        // actual on-air behavior instead of silently diverging.
        const int maxCwResolvedTextLength = 77;
        var cwResolvedText = cwEnabled ? _macroTextResolver.Resolve(stationIdSettings.CwText!, operatorSettings) : string.Empty;
        if (cwResolvedText.Length > maxCwResolvedTextLength)
        {
            cwResolvedText = cwResolvedText[..maxCwResolvedTextLength];
        }

        return new StationIdTransmitOptions
        {
            CwEnabled = cwEnabled,
            CwResolvedText = cwResolvedText,
            CwToneFrequencyHz = toneFrequencyHz,
            CwWpm = wpm,
            FskIdEnabled = stationIdSettings.FskIdTxEnabled,
            Callsign = operatorSettings.Callsign ?? string.Empty,
            NrRstText = nrRstEnabled ? stationIdSettings.NrRstText : null,
        };
    }

    public Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default)
    {
        const int sampleRate = 48_000;
        Log.TuneStarting(_logger, frequencyHz, duration);
        return PlayWithPttAsync(GenerateTone(frequencyHz, duration, sampleRate, ct), sampleRate, ct, leaveKeyedAfterCall: leaveKeyedAfterTune);
    }

    public async Task<int> GetTxVolumePercentAsync(CancellationToken ct = default)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        return settings.TxVolumePercent ?? 100;
    }

    public async Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        var updated = appSettings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with { TxVolumePercent = percent },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        Log.TxVolumeSet(_logger, percent);
    }

    /// <summary>Shared PTT-guarantee shape for both <see cref="TransmitAsync"/> and <see cref="TuneAsync"/>:
    /// pauses capture (resumed afterward only if RX was already running), keys PTT, plays
    /// <paramref name="samples"/>, then un-keys PTT in a <c>finally</c> no matter how playback ends --
    /// unless <paramref name="leaveKeyedAfterCall"/> is set (see <see cref="TuneAsync"/>'s own doc
    /// comment for its one caller) or <see cref="SetPttLockAsync"/>'s lock is currently engaged, in
    /// which case the un-key/resume-RX steps are skipped -- <b>but only on a NORMAL (successful)
    /// completion</b>. A cancellation or fault (manual Stop TX, SWR auto-cutoff -- see
    /// <c>TxControlsPaneViewModel</c>) ALWAYS un-keys PTT and force-clears the lock, even if it was
    /// engaged: a safety cutoff/manual stop must never be overridable by "stay keyed" state (a real
    /// defect an audit pass caught and this fix closes -- the lock existing at all must never be able
    /// to defeat the SWR cutoff's whole reason for existing).
    ///
    /// Device resolution and the entry PTT-key now live INSIDE the guarded region (moved in during
    /// the same audit-fix pass) -- previously a device-resolution failure (e.g. no playback device
    /// configured) after RX had already been paused above left RX stopped forever, since the old
    /// shape's <c>try</c>/<c>finally</c> didn't start until after those calls.</summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    // Blocker 1 (Tier A Batch 3 chunk 3a). IAudioEngine.StopPlaybackAsync takes no CancellationToken
    // -- it cannot be cancelled, only stopped waiting on -- and the real MiniAudioEngine can take
    // ~10.2s worst case on a wedged output device (MiniAudioEngine.DrainTimeout 5s +
    // MiniAudioPlaybackSession.CloseTimeout 5s + DrainTailMargin 200ms). Sharing ONE 5s deadline
    // across StopPlayback-then-un-key therefore meant the un-key's token was already cancelled by the
    // time it ran, and both real protocol backends honor the token at their lock-acquire gate
    // (HamlibRadioProtocol.cs, RigctldClientProtocol.cs) -- so PTT-off was never even attempted on the
    // exact hardware failure where it matters most. This is how long we WAIT before moving on to the
    // un-key; generous enough that a healthy drain (ring ~0.37s + 200ms tail margin) always finishes
    // inside it, so normal transmissions are bit-for-bit unaffected.
    private static readonly TimeSpan PlaybackStopWaitBudget = TimeSpan.FromSeconds(5);

    // Blocker 3: how long DisposeAsync waits for an in-flight keyed transmit's OWN cleanup before
    // force-un-keying itself. Sized against ScanlineStudio.Host/Program.cs's 10s total host-teardown
    // bound: this (3s) + the backstop un-key's own CleanupTimeout (5s) = 8s worst case, leaving
    // headroom for the rest of teardown rather than guaranteeing a TeardownTimedOut.
    private static readonly TimeSpan InFlightKeyedTransmitWait = TimeSpan.FromSeconds(3);

    private async Task PlayWithPttAsync(IAsyncEnumerable<float> samples, int sampleRate, CancellationToken ct, bool leaveKeyedAfterCall = false, long? totalSamplesEstimate = null)
    {
        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingAsync().ConfigureAwait(false);
            // User-reported gap (2026-08-18): see CapturePausedForTransmitChanged's own doc comment.
            // Raised AFTER the await completes -- capture is genuinely stopped by the time a
            // subscriber sees this, not merely "about to stop." Deliberately OUTSIDE the guarded
            // try/finally just below (this line runs before it starts) -- RaiseCapturePausedForTransmitChanged's
            // own try/catch is what keeps a throwing subscriber here from propagating out of this
            // method with capture already stopped and no cleanup ever run (auditor round-1 finding).
            RaiseCapturePausedForTransmitChanged(true);
        }

        var abnormalTermination = false;

        // Blocker 2 (Tier A Batch 3 chunk 3a): captured ONCE, at key time -- NEVER re-read at
        // catch/cleanup time. RadioController.DisconnectAsync/DisposeAsync both reset _rigId to
        // "none" WITHOUT ever un-keying PTT themselves, so a rig this call genuinely keyed can read
        // "none" by the moment the cleanup un-key fails. The old catch-time re-read then classified a
        // physically keyed transmitter as the benign "no radio, nothing to unkey" case and swallowed
        // it.
        var pttKeyedOnRealRig = false;

        // Blocker 3: this call's own handle into _keyedTransmitCompletion. Local as well as field so
        // the finally can clear the field ONLY if it still points at this call's own instance.
        TaskCompletionSource? keyedCompletion = null;

        try
        {
            var device = await ResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
            var gain = (await GetTxVolumePercentAsync(ct).ConfigureAwait(false)) / 100f;
            var audioSettings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);

            // Guarded on RigId ("none" = the null-object "no radio" backend, spec/18-path-to-1.0.md
            // Critical item 1), not Capabilities -- see IRadioController.RigId's own doc comment for
            // why a live-capability check would be unsafe here (real backends connect lazily, so
            // Capabilities reads None during a real window even with a genuine PTT-capable rig
            // configured). Read into a local exactly once: the old code's "RigId is stable so there's
            // no was-available-at-entry-gone-by-cleanup scenario" claim was FALSE (see
            // pttKeyedOnRealRig above), and this is the single read that claim is now replaced by.
            var pttLockedAtEntry = _pttLocked;
            var rigIsRealAtKeyTime = _radioSession.RigId != "none";

            if (rigIsRealAtKeyTime)
            {
                // Published BEFORE the key command goes out and cleared only once this method's own
                // finally has finished its un-key attempt, so DisposeAsync can never tear
                // IRadioSessionService down out from under an in-flight un-key. Published in the
                // pttLockedAtEntry case too: this call didn't key the rig, but the rig IS keyed for
                // the whole duration of this call either way.
                keyedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _keyedTransmitCompletion, keyedCompletion);

                // Set BEFORE the await deliberately: a SetPttAsync that throws mid-command can still
                // have physically keyed the rig, so "keyed" is the only safe assumption for the
                // cleanup un-key's own failure reporting. Erring true costs at most one spurious
                // Warning; erring false is the blocker-2 silent swallow.
                pttKeyedOnRealRig = true;

                // Round-2 fix, publish-then-recheck (same shape as Risk B's own at the RX-resume branch
                // further down this method): either DisposeAsync's read of _keyedTransmitCompletion
                // (its own very first line now sets _disposed before that read) sees THIS call's
                // just-published completion and waits for it, or this call sees _disposed here and
                // never issues the actual key command below at all -- the two sides can no longer miss
                // each other the way a TuneAsync call with no CancellationToken
                // (RadioStatusViewModel.TuneAsync passes CancellationToken.None) previously could race
                // a closing DisposeAsync.
                if (_disposed)
                {
                    // Provably never keyed BY THIS CALL -- the actual SetPttAsync(true) command is
                    // still below this block. Only an already-engaged lock could have the rig keyed
                    // independent of this call, and pttLockedAtEntry covers exactly that -- keeps the
                    // cleanup un-key's Critical-vs-Debug classification honest in the finally below.
                    pttKeyedOnRealRig = pttLockedAtEntry;
                    throw new ObjectDisposedException(nameof(SstvSessionService));
                }
            }

            if (!pttLockedAtEntry)
            {
                if (rigIsRealAtKeyTime)
                {
                    await _radioSession.SetPttAsync(true, ct).ConfigureAwait(false);
                    Log.PttKeyed(_logger);
                }
                else
                {
                    Log.PttSkippedNoRadio(_logger);
                }
            }

            await _audioEngine.StartPlaybackAsync(
                device, sampleRate, audioSettings.PeriodSizeInFrames, audioSettings.Periods,
                audioSettings.StereoTxEnabled, ct).ConfigureAwait(false);
            await PumpToPlaybackAsync(samples, gain, sampleRate, totalSamplesEstimate, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal, expected way for this to end (manual Stop TX, SWR
            // auto-cutoff -- see TxControlsPaneViewModel for which one) -- this layer has no way to
            // tell which caused it, so it's logged generically at Information, not as a failure.
            abnormalTermination = true;
            Log.PlaybackCancelled(_logger);
            throw;
        }
        catch (Exception ex)
        {
            abnormalTermination = true;
            Log.PlaybackFailed(_logger, ex);
            throw;
        }
        finally
        {
            try
            {
                // Risk B (Tier A Batch 3 chunk 3a): _pttLocked read ONCE for this whole decision.
                // The old code read it at the skip computation and AGAIN at the deferred-resume
                // branch below; a SetPttLockAsync(false) landing between the two made those two reads
                // disagree, taking the "leaveKeyedAfterCall residual" branch on a call that had
                // actually paused RX -- RX stranded stopped forever with nothing left to resume it.
                var pttLockedAtCleanup = _pttLocked;

                // Only a NORMAL completion honors "stay keyed" (leaveKeyedAfterCall/lock) -- see this
                // method's own doc comment for why an abnormal termination always overrides both.
                var skipUnkeyAndRxResume = !abnormalTermination && (leaveKeyedAfterCall || pttLockedAtCleanup);
                var unkeyAlreadyAttempted = false;

                // ---- Blocker 1, urgent half ----
                // An abnormal termination IS the safety path (manual Stop TX / SWR auto-cutoff): the
                // operator wants the transmitter off NOW and there is no audio tail worth preserving
                // (the image is aborted either way). Un-key BEFORE StopPlayback so a wedged output
                // device can't delay it at all. skipUnkeyAndRxResume is false by construction whenever
                // abnormalTermination is true, so this can never fire on a "stay keyed" path.
                if (abnormalTermination)
                {
                    await UnkeyForCleanupAsync(pttKeyedOnRealRig).ConfigureAwait(false);
                    unkeyAlreadyAttempted = true;
                }

                // ---- Blocker 1, budget half ----
                // On a NORMAL completion the un-key still runs AFTER the drain -- dropping PTT while
                // the miniaudio ring / PulseAudio server queue still hold audio would truncate the
                // tail of every successful transmission, exactly what MiniAudioEngine.DrainTailMargin
                // exists to prevent. What changed is that the drain can no longer STARVE the un-key:
                // it gets a bounded wait of its own, and the un-key gets its own fresh
                // CancellationTokenSource inside UnkeyForCleanupAsync.
                await StopPlaybackWithWatchdogAsync().ConfigureAwait(false);

                if (!skipUnkeyAndRxResume && !unkeyAlreadyAttempted)
                {
                    await UnkeyForCleanupAsync(pttKeyedOnRealRig).ConfigureAwait(false);
                }

                if (skipUnkeyAndRxResume && leaveKeyedAfterCall)
                {
                    // PTT is deliberately left physically keyed after this call returns, with no
                    // _pttLocked to record it (SetPttLockAsync's own doc comment already calls this
                    // gap out) -- DisposeAsync's shutdown backstop needs to know about it.
                    _pttLeftKeyedByCall = pttKeyedOnRealRig;
                }

                // Created only HERE, after every potentially-slow step above, so the RX-resume steps
                // get a full independent budget rather than whatever StopPlayback/un-key left over --
                // the same starvation bug blocker 1 is about, one step further down the chain. At most
                // one StartReceivingAsync call runs per invocation (the three branches below are
                // mutually exclusive on wasReceiving/skipUnkeyAndRxResume), so one source is enough.
                using var rxResumeCts = new CancellationTokenSource(_cleanupTimeout);

                if (!skipUnkeyAndRxResume)
                {
                    // Auditor-caught (round 2): a PRIOR locked call may have already stopped capture
                    // and set _rxPendingResumeAfterUnlock before THIS call force-unkeyed on an
                    // abnormal termination -- if so, THIS call's own `wasReceiving` is false, so the
                    // `if (wasReceiving)` block below would never see it, leaving RX stopped forever
                    // with IsPttLocked already reporting false. Guarded on `!wasReceiving` so this
                    // never double-fires alongside that block's own resume for THIS call.
                    if (!wasReceiving && _rxPendingResumeAfterUnlock)
                    {
                        _rxPendingResumeAfterUnlock = false;
                        await TryCleanupAsync("Resume RX (stranded lock pending-resume)", () => StartReceivingAsync(rxResumeCts.Token)).ConfigureAwait(false);
                        RaiseCapturePausedForTransmitChanged(false);
                    }
                }

                if (wasReceiving)
                {
                    if (!skipUnkeyAndRxResume)
                    {
                        await TryCleanupAsync("Resume RX", () => StartReceivingAsync(rxResumeCts.Token)).ConfigureAwait(false);
                        // User-reported gap (2026-08-18): fires once the resume attempt has finished,
                        // success or failure -- this event is "no longer paused FOR THIS transmission,"
                        // not a restatement of IsReceiving itself (see the event's own doc comment).
                        RaiseCapturePausedForTransmitChanged(false);
                    }
                    else if (!abnormalTermination && pttLockedAtCleanup)
                    {
                        // Specifically the lock case, not leaveKeyedAfterCall -- a lock can stay
                        // engaged indefinitely with no automatic next step. RX must resume once
                        // SetPttLockAsync(false) eventually un-keys, not be silently forgotten.
                        _rxPendingResumeAfterUnlock = true;

                        // Risk B, publish-then-recheck. A SetPttLockAsync(false) that completed
                        // between the snapshot above and this write already ran its own deferred-resume
                        // check against a still-false flag, so nothing would ever consume what we just
                        // set -- RX stranded stopped forever. Re-reading _pttLocked AFTER publishing
                        // closes that window: whichever side observes the other's write does the
                        // resume. Both sides test the flag before consuming it, so the only residual
                        // interleaving is a duplicate resume, which is harmless -- StartReceivingAsync
                        // early-returns when already receiving, and a second
                        // CapturePausedForTransmitChanged(false) is idempotent for every subscriber.
                        if (!_pttLocked && _rxPendingResumeAfterUnlock)
                        {
                            _rxPendingResumeAfterUnlock = false;
                            await TryCleanupAsync("Resume RX (unlock raced cleanup)", () => StartReceivingAsync(rxResumeCts.Token)).ConfigureAwait(false);
                            RaiseCapturePausedForTransmitChanged(false);
                        }
                    }
                    else
                    {
                        // Auditor-caught (round 1): the residual case -- leaveKeyedAfterCall=true and
                        // NOT locked (TuneAsync's own unwired "stay keyed" option). RX intentionally
                        // stays stopped (nobody's job to resume it), but THIS transmission's own pause
                        // window is over either way, so the event must not stay stuck `true` forever
                        // with no `false` ever coming (a real latent bug: harmless today since no
                        // production caller sets leaveKeyedAfterCall=true, but would permanently
                        // freeze the Receiving indicator dimmed the moment one did).
                        RaiseCapturePausedForTransmitChanged(false);
                    }
                }
            }
            finally
            {
                // Blocker 3: signalled no matter how the cleanup above ended (including a throw from
                // a subscriber or a cleanup step) -- a DisposeAsync waiting on this must never be left
                // hanging until its own timeout by an unrelated failure. CompareExchange, not a plain
                // null write: only clear the field if it still points at THIS call's instance, so a
                // (pathological) overlapping PlayWithPttAsync can't have its own registration erased.
                if (keyedCompletion is not null)
                {
                    Interlocked.CompareExchange(ref _keyedTransmitCompletion, null, keyedCompletion);
                    keyedCompletion.TrySetResult();
                }
            }
        }
    }

    /// <summary>The un-key half of <see cref="PlayWithPttAsync"/>'s cleanup, extracted so blocker 1's
    /// two call sites (the abnormal-termination early un-key and the normal-completion post-drain one)
    /// share one implementation -- and so each gets its OWN fresh <see cref="CancellationTokenSource"/>.
    /// That is blocker 1's actual fix: the un-key's deadline must never be the leftover of an earlier
    /// cleanup step's spend.</summary>
    private async Task<bool> UnkeyForCleanupAsync(bool pttKeyedOnRealRig)
    {
        using var unkeyCts = new CancellationTokenSource(_cleanupTimeout);
        if (await TryUnkeyPttAsync(pttKeyedOnRealRig, unkeyCts.Token).ConfigureAwait(false))
        {
            // Force-release: whether or not a lock was engaged, PTT is now confirmed physically off --
            // IsPttLocked must never report true once that's true.
            _pttLocked = false;
            _pttLeftKeyedByCall = false;
            Log.PttReleased(_logger);
            return true;
        }

        if (pttKeyedOnRealRig)
        {
            // Risk A (partial fix, Tier A Batch 3 chunk 3a): a Warning is too quiet for "a real
            // transmitter this call keyed may still be on the air." Escalated to Critical, and only
            // for the captured-at-key-time real-rig case, so it can never fire for the benign
            // fresh-install RigId=="none" path that the Warning-suppression logic exists for.
            Log.PttStillKeyedAfterFailedUnkey(_logger);
        }

        return false;
    }

    /// <summary>Blocker 1's other half. <see cref="IAudioEngine.StopPlaybackAsync"/> takes no
    /// <see cref="CancellationToken"/> -- it cannot be cancelled, only stopped waiting on. The real
    /// <c>MiniAudioEngine</c> can take ~10.2s worst case on a wedged output device, which is how the
    /// shared-deadline version of this cleanup consumed the entire PTT-off budget before the un-key
    /// was ever attempted. This waits at most <see cref="_playbackStopWaitBudget"/> and then returns,
    /// letting the stop finish in the background -- it is still the same single call (so the engine's
    /// own session teardown is never skipped) and its eventual outcome is still observed and logged
    /// (so it can never surface as an unobserved task exception).
    ///
    /// <b>Known consequence, accepted:</b> on the timeout path this method can return while a
    /// playback stop is still in flight. An immediately following transmit against the same wedged
    /// device fails loudly (a throwing failure on an already-broken device, not a silent one) --
    /// strictly better than the stuck-keyed rig this trade buys.</summary>
    private async Task StopPlaybackWithWatchdogAsync()
    {
        Task stopTask;
        try
        {
            stopTask = _audioEngine.StopPlaybackAsync();
        }
        catch (Exception ex)
        {
            // A synchronous throw (e.g. ObjectDisposedException) never produces a Task at all.
            Log.CleanupStepFailed(_logger, "StopPlayback", ex);
            return;
        }

        var completed = await Task.WhenAny(stopTask, Task.Delay(_playbackStopWaitBudget)).ConfigureAwait(false);
        if (completed == stopTask)
        {
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Same best-effort contract as TryCleanupAsync -- see its own doc comment.
                Log.CleanupStepFailed(_logger, "StopPlayback", ex);
            }

            return;
        }

        Log.PlaybackStopWatchdogFired(_logger, _playbackStopWaitBudget);
        _ = stopTask.ContinueWith(
            t => Log.CleanupStepFailed(_logger, "StopPlayback (finished after watchdog)", t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>The cleanup un-key call is deliberately unconditional (see PlayWithPttAsync's own doc
    /// comment) -- but with RigId == "none" that means it throws on every single default-config
    /// transmit, since the null-object backend always throws from SetPttAsync. Routing that through
    /// the generic TryCleanupAsync would log a Warning with a stack trace on every transmit for the
    /// most common configuration (fresh install, no radio set up yet), undercutting that Warning's own
    /// purpose (flagging a genuinely stuck-keyed rig).
    ///
    /// <b>Blocker 2 fix (Tier A Batch 3 chunk 3a) -- this used to re-check
    /// <see cref="IRadioSessionService.RigId"/> AT CATCH TIME, which was unsound.</b>
    /// <c>RadioController.DisconnectAsync</c>/<c>DisposeAsync</c> both reset <c>_rigId</c> to
    /// <c>"none"</c> WITHOUT un-keying PTT, so the sequence [rig genuinely keyed by this transmit] ->
    /// [controller disconnects/disposes mid-cleanup] -> [SetPttAsync(false) throws "No radio
    /// connected"] -> [RigId now reads "none"] reported a physically keyed transmitter as the benign
    /// "nothing to unkey" case and swallowed it. <paramref name="pttKeyedOnRealRig"/> is captured
    /// ONCE, at the moment PTT was keyed, and is the only input to that decision now -- RigId is never
    /// re-read here.</summary>
    private async Task<bool> TryUnkeyPttAsync(bool pttKeyedOnRealRig, CancellationToken ct)
    {
        try
        {
            await _radioSession.SetPttAsync(false, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (pttKeyedOnRealRig)
            {
                Log.CleanupStepFailed(_logger, "PTT off", ex);
            }
            else
            {
                Log.PttUnkeySkippedNoRadio(_logger);
            }

            return false;
        }
    }

    /// <summary>Returns whether <paramref name="step"/> actually completed without throwing --
    /// callers that need to know (e.g. only clearing <see cref="_pttLocked"/> once a PTT-off command
    /// is confirmed sent, not just attempted) check this; callers that don't care can ignore it, same
    /// as before this return value was added.</summary>
    private async Task<bool> TryCleanupAsync(string stepName, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort cleanup step -- see PlayWithPttAsync's own doc comment for why a failure
            // here must never mask the original exception or block a sibling cleanup step. Still
            // logged at Warning (not swallowed silently) -- a failed "PTT off" step in particular
            // leaves the rig keyed, a safety-relevant condition a user needs to know about.
            Log.CleanupStepFailed(_logger, stepName, ex);
            return false;
        }
    }

    private static async IAsyncEnumerable<float> GenerateTone(
        double frequencyHz, TimeSpan duration, int sampleRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var totalSamples = (long)(duration.TotalSeconds * sampleRate);
        var angularStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (var i = 0L; i < totalSamples; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return (float)Math.Sin(angularStep * i);
            if (i % 4096 == 0)
            {
                await Task.Yield();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Round-2 fix: must be the very FIRST thing this method does, before even
        // AwaitInFlightKeyedTransmitAsync's read of _keyedTransmitCompletion just below -- this is the
        // other half of the publish-then-recheck race PlayWithPttAsync now performs against this same
        // field (see its own disposed-check right after publishing _keyedTransmitCompletion). Whichever
        // side's write happens first, the other side's read is guaranteed to observe it.
        _disposed = true;

        // ---- Blocker 3 (Tier A Batch 3 chunk 3a) ----
        // ORDER CHANGED DELIBERATELY: the PTT backstop now runs BEFORE StopReceivingAsync, which used
        // to be this method's first line. StopReceivingAsync ends in MiniAudioCaptureSession.Dispose's
        // own drain-thread join, which has NO timeout at all if a SamplesCaptured subscriber never
        // returns -- so a wedged drain thread hung shutdown before the PTT-off backstop was ever
        // reached, and the process exited (Program.cs's own ~10s host-teardown bound) with the
        // transmitter still keyed. Un-keying is the one step that must not queue behind anything else.
        //
        // This whole method's correctness also depends on DI teardown order: Program.cs resolves
        // IRadioSessionService before ISstvSessionService, so RadioController/RadioSessionService are
        // constructed first, registered as disposables first, and therefore disposed LAST --
        // IRadioSessionService is still live for everything below. If that resolution order ever
        // changes, this backstop silently stops working.
        await AwaitInFlightKeyedTransmitAsync().ConfigureAwait(false);

        // A rig can be physically keyed at shutdown for three distinct reasons, and _pttLocked only
        // covered one of them: an engaged PTT lock (_pttLocked), a TuneAsync(leaveKeyedAfterTune:true)
        // that deliberately left it keyed (_pttLeftKeyedByCall), and an in-flight transmit whose own
        // un-key never completed (_keyedTransmitCompletion still published after the bounded wait
        // above). Any of the three gets the same best-effort, bounded, swallowed un-key -- a failed
        // shutdown PTT-off must not prevent the rest of teardown from completing, but it is now
        // logged at Critical (inside UnkeyForCleanupAsync), not swallowed silently.
        var keyedTransmitStillInFlight = Volatile.Read(ref _keyedTransmitCompletion) is not null;
        if (_pttLocked || _pttLeftKeyedByCall || keyedTransmitStillInFlight)
        {
            // pttKeyedOnRealRig: true, and NOT a re-read of RigId (blocker 2's whole point). Every one
            // of the three states above is only reachable via a SetPttAsync issued against a
            // non-"none" RigId -- SetPttLockAsync only sets _pttLocked AFTER a successful set, the
            // null-object backend always throws, and _pttLeftKeyedByCall/_keyedTransmitCompletion are
            // only published when RigId was real at key time. A failure here is therefore always a
            // genuinely stuck-keyed rig, never the benign no-radio case.
            await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
        }

        await StopReceivingAsync().ConfigureAwait(false);

        if (Waterfall is IDisposable disposableWaterfall)
        {
            disposableWaterfall.Dispose();
        }

        // RX buffer subsystem Phase 7 (disposal-chain sub-piece): _decoder is ISstvDecoder-typed, not
        // IDisposable itself (RestartableSstvDecoder implements it, but ISstvDecoder deliberately
        // doesn't extend it -- avoids widening that interface's surface for one production
        // implementation's own resource-cleanup need), same duck-typed pattern as the Waterfall check
        // above. Still placed AFTER StopReceivingAsync so no in-flight PushSamples call can race the
        // decoder's own disposal.
        if (_decoder is IDisposable disposableDecoder)
        {
            disposableDecoder.Dispose();
        }
    }

    /// <summary>Blocker 3's wait. <c>TxControlsPaneViewModel.Dispose()</c> cancels an in-flight
    /// transmit's token WITHOUT awaiting the transmit task, so on window-close-during-TX the
    /// transmit's own <c>finally</c>/un-key can still be running while DI teardown proceeds. Bounded
    /// (see <see cref="_inFlightKeyedTransmitWait"/>'s sizing against Program.cs's own host-teardown
    /// bound) and never throws: whether the wait succeeds or times out, DisposeAsync's own backstop
    /// un-key runs next either way, and a redundant un-key is documented-harmless on every shipped
    /// backend (see SetPttLockAsync's own doc comment).</summary>
    private async Task AwaitInFlightKeyedTransmitAsync()
    {
        var pending = Volatile.Read(ref _keyedTransmitCompletion);
        if (pending is null)
        {
            return;
        }

        Log.WaitingForKeyedTransmitAtShutdown(_logger);
        var completed = await Task.WhenAny(pending.Task, Task.Delay(_inFlightKeyedTransmitWait)).ConfigureAwait(false);
        if (completed != pending.Task)
        {
            Log.KeyedTransmitCleanupWaitTimedOut(_logger, _inFlightKeyedTransmitWait);
        }
    }

    /// <summary><paramref name="totalSamplesEstimate"/> is <see langword="null"/> for
    /// <see cref="TuneAsync"/> (no <see cref="TransmitProgressChanged"/> reporting for a tone) and a
    /// real value for <see cref="TransmitAsync"/> (spec/18-path-to-1.0.md Medium item). The running
    /// sample counter is a local, not a field -- nothing must dangle across separate calls.</summary>
    private async Task PumpToPlaybackAsync(IAsyncEnumerable<float> samples, float gain, int sampleRate, long? totalSamplesEstimate, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;
        var samplesEnqueued = 0L;

        await foreach (var sample in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer[count++] = sample * gain;
            if (count == chunkSize)
            {
                await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                samplesEnqueued += count;
                ReportTransmitProgress(samplesEnqueued, totalSamplesEstimate, sampleRate);
                count = 0;
            }
        }

        if (count > 0)
        {
            await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            samplesEnqueued += count;
            ReportTransmitProgress(samplesEnqueued, totalSamplesEstimate, sampleRate);
        }
    }

    /// <summary>See <see cref="ISstvSessionService.TransmitProgressChanged"/>'s own doc comment for
    /// the full threading contract this raise site must uphold: synchronous on the playback pump
    /// thread, so a throwing subscriber must not be allowed to propagate out and abort the
    /// transmission this progress report belongs to.</summary>
    private void ReportTransmitProgress(long samplesEnqueued, long? totalSamplesEstimate, int sampleRate)
    {
        if (totalSamplesEstimate is not { } total || total <= 0)
        {
            return;
        }

        // Code-review finding: try/catch scoped to ONLY the invoke, not the fraction/TimeSpan math
        // above it -- a future change to that math throwing (e.g. a genuinely degenerate sampleRate)
        // would otherwise get misattributed to "a subscriber's handler threw" in the log.
        var fraction = Math.Clamp(samplesEnqueued / (double)total, 0.0, 1.0);
        var info = new TransmitProgressInfo(
            fraction,
            TimeSpan.FromSeconds(samplesEnqueued / (double)sampleRate),
            TimeSpan.FromSeconds(total / (double)sampleRate));

        try
        {
            TransmitProgressChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            // Hot-path rate-limiting (docs/logging-guidelines.md), same reasoning/pattern as
            // _decoderExceptionCount/_waterfallExceptionCount above: this fires roughly every 4096
            // samples (~2.7/s at 11025Hz), so an unguarded Error log per occurrence would flood the
            // log file across a multi-minute transmission if a subscriber keeps throwing.
            var count = Interlocked.Increment(ref _transmitProgressHandlerExceptionCount);
            if (count == 1 || count % ExceptionLogEveryN == 0)
            {
                Log.TransmitProgressHandlerFailed(_logger, count, ex);
            }
        }
    }

    private async Task EnqueueAllAsync(ReadOnlyMemory<float> chunk, CancellationToken ct)
    {
        var offset = 0;
        while (offset < chunk.Length)
        {
            var accepted = _audioEngine.EnqueuePlaybackSamples(chunk[offset..]);
            if (accepted == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
                continue;
            }

            offset += accepted;
        }
    }

    private async Task<AudioDeviceInfo> ResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var device = await TryResolveDeviceAsync(forCapture, ct).ConfigureAwait(false);
        if (device is null)
        {
            var kind = forCapture ? "capture" : "playback";
            var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
            var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;
            if (deviceId is null)
            {
                // TryResolveDeviceAsync already tried the backend-reported default and found none
                // (spec/18-path-to-1.0.md Critical item 1 / item 8) -- this is now the rarer
                // "genuinely no audio device available at all" case, not "user never opened
                // Options."
                Log.NoDeviceConfigured(_logger, kind);
                throw new InvalidOperationException($"No {kind} audio device configured, and no default {kind} device is available -- set one in Options before starting a session.");
            }

            await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
            var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;
            Log.ConfiguredDeviceNotFound(_logger, kind, deviceId, devices.Count);
            throw new InvalidOperationException($"Configured {kind} device '{deviceId}' was not found among currently available devices.");
        }

        return device;
    }

    /// <summary>Non-throwing counterpart to <see cref="ResolveDeviceAsync"/>, extracted from it (not
    /// duplicated) so <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s passive readout use and
    /// <see cref="ResolveDeviceAsync"/>'s action-that-should-fail-loudly use share one lookup.
    /// <see langword="null"/> means either "configured device not found" (a device WAS explicitly
    /// configured, but isn't among the currently enumerated devices -- deliberately NOT
    /// substituted with the default, since a device the user explicitly picked going missing is a
    /// real problem worth surfacing, not silently working around) or "nothing configured, and the
    /// backend reports no default device either" (spec/18-path-to-1.0.md Critical item 1 / item 8
    /// -- a genuinely rare case, e.g. a headless machine with no audio hardware at all). When
    /// nothing is explicitly configured but the backend DOES report a default, that default is
    /// returned here -- this deliberately changes what
    /// <see cref="GetConfiguredPlaybackDeviceNameAsync"/>/<see cref="GetConfiguredCaptureDeviceNameAsync"/>
    /// display: they now show the device that will actually be used, not a blank "not configured"
    /// placeholder that silently implied nothing would happen.</summary>
    private async Task<AudioDeviceInfo?> TryResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;

        await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
        var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;

        if (deviceId is not null)
        {
            return devices.FirstOrDefault(d => d.Id == deviceId);
        }

        var fallback = devices.FirstOrDefault(d => d.IsDefault);
        if (fallback is not null)
        {
            Log.UsingDefaultDevice(_logger, forCapture ? "capture" : "playback", fallback.Name);
        }

        return fallback;
    }

    public async Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
        return device?.Name;
    }

    /// <summary>See <see cref="ISstvSessionService.GetConfiguredCaptureDeviceNameAsync"/>.</summary>
    public async Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: true, ct).ConfigureAwait(false);
        return device?.Name;
    }

    private async Task<AudioDeviceSettings> LoadAudioSettingsAsync(CancellationToken ct)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Decoder PushSamples threw ({Count} occurrences so far)")]
        public static partial void DecoderPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Waterfall PushSamples threw ({Count} occurrences so far)")]
        public static partial void WaterfallPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manual ReSync requested")]
        public static partial void ReSyncRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manual Correct Slant requested")]
        public static partial void CorrectSlantRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Decode mode forced to {ModeId}")]
        public static partial void ModeForced(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX started: device={DeviceId}, sampleRate={SampleRate}Hz")]
        public static partial void RxStarted(ILogger logger, string deviceId, int sampleRate);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX stopped")]
        public static partial void RxStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "TX starting: mode={ModeId}, {Width}x{Height}")]
        public static partial void TxStarting(ILogger logger, string modeId, int width, int height);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tune starting: {FrequencyHz}Hz for {Duration}")]
        public static partial void TuneStarting(ILogger logger, double frequencyHz, TimeSpan duration);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TX volume set to {Percent}%")]
        public static partial void TxVolumeSet(ILogger logger, int percent);

        [LoggerMessage(Level = LogLevel.Debug, Message = "CaptureOverrunCount read raced a concurrent StopReceivingAsync; reporting 0")]
        public static partial void CaptureOverrunCountRaceObserved(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT keyed")]
        public static partial void PttKeyed(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "PTT key skipped -- no radio backend configured (RigId=\"none\")")]
        public static partial void PttSkippedNoRadio(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "PTT un-key skipped -- no radio backend configured (RigId=\"none\")")]
        public static partial void PttUnkeySkippedNoRadio(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT released")]
        public static partial void PttReleased(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT lock {Locked}")]
        public static partial void PttLockChanged(ILogger logger, bool locked);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback cancelled")]
        public static partial void PlaybackCancelled(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Playback failed")]
        public static partial void PlaybackFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup step '{StepName}' failed")]
        public static partial void CleanupStepFailed(ILogger logger, string stepName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "StopPlayback did not finish within {Budget}; continuing to the PTT un-key without waiting (the stop is still running in the background)")]
        public static partial void PlaybackStopWatchdogFired(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Critical, Message = "PTT MAY STILL BE KEYED -- the un-key command failed on a rig this session actually keyed. Check the radio and un-key it manually.")]
        public static partial void PttStillKeyedAfterFailedUnkey(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown is waiting for an in-flight keyed transmit to finish un-keying PTT")]
        public static partial void WaitingForKeyedTransmitAtShutdown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "In-flight keyed transmit did not finish its own PTT un-key within {Budget}; forcing an un-key from shutdown")]
        public static partial void KeyedTransmitCleanupWaitTimedOut(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Warning, Message = "No {Kind} audio device configured")]
        public static partial void NoDeviceConfigured(ILogger logger, string kind);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configured {Kind} device '{DeviceId}' not found among {AvailableCount} available devices")]
        public static partial void ConfiguredDeviceNotFound(ILogger logger, string kind, string deviceId, int availableCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "No {Kind} device configured -- using backend-reported default '{DeviceName}'")]
        public static partial void UsingDefaultDevice(ILogger logger, string kind, string deviceName);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning raised (approaching automatic restart threshold)")]
        public static partial void MaintenanceWarningRaised(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning cleared")]
        public static partial void MaintenanceWarningCleared(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RX force-stopped for required maintenance restart")]
        public static partial void MaintenanceCriticalStop(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance handler '{HandlerName}' threw")]
        public static partial void MaintenanceHandlerFailed(ILogger logger, string handlerName, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "TransmitProgressChanged handler threw ({Count} occurrence(s) so far this session)")]
        public static partial void TransmitProgressHandlerFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "CapturePausedForTransmitChanged handler threw (paused={Paused})")]
        public static partial void CapturePausedHandlerFailed(ILogger logger, bool paused, Exception ex);
    }
}
