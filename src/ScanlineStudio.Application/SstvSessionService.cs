using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService : ISstvSessionService
{
    // Test seam for holding file resolution across rate-change, cancellation and timeout races.
    internal Func<string, int, float[]?>? SoundFileSamplesResolverForTests { get; init; }

    private readonly IAudioEngine _audioEngine;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly IAudioDeviceMuteQuery _deviceMuteQuery;
    private readonly ISettingsStore _settingsStore;
    private readonly ISstvDecoder _decoder;
    private readonly ISstvEncoder _encoder;
    private readonly IMacroTextResolver _macroTextResolver;
    private readonly IRadioSessionService _radioSession;
    private readonly ILogger<SstvSessionService> _logger;
    private readonly TimeSpan _cleanupTimeout;
    private readonly TimeSpan _playbackStopWaitBudget;
    private readonly TimeSpan _inFlightKeyedTransmitWait;
    private readonly TimeSpan _playbackStallTimeout;
    private readonly Action<ReadOnlyMemory<float>> _decoderHandler;
    private readonly Action<ReadOnlyMemory<float>> _waterfallHandler;
    private readonly Action<ReadOnlyMemory<float>> _levelMeterHandler;
    private readonly Action<ReadOnlyMemory<float>> _recordingHandler;
    private readonly Action<ReadOnlyMemory<float>> _audioAutoSaveHandler;
    private readonly Action<ReadOnlyMemory<float>> _cwIdHandler;
    private readonly ICwIdDecoder _cwIdDecoder;

    // Piece C1 (RX tab Re-decode port): guards _recordingChunks/_recordingPath, contended by the
    // single audio-capture drain thread (inside _recordingHandler, once per captured chunk) and
    // whatever thread calls StartRecordingAsync/StopRecordingAsync/DisposeAsync. Never held across
    // an await -- StopRecordingAsync/FinalizeRecordingAsync grab-and-null the list reference under
    // the lock, then concatenate/write it OUTSIDE the lock, so the drain thread is never blocked for
    // the duration of a file write, only for the O(1) reference swap.
    private readonly object _recordingLock = new();

    // Non-null exactly while a recording is armed; the chunk list itself (not a single growing
    // array) since IAudioEngine.SamplesCaptured hands each invocation a fresh, independently-owned
    // array (IAudioEngine.cs's own contract) -- retaining the ReadOnlyMemory<float> chunk directly
    // needs no copy, unlike a List<float>.AddRange, which would do doubling-copy work inline on the
    // capture drain thread (the same "slow handler delays RX processing" hazard _decoderHandler's
    // own isolation exists to avoid).
    private List<ReadOnlyMemory<float>>? _recordingChunks;
    private string? _recordingPath;

    // Restart-required-settings backlog item 4 (2026-08-27): the rate the audio was ACTUALLY
    // captured at, snapshotted at StartRecordingAsync time under _recordingLock -- WavFile.Write
    // (FinalizeRecordingAsync below) uses THIS, never a fresh _decoder.SampleRate read at write time.
    // Round-4 plan-review finding: a fresh read at write time races a rate change landing between
    // FinalizeRecordingAsync clearing _recordingChunks (inside _recordingLock) and the actual
    // WavFile.Write call (outside it, after a Task.Run hop) -- a SEPARATE race from, and not closed
    // by, _sampleRateChangeInProgress below (that flag only prevents a NEW recording from starting
    // mid-change; this field closes the already-in-progress-recording's own header-vs-audio race).
    private int? _recordingSampleRate;

    // Restart-required-settings backlog item 4, round-4 finding R2: set/cleared under
    // _recordingLock for the WHOLE duration of RequestSampleRateAsync's own commit sequence --
    // StartRecordingAsync refuses to start while this is set, closing the TOCTOU a plain
    // "_recordingChunks is not null" check on ITS OWN would leave open (a recording starting in the
    // gap right after that check passes but before the rate change actually finishes).
    private bool _sampleRateChangeInProgress;

    // Configurations-preset backlog, Phase 1 (2026-08-28): the capture device id this class last
    // committed to, via EITHER a successful StartReceivingLockedAsync (latched there, from whatever
    // the resolver actually opened -- may differ from what was requested on a fallback) OR
    // ApplyCaptureDeviceLockedAsync's own idle-path commit (latched from the REQUEST, since there is
    // no open capture to resolve against yet). Null only until the first of either happens (never
    // seeded at construction). Code-review round-2 finding: NOT strictly "what's currently open" --
    // it can be optimistically set on the idle path before RX ever actually opens anything.
    // RequestCaptureDeviceAsync's own no-op guard compares against THIS,
    // not against persisted AudioDeviceSettings -- TryResolveDeviceAsync's own
    // persistIfResolvedIndirectly write can silently change that field to a fallback device behind
    // this class's back, so it is not a reliable "what's actually open" signal on its own. Mirrors
    // RequestSampleRateAsync's own no-op guard against _decoder.SampleRate, a property that is
    // always meaningful regardless of receiving state; this field is the closest analogue capture
    // devices have, since there is no persistent decoder-like object that always holds a current
    // device the way the decoder always holds a current sample rate.
    private string? _activeCaptureDeviceId;

    // Configurations-preset backlog, Phase 1: same TOCTOU-closing shape as
    // _sampleRateChangeInProgress immediately above, for a capture-device swap instead of a rate
    // change -- a SEPARATE dedicated flag, not a rename/generalization of the existing one, so
    // RequestSampleRateAsync's own already-audited (rounds 2/3/4/16-22) commit sequence is never
    // touched by this addition.
    private bool _captureDeviceChangeInProgress;

    private int _recordingExceptionCount;

    // Piece C2: single-flight guard for DecodeFromFileAsync, same CompareExchange shape as
    // _transmitInFlight (0 = idle, 1 = a call owns it) -- see that field's own doc comment for the
    // failure class this pattern closes. A separate field, not a reuse of _transmitInFlight: the two
    // are cross-checked against each other (PlayWithPttAsync's own entry guard below, and
    // DecodeFromFileAsync's own) rather than sharing one flag, so a file decode and a transmit each
    // reject the OTHER cleanly instead of silently overlapping.
    private int _fileDecodeInFlight;

    /// <summary>Single-flight guard for <see cref="RunLoopbackSelfTestAsync"/> -- same
    /// <c>Interlocked.CompareExchange</c> shape as <see cref="_fileDecodeInFlight"/> above, but a
    /// SEPARATE field, not a reuse of it: the self-test's own decoder is a private, throwaway
    /// instance with no relationship to the shared decoder <see cref="_fileDecodeInFlight"/> guards,
    /// so the two must never contend with each other -- two self-test calls racing is the only thing
    /// this needs to prevent.</summary>
    private int _loopbackSelfTestInFlight;

    /// <summary>Backs <see cref="RawInputPeakLevel"/> -- see that property's own doc comment for why
    /// this exists as a THIRD fan-out target alongside <see cref="_decoderHandler"/>/
    /// <see cref="_waterfallHandler"/> rather than reusing either one's own internal state. Written
    /// only from <see cref="_levelMeterHandler"/> on the audio engine's own capture/drain thread (same
    /// single-writer shape as those two handlers' own targets), read from whatever thread polls
    /// <see cref="RawInputPeakLevel"/> (a UI-thread <c>DispatcherTimer</c> in practice) -- volatile
    /// float is a well-defined atomic read/write in C#/.NET (unlike volatile double, which isn't
    /// legal), so no lock is needed for this benign, approximate meter value.</summary>
    private volatile float _rawInputPeakLevel;

    /// <summary>The Pwr gain <see cref="PumpToPlaybackAsync"/> actually multiplies each outgoing
    /// sample by RIGHT NOW -- read fresh once per 4096-sample chunk (not captured once per
    /// <see cref="PlayWithPttAsync"/> call, the older behavior) specifically so a user can drag the
    /// Pwr slider WHILE a <see cref="TuneAsync"/> tone or a live <see cref="TransmitAsync"/> is
    /// already playing and hear/see the change immediately -- the WSJT-X-style "key Tune, watch the
    /// radio's own power meter, dial Pwr to the wattage you want" workflow this exists for doesn't
    /// work at all if gain is frozen at whatever it was the instant PTT keyed. Written by
    /// <see cref="SetTxVolumePercentAsync"/> (every caller of that method updates this immediately,
    /// not just the settings file), seeded from the persisted setting at the start of each
    /// <see cref="PlayWithPttAsync"/> call in case nothing has called
    /// <see cref="SetTxVolumePercentAsync"/> yet this process's life. Same volatile-float-is-a-
    /// well-defined-atomic-read/write reasoning as <see cref="_rawInputPeakLevel"/> right above --
    /// this is a single float, not a struct/tuple, so no lock is needed for cross-thread visibility.</summary>
    private volatile float _liveTxGain = 1f;
    // Ultracode audit finding #34's OnDecoderRestartCriticallyOverdue can now call StopReceivingAsync
    // (which writes this) from the audio drain thread, not just from a UI-thread-initiated
    // StartReceivingAsync/StopReceivingAsync call -- volatile for the same reason _pttLocked below
    // already is (this field's own reads/writes now span more than one caller thread with no lock
    // between them).
    private volatile bool _isReceiving;

    // RX pause/resume toggle (elegant-wondering-hinton.md, port of legacy's RxAutoPush toggle,
    // Main.cpp:6042-6060) -- session-owned, NOT decoder state (see ISstvDecoder.RequestAbandonReception's
    // own doc comment for why). Written from the UI thread (SetAutoDetectPaused), read on the audio
    // engine's own capture/drain thread inside _decoderHandler below, before every PushSamples call --
    // volatile for the same cross-thread-visibility reason _isReceiving above already is.
    private volatile bool _autoDetectPaused;

    // One-shot: true only in the brief window between SetAutoDetectPaused(true) and the NEXT audio
    // callback actually draining it (forwarding one empty buffer so the decoder's own deferred
    // RequestAbandonReception applies promptly instead of possibly sitting pending until resume --
    // see _decoderHandler's own gate below). Also volatile, same reasoning as _autoDetectPaused.
    private volatile bool _autoDetectPauseDrainPending;

    // Round-15 nit: every other cross-thread flag on this class (_isReceiving immediately above,
    // _pttLocked/_rxPendingResumeAfterUnlock below) is volatile with an explicit comment justifying
    // it -- this one is written from OnDecoderRestarted/OnDecoderRestartCriticallyOverdue (see the
    // latter's own doc comment: reachable from the audio drain thread, not just a UI-thread caller)
    // and read from OnDecoderRestarted, with no lock between them.
    private volatile bool _maintenanceWarningActive;

    // T1-5 (production_audit.md): the PTT keying/un-keying state machine -- _pttLocked,
    // _rxPendingResumeAfterUnlock, _keyedTransmitCompletion, _pttLeftKeyedByCall,
    // _pttUnkeyFailedOnRealRig, _pttKeyEpoch, _pttUnkeyEpoch, _keyedTransmitCount -- was extracted
    // into PttSafetyCoordinator (three rounds of plan-review, see that class's own doc comment for
    // the full field-ownership/lost-update-race reasoning). This class keeps every await/Log.* call
    // (all the orchestration); the coordinator owns only the field reads/writes that decide what to
    // record. _disposed and _transmitInFlight deliberately stay here -- see the coordinator's own
    // doc comment for why.
    private readonly PttSafetyCoordinator _pttCoordinator = new();

    // Tier A Batch 3 chunk 3a round-2 finding: DisposeAsync's backstop only guards
    // _keyedTransmitCompletion state published BEFORE it runs -- a PlayWithPttAsync call that hasn't
    // reached its own publish point yet (e.g. RadioStatusViewModel.TuneAsync's real production call
    // passes CancellationToken.None, so nothing can ever cancel it) could previously key PTT AFTER
    // DisposeAsync had already run its backstop and returned, with no shutdown safety net left to catch
    // it. Read by PlayWithPttAsync's own thread, written by whatever thread calls DisposeAsync -- same
    // threading shape as _pttLocked above.
    private volatile bool _disposed;

    // Round-12 finding: two overlapping PlayWithPttAsync calls (TransmitAsync racing TuneAsync --
    // RadioStatusViewModel's own Tune command has no TX-in-progress CanExecute gate, so clicking Tune
    // during a TransmitAsync produces two live calls -- see _keyedTransmitCount's own comment for this
    // same reachability) used to let the SECOND call unconditionally re-key and call
    // StartPlaybackAsync, which throws "already started" against the FIRST call's own live playback
    // session -- that throw is caught generically, classified abnormalTermination, and its finally's
    // urgent un-key runs immediately, dropping the FIRST call's carrier mid-frame, followed by
    // StopPlaybackWithWatchdogAsync disposing the session out from under it. This is a DIFFERENT
    // failure mode than the leaked-keyed-transmitter class every other field on this class defends
    // against: PTT ends up correctly OFF, but a genuinely in-flight, correctly-behaving transmission
    // gets silently killed by an unrelated second call. A single-flight guard (CompareExchange at
    // PlayWithPttAsync's very entry, before ANYTHING else -- no RX pause, no device resolution, no PTT
    // touched) rejects the second call outright instead. Deliberately a SEPARATE field from
    // _keyedTransmitCount: that one is also incremented by SetPttLockAsync (a different, narrower
    // concern -- shutdown-wait visibility, not single-flight exclusivity) and is never incremented at
    // all when RigId == "none" (the "no radio configured" case still needs the SAME playback-session
    // exclusivity this field protects). 0 = idle, 1 = a call owns it; plain `int` is sufficient since
    // CompareExchange is the only operation ever performed on it.
    private int _transmitInFlight;

    // Hot-path exception rate-limiting (docs/logging-guidelines.md's "Hot-path rule") -- these
    // handlers run on the audio engine's own capture-forwarding path, once per captured chunk;
    // logging every occurrence would turn a logging change into dropped RX samples. First
    // occurrence logs immediately, then only every Nth after that.
    private const int ExceptionLogEveryN = 200;
    private int _decoderExceptionCount;
    private int _waterfallExceptionCount;
    private int _transmitProgressHandlerExceptionCount;
    private int _audioAutoSaveExceptionCount;
    private int _cwIdExceptionCount;

    public SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        IAudioDeviceMuteQuery deviceMuteQuery,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IMacroTextResolver macroTextResolver,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession,
        ICwIdDecoder cwIdDecoder,
        ILogger<SstvSessionService> logger)
        : this(audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder, macroTextResolver,
               waterfall, receivedImage, radioSession, cwIdDecoder, logger,
               cleanupTimeoutForTests: null, playbackStopWaitBudgetForTests: null,
               inFlightKeyedTransmitWaitForTests: null, playbackStallTimeoutForTests: null)
    {
    }

    /// <summary>Test-only: lets a test shrink the PTT-safety cleanup budgets (production 5s/5s/3s/5s)
    /// so the blocker-1/blocker-3/round-13 regression tests (Tier A Batch 3 chunk 3a) can actually let
    /// a budget EXPIRE without a multi-second-per-test suite -- same shape as
    /// <c>RxDiskLineStagingBuffer</c>'s own <c>disposeDrainTimeoutForTests</c> precedent.</summary>
    internal SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        IAudioDeviceMuteQuery deviceMuteQuery,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IMacroTextResolver macroTextResolver,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession,
        ICwIdDecoder cwIdDecoder,
        ILogger<SstvSessionService> logger,
        TimeSpan? cleanupTimeoutForTests,
        TimeSpan? playbackStopWaitBudgetForTests,
        TimeSpan? inFlightKeyedTransmitWaitForTests,
        TimeSpan? playbackStallTimeoutForTests)
    {
        _cleanupTimeout = cleanupTimeoutForTests ?? CleanupTimeout;
        _playbackStopWaitBudget = playbackStopWaitBudgetForTests ?? PlaybackStopWaitBudget;
        _inFlightKeyedTransmitWait = inFlightKeyedTransmitWaitForTests ?? InFlightKeyedTransmitWait;
        _playbackStallTimeout = playbackStallTimeoutForTests ?? PlaybackStallTimeout;

        _audioEngine = audioEngine;
        _deviceEnumerator = deviceEnumerator;
        _deviceMuteQuery = deviceMuteQuery;
        _settingsStore = settingsStore;
        _decoder = decoder;
        _encoder = encoder;
        _macroTextResolver = macroTextResolver;
        Waterfall = waterfall;
        ReceivedImage = receivedImage;
        _radioSession = radioSession;
        _cwIdDecoder = cwIdDecoder;
        _logger = logger;

        // Isolated fan-out (Phase-3 plan decision #3): a throwing/slow handler on one target must
        // never prevent the other from running -- this is what actually fixes the bug the pre-build
        // spike's original IWaterfallSource design would otherwise have reintroduced.
        _decoderHandler = samples =>
        {
            try
            {
                if (_autoDetectPaused)
                {
                    // Round-3 plan-review fix: forward one EMPTY buffer instead of skipping outright
                    // on the transition edge, so the decoder's own deferred RequestAbandonReception
                    // applies within about one callback instead of possibly sitting pending until
                    // resume. Same audio-callback thread as every other real PushSamples call here --
                    // NOT an out-of-band call from the UI thread (round-1's rejected design), so no
                    // overlapping-call risk.
                    if (_autoDetectPauseDrainPending)
                    {
                        _autoDetectPauseDrainPending = false;
                        PushSamplesToDecoder(ReadOnlyMemory<float>.Empty);
                    }

                    return;
                }

                PushSamplesToDecoder(samples);
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
                    SafeLog(() => Log.DecoderPushSamplesFailed(_logger, count, ex));
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
                    SafeLog(() => Log.WaterfallPushSamplesFailed(_logger, count, ex));
                }
            }
        };

        // See RawInputPeakLevel's own doc comment -- deliberately reads the RAW buffer directly off
        // this fan-out, not anything _decoderHandler/Waterfall.PushSamples derive from it, so this
        // never sees the decoder's own SSTV-band bandpass filtering. No try/catch needed: this is a
        // plain synchronous loop over already-in-hand memory with no external call that can throw.
        _levelMeterHandler = samples =>
        {
            var span = samples.Span;
            var peak = 0f;
            for (var i = 0; i < span.Length; i++)
            {
                var abs = Math.Abs(span[i]);
                if (abs > peak)
                {
                    peak = abs;
                }
            }

            _rawInputPeakLevel = peak;
        };

        // Piece C1: a 4th, INDEPENDENT fan-out target -- deliberately not subscribed/unsubscribed
        // alongside _decoderHandler/_waterfallHandler/_levelMeterHandler in
        // StartReceivingLockedAsync/StopReceivingLockedAsync (see StartRecordingAsync's own doc
        // comment for why: recording must survive a Stop/Start RX cycle while armed). Same isolated-
        // fan-out shape as the other three: a throwing subscriber here must never affect the others.
        _recordingHandler = samples =>
        {
            try
            {
                lock (_recordingLock)
                {
                    _recordingChunks?.Add(samples);
                }
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref _recordingExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    SafeLog(() => Log.RecordingPushSamplesFailed(_logger, count, ex));
                }
            }
        };

        // ui_transition_plan.md step 12 (Auto-save RX audio): a 5th fan-out target, but UNLIKE
        // _recordingHandler above, subscribed/unsubscribed alongside _decoderHandler/_waterfallHandler/
        // _levelMeterHandler in StartReceivingLockedAsync/StopReceivingLockedAsync -- this feature has
        // no user-initiated start/stop of its own, it simply runs whenever RX is active and the
        // setting is enabled, so it should stop/restart with capture the same way those three do
        // (see SstvSessionService.AudioAutoSave.cs for the full implementation).
        _audioAutoSaveHandler = samples =>
        {
            try
            {
                OnAudioAutoSaveSamplesCaptured(samples);
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref _audioAutoSaveExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    SafeLog(() => Log.AudioAutoSavePushSamplesFailed(_logger, count, ex));
                }
            }
        };

        // fsk_cwid.md §8.2: a 6th fan-out target -- subscribed LAST (see
        // SstvSessionService.CwId.cs's own OnCwIdSamplesCaptured doc comment for why the ORDER here,
        // after _decoderHandler, is load-bearing: the chunk-index invariant requires _pushedSampleCount
        // to already reflect the current chunk by the time this runs). Also called directly from
        // DecodeFromFileAsync's own chunk loop, same dual-caller shape as _waterfallHandler/_levelMeterHandler.
        _cwIdHandler = samples =>
        {
            try
            {
                OnCwIdSamplesCaptured(samples);
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref _cwIdExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    SafeLog(() => Log.CwIdPushSamplesFailed(_logger, count, ex));
                }
            }
        };

        // Subscribed unconditionally, for this object's whole lifetime -- these fire regardless of
        // live-capture state (including during a file decode, on the SAME shared _decoder), so this
        // is NOT tied to StartReceivingLockedAsync/StopReceivingLockedAsync the way the SamplesCaptured
        // handler above is. OnAudioAutoSaveModeDetected/OnAudioAutoSaveDecodeRestarted gate themselves
        // on _fileDecodeInFlight internally -- see their own doc comments.
        _decoder.ModeDetected += OnAudioAutoSaveModeDetected;
        _decoder.DecodeRestarted += OnAudioAutoSaveDecodeRestarted;

        // fsk_cwid.md §8.2: same unconditional-for-the-whole-session-lifetime wiring as the audio
        // auto-save pair immediately above, EXCEPT this one also fires during a file decode (no
        // _fileDecodeInFlight gate) -- see SstvSessionService.CwId.cs's own header comment for why.
        _decoder.ModeDetected += OnCwIdModeDetected;
        _decoder.DecodeRestarted += OnCwIdDecodeRestarted;

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

        // Restart-required-settings backlog item 2 (2026-08-27): same optional-side-channel shape as
        // ISstvDecoderMaintenance immediately above -- the various FakeSstvDecoders used by other test
        // projects don't implement this either, so this is a no-op there.
        if (_decoder is ISstvDecoderReconfiguration reconfiguration)
        {
            reconfiguration.ReconfigurationRejected += OnReconfigurationRejected;
        }
    }

    public IWaterfallSource Waterfall { get; }

    public IReceivedImageBuffer ReceivedImage { get; }

    public IReadOnlyList<SstvModeDefinition> AvailableModes => SstvModeRegistry.All;

    public double GetLeaderToneDurationMs(SstvModeDefinition mode) => AnalogFmSstvEncoder.GetLeaderToneDurationMs(mode);

    public (VisHeaderKind Kind, int Value) GetVisHeaderInfo(SstvModeDefinition mode) => AnalogFmSstvEncoder.GetVisHeaderInfo(mode);

    public bool IsReceiving => _isReceiving;

    /// <summary>See <see cref="ISstvSessionService.IsTransmitting"/>.</summary>
    public bool IsTransmitting => Volatile.Read(ref _transmitInFlight) != 0;

    /// <summary>See <see cref="ISstvSessionService.IsRecording"/>.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_recordingLock)
            {
                return _recordingChunks is not null;
            }
        }
    }

    /// <summary>See <see cref="ISstvSessionService.IsAutoDetectPaused"/>.</summary>
    public bool IsAutoDetectPaused => _autoDetectPaused;

    /// <summary>See <see cref="ISstvSessionService.SetAutoDetectPaused"/>. Session-owned toggle, not
    /// decoder state -- setting <see langword="true"/> requests the decoder abandon whatever's in
    /// progress (<see cref="ISstvDecoder.RequestAbandonReception"/>), arms the one-shot drain-pending
    /// flag so that request applies promptly (see <see cref="_decoderHandler"/>'s own gate), then
    /// stops forwarding real audio to the decoder until cleared. Capture and the waterfall are
    /// untouched either way.</summary>
    public void SetAutoDetectPaused(bool paused)
    {
        if (paused)
        {
            _decoder.RequestAbandonReception();
            _autoDetectPauseDrainPending = true;
            _autoDetectPaused = true;

            // fsk_cwid.md §8.2: SetAutoDetectPaused(true) raises neither AudioCaptureReset nor
            // DecodeRestarted, so an open CW arm would otherwise sit waiting for chunks that will
            // never arrive (OnCwIdSamplesCaptured's own _autoDetectPaused gate stops feeding it) until
            // the NEXT real ModeDetected happens to drop it -- drop it here, at the actual transition,
            // instead.
            DropCwArmForCaptureReset();
        }
        else
        {
            _autoDetectPaused = false;
            _autoDetectPauseDrainPending = false;
        }
    }

    public bool IsPttLocked => _pttCoordinator.IsPttLocked;

    /// <summary>Serializes <see cref="SetPttLockAsync"/> so two overlapping calls can never interleave
    /// (a real TOCTOU an earlier check-then-act version had) -- see that method's own doc comment for
    /// the full reasoning. Round-14 nit: deliberately never disposed by <see cref="DisposeAsync"/>,
    /// matching this class's post-dispose contract of reaching the idempotency/disposed check on a
    /// still-usable primitive rather than throwing from the primitive itself.</summary>
    private readonly SemaphoreSlim _pttLockGate = new(1, 1);

    /// <summary>Tier B audit finding (Area 3): serializes <see cref="StartReceivingAsync"/> and
    /// <see cref="StopReceivingAsync"/> against each other -- without this, both methods' own
    /// `_isReceiving` check-then-act (read at entry, published only after several awaits) had no gate
    /// between them, unlike <see cref="_pttLockGate"/>'s identical shape for <see cref="SetPttLockAsync"/>.
    /// A real UI repro: <c>RadioStatusViewModel.SetReceivingSafeAsync</c> is a fire-and-forget command
    /// with no busy guard, and the same class's own startup maintenance-retry path is a SEPARATE
    /// caller hitting <see cref="StopReceivingAsync"/>/<see cref="StartReceivingAsync"/> concurrently
    /// -- a Start parked mid-flight (e.g. in device
    /// enumeration) let a concurrent Stop read `_isReceiving == false` and silently no-op, leaving
    /// capture live with the UI reporting "not receiving" and no error surfaced; the mirror ordering
    /// dropped a Start instead. <see cref="StopReceivingAsync"/> takes no <see cref="CancellationToken"/>
    /// by design (must still work post-dispose, see that method's own doc comment), so it waits on this
    /// gate uncancellably AND bounded (by <c>_cleanupTimeout</c>) rather than indefinitely -- see that
    /// method's own doc comment for why an unbounded wait here is not safe, and what happens (skips its
    /// own body, logs, returns) on a timeout. Tier B audit finding (Area 4): <see cref="StartReceivingAsync"/>'s
    /// own wait is ALSO bounded by <c>_cleanupTimeout</c> now (in addition to the caller's own token) --
    /// an abandoned RX-resume (<see cref="ResumeReceivingBoundedAsync"/>'s own timed-out call, still
    /// genuinely running inside a native capture-start call that does not respect `ct` mid-flight) can
    /// hold this gate indefinitely, and every later Start needed its own bound to avoid hanging behind
    /// it forever -- unlike Stop's timeout, Start's THROWS <see cref="TimeoutException"/> rather than
    /// silently no-op'ing, since a caller of Start needs to know capture did not actually start.
    /// Tier B audit finding (Area 5, nit): same non-disposal contract as <see cref="_pttLockGate"/>
    /// above, for the same reason (deliberately never disposed by <see cref="DisposeAsync"/> -- see
    /// that field's own round-14 note) -- <see cref="StopReceivingAsync"/> must stay callable post-
    /// dispose on a still-usable primitive.</summary>
    private readonly SemaphoreSlim _rxTransitionGate = new(1, 1);

    /// <summary>Manual-keying diagnostic aid (e.g. a "PTT lock" button) -- keys PTT immediately and
    /// holds it keyed independent of any <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call,
    /// until unlocked. Operates directly on the PTT line only -- unlike <see cref="PlayWithPttAsync"/>,
    /// this method does NOT itself pause/resume RX capture; if a lock is engaged while a
    /// Transmit/Tune call is skipping its own un-key because of this lock (see
    /// <see cref="PlayWithPttAsync"/>'s own doc comment), THAT call's paused RX is what gets resumed
    /// here on unlock (see <c>PttSafetyCoordinator</c>'s own <c>_rxPendingResumeAfterUnlock</c>) --
    /// not a capture pause owned by this method itself.
    ///
    /// <b>Idempotent in OUTCOME, not by skipping redundant calls</b> (an audit-fix correction from an
    /// earlier version of this method that short-circuited when the requested state already matched
    /// <c>IsPttLocked</c> -- a real bug: <see cref="TuneAsync"/>'s <c>leaveKeyedAfterTune</c>
    /// leaves PTT physically keyed without ever setting it, and a failed
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
    /// escape hatch). Revisit if/when a real caller actually needs this closed.
    ///
    /// <b>Round-11/round-12: a failed ENGAGE attempt</b> (this method's own key command throwing
    /// after possibly physically keying the rig -- see the round-7/11/12 catch block below) DOES now
    /// attempt an immediate recovery un-key, but only when <c>PttSafetyCoordinator.HasSingleInFlightKeyedTransmit</c>
    /// reads true at that moment -- i.e. only when nothing else (no concurrent
    /// <see cref="PlayWithPttAsync"/> transmission, no other <see cref="SetPttLockAsync"/> call) has
    /// a registration in flight to endanger. Round 11 originally left this un-recovered entirely,
    /// reasoning an unconditional recovery would introduce a new instance of the race above; round 12
    /// corrected that -- the guarded version is safe (see the catch block's own comment for the full
    /// argument) and closes what would otherwise be an hours-long recorded-but-not-recovered window
    /// in the common (non-concurrent) case, which is the overwhelmingly likely one for this
    /// unwired manual diagnostic aid.</summary>
    public async Task SetPttLockAsync(bool locked, CancellationToken ct = default)
    {
        // Tier B audit finding: the command dispatch further down already special-cases the UNLOCK
        // direction to keep the emergency-unlock escape hatch un-cancellable at the backend gate
        // (round 19's own fix, see that call site's own comment for the full reasoning -- "it must
        // stay queued and eventually reach the rig, not be cancellable away"), but this earlier gate
        // wait was still unconditionally `ct`-cancellable -- a caller cancelling while queued behind
        // a wedged prior command (up to _cleanupTimeout) could abort the emergency unlock before it
        // ever reaches the command dispatch at all, the same class of gap one level up. Same
        // conditional token this method already uses for the command dispatch below.
        await _pttLockGate.WaitAsync(locked ? ct : CancellationToken.None).ConfigureAwait(false);

        // Round-8 finding: this call's own handle into the coordinator's keyed-slot registration --
        // see PttSafetyCoordinator.PublishKeyedSlot's own doc comment for why publishing BEFORE the
        // key command (and this method's own finally clearing ONLY this call's own instance) both
        // matter. Without this, a key command in flight here was invisible to BOTH DisposeAsync's
        // bounded shutdown WAIT (AwaitInFlightKeyedTransmitAsync reads the same registration) and its
        // four-state backstop check -- DisposeAsync could run to completion, dispose
        // IRadioSessionService, and only THEN would this call's own post-await disposal-race recovery
        // (the `if (locked && _disposed)` block below) get a chance to run -- against a radio session
        // that no longer exists.
        var attempt = new PttSafetyCoordinator.PttKeyAttempt();

        // Round-31 finding (nit): this used to be a NESTED try/finally (an inner one ending at
        // _pttLockGate.Release(), wrapped by an outer one whose own finally ran the
        // _keyedTransmitCount decrement AFTER that release) -- now a single try/finally, since moving
        // the decrement to run BEFORE the release (see that finally's own comment) removed the only
        // reason the outer wrapper existed. A single try/finally still guarantees the same thing the
        // outer one did: this runs no matter how the body below exits.
        try
        {
            // Round-2 fix: only the ENGAGE direction is rejected post-disposal -- an unlock must
            // stay a valid escape hatch for a rig this class already left keyed (matches
            // _pttLocked's own failed-unlock-stays-true reasoning below; disposal must never remove
            // the one remaining way to un-key a rig that is still physically keyed).
            if (locked)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            // Round-6 finding: snapshotted BEFORE the SetPttAsync await below, not after -- see
            // PttSafetyCoordinator's own doc comment for the lost-update race this closes. Same fix
            // shape UnkeyForCleanupAsync already uses; round 5 fixed only that twin location and
            // missed this one -- SetPttLockAsync's own unlock-path clears just below had the
            // identical gap.
            var epochAtUnkeyStart = _pttCoordinator.CurrentKeyEpoch;

            // Round-7 finding: captured ONCE, before the key command -- the same blocker-2 rule
            // PlayWithPttAsync already follows (never re-read RigId at catch/failure time, since
            // RadioController.DisconnectAsync/DisposeAsync can reset it to "none" without un-keying).
            // Only meaningful for the ENGAGE direction: an unlock failure doesn't need this --
            // TryUnkeyPttAsync already classifies that failure using the parameter passed to it, not
            // a fresh RigId read.
            var rigIsRealAtKeyTime = locked && _radioSession.RigId != "none";

            // Round-18 finding 5: the unlock direction's own SetPttAsync failure had NO catch arm
            // at all (the existing one below is filtered on rigIsRealAtKeyTime, which is always
            // false when `locked` is false) -- it propagated straight out with no log whatsoever,
            // not even a Warning. The safety net itself was never broken (_pttLocked is only ever
            // cleared on a CONFIRMED success further down, so it correctly stays true here,
            // and DisposeAsync's own backstop still catches it) -- but the operator got no signal
            // at all until shutdown, for a failed EMERGENCY unlock on a genuinely keyed rig, the
            // one action in this whole class an operator reaches for specifically because
            // something already went wrong.
            //
            // Round-24 finding (risk): round 18's own fix filtered this catch on a FRESH RigId
            // read (rigIsRealAtUnlockTime) -- the exact blocker-2 anti-pattern this file's own
            // established rule forbids (see rigIsRealAtKeyTime's own comment just above: never
            // re-read RigId at catch/failure time). If the CAT link drops between key and unlock
            // (RadioController.DisconnectAsync sets RigId to "none" WITHOUT un-keying, per its own
            // doc comment), the rig is still genuinely keyed but this filter reads "none" and never
            // matches AT ALL -- not even the inner belief-based gate below ever runs, so the
            // emergency unlock's own failure produced NO log whatsoever, verbatim the harm round 18
            // exists to prevent. Fixed by filtering on `!locked` instead: the inner gate just below
            // (_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig) is already the correct,
            // belief-based test and needs no device-identity filter layered on top of it.
            //
            // Round-32 finding (nit): the ENGAGE arm just below had the identical device-identity
            // filter round 24 already removed from the unlock arm above -- rigIsRealAtKeyTime is
            // captured once, before the key command (see its own comment), so it is not a fresh
            // read and not the blocker-2 pattern itself, but it is still a device-identity gate on
            // TOP of a belief-based test that doesn't need one. RadioController.ConnectAsync writes
            // _protocol before _rigId (RadioController.cs, ConnectAsync), so a key command landing
            // in that narrow window sees RigId == "none" with a real protocol already assigned --
            // rigIsRealAtKeyTime reads false, this catch never runs, and a mid-command failure here
            // leaves every shutdown-backstop flag untouched. Zero production callers reach this
            // arm today (SetPttLockAsync has none), so this stays a nit -- upgrade the moment it
            // gets one. Filtering on `locked` instead removes the device-identity gate the same way
            // round 24 already did for the unlock arm.

            if (rigIsRealAtKeyTime)
            {
                // Round-8 finding: published BEFORE the key command, same shape/reasoning as
                // PlayWithPttAsync's own publish (see its own comment) -- so DisposeAsync can never
                // tear IRadioSessionService down out from under this call's own in-flight key/un-key.
                _pttCoordinator.PublishKeyedSlot(attempt);

                // Round-9 finding: PlayWithPttAsync rechecks _disposed IMMEDIATELY after this same
                // publish, before ITS key command (see its own comment) -- SetPttLockAsync published
                // the registration but never adopted the matching recheck, so it could still issue a
                // key command after DisposeAsync had already read this registration as empty, found
                // nothing to do, and returned. The window is instruction-scale (this publish and the
                // key command below have no await between them), and this call still self-detects and
                // recovers via the existing `if (locked && _disposed)` block further down if the key
                // itself succeeds -- but that recovery then races IRadioSessionService's own DI
                // teardown, which is exactly the class of failure this whole chunk exists to close.
                // Closing it HERE, before the command is ever issued, is strictly better than relying
                // on the recovery alone. No `locked &&` needed here (unlike the recovery block
                // further down) -- this is already inside `if (rigIsRealAtKeyTime)`, which is itself
                // `locked && ...`.
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            // Round-29 finding (risk): declared out here, not inside the try below, so the two
            // catch arms can reach it for their own fault-observer attachment (see each catch's own
            // comment) -- a local declared inside a try is not in scope in that try's own catch
            // blocks. Nullable: if _radioSession.SetPttAsync itself throws synchronously (e.g. the
            // null-object backend), this is never assigned at all, and there is nothing to observe.
            Task? pttCommand = null;
            try
            {
                // Round-17 finding: this await had no bound of its own -- the identical gap
                // PlayWithPttAsync's own key command had before round 14's fix (see that call
                // site's own comment for the full reasoning; same WaitAsync treatment applies
                // here for the same reason). Verified against both shipped backends directly:
                // HamlibRadioProtocol.SetPttAsync's CallAsync wrapper takes NO CancellationToken
                // parameter at all -- once the blocking native rig_set_ptt call starts, nothing
                // can interrupt it -- and RigctldClientProtocol's reply read has no timeout of its
                // own either. A wedged rig here (engage OR disengage direction) previously hung
                // this call forever WHILE HOLDING _pttLockGate -- stranding every future
                // SetPttLockAsync call, including a future emergency unlock, the one escape hatch
                // this whole method exists to be.
                //
                // Round-19 correction: for the UNLOCK direction specifically, `ct` reaching the
                // COMMAND itself (not just the wait) has the identical shape TryUnkeyPttAsync's own
                // round-18 fix closed -- a caller cancelling `ct` while this call is queued behind
                // a wedged prior command aborts the un-key AT THE BACKEND'S REQUEST GATE, so it
                // never reaches the rig. This is the emergency-unlock escape hatch this method's
                // own doc comment says exists precisely for "when something already went wrong" --
                // it must stay queued and eventually reach the rig, not be cancellable away. The
                // ENGAGE direction keeps `ct` on the command (a cancelled key SHOULD be dropped,
                // matching the caller's actual intent); the wait itself stays bounded by `ct` in
                // both directions either way.
                pttCommand = _radioSession.SetPttAsync(locked, locked ? ct : CancellationToken.None);
                await pttCommand.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception) when (locked)
            {
                // Round-7 finding: SetPttAsync(true) throwing does not mean the rig wasn't physically
                // keyed -- matches PlayWithPttAsync's own "erring true costs at most one spurious
                // Warning; erring false is the blocker-2 silent swallow" reasoning (see its own comment
                // at the pttKeyedOnRealRig assignment), which SetPttLockAsync never adopted. Without
                // this, a mid-command failure here (e.g. RigctldClientProtocol writes the PTT command,
                // then the READ of its reply times out or the connection drops) leaves EVERY
                // shutdown-backstop flag false -- _pttLocked never gets set (this throw happens before
                // that write below), and nothing else records the attempt -- so DisposeAsync's
                // four-state check finds nothing to do and the process can exit with the transmitter
                // genuinely keyed, silently. _pttLeftKeyedByCall is reused as the marker (see its own
                // doc comment, widened to cover this second producer) rather than adding a new field,
                // since its existing meaning ("physically keyed, not tracked by _pttLocked") is exactly
                // this situation.
                //
                // Round-8 finding: this write is ITSELF exactly the lost-update shape rounds 5-7 already
                // closed at three other sites -- without bumping the epoch here too, a concurrent
                // UnkeyForCleanupAsync call whose own epoch snapshot predates this write can still wipe
                // it moments later, believing nothing new happened. Bumping errs conservative (a
                // concurrent un-keyer just skips its clears and logs PttUnkeyRaceLostToNewerKey instead
                // of wiping this call's state) -- same "erring true costs at most one spurious Warning"
                // rule this file already applies everywhere else.
                // Tier B audit finding: this write used to be unconditional inside the catch --
                // but with RigId == "none" (the default, no-radio-configured state), the whole
                // production chain (RadioSessionService -> RadioController -> NoneRadioProtocol)
                // throws SYNCHRONOUSLY, before pttCommand is ever assigned (still null here). That
                // path never sent anything to a real backend, so nothing was ever physically keyed
                // -- but the unconditional write latched _pttLeftKeyedByCall=true anyway, violating
                // that field's own documented invariant ("never fires for the benign RigId=='none'
                // path," see its own doc comment) permanently: neither clear site can ever run
                // without a confirmed un-key, which needs a real rig that was never involved.
                // DisposeAsync's backstop then fires a false Critical "PTT MAY STILL BE KEYED" on a
                // machine with no radio at all, and every SUBSEQUENT transmit's own baseline reads
                // this same stale true, repeating the false Critical for the rest of the process
                // (the round-30 signal-erosion class, now reachable with no radio configured).
                // Gated on pttCommand actually having been issued -- only a genuine dispatch to a
                // real backend can mean "may have physically keyed."
                if (pttCommand is not null)
                {
                    _pttCoordinator.RecordLockEngageFailed();
                    SafeLog(() => Log.PttKeyCommandFailedMayHaveKeyed(_logger));
                }

                // Round-30 finding (risk): this fault-observer attachment used to sit AFTER the
                // recovery-un-key await below (up to _cleanupTimeout, ~5s) -- but both shipped
                // backends serialize on a single approximately-FIFO request gate (see round-19's own
                // comment further down, and round-24's), so the recovery un-key cannot make
                // progress until the abandoned pttCommand clears that SAME gate. That makes "the key
                // command completes during the recovery await" not an unlucky race but the EXPECTED
                // ordering whenever the recovery does anything at all -- so by the time the old
                // placement's own IsCompleted check ran, it was almost always already true, and the
                // observer almost never actually attached on the exact path it exists for. Moved to
                // run BEFORE the recovery await, right after this catch's own state-latching and
                // Critical log, so nothing here depends on the recovery step below. This is the ONE
                // fault-observer site in this class with an await between the triggering throw and
                // the original placement's own IsCompleted check; the sibling sites (TryUnkeyPttAsync,
                // StopReceivingAsync, StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync, and
                // this method's own unlock-direction arm below) all attach immediately with no
                // intervening await, so their own placement is correct as-is.
                //
                // Round-31 finding (nit): an earlier version of this comment claimed "pttCommand is
                // fully assigned by this point (this catch only runs once the command has been
                // issued)" -- FALSE on one path, as round 29's own comment at pttCommand's own
                // declaration already correctly states: a synchronous throw from
                // _radioSession.SetPttAsync leaves pttCommand null, and this catch runs for that case
                // too. The code below was always correct regardless (the `is { IsCompleted: false }`
                // pattern already handles null), but the claim itself was wrong and could have misled
                // a future round into "simplifying" the null-tolerance away as redundant.
                if (pttCommand is { IsCompleted: false })
                {
                    _ = pttCommand.ContinueWith(
                        t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT command (finished after watchdog)", t.Exception!)),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                // Round-11 found this catch never attempts an immediate recovery un-key (unlike
                // PlayWithPttAsync's own finally), leaving the rig recorded-but-not-recovered for
                // potentially the rest of the process's life. Round-11 initially left this
                // deliberately unfixed, reasoning that an unconditional recovery un-key here would
                // introduce a NEW instance of this class's own "Known, accepted race" (a
                // concurrent, genuinely on-air PlayWithPttAsync transmission losing its carrier).
                //
                // Round-12 correction: that reasoning was wrong on two counts. First, the
                // "unconditional" un-key in PlayWithPttAsync's OWN finally already accepts the
                // identical harm today, unguarded, on a MORE reachable path (two overlapping
                // PlayWithPttAsync calls) -- so recovering here is not a new risk class, just a
                // second place accepting the same one. Second, and more usefully, a genuinely safe
                // GUARDED recovery is available and is what's implemented below: _keyedTransmitCount
                // was already incremented for THIS call at the publish above (guarded by
                // rigIsRealAtKeyTime, same as this catch), so it reads exactly 1 if and only if
                // nothing else currently holds a registration. When it's 1, there is nothing else
                // to endanger, and recovering immediately is strictly safe. When it's >1, this
                // skips the recovery -- byte-for-byte today's behavior, zero regression on the
                // concurrent case. The residual (something registers AFTER this check but before
                // the un-key command reaches the wire) is strictly narrower than the unguarded
                // window PlayWithPttAsync's own finally already accepts, and lands before that
                // call's own StartPlaybackAsync, not mid-frame. UnkeyForCleanupAsync's own epoch
                // snapshot (taken after this catch's own bump above) still correctly suppresses its
                // clears if a genuinely newer key raced it -- unchanged by this addition. Bounded:
                // holds _pttLockGate for at most _cleanupTimeout (UnkeyForCleanupAsync's own CTS),
                // same precedent as the existing in-gate recovery a few lines below. `await` inside
                // a `catch` followed by a bare `throw;` is valid C# and preserves the original
                // exception/stack trace -- ONLY if UnkeyForCleanupAsync itself doesn't throw a NEW
                // one first, which round 20 found it actually can (see its own comment). Guarded
                // here so the ORIGINAL key-command failure always reaches the caller, not a
                // logging-provider failure substituted in its place -- the state record itself
                // doesn't depend on this guard (UnkeyForCleanupAsync's own round-20 fix latches
                // _pttUnkeyFailedOnRealRig before it can throw), only which exception surfaces.
                //
                // Tier B audit finding: this comment's own premise -- "_keyedTransmitCount was
                // already incremented for THIS call at the publish above (guarded by
                // rigIsRealAtKeyTime, same as this catch)" -- stopped being true once this catch's
                // own filter above widened from `when (rigIsRealAtKeyTime)` to `when (locked)`: the
                // publish is still gated on rigIsRealAtKeyTime alone, so this catch can now run on
                // paths where THIS call never published at all. A bare count==1 check can then
                // misfire in both directions: it can read a DIFFERENT concurrent call's own
                // registration as "safe to recover" and un-key the rig out from under that other
                // call's genuinely in-flight, on-air transmission mid-frame -- the exact harm this
                // whole guard exists to prevent -- or it can read 0 and skip a recovery this call
                // itself should have run. keyedCompletion is non-null if and only if THIS call's own
                // publish actually ran, so it's the correct, call-scoped guard the count alone no
                // longer is.
                if (attempt.KeyedCompletion is not null && _pttCoordinator.HasSingleInFlightKeyedTransmit)
                {
                    try
                    {
                        await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
                    }
                    catch (Exception recoveryEx)
                    {
                        SafeLog(() => Log.CleanupStepFailed(_logger, "PTT recovery un-key", recoveryEx));
                    }
                }

                throw;
            }
            catch (Exception) when (!locked)
            {
                // Round-18 finding 5's fix, corrected by round 24: see the capture site's own
                // comment just above for why this filters on `!locked` rather than a fresh RigId
                // read. Deliberately simpler than the engage-direction arm above -- no epoch bump
                // (this call never successfully keyed anything; _pttLocked is untouched by this
                // catch and correctly remains true, so no lost-update race is possible here) and
                // no recovery attempt
                // (an unlock that itself failed has nothing safe to recover TO -- the existing
                // "Known, accepted race" this method's own doc comment already documents governs
                // any retry). Just makes the failure loudly visible immediately, matching
                // UnkeyForCleanupAsync's own Critical classification for the identical condition
                // (a real rig this call believed was keyed, whose un-key attempt failed).
                //
                // Round-19 correction: gated on this class actually having believed something was
                // keyed. SetPttLockAsync is documented as always issuing the command regardless of
                // current belief (idempotent-in-outcome) -- an operator can legitimately call
                // SetPttLockAsync(false) on a rig this class never believed was keyed at all. Without
                // this gate, a failed unlock of an already-unkeyed rig latched a false
                // _pttUnkeyFailedOnRealRig for the process lifetime and made DisposeAsync's own
                // backstop fire a spurious Critical "MAY STILL BE KEYED" for a rig that demonstrably
                // never was -- the exact signal erosion the round-4 fix (SetPttLockAsync's
                // successful-unlock clear) exists to prevent.
                if (_pttCoordinator.BelievesKeyed(_pttCoordinator.IsPttLocked))
                {
                    _pttCoordinator.RecordUnlockFailed();
                    SafeLog(() => Log.PttStillKeyedAfterFailedUnkey(_logger));
                }

                // Round-29 finding (risk): see the engage-direction arm's own comment above for why
                // -- identical reasoning applies here. This is the specific direction round 18's own
                // comment names as "must stay queued and eventually reach the rig" (CancellationToken.
                // None on the command itself), making a late failure a real, expected outcome that
                // previously went unobserved.
                if (pttCommand is { IsCompleted: false })
                {
                    _ = pttCommand.ContinueWith(
                        t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT command (finished after watchdog)", t.Exception!)),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                throw;
            }

            // Round-5/6 findings (engage direction: epoch bumped before the lock is recorded, closing
            // an ordering gap a concurrent un-keyer could otherwise observe) and round-4/6/26 findings
            // (disengage direction: every belief flag invalidated on a CONFIRMED un-key, but only if
            // nothing re-keyed while the command was in flight -- Race 1's own guard) -- see
            // PttSafetyCoordinator.CommitLockCommandSucceeded's own doc comment for the full reasoning,
            // including why the engage/disengage directions use genuinely different field policies.
            var commitResult = _pttCoordinator.CommitLockCommandSucceeded(locked, epochAtUnkeyStart);
            if (commitResult == PttSafetyCoordinator.PttLockCommitResult.UnlockRaceLostToNewerKey)
            {
                SafeLog(() => Log.PttUnkeyRaceLostToNewerKey(_logger));
            }

            // Round-3 finding: the pre-await disposed check above (line ~256) only catches the CHEAP,
            // common case. _radioSession.SetPttAsync is a real serial/TCP round-trip -- DisposeAsync can
            // run its entire backstop (which reads _pttLocked/_keyedTransmitCompletion, neither of which
            // this call has published yet) while this await is in flight, find nothing to do, and
            // return. Left uncorrected, this call would then set _pttLocked = true above with nothing
            // left to ever un-key it. Latent today (SetPttLockAsync has zero production callers -- see
            // this method's own doc comment), fixed anyway since the fix is cheap and this method is
            // otherwise fully hardened against the same race everywhere else in this class.
            //
            // Round-4 finding: the plain `_pttLocked = locked;` write above and the `_disposed` read
            // just below are release-store/acquire-load -- the SAME StoreLoad-reordering gap
            // PlayWithPttAsync's Interlocked.Exchange publish closes (see its own comment). DisposeAsync
            // already carries the matching half (Interlocked.MemoryBarrier right after its own
            // `_disposed = true` write); this fence is the other half. `_pttLocked` is `volatile`, so
            // Interlocked.Exchange on it is CS0420 -- a standalone full fence after the plain write is
            // the equivalent. Unlike Risk B further down (whose failure mode is a stranded RX pause,
            // not a leaked keyed transmitter -- see that comment for the criterion), this pattern's
            // failure mode IS a leaked keyed transmitter, so by this file's own stated rule it needs
            // the fence Risk B deliberately goes without.
            Interlocked.MemoryBarrier();

            if (locked && _disposed)
            {
                // Round-20 finding: guarded so a genuine ObjectDisposedException always reaches the
                // caller below -- UnkeyForCleanupAsync's own "never throws" contract turned out to
                // be false (see its own comment), and letting that substitute a logging-provider
                // failure for the real disposal signal misleads any caller that branches on
                // ObjectDisposedException specifically (this class's own established convention).
                // The state record itself doesn't depend on this guard -- UnkeyForCleanupAsync's own
                // round-20 fix latches _pttUnkeyFailedOnRealRig before it can throw.
                try
                {
                    await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
                }
                catch (Exception recoveryEx)
                {
                    SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (post-dispose recovery)", recoveryEx));
                }

                throw new ObjectDisposedException(GetType().FullName);
            }

            SafeLog(() => Log.PttLockChanged(_logger, locked));

            if (!locked && _pttCoordinator.TryConsumeRxResumePending())
            {
                try
                {
                    // Round-10 finding: bounded on a FRESH CancellationTokenSource, not the
                    // caller's own `ct` -- matching PlayWithPttAsync's own `rxResumeCts` pattern
                    // (see its own comment). Without this, a wedged capture device (device
                    // enumeration, settings I/O, or _audioEngine.StartCaptureAsync itself hanging)
                    // parks this call inside `_pttLockGate` indefinitely -- stranding the PTT
                    // lock/unlock escape hatch itself, since every other SetPttLockAsync call
                    // (including a future emergency unlock) blocks on the SAME gate. Not the
                    // leaked-keyed-transmitter class (the rig is already confirmed un-keyed by the
                    // time this runs -- see this block's own `!locked` guard), but a real
                    // availability bug in the one API this whole method exists to keep working.
                    //
                    // Round-16 correction: passing rxResumeCts.Token as the CALLEE's own `ct`
                    // parameter alone does NOT bound this -- MiniAudioDeviceEnumerator.RefreshAsync
                    // and MiniAudioEngine.StartCaptureAsync both only check `ct` at their own
                    // start/lock-acquire boundary, never during the actual blocking native call, so
                    // a token cancelling mid-call does not unblock it. WaitAsync(rxResumeCts.Token)
                    // closes this: it observes the SAME token's cancellation independently of
                    // whether the awaited task itself ever polls it, which is exactly what was
                    // missing -- round 10 through round 15 all assumed this CTS alone was already a
                    // real bound.
                    //
                    // Round-18 correction: WaitAsync alone STILL isn't enough -- StartReceivingAsync
                    // bottoms out in JsonSettingsStore.LoadAsync, which does blocking
                    // File.Exists/File.OpenRead before its own first `await` (see
                    // GetStationIdTransmitOptionsAsync's own comment for the full reasoning), so a
                    // hung network-mounted settings path never even returns a genuinely-pending Task
                    // for WaitAsync to race. Task.Run offloads that synchronous prefix onto a pool
                    // thread, closing the gap regardless of what StartReceivingAsync does internally.
                    using var rxResumeCts = new CancellationTokenSource(_cleanupTimeout);
                    await ResumeReceivingBoundedAsync(rxResumeCts).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SafeLog(() => Log.CleanupStepFailed(_logger, "Resume RX (after unlock)", ex));
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
            // Round-8 finding: signalled no matter how the above ended (success, un-key failure, a
            // throw from the disposal-race recovery, RX-resume failure) -- a DisposeAsync waiting on
            // this must never be left hanging until its own timeout by an unrelated failure. Same
            // CompareExchange pattern as PlayWithPttAsync's own inner finally (see its own comment):
            // only clear the field if it still points at THIS call's instance, so an overlapping
            // PlayWithPttAsync/SetPttLockAsync call can't have its own registration erased.
            //
            // Round-31 finding (nit): moved here, BEFORE _pttLockGate.Release() just below (this
            // used to run in a separate OUTER finally, AFTER the release) -- releasing the gate
            // first let a new SetPttLockAsync(true) call acquire it, publish its own registration,
            // and increment _keyedTransmitCount WHILE this call's own registration was still
            // counted, making round-12's own recovery guard
            // (Volatile.Read(ref _keyedTransmitCount) == 1, in the engage-direction catch above)
            // read 2 spuriously and skip the recovery un-key on a rig this call's own key command
            // may have physically keyed -- the precise harm round 12's fix exists to prevent.
            // Instruction-scale window (no await between the old release and decrement), the same
            // accepted-residual CLASS _pttKeyEpoch's own field doc documents -- but this specific
            // instance is now closed for free by reordering, not just documented. Safe to decrement
            // before releasing: a successful engage has already written _pttLocked = true and a
            // failed one _pttLeftKeyedByCall = true, so DisposeAsync's own backstop is never
            // blinded by the earlier decrement.
            _pttCoordinator.ReleaseKeyedSlot(attempt);

            _pttLockGate.Release();
        }
    }

    public long CurrentReceptionSequence => _decoder.ReceptionSequence;

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
            // Round-20 finding: this method's own doc comment (see its callers) says it exists
            // specifically so a throwing subscriber can never propagate out and mask a caller's real
            // exception -- but the log call right below could ALSO throw (the same logging-provider
            // failure this whole round is about), defeating that guarantee at one remove. SafeLog
            // closes it.
            SafeLog(() => Log.CapturePausedHandlerFailed(_logger, paused, ex));
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

    public async Task<string?> GetOperatorGridAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();
        return operatorSettings.Grid;
    }

    /// <summary>See <see cref="ISstvSessionService.SlantPpm"/> / <see cref="ISstvDecoder.SlantPpm"/>.</summary>
    public double? SlantPpm => _decoder.SlantPpm;

    /// <summary>See <see cref="ISstvSessionService.SyncSource"/> / <see cref="ISstvDecoder.SyncSource"/>.</summary>
    public SstvSyncSource SyncSource => _decoder.SyncSource;

    /// <summary>See <see cref="ISstvSessionService.SyncOffsetSamples"/> / <see cref="ISstvDecoder.SyncOffsetSamples"/>.</summary>
    public int? SyncOffsetSamples => _decoder.SyncOffsetSamples;

    /// <summary>See <see cref="ISstvSessionService.SignalPeakLevel"/> / <see cref="ISstvDecoder.SignalPeakLevel"/>.</summary>
    public double SignalPeakLevel => _decoder.SignalPeakLevel;

    /// <summary>See <see cref="ISstvSessionService.LiveSnrDb"/>.</summary>
    public double LiveSnrDb => _decoder.LiveSnrDb;

    /// <summary>See <see cref="ISstvSessionService.ReceptionSnrDb"/>.</summary>
    public double ReceptionSnrDb => _decoder.ReceptionSnrDb;

    /// <summary>See <see cref="ISstvSessionService.RawInputPeakLevel"/> for the full contract and
    /// why it's a separate value from <see cref="SignalPeakLevel"/> above.</summary>
    public double RawInputPeakLevel => _rawInputPeakLevel;

    /// <summary>See <see cref="ISstvSessionService.IsLevelOverdriven"/> / <see cref="ISstvDecoder.IsLevelOverdriven"/>.</summary>
    public bool IsLevelOverdriven => _decoder.IsLevelOverdriven;

    /// <summary>See <see cref="ISstvSessionService.AutoSlantEnabled"/> / <see cref="ISstvDecoder.AutoSlantEnabled"/> --
    /// genuinely live now (2026-08-27).</summary>
    public bool AutoSlantEnabled => _decoder.AutoSlantEnabled;

    /// <summary>See <see cref="ISstvSessionService.AfcEnabled"/> / <see cref="ISstvDecoder.AfcEnabled"/>.</summary>
    public bool AfcEnabled => _decoder.AfcEnabled;

    /// <summary>See <see cref="ISstvSessionService.SenseLevel"/> / <see cref="ISstvDecoder.SenseLevel"/>.</summary>
    public int SenseLevel => _decoder.SenseLevel;

    /// <summary>See <see cref="ISstvSessionService.RxBpfPreset"/> / <see cref="ISstvDecoder.RxBpfPreset"/>.</summary>
    public RxBpfPreset RxBpfPreset => _decoder.RxBpfPreset;

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
                // Round-22 finding (risk): this getter's own doc comment states the reason this catch
                // exists -- polled every 250ms by RxImagePaneViewModel's telemetry timer, and a
                // DispatcherTimer tick exception has nowhere safe to land. A throwing log call (a
                // broken logging provider) used to defeat that guarantee one frame deeper.
                SafeLog(() => Log.CaptureOverrunCountRaceObserved(_logger));
                return 0;
            }
        }
    }

    public event Action? MaintenanceWarningRaised;

    public event Action? MaintenanceWarningCleared;

    public event Action? MaintenanceCriticalStopRaised;

    public event Action? DecoderInstanceReplaced;

    public event Action? ReconfigurationRejected;

    /// <summary>See <see cref="ISstvSessionService.RequestReSync"/>.</summary>
    public void RequestReSync()
    {
        Log.ReSyncRequested(_logger);
        _decoder.RequestReSync();
    }

    /// <summary>See <see cref="ISstvSessionService.RequestNotch"/>.</summary>
    public void RequestNotch(bool enabled, double? frequencyHz)
    {
        Log.NotchRequested(_logger, enabled, frequencyHz);
        _decoder.RequestNotch(enabled, frequencyHz);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestPllTuning"/>.</summary>
    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz)
    {
        Log.PllTuningRequested(_logger, vcoGain, loopOrder, loopCutoffHz, outputOrder, outputCutoffHz);
        _decoder.RequestPllTuning(vcoGain, loopOrder, loopCutoffHz, outputOrder, outputCutoffHz);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestZeroCrossingTuning"/>.</summary>
    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz)
    {
        Log.ZeroCrossingTuningRequested(_logger, smoothingMode, outputOrder, outputCutoffHz, smoothingFrequencyHz);
        _decoder.RequestZeroCrossingTuning(smoothingMode, outputOrder, outputCutoffHz, smoothingFrequencyHz);
    }

    /// <summary>See <see cref="ISstvSessionService.ArmScopeCapture"/>.</summary>
    public void ArmScopeCapture(int size)
    {
        Log.ScopeCaptureArmed(_logger, size);
        _decoder.ArmScopeCapture(size);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSenseLevel"/>.</summary>
    public void RequestSenseLevel(int level)
    {
        Log.SenseLevelRequested(_logger, level);
        _decoder.SenseLevel = level;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAutoSyncEnabled"/>.</summary>
    public void RequestAutoSyncEnabled(bool enabled)
    {
        Log.AutoSyncEnabledRequested(_logger, enabled);
        _decoder.AutoSyncEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAutoStopEnabled"/>.</summary>
    public void RequestAutoStopEnabled(bool enabled)
    {
        Log.AutoStopEnabledRequested(_logger, enabled);
        _decoder.AutoStopEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSnrMeasurementEnabled"/>.</summary>
    public void RequestSnrMeasurementEnabled(bool enabled)
    {
        Log.SnrMeasurementEnabledRequested(_logger, enabled);
        _decoder.SnrMeasurementEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAutoSlantEnabled"/>.</summary>
    public void RequestAutoSlantEnabled(bool enabled)
    {
        Log.AutoSlantEnabledRequested(_logger, enabled);
        _decoder.AutoSlantEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAfcEnabled"/>.</summary>
    public void RequestAfcEnabled(bool enabled)
    {
        Log.AfcEnabledRequested(_logger, enabled);
        _decoder.AfcEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSyncRestartEnabled"/>.</summary>
    public void RequestSyncRestartEnabled(bool enabled)
    {
        Log.SyncRestartEnabledRequested(_logger, enabled);
        _decoder.SyncRestartEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestReconfiguration"/>.</summary>
    public void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode)
    {
        Log.ReconfigurationRequested(_logger, rxBpfPreset, demodType, rxBufferMode);
        if (_decoder is ISstvDecoderReconfiguration reconfiguration)
        {
            reconfiguration.RequestReconfiguration(rxBpfPreset, demodType, rxBufferMode);
        }
    }

    /// <summary>See <see cref="ISstvSessionService.RequestRxBpfPreset"/>.</summary>
    public void RequestRxBpfPreset(RxBpfPreset rxBpfPreset)
    {
        Log.RxBpfPresetRequested(_logger, rxBpfPreset);
        if (_decoder is ISstvDecoderReconfiguration reconfiguration)
        {
            reconfiguration.RequestRxBpfPreset(rxBpfPreset);
        }
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSampleRateAsync"/>.</summary>
    public async Task<SampleRateApplyResult> RequestSampleRateAsync(int sampleRate, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log.SampleRateChangeRequested(_logger, sampleRate);

        // Step 0 (round-2 finding B3): a no-op guard BEFORE touching anything -- without this, the
        // unconditional-every-Save call shape every other Request* method on this interface uses
        // would drop and reopen the capture session, aborting any in-progress reception, on every
        // Options Save that didn't even touch this setting.
        if (sampleRate == _decoder.SampleRate)
        {
            return SampleRateApplyResult.NoChange;
        }

        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "RequestSampleRateAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            throw new TimeoutException("Timed out waiting to apply a sample rate change -- a concurrent RX transition did not finish in time.");
        }

        try
        {
            // Re-checked now that the gate is actually held (round-4 finding R2, same discipline
            // DecodeFromFileAsync's own re-check above uses) -- a racing caller may have already
            // committed this exact rate while this call was waiting for the gate.
            if (sampleRate == _decoder.SampleRate)
            {
                return SampleRateApplyResult.NoChange;
            }

            lock (_recordingLock)
            {
                if (_recordingChunks is not null)
                {
                    Log.SampleRateChangeDeferred(_logger, sampleRate);
                    return SampleRateApplyResult.DeferredRecordingInProgress;
                }

                _sampleRateChangeInProgress = true;
            }

            try
            {
                return await ApplySampleRateLockedAsync(sampleRate).ConfigureAwait(false);
            }
            finally
            {
                lock (_recordingLock)
                {
                    _sampleRateChangeInProgress = false;
                }
            }
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    /// <summary>The actual commit sequence for <see cref="RequestSampleRateAsync"/> -- called only
    /// while <see cref="_rxTransitionGate"/> is held and the recording-in-progress check has already
    /// passed. Encoder (TX) applies independently of everything below -- RX and TX are separate
    /// streams (see <c>ISstvEncoderReconfiguration</c>'s own doc comment); called first so it always
    /// runs regardless of what happens to the decoder/capture side.</summary>
    private async Task<SampleRateApplyResult> ApplySampleRateLockedAsync(int sampleRate)
    {
        if (_encoder is ISstvEncoderReconfiguration encoderReconfig)
        {
            encoderReconfig.RequestSampleRate(sampleRate);
        }

        if (_decoder is not ISstvDecoderReconfiguration decoderReconfig)
        {
            // No live-apply side-channel on this decoder implementation (e.g. a fake used by a test
            // project) -- matches every other is-test-gated Request* method's own silent-skip
            // convention on this class.
            return SampleRateApplyResult.NoChange;
        }

        // Round-3 plan-review: stop-then-commit-then-restart, not commit-then-restart -- capture
        // being fully stopped BEFORE the decoder commits is what makes committing directly (no
        // idle-gating) safe at all. See ISstvDecoderReconfiguration.ApplyPendingReconfigurationNow's
        // own doc comment for the decoder-side half of this contract.
        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingLockedAsync().ConfigureAwait(false);
        }

        decoderReconfig.RequestSampleRate(sampleRate);
        var result = decoderReconfig.ApplyPendingReconfigurationNow();

        switch (result)
        {
            case SwapResult.Committed:
                if (Waterfall is IWaterfallSourceReconfiguration waterfallReconfig)
                {
                    waterfallReconfig.RequestSampleRate(sampleRate);
                }

                if (wasReceiving)
                {
                    await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }

                Log.SampleRateChangeApplied(_logger, sampleRate);
                return SampleRateApplyResult.Applied;

            case SwapResult.Rejected:
            case SwapResult.NothingPending:
                // Old inner decoder (or, for NothingPending, nothing at all) untouched, still at the
                // previous rate -- restore capture at that unchanged rate, don't touch the waterfall.
                if (wasReceiving)
                {
                    await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }

                Log.SampleRateChangeRejected(_logger, sampleRate);
                return SampleRateApplyResult.Rejected;

            case SwapResult.Busy:
                return await HandleSampleRateBusyAsync(decoderReconfig, wasReceiving).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unexpected {nameof(SwapResult)} value: {result}.");
        }
    }

    /// <summary>Round-3/round-4 plan-review finding C2: <see cref="SwapResult.Busy"/> is genuinely
    /// reachable via <see cref="StopReceivingLockedAsync"/>'s own watchdog-timeout/abandoned-stop
    /// path (a straggler <c>PushSamples</c> call can still be genuinely in flight even after that
    /// method returns) -- NOT retried (a 5s <see cref="IAudioEngine.StopCaptureAsync"/> timeout means
    /// the drain thread is genuinely wedged, so retrying inside any sane budget would not succeed
    /// either).</summary>
    private async Task<SampleRateApplyResult> HandleSampleRateBusyAsync(ISstvDecoderReconfiguration decoderReconfig, bool wasReceiving)
    {
        // Clears the now-stuck pending rate via the decoder's own equality guard (requesting the
        // CURRENT committed value) -- otherwise it would sit armed and get silently applied by a
        // future unrelated maintenance swap (exactly what round-4 finding C1 exists to prevent).
        decoderReconfig.RequestSampleRate(_decoder.SampleRate);

        Exception? restartFailure = null;
        if (wasReceiving)
        {
            try
            {
                await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                restartFailure = ex;
            }
        }

        Log.SampleRateChangeBusy(_logger, restartFailure);
        var message = restartFailure is null
            ? "Sample rate change failed: the decoder was busy after an abandoned capture stop; capture was restarted at the previous rate."
            : "Sample rate change failed: the decoder was busy after an abandoned capture stop, and restarting capture also failed.";
        throw new InvalidOperationException(message, restartFailure);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestCaptureDeviceAsync"/>. Same gate/no-op/
    /// defer-on-recording shape as <see cref="RequestSampleRateAsync"/> above -- see that method's
    /// own doc comment for the shared reasoning behind each step.</summary>
    public async Task<CaptureDeviceApplyResult> RequestCaptureDeviceAsync(string? deviceId, string? deviceName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log.CaptureDeviceChangeRequested(_logger, deviceId);

        // Step 0: a no-op guard BEFORE touching anything -- see _activeCaptureDeviceId's own doc
        // comment for why this compares against THAT field, not persisted settings.
        if (deviceId == _activeCaptureDeviceId)
        {
            return CaptureDeviceApplyResult.NoChange;
        }

        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "RequestCaptureDeviceAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            throw new TimeoutException("Timed out waiting to apply a capture device change -- a concurrent RX transition did not finish in time.");
        }

        try
        {
            // Re-checked now that the gate is actually held -- a racing caller may have already
            // committed this exact device while this call was waiting for the gate.
            if (deviceId == _activeCaptureDeviceId)
            {
                return CaptureDeviceApplyResult.NoChange;
            }

            lock (_recordingLock)
            {
                if (_recordingChunks is not null)
                {
                    Log.CaptureDeviceChangeDeferred(_logger, deviceId);
                    return CaptureDeviceApplyResult.DeferredRecordingInProgress;
                }

                _captureDeviceChangeInProgress = true;
            }

            try
            {
                return await ApplyCaptureDeviceLockedAsync(deviceId, deviceName).ConfigureAwait(false);
            }
            finally
            {
                lock (_recordingLock)
                {
                    _captureDeviceChangeInProgress = false;
                }
            }
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    /// <summary>The actual commit sequence for <see cref="RequestCaptureDeviceAsync"/> -- called only
    /// while <see cref="_rxTransitionGate"/> is held and the recording-in-progress check has already
    /// passed. Same stop-then-commit-then-restart shape as <see cref="ApplySampleRateLockedAsync"/> --
    /// capture being fully stopped BEFORE the settings write is what makes
    /// <see cref="StartReceivingLockedAsync"/>'s own fresh device resolution safe to rely on
    /// afterward.</summary>
    private async Task<CaptureDeviceApplyResult> ApplyCaptureDeviceLockedAsync(string? deviceId, string? deviceName)
    {
        var previousSettings = await LoadAudioSettingsAsync(CancellationToken.None).ConfigureAwait(false);
        var previousDeviceId = previousSettings.CaptureDeviceId;
        var previousDeviceName = previousSettings.CaptureDeviceName;

        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingLockedAsync().ConfigureAwait(false);
        }

        try
        {
            await PersistCaptureDeviceAsync(deviceId, deviceName, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Code-review round-1 finding: persisting the NEW device itself failing (e.g. disk
            // full/permission denied) used to leave RX silently dead with no restart attempt --
            // capture is already stopped at this point. Best-effort restore RX at the OLD device
            // before this exception propagates, so a persist failure doesn't ALSO strand RX. The
            // persist failure itself is a genuinely different problem than "device unresolvable"
            // below -- it must still propagate (not get silently turned into a Rejected result), a
            // restart failure here must not mask it (swallowed, not rethrown).
            if (wasReceiving)
            {
                try
                {
                    await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Swallowed deliberately -- the persist failure about to propagate is the one
                    // that matters; a restart failure here would just mask it.
                }
            }

            throw;
        }

        _activeCaptureDeviceId = deviceId;

        if (!wasReceiving)
        {
            Log.CaptureDeviceChangeApplied(_logger, _activeCaptureDeviceId);
            return CaptureDeviceApplyResult.Applied;
        }

        try
        {
            // StartReceivingLockedAsync resolves the device FRESH from the settings write just
            // above (ResolveDeviceAsync -> TryResolveDeviceAsync, SstvSessionService.cs -- no
            // parameter threading needed here) -- if resolution falls back to a DIFFERENT device
            // than requested (e.g. the requested one vanished between persist and restart), that
            // fallback is what actually ends up latched into _activeCaptureDeviceId by
            // StartReceivingLockedAsync itself (see that field's own doc comment), which is correct:
            // this method's own Applied result reflects reality, not blindly the input -- the log
            // line below reads _activeCaptureDeviceId for the same reason (code-review round-1
            // finding: this used to log the REQUESTED deviceId, which could be wrong on a fallback).
            await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            Log.CaptureDeviceChangeApplied(_logger, _activeCaptureDeviceId);
            return CaptureDeviceApplyResult.Applied;
        }
        catch (Exception ex) when (ex is AudioDeviceUnavailableException || (ex is InvalidOperationException and not ObjectDisposedException))
        {
            // Code-review round-1 finding: the original filter was `catch (InvalidOperationException)`
            // alone, which (a) MISSED the realistic failure -- MiniAudioEngine wraps every native
            // capture-open failure (device busy, unsupported rate, unplugged mid-swap) in
            // AudioDeviceUnavailableException, not InvalidOperationException, so that case escaped
            // uncaught, leaving RX dead with an unopenable device already persisted across restarts
            // -- while (b) being simultaneously TOO BROAD, since ObjectDisposedException derives from
            // InvalidOperationException and would have triggered a spurious rollback write during a
            // disposal race (StopReceivingLockedAsync's own watchdog can force _isReceiving=false
            // while a genuinely disposed engine still throws from underneath). Corrected filter
            // catches the real "device didn't come up" cases and explicitly excludes disposal.
            //
            // Roll back to whatever was active before this call and attempt ONE restart at that
            // value. If even THAT throws, this method lets it propagate uncaught (matches
            // HandleSampleRateBusyAsync's own "genuinely unrecoverable -- throw, don't return an enum
            // value pretending it's a normal outcome" precedent).
            await PersistCaptureDeviceAsync(previousDeviceId, previousDeviceName, CancellationToken.None).ConfigureAwait(false);
            _activeCaptureDeviceId = previousDeviceId;

            await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            // Code-review round-2 finding: the caught exception used to be discarded entirely, and
            // the log message used to unconditionally claim "no capture device could be resolved at
            // all" -- wrong for the REALISTIC case this filter also catches (a device that enumerates
            // but fails to actually open, e.g. AudioDeviceUnavailableException). Logging `ex` here
            // captures the real native reason regardless of which of the two catches fired.
            Log.CaptureDeviceChangeRejected(_logger, deviceId, ex);
            return CaptureDeviceApplyResult.Rejected;
        }
    }

    /// <summary>Writes the caller-REQUESTED device id/name directly -- unlike the existing
    /// <c>PersistResolvedDeviceAsync</c> (which persists whatever a resolution actually landed on),
    /// this persists exactly what the caller asked for, since <see cref="RequestCaptureDeviceAsync"/>
    /// is the one entry point that DECIDES the device, rather than resolving one from existing
    /// settings. Same read-modify-write shape as <see cref="PersistSenseLevelAsync"/> below.</summary>
    private async Task PersistCaptureDeviceAsync(string? deviceId, string? deviceName, CancellationToken ct)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var previous = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
            var updated = previous with { CaptureDeviceId = deviceId, CaptureDeviceName = deviceName };
            return appSettings.WithSection(AudioDeviceSettings.SectionKey, updated, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.PersistSenseLevelAsync"/>. Read-modify-write
    /// against whatever is currently persisted for this section, not a fresh
    /// <c>new SstvDecoderSettings { ... }</c> -- matches this codebase's own established convention
    /// for a targeted single-field settings write (e.g. <c>RxImagePaneViewModel.PersistQuickModeGridAsync</c>).</summary>
    public async Task PersistSenseLevelAsync(int level, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
            var updated = current with { SenseLevel = level };
            return appSettings.WithSection(SstvDecoderSettings.SectionKey, updated, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.PersistAfcEnabledAsync"/>. Same targeted
    /// read-modify-write shape as <see cref="PersistSenseLevelAsync"/> above.</summary>
    public async Task PersistAfcEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
            var updated = current with { AfcEnabled = enabled };
            return appSettings.WithSection(SstvDecoderSettings.SectionKey, updated, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.PersistRxBpfPresetAsync"/>. Same targeted
    /// read-modify-write shape as <see cref="PersistSenseLevelAsync"/> above.</summary>
    public async Task PersistRxBpfPresetAsync(RxBpfPreset preset, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
            var updated = current with { RxBpfPreset = preset };
            return appSettings.WithSection(SstvDecoderSettings.SectionKey, updated, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.TryGetScopeCaptureChannel0"/>. A plain read, no
    /// logging -- matches this class's own established convention of only logging COMMANDS
    /// (RequestNotch/ArmScopeCapture above), not polled getters.</summary>
    public double[]? TryGetScopeCaptureChannel0() => _decoder.TryGetScopeCaptureChannel0();

    /// <summary>See <see cref="ISstvSessionService.TryGetScopeCaptureChannel1"/>.</summary>
    public double[]? TryGetScopeCaptureChannel1() => _decoder.TryGetScopeCaptureChannel1();

    /// <summary>See <see cref="ISstvSessionService.RequestCorrectSlant"/>.</summary>
    public void RequestCorrectSlant()
    {
        Log.CorrectSlantRequested(_logger);
        _decoder.RequestCorrectSlant();
    }

    /// <summary>See <see cref="ISstvSessionService.AbortReception"/>.</summary>
    public void AbortReception()
    {
        Log.ReceptionAborted(_logger);
        _decoder.RequestAbandonReception();
    }

    /// <summary>See <see cref="ISstvSessionService.ForceMode"/>.</summary>
    public void ForceMode(SstvModeDefinition mode)
    {
        Log.ModeForced(_logger, mode.Id);
        _decoder.ForceMode(mode);
    }

    /// <summary>See <see cref="ISstvSessionService.SetModeLock"/>.</summary>
    public void SetModeLock(SstvModeDefinition? mode)
    {
        Log.ModeLockChanged(_logger, mode?.Id ?? "(unlocked)");
        _decoder.SetModeLock(mode);
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
            // Round-22 finding (nit): same log-before-invoke shape as OnDecoderRestartCriticallyOverdue's
            // own MaintenanceCriticalStop fix above -- a throwing log call used to skip Invoke() below.
            SafeLog(() => Log.MaintenanceWarningRaised(_logger));
            MaintenanceWarningRaised?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartOverdue), ex));
        }
    }

    private void OnDecoderRestarted()
    {
        try
        {
            if (_maintenanceWarningActive)
            {
                _maintenanceWarningActive = false;
                // Round-22 finding (nit): same log-before-invoke shape as the other 2 maintenance
                // handlers above -- a throwing log call used to skip Invoke() below.
                SafeLog(() => Log.MaintenanceWarningCleared(_logger));
                MaintenanceWarningCleared?.Invoke();
            }

            // Restart-required-settings backlog item 2 (2026-08-27): unconditional, unlike
            // MaintenanceWarningCleared above -- every swap replaces _inner, whether or not a
            // maintenance warning was ever raised for it, so any UI-layer cache of a decoder-read
            // value (currently only RxImagePaneViewModel.RxBpfPreset) needs this signal every time,
            // not just on the "overdue warning just cleared" subset.
            //
            // T1-4: also unconditionally logged, same reasoning -- a periodic restart with no prior
            // maintenance warning (the common case) used to leave zero trace of it anywhere.
            SafeLog(() => Log.DecoderRestarted(_logger));
            DecoderInstanceReplaced?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestarted), ex));
        }
    }

    /// <summary>See <see cref="ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.ReconfigurationRejected"/>
    /// for the full contract -- restart-required-settings backlog item 2 (2026-08-27). Originally
    /// log-only: <see cref="ISstvSessionService.RxBpfPreset"/>'s live refresh (via
    /// <see cref="DecoderInstanceReplaced"/>) made a rejected reconfiguration visible to the operator
    /// as the Receive tab's Input Chain card silently disagreeing with what Options shows (round-3
    /// plan-review Q3) -- no separate dialog/toast needed. That premise stopped holding once that
    /// same row became a live EDITOR, not a read-only mirror (restart-required-settings backlog item
    /// 6, RX BPF Receive-tab live dropdown, 2026-08-28): a rejected change now needs an explicit
    /// re-sync signal, or the row (and, if the rejected request came from the Receive tab, the
    /// already-written settings.json entry) would permanently show a preset that was requested but
    /// never actually applied. <see cref="ReconfigurationRejected"/> below now carries that signal.</summary>
    private void OnReconfigurationRejected()
    {
        try
        {
            SafeLog(() => Log.ReconfigurationRejected(_logger));
            ReconfigurationRejected?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnReconfigurationRejected), ex));
        }
    }

    private void OnDecoderRestartCriticallyOverdue()
    {
        try
        {
            // The decoder has ALREADY force-restarted unconditionally by the time this fires (see
            // ISstvDecoderMaintenance's own doc comment) -- this only needs to tear down capture and
            // notify the user, not request another swap. RestartableSstvDecoder raises this event
            // strictly after releasing its own swap lock, so ResetAgc() re-entering it from inside
            // StopReceivingLockedAsync below never contends for anything already held -- that part
            // is confirmed (round-3 plan review).
            //
            // T1-6 (production_audit.md), replaces the Round-15/Tier-B-Area-5 comments this method
            // used to carry (both correct as far as they went, neither resolved the real question --
            // see production_audit.md's own T1-6 note for that history): this fires SYNCHRONOUSLY on
            // the audio capture drain thread (RestartableSstvDecoder's maintenance events fire inline
            // from PushSamples, itself called from IAudioEngine.SamplesCaptured on that thread).
            // Blocking that thread on _rxTransitionGate is the real hazard -- NOT a deadlock. This
            // side is bounded by _cleanupTimeout via StopReceivingAsync's own
            // WaitAsync(_cleanupTimeout, ...). Whichever OTHER caller holds the gate matters too --
            // if it's a Stop-family caller (StopReceivingAsync/StopReceivingLockedAsync, e.g.
            // PlayWithPttAsync's own RX-pause below), IT is independently bounded by
            // StopReceivingLockedAsync's own `stopTask.WaitAsync(_cleanupTimeout)` around
            // MiniAudioEngine.DisposeCaptureSessionAsync's Task.Run(session.Dispose) -> drain-thread
            // Join (that WaitAsync is what actually bounds it, not the Task.Run dispatch itself) --
            // this drain thread's own bound alone is sufficient regardless. A Start-family holder
            // (StartReceivingAsync) is a DIFFERENT story -- its own far side (StartCaptureAsync's
            // native device open) is unbounded (this file's own Area-4 finding) -- but that doesn't
            // change the conclusion here: THIS thread still unblocks on its own _cleanupTimeout wait
            // regardless of how long the other holder's own work takes.
            // The real cost is a bounded ~5s FREEZE of this drain thread: capture keeps producing
            // audio into the native ring with nothing draining it (real RX audio dropped mid-image),
            // and the OTHER _rxTransitionGate holder's own stop likely times out and abandons its
            // still-closing native session (CaptureStopWatchdogFired) once its own bound is reached.
            // Reachable on an ordinary transmission, not just at shutdown -- PlayWithPttAsync's own
            // routine RX-pause-for-TX step (its entry, see that method's own doc comment) acquires
            // this exact gate too, not just DisposeAsync as the superseded comment here claimed.
            //
            // Fix: never let this drain-thread-originated call block waiting for a contended gate.
            // Try a zero-wait, non-blocking acquire first (SemaphoreSlim.Wait(0) never suspends, so
            // it can never itself hop this call off the drain thread or cause a wait).
            if (_rxTransitionGate.Wait(0))
            {
                // Fast path (the overwhelmingly common case: gate free). Calls the PRIVATE
                // already-locked method, not the public StopReceivingAsync -- this thread already
                // holds the gate, and it's a non-reentrant SemaphoreSlim. Genuinely synchronous and
                // safe end to end: StopReceivingLockedAsync has no await before
                // IAudioEngine.StopCaptureAsync(), so MiniAudioEngine.ClaimCaptureSessionAsync's own
                // speculative IsRunningOnDrainThread check still runs on THIS thread. In the normal
                // case it sees ITS OWN session, taking the thread-blocking Wait() branch -- no hop,
                // no self-join (MiniAudioCaptureSession.Dispose()'s own Thread.CurrentThread !=
                // _drainThread guard). It CAN legitimately see a different session or null instead --
                // e.g. MiniAudioEngine.DisposeAsync claims the capture session directly, without ever
                // touching _rxTransitionGate -- in which case ClaimCaptureSessionAsync takes its
                // async _captureLock.WaitAsync() branch instead. Still safe even then: the claim
                // returns null or a FOREIGN session (never one requiring THIS thread's own Join), so
                // no Task.Run(session.Dispose) this call triggers can ever target this thread -- see
                // MiniAudioEngine.ClaimCaptureSessionAsync's own doc comment for why that distinction
                // is load-bearing, not pedantic.
                try
                {
                    StopReceivingLockedAsync().GetAwaiter().GetResult();
                }
                finally
                {
                    _rxTransitionGate.Release();
                }

                NotifyMaintenanceCriticalStop();
            }
            else
            {
                // Contended -- defer instead of freezing this drain thread for up to _cleanupTimeout.
                // Returning immediately here lets DrainLoop exit normally, which is exactly what the
                // OTHER gate holder's own MiniAudioEngine.DisposeCaptureSessionAsync call is (in the
                // non-drain-thread case) blocked waiting for via its Task.Run(session.Dispose) join --
                // so deferring, not blocking, is what lets that other call actually finish and release
                // the gate. This deferred call then proceeds normally off the drain thread once the
                // gate frees. StopReceivingAsync/StopReceivingLockedAsync are already idempotent
                // no-ops if RX has already stopped by the time this runs (including post-DisposeAsync
                // -- neither method checks _disposed, by existing design; see StopReceivingAsync's own
                // doc comment). Known, accepted consequence of deferring: if a legitimate RX
                // start/resume (e.g. PlayWithPttAsync's own post-TX resume) lands in the window before
                // this deferred call actually runs, this stops THAT session too -- arguably still the
                // right outcome for a critical/overdue restart notification, just a genuinely new
                // ordering this synchronous call never had to consider before. Also deliberately NOT
                // guarded against firing MaintenanceCriticalStopRaised after DisposeAsync has already
                // torn this instance down -- parity with today's inline call, which has the identical
                // exposure and was never guarded either; not a new gap this fix introduces.
                SafeLog(() => Log.RxTransitionGateContendedDuringMaintenanceStop(_logger));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StopReceivingAsync().ConfigureAwait(false);
                        NotifyMaintenanceCriticalStop();
                    }
                    catch (Exception ex)
                    {
                        SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartCriticallyOverdue), ex));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartCriticallyOverdue), ex));
        }
    }

    // T1-6 (production_audit.md): extracted so both the fast (gate-free) and deferred (gate-contended)
    // paths above notify only AFTER the actual stop attempt completes, matching this method's own
    // pre-existing behavior (notify after the attempt, even if that attempt itself timed out and
    // stopped nothing -- unchanged by this fix, not strengthened). Round-22 finding (nit, still
    // applies): a throwing log call here must not skip MaintenanceCriticalStopRaised below (sequenced
    // after it) -- RX could already be force-stopped with the UI never told why.
    private void NotifyMaintenanceCriticalStop()
    {
        _maintenanceWarningActive = false;
        SafeLog(() => Log.MaintenanceCriticalStop(_logger));
        MaintenanceCriticalStopRaised?.Invoke();
    }

    public async Task StartReceivingAsync(CancellationToken ct = default)
    {
        // Round-3 finding: without this, PlayWithPttAsync's own "Resume RX" cleanup step could restart
        // capture (subscribing _decoderHandler/_waterfallHandler to a live engine, calling
        // _decoder.ResetAgc()) AFTER DisposeAsync had already disposed the waterfall/decoder --
        // reachable when AwaitInFlightKeyedTransmitAsync's bounded wait expires while a transmit's own
        // cleanup is still inside its resume-RX step. Every AWAITED caller of this method routes
        // through TryCleanupAsync or its own try/catch, so this degrades to a logged Warning there
        // rather than propagating unhandled. Round-19 correction: that guarantee does NOT cover an
        // ABANDONED call -- round 18's own Task.Run wrapping means a timed-out RX-resume keeps running
        // as untracked background work with no caller left to catch anything, which is exactly why this
        // check is no longer sufficient alone; see the SECOND recheck further down this method, just
        // before the publish that actually matters.
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Tier B audit finding (Area 4): bounded like StopReceivingAsync's own gate wait, not just
        // cancellable by the caller's own `ct` -- without this, an ABANDONED RX-resume
        // (ResumeReceivingBoundedAsync's own rxResumeCts already expired, so ITS OWN token is no
        // longer any help) can hold this gate forever if its own StartReceivingLockedAsync call is
        // still genuinely stuck inside StartCaptureAsync (round-16's own established "a native call
        // does not actually respect `ct` mid-flight" fact) -- every LATER caller of this method
        // (including the direct UI Start-RX path, which passes CancellationToken.None) would then
        // hang indefinitely trying to acquire an effectively-permanently-held semaphore, with no log,
        // no throw, just a stuck Receiving toggle. Same _cleanupTimeout budget, same reasoning as
        // StopReceivingAsync's own bound -- see that method's own comment.
        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "StartReceivingAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            throw new TimeoutException("Timed out waiting to start receiving -- a concurrent RX transition did not finish in time.");
        }

        try
        {
            await StartReceivingLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    private async Task StartReceivingLockedAsync(CancellationToken ct)
    {
        if (_isReceiving)
        {
            return;
        }

        // Code-review finding (round 2, elegant-wondering-hinton.md): _autoDetectPaused is
        // deliberately NOT reset here -- a DELIBERATE DEVIATION from verified legacy behavior, not a
        // legacy-fidelity claim (an earlier version of this comment wrongly claimed the latter; see
        // that finding for the correction). Legacy's own pause flag (pDem->m_SyncMode = -1,
        // Main.cpp:6042-6060) does NOT survive a transmit: TMmsstv::ToTX (Main.cpp:7360) calls
        // pDem->Stop() (guarded only by the TXLoopBack option, off by default -- Main.cpp:7358),
        // which sets m_SyncMode = 512 (sstv.cpp:1786) -- not a parking
        // state, but the entry to a 0.5s self-clearing wait (case 512/513, sstv.cpp:2243-2252) that
        // lands on 0 (unpaused) regardless of what the flag was before Stop(). So in legacy, pausing
        // then transmitting silently un-pauses ~0.5s after RX audio resumes, and legacy's own UI
        // honestly reflects that (SBAuto->Down re-derives from the flag, Main.cpp:5988). This port
        // chooses STICKY pause instead -- surviving TX and any Stop/Start RX cycle, changed only by
        // the user's own toggle (or QuickSelectMode's own explicit resume-on-force, see that
        // method's comment) -- because this is session/UX state, not DSP/protocol math (out of this
        // project's legacy-fidelity scope), and legacy's drop-after-TX reads as an artifact of
        // Stop()'s shared teardown path rather than an intentional design choice; an operator who
        // explicitly paused auto-detect should not have it silently resume just because they keyed
        // up. A fresh SstvSessionService instance still starts unpaused, via _autoDetectPaused's own
        // `false` field default -- no explicit reset needed for that case.
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
        _decoder.StationIdDecodeEnabled = stationIdSettings.FskIdRxEnabled ?? StationIdSettings.DefaultFskIdRxEnabled;

        // fsk_cwid.md §8.2: same "re-read the persisted section, cache into a field" pattern as
        // FskIdRxEnabled immediately above -- see SstvSessionService.CwId.cs's own _cwIdRxEnabled
        // field doc comment for why this mirrors that setting's shape rather than SetAutoSaveAudioEnabled's.
        _cwIdRxEnabled = stationIdSettings.CwIdRxEnabled ?? StationIdSettings.DefaultCwIdRxEnabled;
        _cwIdRxWindowSeconds = stationIdSettings.CwIdRxWindowSeconds ?? StationIdSettings.DefaultCwIdRxWindowSeconds;

        await _audioEngine.StartCaptureAsync(
            device, _decoder.SampleRate, settings.CaptureThreadPriority,
            settings.PeriodSizeInFrames, settings.Periods, settings.CaptureChannelSource, ct).ConfigureAwait(false);

        // Round-19 finding: rechecked HERE, immediately before publishing -- the entry check above is
        // no longer sufficient on its own now that RX-resume attempts run as abandoned Task.Run
        // background work on timeout (round 18's own Task.Run wrapping). An abandoned resume can pass
        // the entry check, then keep running through the awaits above while DisposeAsync proceeds and
        // disposes the decoder/waterfall/engine -- reaching this point afterward would subscribe
        // handlers to a disposed engine and call ResetAgc() on a disposed decoder. Throwing here is
        // caught by this method's own callers exactly like the entry check already is.
        //
        // Round-21 re-raised finding: throwing here alone used to leak the native capture session
        // StartCaptureAsync just opened above -- _isReceiving is still false at this point (it only
        // flips true below), so a later StopReceivingAsync call unconditionally early-returns via its
        // own `if (!_isReceiving) return;` guard, and this session is never closed. Rounds 18/19 made
        // an abandoned/timed-out RX-resume the NORMAL way to reach this branch (not an exotic race),
        // so this is a real session/device/thread leak at shutdown, not just a theoretical one.
        // Best-effort close the session we just opened before throwing -- swallow any failure from
        // that close, since ObjectDisposedException is already about to propagate and is the
        // operative signal to whatever caller/fault-observer is left; nothing here re-touches
        // _isReceiving, since it is correctly still false either way.
        if (_disposed)
        {
            try
            {
                await _audioEngine.StopCaptureAsync().WaitAsync(_cleanupTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture (abandoned resume, disposed mid-flight)", ex));
            }

            throw new ObjectDisposedException(GetType().FullName);
        }

        // Tier B audit finding (Area 4): same shape as the _disposed recheck just above, for a
        // DIFFERENT abandoned-resume hazard that recheck doesn't cover -- an ABANDONED RX-resume
        // (ResumeReceivingBoundedAsync's own rxResumeCts already expired, per that method's own
        // comment; round-16's own established "a native call does not actually respect `ct`
        // mid-flight" fact means StartCaptureAsync above can still be genuinely running well past
        // that point) reaching HERE would publish `_isReceiving = true` regardless of what happened
        // in the meantime -- including a NEWER PlayWithPttAsync call that is by then actively
        // transmitting, turning capture ON mid-keyed-TX even though that method's own entry-time
        // `wasReceiving` read correctly saw `false` and skipped pausing RX for it. `ct.IsCancellationRequested`
        // is a reliable signal this specific call IS exactly that abandoned resume:
        // ResumeReceivingBoundedAsync passes rxResumeCts.Token all the way through as this method's
        // own `ct`, and no other caller of this method ever cancels its own token (the direct UI
        // Start-RX path passes CancellationToken.None). Same close-the-just-opened-session-then-throw
        // shape as the _disposed recheck above.
        if (ct.IsCancellationRequested)
        {
            try
            {
                await _audioEngine.StopCaptureAsync().WaitAsync(_cleanupTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture (abandoned resume, caller gave up waiting)", ex));
            }

            throw new OperationCanceledException(
                "Abandoned RX-resume: the caller gave up waiting before StartCaptureAsync finished -- not publishing IsReceiving.", ct);
        }

        // Reset before subscribing: capture is running, but its callbacks must not enter
        // PushSamples while ResetAgc mutates the decoder's level-tracking state.
        // A reset failure still leaves real capture available; preserve swallow-and-log behavior.
        try
        {
            _decoder.ResetAgc();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "ResetAgc (RX start)", ex));
        }

        _audioEngine.SamplesCaptured += _decoderHandler;
        _audioEngine.SamplesCaptured += _waterfallHandler;
        _audioEngine.SamplesCaptured += _levelMeterHandler;
        _audioEngine.SamplesCaptured += _audioAutoSaveHandler;
        _audioEngine.SamplesCaptured += _cwIdHandler;
        _isReceiving = true;
        // Configurations-preset backlog, Phase 1 (2026-08-28): latched here, at the exact point
        // capture is confirmed genuinely open -- see this field's own doc comment for why it isn't
        // seeded at construction or read back from settings.
        _activeCaptureDeviceId = device.Id;

        // Round-22 finding (nit): round 21's own ResetAgc() fix above justified itself partly as "no
        // longer skips Log.RxStarted below" -- but this call itself was still unwrapped, so a throwing
        // provider still propagated out of this method after capture had genuinely started (swallowed
        // on the RX-resume path via TryCleanupAsync, but not on the direct UI Start-RX path).
        SafeLog(() => Log.RxStarted(_logger, device.Id, _decoder.SampleRate));
    }

    public async Task StopReceivingAsync()
    {
        // Bounded, not indefinite or uncancellable-and-unbounded -- DisposeAsync (this method's other
        // production caller alongside PlayWithPttAsync's entry and, since T1-6, the deferred
        // Task.Run OnDecoderRestartCriticallyOverdue schedules on gate contention) has its own
        // established contract of best-effort, never-hanging teardown (every other cleanup-path wait
        // in this file uses this same _cleanupTimeout budget). Without a bound, a concurrent StartReceivingAsync still
        // resolving its own device/settings (itself unbounded -- a separate, already-tracked risk-tier
        // finding) would make DisposeAsync's own call here hang for as long as that resolution takes.
        // Safe to just give up and return on timeout: by the time DisposeAsync calls this, _disposed
        // is already true, so the racing StartReceivingAsync's own recheck-then-throw unwinds and
        // closes the session it opened on its own -- this method has nothing left to do in that case
        // either way. A genuine (non-disposal) concurrent Start finishing slowly degrades the same way:
        // logged and skipped rather than silently no-op'd, which is still strictly better than the
        // race this gate exists to close (see _rxTransitionGate's own doc comment).
        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, CancellationToken.None).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "StopReceivingAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            return;
        }

        try
        {
            await StopReceivingLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    private async Task StopReceivingLockedAsync()
    {
        if (!_isReceiving)
        {
            return;
        }

        _audioEngine.SamplesCaptured -= _decoderHandler;
        _audioEngine.SamplesCaptured -= _waterfallHandler;
        _audioEngine.SamplesCaptured -= _levelMeterHandler;
        _audioEngine.SamplesCaptured -= _audioAutoSaveHandler;
        _audioEngine.SamplesCaptured -= _cwIdHandler;
        // RawInputPeakLevel's own contract: 0.0 whenever capture isn't running, never a stale
        // reading left over from before this stop -- no more buffers will arrive to overwrite it.
        _rawInputPeakLevel = 0f;
        try
        {
            // Round-16 finding: IAudioEngine.StopCaptureAsync takes no CancellationToken at all, and
            // its drain-thread join is unbounded by design (MiniAudioCaptureSession's own doc comment
            // -- only a managed SamplesCaptured subscriber that never returns can make it hang, but
            // the handlers are already detached two lines up, so that specific cause can't apply to
            // THIS call; a genuinely wedged native close is still possible). Mirrors
            // StopPlaybackWithWatchdogAsync's own shape exactly -- bounded with WaitAsync on the TASK
            // itself, not a token (there is none to bound), swallow-and-log on either a real failure
            // or a timeout, never propagate. WaitAsync specifically, not Task.WhenAny+Task.Delay: on
            // an ALREADY-COMPLETED task (the common case -- every ordinary uncontended stop, and the
            // synchronous drain-thread-inline path OnDecoderRestartCriticallyOverdue's own
            // GetAwaiter().GetResult() call relies on) WaitAsync returns the same task directly with
            // no hop, so that path stays genuinely synchronous and zero-allocation exactly as before;
            // WhenAny would force a state-machine yield even when nothing is actually pending, moving
            // ResetAgc()/the log line/MaintenanceCriticalStopRaised off the drain thread on EVERY
            // call, not just a hung one -- a real regression the WhenAny shape would have introduced.
            //
            // Round-17 correction: for that SAME drain-thread caller, the watchdog below bounds
            // NOTHING -- MiniAudioEngine's own claim-then-dispose sequence runs entirely inline and
            // synchronously on that thread (ClaimCaptureSessionAsync's thread-blocking Wait() branch,
            // then session.Dispose() called directly, not via Task.Run), so stopTask is already
            // completed by the time it reaches WaitAsync and the 5s budget is never actually
            // consulted. The watchdog only does real work for a NON-drain-thread caller (PlayWithPttAsync's
            // entry, DisposeAsync, and, since T1-6, the deferred Task.Run
            // OnDecoderRestartCriticallyOverdue itself schedules on _rxTransitionGate contention --
            // that deferred call runs on a genuine pool thread, not the drain thread it was
            // originally invoked from).
            Task stopTask;
            try
            {
                stopTask = _audioEngine.StopCaptureAsync();
            }
            catch (Exception ex)
            {
                // A synchronous throw (e.g. ObjectDisposedException) never produces a Task at all --
                // same shape as StopPlaybackWithWatchdogAsync's own sync-throw arm. Round-17 nit:
                // no early `return` here (unlike that method) -- this one still has ResetAgc()/
                // Log.RxStopped() below the finally, and skipping them left the legacy-parity TX<->RX
                // AGC reset (ultracode finding #6) un-run and no "RX stopped" line ever logged on this
                // specific path, even though _isReceiving still correctly flips false via the finally
                // either way.
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture", ex));
                stopTask = Task.CompletedTask;
            }

            try
            {
                await stopTask.WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The handlers are already detached above, so "not receiving" (set in the finally
                // below regardless of this branch) is the only state consistent with reality whether
                // or not the underlying stop call ever actually finishes. The abandoned stopTask is
                // left running in the background -- observe its eventual fault so it doesn't surface
                // as an unobserved task exception at GC time, detached from this call's own context.
                //
                // Round-17 nit: the capture-side twin of StopPlaybackWithWatchdogAsync's own documented
                // "known consequence" (see that method's own doc comment) -- a following
                // StartReceivingAsync can now open a SECOND native session while this abandoned one is
                // still closing. Verified benign here (unlike that residual concurrent-open risk on
                // the playback side, which is flagged, not fixed): MiniAudioEngine's own claim step
                // nulls _captureSession AND unsubscribes SamplesAvailable together, before release, so
                // the abandoned session cannot interleave audio into the new one.
                SafeLog(() => Log.CaptureStopWatchdogFired(_logger, _cleanupTimeout));
                _ = stopTask.ContinueWith(
                    t => SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture (finished after watchdog)", t.Exception!)),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture", ex));
            }
        }
        finally
        {
            // Round-12 finding: moved into a finally -- previously, if StopCaptureAsync threw,
            // _isReceiving stayed true with the handlers already detached above. Every subsequent
            // StartReceivingAsync call would then silently early-return (its own `if (_isReceiving)
            // return;` guard, believing capture was already running), leaving RX invisibly dead for
            // the rest of the process with no way to recover via this API at all. The handlers are
            // already unsubscribed either way, so "not receiving" is the only state consistent with
            // reality regardless of whether the underlying stop call itself succeeded.
            _isReceiving = false;
        }

        // ultracode audit finding #6: the RX-halting (entering-TX) side of the same transition.
        //
        // Round-21 finding (risk 6): previously unguarded -- a throwing ResetAgc() here propagated
        // out of StopReceivingAsync even though _isReceiving was already correctly latched false by
        // the finally above, and skipped Log.RxStopped below. This is also the direct fix for the
        // "risk 6b" call site in PlayWithPttAsync's entry (a bare `await StopReceivingAsync()`, inside
        // its own single-flight-guarded try but before PTT is ever touched) -- with ResetAgc() no
        // longer able to throw out of this method, that call site needs no separate guard of its own:
        // capture is genuinely stopped either way, no PTT/radio state is touched yet, and the
        // single-flight guard's own finally still releases normally.
        try
        {
            _decoder.ResetAgc();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "ResetAgc (RX stop)", ex));
        }

        // ui_transition_plan.md step 12 (Auto-save RX audio): deliberately placed HERE, after
        // StopCaptureAsync has been joined/timed-out above -- NOT right after the SamplesCaptured
        // unsubscribes. Auditor-caught (round 2 code-review): `-=` on a multicast delegate cannot
        // affect an invocation already in progress (MiniAudioEngine's own documented snapshot
        // semantics), so a straggler `_audioAutoSaveHandler` call can still be running after the
        // unsubscribe line above. Resetting the ring/arm before that straggler completes let it
        // re-seed the pre-roll ring sized against the OLD (about-to-change) sample rate, silently
        // undersizing it for the rest of the session. Placing this after the stop is actually
        // awaited closes that window -- this ONE hook, gated by the same "only on a real transition"
        // guard as the unsubscribes above, covers every AudioCaptureReset seam the plan doc calls for
        // (sample-rate change, capture-device change, TX pause/resume, file-decode entry) --
        // RequestSampleRateAsync/RequestCaptureDeviceAsync/PlayWithPttAsync/DecodeFromFileAsync all
        // funnel through this method before doing their own respective mutation, so a single call
        // site here is sufficient; no need to duplicate it at each of those 4 call sites separately.
        HandleAudioCaptureReset();

        SafeLog(() => Log.RxStopped(_logger));
    }

    public Task StartRecordingAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isReceiving)
        {
            throw new InvalidOperationException("Cannot start recording while not receiving.");
        }

        lock (_recordingLock)
        {
            if (_recordingChunks is not null)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            // Restart-required-settings backlog item 4, round-4 finding R2: the other half of
            // RequestSampleRateAsync's own TOCTOU close -- refuse to start while a rate change is
            // actively being applied, rather than starting a recording whose header could end up
            // describing a rate the audio wasn't actually captured at.
            if (_sampleRateChangeInProgress)
            {
                throw new InvalidOperationException("Cannot start recording while a sample rate change is being applied.");
            }

            // Configurations-preset backlog, Phase 1 (2026-08-28): same TOCTOU close as the check
            // immediately above, for RequestCaptureDeviceAsync's own commit sequence.
            if (_captureDeviceChangeInProgress)
            {
                throw new InvalidOperationException("Cannot start recording while a capture device change is being applied.");
            }

            _recordingChunks = [];
            _recordingPath = path;
            _recordingSampleRate = _decoder.SampleRate;
            // Code-review finding: subscribing here, INSIDE the same lock acquisition that
            // publishes _recordingChunks, not after releasing it -- a concurrent
            // FinalizeRecordingAsync landing in the gap between unlock and a subscribe-after-unlock
            // could otherwise grab-and-null the just-published state before this ever subscribes,
            // making its own unsubscribe a no-op and leaving this subscription permanently
            // dangling (double-subscribed, duplicated audio, on the next StartRecordingAsync).
            _audioEngine.SamplesCaptured += _recordingHandler;
        }

        SafeLog(() => Log.RecordingStarted(_logger, path));
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FinalizeRecordingAsync();
    }

    /// <summary>Grabs and clears the in-progress recording's buffered chunks under
    /// <see cref="_recordingLock"/>, then concatenates and writes them OUTSIDE the lock so the audio
    /// drain thread is never blocked for the duration of the file write. A no-op if no recording is
    /// in progress -- both <see cref="StopRecordingAsync"/> and <see cref="DisposeAsync"/> call this,
    /// and an in-progress recording must be finalized before <see cref="DisposeAsync"/> returns or
    /// the whole in-RAM buffer is silently discarded with no file written.</summary>
    private async Task FinalizeRecordingAsync()
    {
        List<ReadOnlyMemory<float>>? chunks;
        string? path;
        int sampleRate;
        lock (_recordingLock)
        {
            chunks = _recordingChunks;
            path = _recordingPath;
            // Restart-required-settings backlog item 4: the rate CAPTURED at StartRecordingAsync
            // time, read here (still inside the lock) rather than at WavFile.Write below, after this
            // lock is released and a Task.Run hop -- see _recordingSampleRate's own doc comment for
            // the race this closes. Falls back to the live decoder rate only for the (unreachable in
            // practice, since _recordingSampleRate is always set alongside _recordingChunks)
            // chunks-is-not-null-but-field-somehow-null case.
            sampleRate = _recordingSampleRate ?? _decoder.SampleRate;
            _recordingChunks = null;
            _recordingPath = null;
            _recordingSampleRate = null;

            // Code-review finding: unsubscribing here, INSIDE the same lock acquisition that clears
            // _recordingChunks -- see StartRecordingAsync's own subscribe-inside-the-lock comment
            // for the dangling-subscription race this closes. A no-op if chunks is about to come
            // back null below (nothing was ever subscribed).
            if (chunks is not null)
            {
                _audioEngine.SamplesCaptured -= _recordingHandler;
            }
        }

        if (chunks is null)
        {
            return;
        }

        var totalLength = 0;
        foreach (var chunk in chunks)
        {
            totalLength += chunk.Length;
        }

        var combined = new float[totalLength];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Span.CopyTo(combined.AsSpan(offset));
            offset += chunk.Length;
        }

        await Task.Run(() => WavFile.Write(path!, combined, sampleRate)).ConfigureAwait(false);
        SafeLog(() => Log.RecordingSaved(_logger, path!, combined.Length));
    }

    public async Task DecodeFromFileAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Piece C2 single-flight guard, checked before anything else -- same shape/reasoning as
        // PlayWithPttAsync's own _transmitInFlight guard.
        if (Interlocked.CompareExchange(ref _fileDecodeInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A file decode is already in progress.");
        }

        try
        {
            // Cross-check against a concurrent transmit -- see PlayWithPttAsync's own mirrored check
            // for why this needs to be bidirectional, not just this direction.
            if (Volatile.Read(ref _transmitInFlight) != 0)
            {
                throw new InvalidOperationException("Cannot decode a file while a transmit or tune is in progress.");
            }

            // _autoDetectPaused lives in this session's own handler, not the decoder -- legacy's
            // equivalent pause (pDem->m_SyncMode = -1) lives INSIDE CSSTVDEM itself, so legacy file
            // playback is genuinely suppressed by it too (Sound.cpp:334's WaveFile.ReadWrite feeds
            // the same demod loop the pause affects). Reject rather than silently bypassing it.
            if (_autoDetectPaused)
            {
                throw new InvalidOperationException("Cannot decode a file while auto-detect is paused.");
            }

            var (samples, sampleRate) = await Task.Run(() => WavFile.Read(path), ct).ConfigureAwait(false);

            if (sampleRate != _decoder.SampleRate)
            {
                throw new InvalidOperationException(
                    $"File sample rate ({sampleRate} Hz) does not match the configured decode rate ({_decoder.SampleRate} Hz).");
            }

            if (samples.Length == 0)
            {
                throw new InvalidOperationException("File contains no audio samples.");
            }

            // Round-2 plan-review blocker fix: acquire _rxTransitionGate directly and use the PRIVATE
            // *Locked variants, not the public StartReceivingAsync/StopReceivingAsync pair -- the
            // public StopReceivingAsync has a real early-return-without-detaching path (its own
            // rxTransitionGate wait can time out), so calling it and assuming detachment would race
            // the live drain thread's PushSamples calls against this method's own, tripping
            // RestartableSstvDecoder's _pushActive CAS. Holding the gate for the WHOLE decode
            // serializes this against every other RX transition by construction.
            if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
            {
                throw new TimeoutException("Timed out waiting to start file decode -- a concurrent RX transition did not finish in time.");
            }

            // Restart-required-settings backlog item 4, round-4 finding R1: re-check the rate AGAIN
            // now that the gate is actually held -- the check above ran BEFORE acquiring it, so a
            // RequestSampleRateAsync call that committed a new rate while this one waited for the
            // gate would otherwise go undetected here, and the file's samples would be pushed at the
            // wrong rate. Previously fine when the rate was immutable; not anymore.
            if (sampleRate != _decoder.SampleRate)
            {
                _rxTransitionGate.Release();
                throw new InvalidOperationException(
                    $"File sample rate ({sampleRate} Hz) does not match the configured decode rate ({_decoder.SampleRate} Hz) -- the decode rate changed while waiting to start.");
            }

            var wasReceiving = _isReceiving;
            try
            {
                await StopReceivingLockedAsync().ConfigureAwait(false);

                // Round-2 plan-review blocker fix: reuse the SAME DI-singleton _decoder rather than a
                // "fresh instance" (not implementable -- ReceivedImageBuffer/ReceiveHistoryRecorder
                // subscribe directly to this singleton, and RestartableSstvDecoder has no public
                // force-restart hook). StopReceivingLockedAsync above already called _decoder.ResetAgc()
                // as part of its own RX-stop transition; RequestAbandonReception here clears any
                // in-progress reception state before the first file-sourced chunk arrives. Residual
                // AGC/filter-delay-line carry-over across this seam is legacy-faithful, not a
                // compromise -- Sound.cpp:334-364 swaps only the buffer source on the same CSSTVDEM
                // instance, so its own state runs straight through the identical seam.
                _decoder.RequestAbandonReception();

                // fsk_cwid.md §8.2: a NESTED try/finally around the decode loop, not a plain
                // sequential call after it -- a plain call would never run for a cancelled/faulted
                // decode (control would jump straight past it to the OUTER finally below). Nesting it
                // HERE, inside the existing inner try (not before it, and not moved into the outer
                // finally alongside StartReceivingLockedAsync) means that if FlushOrDropCwArmForEndOfFile
                // itself throws, the outer finally's StartReceivingLockedAsync/_rxTransitionGate.Release()
                // still run -- see that method's own doc comment.
                var completedNormally = false;
                try
                {
                    // Runs on a background thread -- PushSamples/decode events are synchronous, so
                    // without this the whole loop would run on whatever thread called this method (the
                    // UI thread, in practice) and block it for the file's full decode duration.
                    await Task.Run(
                        () =>
                        {
                            const int ChunkSize = 4096;
                            for (var chunkOffset = 0; chunkOffset < samples.Length; chunkOffset += ChunkSize)
                            {
                                ct.ThrowIfCancellationRequested();
                                ObjectDisposedException.ThrowIf(_disposed, this);

                                var length = Math.Min(ChunkSize, samples.Length - chunkOffset);
                                var chunk = new ReadOnlyMemory<float>(samples, chunkOffset, length);

                                // Same three fan-out targets live capture feeds (matching legacy's own
                                // fftIN.CollectFFT reading from the same buffer WaveFile.ReadWrite
                                // fills, Sound.cpp:368) -- reusing these exact delegate instances, not
                                // reimplementing their isolation logic. _decoder.PushSamples is
                                // deliberately NOT wrapped here (unlike the live _decoderHandler) -- a
                                // decode failure on this explicit, single-shot, user-initiated call
                                // should propagate, not be silently swallowed the way an ambient
                                // hot-path capture callback's failure is.
                                PushSamplesToDecoder(chunk);
                                _waterfallHandler(chunk);
                                _levelMeterHandler(chunk);

                                // fsk_cwid.md §8.2: "re-decode a recording and get its CW ID" is an
                                // explicit goal -- UNLIKE _audioAutoSaveHandler, which is deliberately
                                // excluded from this loop (the file itself is already the audio), the
                                // CW arm needs every chunk to compute its own capture window the same
                                // way live capture does.
                                _cwIdHandler(chunk);
                            }
                        },
                        ct).ConfigureAwait(false);
                    completedNormally = true;
                }
                finally
                {
                    FlushOrDropCwArmForEndOfFile(completedNormally);
                }
            }
            finally
            {
                try
                {
                    if (wasReceiving)
                    {
                        await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _rxTransitionGate.Release();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _fileDecodeInFlight, 0);
        }
    }

    /// <summary>1x1 placeholder for the (unlikely in practice) case where the self-test's own decoder
    /// never fired a single <see cref="ISstvDecoder.LineDecoded"/> event -- same shape as
    /// <c>ReceivedImageBuffer</c>'s own <c>EmptyImage</c>, a distinct instance since that class isn't
    /// reachable from here (this self-test never touches it, by design).</summary>
    private static readonly IImageSource EmptySelfTestImage = new ArrayImageSource(1, 1, [new Rgb24(0, 0, 0)]);

    /// <inheritdoc/>
    public async Task<LoopbackSelfTestResult> RunLoopbackSelfTestAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref _loopbackSelfTestInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A loopback self-test is already in progress.");
        }

        try
        {
            if (Volatile.Read(ref _transmitInFlight) != 0)
            {
                throw new InvalidOperationException("Cannot run a loopback self-test while a transmit or tune is in progress.");
            }

            // sampleRate comes from _encoder.SampleRate -- the encoder is what actually generates the
            // audio this decoder consumes. Restart-required-settings backlog item 4 (2026-08-27):
            // SampleRate is genuinely live now, not restart-only -- the real protection against a
            // mid-self-test rate change is the BeginTransmission/EndTransmission bracket below (round-4
            // plan-review finding B1: this is a SECOND, independent call site into the encoder that
            // needs the same bracket TransmitAsync's own TX path uses -- easy to miss since it doesn't
            // go through PlayWithPttAsync at all). Only the decoder BEHAVIOR settings need a fresh read
            // (DemodType/senseLevel/etc. have no such encoder-side counterpart to drift against).
            // Deliberately NOT ResolveTransmitSettingsAsync -- that resolves the REAL persisted
            // StationId/TxSampleRateOffsetHz, both of which this self-test must never use (see this
            // method's own interface doc comment for why).
            var encoderReconfig = _encoder as ISstvEncoderReconfiguration;
            encoderReconfig?.BeginTransmission();
            try
            {
                var sampleRate = _encoder.SampleRate;
                var appSettings = await Task.Run(() => _settingsStore.LoadAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);

                // Code-review round-1 finding: unguarded GetSection call here would let a corrupt/
                // version-skewed "SstvDecoder" section (e.g. a hand-edited settings.json) permanently
                // break the self-test with a generic "failed" error, while live RX silently falls back to
                // defaults via this SAME try/catch/fallback shape (Program.CreateSstvDecoder). Matching
                // that established pattern here instead of leaving this the one unguarded read.
                SstvDecoderSettings decoderSettings;
                try
                {
                    decoderSettings = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
                        ?? new SstvDecoderSettings();
                }
                catch (JsonException ex)
                {
                    SafeLog(() => Log.SettingsSectionReadFailed(_logger, SstvDecoderSettings.SectionKey, ex));
                    decoderSettings = new SstvDecoderSettings();
                }

                var resolved = decoderSettings.Resolve();

                // Fresh, throwaway instance -- see this method's own interface doc comment for why this
                // is the whole design (round-5 plan-review): never the shared _decoder, so nothing here
                // needs to serialize against _rxTransitionGate or touch ReceivedImageBuffer/
                // ReceiveHistoryRecorder/the live RX pane at all.
                using var decoder = new AnalogFmSstvDecoder(
                    sampleRate: sampleRate,
                    afcEnabled: resolved.AfcEnabled,
                    syncRestartEnabled: resolved.SyncRestartEnabled,
                    autoSyncEnabled: resolved.AutoSyncEnabled,
                    autoStopEnabled: resolved.AutoStopEnabled,
                    autoSlantEnabled: resolved.AutoSlantEnabled,
                    senseLevel: resolved.SenseLevel,
                    demodType: resolved.DemodType,
                    rxBpfPreset: resolved.RxBpfPreset,
                    rxBufferMode: resolved.RxBufferMode,
                    pllVcoGain: resolved.PllVcoGain,
                    pllLoopOrder: resolved.PllLoopOrder,
                    pllLoopCutoffHz: resolved.PllLoopCutoffHz,
                    pllOutputOrder: resolved.PllOutputOrder,
                    pllOutputCutoffHz: resolved.PllOutputCutoffHz,
                    zeroCrossingSmoothingMode: resolved.ZeroCrossingSmoothingMode,
                    zeroCrossingOutputOrder: resolved.ZeroCrossingOutputOrder,
                    zeroCrossingOutputCutoffHz: resolved.ZeroCrossingOutputCutoffHz,
                    zeroCrossingSmoothingFrequencyHz: resolved.ZeroCrossingSmoothingFrequencyHz);

                ArrayImageSource? lastImage = null;
                int? previousLine = null;
                int? observedStep = null;
                var lastLine = -1;
                string? detectedModeId = null;
                var decodeRestarted = false;

                // Learns the scanline step from the first two events rather than assuming 1 -- paired-line
                // families (PD/MP/RM8/RM12) advance 2 rows per event and RowsPerTransmissionLine isn't on
                // the public SstvModeDefinition. Same technique ReceivedImageBuffer/ReceiveHistoryRecorder
                // already use (round-5 plan-review finding).
                void OnLineDecoded(DecodedImageUpdate update)
                {
                    // Copy, not the live update.Image reference -- see ArrayImageSource.CopyFrom's own
                    // doc comment.
                    lastImage = ArrayImageSource.CopyFrom(update.Image);

                    if (previousLine is int previous && observedStep is null)
                    {
                        observedStep = update.Line - previous;
                    }

                    previousLine = update.Line;
                    lastLine = update.Line;
                }

                void OnModeDetected(SstvModeDefinition detected) => detectedModeId = detected.Id;

                // Code-review round-1 finding: DecodeRestarted has TWO reachable raise sites during a
                // self-test, not one -- Auto Stop AND a mid-reception re-lock (a stronger/cleaner sync
                // found while already locked, gated by syncRestartEnabled, which defaults true). With
                // AutoStopEnabled defaulting FALSE, under stock settings every DecodeRestarted here is
                // actually the re-lock case -- a real decode-fidelity failure, the exact thing this
                // feature exists to catch. Only attribute it to Auto Stop when that setting is actually
                // on; the event alone can't distinguish the two cases even then, so the outcome's own
                // display text is deliberately hedged ("may be"), not asserted as fact.
                void OnDecodeRestarted(SstvModeDefinition abandoned) => decodeRestarted = true;

                decoder.LineDecoded += OnLineDecoded;
                decoder.ModeDetected += OnModeDetected;
                decoder.DecodeRestarted += OnDecodeRestarted;

                try
                {
                    // Runs on a background thread -- encode+decode events are synchronous, so without this
                    // the whole loop would run on whatever thread called this method (the UI thread, in
                    // practice) and block it for the full self-test duration. Same reasoning as
                    // DecodeFromFileAsync's own push loop.
                    await Task.Run(
                        async () =>
                        {
                            // T1-3 (production_audit.md): EncodeBatchedAsync directly -- no local
                            // re-batching buffer needed at all, since decoder.PushSamples already
                            // accepts ReadOnlyMemory<float> and this loop has no per-sample transform
                            // to apply (unlike PumpToPlaybackAsync's own gain multiply). ChunkSize
                            // itself is kept only for the trailing-silence padding loop below, no
                            // longer for re-batching the encoder's own output.
                            const int ChunkSize = 4096;
                            //
                            // sampleRateOffsetHz: 0.0 and stationId: null (StationIdTransmitOptions.None) --
                            // both deliberate, see this method's own interface doc comment. TX BPF/LPF
                            // (Options stub backlog item 3) left at their legacy-matching defaults for
                            // the same reason -- this self-test is a minimal, deterministic decode
                            // check, not a faithful mirror of the user's real TX settings.
                            await foreach (var chunk in _encoder.EncodeBatchedAsync(mode, image, stationId: null, sampleRateOffsetHz: 0.0, ct: ct)
                                .WithCancellation(ct).ConfigureAwait(false))
                            {
                                decoder.PushSamples(chunk);
                            }

                            // Trailing silence: TryProcessBuffer can leave the final scanline undecoded
                            // without it -- the sync-anchor correction shifts consumed-vs-received sample
                            // counts, so exact-length encoded audio can fall short on the last line (round-5
                            // plan-review finding). One full transmission line covers any anchor offset; the
                            // sampleRate/2 floor keeps short-line modes sane.
                            var padSamples = Math.Max((int)Math.Ceiling(mode.LineDurationMs / 1000.0 * sampleRate), sampleRate / 2);
                            var silence = new float[Math.Min(padSamples, ChunkSize)];
                            for (var remaining = padSamples; remaining > 0;)
                            {
                                var n = Math.Min(remaining, silence.Length);
                                decoder.PushSamples(silence.AsMemory(0, n));
                                remaining -= n;
                            }
                        },
                        ct).ConfigureAwait(false);
                }
                finally
                {
                    decoder.LineDecoded -= OnLineDecoded;
                    decoder.ModeDetected -= OnModeDetected;
                    decoder.DecodeRestarted -= OnDecodeRestarted;
                }

                // Code-review round-1 finding: only attribute a restart to Auto Stop when that setting is
                // actually enabled -- see OnDecodeRestarted's own comment above for why a restart with
                // Auto Stop OFF (the default) is really a decode-fidelity failure, not expected behavior.
                var outcome = decodeRestarted && resolved.AutoStopEnabled
                    ? LoopbackSelfTestOutcome.AbandonedByAutoStop
                    : observedStep is int step && step > 0 && lastLine + step >= mode.ImageHeight
                        ? LoopbackSelfTestOutcome.Completed
                        : LoopbackSelfTestOutcome.Incomplete;

                return new LoopbackSelfTestResult(lastImage ?? EmptySelfTestImage, detectedModeId, outcome);
            }
            finally
            {
                encoderReconfig?.EndTransmission();
            }
        }
        finally
        {
            Volatile.Write(ref _loopbackSelfTestInFlight, 0);
        }
    }

    public async Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        Log.TxStarting(_logger, mode.Id, image.Width, image.Height);

        // Freeze the encoder before rate-dependent footer preparation, including asynchronous
        // file reads. Keep the lease until playback drains, and release it on every failure.
        var encoderReconfig = _encoder as ISstvEncoderReconfiguration;
        encoderReconfig?.BeginTransmission();
        try
        {
            var (stationId, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz) = await ResolveTransmitSettingsAsync(resolveSoundFile: true, ct).ConfigureAwait(false);
            // Reuse the exact resolved options for estimation and encoding. Estimation traverses
            // the scanlines, so keep that work off the caller's UI thread.
            var totalSamplesEstimate = await Task.Run(() => _encoder.EstimateSampleCount(mode, image, stationId, sampleRateOffsetHz), ct).ConfigureAwait(false);
            await PlayWithPttAsync(_encoder.EncodeBatchedAsync(mode, image, stationId, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz, ct), _encoder.SampleRate, ct, totalSamplesEstimate: totalSamplesEstimate).ConfigureAwait(false);
        }
        finally
        {
            encoderReconfig?.EndTransmission();
        }
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
        // resolveSoundFile: false -- this is a read-only preview (TxControlsPaneViewModel's
        // Identification summary, refreshed on every Options-dialog close), never a real
        // transmission. A full file-read/resample/IIR-filter pass here would run on every dialog
        // close for no reason -- StationIdTransmitOptions.SoundFileIdEnabled (a cheap, I/O-free flag)
        // is what this preview should read instead of SoundFileSamples for a "configured" summary.
        => (await ResolveTransmitSettingsAsync(resolveSoundFile: false, ct).ConfigureAwait(false)).StationId;

    /// <summary>Shared settings resolution for <see cref="TransmitAsync"/> and the read-only preview
    /// <see cref="GetStationIdTransmitOptionsAsync"/> exposes -- one <see cref="_settingsStore"/>
    /// load, not two, so both the station-ID text and the TX sample-rate offset always agree with
    /// each other and with whatever a single settings.json snapshot actually held (round-2 Clock
    /// calibration plan-review finding: an independent second load on this PTT-adjacent path would
    /// re-introduce the same unbounded-hang class <see cref="_cleanupTimeout"/> below already
    /// guards against once).</summary>
    /// <summary>Cap on the raw <c>.MMV</c> sound-file station-ID FILE READ (`docs/plans/sound-file-id-plan.md`'s
    /// "File I/O / DSP layering" -- legacy's own `OutputMMV` has no read-size cap at all, but an
    /// arbitrary user-picked file could be a mispicked multi-gigabyte file; comfortably larger than
    /// any real multi-second mono 16-bit ID clip at any supported rate, a safety-only divergence, not
    /// a fidelity change). Code-review correction: this bounds the FILE, not the resulting `float[]`
    /// allocation -- a file declaring a low source rate (e.g. 6000Hz) resampled to a high TX rate
    /// (e.g. 48000Hz) expands roughly 8x, so a file right at this cap can still produce an allocation
    /// well over 500MB. Accepted as-is (matches legacy's own equally-unbounded worst case in kind,
    /// just not degree) rather than adding a second cap on the output sample count -- a real user
    /// would never configure a multi-hundred-MB station-ID clip in practice.</summary>
    // Keep in sync with the "32 MB" figure hardcoded into
    // en.json's Options.Identification.SoundFile.Help / Options.Radio.SoundFileId.Error.TooLarge --
    // both are hand-maintained UI-facing text describing this constant, not derived from it.
    private const long MaxSoundFileIdBytes = 32 * 1024 * 1024;

    private async Task<(StationIdTransmitOptions StationId, double SampleRateOffsetHz, bool TxBpfEnabled, int TxBpfTapCount, bool TxLpfEnabled, double TxLpfFrequencyHz)> ResolveTransmitSettingsAsync(bool resolveSoundFile, CancellationToken ct)
    {
        // Round-15 finding (discovered while testing finding 3, not itself in the auditor's report):
        // same unbounded-external-read shape as ResolveDeviceAsync/GetTxVolumePercentAsync/
        // LoadAudioSettingsAsync inside PlayWithPttAsync -- but reached from TransmitAsync's own
        // preamble, BEFORE PlayWithPttAsync (and its _transmitInFlight guard) is ever entered. Lower
        // severity than finding 3 (a hang here does NOT strand _transmitInFlight, since it hasn't
        // been acquired yet -- a concurrent second TransmitAsync/TuneAsync call is unaffected), but
        // still a real unbounded wait on a settings read (e.g. a config file on a hung network mount)
        // with no caller-visible way to bound it on the TuneAsync-with-no-ct production path. Same
        // fix, same reasoning.
        //
        // Round-18 finding 4 (round-17's own deferral (a), now fixed): WaitAsync alone cannot bound
        // this -- JsonSettingsStore.LoadAsync does blocking File.Exists/File.OpenRead BEFORE its own
        // first `await` (this file's own EstimateSampleCount comment already states this exact fact,
        // just never connected it to the WaitAsync bounds above), so on a hung network-mounted
        // settings path the call doesn't even return a genuinely-pending Task for WaitAsync to race --
        // the calling thread blocks synchronously before WaitAsync is ever reached. Task.Run offloads
        // that synchronous prefix onto a pool thread, so the awaited Task really is pending
        // immediately and WaitAsync's bound becomes real. Cost: a wedged mount leaks one abandoned
        // pool thread per call using this pattern -- bounded and slow-growing in practice, since this
        // path (unlike PlayWithPttAsync's own) isn't behind _transmitInFlight.
        var appSettings = await Task.Run(() => _settingsStore.LoadAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
        var stationIdSettings = appSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings)
            ?? new StationIdSettings();
        var audioSettings = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();

        // Main.cpp:6969's !sys.m_CWIDText.IsEmpty() -- checked on the RAW (pre-macro) text, matching
        // legacy exactly (a macro token that resolves to empty would still fire in legacy, since the
        // gate never re-checks after MacroText expansion).
        var cwEnabled = stationIdSettings.CwIdMode == CwIdMode.Cw && !string.IsNullOrEmpty(stationIdSettings.CwText);

        // Tier B audit finding (Area 3, matches round 16's TxVolumePercent / round 18's TuneAsync
        // precedent): this doc comment's own "settings-boundary validation lives here" claim used to
        // check only the lower bound (`> 0`). A corrupted/hand-edited settings.json could still reach
        // the transmitter WITH PTT KEYED: CwWpm=1 -> MillisecondsPerDotFromWpm(1)=1110ms/dot, minutes
        // of keyed CW after every image with none of TuneAsync's own duration backstop on this path;
        // CwToneFrequencyHz>=24000 (this file's encoder Nyquist) aliases to an arbitrary on-air tone,
        // and +Infinity would reach Math.Sin as NaN samples (NaN itself already falls through `> 0`,
        // since a NaN comparison is always false). Bounded to the Options dialog's own product-decided
        // legitimate range (OptionsWindowView.axaml's CwWpm/CwToneFrequencyHz NumericUpDown Minimum/
        // Maximum) rather than an arbitrary wider one -- anything outside it is definitionally the
        // "corrupted settings" case this method's own doc comment already promises to fall back from.
        var wpm = stationIdSettings.CwWpm is >= 10 and <= 50
            ? stationIdSettings.CwWpm.Value
            : StationIdSettings.DefaultCwWpm;
        var toneFrequencyHz = stationIdSettings.CwToneFrequencyHz is >= 100 and <= 3000
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

        // Main.cpp:7021-7025: sys.m_CWID is a single-value tri-state -- `cwEnabled` above already
        // checks `== CwIdMode.Cw` specifically (not merely `!= Off`), so this check is naturally
        // mutually exclusive with it already, no explicit if/else-if restructure needed.
        var soundFileIdEnabled = stationIdSettings.CwIdMode == CwIdMode.SoundFile && !string.IsNullOrEmpty(stationIdSettings.SoundFileMmvPath);

        // Cheap, I/O-free even when resolveSoundFile is false -- see SoundFileIdEnabled's own doc
        // comment for why the read-only preview path needs this but must NOT trigger the real file
        // read below.
        ReadOnlyMemory<float>? soundFileSamples = null;
        if (resolveSoundFile && soundFileIdEnabled)
        {
            var mmvPath = stationIdSettings.SoundFileMmvPath!;
            // The transmission lease is already held. Capture the rate before scheduling so
            // even an abandoned worker cannot read a later encoder configuration.
            var targetSampleRate = _encoder.SampleRate;
            var resolver = SoundFileSamplesResolverForTests ?? TryResolveSoundFileSamples;
            var resolveTask = Task.Run(() => resolver(mmvPath, targetSampleRate), ct);
            float[]? resolvedSamples;
            try
            {
                resolvedSamples = await resolveTask.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _ = resolveTask.ContinueWith(
                    task => SafeLog(() => Log.SoundFileIdReadFailed(_logger, mmvPath, task.Exception!)),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }

            // Deliberately an `if`, NOT a ternary ("soundFileSamples = resolvedSamples is null ? null
            // : new ReadOnlyMemory<float>(resolvedSamples)") -- confirmed by a real failing test, not
            // a hypothetical: ReadOnlyMemory<float> has an implicit operator FROM float[] (including a
            // null array), so the C# conditional-expression common-type algorithm resolves the ternary
            // through THAT conversion path even for the bare `null` literal branch, producing
            // `ReadOnlyMemory<float>` (non-nullable, default/empty) as the ternary's type -- which then
            // silently boxes into HasValue=true, Length=0 on assignment to this ReadOnlyMemory<float>?
            // field, not the "unconfigured" null this method's whole contract depends on. Leaving
            // soundFileSamples at its already-null default and only assigning in the non-null case
            // sidesteps the whole conversion-path ambiguity.
            if (resolvedSamples is not null)
            {
                soundFileSamples = new ReadOnlyMemory<float>(resolvedSamples);
            }
        }

        var stationId = new StationIdTransmitOptions
        {
            CwEnabled = cwEnabled,
            CwResolvedText = cwResolvedText,
            CwToneFrequencyHz = toneFrequencyHz,
            CwWpm = wpm,
            FskIdEnabled = stationIdSettings.FskIdTxEnabled,
            Callsign = operatorSettings.Callsign ?? string.Empty,
            NrRstText = nrRstEnabled ? stationIdSettings.NrRstText : null,
            SoundFileIdEnabled = soundFileIdEnabled,
            SoundFileSamples = soundFileSamples,
        };

        // Settings-boundary validation, same shape/precedent as CwToneFrequencyHz above: a
        // corrupted/hand-edited settings.json outside legacy's own accepted +/-1500 Hz manual range
        // (Option.cpp:1142-1146) falls back to 0.0 (no correction) rather than reaching the encoder.
        // The `is >= x and <= y` pattern is NaN-safe by construction (a NaN comparison is always
        // false in a range pattern, same as the `>` bug CwToneFrequencyHz's own history already
        // found and fixed) -- catches the Clock calibration round-2 plan-review's NaN/Infinity
        // finding without a separate double.IsFinite check here.
        var sampleRateOffsetHz = audioSettings.TxSampleRateOffsetHz is >= -1500.0 and <= 1500.0
            ? audioSettings.TxSampleRateOffsetHz
            : 0.0;

        // Options stub backlog item 3 (docs/plans/options-stub-item3-tx-bpf-lpf-plan.md):
        // TxBpfEnabled/TxLpfEnabled default true/false when absent (legacy's own real CSSTVMOD ctor
        // defaults, sstv.cpp:2759-2760). TxBpfTapCount/TxLpfFrequencyHz clamped to legacy's own real
        // Save-handler ranges (Option.cpp:452-459) -- tap count [2,512] rounded to the nearest EVEN
        // value (see TxOutputBandpassFilter's own doc comment for why odd is legacy UB), LPF
        // frequency [100,3000] -- same settings-boundary-validation-lives-here precedent as
        // sampleRateOffsetHz immediately above, not a separate Resolve() method (this section has
        // none).
        var txBpfEnabled = audioSettings.TxBpfEnabled ?? true;
        // TxOutputBandpassFilter's own ClampTapCount is internal to ScanlineStudio.Core.Sstv, not
        // visible here -- this clamp is trivial enough to duplicate independently at this layer,
        // same precedent as SstvDecoderSettings.Resolve()'s own clamps duplicating decoder-side ones.
        var txBpfTapCount = audioSettings.TxBpfTapCount is { } tap ? Math.Clamp(tap, 2, 512) : 24;
        txBpfTapCount = txBpfTapCount % 2 == 0 ? txBpfTapCount : txBpfTapCount - 1;
        var txLpfEnabled = audioSettings.TxLpfEnabled ?? false;
        var txLpfFrequencyHz = audioSettings.TxLpfFrequencyHz is >= 100.0 and <= 3000.0
            ? audioSettings.TxLpfFrequencyHz.Value
            : 2000.0;

        return (stationId, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz);
    }

    /// <summary>Shared parse core for both <see cref="TryResolveSoundFileSamples"/> (the real
    /// TX-time resolution) and <see cref="ValidateStationIdSoundFileAsync"/> (the Options dialog's
    /// pre-Save validation, ui_transition_plan.md step 8/T2-2) -- exists/size-cap/header-parse are
    /// identical for both callers; only what happens to the parsed payload differs (resample for TX,
    /// just a duration calc for validation). Never throws -- see this class's own doc comment on the
    /// original single-purpose method this was extracted from for the exact exception-widening
    /// history.</summary>
    private readonly record struct SoundFileParseOutcome(byte[]? FileBytes, MmvSoundFile.Header Header, SoundFileIdValidationFailure Failure);

    private SoundFileParseOutcome TryParseSoundFile(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                Log.SoundFileIdMissing(_logger, path);
                return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.FileNotFound);
            }

            if (fileInfo.Length > MaxSoundFileIdBytes)
            {
                Log.SoundFileIdTooLarge(_logger, path, MaxSoundFileIdBytes);
                return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.FileTooLarge);
            }

            var fileBytes = File.ReadAllBytes(path);
            var header = MmvSoundFile.ParseHeader(fileBytes);
            if (header is null)
            {
                Log.SoundFileIdUnplayableHeader(_logger, path);
                return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.UnplayableHeader);
            }

            return new SoundFileParseOutcome(fileBytes, header.Value, SoundFileIdValidationFailure.None);
        }
        // Code-review finding: the ORIGINAL catch clause covered only IOException/
        // UnauthorizedAccessException, and only around the FileInfo/ReadAllBytes calls -- leaving
        // MmvSoundFile.Resample's own call outside any try at all (a real crash bug, since fixed,
        // used to be reachable through it) and missing exception types a free-text path TextBox can
        // realistically produce (e.g. a trailing-space/invalid-character path throwing
        // ArgumentException on some platforms). Widened to cover the whole method body and every
        // exception type a bad user-supplied path or file could plausibly throw -- this method's own
        // contract ("never throws out of this method," matching legacy's own unconfigured-sound-file
        // silent-no-op behavior) must hold for EVERY failure mode, not just I/O ones.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.SoundFileIdReadFailed(_logger, path, ex);
            return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.ReadError);
        }
    }

    /// <summary>Reads, parses, and resamples a sound-file station-ID file (`docs/plans/sound-file-id-plan.md`)
    /// to <paramref name="targetSampleRateHz"/>. Returns <see langword="null"/> for any failure --
    /// missing/unreadable/oversized file, or an unplayable header (<see cref="MmvSoundFile.ParseHeader"/>'s
    /// own doc comment) -- matching legacy's own "unconfigured sound file -&gt; silent no-op"
    /// behavior; never throws out of this method. The actual parsing/resampling math is pure and
    /// lives in <c>ScanlineStudio.Core.Sstv.MmvSoundFile</c> -- this method owns only the file I/O
    /// boundary, per this port's established "Application does I/O, Core.Sstv does pure DSP"
    /// split.</summary>
    private float[]? TryResolveSoundFileSamples(string path, int targetSampleRateHz)
    {
        var outcome = TryParseSoundFile(path);
        if (outcome.FileBytes is null)
        {
            return null;
        }

        try
        {
            var payload = outcome.FileBytes.AsSpan(outcome.Header.PayloadOffset);
            return MmvSoundFile.Resample(payload, outcome.Header.SampleRateIndex, targetSampleRateHz);
        }
        // Code-review finding (step 8 refactor): TryParseSoundFile's own catch only covers ITS OWN
        // body (exists/size-cap/File.ReadAllBytes/ParseHeader) -- Resample runs after that method
        // returns, so it needs this same widened exception set repeated here, or the real crash bug
        // this catch was originally added to fix (Resample's own call outside any try at all) comes
        // back for the TX path specifically.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.SoundFileIdReadFailed(_logger, path, ex);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<SoundFileIdValidationResult> ValidateStationIdSoundFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                var outcome = TryParseSoundFile(path);
                if (outcome.FileBytes is null)
                {
                    return SoundFileIdValidationResult.Fail(outcome.Failure);
                }

                // Duration from the file's own ORIGINAL sample rate -- see this method's own
                // interface doc comment for why this is deliberately NOT resampled to any TX target
                // rate.
                var payloadBytes = outcome.FileBytes.Length - outcome.Header.PayloadOffset;
                var sampleCount = payloadBytes / 2; // MmvSoundFile's own 16-bit-mono trailing-odd-byte truncation.
                var sourceRateHz = MmvSoundFile.GetSourceSampleRateHz(outcome.Header.SampleRateIndex);
                return SoundFileIdValidationResult.Ok(sampleCount / (double)sourceRateHz);
            }, ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
        }
        // Code-review finding: TryParseSoundFile's File.ReadAllBytes can hang indefinitely against a
        // wedged network mount -- the TX-time caller of the same parse core already bounds this
        // (TransmitAsync's own WaitAsync(_cleanupTimeout, ct) around TryResolveSoundFileSamples).
        // Without the same bound here, a hung Options-dialog validation would hold OptionsWindowViewModel's
        // own _saveGate open indefinitely, wedging every subsequent Save/Apply/Connect. This method's
        // own "never throws" contract still holds -- a timeout maps to a real, if generic, failure
        // reason rather than propagating.
        catch (TimeoutException)
        {
            return SoundFileIdValidationResult.Fail(SoundFileIdValidationFailure.ReadError);
        }
    }

    // Round-18 finding 3 (round-17's own deferral (b), now fixed): a generous backstop, not a UX
    // limit -- the default UI value is 5s and legitimate antenna-tuning use is on that order. Exists
    // only to bound the worst case of a corrupted/mistyped duration reaching TuneAsync below with no
    // caller-side cancellation available (see that method's own comment).
    private static readonly TimeSpan MaxTuneDuration = TimeSpan.FromMinutes(5);

    public Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default)
    {
        const int sampleRate = 48_000;

        // Round-18 finding 3: neither parameter was validated at all -- these are direct caller
        // arguments with a live caller that already catches and surfaces a failure
        // (RadioStatusViewModel's own RadioStatus.Error.TuneFailed), so this throws rather than
        // silently clamping -- the caller needs to know its own value was wrong, not have it
        // silently substituted. Checked and thrown BEFORE Log.TuneStarting/PlayWithPttAsync, so PTT is
        // never touched and _transmitInFlight is never taken on an invalid call.
        //
        // frequencyHz: NaN/infinity propagates through GenerateTone's Math.Sin into PumpToPlaybackAsync's
        // unclamped `sample * gain` as NaN samples enqueued to the playback device WITH PTT KEYED;
        // anything at or above Nyquist (half of sampleRate) aliases to arbitrary audible garbage.
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0 || frequencyHz >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, $"Must be finite and in (0, {sampleRate / 2.0}) Hz.");
        }

        // duration: GenerateTone's totalSamples = (long)(duration.TotalSeconds * sampleRate) accepts
        // any TimeSpan a caller can construct -- an absurd value (e.g. a unit-conversion bug upstream)
        // keys PTT for a correspondingly absurd duration, and the one production caller
        // (RadioStatusViewModel.TuneAsync) passes CancellationToken.None with no Stop command, so
        // there is no way to interrupt it short of process exit. Zero/negative is otherwise benign
        // (the pump loop just never runs) but still rejected -- a caller passing that almost certainly
        // has a bug worth surfacing, not a deliberate "key and immediately un-key" request.
        if (duration <= TimeSpan.Zero || duration > MaxTuneDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, $"Must be greater than zero and at most {MaxTuneDuration}.");
        }

        Log.TuneStarting(_logger, frequencyHz, duration);
        return PlayWithPttAsync(GenerateTone(frequencyHz, duration, sampleRate, ct), sampleRate, ct, leaveKeyedAfterCall: leaveKeyedAfterTune);
    }

    public async Task<int> GetTxVolumePercentAsync(CancellationToken ct = default)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);

        // Round-16 finding (this project's own history): unlike GetStationIdTransmitOptionsAsync's
        // own WPM/tone-frequency boundary validation (same threat model: a corrupted/hand-edited
        // settings.json), this value flows straight into PlayWithPttAsync's `gain` multiplier with
        // no range check at all -- an out-of-range value (e.g. a typo'd 10000, or a negative number)
        // would reach PumpToPlaybackAsync's unclamped `sample * gain` directly, hard-clipping the
        // transmitted audio into a square wave (real-world splatter risk on an actual transmitter) or
        // inverting phase. Clamped here so every reader gets a safe value regardless of what's on disk.
        return Math.Clamp(settings.TxVolumePercent ?? 100, 0, 100);
    }

    public async Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        // Clamped on write too, not just on read -- GetTxVolumePercentAsync's own clamp already
        // makes an out-of-range value on disk safe to READ, but leaving it unclamped here would
        // still let a bogus value silently reach disk via this API's own normal use (e.g. a UI
        // control with a bug, or a scripted settings import), for GetTxVolumePercentAsync to mask
        // again on every future read -- clamping at the write boundary keeps what's actually stored
        // consistent with what every reader promises.
        percent = Math.Clamp(percent, 0, 100);

        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                ?? new AudioDeviceSettings();
            return appSettings.WithSection(
                AudioDeviceSettings.SectionKey,
                current with { TxVolumePercent = percent },
                AudioSettingsJsonContext.Default.AudioDeviceSettings);
        }, ct).ConfigureAwait(false);

        // Write-through to the live field _before_ Log/return -- so a caller awaiting this method's
        // completion (e.g. the Options window's debounced Pwr-slider persist) is guaranteed
        // PumpToPlaybackAsync's NEXT chunk read already sees the new value, not just "eventually".
        // See _liveTxGain's own doc comment for why a live in-flight transmission needs this at all.
        _liveTxGain = percent / 100f;

        Log.TxVolumeSet(_logger, percent);
    }

    public async Task<bool> GetTxDeviceMutedAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
        if (device is null)
        {
            return false;
        }

        return await _deviceMuteQuery.IsDeviceMutedAsync(device, isCapture: false, ct).ConfigureAwait(false) ?? false;
    }

    /// <summary>Shared bounded-step budget reused across this class's various cleanup/safety-critical
    /// awaits (un-key, RX-resume-after-unlock, the round-14 key-command/StartPlaybackAsync bounds) --
    /// see each call site's own comment for why that particular step needs one.</summary>
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
    // force-un-keying itself. Originally sized against ScanlineStudio.Host/Program.cs's 10s total
    // host-teardown bound as this (3s) + the backstop un-key's own CleanupTimeout (5s) = 8s worst
    // case.
    //
    // Round-22 finding (nit), Tier B Area 5 update: that arithmetic is now stale AGAIN, and by more
    // than round 22's own correction accounted for -- round 16 added a 5s StopCapture watchdog inside
    // StopReceivingAsync (bringing the total to ~13s, round 22's own figure), and Tier B Area 3 then
    // added a SEPARATE 5s bounded wait of its own to StopReceivingAsync's own gate acquisition (see
    // _rxTransitionGate's doc comment) -- both sit in the SAME call, back to back, not overlapping.
    // Current real worst case: this wait (3s) + backstop un-key (5s) + StopReceivingAsync's own gate
    // wait (5s) + its StopCapture watchdog (5s) = ~18s, plus Waterfall.Dispose()/Decoder.Dispose()
    // (both unbounded, out of this chunk's scope), against Program.cs's own ~10s total host-teardown
    // bound -- and that 10s is itself shared with every OTHER singleton disposed before this one, not
    // reserved for this class alone. Still no PTT consequence: the safety-critical prefix (this wait +
    // the backstop un-key) is ordered first and totals 8s, still inside the 10s bound, and a timed-out
    // host teardown (Program.cs's own catch) logs and exits rather than killing mid-step -- so an
    // overrun only costs the LATER, non-safety-critical steps (RX stop, waterfall/decoder disposal),
    // not PTT-off. A future round sizing a NEW budget against either this comment's or round 22's own
    // superseded figures would still be misled -- ~18s, not ~13s, is the number to size against now.
    private static readonly TimeSpan InFlightKeyedTransmitWait = TimeSpan.FromSeconds(3);

    // Round-13 finding: EnqueueAllAsync's own "buffer full, wait 10ms, retry" loop (see its own
    // comment) had no bound at all -- a fully-wedged playback device (driver stall, device
    // removed mid-transmission -- the same "wedged output device" class StopPlaybackWithWatchdogAsync
    // already treats as a real failure mode) left the ring permanently full, `accepted` permanently
    // 0, and the loop spinning forever WITH PTT STILL KEYED. Round-14 nit: this only catches a device
    // that accepts literally ZERO samples per attempt -- a device accepting a nonzero trickle far
    // below real-time (a severe rate mismatch or a slow virtual/loopback sink) resets the stall timer
    // on every partial accept and is not caught by this budget at all. Accepted as out of this fix's
    // stated scope; a whole-transmission bound derived from totalSamplesEstimate would be the fix for
    // that broader case, not attempted here. TuneAsync's real production caller
    // (RadioStatusViewModel) passes no CancellationToken at all and has no Stop command, so on that
    // path there was no way to interrupt it short of process exit -- and round 12's own
    // _transmitInFlight guard means this hang now ALSO permanently blocks every subsequent
    // Transmit/Tune for the rest of the process, not just the stuck one. This is how long
    // EnqueueAllAsync waits with zero progress before concluding the device is wedged and aborting
    // (throwing, which routes through PlayWithPttAsync's own generic catch -> abnormalTermination ->
    // immediate urgent un-key, the same bounded recovery path every other failure on this method
    // already uses).
    private static readonly TimeSpan PlaybackStallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Shared PTT-guarantee shape for both <see cref="TransmitAsync"/> and <see cref="TuneAsync"/>:
    /// pauses capture (resumed afterward only if RX was already running), keys PTT, plays
    /// <paramref name="samples"/>, then un-keys PTT in a <c>finally</c> no matter how playback ends --
    /// unless <paramref name="leaveKeyedAfterCall"/> is set (see <see cref="TuneAsync"/>'s own doc
    /// comment for its one caller) or <see cref="SetPttLockAsync"/>'s lock is currently engaged, in
    /// which case the un-key/resume-RX steps are skipped -- <b>but only on a NORMAL (successful)
    /// completion</b>. A cancellation or fault (manual Stop TX, SWR auto-cutoff -- see
    /// <c>TxControlsPaneViewModel</c>) ALWAYS un-keys PTT, even if the lock was engaged: a safety
    /// cutoff/manual stop must never be overridable by "stay keyed" state (a real defect an audit pass
    /// caught and this fix closes -- the lock existing at all must never be able to defeat the SWR
    /// cutoff's whole reason for existing). <b>Doc-comment correction (round-7 finding):</b> the un-key
    /// COMMAND is unconditional, but clearing the lock/state flags it left behind is not, as of round
    /// 5's epoch guard in <c>UnkeyForCleanupAsync</c> -- if a CONCURRENT, NEWER key command completed
    /// during this call's own un-key attempt, the clear is deliberately skipped so it doesn't wipe
    /// that newer call's genuinely-still-keyed state (see <c>_pttKeyEpoch</c>'s own doc comment).
    /// "Force-clears the lock" was accurate before that fix; it is not unconditional anymore.
    ///
    /// Device resolution and the entry PTT-key now live INSIDE the guarded region (moved in during
    /// the same audit-fix pass) -- previously a device-resolution failure (e.g. no playback device
    /// configured) after RX had already been paused above left RX stopped forever, since the old
    /// shape's <c>try</c>/<c>finally</c> didn't start until after those calls.</summary>
    // T1-3 (production_audit.md): samples is IAsyncEnumerable<ReadOnlyMemory<float>> (batched), not
    // IAsyncEnumerable<float> -- verified this parameter has exactly ONE use in this method's whole
    // 844-line body (the pass-through to PumpToPlaybackAsync below), so this is a pure type-plumbing
    // change, not a logic change to anything PTT-safety-related in this method. Both real producers
    // (AnalogFmSstvEncoder.EncodeBatchedAsync via TransmitAsync, GenerateTone via TuneAsync) batch
    // now specifically so PumpToPlaybackAsync can consume batches directly -- see that method's own
    // comment for why flattening back to individual floats before this point would erase the entire
    // point of batching.
    private async Task PlayWithPttAsync(IAsyncEnumerable<ReadOnlyMemory<float>> samples, int sampleRate, CancellationToken ct, bool leaveKeyedAfterCall = false, long? totalSamplesEstimate = null)
    {
        // Round-12 finding: single-flight guard, checked before ANYTHING else -- no RX pause, no
        // device resolution, no PTT touched. See _transmitInFlight's own doc comment for the failure
        // this closes: a second overlapping PlayWithPttAsync call used to silently re-key and tear
        // down the FIRST call's own live playback session instead of being rejected outright.
        if (Interlocked.CompareExchange(ref _transmitInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A transmit or tune is already in progress.");
        }

        // Piece C2 cross-check: DecodeFromFileAsync's own entry checks _transmitInFlight the same
        // way, in the other direction -- without this, a file decode reads _isReceiving == false
        // (RX is genuinely paused for it) and this call would see wasReceiving == false, skip its
        // own RX-pause step below, and key PTT mid-re-decode. Released by CompareExchange back to 0
        // in this method's own finally below, same as _transmitInFlight itself.
        if (Volatile.Read(ref _fileDecodeInFlight) != 0)
        {
            Volatile.Write(ref _transmitInFlight, 0);
            throw new InvalidOperationException("Cannot transmit or tune while a file decode is in progress.");
        }

        // Code-review round-1 finding: the self-test's own entry checks _transmitInFlight (rejects a
        // self-test started mid-transmit), but nothing enforced the OTHER direction -- a self-test's
        // encode+decode is real CPU work competing with this live PTT-keyed playback pump, and its
        // own result dialog is a MODAL ShowDialog that would otherwise pop up mid-transmission and
        // block the user from reaching Stop TX. Same shape as the _fileDecodeInFlight check above.
        if (Volatile.Read(ref _loopbackSelfTestInFlight) != 0)
        {
            Volatile.Write(ref _transmitInFlight, 0);
            throw new InvalidOperationException("Cannot transmit or tune while a loopback self-test is in progress.");
        }

        try
        {
            var wasReceiving = _isReceiving;
            if (wasReceiving)
            {
                // Round-15 finding 3 flagged this call as having the identical "hang here strands
                // _transmitInFlight forever" exposure as the three awaits inside the guarded
                // try/finally below, but judged a naive WaitAsync bound here UNSAFE (this call sits
                // OUTSIDE the guarded region, so no finally would run its own cleanup) and deferred it,
                // pending a "paired change" (bound the wait AND force _isReceiving = false on the
                // timeout path). Round-16 finding: that paired fix exists and is now applied INSIDE
                // StopReceivingAsync itself (see its own comment) -- the handlers are already detached
                // before its own bounded stop attempt, so "not receiving" is truthfully the state on
                // a timeout regardless of whether the underlying native close ever finishes, closing
                // the exact gap round 15 correctly identified but didn't yet have a design for.
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

            // Round-26 finding, round-28 correction: declared out here, not alongside its own snapshot
            // assignment inside the try below -- the cleanup finally needs to read this, and a local
            // declared inside the try is not in scope there. Round-28 finding (nit): the ORIGINAL
            // safety argument for this placeholder ("every guard is prefixed with `pttKeyedOnRealRig &&`,
            // also false-by-default until the try reaches its own snapshot line") went stale the moment
            // round 27 moved pttKeyedOnRealRig's own assignment earlier (now near the top of the try,
            // before this field is ever touched) -- so it is generally NOT false-by-default anymore at
            // any point this placeholder could matter. Still safe, for a different reason: every
            // consumer requires pttKeyedOnRealRig true, which requires _pttLocked having been true at
            // some point, which requires _pttKeyEpoch >= 1 -- combined with the second epoch term below,
            // every interleaving still errs toward attempting the un-key, never toward wrongly skipping
            // one based on this specific placeholder value.
            var unkeyEpochAfterOwnKeyAttempt = 0;

            // Round-27 finding: same reason/shape as unkeyEpochAfterOwnKeyAttempt just above -- declared out here,
            // assigned inside the try below, once this call's own key phase (not just method entry) has
            // completed. See that assignment's own comment.
            var keyEpochAfterOwnKeyAttempt = 0;

            // Blocker 3: this call's own handle into the coordinator's keyed-slot registration.
            var attempt = new PttSafetyCoordinator.PttKeyAttempt();

            try
            {
                // Round-27 finding (risk): pttKeyedOnRealRig's own round-25 baseline used to read AND
                // ASSIGN from _pttLocked via pttLockedBeforeKeyDecision, both of which happen LATE -- just before
                // the rigIsRealAtKeyTime branch, AFTER the three bounded device/settings awaits below.
                // Correct for pttLockedBeforeKeyDecision's OWN purpose (deciding whether to double-key an
                // already-engaged lock, which must see the freshest possible state), but wrong for
                // baselining pttKeyedOnRealRig: a failure in any of the three awaits below (device not
                // found, no device configured, or the WaitAsync timeout itself -- all realistic)
                // reached the generic catch with pttKeyedOnRealRig still at its OUTER false default,
                // since the code never reached the late assignment -- even when a lock was ALREADY
                // engaged at this call's own true entry, reproducing verbatim the harm round 25 exists
                // to prevent (no Critical, no retry, just a Debug "no radio configured" line). Both the
                // read AND the assignment happen HERE instead, before those awaits -- the read into a
                // separate local from pttLockedBeforeKeyDecision (kept late, for its own purpose), the assignment
                // directly into pttKeyedOnRealRig so it survives a throw from any of the three awaits.
                // The rigIsRealAtKeyTime branch further down still unconditionally sets this true for
                // the case THIS call keys/re-keys the rig itself, a strict superset of this baseline.
                //
                // Round-28 finding (nit, flagged not fixed): moving this baseline earlier widened its
                // OWN false-positive window in the opposite direction -- if a lock is engaged here, then
                // during the three awaits below a confirmed un-key genuinely turns the rig off AND
                // RigId separately goes to "none" (RadioController.DisconnectAsync), this call never
                // keys (rigIsRealAtKeyTime false) but pttKeyedOnRealRig stays stuck true from this
                // stale baseline -- cleanup then attempts a doomed un-key against the null-object
                // backend and logs a false Critical on a rig already confirmed off elsewhere. Pre-round-
                // 27 this same window was instruction-scale (right up to the read); it is now up to
                // three bounded-await-widths. unkeyEpochAfterOwnKeyAttempt's own round-28 fix does not rescue this
                // -- that confirmed un-key's own epoch bump lands BEFORE unkeyEpochAfterOwnKeyAttempt's own (also
                // moved-later) read, so the pair sees no change and does not skip the doomed attempt.
                // Narrow (needs lock-engaged-then-confirmed-unlock-then-disconnect, all within these
                // three awaits) and requires SetPttLockAsync, which has zero production callers today --
                // not fixed this round; a real fix would need this baseline itself to be downgradeable
                // by a confirmed un-key observed since this exact line, not just guardable at the
                // consumer sites the way _pttUnkeyEpoch already is.
                //
                // Round-29 finding (nit): this used to read _pttLocked alone -- narrower than this
                // class's own equivalent "did this class believe something was keyed" test used at the
                // unlock-direction catch above (_pttLocked || _pttLeftKeyedByCall ||
                // _pttUnkeyFailedOnRealRig). Scenario: a prior TuneAsync(leaveKeyedAfterTune: true) left
                // the rig physically keyed (_pttLeftKeyedByCall = true, _pttLocked still false), then
                // RadioController.DisconnectAsync sets RigId to "none" without un-keying -- a new
                // transmit entering here baselined false, so its cleanup un-key logged Debug instead of
                // the Critical this baseline exists to guarantee. Not a leak either way
                // (_pttLeftKeyedByCall itself survives untouched, so DisposeAsync's own four-state
                // backstop still fires) -- same signal-quality class this whole baseline exists to
                // protect, one flag short. Widened to match the established belief test exactly.
                //
                // Round-30 finding (nit, considered and accepted, not changed): including
                // _pttUnkeyFailedOnRealRig here means that once ONE real un-key failure has latched it
                // and the operator then disconnects (RigId -> "none", where every un-key attempt throws
                // synchronously and so can never CONFIRM success to clear the flag -- see its own clear
                // sites), every SUBSEQUENT transmit's cleanup re-attempts an un-key against the
                // null-object backend, fails again, and re-emits a fresh Critical "PTT MAY STILL BE
                // KEYED" -- once per transmit, indefinitely, not just once. This is the signal-erosion
                // SHAPE rounds 4/19 added gates to prevent, but the underlying belief (a rig that failed
                // to un-key and is now unreachable) is genuinely still unresolved -- unlike the false
                // positives those two rounds actually fixed (a rig confirmed OFF, or never keyed at
                // all), there is no confirmed-safe state to fall silent about here. A real fix would need
                // to distinguish "the same still-latent failure repeating" from "a genuinely new event"
                // (e.g. de-duplicating by _pttUnkeyEpoch's own value at latch time), which trades a
                // repeated-but-true alarm for a real risk of under-warning if implemented wrong. Left as
                // a persistent, if repetitive, Critical -- the more conservative failure mode for a
                // possibly-still-transmitting rig.
                pttKeyedOnRealRig = _pttCoordinator.BelievesKeyed(_pttCoordinator.IsPttLocked);

                // Round-15 finding 3: these three awaits had no bound of their own either -- unlike
                // findings 1/2 (round 14), PTT is NOT yet keyed at this point, so a hang here is not
                // the leaked-keyed-transmitter class. It is still a real, severe bug: RX was already
                // paused above (StopReceivingAsync, outside this guarded region -- see that call's own
                // comment for why it is deliberately NOT given the same treatment here), and a hang in
                // ANY of these three permanently strands _transmitInFlight (round 12's single-flight
                // guard never releases while an await here is still pending), killing TX *and* RX for
                // the rest of the process's life from a single wedged device enumeration or a settings
                // read against a hung network mount -- worth fixing on its own even without the PTT
                // angle. Bounded with the same WaitAsync treatment as findings 1/2: a TimeoutException
                // here routes through the generic catch below -> abnormalTermination -> the un-key
                // attempt (a harmless no-op via TryUnkeyPttAsync's own RigId=="none"/never-keyed
                // handling, since pttKeyedOnRealRig is still false at this point) and RX-resume, then
                // releases _transmitInFlight.
                //
                // Round-18 finding 4 (round-17's own deferral (a), now fixed): WaitAsync alone cannot
                // bound any of these three -- each bottoms out in JsonSettingsStore.LoadAsync, which
                // does blocking File.Exists/File.OpenRead BEFORE its own first `await` (see
                // GetStationIdTransmitOptionsAsync's own matching comment for the full reasoning), so
                // on a hung network-mounted settings path the call doesn't even return a genuinely-
                // pending Task for WaitAsync to race. Task.Run offloads that synchronous prefix onto a
                // pool thread so WaitAsync's bound becomes real.
                var device = await Task.Run(() => ResolveDeviceAsync(forCapture: false, ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                // Seeds _liveTxGain for this call in case nothing has called SetTxVolumePercentAsync
                // yet this process's life -- PumpToPlaybackAsync below reads the LIVE field per chunk,
                // not this local, so a Pwr-slider change after this point (e.g. mid-Tune) still takes
                // effect immediately. See _liveTxGain's own doc comment for the full reasoning.
                _liveTxGain = (await Task.Run(() => GetTxVolumePercentAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false)) / 100f;
                var audioSettings = await Task.Run(() => LoadAudioSettingsAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);

                // Guarded on RigId ("none" = the null-object "no radio" backend, spec/18-path-to-1.0.md
                // Critical item 1), not Capabilities -- see IRadioController.RigId's own doc comment for
                // why a live-capability check would be unsafe here (real backends connect lazily, so
                // Capabilities reads None during a real window even with a genuine PTT-capable rig
                // configured). Read into a local exactly once: the old code's "RigId is stable so there's
                // no was-available-at-entry-gone-by-cleanup scenario" claim was FALSE (see
                // pttKeyedOnRealRig above), and this is the single read that claim is now replaced by.
                //
                // Round-31 finding (nit): renamed from pttLockedAtEntry -- despite the name, this was
                // never read at method ENTRY (that read is the separate, genuinely-early one feeding
                // pttKeyedOnRealRig's own baseline above, before the three bounded awaits). This one is
                // deliberately read LATE, right here, for its own different purpose (deciding whether to
                // double-key an already-engaged lock, which needs the freshest possible observation) --
                // see its own consumers' comments. The old name invited exactly the kind of confusion
                // round 29 already fixed once for unkeyEpochAtEntry -> unkeyEpochAfterOwnKeyAttempt.
                var pttLockedBeforeKeyDecision = _pttCoordinator.IsPttLocked;
                var rigIsRealAtKeyTime = _radioSession.RigId != "none";

                // Round-28 finding (blocker): this used to be snapshotted here, at method entry -- see
                // _pttUnkeyEpoch's own doc comment for what it's used for. Round 28 found that reading
                // it THIS early was itself the bug: it let a confirmed un-key that happened BEFORE this
                // call's own key command be misread, at cleanup time, as "confirmed AFTER I keyed" --
                // the pairing with keyEpochAfterOwnKeyAttempt (below) only proves "nobody re-keyed since
                // MY key phase", not "this specific confirmed un-key postdates my key". Moved to the
                // SAME point as keyEpochAfterOwnKeyAttempt (right after this call's own key phase, see
                // that assignment's own comment) so the pair now correctly means "since MY OWN key:
                // someone confirmed off, and nobody re-keyed" -- preserving both of round 26's own
                // motivating scenarios (an unlock mid-tone, and an unlock during
                // StopPlaybackWithWatchdogAsync's own drain), both of which land after that point.

                // Round-25 finding (risk): pttKeyedOnRealRig is baselined on an already-engaged lock --
                // an already-engaged lock means the rig is genuinely keyed independent of THIS call's
                // own RigId observation, even once RigId has since gone to "none" (RadioController.
                // DisconnectAsync's own documented no-unkey-on-disconnect behavior, the file's own
                // blocker-2 premise). Previously this stayed false whenever rigIsRealAtKeyTime was
                // false, silently disabling the cleanup Critical log AND the round-18 retry for a call
                // that entered on a rig genuinely keyed by an EARLIER SetPttLockAsync call whose RigId
                // has since gone stale -- an SWR cutoff/manual Stop TX on that call then un-keyed with
                // no Critical, no retry, just a Debug "no radio configured" line. The branch below still
                // unconditionally sets this true for the case THIS call keys/re-keys the rig itself, a
                // strict superset of this baseline.
                //
                // Round-27 finding (risk): that baseline is now assigned at this method's own true
                // entry (see the assignment right after the try block opens, above), not here -- this
                // comment stays as the reasoning for WHY the baseline exists; see that assignment's own
                // comment for WHY it had to move earlier.

                // Round-26 finding (nit, flagged not fixed): when pttLockedBeforeKeyDecision is true but
                // rigIsRealAtKeyTime is false (a lock engaged on a real rig, then RadioController.
                // DisconnectAsync set RigId to "none" without un-keying), the baseline just above
                // correctly declares this call IS holding a genuinely keyed real rig -- but the
                // keyedCompletion publish/_keyedTransmitCount increment just below are still gated
                // entirely on rigIsRealAtKeyTime, so DisposeAsync will not wait for THIS call's own
                // cleanup un-key. Harmless while RigId stays "none" (nothing to talk to either way);
                // if RadioController reconnects mid-transmit, the cleanup un-key could race
                // IRadioSessionService's own DI teardown, the exact hazard the round-8 publish exists
                // to prevent. Narrow (requires a lock-then-disconnect-then-reconnect-mid-transmit
                // sequence) and not a live bug today -- not fixed this round; revisit if reachability
                // ever changes.
                if (rigIsRealAtKeyTime)
                {
                    // Published BEFORE the key command goes out and cleared only once this method's own
                    // finally has finished its un-key attempt, so DisposeAsync can never tear
                    // IRadioSessionService down out from under an in-flight un-key. Published in the
                    // pttLockedBeforeKeyDecision case too: this call didn't key the rig, but the rig IS keyed for
                    // the whole duration of this call either way.
                    //
                    // Round-3 finding: Volatile.Write here paired with DisposeAsync's Volatile.Read is NOT
                    // enough to make the publish-then-recheck below actually see each other -- a volatile
                    // write is a release-store and a volatile read is an acquire-load, and that pairing does
                    // not forbid StoreLoad reordering (the classic Dekker's-algorithm gap). On x86-64, .NET
                    // emits both as plain movs, so this call's write and DisposeAsync's _disposed write can
                    // each sit in a per-core store buffer while the OTHER side's read runs first -- this call
                    // reads _disposed as false, DisposeAsync reads this field as null, and neither side sees
                    // the other. Interlocked.Exchange is a full fence, closing that gap. (The pre-existing
                    // Risk B pattern below -- _rxPendingResumeAfterUnlock/_pttLocked -- has the identical
                    // shape and the identical gap; that one's failure mode is a stranded RX pause, not a
                    // leaked keyed transmitter, so it hasn't been given the same treatment here.)
                    _pttCoordinator.PublishKeyedSlot(attempt);

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
                        // independent of this call, and pttLockedBeforeKeyDecision covers exactly that -- keeps the
                        // cleanup un-key's Critical-vs-Debug classification honest in the finally below.
                        // Deliberately the LATE read here -- this is exactly the "don't double-key/
                        // misclassify an already-engaged lock" decision that local is designed for, and
                        // the freshest possible observation is correct for it.
                        // (Round 27 note: no longer necessarily the same value the baseline near this
                        // method's own entry assigned -- that one now deliberately uses the EARLY
                        // `_pttLocked` read taken at entry instead, so the two can genuinely differ if
                        // _pttLocked changed during this method's own device/settings awaits.)
                        //
                        // Round-30 finding (nit): this used to silently re-narrow round 29's own widened
                        // baseline back down to pttLockedBeforeKeyDecision alone -- e.g. a prior
                        // TuneAsync(leaveKeyedAfterTune: true) leaving the rig keyed via
                        // _pttLeftKeyedByCall alone (with _pttLocked false) survived the entry baseline
                        // correctly, then got wiped back to false right here, downgrading the eventual
                        // cleanup failure from Critical to Debug. Widened to match the SAME three-flag
                        // belief test the entry baseline uses, keeping pttLockedBeforeKeyDecision's own late/fresh
                        // read for the _pttLocked term specifically (this branch's whole reason to exist)
                        // while no longer silently dropping the other two flags.
                        pttKeyedOnRealRig = _pttCoordinator.BelievesKeyed(pttLockedBeforeKeyDecision);
                        // Round-4 nit: GetType().FullName, not nameof(SstvSessionService), to match the
                        // ObjectName ObjectDisposedException.ThrowIf(_disposed, this) produces elsewhere in
                        // this class -- both throw sites should report the same object identity.
                        throw new ObjectDisposedException(GetType().FullName);
                    }
                }

                if (!pttLockedBeforeKeyDecision)
                {
                    if (rigIsRealAtKeyTime)
                    {
                        // Round-14 finding 1: this await had no bound of its own.
                        // RigctldClientProtocol bounds only its initial connect (_connectTimeout) --
                        // the per-command reply read has none -- so a half-open CAT connection (peer
                        // power-cycled, VPN drop) can block this forever WHILE THE PTT COMMAND HAS
                        // ALREADY REACHED THE WIRE AND PHYSICALLY KEYED THE RIG. Bounded with
                        // WaitAsync rather than a fresh CancellationTokenSource (contrast
                        // UnkeyForCleanupAsync's own pattern, a pure cleanup step with no caller-
                        // cancellation distinction to preserve): a genuine cancellation of `ct` still
                        // completes the awaited task with OperationCanceledException (the benign arm
                        // below), while exceeding the budget with no cancellation surfaces as
                        // TimeoutException instead, correctly routing through the generic catch below
                        // -> abnormalTermination -> urgent un-key-before-StopPlayback. `ct` is passed
                        // to WaitAsync itself too (CA2016 -- not just the inner call): WaitAsync(TimeSpan,
                        // CancellationToken) races completion/timeout/cancellation independently, so a
                        // genuine caller cancellation is observed and classified correctly HERE even if
                        // the backend itself never polls `ct` promptly once its native call has started
                        // (HamlibRadioProtocol's own documented limitation -- round-15 finding 4, closed
                        // by this rather than just caveated).
                        //
                        // Round-15 caveat: that urgent un-key is only a genuine recovery when the backend
                        // doesn't serialize requests behind the very command that just timed out --
                        // RigctldClientProtocol/HamlibRadioProtocol both hold a single request
                        // semaphore across the reply read, so the abandoned key command still holds it
                        // and the un-key attempt is expected to itself time out and log Critical
                        // ("MAY STILL BE KEYED"), not silently recover -- still strictly better than
                        // the pre-round-14 unbounded hang (the operator is now told, loudly, instead
                        // of the app just stalling), but this is a signal to disconnect/reconnect the
                        // CAT link, not evidence the rig is actually off. The underlying call is left
                        // running in the background either way -- same trade-off EnqueueAllAsync's own
                        // round-13 stall fix accepts.
                        Task? keyCommand = null;
                        try
                        {
                            keyCommand = _radioSession.SetPttAsync(true, ct);
                            await keyCommand.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Round-15 finding: this call is just as much a lost-update hazard as
                            // SetPttLockAsync's own key-failure catch (round-8 finding, see its own
                            // comment) -- a failed-but-possibly-keyed attempt is a NEW key for
                            // _pttKeyEpoch's purposes. Without this, a concurrent un-keyer that
                            // snapshotted the epoch BEFORE this call reached the wire can read the
                            // epoch as unchanged after this call's own cleanup un-key also fails, and
                            // wrongly clear _pttUnkeyFailedOnRealRig -- reporting a rig this call may
                            // have genuinely keyed as confirmed off. This was the one site rounds 5-8
                            // missed of this class; round 14's WaitAsync fix made it newly reachable in
                            // practice (a hung key command now reliably throws instead of hanging).
                            _pttCoordinator.RecordKeyEpochAdvance();

                            // Round-31 finding (risk): this abandoned key command had no fault-observer
                            // -- the direct twin of SetPttLockAsync's own pttCommand (rounds 29/30), but
                            // strictly more reachable (this method has live production callers,
                            // SetPttLockAsync currently has none) and the most safety-relevant instance
                            // in the whole class: this is the one abandoned command that, by this
                            // method's own reasoning just above, may have physically keyed the rig.
                            // Harm is diagnostic/attribution only -- state handling above is already
                            // correct regardless (epoch bump, then abnormalTermination -> urgent un-key
                            // in the finally). Same gating as every sibling site: only attach if the
                            // command was actually issued (not still null from a synchronous throw) and
                            // is still running (not yet completed -- if it already finished, its own
                            // fault already propagated through the WaitAsync above as the exception this
                            // catch is handling).
                            if (keyCommand is { IsCompleted: false })
                            {
                                _ = keyCommand.ContinueWith(
                                    t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT key command (finished after watchdog)", t.Exception!)),
                                    CancellationToken.None,
                                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                    TaskScheduler.Default);
                            }

                            throw;
                        }

                        // Round-5 finding: see PttSafetyCoordinator's own doc comment.
                        _pttCoordinator.RecordKeyEpochAdvance();
                        // Round-23 finding (risk): the ONE Log.* call in the whole file that executes
                        // while a real transmitter is physically keyed -- was still unwrapped. A
                        // transient logging-provider failure landing in this exact window (the state
                        // latches above already correctly mark this key attempt, so no state-latching
                        // write is skipped -- just a risk, not a blocker) used to abort the transmit
                        // with the raw logging exception instead of a caller-recognizable type, and
                        // swallow the very PlaybackFailed log that would have explained why.
                        SafeLog(() => Log.PttKeyed(_logger));
                    }
                    else
                    {
                        // Round-23 finding (nit): same shape, no-radio branch -- PTT is never touched
                        // here, so the only harm is a transient logging failure aborting a no-radio
                        // transmit with a bogus exception type.
                        SafeLog(() => Log.PttSkippedNoRadio(_logger));
                    }
                }

                // Round-27 finding (risk): _pttUnkeyEpoch alone answers "did a confirmed un-key happen
                // since I started", not "is the rig off NOW" -- a key landing AFTER that confirmed
                // un-key but BEFORE this call's own cleanup is invisible to unkeyEpochAfterOwnKeyAttempt's own
                // check, unlike _pttKeyEpoch's own consumers, which snapshot immediately before their
                // own un-key await (a one-await-wide window) rather than at method entry (a whole-
                // transmission-wide window, potentially minutes). Concrete leak: this transmit keys ->
                // a concurrent SetPttLockAsync(false) confirms the rig off (_pttUnkeyEpoch bumps) -> a
                // LATER SetPttLockAsync(true) re-keys it (successfully, or fails after physically
                // keying -- either way _pttKeyEpoch bumps again, per round-7/8's own established rule)
                // -> this transmit's own cleanup sees _pttUnkeyEpoch moved and skips its un-key
                // entirely, leaving a genuinely re-keyed rig un-attended. Snapshotted here, right after
                // this call's own key phase (success, failure, or no-radio skip) -- both consumer sites
                // now also require this to be UNCHANGED before trusting the "already off" signal,
                // erring toward attempting the un-key (this file's own established rule) whenever
                // ANYONE has re-keyed since this point, not just when this call's own belief is stale.
                keyEpochAfterOwnKeyAttempt = _pttCoordinator.CurrentKeyEpoch;

                // Round-28 finding (blocker): unkeyEpochAfterOwnKeyAttempt now read HERE too, not at method entry
                // -- see its own comment (near pttLockedBeforeKeyDecision, above) for why the entry-time read was
                // itself the bug. Reading both epochs at this SAME point (immediately after this call's
                // own key phase, no await between the two reads) means the pair now correctly answers
                // "since MY OWN key: did anyone confirm off, and did anyone re-key" -- not two questions
                // anchored to different, unrelated points in time.
                unkeyEpochAfterOwnKeyAttempt = _pttCoordinator.CurrentUnkeyEpoch;

                // Round-14 finding 2: same unbounded-await shape as finding 1, one step later --
                // PTT is already keyed (or was already locked keyed at entry) by this point, so an
                // indefinite native device-open hang here is exactly as much a leaked-keyed-
                // transmitter exposure as finding 1's key command itself, not a smaller-window
                // variant of it. Same WaitAsync treatment, same reasoning. Round-15 nit:
                // MiniAudioEngine.StartPlaybackAsync holds its own playback lock across the abandoned
                // native open, so the following StopPlaybackWithWatchdogAsync's own claim step queues
                // FIFO-behind it and can burn its own full watchdog budget too (self-healing, not a
                // new failure mode -- the queued claim still eventually gets and disposes the late-
                // published session -- but worth knowing this path's worst case is roughly
                // 2x _cleanupTimeout, not 1x, if this lands during DisposeAsync's own bounded wait).
                await _audioEngine.StartPlaybackAsync(
                    device, sampleRate, audioSettings.PeriodSizeInFrames, audioSettings.Periods,
                    audioSettings.StereoTxEnabled, ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                await PumpToPlaybackAsync(samples, sampleRate, totalSamplesEstimate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a normal, expected way for this to end (manual Stop TX, SWR
                // auto-cutoff -- see TxControlsPaneViewModel for which one) -- this layer has no way to
                // tell which caused it, so it's logged generically at Information, not as a failure.
                abnormalTermination = true;
                // Round-22 finding (risk): a throwing log call here (a broken logging provider) used
                // to REPLACE this OperationCanceledException as what propagates out of this method --
                // TxControlsPaneViewModel's own catch (OperationCanceledException) branches on this
                // exception identity to distinguish an SWR-cutoff abort from a generic failure (see
                // RaiseCapturePausedForTransmitChanged's own doc comment for the identical harm, which
                // round 20 already guarded against for that method). PTT itself is unaffected either
                // way -- the finally below still runs the urgent un-key -- only the exception identity
                // reaching the caller was at risk.
                SafeLog(() => Log.PlaybackCancelled(_logger));
                throw;
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Round-3 nit: without this arm, the disposed-check throw above falls into the generic
                // `catch (Exception)` below and logs at Error with a full stack trace on every ordinary
                // close-during-tune -- this is an expected shutdown condition (same category as the
                // OperationCanceledException arm above), not a failure. The `when (_disposed)` guard only
                // narrows this to "we are already mid-DisposeAsync" -- it does NOT distinguish OUR disposed-
                // check throw from a genuine ObjectDisposedException some other dependency happens to throw
                // during that same shutdown window (e.g. a disposed audio/radio backend); both are equally
                // expected once _disposed is true, so both are logged at Information here. A GENUINE
                // ObjectDisposedException reached OUTSIDE of shutdown (_disposed still false) still falls
                // through to the generic catch below and logs as a real Error-level failure.
                abnormalTermination = true;
                // Round-22 finding (risk): same shape as the OperationCanceledException arm above -- a
                // throwing log call used to substitute an unrelated exception for the
                // ObjectDisposedException this class's own established convention has callers branch
                // on (see SetPttLockAsync's own post-dispose recovery guard for the same reasoning).
                SafeLog(() => Log.PlaybackAbortedByDispose(_logger));
                throw;
            }
            catch (Exception ex)
            {
                abnormalTermination = true;
                // Round-22 finding (nit): same log-before-rethrow shape as the two typed catch arms
                // above, wrapped for consistency -- abnormalTermination is already set and the finally
                // below still runs the urgent un-key regardless, so this is exception-identity-only.
                SafeLog(() => Log.PlaybackFailed(_logger, ex));
                throw;
            }
            finally
            {
                try
                {
                    var unkeyAlreadyAttempted = false;
                    var unkeyConfirmed = false;

                    // ---- Blocker 1, urgent half ----
                    // An abnormal termination IS the safety path (manual Stop TX / SWR auto-cutoff): the
                    // operator wants the transmitter off NOW and there is no audio tail worth preserving
                    // (the image is aborted either way). Un-key BEFORE StopPlayback so a wedged output
                    // device can't delay it at all. skipUnkeyAndRxResume is false by construction whenever
                    // abnormalTermination is true, so this can never fire on a "stay keyed" path.
                    // Round-19 finding: this whole cleanup region (the enclosing try starting above)
                    // had a finally but NO catch -- both UnkeyForCleanupAsync and
                    // StopPlaybackWithWatchdogAsync are documented as never throwing, but neither claim
                    // was actually enforced, and a throwing logging provider (the same realistic source
                    // round 17/18 already treated as in-scope for DisposeAsync's own per-step guards) is
                    // enough to violate it. Without a guard HERE, that throw skips every step below it
                    // (StopPlayback, the retry, RX-resume) with NOTHING recorded -- worse than
                    // DisposeAsync's own equivalent gap, since this is the PTT-off record itself, not
                    // just teardown. Each step now gets its own try/catch, matching DisposeAsync's
                    // already-established per-step shape (see its own comment).
                    if (abnormalTermination)
                    {
                        try
                        {
                            unkeyConfirmed = await UnkeyForCleanupAsync(pttKeyedOnRealRig).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // Round-20 finding: SafeLog, not a plain call -- see its own doc comment.
                            // The state record itself doesn't depend on this log succeeding either
                            // way (UnkeyForCleanupAsync's own round-20 fix already latched
                            // _pttUnkeyFailedOnRealRig before it could throw), only whether this catch
                            // itself stays reachable so StopPlayback/the retry/RX-resume below still run.
                            SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (urgent)", ex));
                        }

                        unkeyAlreadyAttempted = true;
                    }

                    // ---- Blocker 1, budget half ----
                    // On a NORMAL completion the un-key still runs AFTER the drain -- dropping PTT while
                    // the miniaudio ring / PulseAudio server queue still hold audio would truncate the
                    // tail of every successful transmission, exactly what MiniAudioEngine.DrainTailMargin
                    // exists to prevent. What changed is that the drain can no longer STARVE the un-key:
                    // it gets a bounded wait of its own, and the un-key gets its own fresh
                    // CancellationTokenSource inside UnkeyForCleanupAsync.
                    try
                    {
                        await StopPlaybackWithWatchdogAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback", ex));
                    }

                    // Risk B (Tier A Batch 3 chunk 3a, round 19): _pttLocked read ONCE for this whole
                    // decision, not independently at the skip computation and again at the deferred-
                    // resume branch below -- two independent reads let a SetPttLockAsync(false) landing
                    // between them disagree, taking the "leaveKeyedAfterCall residual" branch on a call
                    // that had actually paused RX, stranding it with nothing left to resume it.
                    //
                    // Round-25 finding (risk): round 19's fix took this ONE read too early -- at the
                    // very top of this finally, BEFORE StopPlaybackWithWatchdogAsync's own await (up to
                    // playbackStopWaitBudget). A SetPttLockAsync(true) that completes DURING that drain
                    // wait -- engaging a genuine operator lock seconds after this transmit's own body
                    // finished -- was invisible to a snapshot already taken before the drain even
                    // started: this call's own un-key below then ran unconditionally and silently
                    // defeated that just-engaged lock, with UnkeyForCleanupAsync's own epoch guard
                    // unable to catch it either (that guard protects against a newer key completing
                    // WHILE the un-key call itself is in flight, not one that already completed before
                    // the un-key call was even entered). Moved here, immediately before this same read's
                    // first actual use -- still exactly ONE read shared by every decision below (round
                    // 19's own property is preserved), just taken as late as StopPlaybackWithWatchdogAsync
                    // allows rather than long before it. Shrinks the race to the residual, instruction-
                    // scale window between this read and the un-key command actually reaching the wire
                    // just below -- the same class of accepted residual this file already documents at
                    // its other epoch-guarded sites, not eliminated outright.
                    var pttLockedAtCleanup = _pttCoordinator.IsPttLocked;

                    // Only a NORMAL completion honors "stay keyed" (leaveKeyedAfterCall/lock) -- see this
                    // method's own doc comment for why an abnormal termination always overrides both.
                    var skipUnkeyAndRxResume = PttSafetyCoordinator.ComputeSkipUnkeyAndRxResume(abnormalTermination, leaveKeyedAfterCall, pttLockedAtCleanup);

                    // Round-18 finding: UnkeyForCleanupAsync's own bool return (whether the un-key was
                    // actually CONFIRMED, not just attempted) used to be discarded here -- an urgent
                    // un-key that timed out against a momentarily-busy backend (SWR cutoff / manual Stop
                    // TX racing a backend that frees up moments later) got exactly one attempt, then
                    // deferred all the way to DisposeAsync, which may not run for hours. Retried here,
                    // immediately, while the failure is still fresh. Guarded on pttKeyedOnRealRig so the
                    // benign RigId=="none" case (already correctly "confirmed" false-but-harmless) never
                    // gets a pointless second attempt.
                    //
                    // Round-19 correction: the retry does NOT race the first attempt on an independent
                    // window, as an earlier version of this comment claimed -- round 18's own fix left
                    // the first, timed-out command queued and UNCANCELLABLE (CancellationToken.None,
                    // see TryUnkeyPttAsync's own comment), and both backends serialize on a single
                    // approximately-FIFO request gate, so this retry queues BEHIND the abandoned first
                    // command rather than beside it. Net effect is still strictly better than pre-
                    // round-18 (the first command eventually reaches an unwedging rig on its own, and
                    // if it doesn't, this retry -- and later DisposeAsync's own backstop -- queue up
                    // behind it and get their own turn once it clears), just not via the two
                    // "independent" attempts the removed wording implied.
                    if (!skipUnkeyAndRxResume && (!unkeyAlreadyAttempted || (pttKeyedOnRealRig && !unkeyConfirmed)))
                    {
                        // Round-26 finding (nit, closed as a side effect of finding 1's own
                        // _pttUnkeyEpoch mechanism): round 25 moved pttLockedAtCleanup's own read to
                        // right before this decision, closing a leaked-transmitter risk -- but that also
                        // opened a symmetric window: an operator's own SetPttLockAsync(false) completing
                        // DURING StopPlaybackWithWatchdogAsync's own drain now reads pttLockedAtCleanup
                        // as false too, so this call's cleanup would otherwise attempt a REDUNDANT
                        // un-key on a rig the operator's own unlock already confirmed off. If that
                        // redundant attempt then fails for any unrelated reason (e.g. the CAT link
                        // dropped in the interim), it falsely latches _pttUnkeyFailedOnRealRig on a rig
                        // that is demonstrably fine -- the exact signal erosion round 4's clear exists to
                        // prevent. Skip the attempt entirely when a confirmed un-key already happened
                        // anywhere since this call's own key phase (round-29 nit: corrected from "since
                        // this call's own entry" -- the actual snapshot point, per round 28, is right
                        // after the key phase, not at method entry).
                        //
                        // Round-27 finding (risk): also requires _pttKeyEpoch to be UNCHANGED since
                        // this call's own key phase -- see keyEpochAfterOwnKeyAttempt's own comment for
                        // why the confirmed-un-key signal alone is not enough (a re-key AFTER that
                        // confirmed un-key, still within this call's own lifetime, must never be
                        // skipped over).
                        if (pttKeyedOnRealRig && _pttCoordinator.ConfirmedUnkeyAlreadyHappenedSinceOwnKey(keyEpochAfterOwnKeyAttempt, unkeyEpochAfterOwnKeyAttempt))
                        {
                            // Round-27 finding (nit): no `unkeyConfirmed = true` here -- nothing rechecks
                            // this block's own outer `if` condition afterward, so that write was dead
                            // (this whole block runs at most once per call).
                            SafeLog(() => Log.PttCleanupUnkeySkippedConfirmedElsewhere(_logger));
                        }
                        else
                        {
                            try
                            {
                                await UnkeyForCleanupAsync(pttKeyedOnRealRig).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // Round-20 finding: SafeLog -- see the urgent-half arm's own comment above.
                                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (retry)", ex));
                            }
                        }
                    }

                    if (skipUnkeyAndRxResume && leaveKeyedAfterCall && pttKeyedOnRealRig)
                    {
                        // PTT is deliberately left physically keyed after this call returns, with no
                        // _pttLocked to record it (SetPttLockAsync's own doc comment already calls this
                        // gap out) -- DisposeAsync's shutdown backstop needs to know about it.
                        //
                        // Round-7 finding: only ever writes `true` here, never `false` -- this line runs
                        // AFTER StopPlaybackWithWatchdogAsync's own await (up to 5s), the same
                        // snapshot-then-act-on-stale-state shape rounds 5/6 already closed at the two
                        // epoch-guarded sites. Writing `false` unconditionally here (the old behavior, when
                        // pttKeyedOnRealRig happened to be false -- e.g. RigId was "none" at this call's own
                        // key time) could stomp a CONCURRENT call's genuinely-keyed `true`, set moments
                        // earlier while this await was in flight. The `false` direction is never load-
                        // bearing here -- the only correct owner of clearing this flag is a CONFIRMED
                        // un-key, which the two epoch-guarded sites already handle.
                        //
                        // Round-26 finding (risk): that reasoning covered a re-KEY racing this write, but
                        // not a confirmed UN-key racing it -- e.g. TuneAsync(leaveKeyedAfterTune: true)
                        // keys the rig, the operator calls SetPttLockAsync(false) mid-tone and genuinely
                        // un-keys it (rig confirmed OFF, _pttUnkeyEpoch bumped), then this line ran anyway
                        // and wrote a permanent false "still keyed" belief on a rig that is demonstrably
                        // off. Guarded on unkeyEpochAfterOwnKeyAttempt: if a confirmed un-key happened
                        // anywhere since this call's own key phase, skip the write -- the rig is already
                        // known to be off, and writing `true` here would just be wrong, not merely
                        // stale. (Round-29 nit: this comment previously said "since this call started" --
                        // corrected; the actual snapshot point, per round 28, is right after this call's
                        // own key phase, not at method entry/start.)
                        //
                        // Round-27 finding (risk, reported as "covered by luck" today -- whoever re-keys
                        // after a confirmed un-key sets _pttLocked or _pttLeftKeyedByCall itself, so
                        // nothing is currently lost by this write's own skip -- but the same
                        // _pttKeyEpoch cross-check is added here anyway for defense-in-depth, matching
                        // the un-key call site's own fix and not relying on that luck holding under a
                        // future change).
                        var latchResult = _pttCoordinator.TryLatchLeftKeyedAfterCall(keyEpochAfterOwnKeyAttempt, unkeyEpochAfterOwnKeyAttempt);
                        if (latchResult == PttSafetyCoordinator.PttLeftKeyedLatchResult.SkippedConfirmedUnkeyRace)
                        {
                            SafeLog(() => Log.PttLeftKeyedSkippedConfirmedUnkeyRace(_logger));
                        }
                    }

                    // Created only HERE, after every potentially-slow step above, so the RX-resume steps
                    // get a full independent budget rather than whatever StopPlayback/un-key left over --
                    // the same starvation bug blocker 1 is about, one step further down the chain. At most
                    // one StartReceivingAsync call runs per invocation (the three branches below are
                    // mutually exclusive on wasReceiving/skipUnkeyAndRxResume), so one source is enough.
                    //
                    // Round-16 correction (mirrors SetPttLockAsync's own twin CTS's identical fix, see
                    // its own comment): every StartReceivingAsync(rxResumeCts.Token) call below is now
                    // ALSO wrapped in .WaitAsync(rxResumeCts.Token) -- passing the token as the callee's
                    // own `ct` parameter alone never actually bounded this call, since
                    // MiniAudioDeviceEnumerator.RefreshAsync/MiniAudioEngine.StartCaptureAsync only check
                    // `ct` at their own start/lock-acquire boundary, not during the blocking native call
                    // itself. Without this, a wedged capture device leaves this await pending forever,
                    // and since this whole method's outer finally (where _transmitInFlight is finally
                    // released) can't complete while ANY await inside it is still pending, that strands
                    // _transmitInFlight too -- the exact permanent-lockout class rounds 13-15 closed at
                    // every OTHER site in this method, missed here because this CTS looked
                    // already-sufficient.
                    using var rxResumeCts = new CancellationTokenSource(_cleanupTimeout);

                    // Auditor-caught (round 2): a PRIOR locked call may have already stopped capture
                    // and set the coordinator's own deferred-resume flag before THIS call force-unkeyed
                    // on an abnormal termination -- if so, THIS call's own `wasReceiving` is false, so the
                    // `if (wasReceiving)` block below would never see it, leaving RX stopped forever
                    // with IsPttLocked already reporting false. See
                    // PttSafetyCoordinator.DecideStrandedLockResume's own doc comment: guarded on
                    // `!wasReceiving` internally so this never double-fires alongside that block's own
                    // resume for THIS call.
                    if (_pttCoordinator.DecideStrandedLockResume(skipUnkeyAndRxResume, wasReceiving) == PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaise)
                    {
                        await TryCleanupAsync("Resume RX (stranded lock pending-resume)", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                        RaiseCapturePausedForTransmitChanged(false);
                    }

                    if (wasReceiving)
                    {
                        // See PttSafetyCoordinator.DecideWasReceivingResume's own doc comment for the
                        // full 3-way reasoning: ResumeThenRaise (not skipping -- normal resume);
                        // ResumeThenRaiseUnlockRaced (Risk B, publish-then-recheck -- a concurrent
                        // SetPttLockAsync(false) raced this call's own deferred-resume publish, narrows
                        // but does not fully close the window, deliberately unfenced since the failure
                        // mode is a user-recoverable stranded RX pause, not a leaked keyed transmitter --
                        // see the coordinator's own class doc comment); RaiseOnly (Auditor-caught round 1
                        // residual: leaveKeyedAfterCall=true and NOT locked -- RX intentionally stays
                        // stopped, but this transmission's own pause window is still over, so the event
                        // must not stay stuck true forever); DeferPending (published, nothing to do yet).
                        var decision = _pttCoordinator.DecideWasReceivingResume(skipUnkeyAndRxResume, abnormalTermination, pttLockedAtCleanup);
                        switch (decision)
                        {
                            case PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaise:
                                await TryCleanupAsync("Resume RX", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                                // User-reported gap (2026-08-18): fires once the resume attempt has finished,
                                // success or failure -- this event is "no longer paused FOR THIS transmission,"
                                // not a restatement of IsReceiving itself (see the event's own doc comment).
                                RaiseCapturePausedForTransmitChanged(false);
                                break;
                            case PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaiseUnlockRaced:
                                await TryCleanupAsync("Resume RX (unlock raced cleanup)", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                                RaiseCapturePausedForTransmitChanged(false);
                                break;
                            case PttSafetyCoordinator.PttRxResumeDecision.RaiseOnly:
                                RaiseCapturePausedForTransmitChanged(false);
                                break;
                            case PttSafetyCoordinator.PttRxResumeDecision.DeferPending:
                            case PttSafetyCoordinator.PttRxResumeDecision.None:
                            default:
                                break;
                        }
                    }
                }
                finally
                {
                    // Blocker 3: signalled no matter how the cleanup above ended (including a throw from
                    // a subscriber or a cleanup step) -- a DisposeAsync waiting on this must never be left
                    // hanging until its own timeout by an unrelated failure. See
                    // PttSafetyCoordinator.ReleaseKeyedSlot's own doc comment: only clears the shared
                    // registration if it still points at THIS call's instance, so an overlapping
                    // PlayWithPttAsync call (round-6 finding: a REAL production interleaving, not
                    // pathological) can't have its own registration erased.
                    _pttCoordinator.ReleaseKeyedSlot(attempt);
                }
            }
        }
        finally
        {
            // Released unconditionally -- whether this call completed normally, was cancelled, or
            // threw at any point above, including before PTT was ever touched.
            Volatile.Write(ref _transmitInFlight, 0);
        }
    }

    /// <summary>The un-key half of <see cref="PlayWithPttAsync"/>'s cleanup, extracted so blocker 1's
    /// two call sites (the abnormal-termination early un-key and the normal-completion post-drain one)
    /// share one implementation -- and so each gets its OWN fresh <see cref="CancellationTokenSource"/>.
    /// That is blocker 1's actual fix: the un-key's deadline must never be the leftover of an earlier
    /// cleanup step's spend.
    ///
    /// Round-17 correction: giving this its own fresh CTS closes blocker 1 (a shared deadline), but
    /// passing that CTS's token as <see cref="TryUnkeyPttAsync"/>'s own `ct` parameter did NOT, by
    /// itself, actually bound the un-key call -- see that method's own comment for why (neither
    /// shipped CAT backend polls `ct` once its native/protocol call has started). <c>unkeyCts</c>'s
    /// own timeout is real and still correctly sized; what was missing was an independent
    /// <c>WaitAsync</c> on the awaiting side, now added at the one call site.</summary>
    // T1-5: thin wrapper around PttSafetyCoordinator.BeginConfirmUnkey/CommitConfirmedUnkey -- this
    // method still owns the actual awaited unkey command and every Log.* call; the coordinator owns
    // only the epoch-snapshot/pessimistic-latch/confirmed-clear state transitions (see that class's
    // own doc comment for the full lost-update-race reasoning this decomposition preserves).
    private async Task<bool> UnkeyForCleanupAsync(bool pttKeyedOnRealRig)
    {
        var attempt = _pttCoordinator.BeginConfirmUnkey(pttKeyedOnRealRig);

        using var unkeyCts = new CancellationTokenSource(_cleanupTimeout);
        if (await TryUnkeyPttAsync(pttKeyedOnRealRig, unkeyCts.Token).ConfigureAwait(false))
        {
            var commitResult = _pttCoordinator.CommitConfirmedUnkey(attempt);
            if (commitResult == PttSafetyCoordinator.PttUnkeyCommitResult.Confirmed)
            {
                SafeLog(() => Log.PttReleased(_logger));
            }
            else
            {
                SafeLog(() => Log.PttUnkeyRaceLostToNewerKey(_logger));
            }

            return true;
        }

        if (pttKeyedOnRealRig)
        {
            // Round-3 finding: this is the fourth "keyed at shutdown" state -- see the field's own doc
            // comment for why DisposeAsync's existing three-state check otherwise misses exactly this
            // case. Round-20 finding: the write itself now happens BEFORE the attempt above, not here
            // -- see this method's own comment at the top for why.
            //
            // Risk A (partial fix, Tier A Batch 3 chunk 3a): a Warning is too quiet for "a real
            // transmitter this call keyed may still be on the air." Escalated to Critical, and only
            // for the captured-at-key-time real-rig case, so it can never fire for the benign
            // fresh-install RigId=="none" path that the Warning-suppression logic exists for.
            SafeLog(() => Log.PttStillKeyedAfterFailedUnkey(_logger));
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
    /// playback stop is still in flight. Round-14 correction: an immediately following transmit does
    /// NOT fail loudly against the still-wedged device -- MiniAudioEngine's own StopPlaybackAsync
    /// un-publishes (nulls) its session field at CLAIM time, synchronously, before the drain/dispose
    /// that can actually block even starts, so <see cref="IAudioEngine.StartPlaybackAsync"/> sees no
    /// session and proceeds to open a SECOND native session against the same wedged device rather
    /// than throwing "already started". That second open is itself now bounded by finding 2's
    /// WaitAsync fix (see the call site in <see cref="PlayWithPttAsync"/>), so it can no longer hang
    /// forever -- but two concurrent native opens against one wedged device is still an
    /// undocumented-behavior risk this class does not otherwise attempt to prevent. Out of this
    /// chunk's scope (the fix, if any, belongs in <c>MiniAudioEngine</c>, not here) -- flagged, not
    /// fixed.</summary>
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
            SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback", ex));
            return;
        }

        // Round-14 nit: an uncancelled Task.Delay stays queued to the timer wheel until its own full
        // duration elapses even after WhenAny returns on the OTHER branch -- a small, bounded leak per
        // call (once per transmission), closed by cancelling it on the stopTask-wins path below.
        using var watchdogCts = new CancellationTokenSource();
        var completed = await Task.WhenAny(stopTask, Task.Delay(_playbackStopWaitBudget, watchdogCts.Token)).ConfigureAwait(false);
        if (completed == stopTask)
        {
            watchdogCts.Cancel();
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Same best-effort contract as TryCleanupAsync -- see its own doc comment.
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback", ex));
            }

            return;
        }

        SafeLog(() => Log.PlaybackStopWatchdogFired(_logger, _playbackStopWaitBudget));
        _ = stopTask.ContinueWith(
            t => SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback (finished after watchdog)", t.Exception!)),
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
        // Round-17/18/20/26 history (why the command runs on CancellationToken.None while only the
        // WAIT is bounded by `ct`; why a failed logging call itself must not throw; why a late
        // failure on the abandoned command still needs a fault-observer continuation) now lives on
        // PttUnkeyHelper.TryUnkeyBoundedAsync's own doc comment -- T0-1 extracted it there so
        // RadioSessionService's own un-key retry loop can reuse the identical shape. This method's
        // own observable behavior (exceptions, logging, fault-observer semantics) is unchanged by
        // that extraction.
        var result = await PttUnkeyHelper.TryUnkeyBoundedAsync(
            _radioSession.SetPttAsync,
            ct,
            onLateFailure: ex => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (finished after watchdog)", ex))).ConfigureAwait(false);

        if (!result.Success)
        {
            // Round-20 finding: this method's own doc/callers assumed it "never throws" -- it did,
            // via these very log calls, when the logging provider itself failed. SafeLog closes that
            // specific hole (see its own doc comment).
            if (pttKeyedOnRealRig)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off", result.Exception!));
            }
            else
            {
                SafeLog(() => Log.PttUnkeySkippedNoRadio(_logger));
            }
        }

        return result.Success;
    }

    /// <summary>Round-20 finding: this class's own cleanup/dispose paths, across rounds 17-19, all
    /// assumed logging a failure could itself never fail -- a throwing logging provider (a file logger
    /// on a full disk, the realistic source this chunk has repeatedly treated as in-scope) proved that
    /// wrong at <see cref="TryUnkeyPttAsync"/>, defeating every guard built on top of it. Used at every
    /// log call this class's own cleanup/dispose/catch paths reach -- round 21 finding: round 20's own
    /// claim that this was already true was itself false (round 19 and round 20 each thought they'd
    /// found "the" instance of the never-throws-callee pattern and were each wrong one frame deeper);
    /// round 21 did a full enumeration of every <c>Log.*</c> call site in the file and applied this
    /// everywhere a throw could skip a safety-relevant step or escape into a background/drain thread
    /// with no isolation of its own. Given that round 19 and round 20 each made this same
    /// "comprehensive" claim and were each subsequently found incomplete, treat "applied everywhere"
    /// above as a snapshot of round 21's own sweep, not a closed guarantee -- a future round finding
    /// one more unwrapped <c>Log.*</c> call inside a catch/cleanup/fault-observer path on this class's
    /// PTT-safety-relevant chain would not be a surprise; keep checking rather than trusting this
    /// comment.
    ///
    /// Round-21 finding 7: NOT silent-only anymore. A totally broken logging provider (this whole
    /// mechanism's own threat model) used to leave an operator with ZERO indication a transmitter
    /// might still be keyed -- "safe" and "silent" are not the same thing for a Critical "PTT MAY
    /// STILL BE KEYED" message. Falls back to stderr, bypassing the broken <see cref="ILogger"/>
    /// entirely, so a broken PROVIDER specifically still surfaces something. That fallback is itself
    /// wrapped -- if stderr is ALSO broken (e.g. redirected somewhere failing), there is genuinely
    /// nothing left this method can safely do, and it gives up rather than risk being the thing that
    /// crashes the process.</summary>
    private static void SafeLog(Action logAction)
    {
        try
        {
            logAction();
        }
#pragma warning disable CA1031 // Deliberately catches everything -- see this method's own doc comment.
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"[SstvSessionService] Logging provider failed while reporting a safety-critical event: {ex}");
            }
            catch
            {
                // Truly nothing left to do -- see this method's own doc comment.
            }
        }
#pragma warning restore CA1031
    }

    /// <summary>Returns whether <paramref name="step"/> actually completed without throwing --
    /// callers that need to know (e.g. only clearing <c>IsPttLocked</c> once a PTT-off command
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
            //
            // Round-20 finding: this method's own "never masks the original exception" guarantee
            // (its own doc comment above) depended on this log call itself never throwing -- SafeLog
            // closes that specific hole (a broken logging provider is the realistic source this round
            // treats as in-scope, same as everywhere else this fix was applied this round).
            SafeLog(() => Log.CleanupStepFailed(_logger, stepName, ex));
            return false;
        }
    }

    /// <summary>Shared RX-resume bound for all 4 call sites -- Task.Run offloads
    /// StartReceivingAsync's own synchronous settings-read prefix (see
    /// GetStationIdTransmitOptionsAsync's own comment for why WaitAsync alone can't bound that),
    /// WaitAsync(rxResumeCts.Token) bounds the wait itself.
    ///
    /// Round-19 finding: on timeout, the abandoned background task's eventual outcome used to be
    /// unobserved -- unlike StopPlaybackWithWatchdogAsync/StopReceivingAsync's own established
    /// fault-observer continuations for their own abandoned tasks. Attached unconditionally right
    /// here (rather than only inside a timeout branch, the shape those two siblings use) since this
    /// helper is the single choke point for the Task.Run creation across all 4 call sites --
    /// OnlyOnFaulted means it is a no-op on the (common) success path regardless of when it was
    /// attached.</summary>
    private async Task ResumeReceivingBoundedAsync(CancellationTokenSource rxResumeCts)
    {
        var resumeTask = Task.Run(() => StartReceivingAsync(rxResumeCts.Token), rxResumeCts.Token);
        try
        {
            await resumeTask.WaitAsync(rxResumeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!resumeTask.IsCompleted)
        {
            // Round-20 nit: the round-19 shape attached this observer UNCONDITIONALLY at creation
            // time, which double-logged every ORDINARY (non-timeout) resume failure -- resumeTask
            // faulting for a mundane reason (e.g. no capture device configured) makes this WAIT throw
            // the SAME exception, which the caller (TryCleanupAsync) already logs once as "Resume RX";
            // the unconditional observer then logged it AGAIN as "(finished after watchdog)" once
            // resumeTask's own fault also satisfied OnlyOnFaulted. Gated here on the wait genuinely
            // giving up while resumeTask is STILL running (the actual "abandoned" case this observer
            // exists for) -- if resumeTask is already complete by the time this catch runs, its
            // result/fault already propagated through this same WaitAsync call, so there is nothing
            // left to observe later.
            _ = resumeTask.ContinueWith(
                t => SafeLog(() => Log.CleanupStepFailed(_logger, "Resume RX (finished after watchdog)", t.Exception!)),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    // T1-3 (production_audit.md): batches its output into fixed-size chunks instead of yielding one
    // float per await, so PlayWithPttAsync/PumpToPlaybackAsync can consume Tune's own tone the same
    // way as a real SSTV transmission's audio -- see PlayWithPttAsync's own comment for why. Plan-
    // review finding: `yield return` inside an async iterator completing synchronously is NOT itself
    // a cooperative thread-yield point (the consumer's own await just continues inline) -- removing
    // the old per-4096-sample `await Task.Yield()` on the theory that each batch's own yield already
    // provides one would have removed the ONLY guaranteed suspension point in this whole path (with
    // FakeAudioEngine, which accepts unbounded playback synchronously in tests, this loop would then
    // run as one uninterrupted synchronous block). Kept, same cadence as before (once per batch now,
    // was once per 4096 samples -- identical when ToneBatchSize is 4096, and harmless either way).
    // Only caller is TuneAsync, private static, no test reaches this directly -- no separate
    // per-float flatten wrapper kept around, unlike AnalogFmSstvEncoder's own EncodeAsync/
    // EncodeBatchedAsync pair, since nothing needs the per-float shape here anymore.
    private const int ToneBatchSize = 4096;

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> GenerateTone(
        double frequencyHz, TimeSpan duration, int sampleRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var totalSamples = (long)(duration.TotalSeconds * sampleRate);
        var angularStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        var buffer = new float[ToneBatchSize];
        var count = 0;

        for (var i = 0L; i < totalSamples; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Plan-review finding: MUST stay the absolute sample index i, never a per-batch-relative
            // offset -- this project has hit exactly this local-vs-absolute-index bug class three
            // separate times before (RX buffer phases 6c-6d). angularStep*i is what keeps the sine
            // wave phase-continuous across every batch boundary.
            buffer[count++] = (float)Math.Sin(angularStep * i);
            if (count == ToneBatchSize)
            {
                yield return buffer;
                buffer = new float[ToneBatchSize];
                count = 0;
                await Task.Yield();
            }
        }

        if (count > 0)
        {
            yield return buffer.AsMemory(0, count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Round-2 fix: must be the very FIRST thing this method does, before even
        // AwaitInFlightKeyedTransmitAsync's read of _keyedTransmitCompletion just below -- this is the
        // other half of the publish-then-recheck race PlayWithPttAsync now performs against this same
        // field (see its own disposed-check right after publishing _keyedTransmitCompletion). Whichever
        // side's write happens first, the other side's read is guaranteed to observe it.
        //
        // Round-3 finding: the plain write above is release-store, not a full fence -- see
        // PlayWithPttAsync's own comment at its Interlocked.Exchange publish for why that pairing alone
        // does not forbid StoreLoad reordering. This is the other half of that same fence.
        _disposed = true;
        Interlocked.MemoryBarrier();

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
        // Round-18 finding 6: this step (and the backstop un-key just below) used to run with no
        // guard at all -- the identical "one throw skips everything after it" shape round 17 fixed 30
        // lines below, just two steps earlier. A throw here (e.g. a logging provider failing inside
        // Log.WaitingForKeyedTransmitAtShutdown/Log.KeyedTransmitCleanupWaitTimedOut -- a file logger
        // on a full disk) would skip the backstop un-key AND every step round 17 already guarded,
        // with _disposed already true and the exception escaping into DI teardown.
        try
        {
            await AwaitInFlightKeyedTransmitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "AwaitInFlightKeyedTransmit (dispose)", ex));
        }

        // A rig can be physically keyed at shutdown for FOUR distinct reasons, and _pttLocked only
        // covered one of them: an engaged PTT lock (_pttLocked), a TuneAsync(leaveKeyedAfterTune:true)
        // that deliberately left it keyed (_pttLeftKeyedByCall), an in-flight transmit whose own
        // un-key never completed (_keyedTransmitCount still nonzero after the bounded wait above), and
        // a completed transmit whose own un-key attempt FAILED (_pttUnkeyFailedOnRealRig -- round-3
        // finding: _pttLocked is already false by the time this failure is even observed, and
        // _keyedTransmitCompletion is unconditionally cleared by PlayWithPttAsync's own finally
        // regardless of whether the un-key succeeded, so without this fourth flag a rig this class
        // already logged Critical about would be silently walked away from here). All four get the same
        // best-effort, bounded, swallowed un-key -- a failed shutdown PTT-off must not prevent the rest
        // of teardown from completing, but it is now logged at Critical (inside UnkeyForCleanupAsync),
        // not swallowed silently.
        //
        // Round-6 finding: this reads _keyedTransmitCount, NOT _keyedTransmitCompletion's nullness --
        // see the count field's own doc comment for why nullness alone goes blind when two
        // PlayWithPttAsync calls overlap and the newer one's publish overwrites the older one's TCS.
        if (_pttCoordinator.BelievesKeyedOrInFlight())
        {
            // pttKeyedOnRealRig: true, and NOT a re-read of RigId (blocker 2's whole point). Every one
            // of the four states above is only reachable via a SetPttAsync issued against a
            // non-"none" RigId -- SetPttLockAsync only sets _pttLocked AFTER a successful set, the
            // null-object backend always throws, and _pttLeftKeyedByCall/_keyedTransmitCompletion are
            // only published when RigId was real at key time. A failure here is therefore always a
            // genuinely stuck-keyed rig, never the benign no-radio case.
            //
            // Round-18 finding 6: also now guarded -- see the comment on this method's own first step
            // above for why (same shape, same reasoning). UnkeyForCleanupAsync itself does not throw
            // by design (TryUnkeyPttAsync's own doc comment), so this guards only the logging calls
            // inside it, but that is exactly the realistic failure source identified above.
            //
            // Round-24 finding (nit, documentation-only): this backstop's own SetPttAsync(false) queues
            // FIFO behind an abandoned in-flight command on the backend's single request gate if one is
            // still outstanding (the same trade-off documented at PlayWithPttAsync's urgent un-key and
            // its retry) -- it can burn its whole CleanupTimeout budget waiting behind that queued
            // command without ever reaching the rig, and this method still returns once the budget
            // expires. Presented here as the reliable last-resort step; it is best-effort like every
            // other step in this method, not a guarantee the command reaches real hardware before the
            // host's own teardown bound expires.
            try
            {
                await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "UnkeyForCleanup (dispose backstop)", ex));
            }
        }

        // Piece C1: an in-progress recording must be finalized (its buffered chunks written to disk)
        // before this method returns, or the whole in-RAM buffer is silently discarded with no file
        // ever written -- best-effort/swallowed here, same as every other step in this method,
        // matching StopRecordingAsync's own doc comment for why THAT call propagates instead. Placed
        // AFTER the _disposed=true/MemoryBarrier fence above (does not read _disposed, so ordering
        // relative to it doesn't matter) and does not need to run before the PTT backstop above --
        // recording touches no PTT/radio state.
        try
        {
            await FinalizeRecordingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "FinalizeRecording (dispose)", ex));
        }

        // Round-17 finding: these three teardown steps used to run with no guard at all -- a throw
        // from any one (e.g. StopReceivingAsync's own _decoder.ResetAgc() call, which sits outside
        // its own internal try, or a throwing Waterfall.Dispose()) aborted DisposeAsync mid-way,
        // skipping whatever came after and leaking that resource. Matches MiniAudioEngine.DisposeAsync's
        // own established fix for the identical shape (its own comment: "each lifecycle now gets its
        // own try/finally so a failure in one never prevents the other") -- each step here now gets
        // its own try/catch instead, logged and swallowed, so teardown always reaches every step.
        //
        // Piece C2 note: DecodeFromFileAsync holds _rxTransitionGate for its whole decode, so a
        // Dispose racing an in-progress file decode will burn this call's full _cleanupTimeout (5s)
        // waiting on that gate -- bounded and expected, not a bug: the file-decode loop's own
        // per-chunk _disposed check (set true at this method's very first line) makes it bail out and
        // release the gate well within that budget.
        try
        {
            await StopReceivingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "StopReceiving (dispose)", ex));
        }

        try
        {
            if (Waterfall is IDisposable disposableWaterfall)
            {
                disposableWaterfall.Dispose();
            }
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "Waterfall.Dispose", ex));
        }

        // RX buffer subsystem Phase 7 (disposal-chain sub-piece): _decoder is ISstvDecoder-typed, not
        // IDisposable itself (RestartableSstvDecoder implements it, but ISstvDecoder deliberately
        // doesn't extend it -- avoids widening that interface's surface for one production
        // implementation's own resource-cleanup need), same duck-typed pattern as the Waterfall check
        // above. Still placed AFTER StopReceivingAsync so no in-flight PushSamples call can race the
        // decoder's own disposal.
        try
        {
            if (_decoder is IDisposable disposableDecoder)
            {
                disposableDecoder.Dispose();
            }
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "Decoder.Dispose", ex));
        }
    }

    /// <summary>Blocker 3's wait. <c>TxControlsPaneViewModel.Dispose()</c> cancels an in-flight
    /// transmit's token WITHOUT awaiting the transmit task, so on window-close-during-TX the
    /// transmit's own <c>finally</c>/un-key can still be running while DI teardown proceeds. Bounded
    /// (see <see cref="_inFlightKeyedTransmitWait"/>'s sizing against Program.cs's own host-teardown
    /// bound) and never throws: whether the wait succeeds or times out, DisposeAsync's own backstop
    /// un-key runs next either way, and a redundant un-key is documented-harmless on every shipped
    /// backend (see SetPttLockAsync's own doc comment).
    ///
    /// <b>Round-6 finding: this wait targets only the most recently published
    /// <c>PttSafetyCoordinator.PendingKeyedTransmit</c>, best-effort</b> -- if two <see cref="PlayWithPttAsync"/>
    /// calls overlap, an older call's own TCS can be overwritten by a newer one's publish (see
    /// that class's own doc comment), so this method has nothing to await for
    /// that older call even though it may still be genuinely keyed. Not fixed here -- DisposeAsync's own
    /// caller reads the coordinator's own in-flight count (not this field's nullness) for its "is anything
    /// still in flight" decision, so the fallback backstop un-key below still fires correctly even when
    /// this wait has nothing to observe; only the WAIT itself (letting the older call finish on its own
    /// terms first) is best-effort, not the safety guarantee.</summary>
    private async Task AwaitInFlightKeyedTransmitAsync()
    {
        var pending = _pttCoordinator.PendingKeyedTransmit;
        if (pending is null)
        {
            return;
        }

        SafeLog(() => Log.WaitingForKeyedTransmitAtShutdown(_logger));
        // Round-14 nit: same uncancelled-Task.Delay leak as StopPlaybackWithWatchdogAsync's own --
        // this runs once per DisposeAsync, so bounded, but closed the same way for consistency.
        using var waitCts = new CancellationTokenSource();
        var completed = await Task.WhenAny(pending.Task, Task.Delay(_inFlightKeyedTransmitWait, waitCts.Token)).ConfigureAwait(false);
        if (completed == pending.Task)
        {
            waitCts.Cancel();
        }
        else
        {
            SafeLog(() => Log.KeyedTransmitCleanupWaitTimedOut(_logger, _inFlightKeyedTransmitWait));
        }
    }

    /// <summary><paramref name="totalSamplesEstimate"/> is <see langword="null"/> for
    /// <see cref="TuneAsync"/> (no <see cref="TransmitProgressChanged"/> reporting for a tone) and a
    /// real value for <see cref="TransmitAsync"/> (spec/18-path-to-1.0.md Medium item). The running
    /// sample counter is a local, not a field -- nothing must dangle across separate calls.</summary>
    // T1-3 (production_audit.md): samples is IAsyncEnumerable<ReadOnlyMemory<float>> (batched) --
    // consumed via an outer loop over batches (few awaits) wrapping an inner, synchronous loop over
    // each batch's own samples (unchanged from before: still one MoveNextAsync per SAMPLE would have
    // been needed to get the per-sample gain multiply either way, so the win here is entirely in the
    // OUTER loop's own await count, not a removed inner loop). chunkSize/buffer/count/gain-refresh
    // cadence/ReportTransmitProgress cadence are ALL UNCHANGED from before this change, still driven
    // entirely by this method's OWN chunkSize constant -- completely decoupled from whatever batch
    // size the producer happens to use internally, deliberately, so this method's own observable
    // behavior (progress-report frequency, gain-refresh frequency) is provably unaffected by T1-3.
    private async Task PumpToPlaybackAsync(IAsyncEnumerable<ReadOnlyMemory<float>> samples, int sampleRate, long? totalSamplesEstimate, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;
        var samplesEnqueued = 0L;

        // Re-read once per chunk (not once for the whole call, the older behavior) -- see
        // _liveTxGain's own doc comment. A per-sample volatile read would be needlessly expensive on
        // this hot loop; re-reading every 4096 samples (~0.1-0.4s at typical SSTV sample rates) is
        // well within "immediate" for a human dragging a slider while watching a power meter.
        // Auditor-caught: the chunk-boundary term above is not the WHOLE slider-to-air latency --
        // gain is applied at enqueue time, and whatever is already sitting in MiniAudioPlaybackSession's
        // own playback ring (16384 frames by default, StartPlaybackAsync's periodSizeInFrames/periods
        // args) still plays at the OLD gain regardless. That adds up to ~0.3s more at Tune's fixed
        // 48kHz (the primary workflow this exists for) and up to ~1.5s more mid-image at a real
        // 11025Hz TX sample rate -- still usable for "drag Pwr, watch the meter," just not as tight
        // as the chunk-boundary number alone implies.
        var gain = _liveTxGain;

        await foreach (var chunk in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            // Plan-review finding: chunk.Span is deliberately NOT hoisted to a local outside this
            // inner loop -- the loop body contains an await (EnqueueAllAsync below), and a
            // ReadOnlySpan<float> local can't live across one (CS4012/4013, the same constraint
            // AnalogFmSstvEncoder.EncodeBatchedAsyncCore's own doc comment already documents for
            // itself). `chunk.Span[i]` inline as a transient rvalue is correct.
            for (var i = 0; i < chunk.Length; i++)
            {
                buffer[count++] = chunk.Span[i] * gain;
                if (count == chunkSize)
                {
                    await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    samplesEnqueued += count;
                    ReportTransmitProgress(samplesEnqueued, totalSamplesEstimate, sampleRate);
                    count = 0;
                    gain = _liveTxGain;
                }
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
                SafeLog(() => Log.TransmitProgressHandlerFailed(_logger, count, ex));
            }
        }
    }

    private async Task EnqueueAllAsync(ReadOnlyMemory<float> chunk, CancellationToken ct)
    {
        var offset = 0;

        // Round-13 finding: null while samples are flowing, set to the moment the FIRST consecutive
        // zero-accepted attempt happens -- see PlaybackStallTimeout's own doc comment for the failure
        // this closes. Reset to null on every successful accept, so a healthy device that only
        // legitimately backpressures briefly (buffer momentarily full, draining normally) never trips
        // this -- only a stretch of ZERO progress lasting the full timeout does.
        long? stallStartMs = null;

        while (offset < chunk.Length)
        {
            var accepted = _audioEngine.EnqueuePlaybackSamples(chunk[offset..]);
            if (accepted == 0)
            {
                stallStartMs ??= Environment.TickCount64;
                if (Environment.TickCount64 - stallStartMs.Value >= _playbackStallTimeout.TotalMilliseconds)
                {
                    throw new TimeoutException(
                        $"Playback device accepted no samples for {_playbackStallTimeout} -- assuming it is wedged.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
                continue;
            }

            stallStartMs = null;
            offset += accepted;
        }
    }

    /// <summary>User-reported fix, round 3 (2026-08-23): a configured-but-missing device (unplugged,
    /// uninstalled, or churned with no name match either) now falls all the way through to
    /// <see cref="TryResolveDeviceAsync"/>'s own backend-default lookup instead of returning
    /// <see langword="null"/> for that case specifically -- see that method's own doc comment. This
    /// means <see langword="null"/> ONLY ever means "no device available at all," a single, simpler
    /// error case (was two, "nothing configured + no default" vs "configured device not found" --
    /// both now collapse into this one, since TryResolveDeviceAsync already tried everything before
    /// giving up).</summary>
    private async Task<AudioDeviceInfo> ResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var device = await TryResolveDeviceAsync(forCapture, ct, persistIfResolvedIndirectly: true).ConfigureAwait(false);
        if (device is null)
        {
            var kind = forCapture ? "capture" : "playback";
            // Tier B audit finding: SafeLog-wrapped, same as every other logging call in this file's
            // cleanup/device-resolution paths (see SafeLog's own doc comment) -- a throwing logging
            // provider here must not substitute a generic logging exception for the actionable
            // InvalidOperationException message this method is about to throw anyway.
            SafeLog(() => Log.NoDeviceConfigured(_logger, kind));
            throw new InvalidOperationException($"No {kind} audio device configured, and no default {kind} device is available -- connect one, or set one in Options before starting a session.");
        }

        return device;
    }

    /// <summary>Non-throwing counterpart to <see cref="ResolveDeviceAsync"/>, extracted from it (not
    /// duplicated) so <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s passive readout use and
    /// <see cref="ResolveDeviceAsync"/>'s action-that-should-fail-loudly use share one lookup.
    /// <see langword="null"/> means "no device available at all" -- neither an exact id match, a
    /// same-name recovery match, nor a backend-reported default exists (spec/18-path-to-1.0.md
    /// Critical item 1 / item 8's genuinely rare case, e.g. a headless machine with no audio
    /// hardware). Every other case recovers to SOME real device rather than surfacing "not found."
    /// <para>User-reported fix (2026-08-23): an id mismatch first tries recovering the SAME device by
    /// its last-known <see cref="AudioDeviceSettings.CaptureDeviceName"/>/
    /// <see cref="AudioDeviceSettings.PlaybackDeviceName"/> -- see that field's own doc comment for
    /// the real, OS-agnostic device-id-churn scenario this closes (observed live: a PipeWire USB
    /// capture node re-created under a new id after a mute toggle, same physical device, same
    /// name).</para>
    /// <para>User-reported fix, round 3, same day (explicit product decision, overriding this
    /// method's own prior "surface a missing configured device, don't silently substitute"
    /// stance): "if in the file there is an RX/TX device that is not currently attached to the
    /// computer, just set it to the OS defaults." A configured device that matches NEITHER by id NOR
    /// by name (genuinely unplugged/uninstalled, not just churned) now falls through to the same
    /// backend-default lookup "nothing configured" already used, instead of returning
    /// <see langword="null"/> and making <see cref="ResolveDeviceAsync"/> throw.</para>
    /// <para>User-reported fix, round 2 (2026-08-23): <paramref name="persistIfResolvedIndirectly"/>
    /// -- "load from the file; if nothing in the file, use the OS default; SAVE it to the file; use
    /// it in the program" (verbatim user spec). Before this, a "nothing configured" resolution
    /// re-ran the SAME backend-default lookup on every single call with nothing ever written back,
    /// so a later OS-level default change (a different mic becoming the system default) silently
    /// changed what this app used too, with no durable record of what was actually selected. Passed
    /// <see langword="true"/> only from <see cref="ResolveDeviceAsync"/> (the action paths --
    /// <see cref="StartReceivingAsync"/>/<see cref="TransmitAsync"/>/<see cref="TuneAsync"/> --
    /// where actually committing to a device is appropriate); left <see langword="false"/> (its
    /// default) for <see cref="GetConfiguredCaptureDeviceNameAsync"/>/
    /// <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s passive display-only reads, which must
    /// stay read-only and not write to disk just because the Options dialog (or this app's own
    /// header chip) happened to ask "what would be used right now."</para></summary>
    private async Task<AudioDeviceInfo?> TryResolveDeviceAsync(bool forCapture, CancellationToken ct, bool persistIfResolvedIndirectly = false)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;
        var deviceName = forCapture ? settings.CaptureDeviceName : settings.PlaybackDeviceName;

        await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
        var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;

        if (deviceId is not null)
        {
            var exactMatch = devices.FirstOrDefault(d => d.Id == deviceId);
            if (exactMatch is not null)
            {
                return exactMatch;
            }

            if (deviceName is not null)
            {
                var nameMatch = devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.Ordinal));
                if (nameMatch is not null)
                {
                    SafeLog(() => Log.RecoveredDeviceByName(_logger, forCapture ? "capture" : "playback", deviceId, nameMatch.Id, nameMatch.Name));
                    if (persistIfResolvedIndirectly)
                    {
                        await PersistResolvedDeviceAsync(forCapture, nameMatch, ct).ConfigureAwait(false);
                    }

                    return nameMatch;
                }
            }

            SafeLog(() => Log.ConfiguredDeviceNotFound(_logger, forCapture ? "capture" : "playback", deviceId, devices.Count));
        }

        var fallback = devices.FirstOrDefault(d => d.IsDefault);
        if (fallback is not null)
        {
            // Tier B audit finding: this sat unwrapped on the SUCCESS path -- a throwing logging
            // provider here (this file's own stated threat model, see SafeLog's own doc comment)
            // would throw out of TryResolveDeviceAsync AFTER a device was already successfully
            // resolved into `fallback`, taking down every caller: StartReceivingAsync (RX dead),
            // TransmitAsync/TuneAsync (TX dead), and both GetConfigured*DeviceNameAsync readouts.
            // Same shape round 22 already fixed for Log.RxStarted/Log.RxStopped ("a throwing
            // provider still propagated out of this method after capture had genuinely started",
            // see that round's own comment near RxStarted's call site) -- the identical
            // "one method has the guard, a near-identical sibling doesn't" pattern, just on the
            // device-resolution path instead of the RX-start path.
            SafeLog(() => Log.UsingDefaultDevice(_logger, forCapture ? "capture" : "playback", fallback.Name));
            if (persistIfResolvedIndirectly)
            {
                await PersistResolvedDeviceAsync(forCapture, fallback, ct).ConfigureAwait(false);
            }
        }

        return fallback;
    }

    /// <summary>Writes the just-resolved device's id+name back into <see cref="AudioDeviceSettings"/>
    /// -- see <see cref="TryResolveDeviceAsync"/>'s own <c>persistIfResolvedIndirectly</c> doc
    /// comment for when/why this runs. T0-2: read-modify-write via <see cref="ISettingsStore.UpdateAsync"/>,
    /// atomic against a concurrent Options Save writing a different section -- no longer just
    /// "reload right before writing" to minimize the window, the window is closed. Swallow-and-log
    /// on failure, never propagate: a failed opportunistic persist must not turn an
    /// otherwise-successful device resolution into a failed <see cref="StartReceivingAsync"/>/
    /// <see cref="TransmitAsync"/> call.</summary>
    private async Task PersistResolvedDeviceAsync(bool forCapture, AudioDeviceInfo device, CancellationToken ct)
    {
        try
        {
            await _settingsStore.UpdateAsync(appSettings =>
            {
                var previousAudio = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
                var updatedAudio = forCapture
                    ? previousAudio with { CaptureDeviceId = device.Id, CaptureDeviceName = device.Name }
                    : previousAudio with { PlaybackDeviceId = device.Id, PlaybackDeviceName = device.Name };
                return appSettings.WithSection(AudioDeviceSettings.SectionKey, updatedAudio, AudioSettingsJsonContext.Default.AudioDeviceSettings);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.PersistResolvedDeviceFailed(_logger, forCapture ? "capture" : "playback", device.Id, ex));
        }
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading settings section '{SectionKey}' failed; falling back to defaults")]
        public static partial void SettingsSectionReadFailed(ILogger logger, string sectionKey, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sound-file station ID '{Path}' could not be read; transmitting no sound-file ID")]
        public static partial void SoundFileIdReadFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sound-file station ID '{Path}' does not exist; transmitting no sound-file ID")]
        public static partial void SoundFileIdMissing(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sound-file station ID '{Path}' exceeds the {MaxBytes} byte size cap; transmitting no sound-file ID")]
        public static partial void SoundFileIdTooLarge(ILogger logger, string path, long maxBytes);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sound-file station ID '{Path}' has an unplayable header; transmitting no sound-file ID")]
        public static partial void SoundFileIdUnplayableHeader(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Error, Message = "Waterfall PushSamples threw ({Count} occurrences so far)")]
        public static partial void WaterfallPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Recording sink threw ({Count} occurrences so far)")]
        public static partial void RecordingPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Recording started: {Path}")]
        public static partial void RecordingStarted(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Information, Message = "Recording saved: {Path} ({SampleCount} samples)")]
        public static partial void RecordingSaved(ILogger logger, string path, int sampleCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manual ReSync requested")]
        public static partial void ReSyncRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX notch {Enabled} (frequency {FrequencyHz} Hz)")]
        public static partial void NotchRequested(ILogger logger, bool enabled, double? frequencyHz);

        [LoggerMessage(Level = LogLevel.Information, Message = "PLL tuning requested: VcoGain={VcoGain}, LoopOrder={LoopOrder}, LoopCutoffHz={LoopCutoffHz}, OutputOrder={OutputOrder}, OutputCutoffHz={OutputCutoffHz}")]
        public static partial void PllTuningRequested(ILogger logger, double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz);

        [LoggerMessage(Level = LogLevel.Information, Message = "Zero-crossing tuning requested: SmoothingMode={SmoothingMode}, OutputOrder={OutputOrder}, OutputCutoffHz={OutputCutoffHz}, SmoothingFrequencyHz={SmoothingFrequencyHz}")]
        public static partial void ZeroCrossingTuningRequested(ILogger logger, ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX mode lock changed to {ModeId}")]
        public static partial void ModeLockChanged(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Decoder Trace capture armed ({Size} samples)")]
        public static partial void ScopeCaptureArmed(ILogger logger, int size);

        [LoggerMessage(Level = LogLevel.Information, Message = "Squelch level requested: {Level}")]
        public static partial void SenseLevelRequested(ILogger logger, int level);

        [LoggerMessage(Level = LogLevel.Information, Message = "Auto-Sync enabled requested: {Enabled}")]
        public static partial void AutoSyncEnabledRequested(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Information, Message = "Auto-Stop enabled requested: {Enabled}")]
        public static partial void AutoStopEnabledRequested(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Information, Message = "Auto-Slant enabled requested: {Enabled}")]
        public static partial void AutoSlantEnabledRequested(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Information, Message = "Reception SNR measurement enabled requested: {Enabled}")]
        public static partial void SnrMeasurementEnabledRequested(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Information, Message = "AFC enabled requested: {Enabled}")]
        public static partial void AfcEnabledRequested(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Information, Message = "Sync-Restart enabled requested: {Enabled}")]
        public static partial void SyncRestartEnabledRequested(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Information, Message = "Reconfiguration requested: RxBpfPreset={RxBpfPreset}, DemodType={DemodType}, RxBufferMode={RxBufferMode}")]
        public static partial void ReconfigurationRequested(ILogger logger, RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX BPF preset requested: {RxBpfPreset}")]
        public static partial void RxBpfPresetRequested(ILogger logger, RxBpfPreset rxBpfPreset);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A queued reconfiguration request was rejected -- the decoder kept its previous RxBpfPreset/DemodType/RxBufferMode")]
        public static partial void ReconfigurationRejected(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Sample rate change requested: {SampleRate}Hz")]
        public static partial void SampleRateChangeRequested(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Information, Message = "Sample rate change applied: {SampleRate}Hz")]
        public static partial void SampleRateChangeApplied(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sample rate change to {SampleRate}Hz deferred -- a recording is in progress")]
        public static partial void SampleRateChangeDeferred(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sample rate change to {SampleRate}Hz rejected -- the decoder kept its previous rate")]
        public static partial void SampleRateChangeRejected(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Error, Message = "Sample rate change failed -- the decoder was busy after an abandoned capture stop")]
        public static partial void SampleRateChangeBusy(ILogger logger, Exception? restartFailure);

        [LoggerMessage(Level = LogLevel.Information, Message = "Capture device change requested: {DeviceId}")]
        public static partial void CaptureDeviceChangeRequested(ILogger logger, string? deviceId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Capture device change applied: {DeviceId}")]
        public static partial void CaptureDeviceChangeApplied(ILogger logger, string? deviceId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture device change to {DeviceId} deferred -- a recording is in progress")]
        public static partial void CaptureDeviceChangeDeferred(ILogger logger, string? deviceId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture device change to {DeviceId} rejected -- rolled back to the previous device")]
        public static partial void CaptureDeviceChangeRejected(ILogger logger, string? deviceId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manual Correct Slant requested")]
        public static partial void CorrectSlantRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Reception aborted by user")]
        public static partial void ReceptionAborted(ILogger logger);

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

        [LoggerMessage(Level = LogLevel.Debug, Message = "TX Pwr set to {Percent}%")]
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

        [LoggerMessage(Level = LogLevel.Debug, Message = "PTT un-key succeeded, but a newer key command completed while it was in flight -- leaving that call's own \"still keyed\" state intact instead of clearing it")]
        public static partial void PttUnkeyRaceLostToNewerKey(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping \"left keyed\" state write -- a confirmed PTT un-key completed elsewhere while this call was finishing up, so the rig is already known to be off")]
        public static partial void PttLeftKeyedSkippedConfirmedUnkeyRace(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping cleanup PTT un-key attempt -- a confirmed un-key completed elsewhere while this call was finishing up, so the rig is already known to be off")]
        public static partial void PttCleanupUnkeySkippedConfirmedElsewhere(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT lock {Locked}")]
        public static partial void PttLockChanged(ILogger logger, bool locked);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback cancelled")]
        public static partial void PlaybackCancelled(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback aborted -- the session was disposed while this call was still resolving its device")]
        public static partial void PlaybackAbortedByDispose(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Playback failed")]
        public static partial void PlaybackFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup step '{StepName}' failed")]
        public static partial void CleanupStepFailed(ILogger logger, string stepName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "StopPlayback did not finish within {Budget}; continuing to the PTT un-key without waiting (the stop is still running in the background)")]
        public static partial void PlaybackStopWatchdogFired(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Warning, Message = "StopCapture did not finish within {Budget}; treating RX as stopped without waiting (the stop is still running in the background)")]
        public static partial void CaptureStopWatchdogFired(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Critical, Message = "PTT MAY STILL BE KEYED -- the un-key command failed on a rig this session actually keyed. Check the radio and un-key it manually.")]
        public static partial void PttStillKeyedAfterFailedUnkey(ILogger logger);

        [LoggerMessage(Level = LogLevel.Critical, Message = "PTT MAY HAVE BEEN KEYED -- the key command failed, but may have physically keyed the rig before failing. Check the radio and un-key it manually if needed.")]
        public static partial void PttKeyCommandFailedMayHaveKeyed(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown is waiting for an in-flight keyed transmit to finish un-keying PTT")]
        public static partial void WaitingForKeyedTransmitAtShutdown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "In-flight keyed transmit did not finish its own PTT un-key within {Budget}; forcing an un-key from shutdown")]
        public static partial void KeyedTransmitCleanupWaitTimedOut(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Warning, Message = "No {Kind} audio device configured")]
        public static partial void NoDeviceConfigured(ILogger logger, string kind);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configured {Kind} device '{DeviceId}' not found among {AvailableCount} available devices")]
        public static partial void ConfiguredDeviceNotFound(ILogger logger, string kind, string deviceId, int availableCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Configured {Kind} device id '{OldDeviceId}' not found, but recovered it by name as '{NewDeviceId}' ({DeviceName})")]
        public static partial void RecoveredDeviceByName(ILogger logger, string kind, string oldDeviceId, string newDeviceId, string deviceName);

        [LoggerMessage(Level = LogLevel.Information, Message = "No {Kind} device configured -- using backend-reported default '{DeviceName}'")]
        public static partial void UsingDefaultDevice(ILogger logger, string kind, string deviceName);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting resolved {Kind} device '{DeviceId}' back to settings failed -- will re-resolve the same way next time")]
        public static partial void PersistResolvedDeviceFailed(ILogger logger, string kind, string deviceId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning raised (approaching automatic restart threshold)")]
        public static partial void MaintenanceWarningRaised(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning cleared")]
        public static partial void MaintenanceWarningCleared(ILogger logger);

        // T1-4 (production_audit.md): OnDecoderRestarted fired DecoderInstanceReplaced with zero
        // logging -- a periodic decoder restart (avoids an int-overflow) happened with no trace at
        // all when no maintenance warning had been active for it (the common case), unlike its
        // sibling maintenance events above.
        [LoggerMessage(Level = LogLevel.Information, Message = "Decoder instance restarted")]
        public static partial void DecoderRestarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RX force-stopped for required maintenance restart")]
        public static partial void MaintenanceCriticalStop(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred a maintenance-triggered RX stop because _rxTransitionGate was already held by another caller")]
        public static partial void RxTransitionGateContendedDuringMaintenanceStop(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance handler '{HandlerName}' threw")]
        public static partial void MaintenanceHandlerFailed(ILogger logger, string handlerName, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "TransmitProgressChanged handler threw ({Count} occurrence(s) so far this session)")]
        public static partial void TransmitProgressHandlerFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "CapturePausedForTransmitChanged handler threw (paused={Paused})")]
        public static partial void CapturePausedHandlerFailed(ILogger logger, bool paused, Exception ex);
    }
}
