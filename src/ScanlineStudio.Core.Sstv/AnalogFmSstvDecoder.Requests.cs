using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    /// <summary>See <see cref="ISstvDecoder.ResetAgc"/> for the full concurrency contract (D0-audit
    /// round-6 finding: this is the one public mutator on this class that writes DSP state directly
    /// and synchronously instead of through a deferred flag <see cref="PushSamples"/> consumes --
    /// contrast <see cref="RequestReSync"/>/<see cref="RequestCorrectSlant"/>/<see cref="ForceMode"/>
    /// immediately below). Callers must not call this while a concurrent <see cref="PushSamples"/>
    /// call could be reading/writing the same <see cref="LevelAgc"/> instance.</summary>
    public void ResetAgc() => _levelAgc.Init();

    /// <summary>See <see cref="ISstvDecoder.RequestReSync"/>. A single volatile write, safe from any
    /// thread -- consumed at the top of the next <see cref="PushSamples"/> call, on whichever thread
    /// actually calls that (matching this class's own established single-caller-thread contract).</summary>
    public void RequestReSync() => _reSyncRequested = true;

    /// <summary>See <see cref="ISstvDecoder.RequestNotch"/>. A single atomic exchange, safe from any
    /// thread -- consumed at the top of the next <see cref="PushSamples"/> call, same shape and same
    /// consumption point as <see cref="_reSyncRequested"/> immediately above (unlike ReSync, this is
    /// persistent STATE, not a one-shot command -- see <see cref="ApplyPendingNotchRequest"/>'s own
    /// doc comment for how the enabled/frequency state itself, as opposed to just this request latch,
    /// survives across calls).</summary>
    public void RequestNotch(bool enabled, double? frequencyHz) => Interlocked.Exchange(ref _pendingNotchRequest, new NotchRequest(enabled, frequencyHz));

    /// <summary>See <see cref="ISstvDecoder.RequestPllTuning"/>. Same atomic-exchange shape as
    /// <see cref="RequestNotch"/> immediately above -- consumed at the top of the next
    /// <see cref="PushSamples"/> call by <see cref="ApplyPendingPllTuningRequest"/>. All 5 fields
    /// are always set together as one group (never independently), so a plain exchange is correct --
    /// no CAS-merge needed, unlike <see cref="_desiredFlags"/>'s own 4-independent-property shape.</summary>
    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz) =>
        Interlocked.Exchange(ref _pendingPllTuningRequest, new PllTuningRequest(PllFmDemodulator.NormalizeVcoGain(vcoGain), loopOrder, loopCutoffHz, outputOrder, outputCutoffHz));

    /// <summary>See <see cref="ISstvDecoder.RequestZeroCrossingTuning"/>. Same atomic-exchange shape as
    /// <see cref="RequestPllTuning"/> immediately above -- consumed at the top of the next
    /// <see cref="PushSamples"/> call by <see cref="ApplyPendingZeroCrossingTuningRequest"/>.</summary>
    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz) =>
        Interlocked.Exchange(ref _pendingZeroCrossingTuningRequest, new ZeroCrossingTuningRequest(smoothingMode, outputOrder, outputCutoffHz, smoothingFrequencyHz));

    /// <summary>See <see cref="ISstvDecoder.ArmScopeCapture"/>. A single atomic exchange, safe from
    /// any thread -- consumed at the top of the next <see cref="PushSamples"/> call, same shape as
    /// <see cref="RequestNotch"/> immediately above. Re-arming while a previous capture is still
    /// in progress or already complete-but-unread discards it, matching legacy's own
    /// <c>TrigNext</c> re-trigger semantics (see <see cref="ScopeCaptureBuffer.Arm"/>'s own doc
    /// comment). Auditor code-review finding: validated at the call boundary, not left to throw
    /// on the decode thread inside a later <see cref="PushSamples"/> call (a negative
    /// <paramref name="size"/> would otherwise reach <c>new double[size]</c> deep inside
    /// <see cref="ScopeCaptureBuffer.Arm"/>) -- same synchronous-guard convention
    /// <see cref="RestartableSstvDecoder"/>'s own chunk-size check already uses.</summary>
    public void ArmScopeCapture(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Interlocked.Exchange(ref _pendingScopeCaptureArm, new ScopeCaptureArmRequest(size));
    }

    /// <summary>See <see cref="ISstvDecoder.TryGetScopeCaptureChannel0"/>. Safe from any thread, at
    /// any time -- see <see cref="ScopeCaptureBuffer.TrySnapshot"/>'s own doc comment.</summary>
    public double[]? TryGetScopeCaptureChannel0() => _scopeCaptureChannel0.TrySnapshot();

    /// <summary>See <see cref="ISstvDecoder.TryGetScopeCaptureChannel1"/>. Safe from any thread, at
    /// any time -- see <see cref="ScopeCaptureBuffer.TrySnapshot"/>'s own doc comment.</summary>
    public double[]? TryGetScopeCaptureChannel1() => _scopeCaptureChannel1.TrySnapshot();

    /// <summary>See <see cref="ISstvDecoder.RequestCorrectSlant"/>. A single volatile write, safe from
    /// any thread -- consumed inside <see cref="TryProcessBuffer"/>'s own per-line loop on whichever
    /// thread next calls <see cref="PushSamples"/>, NOT at the top of that call (see
    /// <see cref="_correctSlantRequested"/>'s own doc comment for why).</summary>
    public void RequestCorrectSlant() => _correctSlantRequested = true;

    /// <summary>See <see cref="ISstvDecoder.ForceMode"/>. A single atomic exchange, safe from any
    /// thread -- consumed at the top of the next <see cref="PushSamples"/> call, on whichever thread
    /// actually calls that (matching this class's own established single-caller-thread contract; no
    /// thread marshaling, no synchronization context, no background dispatch).</summary>
    public void ForceMode(SstvModeDefinition mode) => Interlocked.Exchange(ref _forcedMode, mode);

    /// <summary>See <see cref="ISstvDecoder.SetModeLock"/>. <see langword="null"/> unlocks. A
    /// deferred-request latch, not a plain field write -- see <see cref="_pendingModeLockRequest"/>'s
    /// own doc comment for why (same reasoning as <see cref="SenseLevel"/>'s own setter: a plain
    /// cross-thread write gives <see cref="PushSamplesCore"/> no memory-model ordering guarantee
    /// against <see cref="_lockedMode"/>, which <see cref="Commit"/> reads on every commit).
    /// Independent of <see cref="ForceMode"/> above -- setting a lock does not affect any in-flight
    /// forced mode, and a forced mode does not affect the lock (see <see cref="Commit"/>'s own
    /// <c>applyModeLock</c> parameter).</summary>
    public void SetModeLock(SstvModeDefinition? mode) => Interlocked.Exchange(ref _pendingModeLockRequest, new ModeLockRequest(mode));

    /// <summary>See <see cref="ISstvDecoder.RequestAbandonReception"/>. A single atomic exchange,
    /// safe from any thread -- consumed at the very top of the next <see cref="PushSamples"/> call,
    /// strictly BEFORE the <see cref="_forcedMode"/> drain (plan-review round 3 finding: draining
    /// after would let a pending abandon silently tear down a mode <see cref="ForceMode"/> just
    /// committed in the same call -- draining first means <see cref="PerformForceMode"/> always
    /// sees an already-idle decoder, so a mode forced while paused just proceeds normally, no
    /// special-case interaction code needed anywhere).</summary>
    public void RequestAbandonReception() => Interlocked.Exchange(ref _abandonRequested, 1);

    /// <summary>See <see cref="ISstvDecoder.SlantPpm"/>. Thin wrapper over
    /// <see cref="SlantTracker.DriftPpm"/>, but NOT simply <c>_slantTracker?.DriftPpm</c> -- auditor
    /// finding: <see cref="_slantTracker"/> alone is not null in every case the interface documents as
    /// null. <see cref="AbandonInProgressImage"/> (the AVT training-resettle hand-off,
    /// `sstv.cpp`'s equivalent lock-abandon path) nulls <see cref="_mode"/> but deliberately leaves
    /// <see cref="_slantTracker"/> alive (see that method's own doc comment) for up to ~7.1s while a
    /// new AVT training lock resolves -- without the explicit <see cref="_mode"/> check here, a
    /// polling GUI would keep reading the ABANDONED image's stale drift for that whole window, not
    /// null. Round-1 D0-audit finding: a SECOND, distinct leak window exists for every NON-AVT
    /// mid-reception restart too -- <see cref="Commit"/> reassigns <see cref="_mode"/> to the NEW
    /// mode immediately, but <see cref="_slantTracker"/> is not rebuilt until
    /// <c>FinalizeAnchorAndStartDecoding</c> resolves <see cref="_pendingAnchorCorrectionMode"/>
    /// (deliberately deferred, see that field's own doc comment) -- so a poll landing in that window
    /// would read the ABANDONED image's tracker attributed to the NEW mode instead of null. Checking
    /// <see cref="_pendingAnchorCorrectionMode"/> here closes that window too. Reads all three fields
    /// into locals once, not twice -- see <see cref="SyncOffsetSamples"/>'s own doc comment for why a
    /// cross-thread poll needs that even though this class documents no general thread-safety
    /// guarantee beyond the single-producer-thread contract every other member already assumes.</summary>
    public double? SlantPpm
    {
        get
        {
            var mode = _mode;
            var pendingAnchor = _pendingAnchorCorrectionMode;
            var tracker = _slantTracker;
            return mode is null || pendingAnchor is not null ? null : tracker?.DriftPpm;
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SyncSource"/>. Directly reuses this class's own
    /// already-vetted <c>IsIdle</c> invariant (<see cref="_mode"/> null and
    /// <see cref="_avtTrainingPending"/> false), not a separately-derived condition -- same
    /// once-into-locals read pattern as <see cref="SlantPpm"/> above, for the same cross-thread-poll
    /// torn-read reasoning.
    ///
    /// Auditor finding, 2026-08-25: checking <c>mode is not null</c> FIRST is load-bearing, not
    /// arbitrary ordering -- <c>TryResolveAvtTraining</c>'s narrow-FSK exits (case 4977-4983 and
    /// 4987-4998) call <c>Commit()</c>, which sets <see cref="_mode"/>, several lines BEFORE
    /// <see cref="_avtTrainingPending"/> is cleared back to <see langword="false"/>. A poll landing
    /// in that narrow window has both fields true/non-null simultaneously; a narrow mode genuinely
    /// owns decoder state there (training is already dead), so <see cref="SstvSyncSource.Locked"/>
    /// is correct -- but only because this checks <c>mode</c> first. Reordering to check
    /// <c>avtTrainingPending</c> first would silently misreport that window as still training.</summary>
    public SstvSyncSource SyncSource
    {
        get
        {
            var mode = _mode;
            var avtTrainingPending = _avtTrainingPending;
            return mode is not null
                ? SstvSyncSource.Locked
                : avtTrainingPending ? SstvSyncSource.AvtTraining : SstvSyncSource.Idle;
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SyncOffsetSamples"/>. Deliberately reads
    /// <see cref="_lastLineSyncPeakPosition"/>, NOT <see cref="ComputeAutoSyncPosition"/>'s own live
    /// <see cref="_slantLinePeakPosition"/> input -- that field is a within-line accumulator reset to
    /// 0 at the end of every completed line, so a caller reading it from outside the decode loop
    /// (exactly what a public property getter is) would not reliably observe the value for whichever
    /// line was current when <see cref="PushSamples"/> last returned (see that field's own doc
    /// comment). <see cref="_lastLineSyncPeakPosition"/> is the field already designed for this
    /// exact after-the-fact read -- <see cref="PerformReSync"/>'s own deadband check uses it the same
    /// way.
    ///
    /// Auditor finding: each of <see cref="_mode"/>/<see cref="_slantTracker"/>/
    /// <see cref="_lastLineSyncPeakPosition"/> is read into a local exactly ONCE, not re-read between
    /// the null-check and its use -- a caller on a different thread than whichever one calls
    /// <see cref="PushSamples"/> (the documented intended use: a GUI polling this on a timer) can race
    /// a decode-thread write that nulls one of these fields (<see cref="ResetReSyncState"/> alone runs
    /// at the end of EVERY image) between two separate reads of the same field. An earlier version of
    /// this getter checked <c>_lastLineSyncPeakPosition.HasValue</c> then separately read
    /// <c>_lastLineSyncPeakPosition.Value</c> -- two non-volatile field loads of the same
    /// <see cref="Nullable{T}"/>, which a race between them can turn into a thrown
    /// <see cref="InvalidOperationException"/> instead of a clean null. Reading once into a local and
    /// pattern-matching it removes THAT window (the throw) -- it does NOT make the read atomic (D2
    /// round 1 correction: an earlier version of this sentence said "entirely", which overclaimed).
    /// <see cref="Nullable{T}"/> is a multi-field struct (a bool plus the payload), so a single field
    /// read here is a struct copy, not a guaranteed-atomic primitive load -- a genuinely concurrent
    /// writer (<see cref="ResetReSyncState"/> nulling this field mid-copy) could in principle observe a
    /// torn combination of an old HasValue with a new payload or vice versa. Never throws either way
    /// (the local is a plain value, not re-checked), and the worst observable outcome is a stale or
    /// zero-valued snapshot on one polling tick -- a cosmetic diagnostic blip, not a correctness bug
    /// for decoded pixel data, which this property never touches.</summary>
    public int? SyncOffsetSamples
    {
        get
        {
            var mode = _mode;
            var tracker = _slantTracker;
            var lastPeak = _lastLineSyncPeakPosition;
            return mode is null || tracker is null || lastPeak is not double peak
                ? null
                : WrapSyncOffset((int)peak, mode);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SignalPeakLevel"/>. <see cref="_levelAgc"/> is
    /// decoder-lifetime (constructed once in the ctor, unlike <see cref="_slantTracker"/>'s
    /// per-lock construction), so there is no null/not-ready case to model here -- a single plain
    /// field read of <see cref="LevelAgc.CurMax"/>, which is itself already <c>0.0</c> (not
    /// garbage) before its first <c>Fix()</c> window completes.</summary>
    public double SignalPeakLevel => _levelAgc.CurMax / 32768.0;

    /// <summary>See <see cref="ISstvDecoder.IsLevelOverdriven"/>. Deliberately its own independent
    /// read of <see cref="LevelAgc.CurMax"/>, not derived from <see cref="SignalPeakLevel"/>'s own
    /// already-divided value -- see that property's interface doc comment for why the two can
    /// disagree by one sample generation, an accepted consequence of two separate reads, not a
    /// bug to fix by coupling them.</summary>
    public bool IsLevelOverdriven => _levelAgc.CurMax >= 24578.0;

    /// <summary>See <see cref="ISstvDecoder.AutoSlantEnabled"/> -- genuinely live now (2026-08-27,
    /// restart-required-settings backlog item 1), same deferred-latch mechanism as
    /// <see cref="AutoSyncEnabled"/>/<see cref="AutoStopEnabled"/>/<see cref="SyncRestartEnabled"/>
    /// below -- see <see cref="_desiredFlags"/>'s own doc comment for the full design and the getter-
    /// contract split between this class and <see cref="RestartableSstvDecoder"/>. The getter returns
    /// the already-applied value currently in effect (last-drained, mirroring <see cref="SenseLevel"/>'s
    /// own inner-layer getter), not the last-requested one.</summary>
    public bool AutoSlantEnabled
    {
        get => _autoSlantEnabled;
        set
        {
            DecoderFlagsRequest current, updated;
            do
            {
                current = Volatile.Read(ref _desiredFlags);
                updated = current with { AutoSlantEnabled = value };
            } while (Interlocked.CompareExchange(ref _desiredFlags, updated, current) != current);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.AutoSyncEnabled"/> -- genuinely live now (2026-08-27).
    /// See <see cref="AutoSlantEnabled"/>'s own doc comment immediately above for the shared
    /// mechanism and getter contract.</summary>
    public bool AutoSyncEnabled
    {
        get => _autoSyncEnabled;
        set
        {
            DecoderFlagsRequest current, updated;
            do
            {
                current = Volatile.Read(ref _desiredFlags);
                updated = current with { AutoSyncEnabled = value };
            } while (Interlocked.CompareExchange(ref _desiredFlags, updated, current) != current);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.AutoStopEnabled"/> -- genuinely live now (2026-08-27).
    /// See <see cref="AutoSlantEnabled"/>'s own doc comment above for the shared mechanism and getter
    /// contract. <b>Real behavioral note, not a bug</b>: the counter this flag gates
    /// (<see cref="_autoStopCnt"/>, see <see cref="TryAutoSync"/>) accumulates regardless of this
    /// flag's own value -- so enabling Auto-stop mid-reception can abort the CURRENT image on the
    /// very next qualifying line if the counter already reached its threshold (8) before this flag
    /// was turned on. Confirmed legacy-faithful for the counter's own accumulation-while-off shape
    /// (`Main.cpp:3931`), but see <see cref="_autoStopEnabled"/>'s own doc comment for a related,
    /// newly-reachable divergence once all three of this flag's sibling gates are independently
    /// live.</summary>
    public bool AutoStopEnabled
    {
        get => _autoStopEnabled;
        set
        {
            DecoderFlagsRequest current, updated;
            do
            {
                current = Volatile.Read(ref _desiredFlags);
                updated = current with { AutoStopEnabled = value };
            } while (Interlocked.CompareExchange(ref _desiredFlags, updated, current) != current);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SyncRestartEnabled"/> -- genuinely live now (2026-08-27).
    /// See <see cref="AutoSlantEnabled"/>'s own doc comment above for the shared deferred-latch
    /// mechanism. UNLIKE its three siblings there, a change to THIS flag additionally rebuilds
    /// <see cref="_searchBandpassFilter"/>'s H1 coefficients and, on the false-&gt;true edge only,
    /// re-anchors <see cref="VisLockStateMachine"/> -- see <see cref="ApplyPendingDecoderFlagsRequest"/>
    /// for why (a real, reachable decode-thread crash otherwise, found by plan-review round 1).</summary>
    public bool SyncRestartEnabled
    {
        get => _syncRestartEnabled;
        set
        {
            DecoderFlagsRequest current, updated;
            do
            {
                current = Volatile.Read(ref _desiredFlags);
                updated = current with { SyncRestartEnabled = value };
            } while (Interlocked.CompareExchange(ref _desiredFlags, updated, current) != current);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.AfcEnabled"/>. Same deferred-latch mechanism and getter
    /// contract as <see cref="AutoSlantEnabled"/> above. UNLIKE its siblings there, a change to THIS
    /// flag additionally tears down or rebuilds <see cref="_afcTracker"/> mid-image and resets the
    /// five AFC-retuned resonators -- see <see cref="ApplyPendingDecoderFlagsRequest"/> for the
    /// legacy citations.</summary>
    public bool AfcEnabled
    {
        get => _afcEnabled;
        set
        {
            DecoderFlagsRequest current, updated;
            do
            {
                current = Volatile.Read(ref _desiredFlags);
                updated = current with { AfcEnabled = value };
            } while (Interlocked.CompareExchange(ref _desiredFlags, updated, current) != current);
        }
    }

    /// <summary>Drains <see cref="_desiredFlags"/> -- see that field's own doc comment for the full
    /// mechanism. Called from <see cref="PushSamplesCore"/> immediately after
    /// <see cref="ApplyPendingSenseLevelRequest"/> (order between the two doesn't matter -- zero
    /// field overlap, confirmed: that method touches <see cref="_senseLevel"/>/<see cref="_slvl"/>/
    /// <see cref="_slvl2"/>/<see cref="_slvl3"/>/<see cref="VisLockStateMachine"/>'s thresholds only;
    /// this one touches <see cref="_autoSyncEnabled"/>/<see cref="_autoStopEnabled"/>/
    /// <see cref="_autoSlantEnabled"/>/<see cref="_syncRestartEnabled"/>/<see cref="_afcEnabled"/>/
    /// <see cref="_searchBandpassFilter"/>/<see cref="_visLockProcessedUpTo"/>/
    /// <see cref="_visLockOriginSample"/>/<see cref="_afcTracker"/>/<see cref="_afcProcessedUpTo"/>/
    /// <see cref="_lastAppliedAfcRetuneHz"/> and the five AFC-retuned resonators). MUST stay below the
    /// abandon/ForceMode/ReSync/Notch block above in <see cref="PushSamplesCore"/> (plan-review round
    /// 2 nit) -- that block can move <see cref="_consumedSamples"/>, which the re-anchor below
    /// reads.</summary>
    private void ApplyPendingDecoderFlagsRequest()
    {
        var desired = Volatile.Read(ref _desiredFlags);
        if (ReferenceEquals(desired, _appliedFlags))
        {
            return; // nothing requested since the last drain
        }

        var previouslyAppliedSyncRestart = _appliedFlags.SyncRestartEnabled;
        var previouslyAppliedAfc = _appliedFlags.AfcEnabled;
        _appliedFlags = desired;

        _autoSyncEnabled = desired.AutoSyncEnabled;
        _autoStopEnabled = desired.AutoStopEnabled;
        _autoSlantEnabled = desired.AutoSlantEnabled;
        _syncRestartEnabled = desired.SyncRestartEnabled;
        _afcEnabled = desired.AfcEnabled;

        ApplyAfcEnabledEdge(previouslyAppliedAfc);

        if (_syncRestartEnabled != previouslyAppliedSyncRestart)
        {
            // H1's fcl is the only tap-count-invariant coefficient this flag affects
            // (SearchBandpassFilter's own doc comment) -- rebuilding it here on EVERY change (both
            // directions) is a deliberate DIVERGENCE from legacy's own Options-OK path, which does
            // NOT rebuild H1 for this specific change (Option.cpp:609-611 assigns m_SyncRestart
            // AFTER SetBPF already ran, with no later CalcBPF call) -- it matches legacy's
            // TOOLBAR-toggle path instead (Main.cpp:11887-11888's KRARClick, which DOES call
            // CalcBPF) -- see SearchBandpassFilter.UpdateSyncRestart's own doc comment for the full
            // reasoning, including why this call is safe with no Volatile.Write/lock (single writer,
            // this same decode thread, no other reader in production).
            _searchBandpassFilter?.UpdateSyncRestart(_syncRestartEnabled); // null-safe: RxBpfPreset.Off has no filter

            // Gated on _mode is not null (plan-review round 2 correction -- round 1's "unconditional"
            // framing was wrong): this re-anchor exists ONLY to fix one real hazard -- _visLockProcessedUpTo
            // frozen behind the trim watermark while SyncRestart was off AND a mode was locked
            // (TrimBuffers' own :2855-area comment). Pre-lock, _visLockProcessedUpTo is never frozen
            // (included unconditionally in the pre-lock watermark branch) and
            // _visLockStateMachine.Reset() would NOT be a no-op there -- it would discard in-flight
            // VIS header detection state, silently dropping whatever is arriving. Legacy has no
            // equivalent to gate against: it feeds its own sync-search state every sample
            // unconditionally (sstv.cpp:1847-1858) and NEVER resets anything on a flag flip
            // (Main.cpp:11887-11888) -- it simply resumes the frozen m_sint* state. This Reset() is a
            // mitigation for THIS PORT's own frozen-state-machine architecture across a discontinuous
            // sample jump, not a ported legacy behavior -- say so, don't claim parity. Re-anchor
            // target reuses Commit()'s own established triple verbatim (see that method's re-anchor
            // for the identical pattern at a mode-lock boundary).
            if (_mode is not null && !previouslyAppliedSyncRestart && _syncRestartEnabled)
            {
                _visLockStateMachine.Reset();
                _visLockProcessedUpTo = Math.Max(_visLockProcessedUpTo, _consumedSamples);
                _visLockOriginSample = _visLockProcessedUpTo;
            }
        }
    }

    /// <summary>Applies an <see cref="AfcEnabled"/> change mid-image, matching legacy's own
    /// <c>SBAFCClick</c> (<c>Main.cpp:6013-6016</c>): it assigns <c>m_afc</c> and calls
    /// <c>InitAFC()</c> on the OFF transition ONLY. <c>m_afc</c> is then read per sample
    /// (<c>sstv.cpp:2258</c>/<c>2263</c>/<c>2267</c> gate the <c>SyncFreq</c> update, and
    /// <c>sstv.cpp:2270</c>'s <c>if( m_afc ) d += m_AFCDiff;</c> gates the standing correction), so
    /// the toggle takes effect immediately rather than at the next image.
    ///
    /// FORWARD-ONLY, which is legacy's own shape rather than a shortcut: legacy adds
    /// <c>m_AFCDiff</c> into <c>d</c> and stores the result (<c>sstv.cpp:2289</c>), so pixels already
    /// written keep their correction there too. Nothing is un-applied here either. This port's
    /// run-ahead is bounded to the current line and this drain runs before the sample-append loop,
    /// so a toggle lands at the next LINE boundary against legacy's next SAMPLE.
    ///
    /// Gated on a locked mode: with no mode there is no tracker to tear down and no resonator that
    /// was ever retuned. AVT is excluded for the same reason <see cref="InitializeAfc"/> excludes it
    /// -- its tracker is always null, so both edges would be no-ops.</summary>
    private void ApplyAfcEnabledEdge(bool previouslyApplied)
    {
        if (_afcEnabled == previouslyApplied || _mode is null || _mode == SstvModeRegistry.Avt)
        {
            return;
        }

        if (!_afcEnabled)
        {
            // Legacy's InitAFC() -> InitTone(0) (sstv.cpp:1658-1693/1695-1705) retunes all FIVE
            // AFC-retuned resonators (m_iir11/12/13/19/fsk) back to nominal. InitializeAfc's own copy
            // of this reset lists only four because _syncEnvelopeDetector does not exist yet at that
            // point in a lock; mid-image it does, and leaving it retuned would strand the sync
            // envelope detector at the last correction while nothing updates it again.
            _afcTracker = null;
            _lastAppliedAfcRetuneHz = null;
            _syncEnvelopeDetector?.Retune(0);
            _visLockStateMachine.Retune(0);
            _visDataD19Detector.Retune(0);
            _fskSpaceDetector.Retune(0);
            _visDataD12Detector.Retune(0);
            return;
        }

        // Re-base before rebuilding: ApplyAfcCorrections early-returns while the tracker is null, so
        // _afcProcessedUpTo stalled wherever the OFF edge left it. Without this the loop would walk
        // the new tracker across audio already decoded uncorrected. Same Math.Max pattern, and same
        // reason, as InitializeAfc's own cursor guard.
        _afcProcessedUpTo = Math.Max(_afcProcessedUpTo, _consumedSamples);

        // Legacy does NOT call InitAFC on the ON edge, and nothing touches AFC state while the flag
        // is off, so a fresh tracker here is legacy-equivalent: m_AFCDiff was already zeroed by the
        // OFF edge, and tracking resumes from nominal.
        CreateAfcTracker(_mode);
    }

    /// <summary>See <see cref="ISstvDecoder.SenseLevel"/> for the full contract. The getter returns
    /// the already-CLAMPED value (0-3) currently in effect. The setter does NOT write <see cref="_senseLevel"/>
    /// directly (unlike the constructor) -- it stores a <see cref="SenseLevelRequest"/> via
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/> (safe from any thread), applied by
    /// <see cref="ApplyPendingSenseLevelRequest"/> on whichever thread next calls
    /// <see cref="PushSamples"/>, the same deferred shape <see cref="RequestNotch"/> already uses.
    /// A deferred latch (not a direct cross-thread write) is required here, unlike the single-bool
    /// <see cref="StationIdDecodeEnabled"/>: (1) a plain write gives the push thread no memory-model
    /// ordering guarantee against these tight-loop-read fields; (2) <see cref="_slvl"/>/
    /// <see cref="_slvl2"/>/<see cref="_slvl3"/> plus <see cref="VisLockStateMachine"/>'s own copies
    /// are 5 separate writes with no atomicity across the group -- a decode function reading a mixed
    /// old/new preset pair mid-update would make a genuinely wrong, untestable decision; (3) draining
    /// only at a fixed point in <see cref="PushSamplesCore"/> keeps a decode a pure function of
    /// (samples, settings at chunk boundaries), not of OS thread scheduling.</summary>
    public int SenseLevel
    {
        get => _senseLevel;
        set => Interlocked.Exchange(ref _pendingSenseLevelRequest, new SenseLevelRequest(value));
    }

    /// <summary>Drains <see cref="_pendingSenseLevelRequest"/> -- see that field's own doc comment
    /// for the mechanism. A bare reassignment of <see cref="_senseLevel"/>/<see cref="_slvl"/>/
    /// <see cref="_slvl2"/>/<see cref="_slvl3"/> plus <see cref="VisLockStateMachine.UpdateThresholds"/>,
    /// deliberately nothing else -- matches legacy's own SetSenseLvl (`sstv.cpp:1793-1817`), a bare
    /// field reassignment with no state reset, called unconditionally on every Options OK
    /// (`Option.cpp:612-613`) regardless of whether the value actually changed. Re-applying the same
    /// value this way (e.g. the UI-layer guard reverting an invalid selection back to the last valid
    /// one) is therefore a safe no-op, not a special case to avoid.</summary>
    private void ApplyPendingSenseLevelRequest()
    {
        var request = Interlocked.Exchange(ref _pendingSenseLevelRequest, null);
        if (request is null)
        {
            return;
        }

        _senseLevel = request.Level is >= 0 and <= 3 ? request.Level : 0;
        (_slvl, _slvl2, _slvl3) = SenseLevelPresets[_senseLevel];
        _visLockStateMachine.UpdateThresholds(_slvl, _slvl2);
    }

    /// <summary>Drains <see cref="_pendingModeLockRequest"/> -- see that field's and
    /// <see cref="_lockedMode"/>'s own doc comments. A bare reassignment; the new value only takes
    /// effect at the NEXT <see cref="Commit"/> (whether that's the reception already in progress or a
    /// future one), never retroactively re-decoding whatever is currently committed.</summary>
    private void ApplyPendingModeLockRequest()
    {
        var request = Interlocked.Exchange(ref _pendingModeLockRequest, null);
        if (request is null)
        {
            return;
        }

        _lockedMode = request.Mode;
    }

    /// <summary>See <see cref="ISstvDecoder.RxBpfPreset"/>. Plain restart-only field readback.
    /// Previously only exposed test-only as <see cref="RxBpfPresetForTests"/>; this is the same
    /// field, now a real public member for the Input Chain card's "BPF" row.</summary>
    public RxBpfPreset RxBpfPreset => _rxBpfPreset;

    /// <summary>See <see cref="ISstvDecoder.SyncFrequencyCorrectionHz"/>. Same shape as
    /// <see cref="SlantPpm"/>'s fix above, for the same reason: <see cref="_afcTracker"/>, like
    /// <see cref="_slantTracker"/>, is deliberately NOT nulled by <see cref="AbandonInProgressImage"/>
    /// (see that method's own doc comment) -- so <see cref="_mode"/> must be checked explicitly,
    /// not just the tracker reference, or this would leak an abandoned image's stale correction
    /// for the whole AVT mid-reception training pending window. Round-1 D0-audit finding: also
    /// gated on <see cref="_pendingAnchorCorrectionMode"/>, same second leak window as
    /// <see cref="SlantPpm"/>'s own matching fix -- see that property's doc comment for the full
    /// reasoning. Fields read into locals once, same TOCTOU reasoning as
    /// <see cref="SyncOffsetSamples"/>.</summary>
    public double? SyncFrequencyCorrectionHz
    {
        get
        {
            var mode = _mode;
            var pendingAnchor = _pendingAnchorCorrectionMode;
            var tracker = _afcTracker;
            return mode is null || pendingAnchor is not null ? null : tracker?.CorrectionHz;
        }
    }

    // Un-stub-RX-tab Piece B (plan-review round 2 finding): TrimBuffers' own D12/D19 catch-up alone
    // (see D12At/D19At's own doc comments) only advances in ~44100-sample bursts from a position
    // seconds behind the stream head -- an armed capture relying on that alone would fill in one
    // late lump instead of tracking the live stream the way legacy's real per-sample write does.
    // Bounded by _levelAgcProcessedUpTo, NEVER TotalSamplesReceived -- keeps every AgcSampleAt call
    // inside D12At/D19At's own fill loop a pure cache read (every index it asks for is already
    // &lt;= watermark-derived bounds already &lt;= _levelAgcProcessedUpTo, so never a NEW AGC fill),
    // matching the invariant TrimBuffers' own D12/D19 catch-up already relies on. Auditor code-review
    // correction: the earlier version of this comment claimed watermark &lt;= _levelAgcProcessedUpTo
    // alone was sufficient -- it covers the pure-cache-read half, but D12At(_levelAgcProcessedUpTo-1)
    // ALSO needs _levelAgcProcessedUpTo-1 &gt;= _bufferBase or Rel() throws; that currently holds only
    // via TrimBuffers' own separate watermark chain (Math.Min against _bandpassFilteredProcessedUpTo,
    // itself &gt;= _levelAgcProcessedUpTo per AgcSampleAt), not via anything stated here -- the explicit
    // guard below is real insurance, not decoration.
    private void ForceScopeCaptureChannel0Progress()
    {
        if (!_scopeCaptureChannel0.IsCapturing || _levelAgcProcessedUpTo - 1 < _bufferBase)
        {
            return;
        }

        if (_scopeCaptureChannel0.UsesD19)
        {
            D19At(_levelAgcProcessedUpTo - 1);
        }
        else
        {
            D12At(_levelAgcProcessedUpTo - 1);
        }
    }

    // Un-stub-RX-tab Piece B: consumes _pendingScopeCaptureArm, matching ApplyPendingNotchRequest's
    // own shape/consumption point exactly.
    //
    // Auditor code-review finding (real bug, not a nit): while genuinely locked, the ONLY thing that
    // ever advances _visDataD12ProcessedUpTo/_visDataD19ProcessedUpTo forward is TrimBuffers' own
    // catch-up (TryInterleavedHeaderScan's per-sample D12At call is unreachable once _mode is set) --
    // that catch-up only fires once the pending trim reaches MinTrimSamples (44100) and even then only
    // reaches watermark-AnchorWarmupSamples (2000), so between trims these cursors can trail
    // _levelAgcProcessedUpTo by up to ~46,100 samples (~4.2s @ 11025Hz). Arming without first
    // discarding that backlog meant ForceScopeCaptureChannel0Progress's FIRST call after an arm would
    // walk the whole stale gap in one shot and fill the fresh buffer with historical, not
    // trigger-instant, samples -- exactly the "one late lump" failure this whole mechanism exists to
    // avoid, just moved to arm time instead of gone. Fixed: drain the backlog into the OLD (about to
    // be replaced) buffer BEFORE arming, so Arm() starts both channels genuinely empty and the very
    // next real write is at-or-after the trigger.
    private void ApplyPendingScopeCaptureArm()
    {
        var request = Interlocked.Exchange(ref _pendingScopeCaptureArm, null);
        if (request is null)
        {
            return;
        }

        var usesD19 = _mode is not null && _mode.NarrowModeCode is not null;
        if (_levelAgcProcessedUpTo - 1 >= _bufferBase)
        {
            if (usesD19)
            {
                D19At(_levelAgcProcessedUpTo - 1);
            }
            else
            {
                D12At(_levelAgcProcessedUpTo - 1);
            }
        }

        _scopeCaptureChannel0.Arm(request.Size);
        _scopeCaptureChannel1.Arm(request.Size);
        // Latched here, held for the whole capture -- see _scopeCaptureChannel0.UsesD19's own doc
        // comment for why. _mode is null before any lock; that correctly defaults to d12 (wide),
        // matching legacy's own m_fNarrow default of false.
        _scopeCaptureChannel0.UsesD19 = usesD19;
    }

    // Options stub backlog item 1 (docs/plans/options-stub-item1-pll-tuning-plan.md): consumes
    // _pendingPllTuningRequest, applying to BOTH PLL instances -- the picture-path _pllDemodulator
    // (always live) and, if an AVT-training attempt is currently in flight, _avtPllDemodulator too
    // (null-conditional -- legacy's single m_pll instance affects both paths, so a retune arriving
    // mid-AVT-attempt must reach it, not just the not-yet-started case _avtPllDemodulator's own
    // construction site already covers). Also updates the CURRENT-tuning fields (_pllVcoGain etc.)
    // BEFORE calling SetTuning -- these are read by _avtPllDemodulator's own construction site for a
    // FUTURE AVT attempt, so they must reflect the latest request even if no AVT attempt is active
    // right now to apply it to directly.
    private void ApplyPendingPllTuningRequest()
    {
        var request = Interlocked.Exchange(ref _pendingPllTuningRequest, null);
        if (request is null)
        {
            return;
        }

        _pllVcoGain = request.VcoGain;
        _pllLoopOrder = request.LoopOrder;
        _pllLoopCutoffHz = request.LoopCutoffHz;
        _pllOutputOrder = request.OutputOrder;
        _pllOutputCutoffHz = request.OutputCutoffHz;

        _pllDemodulator.SetTuning(request.VcoGain, request.LoopOrder, request.LoopCutoffHz, request.OutputOrder, request.OutputCutoffHz);
        _avtPllDemodulator?.SetTuning(request.VcoGain, request.LoopOrder, request.LoopCutoffHz, request.OutputOrder, request.OutputCutoffHz);
    }

    /// <summary>Drains <see cref="_pendingZeroCrossingTuningRequest"/>, applying to BOTH
    /// <see cref="_zeroCrossingDemodulator"/> and <see cref="_afcZeroCrossingCounter"/> -- legacy's
    /// single <c>m_fqc</c> serves both roles, so whatever smoothing is configured affects both,
    /// mirroring <see cref="ApplyPendingPllTuningRequest"/> immediately above.</summary>
    private void ApplyPendingZeroCrossingTuningRequest()
    {
        var request = Interlocked.Exchange(ref _pendingZeroCrossingTuningRequest, null);
        if (request is null)
        {
            return;
        }

        _zeroCrossingSmoothingMode = request.SmoothingMode;
        _zeroCrossingOutputOrder = request.OutputOrder;
        _zeroCrossingOutputCutoffHz = request.OutputCutoffHz;
        _zeroCrossingSmoothingFrequencyHz = request.SmoothingFrequencyHz;

        _zeroCrossingDemodulator.SetTuning(request.SmoothingMode, request.OutputOrder, request.OutputCutoffHz, request.SmoothingFrequencyHz);
        _afcZeroCrossingCounter.SetTuning(request.SmoothingMode, request.OutputOrder, request.OutputCutoffHz, request.SmoothingFrequencyHz);
    }

    // Un-stub-RX-tab Piece A: consumes _pendingNotchRequest, applying the on/off/frequency change
    // itself plus legacy's own group-delay compensation for a toggle mid-sync (Main.cpp:14375-14379,
    // sstv.cpp:2271-2283). Two genuinely asymmetric directions, NOT one signed value with a shared
    // apply path -- see NotchFilter's own class doc comment and ApplyNotchDisableShift below for why
    // "insert N duplicated samples" has no representation in this port's absolute-index
    // architecture the way it does in legacy's write-pointer one.
    private void ApplyPendingNotchRequest()
    {
        var request = Interlocked.Exchange(ref _pendingNotchRequest, null);
        if (request is null)
        {
            return;
        }

        var wasEnabled = _notchFilter is not null;
        var tapForCompensation = wasEnabled ? _notchFilter!.Tap : 0;

        if (request.FrequencyHz is { } requestedHz)
        {
            _notchFrequencyHz = requestedHz; // persists across a disable, matching legacy's own CNotch::m_freq
        }

        if (request.Enabled)
        {
            if (_notchFilter is null)
            {
                _notchFilter = new NotchFilter(_sampleRate, _notchFrequencyHz);
                tapForCompensation = _notchFilter.Tap;
            }
            else if (request.FrequencyHz is not null)
            {
                _notchFilter.SetFrequency(_notchFrequencyHz); // in-place -- does NOT reset the delay line, see NotchFilter.SetFrequency's own doc comment
            }
        }
        else
        {
            _notchFilter = null;
        }

        // Main.cpp:14375: `(notch != pSound->m_notch) && pDem->m_Sync` -- only compensate for a REAL
        // toggle (not a same-state re-request or a retune-while-already-on/off), and only while
        // genuinely locked, matching PerformReSync's own gate.
        if (request.Enabled == wasEnabled || _mode is null || _slantTracker is null)
        {
            return;
        }

        // `notch` in Main.cpp:14363 is the OLD state, captured before the toggle; `if(notch) delay =
        // -delay;` -- enabling (old=false) leaves delay POSITIVE, disabling (old=true) makes it
        // NEGATIVE. Verify against the literal legacy source directly if this ever looks backwards,
        // don't trust a paraphrase (an earlier draft of this port's own plan had the two directions
        // swapped).
        var halfTap = tapForCompensation / 2;
        if (request.Enabled)
        {
            ApplySyncCorrection(halfTap); // reuses the EXISTING forward-drain mechanism verbatim -- see DrainPendingSkip
        }
        else
        {
            ApplyNotchDisableShift(halfTap);
        }
    }

    // Un-stub-RX-tab Piece A: the disable-direction's own instantaneous cursor rewind -- NOT an
    // extension of DrainPendingSkip/_pendingSkipSamples (plan-review finding: "insert N duplicated
    // samples into the demod stream" is unrepresentable in this port's absolute-index architecture;
    // the correct equivalent is the mapping-shift side effect legacy's own duplicate-insert achieves
    // -- for a fixed pixel, the input landing there becomes EARLIER by N). A single instantaneous
    // adjustment, no loop, no deferral -- unlike the forward direction, nothing needs to wait for
    // future samples to arrive, since the samples being "un-consumed" already exist.
    private void ApplyNotchDisableShift(int n)
    {
        if (n <= 0 || _mode is null || _slantTracker is null)
        {
            return;
        }

        if (_consumedSamples - n < _bufferBase)
        {
            // Rel() safety net -- n is capped at NotchFilter's own 256-tap-max/2=128, and
            // TrimBuffers' own locked watermark keeps at least AnchorWarmupSamples (2000) of margin
            // behind _consumedSamples, so this should never actually trip in practice; kept as an
            // explicit guard rather than relying on that margin alone.
            return;
        }

        _consumedSamples -= n;
        _idealLineStartSample -= n; // LOCKSTEP with _consumedSamples -- moving only one would stretch exactly one line by n instead of shifting the whole timeline uniformly
        _rxBufferAnchorSample -= n; // mirror of DrainPendingSkip's own ++ for the forward direction

        // _slantProcessedUpTo is DELIBERATELY NOT moved -- rewinding it would re-feed the stateful
        // _syncEnvelopeDetector a second time over the same span and re-append into
        // RxLineStagingBuffer, duplicating already-staged content. Legacy has this same asymmetry
        // for the same reason: its own filters (m_iir12/m_iir19/PLL/etc.) run BEFORE the m_Skip
        // check, so the duplicate-insert branch never re-runs them either.
        _slantIdealSamplesSoFarInLine += n; // mirror of DrainPendingSkip's own "deliberately not advanced" trick, opposite direction
        if (_slantIdealSamplesSoFarInLine >= _effectiveSamplesPerLine)
        {
            _slantIdealSamplesSoFarInLine -= _effectiveSamplesPerLine;
        }

        // Same shared-tail bookkeeping ApplySyncCorrection performs for the forward direction, minus
        // _pendingSkipSamples itself (this direction is instantaneous, nothing to defer). Deliberately
        // CLEARS any not-yet-drained forward skip rather than netting the two together -- every other
        // m_Skip-setting site in this file (ApplySyncCorrection, called by both PerformReSync and
        // TryAutoSync's triggers) already uses last-command-wins overwrite semantics for this exact
        // field, matching legacy's own per-trigger m_Skip assignment.
        _pendingSkipSamples = 0;
        _lastLineSyncPeakPosition = null;
        _suppressNextSlantProcessLine = true;
        _slantCorrectionsDisabledForRestOfImage = true;
        _autoSyncReferencePosition = null;
        _autoSyncCooldown = 6;
    }

    // Force-mode (legacy's RX quick-mode-button click, Main.cpp:6096-6122 -> CSSTVDEM::Start(mode,
    // TRUE), sstv.cpp:1749-1767 -> Start(void), sstv.cpp:1717-1747). Reuses the exact same
    // Commit()/_pendingAnchorCorrectionMode/TryResolveSyncAnchorCorrection/
    // FinalizeAnchorAndStartDecoding pipeline VIS auto-detect itself uses -- legacy-faithful, not an
    // invented shortcut: legacy's own Start() is shared between the VIS-auto path (sstv.cpp:1902-1903)
    // and the force-mode path.
    private void PerformForceMode(SstvModeDefinition mode)
    {
        // Captured BEFORE anything below mutates them. previousMode: DecodeRestarted must report the
        // OLD mode, matching every other restart call site's convention. hadPendingAnchor: whether
        // previousMode ever actually reached ModeDetected -- Commit() fires ModeDetected immediately
        // only for AVT (FinalizeAnchorAndStartDecoding called inline); every other mode defers it
        // until TryResolveSyncAnchorCorrection succeeds, via _pendingAnchorCorrectionMode. A mode still
        // sitting in that field was never announced, so DecodeRestarted must not fire for it either --
        // see this method's own DecodeRestarted call below.
        var previousMode = _mode;
        var hadPendingAnchor = _pendingAnchorCorrectionMode is not null;

        // Aborts any in-progress AVT training. Legacy basis: Start(void) unconditionally lands on
        // m_SyncMode=0 (sstv.cpp:1744), and AVT training lives entirely in m_SyncMode states 4-8 (see
        // TryResolveAvtTraining's own doc comment for that state list) -- a forced start unambiguously
        // aborts in-flight training. Matches EndOfImage's own AVT cleanup exactly (same three fields).
        // Deliberately done HERE, not inside AbandonInProgressImage() -- the S7 AVT hand-off call site
        // relies on _avtTrainingPending surviving through that method (see its own doc comment).
        _avtTrainingPending = false;
        _avtTrainingLock = null;
        _avtPllDemodulator = null;

        // Also clears any OTHER mode's still-unresolved anchor correction. Functional-audit
        // correction (D6, round 2): _pendingAnchorCorrectionMode is now cleared in TWO other places
        // -- TryProcessBuffer's own resolution success path, and (defense-in-depth, added since this
        // comment was first written) AbandonInProgressImage -- not "exactly one," and this method
        // isn't the only place aware of the invariant anymore either. The core point stands: every
        // OTHER caller provably can't reach Commit() while this field is already set (see the
        // field's own doc comment for the current, corrected reachability argument), an invariant
        // ForceMode is the first to break. Left stale, forcing (say) AVT while a different mode's
        // anchor correction is still pending would leave that stale mode's entry behind: the next
        // TryProcessBuffer call would block AVT decoding until the stale mode's own 3-4-line window
        // fills, then resolve it against AVT's _lineDecoder/_consumedSamples and fire a spurious second
        // ModeDetected for a mode that isn't _mode anymore. hadPendingAnchor above is captured before
        // this clear, so the DecodeRestarted gate below still sees the pre-clear value.
        _pendingAnchorCorrectionMode = null;

        // Round-1 D0-audit finding: legacy's own force-mode-specific Start(int mode, int f) resets
        // the three sync-interval trackers here (sstv.cpp:1752-1754's m_sint1/m_sint2/m_sint3.Reset())
        // -- Start(void), the VIS-auto path this method otherwise shares, does NOT. Behaviorally inert
        // today (these are pre-lock-only state; Commit() below makes _mode non-null immediately,
        // silencing them, and EndOfImage's own reset block already re-clears all five of these fields
        // before the decoder's next pre-lock epoch) -- added for legacy fidelity and so a future
        // pre-lock-adjacent change can't silently observe stale state from before this force-mode call.
        // Round-6 D0-audit correction: round 1's own fix stopped short -- it reset the three
        // trackers' internal sample counters WITHOUT the paired origin/cursor fields
        // (_syncBypassOriginSample/_syncBypassProcessedUpTo) EndOfImage's own matching reset block
        // always resets alongside them (see that method's own reset list). CommitSyncBypassMatch
        // computes its line-start anchor as `_syncBypassOriginSample + (peak - midpoint)`, so
        // resetting the tracker's counter without re-anchoring the origin at the SAME instant leaves
        // a worse-formed pairing than before the reset, not a cleaner one -- the opposite of this
        // block's own stated intent. Still inert today for the identical reachability reason (Commit()
        // on the next line makes _mode non-null immediately), but now genuinely matches EndOfImage's
        // pairing instead of only half of it.
        _syncBypass1Tracker.Reset();
        _syncBypassTracker.Reset();
        _syncBypassNarrowTracker.Reset();
        _syncBypassNarrowPhaseActive = false;
        _syncBypassProcessedUpTo = TotalSamplesReceived;
        _syncBypassOriginSample = TotalSamplesReceived;

        // Anchor at TotalSamplesReceived, NOT _consumedSamples -- pre-lock, _consumedSamples is a
        // frozen header-search start that TrimBuffers stops protecting once _fixedWindowExhausted is
        // set, so on a decoder idle long enough it can sit behind _bufferBase; committing there would
        // read already-trimmed-away buffer and throw on this call's thread. TotalSamplesReceived is
        // always within TrimBuffers' retained window in both the pre-lock and already-locked cases (see
        // TrimBuffers' own watermark comments) and matches legacy's actual reset target -- Start(void)
        // zeroes m_wBase/m_wPage/m_rPage/m_rBase (sstv.cpp:1726-1730), "begin buffering from now", not
        // the m_wBgn=2 buffered-lines-gate flag.
        // applyModeLock: false -- see Commit's own doc comment: a manual quick-mode-button click
        // always wins its own reception, a one-shot exception to an active persistent lock, not a
        // change to it.
        Commit(mode, TotalSamplesReceived, applyModeLock: false);

        // See this method's own top comment for why this is gated on !hadPendingAnchor: firing
        // DecodeRestarted for a mode that never reached ModeDetected would violate the event's own
        // documented contract (a caller that allocates on ModeDetected and discards on DecodeRestarted
        // would discard a buffer it never allocated).
        if (previousMode is not null && !hadPendingAnchor)
        {
            RaiseSubscribers(DecodeRestarted, previousMode);
        }
    }

    // Decode thread only: drains the last requested toggle state.
    private void ApplyPendingSnrMeasurementRequest()
    {
        var requested = Volatile.Read(ref _snrMeasurementRequested) == 1;
        if (requested == _snrMeasurementEnabled)
        {
            return;
        }

        _snrMeasurementEnabled = requested;
        _snrExpectedAnchor = int.MinValue;
        _syncSnrTracker.ResetReception();
        PublishSnr();
    }
}
