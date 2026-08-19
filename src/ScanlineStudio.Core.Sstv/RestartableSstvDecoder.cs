using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Fixes ultracode audit finding #34 (AnalogFmSstvDecoder's absolute sample-index space is
/// `int`, wrapping after ~13.5h of continuous streaming @44100Hz) by periodically discarding and
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
/// <item>`n &gt;= criticalThreshold` (regardless of idle): force the swap UNCONDITIONALLY. This is the
/// actual overflow-safety guarantee -- it never waits for idle, so it can never wedge and can never
/// fail to happen. Self-clearing: the swap resets `n` back to ~0, so this can't re-fire on the very
/// next call.</item>
/// <item>Else if idle and `n &gt;= warningThreshold`: normal swap (the common path in real usage,
/// since real receiving has gaps).</item>
/// <item>Else if not idle and `n &gt;= warningThreshold`: raise <see cref="RestartOverdue"/> once
/// (guarded so it doesn't fire on every subsequent call for the rest of the window).</item>
/// </list>
/// This makes the swap itself (case 1) unconditional and independent of the visibility layer (cases
/// 2-3) -- even a bug in whatever consumes <see cref="RestartOverdue"/>/<see cref="RestartCriticallyOverdue"/>
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
    // Sized against AnalogFmSstvDecoder's own constructor default (11025) -- this class never passes
    // a different sampleRate (matching Program.cs's actual registration today), so these constants
    // really do mean 12h/13h in production. If the decoder's sample rate is ever made configurable
    // to match the capture device's actual rate (a separate, pre-existing, unrelated mismatch), these
    // must be recomputed against THAT rate, not left as a raw sample count sized for 11025.
    internal const int ProductionSampleRate = 11025;
    internal const long DefaultWarningThresholdSamples = 12L * 3600 * ProductionSampleRate;
    internal const long DefaultCriticalThresholdSamples = 13L * 3600 * ProductionSampleRate;

    private readonly bool _afcEnabled;
    private readonly bool _syncRestartEnabled;
    private readonly bool _autoSyncEnabled;
    private readonly bool _autoStopEnabled;
    private readonly bool _autoSlantEnabled;
    private readonly int _senseLevel;
    private readonly DemodType _demodType;
    private readonly RxBpfPreset _rxBpfPreset;
    private readonly RxBufferMode _rxBufferMode;
    private readonly long _warningThresholdSamples;
    private readonly long _criticalThresholdSamples;
    private readonly Func<AnalogFmSstvDecoder>? _decoderFactoryForTests;
    private readonly object _gate = new();

    // NOT readonly, unlike every toggle above -- StationIdDecodeEnabled is deliberately
    // LIVE-settable (see ISstvDecoder.StationIdDecodeEnabled's own doc comment for why), so this is
    // both the "what to apply to a freshly-(re)built inner decoder" seed AND the current live value,
    // kept in sync with _inner.StationIdDecodeEnabled by the constructor, property setter, and
    // CreateInner under _gate.
    private bool _stationIdDecodeEnabled;

    private AnalogFmSstvDecoder _inner;
    private bool _warningRaised;

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

    public RestartableSstvDecoder(bool afcEnabled = true, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false, bool autoSlantEnabled = true, int senseLevel = 1, bool stationIdDecodeEnabled = false, DemodType demodType = DemodType.Hilbert, RxBpfPreset rxBpfPreset = RxBpfPreset.Wide, RxBufferMode rxBufferMode = RxBufferMode.On)
        : this(afcEnabled, DefaultWarningThresholdSamples, DefaultCriticalThresholdSamples, syncRestartEnabled, autoSyncEnabled, autoStopEnabled, autoSlantEnabled, senseLevel, stationIdDecodeEnabled, demodType, rxBpfPreset, rxBufferMode)
    {
    }

    /// <summary>Test-only seam for injecting short thresholds instead of the real 12h/13h ones --
    /// see this class' own doc comment for why a clock-injection seam is unnecessary now that the
    /// trigger is sample-count-based, not wall-clock-based.</summary>
    internal RestartableSstvDecoder(bool afcEnabled, long warningThresholdSamples, long criticalThresholdSamples, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false, bool autoSlantEnabled = true, int senseLevel = 1, bool stationIdDecodeEnabled = false, DemodType demodType = DemodType.Hilbert, RxBpfPreset rxBpfPreset = RxBpfPreset.Wide, RxBufferMode rxBufferMode = RxBufferMode.On, Func<AnalogFmSstvDecoder>? decoderFactoryForTests = null)
    {
        _afcEnabled = afcEnabled;
        _syncRestartEnabled = syncRestartEnabled;
        _autoSyncEnabled = autoSyncEnabled;
        _autoStopEnabled = autoStopEnabled;
        _autoSlantEnabled = autoSlantEnabled;
        _senseLevel = senseLevel;
        _demodType = demodType;
        _rxBpfPreset = rxBpfPreset;
        _rxBufferMode = rxBufferMode;
        _warningThresholdSamples = warningThresholdSamples;
        _criticalThresholdSamples = criticalThresholdSamples;
        _decoderFactoryForTests = decoderFactoryForTests;
        _stationIdDecodeEnabled = stationIdDecodeEnabled;
        _inner = CreateInner();
    }

    /// <summary>Not safe to call concurrently from multiple threads -- `_gate` only protects the swap
    /// itself (so a same-thread re-entrant call, e.g. from a critical-stop handler's <see cref="ResetAgc"/>,
    /// can't deadlock), not general thread-safety: <c>current</c> is dereferenced OUTSIDE the lock
    /// (matching <see cref="AnalogFmSstvDecoder"/>'s own single-caller assumption), and two concurrent
    /// callers could both read a stale `current` or interleave against the same inner instance. Today's
    /// sole caller (<c>ScanlineStudio.Abstractions.Audio.IAudioEngine.SamplesCaptured</c>'s drain thread) already
    /// satisfies this.</summary>
    public void PushSamples(ReadOnlyMemory<float> samples)
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

            var n = _inner.TotalSamplesReceived;

            if (n >= _criticalThresholdSamples)
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

        try
        {
            current.PushSamples(samples);
        }
        finally
        {
            // A swap is already committed before the triggering chunk is forwarded. Its maintenance
            // notifications must therefore still be attempted if decoding (or a decode subscriber)
            // throws while processing that chunk. Nested finally blocks also ensure Restarted is
            // attempted if a critical handler throws, and RestartOverdue if a prior handler throws.
            try
            {
                if (raiseCritical)
                {
                    RestartCriticallyOverdue?.Invoke();
                }
            }
            finally
            {
                try
                {
                    if (raiseRestarted)
                    {
                        Restarted?.Invoke();
                    }
                }
                finally
                {
                    if (raiseWarning)
                    {
                        RestartOverdue?.Invoke();
                    }
                }
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
        // the ~12h swap interval, not a normal-operation cost.
        var outgoing = _inner;

        // Construct and fully subscribe the replacement before disconnecting the installed decoder.
        // Extended mode can fail while creating its scratch-file backend; if that happens, the old
        // decoder must remain both installed and observable so the failed swap is transactional.
        var incoming = CreateInner();
        UnsubscribeFrom(outgoing);
        _inner = incoming;

        // Code-review finding: RxDiskLineStagingBuffer.Dispose() calls FileStream.Dispose()
        // unguarded, which flushes and can throw IOException (a full disk during that final flush --
        // exactly the failure mode this subsystem's own HasWriteFailed design already anticipates
        // elsewhere). Letting that propagate here would skip _warningRaised/RestartCountForTests
        // below AND abort PushSamples before its own RestartCriticallyOverdue/Restarted raise --
        // silently defeating the overflow-safety guarantee this whole class exists for, over a
        // disposal-time I/O failure in an already-outgoing, already-replaced instance. The swap
        // itself (_inner reassignment above) has already fully happened by this point regardless.
        try
        {
            outgoing.Dispose();
        }
        catch (IOException)
        {
        }

        _warningRaised = false;
        RestartCountForTests++;
    }

    private AnalogFmSstvDecoder CreateInner()
    {
        var decoder = _decoderFactoryForTests?.Invoke()
            ?? new AnalogFmSstvDecoder(afcEnabled: _afcEnabled, syncRestartEnabled: _syncRestartEnabled, autoSyncEnabled: _autoSyncEnabled, autoStopEnabled: _autoStopEnabled, autoSlantEnabled: _autoSlantEnabled, senseLevel: _senseLevel, demodType: _demodType, rxBpfPreset: _rxBpfPreset, rxBufferMode: _rxBufferMode);
        decoder.StationIdDecodeEnabled = _stationIdDecodeEnabled;
        decoder.LineDecoded += OnLineDecoded;
        decoder.ModeDetected += OnModeDetected;
        decoder.DecodeRestarted += OnDecodeRestarted;
        decoder.StationIdDecoded += OnStationIdDecoded;
        return decoder;
    }

    private void UnsubscribeFrom(AnalogFmSstvDecoder decoder)
    {
        decoder.LineDecoded -= OnLineDecoded;
        decoder.ModeDetected -= OnModeDetected;
        decoder.DecodeRestarted -= OnDecodeRestarted;
        decoder.StationIdDecoded -= OnStationIdDecoded;
    }

    private void OnLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    private void OnModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    private void OnDecodeRestarted(SstvModeDefinition mode) => DecodeRestarted?.Invoke(mode);

    private void OnStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

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
