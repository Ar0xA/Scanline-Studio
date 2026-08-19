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
public sealed class RestartableSstvDecoder : ISstvDecoder, ISstvDecoderMaintenance, IDisposable
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
    private readonly bool _syncRestartEnabled;
    private readonly bool _autoSyncEnabled;
    private readonly bool _autoStopEnabled;
    private readonly bool _autoSlantEnabled;
    private readonly int _senseLevel;
    private readonly DemodType _demodType;
    private readonly RxBpfPreset _rxBpfPreset;
    private readonly RxBufferMode _rxBufferMode;
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
        _senseLevel = senseLevel;
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
                Swap();
                raiseCritical = true;
                raiseRestarted = true;
            }
            else if (n >= _criticalThresholdSamples)
            {
                Swap();
                raiseCritical = true;
                raiseRestarted = true;
            }
            else if (_inner.IsIdle && n >= _warningThresholdSamples)
            {
                Swap();
                raiseRestarted = true;
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

    /// <summary>Reads this WRAPPER's own stored flag, not the inner decoder's -- unlike the
    /// swap-affected telemetry below, this is immutable for the wrapper's whole lifetime (restart-only,
    /// same reasoning as every constructor-injected toggle here), so there's no post-swap caveat to
    /// document and no need to take <see cref="_gate"/> to read it.</summary>
    public bool AutoSlantEnabled => _autoSlantEnabled;

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

    private void Swap()
    {
        // RX buffer subsystem Phase 7 (disposal-chain sub-piece): the outgoing instance's own
        // RxBufferMode.Extended staging buffer (if any) owns scratch files and a background writer
        // task -- disposing it here, before the reference is dropped, is the only place that ever
        // happens for a decoder that gets swapped out mid-session (as opposed to torn down at
        // session end, see this class's own Dispose() below). Every restart creates a fresh inner
        // instance (CreateInner, below), so without this, Extended mode would leak two scratch files
        // + a live consumer task per restart cycle -- a real, unbounded production leak, not a
        // hypothetical one (this exact gap was flagged by round-2 plan-review before Phase 7 started).
        // This whole method runs under `lock (_gate)` (this class's own established convention, see
        // the class doc comment) -- the outgoing instance's own Dispose() does a bounded
        // channel-drain-and-FileStream-dispose (RxDiskLineStagingBuffer.DrainTimeout, waited twice =
        // ~10s worst case), so a genuinely stuck writer blocks whichever UI-thread property getter
        // (SignalPeakLevel/SlantPpm/BufferedSampleCount) is waiting on this same lock for up to that
        // long. Code-review-accepted: only reachable under a pathological stuck-writer condition at
        // a many-hour maintenance swap interval, not a normal-operation cost.
        var outgoing = _inner;

        // Construct and fully subscribe the replacement before disconnecting the installed decoder.
        // Extended mode can fail while creating its scratch-file backend; if that happens, the old
        // decoder must remain both installed and observable so the failed swap is transactional.
        var incoming = CreateInner();
        UnsubscribeFrom(outgoing);
        _inner = incoming;

        // The disk-backed staging buffer performs each teardown step independently, logs failures,
        // and never throws, so disposing the outgoing decoder cannot suppress the already-committed
        // swap's bookkeeping or maintenance notifications.
        outgoing.Dispose();

        _warningRaised = false;
        RestartCountForTests++;
    }

    private AnalogFmSstvDecoder CreateInner()
    {
        var decoder = _decoderFactoryForTests?.Invoke(_sampleRate)
            ?? new AnalogFmSstvDecoder(sampleRate: _sampleRate, afcEnabled: _afcEnabled, syncRestartEnabled: _syncRestartEnabled, autoSyncEnabled: _autoSyncEnabled, autoStopEnabled: _autoStopEnabled, autoSlantEnabled: _autoSlantEnabled, senseLevel: _senseLevel, demodType: _demodType, rxBpfPreset: _rxBpfPreset, rxBufferMode: _rxBufferMode, loggerFactory: _loggerFactory);
        if (decoder.SampleRate != _sampleRate)
        {
            decoder.Dispose();
            throw new InvalidOperationException($"Decoder factory returned {decoder.SampleRate} Hz; expected {_sampleRate} Hz.");
        }

        decoder.StationIdDecodeEnabled = _stationIdDecodeEnabled;
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
    /// already-received data (for example <c>_syncBypassProcessedUpTo + 1</c>) add no forward term.</summary>
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
