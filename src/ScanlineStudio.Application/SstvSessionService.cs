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

    /// <summary>Incremented inside the settings-store mutate of every Pwr write/resync, so it follows
    /// commit order. See <see cref="ISstvSessionService.TxVolumePercentChanged"/>.</summary>
    private long _txVolumeVersion;

    private readonly object _liveTxGainGate = new();

    /// <summary>Version of the last write to <see cref="_liveTxGain"/>, guarded by
    /// <see cref="_liveTxGainGate"/>.</summary>
    private long _liveTxGainVersion;

    public event Action<TxVolumeChange>? TxVolumePercentChanged;
    // Ultracode audit finding #34's OnDecoderRestartCriticallyOverdue can now call StopReceivingAsync
    // (which writes this) from the audio drain thread, not just from a UI-thread-initiated
    // StartReceivingAsync/StopReceivingAsync call -- volatile because this field's own reads/writes now
    // span more than one caller thread with no lock between them.
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

    // Volatile like _isReceiving above -- this one is written from OnDecoderRestarted/OnDecoderRestartCriticallyOverdue (see the
    // latter's own doc comment: reachable from the audio drain thread, not just a UI-thread caller)
    // and read from OnDecoderRestarted, with no lock between them.
    private volatile bool _maintenanceWarningActive;

    // The PTT keying/un-keying state machine -- _keyedTransmitCompletion, _pttUnkeyFailedOnRealRig,
    // _pttKeyEpoch, _pttUnkeyEpoch, _keyedTransmitCount -- lives in PttSafetyCoordinator (see that
    // class's own doc comment for the field-ownership/lost-update-race reasoning). This class keeps every await/Log.* call
    // (all the orchestration); the coordinator owns only the field reads/writes that decide what to
    // record. _disposed and _transmitInFlight deliberately stay here -- see the coordinator's own
    // doc comment for why.
    private readonly PttSafetyCoordinator _pttCoordinator = new();

    // Tier A Batch 3 chunk 3a round-2 finding: DisposeAsync's backstop only guards
    // _keyedTransmitCompletion state published BEFORE it runs -- a PlayWithPttAsync call that hasn't
    // reached its own publish point yet (e.g. RadioStatusViewModel.TuneAsync's real production call
    // passes CancellationToken.None, so nothing can ever cancel it) could previously key PTT AFTER
    // DisposeAsync had already run its backstop and returned, with no shutdown safety net left to catch
    // it. Read by PlayWithPttAsync's own thread, written by whatever thread calls DisposeAsync, with no
    // lock between them.
    private volatile bool _disposed;

    // Round-12 finding: two overlapping PlayWithPttAsync calls (TransmitAsync racing TuneAsync --
    // RadioStatusViewModel's own Tune command has no TX-in-progress CanExecute gate, so clicking Tune
    // during a TransmitAsync produces two live calls) used to let the SECOND call unconditionally re-key and call
    // StartPlaybackAsync, which throws "already started" against the FIRST call's own live playback
    // session -- that throw is caught generically, classified abnormalTermination, and its finally's
    // urgent un-key runs immediately, dropping the FIRST call's carrier mid-frame, followed by
    // StopPlaybackWithWatchdogAsync disposing the session out from under it. This is a DIFFERENT
    // failure mode than the leaked-keyed-transmitter class every other field on this class defends
    // against: PTT ends up correctly OFF, but a genuinely in-flight, correctly-behaving transmission
    // gets silently killed by an unrelated second call. A single-flight guard (CompareExchange at
    // PlayWithPttAsync's very entry, before ANYTHING else -- no RX pause, no device resolution, no PTT
    // touched) rejects the second call outright instead. Deliberately a SEPARATE field from
    // _keyedTransmitCount: that one is a different, narrower concern (shutdown-wait visibility, not
    // single-flight exclusivity) and is never incremented at all when RigId == "none" (the "no radio configured" case still needs the SAME playback-session
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

    /// <summary>Tier B audit finding (Area 3): serializes <see cref="StartReceivingAsync"/> and
    /// <see cref="StopReceivingAsync"/> against each other -- without this, both methods' own
    /// `_isReceiving` check-then-act (read at entry, published only after several awaits) had no gate
    /// between them.
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
    /// Deliberately never disposed by <see cref="DisposeAsync"/>: <see cref="StopReceivingAsync"/>
    /// must stay callable post-dispose, reaching its own idempotency/disposed check on a still-usable
    /// primitive rather than throwing from the primitive itself.</summary>
    private readonly SemaphoreSlim _rxTransitionGate = new(1, 1);

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
    /// samples; this fires at most twice per transmission -- so a plain per-occurrence Error (matching the
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

    /// <summary>1x1 placeholder for the (unlikely in practice) case where the self-test's own decoder
    /// never fired a single <see cref="ISstvDecoder.LineDecoded"/> event -- same shape as
    /// <c>ReceivedImageBuffer</c>'s own <c>EmptyImage</c>, a distinct instance since that class isn't
    /// reachable from here (this self-test never touches it, by design).</summary>
    private static readonly IImageSource EmptySelfTestImage = new ArrayImageSource(1, 1, [new Rgb24(0, 0, 0)]);

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

    // Round-18 finding 3 (round-17's own deferral (b), now fixed): a generous backstop, not a UX
    // limit -- the default UI value is 5s and legitimate antenna-tuning use is on that order. Exists
    // only to bound the worst case of a corrupted/mistyped duration reaching TuneAsync below with no
    // caller-side cancellation available (see that method's own comment).
    private static readonly TimeSpan MaxTuneDuration = TimeSpan.FromMinutes(5);

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

        // A rig can be physically keyed at shutdown for TWO distinct reasons: an in-flight transmit
        // whose own un-key never completed (_keyedTransmitCount still nonzero after the bounded wait
        // above), and a completed transmit whose own un-key attempt FAILED (_pttUnkeyFailedOnRealRig --
        // _keyedTransmitCompletion is unconditionally cleared by PlayWithPttAsync's own finally
        // regardless of whether the un-key succeeded, so without this flag a rig this class already
        // logged Critical about would be silently walked away from here). Both get the same
        // best-effort, bounded, swallowed un-key -- a failed shutdown PTT-off must not prevent the rest
        // of teardown from completing, but it is logged at Critical (inside UnkeyForCleanupAsync), not
        // swallowed silently.
        //
        // Round-6 finding: this reads the coordinator's in-flight count, NOT _keyedTransmitCompletion's
        // nullness -- a newer publish overwrites an older registration's TCS and its release then nulls
        // the field while the older one is still keyed; the count survives that overwrite.
        if (_pttCoordinator.BelievesKeyedOrInFlight())
        {
            // pttKeyedOnRealRig: true, and NOT a re-read of RigId (blocker 2's whole point). Both
            // states above are only reachable via a SetPttAsync issued against a non-"none" RigId --
            // _keyedTransmitCompletion is only published when RigId was real at key time, and
            // _pttUnkeyFailedOnRealRig is only latched by an unkey whose caller believed it had keyed a
            // real rig. A failure here is therefore always a
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

        [LoggerMessage(Level = LogLevel.Debug, Message = "TX Pwr resynced from settings: {Percent}%")]
        public static partial void TxVolumeResynced(ILogger logger, int percent);

        [LoggerMessage(Level = LogLevel.Error, Message = "A TxVolumePercentChanged subscriber threw")]
        public static partial void TxVolumeChangedSubscriberFailed(ILogger logger, Exception ex);

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

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping cleanup PTT un-key attempt -- a confirmed un-key completed elsewhere while this call was finishing up, so the rig is already known to be off")]
        public static partial void PttCleanupUnkeySkippedConfirmedElsewhere(ILogger logger);

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
