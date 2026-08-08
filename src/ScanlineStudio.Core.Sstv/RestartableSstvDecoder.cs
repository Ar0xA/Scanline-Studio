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
/// <b>Locking</b>: the swap happens under <c>lock (_gate)</c> (plain <c>Monitor</c>, chosen
/// specifically for re-entrancy -- a critical-path consumer's response to
/// <see cref="RestartCriticallyOverdue"/> can call back into <see cref="ResetAgc"/> on the same
/// thread). All three events are raised strictly AFTER releasing the lock: a round-3 plan review found
/// a rare shutdown-timing path where raising inside the lock could let a handler's continuation resume
/// on a different thread and then contend for `_gate` against the (still-lock-holding) original
/// thread -- a genuine deadlock. Raising outside the lock removes the whole class, since the swap has
/// already fully happened by the time any handler runs.</summary>
public sealed class RestartableSstvDecoder : ISstvDecoder, ISstvDecoderMaintenance
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
    private readonly long _warningThresholdSamples;
    private readonly long _criticalThresholdSamples;
    private readonly object _gate = new();

    private AnalogFmSstvDecoder _inner;
    private bool _warningRaised;

    public event Action<DecodedImageUpdate>? LineDecoded;
    public event Action<SstvModeDefinition>? ModeDetected;
    public event Action<SstvModeDefinition>? DecodeRestarted;
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

    public RestartableSstvDecoder(bool afcEnabled = true, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false)
        : this(afcEnabled, DefaultWarningThresholdSamples, DefaultCriticalThresholdSamples, syncRestartEnabled, autoSyncEnabled, autoStopEnabled)
    {
    }

    /// <summary>Test-only seam for injecting short thresholds instead of the real 12h/13h ones --
    /// see this class' own doc comment for why a clock-injection seam is unnecessary now that the
    /// trigger is sample-count-based, not wall-clock-based.</summary>
    internal RestartableSstvDecoder(bool afcEnabled, long warningThresholdSamples, long criticalThresholdSamples, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false)
    {
        _afcEnabled = afcEnabled;
        _syncRestartEnabled = syncRestartEnabled;
        _autoSyncEnabled = autoSyncEnabled;
        _autoStopEnabled = autoStopEnabled;
        _warningThresholdSamples = warningThresholdSamples;
        _criticalThresholdSamples = criticalThresholdSamples;
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

        current.PushSamples(samples);

        // Raised strictly after releasing _gate -- see this class' own doc comment.
        if (raiseCritical)
        {
            RestartCriticallyOverdue?.Invoke();
        }

        if (raiseRestarted)
        {
            Restarted?.Invoke();
        }

        if (raiseWarning)
        {
            RestartOverdue?.Invoke();
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
        UnsubscribeFrom(_inner);
        _inner = CreateInner();
        _warningRaised = false;
        RestartCountForTests++;
    }

    private AnalogFmSstvDecoder CreateInner()
    {
        var decoder = new AnalogFmSstvDecoder(afcEnabled: _afcEnabled, syncRestartEnabled: _syncRestartEnabled, autoSyncEnabled: _autoSyncEnabled, autoStopEnabled: _autoStopEnabled);
        decoder.LineDecoded += OnLineDecoded;
        decoder.ModeDetected += OnModeDetected;
        decoder.DecodeRestarted += OnDecodeRestarted;
        return decoder;
    }

    private void UnsubscribeFrom(AnalogFmSstvDecoder decoder)
    {
        decoder.LineDecoded -= OnLineDecoded;
        decoder.ModeDetected -= OnModeDetected;
        decoder.DecodeRestarted -= OnDecodeRestarted;
    }

    private void OnLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    private void OnModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    private void OnDecodeRestarted(SstvModeDefinition mode) => DecodeRestarted?.Invoke(mode);
}
