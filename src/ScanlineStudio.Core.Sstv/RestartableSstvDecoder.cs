using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Fixes ultracode audit finding #34 (AnalogFmSstvDecoder's absolute sample-index space is
/// `int`) by periodically discarding and
/// reconstructing the whole <see cref="AnalogFmSstvDecoder"/> object graph instead of widening every
/// affected field -- a fresh instance gives every field a correct starting value by construction, the
/// same effect as a user restarting the application (already the case today, since
/// <see cref="ISstvDecoder"/> is a DI singleton rebuilt fresh per process), but automatic and
/// in-process. A widen-every-field plan was drafted first and rejected by plan-readiness review: the
/// coordinate space escapes into <c>IScanlineDecoder</c>/<c>PixelSampleReader</c> and
/// <see cref="VisLockStateMachine"/> has its own internal unbounded counter -- a much larger blast
/// radius than enumerating fields in this one class.
///
/// <b>State machine</b> (evaluated on every <see cref="PushSamples"/> call, BEFORE forwarding the
/// incoming chunk to whichever inner instance ends up current -- so <see cref="AnalogFmSstvDecoder.IsIdle"/>
/// and the sample count always reflect settled state as of the end of the previous call, and a swap
/// never splits one chunk across old/new):
/// <list type="number">
/// <item>If the incoming chunk itself exceeds the safe ceiling, reject it without mutation. Otherwise,
/// if forwarding it would cross that ceiling, force a critical swap before forwarding the whole
/// chunk to the fresh decoder.</item>
/// <item>Else if `n &gt;= criticalThreshold` (regardless of idle): force the swap unconditionally.
/// These first two swap paths are the actual overflow-safety guarantee: neither waits for idle.
/// Self-clearing: the swap resets `n` back near zero.</item>
/// <item>Else if idle and `n &gt;= warningThreshold`: normal swap (the common path in real usage,
/// since real receiving has gaps).</item>
/// <item>Else if not idle and `n &gt;= warningThreshold`: raise <see cref="RestartOverdue"/> once
/// (guarded so it doesn't fire on every subsequent call for the rest of the window).</item>
/// </list>
/// This makes the safety swaps independent of the visibility layer -- even a bug in whatever consumes
/// <see cref="RestartOverdue"/>/<see cref="RestartCriticallyOverdue"/>
/// can't let the counter actually overflow.
///
/// <b>Locking</b>: the swap happens under <c>lock (_gate)</c>. All three maintenance events are raised
/// strictly AFTER releasing the lock, so a critical-path consumer can call back into
/// <see cref="ResetAgc"/> without contending with the swap. A round-3 plan review found
/// a rare shutdown-timing path where raising inside the lock could let a handler's continuation resume
/// on a different thread and then contend for `_gate` against the (still-lock-holding) original
/// thread -- a genuine deadlock. Raising outside the lock removes the whole class, since the swap has
/// already fully happened by the time any handler runs.</summary>
public sealed class RestartableSstvDecoder : ISstvDecoder, ISstvDecoderMaintenance, ISstvDecoderReconfiguration, IDisposable
{
    internal const int ProductionSampleRate = SstvSampleRate.Default;
    internal const long DefaultWarningThresholdSamples = 12L * 3600 * ProductionSampleRate;
    internal const long DefaultCriticalThresholdSamples = 13L * 3600 * ProductionSampleRate;

    internal readonly record struct RestartThresholds(
        long WarningThresholdSamples,
        long CriticalThresholdSamples,
        int MaximumSafeSampleIndex,
        long ProjectionReserveSamples);

    private readonly bool _afcEnabled;

    // NOT readonly (2026-08-27, restart-required-settings backlog item 1) -- same shape as
    // _stationIdDecodeEnabled below: both the seed for a freshly-(re)built inner (CreateInner) AND
    // the current live value (this wrapper's own field, kept in sync by each property's setter, NOT
    // forwarded-to from the inner decoder's own value -- see each property's own doc comment for why
    // that distinction matters for the getter contract).
    private bool _syncRestartEnabled;
    private bool _autoSyncEnabled;
    private bool _autoStopEnabled;
    private bool _autoSlantEnabled;

    // NOT readonly (user-reported 2026-08-27, "Squelch level" live control) -- same shape as
    // _stationIdDecodeEnabled below: both the seed for a freshly-(re)built inner AND the current
    // live value, kept in sync with _inner.SenseLevel by the constructor, property setter, and
    // CreateInner under _gate.
    private int _senseLevel;

    // NOT readonly (2026-08-27, restart-required-settings backlog item 2) -- unlike the four
    // booleans and SenseLevel above, these three have NO in-place mutation path on
    // AnalogFmSstvDecoder (each is read ONCE, at construction, by CreateInner below); a requested
    // change is only ever applied by DRAINING _pendingReconfiguration inside Swap, which reassigns
    // these fields immediately before calling CreateInner. Safe to leave un-`readonly`: only
    // CreateInner (under _gate) ever reads them, and this wrapper's own public getters (RxBpfPreset
    // below) forward to `_inner`'s value, never to these fields directly -- so there is no
    // torn-read/stale-read exposure from dropping `readonly` (round-3 plan-review nit).
    private DemodType _demodType;
    private RxBpfPreset _rxBpfPreset;
    private RxBufferMode _rxBufferMode;

    /// <summary>NOT drained anywhere but inside <see cref="Swap"/> -- see
    /// <see cref="RequestReconfiguration"/>'s own doc comment for the full contract (idle-gating,
    /// equality-guard, and the bounded-retry failure policy a persistent <c>CreateInner</c> failure
    /// needs, restart-required-settings backlog item 2, round-2/round-3 plan-review).</summary>
    private sealed record PendingReconfiguration(RxBpfPreset RxBpfPreset, DemodType DemodType, RxBufferMode RxBufferMode);

    private PendingReconfiguration? _pendingReconfiguration;

    private readonly int _sampleRate;
    private readonly long _warningThresholdSamples;
    private readonly long _criticalThresholdSamples;
    private readonly int _maximumSafeSampleIndex;
    private readonly Func<int, AnalogFmSstvDecoder>? _decoderFactoryForTests;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly object _gate = new();
    private int _pushActive;

    // NOT readonly, unlike every toggle above -- StationIdDecodeEnabled is deliberately
    // LIVE-settable (see ISstvDecoder.StationIdDecodeEnabled's own doc comment for why), so this is
    // both the "what to apply to a freshly-(re)built inner decoder" seed AND the current live value,
    // kept in sync with _inner.StationIdDecodeEnabled by the constructor, property setter, and
    // CreateInner under _gate.
    private bool _stationIdDecodeEnabled;

    // Un-stub-RX-tab Piece A: NOT readonly, same reasoning as _stationIdDecodeEnabled immediately
    // above -- unlike RequestReSync's own genuinely one-shot request (silently dropped if a restart
    // races it, see that method's own doc comment), the notch is persistent STATE that must survive
    // a restart, so it needs storing here too, not just forwarding to whichever inner is current.
    private bool _notchEnabled;
    private double _notchFrequencyHz = 2400.0; // matches CNotch::CNotch's own default (fir.cpp:271)

    // Un-stub-RX-tab Piece B: unlike notch state above, these two are NOT re-seeded per rebuild --
    // owned here for this wrapper's WHOLE lifetime and passed as the SAME instances into every
    // CreateInner call, so an in-progress capture survives a periodic restart instead of resetting.
    // See AnalogFmSstvDecoder's own constructor doc comment for the full reasoning (legacy's CScope
    // has no rebuild concept to begin with -- it just lives on CSSTVDEM for that object's whole
    // lifetime).
    private readonly ScopeCaptureBuffer _scopeCaptureChannel0 = new();
    private readonly ScopeCaptureBuffer _scopeCaptureChannel1 = new();

    private AnalogFmSstvDecoder _inner;
    private bool _warningRaised;

    /// <summary>See <see cref="ISstvDecoder.SampleRate"/>. Every inner decoder is validated against
    /// this immutable value before it can be installed.</summary>
    public int SampleRate => _sampleRate;

    public event Action<DecodedImageUpdate>? LineDecoded;
    public event Action<SstvModeDefinition>? ModeDetected;
    public event Action<SstvModeDefinition>? DecodeRestarted;

    /// <summary>See <see cref="ISstvDecoder.StationIdDecoded"/> -- forwarded from whichever inner
    /// instance is current, same subscribe/unsubscribe-on-swap pattern as <see cref="LineDecoded"/>/
    /// <see cref="ModeDetected"/>/<see cref="DecodeRestarted"/> above (Phase 5 of the CW-ID/FSK
    /// station-ID subsystem plan -- Phase 4 only wired the enable FLAG through this class;
    /// forwarding the event itself was explicitly left for this phase, not a gap in Phase 4).</summary>
    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    public event Action? RestartOverdue;
    public event Action? RestartCriticallyOverdue;
    public event Action? Restarted;
    public event Action? ReconfigurationRejected;

    /// <summary>Diagnostic-only: how many times the inner decoder has been swapped (initial
    /// construction does not count). Test infrastructure for pinning the self-clearing property.</summary>
    internal long RestartCountForTests { get; private set; }

    /// <summary>Diagnostic-only: whether the CURRENT inner instance is idle right now. Test
    /// infrastructure for proving a chunked push actually observed a non-idle decoder at some point
    /// (not just "no swap happened," which a single bulk push would satisfy vacuously without
    /// exercising anything).</summary>
    internal bool IsIdleForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.IsIdle;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.StationIdDecodeEnabled"/> directly -- NOT the wrapper's stored
    /// <see cref="_stationIdDecodeEnabled"/> field the public <see cref="StationIdDecodeEnabled"/>
    /// getter reads. Auditor round-2 finding: a test asserting only the public getter after a swap
    /// would pass even if <see cref="CreateInner"/> stopped applying the stored value to a freshly
    /// built inner entirely -- this exists so a test can prove the value actually reached the LIVE
    /// inner decoder post-swap, not just that the wrapper still remembers what it was told.</summary>
    internal bool InnerStationIdDecodeEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.StationIdDecodeEnabled;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.AutoSlantEnabled"/> directly -- same reasoning/shape as
    /// <see cref="InnerStationIdDecodeEnabledForTests"/> above (2026-08-27, restart-required-settings
    /// backlog item 1): proves a live change, and a re-seed across a periodic swap, actually reached
    /// the LIVE inner decoder, not just the wrapper's own stored field. AutoSyncEnabled/
    /// AutoStopEnabled/SyncRestartEnabled already have their own equivalent
    /// <c>InnerXForTests</c> accessors further below (pre-existing, forwarding to
    /// <c>AnalogFmSstvDecoder</c>'s own test-only <c>XForTests</c> readbacks of the same now-mutable
    /// fields) -- not duplicated here.</summary>
    internal bool InnerAutoSlantEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.AutoSlantEnabled;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.DemodTypeForTests"/> directly -- there is no public
    /// wrapper-level <c>DemodType</c> getter to compare against (see this class' own constructor,
    /// which follows <c>SenseLevel</c>'s "no read-back needed" shape, not <c>AutoSlantEnabled</c>'s
    /// exposed one). Auditor round-1 (Phase 3) finding: without this, a dropped `demodType` argument
    /// in <see cref="CreateInner"/> would silently revert a PLL/ZeroCrossing user to Hilbert after
    /// the periodic restart rebuild, with no test able to catch it -- same reasoning as
    /// <see cref="InnerStationIdDecodeEnabledForTests"/> above.</summary>
    internal DemodType InnerDemodTypeForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.DemodTypeForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.RxBpfPresetForTests"/> directly -- same reasoning/shape as
    /// <see cref="InnerDemodTypeForTests"/> above (RX BPF subsystem Phase 3, mirroring the demod-type
    /// subsystem's own round-1 finding: without this, a dropped `rxBpfPreset` argument in
    /// <see cref="CreateInner"/> would silently revert a user to Wide after the periodic restart
    /// rebuild, with no test able to catch it).</summary>
    internal RxBpfPreset InnerRxBpfPresetForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.RxBpfPresetForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.RxBufferModeForTests"/> directly -- same reasoning/shape as
    /// <see cref="InnerDemodTypeForTests"/>/<see cref="InnerRxBpfPresetForTests"/> above (RX buffer
    /// subsystem Phase 2: without this, a dropped `rxBufferMode` argument in <see cref="CreateInner"/>
    /// would silently revert a user to On after the periodic restart rebuild, with no test able to
    /// catch it).</summary>
    internal RxBufferMode InnerRxBufferModeForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.RxBufferModeForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.RxLineStagingBufferForTests"/> directly -- RX buffer subsystem
    /// Phase 7 (disposal-chain sub-piece), lets a test capture a pre-swap instance's scratch-file
    /// paths (via <c>RxDiskLineStagingBufferForTests</c>-style test-only properties on the concrete
    /// disk implementation) to later assert they were actually deleted once <see cref="Swap"/>
    /// disposes the outgoing decoder, same reasoning/shape as <see cref="InnerRxBufferModeForTests"/>
    /// above.</summary>
    internal IRxLineStagingBuffer? InnerRxLineStagingBufferForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.RxLineStagingBufferForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.RxBufferBaseTransmissionLineForTests"/> directly -- RX buffer
    /// subsystem Phase 8c, lets a wrapper-level test prove a replay pass actually ran (this field only
    /// ever moves off 0 inside <c>PerformReplay</c>'s own tail) with the same proof-positive precision
    /// the direct <see cref="AnalogFmSstvDecoder"/> tests use, instead of inferring it from a weaker
    /// public-surface signal like a raw <c>LineDecoded</c> event count. Same reasoning/shape as
    /// <see cref="InnerRxLineStagingBufferForTests"/> above.</summary>
    internal int InnerRxBufferBaseTransmissionLineForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.RxBufferBaseTransmissionLineForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.AfcEnabledForTests"/> directly -- same reasoning/shape as
    /// <see cref="InnerDemodTypeForTests"/> above (round-8 D2-audit finding: <see cref="CreateInner"/>
    /// forwards `afcEnabled` and 4 siblings below with no accessor able to prove any of them survive a
    /// periodic rebuild).</summary>
    internal bool InnerAfcEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.AfcEnabledForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.SyncRestartEnabledForTests"/> directly. Same reasoning/shape as
    /// <see cref="InnerAfcEnabledForTests"/> above.</summary>
    internal bool InnerSyncRestartEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.SyncRestartEnabledForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.AutoSyncEnabledForTests"/> directly. Same reasoning/shape as
    /// <see cref="InnerAfcEnabledForTests"/> above.</summary>
    internal bool InnerAutoSyncEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.AutoSyncEnabledForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.NotchEnabledForTests"/> directly. Same reasoning/shape as
    /// <see cref="InnerAfcEnabledForTests"/> above -- Un-stub-RX-tab Piece A code-review finding:
    /// without this, a dropped <c>RequestNotch</c> re-seed in <see cref="CreateInner"/> would
    /// silently turn the notch off after a periodic restart rebuild, with no test able to catch it.</summary>
    internal bool InnerNotchEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.NotchEnabledForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.NotchFrequencyForTests"/> directly. Same reasoning/shape as
    /// <see cref="InnerNotchEnabledForTests"/> above.</summary>
    internal double? InnerNotchFrequencyForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.NotchFrequencyForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.AutoStopEnabledForTests"/> directly. Same reasoning/shape as
    /// <see cref="InnerAfcEnabledForTests"/> above.</summary>
    internal bool InnerAutoStopEnabledForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.AutoStopEnabledForTests;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.SenseLevelForTests"/> directly. Same reasoning/shape as
    /// <see cref="InnerAfcEnabledForTests"/> above.</summary>
    internal int InnerSenseLevelForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.SenseLevelForTests;
            }
        }
    }

    public RestartableSstvDecoder(bool afcEnabled = true, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false, bool autoSlantEnabled = true, int senseLevel = 1, bool stationIdDecodeEnabled = false, DemodType demodType = DemodType.Hilbert, RxBpfPreset rxBpfPreset = RxBpfPreset.Wide, RxBufferMode rxBufferMode = RxBufferMode.On, int sampleRate = SstvSampleRate.Default, ILoggerFactory? loggerFactory = null)
        : this(
            afcEnabled,
            ComputeDefaultThresholds(sampleRate).WarningThresholdSamples,
            ComputeDefaultThresholds(sampleRate).CriticalThresholdSamples,
            syncRestartEnabled,
            autoSyncEnabled,
            autoStopEnabled,
            autoSlantEnabled,
            senseLevel,
            stationIdDecodeEnabled,
            demodType,
            rxBpfPreset,
            rxBufferMode,
            sampleRate,
            ComputeDefaultThresholds(sampleRate).MaximumSafeSampleIndex,
            decoderFactoryForTests: null,
            loggerFactory: loggerFactory)
    {
    }

    /// <summary>Test-only seam for injecting short thresholds/a small safe-index ceiling instead of
    /// the rate-aware production values --
    /// see this class' own doc comment for why a clock-injection seam is unnecessary now that the
    /// trigger is sample-count-based, not wall-clock-based.</summary>
    internal RestartableSstvDecoder(bool afcEnabled, long warningThresholdSamples, long criticalThresholdSamples, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false, bool autoSlantEnabled = true, int senseLevel = 1, bool stationIdDecodeEnabled = false, DemodType demodType = DemodType.Hilbert, RxBpfPreset rxBpfPreset = RxBpfPreset.Wide, RxBufferMode rxBufferMode = RxBufferMode.On, int sampleRate = SstvSampleRate.Default, int maximumSafeSampleIndex = int.MaxValue, Func<int, AnalogFmSstvDecoder>? decoderFactoryForTests = null, ILoggerFactory? loggerFactory = null)
    {
        if (!SstvSampleRate.IsSupported(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, $"Sample rate must be between {SstvSampleRate.Minimum} and {SstvSampleRate.Maximum} Hz inclusive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSafeSampleIndex, 1);

        _afcEnabled = afcEnabled;
        _syncRestartEnabled = syncRestartEnabled;
        _autoSyncEnabled = autoSyncEnabled;
        _autoStopEnabled = autoStopEnabled;
        _autoSlantEnabled = autoSlantEnabled;
        // Clamped here too (round-2 plan-review finding), not just left to AnalogFmSstvDecoder's own
        // constructor clamp -- this wrapper's own SenseLevel getter now reads THIS field directly
        // (see that property's own doc comment), so an unclamped seed would leak an out-of-range
        // value (e.g. a hand-edited settings.json) straight out to callers, even though the inner
        // decoder itself would have silently clamped to 0. Same clamp/fallback as
        // AnalogFmSstvDecoder's own constructor.
        _senseLevel = senseLevel is >= 0 and <= 3 ? senseLevel : 0;
        _demodType = demodType;
        _rxBpfPreset = rxBpfPreset;
        _rxBufferMode = rxBufferMode;
        _sampleRate = sampleRate;
        _warningThresholdSamples = warningThresholdSamples;
        _criticalThresholdSamples = criticalThresholdSamples;
        _maximumSafeSampleIndex = maximumSafeSampleIndex;
        _decoderFactoryForTests = decoderFactoryForTests;
        _loggerFactory = loggerFactory;
        _stationIdDecodeEnabled = stationIdDecodeEnabled;
        _inner = CreateInner();
    }

    /// <summary>Accepts one active producer call at a time. Recursive or concurrent overlapping calls
    /// are rejected before threshold evaluation, so an event handler cannot swap/dispose the inner
    /// decoder whose callback is still executing. The guard remains held through maintenance-event
    /// delivery; non-Push callbacks such as a critical-stop handler's <see cref="ResetAgc"/> remain
    /// valid and do not run under <see cref="_gate"/>.</summary>
    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        if (Interlocked.CompareExchange(ref _pushActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("Recursive or concurrent PushSamples calls are not supported.");
        }

        try
        {
            PushSamplesCore(samples);
        }
        finally
        {
            Volatile.Write(ref _pushActive, 0);
        }
    }

    private void PushSamplesCore(ReadOnlyMemory<float> samples)
    {
        AnalogFmSstvDecoder current;
        var raiseWarning = false;
        var raiseCritical = false;
        var raiseRestarted = false;
        var raiseReconfigurationRejected = false;

        lock (_gate)
        {
            // D2 round 2 fix: without this, a post-dispose call landing in the _criticalThresholdSamples
            // or _warningThresholdSamples branch below would call Swap(), which constructs a FRESH,
            // undisposed AnalogFmSstvDecoder and pushes to it successfully -- silently violating the
            // ObjectDisposedException contract ISstvDecoder.PushSamples now documents, AND leaking that
            // new inner decoder (this wrapper's own _disposed is already true, so nothing will ever
            // dispose it). Previously this only happened to throw as a side effect of forwarding to an
            // already-disposed _inner in the non-Swap branches.
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (samples.Length > _maximumSafeSampleIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(samples), samples.Length, "A single sample chunk cannot exceed the decoder's safe absolute-index capacity.");
            }

            var n = _inner.TotalSamplesReceived;

            if (samples.Length > _maximumSafeSampleIndex - n)
            {
                // mandatory: true -- this swap is the overflow-safety guarantee itself and must
                // happen regardless of a coincidentally-pending (and possibly failing) reconfiguration
                // request; see Swap's own doc comment for why "mandatory" always either commits or
                // throws, never silently no-ops (round-3 plan-review B1/Q3).
                raiseRestarted = Swap(mandatory: true, out raiseReconfigurationRejected);
                raiseCritical = true;
            }
            else if (n >= _criticalThresholdSamples)
            {
                raiseRestarted = Swap(mandatory: true, out raiseReconfigurationRejected);
                raiseCritical = true;
            }
            else if (_inner.IsIdle && n >= _warningThresholdSamples)
            {
                // Also mandatory (round-3 plan-review finding): this is the routine periodic
                // maintenance swap, not the new reconfiguration-only trigger below -- it must not
                // silently degrade into a no-op just because a pending reconfiguration happens to be
                // queued and its CreateInner attempt fails.
                raiseRestarted = Swap(mandatory: true, out raiseReconfigurationRejected);
            }
            else if (_inner.IsIdle && _pendingReconfiguration is not null)
            {
                // Restart-required-settings backlog item 2 (2026-08-27): the ONLY discretionary swap
                // trigger -- a persistent CreateInner failure here just leaves the old (still fully
                // functional) inner decoder in place instead of forcing anything (see Swap's own
                // doc comment).
                raiseRestarted = Swap(mandatory: false, out raiseReconfigurationRejected);
            }
            else if (!_inner.IsIdle && n >= _warningThresholdSamples && !_warningRaised)
            {
                _warningRaised = true;
                raiseWarning = true;
            }

            current = _inner;
        }

        ExceptionDispatchInfo? innerFailure = null;
        try
        {
            current.PushSamples(samples);
        }
        catch (Exception ex)
        {
            innerFailure = ExceptionDispatchInfo.Capture(ex);
        }

        // A committed swap's notifications are all attempted even when decoding failed. The inner
        // failure remains primary; maintenance failures are surfaced only when decoding succeeded.
        ExceptionDispatchInfo? maintenanceFailure = null;
        if (raiseCritical)
        {
            RaiseMaintenanceSubscribers(RestartCriticallyOverdue, ref maintenanceFailure);
        }

        if (raiseRestarted)
        {
            RaiseMaintenanceSubscribers(Restarted, ref maintenanceFailure);
        }

        if (raiseWarning)
        {
            RaiseMaintenanceSubscribers(RestartOverdue, ref maintenanceFailure);
        }

        if (raiseReconfigurationRejected)
        {
            RaiseMaintenanceSubscribers(ReconfigurationRejected, ref maintenanceFailure);
        }

        innerFailure?.Throw();
        maintenanceFailure?.Throw();
    }

    private static void RaiseMaintenanceSubscribers(Action? handlers, ref ExceptionDispatchInfo? firstFailure)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. See
    /// <see cref="ISstvDecoder.ResetAgc"/> for the full concurrency contract this method's caller
    /// must honor (D0-audit round-6 finding on the base implementation) -- deliberately called
    /// outside <see cref="_gate"/> once the current inner reference is captured, matching every
    /// other non-<see cref="PushSamples"/> deferred-request forward below, so a swap racing this call
    /// is silently applied to whichever inner instance was current at the moment of the call, same
    /// fire-and-forget shape as <see cref="RequestReSync"/> immediately below.</summary>
    public void ResetAgc()
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            current = _inner;
        }

        current.ResetAgc();
    }

    /// <summary>Forwards to whichever inner instance is current. A request racing a restart is
    /// silently dropped if the swap wins (the fresh inner has nothing to ReSync yet, matching legacy's
    /// own reception-start reset of this same state) -- not queued, same fire-and-forget contract
    /// <see cref="ISstvDecoder.RequestReSync"/> already documents.</summary>
    public void RequestReSync()
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            current = _inner;
        }

        current.RequestReSync();
    }

    /// <summary>See <see cref="ISstvDecoder.RequestNotch"/>. Unlike <see cref="RequestReSync"/>
    /// immediately above, this is persistent state, not a one-shot request that can be silently
    /// dropped by a racing restart -- stored under <see cref="_gate"/> (same pattern as
    /// <see cref="StationIdDecodeEnabled"/>'s setter) so <see cref="CreateInner"/> can re-apply it to
    /// a freshly-built inner decoder, in addition to forwarding to whichever inner is current right
    /// now.</summary>
    public void RequestNotch(bool enabled, double? frequencyHz)
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            _notchEnabled = enabled;
            if (frequencyHz is { } hz)
            {
                _notchFrequencyHz = hz;
            }

            current = _inner;
        }

        current.RequestNotch(enabled, frequencyHz);
    }

    /// <summary>See <see cref="ISstvDecoder.ArmScopeCapture"/>. Fire-and-forget, same shape as
    /// <see cref="RequestReSync"/> above -- unlike notch, there is no wrapper-level "re-seed on
    /// rebuild" step needed here: the STATE that must survive a restart is the capture's own fill
    /// progress, which already lives on <see cref="_scopeCaptureChannel0"/>/
    /// <see cref="_scopeCaptureChannel1"/> (the SAME instances every <see cref="CreateInner"/> call
    /// hands to the fresh inner decoder), not on this wrapper's own request-latch field. A request
    /// racing a restart is silently dropped if the swap wins, same accepted gap as
    /// <see cref="RequestReSync"/>'s own -- the caller can simply re-arm.</summary>
    public void ArmScopeCapture(int size)
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            current = _inner;
        }

        current.ArmScopeCapture(size);
    }

    /// <summary>See <see cref="ISstvDecoder.TryGetScopeCaptureChannel0"/>. Reads
    /// <see cref="_scopeCaptureChannel0"/> directly rather than forwarding through <c>_inner</c> --
    /// safe and equivalent, since it's the SAME instance the current (and every past/future) inner
    /// decoder writes into; <see cref="ScopeCaptureBuffer.TrySnapshot"/> is already safe from any
    /// thread, at any time, so no <see cref="_gate"/> is needed here either.</summary>
    public double[]? TryGetScopeCaptureChannel0() => _scopeCaptureChannel0.TrySnapshot();

    /// <summary>See <see cref="ISstvDecoder.TryGetScopeCaptureChannel1"/>. Same reasoning as
    /// <see cref="TryGetScopeCaptureChannel0"/> immediately above.</summary>
    public double[]? TryGetScopeCaptureChannel1() => _scopeCaptureChannel1.TrySnapshot();

    /// <summary>Forwards to whichever inner instance is current. A request racing a restart is
    /// silently dropped if the swap wins (the fresh inner has no in-progress reception to search yet,
    /// matching legacy's own reception-start reset) -- same fire-and-forget contract
    /// <see cref="ISstvDecoder.RequestCorrectSlant"/> already documents.</summary>
    public void RequestCorrectSlant()
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            current = _inner;
        }

        current.RequestCorrectSlant();
    }

    /// <summary>Forwards to whichever inner instance is current. A request racing a restart is
    /// silently dropped if the swap wins (the fresh inner has no in-progress decode to redirect,
    /// matching legacy's own reception-start reset) -- same fire-and-forget contract
    /// <see cref="ISstvDecoder.ForceMode"/> already documents. Unlike <see cref="RequestReSync"/>'s
    /// best-effort framing, this is a deliberate user command, so a drop here is worth surfacing to a
    /// caller that cares (not attempted here -- ForceMode has no return value by design, matching the
    /// interface).</summary>
    public void ForceMode(SstvModeDefinition mode)
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            current = _inner;
        }

        current.ForceMode(mode);
    }

    /// <summary>Forwards to whichever inner instance is current, same shape as <see cref="ForceMode"/>
    /// above. A request racing a restart is silently dropped if the swap wins -- harmless, since a
    /// fresh inner is already idle (nothing to abandon).</summary>
    public void RequestAbandonReception()
    {
        AnalogFmSstvDecoder current;
        lock (_gate)
        {
            current = _inner;
        }

        current.RequestAbandonReception();
    }

    /// <summary>Forwards to whichever inner instance is current. A restart swap resets this to
    /// <see langword="null"/> for any realistic triggering chunk (a fresh inner has no lock/
    /// slant-tracker yet, and establishing one requires a full VIS lock, not just pre-lock
    /// scanning progress) -- same state-gated reasoning as <see cref="SyncFrequencyCorrectionHz"/>
    /// below, not a hard guarantee for an arbitrarily large forwarded chunk. Matches
    /// <see cref="ISstvDecoder.SlantPpm"/>'s own documented null cases.</summary>
    public double? SlantPpm
    {
        get
        {
            lock (_gate)
            {
                return _inner.SlantPpm;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. Same post-swap reasoning as
    /// <see cref="SlantPpm"/> above -- state-gated on a fresh lock, reliably <see langword="null"/>
    /// for any realistic triggering chunk. Matches <see cref="ISstvDecoder.SyncOffsetSamples"/>'s
    /// own documented null cases.</summary>
    public int? SyncOffsetSamples
    {
        get
        {
            lock (_gate)
            {
                return _inner.SyncOffsetSamples;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. A restart swap resets this to
    /// <see cref="SstvSyncSource.Idle"/> for any realistic triggering chunk -- same state-gated
    /// reasoning as <see cref="SlantPpm"/> above (a fresh inner has no lock and no AVT training in
    /// progress until a fresh header search reaches one of those states).</summary>
    public SstvSyncSource SyncSource
    {
        get
        {
            lock (_gate)
            {
                return _inner.SyncSource;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. Unlike <see cref="SlantPpm"/>/
    /// <see cref="SyncOffsetSamples"/> above, a restart swap does NOT reliably reset this to
    /// <c>0.0</c>: <see cref="PushSamples"/> swaps to a fresh inner and then forwards that SAME
    /// chunk to it, and pre-lock header scanning drives this underlying AGC cursor forward with no
    /// lock required -- so the value immediately after a restart reflects a freshly-constructed
    /// inner PLUS whatever that one push call fed it (0.0 only if that chunk was under the
    /// underlying ~100ms averaging window).</summary>
    public double SignalPeakLevel
    {
        get
        {
            lock (_gate)
            {
                return _inner.SignalPeakLevel;
            }
        }
    }

    /// <summary>See <see cref="ISstvDecoder.AutoSlantEnabled"/> -- genuinely live now (2026-08-27,
    /// restart-required-settings backlog item 1), same shape as <see cref="StationIdDecodeEnabled"/>
    /// below: a set value is applied to the CURRENT inner instance immediately, under
    /// <see cref="_gate"/>, and also stored so <see cref="CreateInner"/> seeds a future (re)built
    /// inner with the last value set. The getter reads THIS wrapper's own stored field, not
    /// <c>_inner.AutoSlantEnabled</c> -- deliberately, same reasoning as <see cref="SenseLevel"/>'s
    /// own doc comment: forwarding to the inner decoder's own deferred (apply-on-next-PushSamples)
    /// value could read stale for a moment after a set (plan-review round 3 finding).</summary>
    public bool AutoSlantEnabled
    {
        get
        {
            lock (_gate)
            {
                return _autoSlantEnabled;
            }
        }
        set
        {
            lock (_gate)
            {
                _autoSlantEnabled = value;
                _inner.AutoSlantEnabled = value;
            }
        }
    }

    /// <summary>See <see cref="ISstvDecoder.AutoSyncEnabled"/> -- genuinely live now (2026-08-27).
    /// See <see cref="AutoSlantEnabled"/>'s own doc comment immediately above for the shared shape
    /// and getter contract.</summary>
    public bool AutoSyncEnabled
    {
        get
        {
            lock (_gate)
            {
                return _autoSyncEnabled;
            }
        }
        set
        {
            lock (_gate)
            {
                _autoSyncEnabled = value;
                _inner.AutoSyncEnabled = value;
            }
        }
    }

    /// <summary>See <see cref="ISstvDecoder.AutoStopEnabled"/> -- genuinely live now (2026-08-27).
    /// See <see cref="AutoSlantEnabled"/>'s own doc comment above for the shared shape and getter
    /// contract.</summary>
    public bool AutoStopEnabled
    {
        get
        {
            lock (_gate)
            {
                return _autoStopEnabled;
            }
        }
        set
        {
            lock (_gate)
            {
                _autoStopEnabled = value;
                _inner.AutoStopEnabled = value;
            }
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SyncRestartEnabled"/> -- genuinely live now
    /// (2026-08-27). See <see cref="AutoSlantEnabled"/>'s own doc comment above for the shared shape
    /// and getter contract.</summary>
    public bool SyncRestartEnabled
    {
        get
        {
            lock (_gate)
            {
                return _syncRestartEnabled;
            }
        }
        set
        {
            lock (_gate)
            {
                _syncRestartEnabled = value;
                _inner.SyncRestartEnabled = value;
            }
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SenseLevel"/> for the full contract -- genuinely live
    /// now (user-reported 2026-08-27, "Squelch level" live control), same shape as
    /// <see cref="StationIdDecodeEnabled"/> below: a set value is applied to the CURRENT inner
    /// instance immediately, under <see cref="_gate"/>, and also stored (already clamped 0-3, same
    /// as the constructor) so <see cref="CreateInner"/> seeds a future (re)built inner with the last
    /// value set, not the constructor default -- fixes a real defect an earlier draft of this change
    /// had: forwarding straight to <c>_inner.SenseLevel</c> with no wrapper-level storage meant a
    /// live change was silently reverted at the next periodic maintenance swap. The getter reads
    /// THIS wrapper's own stored field, not <c>_inner.SenseLevel</c> -- immediate and restart-stable,
    /// unlike forwarding through to the inner decoder's own deferred (apply-on-next-PushSamples)
    /// value, which could read stale for a moment after a set.</summary>
    public int SenseLevel
    {
        get
        {
            lock (_gate)
            {
                return _senseLevel;
            }
        }
        set
        {
            lock (_gate)
            {
                var clamped = value is >= 0 and <= 3 ? value : 0;
                _senseLevel = clamped;
                _inner.SenseLevel = clamped;
            }
        }
    }

    /// <summary>Diagnostic-only: reads the CURRENT inner instance's own
    /// <see cref="AnalogFmSstvDecoder.VisLockThresholdsForTests"/> directly -- same reasoning as
    /// <see cref="InnerSenseLevelForTests"/> above, extended to prove a live change reached
    /// <see cref="VisLockStateMachine"/>'s own copies too, including post-restart-swap.</summary>
    internal (double Slvl, double Slvl2) InnerVisLockThresholdsForTests
    {
        get
        {
            lock (_gate)
            {
                return _inner.VisLockThresholdsForTests;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current -- deliberately last-APPLIED, not
    /// last-requested (different from <see cref="SenseLevel"/>/<see cref="AutoSlantEnabled"/>'s
    /// choice above; restart-required-settings backlog item 2, 2026-08-27). No round-trip
    /// persistence risk exists either way here, and last-applied is the more honest read for a
    /// display that can legitimately lag an entire reception behind what Options just saved (a
    /// pending change only applies once the decoder goes idle -- see
    /// <see cref="RequestReconfiguration"/>). Genuinely live now, no longer restart-only.</summary>
    public RxBpfPreset RxBpfPreset
    {
        get
        {
            lock (_gate)
            {
                return _inner.RxBpfPreset;
            }
        }
    }

    /// <summary>See <see cref="ISstvDecoderReconfiguration.RequestReconfiguration"/> for the
    /// caller-facing contract. Unlike <see cref="StationIdDecodeEnabled"/>'s in-place forward, none
    /// of these three fields has a safe in-place mutation path on <see cref="AnalogFmSstvDecoder"/>
    /// (each is <c>readonly</c> there, read once at construction) -- so a request here only ever
    /// QUEUES a value; <see cref="Swap"/> is the sole place that ever drains and applies it, gated
    /// to only ever fire while <see cref="AnalogFmSstvDecoder.IsIdle"/> (see <see cref="PushSamplesCore"/>'s
    /// new branch) so a live reception's own group-delay/sync-anchor/staging-buffer state is never
    /// disturbed mid-image. The equality guard below compares against this wrapper's own currently-
    /// COMMITTED fields (not `_inner`'s, which could be mid-drain) -- so an unrelated Options Save
    /// that didn't touch any of these three never queues a spurious rebuild, and a user who queues a
    /// change then reverts it back before it applies correctly cancels the pending request.
    /// Deliberately does NOT guard against a post-<see cref="Dispose"/> call the way
    /// <see cref="PushSamplesCore"/> does -- matching every sibling <c>Request*</c> method on this
    /// class (<see cref="RequestReSync"/>/<see cref="RequestNotch"/>/<see cref="RequestCorrectSlant"/>/
    /// <see cref="RequestAbandonReception"/>), none of which throws post-dispose either (round-3
    /// plan-review R2: a moot post-dispose request is correctly dropped, not lossy, and introducing
    /// the ONE throwing `Request*` method here would be new, unprecedented behavior, not consistency
    /// with anything). Safe to call from any thread.</summary>
    public void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode)
    {
        lock (_gate)
        {
            _pendingReconfiguration = (rxBpfPreset == _rxBpfPreset && demodType == _demodType && rxBufferMode == _rxBufferMode)
                ? null
                : new PendingReconfiguration(rxBpfPreset, demodType, rxBufferMode);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.StationIdDecodeEnabled"/> for the full contract --
    /// unlike <see cref="AutoSlantEnabled"/> above, this is genuinely live: a set value is applied to
    /// the CURRENT inner instance immediately, under <see cref="_gate"/> (consistent with every
    /// swap-affected accessor here), and also stored so <see cref="CreateInner"/> seeds a future
    /// (re)built inner with the last value set, not the constructor default.</summary>
    public bool StationIdDecodeEnabled
    {
        get
        {
            lock (_gate)
            {
                return _stationIdDecodeEnabled;
            }
        }
        set
        {
            lock (_gate)
            {
                _stationIdDecodeEnabled = value;
                _inner.StationIdDecodeEnabled = value;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. Same post-swap caveat as
    /// <see cref="SignalPeakLevel"/> above -- NOT reliably <see langword="false"/> immediately
    /// after a restart, since the triggering chunk is forwarded to the fresh inner and pre-lock
    /// scanning alone can drive the underlying level past threshold for a large enough chunk.</summary>
    public bool IsLevelOverdriven
    {
        get
        {
            lock (_gate)
            {
                return _inner.IsLevelOverdriven;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. A restart swap resets this to
    /// <see langword="null"/> (a fresh inner has no lock/AFC-tracker yet), matching
    /// <see cref="ISstvDecoder.SyncFrequencyCorrectionHz"/>'s own documented null cases -- unlike
    /// the two AGC-backed properties above, this one IS state-gated (requires a fresh <c>_mode</c>
    /// lock, not just any pre-lock scanning progress), so a realistic single streaming chunk
    /// reliably can't establish one within the same push call that triggered the swap.</summary>
    public double? SyncFrequencyCorrectionHz
    {
        get
        {
            lock (_gate)
            {
                return _inner.SyncFrequencyCorrectionHz;
            }
        }
    }

    /// <summary>Forwards to whichever inner instance is current. Same post-swap caveat as
    /// <see cref="SignalPeakLevel"/> above -- NOT reliably near-zero immediately after a restart,
    /// since the triggering chunk (which could itself be large) is forwarded to the fresh inner
    /// and counted by its own pre-lock retention window.</summary>
    public int BufferedSampleCount
    {
        get
        {
            lock (_gate)
            {
                return _inner.BufferedSampleCount;
            }
        }
    }

    /// <summary>Returns whether the swap actually committed a fresh <see cref="_inner"/> (always
    /// <see langword="true"/> when <paramref name="mandatory"/> is <see langword="true"/> -- a
    /// mandatory swap either commits or throws, it never silently no-ops). <paramref name="reconfigurationRejected"/>
    /// is set when a queued <see cref="RequestReconfiguration"/> request had to be rolled back and
    /// dropped because <c>CreateInner</c> threw while draining it -- orthogonal to the return value:
    /// a mandatory swap can commit (<see langword="true"/>) using the PREVIOUS settings while still
    /// reporting the drop (round-3 plan-review Q3/finding).
    ///
    /// RX buffer subsystem Phase 7 (disposal-chain sub-piece): the outgoing instance's own
    /// RxBufferMode.Extended staging buffer (if any) owns scratch files and a background writer
    /// task -- disposing it here, before the reference is dropped, is the only place that ever
    /// happens for a decoder that gets swapped out mid-session (as opposed to torn down at
    /// session end, see this class's own Dispose() below). Every restart creates a fresh inner
    /// instance (CreateInner, below), so without this, Extended mode would leak two scratch files
    /// + a live consumer task per restart cycle -- a real, unbounded production leak, not a
    /// hypothetical one (this exact gap was flagged by round-2 plan-review before Phase 7 started).
    /// This whole method runs under <see cref="_gate"/> (this class's own established convention, see
    /// the class doc comment) -- the outgoing instance's own Dispose() does a bounded
    /// channel-drain-and-FileStream-dispose (RxDiskLineStagingBuffer.DrainTimeout, waited twice =
    /// ~10s worst case), so a genuinely stuck writer blocks whichever UI-thread property getter
    /// (SignalPeakLevel/SlantPpm/SyncSource/SyncOffsetSamples/BufferedSampleCount/
    /// IsLevelOverdriven/SyncFrequencyCorrectionHz) is waiting on this same lock for up to that
    /// long. Code-review-accepted: only reachable under a pathological stuck-writer condition at
    /// a many-hour maintenance swap interval originally -- restart-required-settings backlog item 2
    /// (2026-08-27) made this user-triggerable (a Reconfiguration Save queues a swap on the very next
    /// idle push), so the worst case is now bounded by human click rate, not a many-hour interval;
    /// still code-review-accepted, the underlying stuck-writer precondition is unchanged.</summary>
    private bool Swap(bool mandatory, out bool reconfigurationRejected)
    {
        reconfigurationRejected = false;
        var pending = _pendingReconfiguration;
        var previousRxBpfPreset = _rxBpfPreset;
        var previousDemodType = _demodType;
        var previousRxBufferMode = _rxBufferMode;
        if (pending is not null)
        {
            _rxBpfPreset = pending.RxBpfPreset;
            _demodType = pending.DemodType;
            _rxBufferMode = pending.RxBufferMode;
        }

        var outgoing = _inner;

        // Construct and fully subscribe the replacement before disconnecting the installed decoder.
        // Extended mode can fail while creating its scratch-file backend; if that happens, the old
        // decoder must remain both installed and observable so the failed swap is transactional.
        AnalogFmSstvDecoder incoming;
        try
        {
            incoming = CreateInner();
        }
        catch when (pending is not null)
        {
            // Round-2/round-3 plan-review B1: roll back to the previously-committed values and drop
            // the pending marker -- ONE attempt only, never automatically retried. A PERSISTENT
            // CreateInner failure (e.g. RxBufferMode.Extended's scratch-file creation failing on a
            // full or read-only temp directory) must not re-arm this same idle-drain trigger on
            // every subsequent push forever -- that would permanently and silently stop all RX
            // decoding until the user happened to reopen Options and re-save the old value. A
            // transient failure simply isn't retried until the user changes the setting again,
            // matching how every other one-shot user action in this codebase behaves on failure (no
            // hidden background retry loop). This `catch` deliberately only matches when a
            // reconfiguration was actually pending (`when (pending is not null)`) -- with no pending
            // change, a `CreateInner` failure propagates out of this method exactly as it always has,
            // with no new failure mode for that case.
            _rxBpfPreset = previousRxBpfPreset;
            _demodType = previousDemodType;
            _rxBufferMode = previousRxBufferMode;
            _pendingReconfiguration = null;
            reconfigurationRejected = true;

            if (!mandatory)
            {
                // The old `outgoing` inner decoder is untouched and still decoding fine -- reject the
                // reconfiguration this cycle instead of forcing a swap that isn't required.
                return false;
            }

            // mandatory: this swap MUST happen -- the overflow/critical-overdue safety contract
            // predates this feature and must not regress just because an unrelated reconfiguration
            // request happened to be queued at the same moment. Retry once with the ROLLED-BACK
            // (previous, known-good) values so the mandatory swap still completes; if this ALSO
            // throws, that is the exact same unhandled-exception behavior CreateInner already has
            // today for a mandatory swap with no pending change involved at all.
            incoming = CreateInner();
        }

        UnsubscribeFrom(outgoing);
        _inner = incoming;

        // The disk-backed staging buffer performs each teardown step independently, logs failures,
        // and never throws, so disposing the outgoing decoder cannot suppress the already-committed
        // swap's bookkeeping or maintenance notifications.
        outgoing.Dispose();

        // Cleared on the commit path too (round-3 plan-review finding), not just inside the catch
        // above -- otherwise a SUCCESSFUL drain of a pending reconfiguration would leave
        // _pendingReconfiguration set, and the new idle-plus-pending branch in PushSamplesCore would
        // re-arm and rebuild a fresh (identical) inner decoder on every subsequent idle push forever.
        _pendingReconfiguration = null;
        _warningRaised = false;
        RestartCountForTests++;
        return true;
    }

    private AnalogFmSstvDecoder CreateInner()
    {
        // Auditor code-review note: a decoder produced via _decoderFactoryForTests (test-only seam)
        // does NOT receive _scopeCaptureChannel0/_scopeCaptureChannel1 -- that factory decides its
        // own AnalogFmSstvDecoder construction entirely, so a test using it gets a decoder with its
        // own private, unshared buffers, and this wrapper's own TryGetScopeCaptureChannel0/1 would
        // never observe anything it writes. Test-only, never reachable in production
        // (_decoderFactoryForTests is never set outside this project's own tests) -- documented here
        // so a future test using this seam doesn't chase a phantom capture stall.
        var decoder = _decoderFactoryForTests?.Invoke(_sampleRate)
            ?? new AnalogFmSstvDecoder(sampleRate: _sampleRate, afcEnabled: _afcEnabled, syncRestartEnabled: _syncRestartEnabled, autoSyncEnabled: _autoSyncEnabled, autoStopEnabled: _autoStopEnabled, autoSlantEnabled: _autoSlantEnabled, senseLevel: _senseLevel, demodType: _demodType, rxBpfPreset: _rxBpfPreset, rxBufferMode: _rxBufferMode, loggerFactory: _loggerFactory, scopeCaptureChannel0: _scopeCaptureChannel0, scopeCaptureChannel1: _scopeCaptureChannel1);
        if (decoder.SampleRate != _sampleRate)
        {
            decoder.Dispose();
            throw new InvalidOperationException($"Decoder factory returned {decoder.SampleRate} Hz; expected {_sampleRate} Hz.");
        }

        decoder.StationIdDecodeEnabled = _stationIdDecodeEnabled;
        if (_notchEnabled)
        {
            // A fresh decoder has nothing locked yet, so ApplyPendingNotchRequest's own group-delay
            // compensation correctly no-ops (same _mode/_slantTracker gate PerformReSync uses) --
            // this just seeds the enabled/frequency state itself, matching StationIdDecodeEnabled's
            // own re-seed immediately above.
            decoder.RequestNotch(true, _notchFrequencyHz);
        }

        decoder.LineDecoded += OnLineDecoded;
        decoder.ModeDetected += OnModeDetected;
        decoder.DecodeRestarted += OnDecodeRestarted;
        decoder.StationIdDecoded += OnStationIdDecoded;
        return decoder;
    }

    internal static RestartThresholds ComputeDefaultThresholds(int sampleRate)
    {
        if (!SstvSampleRate.IsSupported(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, $"Sample rate must be between {SstvSampleRate.Minimum} and {SstvSampleRate.Maximum} Hz inclusive.");
        }

        var projectionReserveSamples = checked((long)Math.Ceiling(3600d * SstvSampleRate.MaximumAutoSlantRate(sampleRate)));
        var maximumSafeSampleIndex = checked((int)(int.MaxValue - projectionReserveSamples));
        var preferredCritical = checked(13L * 3600L * sampleRate);
        var critical = Math.Min(preferredCritical, maximumSafeSampleIndex);
        var preferredWarning = checked(12L * 3600L * sampleRate);
        var warning = Math.Min(preferredWarning, checked(critical - 3600L * sampleRate));

        if (warning <= 0 || warning >= critical || critical > maximumSafeSampleIndex)
        {
            throw new InvalidOperationException("Computed restart thresholds are not strictly ordered inside the decoder's safe sample-index range.");
        }

        return new RestartThresholds(warning, critical, maximumSafeSampleIndex, projectionReserveSamples);
    }

    /// <summary>Conservative composed forward horizon for every current absolute-index addition in
    /// <see cref="AnalogFmSstvDecoder"/>. Several terms are mutually exclusive and paired-row modes
    /// are deliberately over-counted; the purpose is a durable upper-bound proof against the
    /// one-hour reserve, not a tight runtime estimate. Absolute-index additions bounded directly by
    /// already-received data (for example <c>_syncBypassProcessedUpTo + 1</c>) add no forward term.
    ///
    /// Batch 2 chunk 2c round-1 finding: every term here is scaled by
    /// <see cref="SstvSampleRate.MaximumAutoSlantRate"/> -- the AUTOMATIC Auto-Slant clamp
    /// (`SlantTracker.ClampAndNormalizeAutoSlantRate`'s own `*1100/1060` ceiling). The MANUAL
    /// Correct Slant path (<see cref="AnalogFmSstvDecoder.IsManualSlantProjectionSafe"/>) has no
    /// such clamp by design (see that method's own doc comment) and is guarded independently
    /// (`projectedSamples &lt; int.MaxValue - consumed`), NOT by this reserve. At most supported
    /// rates the real headroom above <see cref="RestartThresholds.CriticalThresholdSamples"/> is
    /// tens of times this reserve, so a manual projection eating into it is not reachable in
    /// practice -- but at 42,495 Hz and above (including 44,100 and 48,000, the two most common
    /// real capture rates) <c>critical == maximumSafeSampleIndex</c>
    /// exactly (this reserve IS the entire remaining headroom), so a sufficiently pathological
    /// manual regression result immediately before a 13h-mark push could in principle leave less
    /// margin than this function's own callers assume. A future change to either guard's clamp
    /// should re-check this assumption, not just re-run the existing per-rate tests.</summary>
    internal static long ComputeMaximumComposedProjectionSamples(int sampleRate)
    {
        if (!SstvSampleRate.IsSupported(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var effectiveRate = SstvSampleRate.MaximumAutoSlantRate(sampleRate);
        static long DurationSamples(double durationMs, double rate) =>
            checked((long)Math.Ceiling(durationMs / 1000d * rate));

        var fixedForwardAdditions = checked(
            DurationSamples(500d, effectiveRate) // EndOfImage dead-time cursor
            + DurationSamples(VisHeader.MaxSearchCeilingMs, effectiveRate) // standard/fixed VIS ceiling
            + DurationSamples(VisHeader.NarrowSearchCeilingMs, effectiveRate) // narrow VIS ceiling
            + DurationSamples(VisHeader.NarrowPostBitClockOriginDurationMs, effectiveRate) // narrow-FSK completion-to-anchor offset
            + DurationSamples(VisLockStateMachine.AnchorReconciliationDurationMs, effectiveRate) // VIS state-machine completion-to-anchor reconciliation
            + DurationSamples(VisHeader.ScottiePostVisPulseDurationMs, effectiveRate) // maximum VIS state-machine mode-specific tail
            + DurationSamples(2d * VisHeader.AvtVisBlockDurationMs, effectiveRate) // AVT training origin
            + DurationSamples(VisHeader.AvtExtraHeaderDurationMs, effectiveRate) // AVT fallback/training ceiling
            + AnalogFmSstvDecoder.AnchorWarmupSamples); // anchor/dead-zone warm-up margin

        var maximumFullImage = SstvModeRegistry.All.Max(mode =>
            DurationSamples(mode.LineDurationMs * mode.ImageHeight, effectiveRate));
        var maximumLine = SstvModeRegistry.All.Max(mode =>
            DurationSamples(mode.LineDurationMs, effectiveRate));

        return checked(fixedForwardAdditions + maximumFullImage + 2L * maximumLine);
    }

    private void UnsubscribeFrom(AnalogFmSstvDecoder decoder)
    {
        decoder.LineDecoded -= OnLineDecoded;
        decoder.ModeDetected -= OnModeDetected;
        decoder.DecodeRestarted -= OnDecodeRestarted;
        decoder.StationIdDecoded -= OnStationIdDecoded;
    }

    private void OnLineDecoded(DecodedImageUpdate update) => RaiseForwardedSubscribers(LineDecoded, update);

    private void OnModeDetected(SstvModeDefinition mode) => RaiseForwardedSubscribers(ModeDetected, mode);

    private void OnDecodeRestarted(SstvModeDefinition mode) => RaiseForwardedSubscribers(DecodeRestarted, mode);

    private void OnStationIdDecoded(FskStationIdDecodedInfo info) => RaiseForwardedSubscribers(StationIdDecoded, info);

    private static void RaiseForwardedSubscribers<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        ExceptionDispatchInfo? firstFailure = null;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        firstFailure?.Throw();
    }

    // RX buffer subsystem Phase 7 (disposal-chain sub-piece): disposes whichever AnalogFmSstvDecoder
    // is current at teardown time -- the outgoing-instance disposal inside Swap() (above) only covers
    // decoders that get REPLACED mid-session; this covers the one that's still current when the
    // session itself ends. Locked under _gate, consistent with every other _inner access in this
    // class (this class's own doc comment on why: the swap and every _inner read/write share this one
    // lock). Idempotent: this class is a container-created DI singleton (Program.cs), so the DI
    // container can dispose it at host shutdown IN ADDITION to SstvSessionService's own explicit
    // disposal call, in unspecified relative order (round-2 plan-review finding).
    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inner.Dispose();
        }
    }
}
