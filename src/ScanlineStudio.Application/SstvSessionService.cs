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

    // Hot-path exception rate-limiting (docs/logging-guidelines.md's "Hot-path rule") -- these
    // handlers run on the audio engine's own capture-forwarding path, once per captured chunk;
    // logging every occurrence would turn a logging change into dropped RX samples. First
    // occurrence logs immediately, then only every Nth after that.
    private const int ExceptionLogEveryN = 200;
    private int _decoderExceptionCount;
    private int _waterfallExceptionCount;

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
    {
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
            await _radioSession.SetPttAsync(locked, ct).ConfigureAwait(false);
            _pttLocked = locked;
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

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded
    {
        add => _decoder.StationIdDecoded += value;
        remove => _decoder.StationIdDecoded -= value;
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
    /// documented race <c>MiniAudioEngine.CaptureOverrunCount</c>'s own doc comment describes (a
    /// concurrent <see cref="StopReceivingAsync"/> disposing the capture session between that
    /// property's field read and its underlying native call) -- auditor-caught: this property is
    /// polled every 250ms by <c>RxImagePaneViewModel</c>'s telemetry timer, an ordinary "user clicks
    /// Stop RX mid-poll" interleaving reachable on every real session, and a <c>DispatcherTimer</c>
    /// tick exception has nowhere safe to land (this port's own global unhandled-exception handler
    /// only logs, it doesn't recover). <c>0</c> is not a fallback value here -- it IS the documented
    /// contract value for "capture isn't running", so this doesn't hide a real failure, it just
    /// reaches the same answer a clean read would have found a moment later.</summary>
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
            device, settings.SampleRate, settings.CaptureThreadPriority,
            settings.PeriodSizeInFrames, settings.Periods, settings.CaptureChannelSource, ct).ConfigureAwait(false);

        _audioEngine.SamplesCaptured += _decoderHandler;
        _audioEngine.SamplesCaptured += _waterfallHandler;
        _isReceiving = true;

        // ultracode audit finding #6: legacy resets its AGC (CLVL::Init) at every TX<->RX transition
        // (Sound.cpp:398,443) -- this is that transition point on the RX-resuming side.
        _decoder.ResetAgc();
        Log.RxStarted(_logger, device.Id, settings.SampleRate);
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
        var stationId = await ResolveStationIdTransmitOptionsAsync(ct).ConfigureAwait(false);
        await PlayWithPttAsync(_encoder.EncodeAsync(mode, image, stationId, ct), _encoder.SampleRate, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves the CW-ID/FSK station-ID settings + operator identity into one fully-formed
    /// <see cref="StationIdTransmitOptions"/> right before each transmission -- see that type's own
    /// doc comment for why this resolution lives here (Application layer) rather than inside the
    /// pure-DSP encoder. Settings-boundary validation for WPM/tone-frequency lives here too (Phase 1
    /// code-review finding): a corrupted/hand-edited settings.json with WPM &lt;= 0 would otherwise
    /// make <c>CwMorseGenerator.MillisecondsPerDotFromWpm</c> return Infinity/negative -- this falls
    /// back to the documented default instead of letting a bad value reach the generator or abort an
    /// in-flight transmission.</summary>
    private async Task<StationIdTransmitOptions> ResolveStationIdTransmitOptionsAsync(CancellationToken ct)
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

    private async Task PlayWithPttAsync(IAsyncEnumerable<float> samples, int sampleRate, CancellationToken ct, bool leaveKeyedAfterCall = false)
    {
        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingAsync().ConfigureAwait(false);
        }

        var abnormalTermination = false;
        try
        {
            var device = await ResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
            var gain = (await GetTxVolumePercentAsync(ct).ConfigureAwait(false)) / 100f;
            var audioSettings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);

            if (!_pttLocked)
            {
                await _radioSession.SetPttAsync(true, ct).ConfigureAwait(false);
                Log.PttKeyed(_logger);
            }

            await _audioEngine.StartPlaybackAsync(
                device, sampleRate, audioSettings.PeriodSizeInFrames, audioSettings.Periods,
                audioSettings.StereoTxEnabled, ct).ConfigureAwait(false);
            await PumpToPlaybackAsync(samples, gain, ct).ConfigureAwait(false);
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
            // Cleanup must never be defeated by the very cancellation (Stop TX / SWR auto-cutoff,
            // spec/14-roadmap.md's Piece 6) that triggered it -- reusing the possibly-cancelled `ct`
            // here (the original shape) left the rig keyed indefinitely and RX capture permanently
            // stopped, since SetPttAsync/StartReceivingAsync both wait on an already-cancelled token
            // before ever sending anything. A fresh, non-linked, bounded-timeout token instead:
            // uncancellable-in-practice for Hamlib's native calls, but still gives up eventually if a
            // wedged rigctld TCP read would otherwise hang this cleanup forever. StopPlaybackAsync
            // moved in here too (previously inside the try, so it was skipped entirely on
            // cancellation, leaving the output stream open with buffered audio still draining). Each
            // step is independently guarded so one failure can never mask the original exception or
            // prevent a sibling cleanup step from running.
            using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
            await TryCleanupAsync("StopPlayback", () => _audioEngine.StopPlaybackAsync()).ConfigureAwait(false);

            // Only a NORMAL completion honors "stay keyed" (leaveKeyedAfterCall/lock) -- see this
            // method's own doc comment for why an abnormal termination always overrides both.
            var skipUnkeyAndRxResume = !abnormalTermination && (leaveKeyedAfterCall || _pttLocked);
            if (!skipUnkeyAndRxResume)
            {
                if (await TryCleanupAsync("PTT off", () => _radioSession.SetPttAsync(false, cleanupCts.Token)).ConfigureAwait(false))
                {
                    // Force-release: whether or not a lock was engaged, PTT is now confirmed
                    // physically off -- IsPttLocked must never report true once that's true.
                    _pttLocked = false;
                    Log.PttReleased(_logger);
                }
            }

            if (wasReceiving)
            {
                if (!skipUnkeyAndRxResume)
                {
                    await TryCleanupAsync("Resume RX", () => StartReceivingAsync(cleanupCts.Token)).ConfigureAwait(false);
                }
                else if (!abnormalTermination && _pttLocked)
                {
                    // Specifically the lock case, not leaveKeyedAfterCall -- a lock can stay engaged
                    // indefinitely with no automatic next step, unlike a Tune-into-satellite-pass
                    // workflow where the very next action is expected to key PTT again anyway. RX
                    // must resume once SetPttLockAsync(false) eventually un-keys, not be silently
                    // forgotten -- consumed there.
                    _rxPendingResumeAfterUnlock = true;
                }
            }
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
        await StopReceivingAsync().ConfigureAwait(false);

        // Audit-fix: app shutdown must never leave a locked rig keyed indefinitely just because
        // nothing called SetPttLockAsync(false) first -- best-effort, bounded, and swallowed (like
        // PlayWithPttAsync's own cleanup steps) since a failed shutdown-time PTT-off must not prevent
        // the rest of teardown from completing.
        if (_pttLocked)
        {
            using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
            if (await TryCleanupAsync("PTT off (shutdown)", () => _radioSession.SetPttAsync(false, cleanupCts.Token)).ConfigureAwait(false))
            {
                _pttLocked = false;
            }
        }

        if (Waterfall is IDisposable disposableWaterfall)
        {
            disposableWaterfall.Dispose();
        }
    }

    private async Task PumpToPlaybackAsync(IAsyncEnumerable<float> samples, float gain, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;

        await foreach (var sample in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer[count++] = sample * gain;
            if (count == chunkSize)
            {
                await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                count = 0;
            }
        }

        if (count > 0)
        {
            await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
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
                Log.NoDeviceConfigured(_logger, kind);
                throw new InvalidOperationException($"No {kind} audio device configured -- set one in settings before starting a session.");
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
    /// <see cref="ResolveDeviceAsync"/>'s action-that-should-fail-loudly use share one lookup --
    /// <see langword="null"/> for either "no device configured" or "configured device not found",
    /// not distinguished here (the caller-facing exception messages for those two cases still live
    /// in <see cref="ResolveDeviceAsync"/> alone, the only caller that needs them).</summary>
    private async Task<AudioDeviceInfo?> TryResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;
        if (deviceId is null)
        {
            return null;
        }

        await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
        var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;
        return devices.FirstOrDefault(d => d.Id == deviceId);
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "No {Kind} audio device configured")]
        public static partial void NoDeviceConfigured(ILogger logger, string kind);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configured {Kind} device '{DeviceId}' not found among {AvailableCount} available devices")]
        public static partial void ConfiguredDeviceNotFound(ILogger logger, string kind, string deviceId, int availableCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning raised (approaching automatic restart threshold)")]
        public static partial void MaintenanceWarningRaised(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning cleared")]
        public static partial void MaintenanceWarningCleared(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RX force-stopped for required maintenance restart")]
        public static partial void MaintenanceCriticalStop(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance handler '{HandlerName}' threw")]
        public static partial void MaintenanceHandlerFailed(ILogger logger, string handlerName, Exception ex);
    }
}
