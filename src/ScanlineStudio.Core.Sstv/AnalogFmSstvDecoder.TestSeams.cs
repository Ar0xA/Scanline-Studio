using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    /// <summary>Diagnostic-only: largest retained length among the four decoder-wide sample caches
    /// that must trim in lockstep with <see cref="BufferedSampleCount"/>. A maximum (rather than a
    /// sum) lets the bounded-retention test fail if any single cache stops trimming.</summary>
    internal int LargestCoreSampleCacheBufferedCountForTests => Math.Max(
        Math.Max(_demodulatedFrequencies.Count, _bandpassFilteredSamples.Count),
        Math.Max(_agcSamples.Count, _agcCurMaxSamples.Count));

    /// <summary>Diagnostic-only: how far AGC's own forward-fill cursor has advanced -- the same
    /// bound <see cref="ForceScopeCaptureChannel0Progress"/>/<see cref="ApplyPendingScopeCaptureArm"/>
    /// use for their own D12/D19 catch-up calls.</summary>
    internal int LevelAgcProcessedUpToForTests => _levelAgcProcessedUpTo;

    /// <summary>Diagnostic-only: exposes <see cref="_searchBandpassFilter"/> so a test can prove
    /// <see cref="SyncRestartEnabled"/>'s live setter actually rebuilt H1's coefficients (see
    /// <see cref="SearchBandpassFilter.H1ForTests"/>), not just that it ran without throwing.
    /// <see langword="null"/> under <see cref="RxBpfPreset.Off"/>, same as the field itself.</summary>
    internal SearchBandpassFilter? SearchBandpassFilterForTests => _searchBandpassFilter;

    /// <summary>Diagnostic-only: exposes <see cref="VisLockStateMachine.ThresholdsForTests"/> so a
    /// test can prove <see cref="ApplyPendingSenseLevelRequest"/> actually reached that class' own
    /// copies, not just this decoder's.</summary>
    internal (double Slvl, double Slvl2) VisLockThresholdsForTests => _visLockStateMachine.ThresholdsForTests;

    /// <summary>Test-only observer: (transmission line index, raw-timeline search centre sample) for every
    /// line the SNR hook searches. Step-0 placement probe only; read-only, no decode state.</summary>
    internal Action<int, double>? SyncWindowObservedForTests { get; set; }

    /// <summary>Test-only observer: (transmission line index, raw-timeline sync start the SNR window was
    /// placed at or NaN when the line only acquired, the line's estimate).</summary>
    internal Action<int, double, SyncSnrEstimator.Estimate>? SyncSnrLineObservedForTests { get; set; }

    internal SyncSnrTracker SyncSnrTrackerForTests => _syncSnrTracker;

    // Functional-audit fix (chunk D4 round 1): `EndOfImage` itself has no precondition on decode
    // state (every field it touches is unconditionally reassigned, not conditionally read first),
    // so it's safe to call directly on a freshly-constructed decoder -- this internal wrapper (same
    // "internal for direct testability" convention as Rel/FirstLockedBandpassIndex/etc.) lets a test
    // pin the applyDeadTime:true/false VALUE deterministically, instead of relying on Auto Stop's
    // own statistical trigger (which every existing Auto-Stop test already needs "several
    // consecutive qualifying lines" to reach) -- EndOfImageResetTests/AutoStopTests use this.
    internal void EndOfImageForTests(bool applyDeadTime) => EndOfImage(applyDeadTime);

    internal SyncIntervalTracker PrimarySyncTrackerForTests => _syncBypass1Tracker;
    internal VisLockStateMachine PrimaryVisStateMachineForTests => _visLockStateMachine;

    /// <summary>Test-only: directly invokes <see cref="InitializeAfc"/> (normally only reached via a
    /// real VIS-lock/mode-match during <see cref="PushSamples"/>) so a test can check
    /// <see cref="HasAfcTrackerForTests"/> without needing to drive a full decode. Production code
    /// never calls this.</summary>
    internal void InitializeAfcForTests(SstvModeDefinition mode) => InitializeAfc(mode);

    /// <summary>Test-only visibility into whether AFC is currently active for the mode last passed to
    /// <see cref="InitializeAfc"/> -- production code has no need to read this back.</summary>
    internal bool HasAfcTrackerForTests => _afcTracker is not null;

    /// <summary>Test-only: the offset last pushed to the AFC-retuned resonators, or
    /// <see langword="null"/> when they sit at nominal. Exists so a test can prove
    /// <see cref="ApplyAfcEnabledEdge"/>'s off edge actually ran legacy's <c>InitTone(0)</c>
    /// equivalent, rather than only nulling the tracker.</summary>
    internal double? LastAppliedAfcRetuneHzForTests => _lastAppliedAfcRetuneHz;

    /// <summary>Test-only direct entry point to <see cref="ApplyGatedAfcUpdate"/> -- lets a test drive
    /// a real AFC lock (and its downstream detector retunes) deterministically, without needing a full
    /// encode/decode round trip's own timing to happen to produce one. Mirrors
    /// <see cref="InitializeAfcForTests"/>'s own reasoning. Production code never calls this.</summary>
    internal void ApplyGatedAfcUpdateForTests(double measuredFrequencyHz) => ApplyGatedAfcUpdate(measuredFrequencyHz);

    /// <summary>Test-only visibility into the AFC correction cursor -- functional-audit addition
    /// (D7, round 1). Lets a test confirm AFC correction actually keeps pace with live decode
    /// through to (near) the end of a real image, not just the first few lines -- <see
    /// cref="_afcBoundSample"/> (below) has already shipped one silent total-AFC-disable regression
    /// once (see its own call-site comment), with zero test coverage for either field before now.</summary>
    internal int AfcProcessedUpToForTests => _afcProcessedUpTo;

    /// <summary>Test-only visibility into <see cref="_afcBoundSample"/> -- see
    /// <see cref="AfcProcessedUpToForTests"/>'s own doc comment for why this exists.</summary>
    internal int AfcBoundSampleForTests => _afcBoundSample;

    /// <summary>Test-only visibility into the demod type this instance was actually constructed
    /// with -- production code has no need to read this back. Same reasoning as
    /// <see cref="RestartableSstvDecoder.InnerStationIdDecodeEnabledForTests"/>: exists so a test can
    /// prove a value actually reached the LIVE inner decoder after a
    /// <see cref="RestartableSstvDecoder"/> rebuild, not just that the wrapper still remembers what
    /// it was told.</summary>
    internal DemodType DemodTypeForTests => _demodType;

    /// <summary>Test-only visibility into the CURRENTLY APPLIED PLL tuning -- same reasoning as
    /// <see cref="DemodTypeForTests"/> above, backing <see cref="RestartableSstvDecoder"/>'s own
    /// InnerPllTuningForTests hook (without this, a dropped tuning argument in
    /// <see cref="RestartableSstvDecoder"/>'s own CreateInner would silently revert a user's PLL
    /// tuning to the legacy defaults after a periodic restart, with no test able to catch it).</summary>
    internal (double VcoGain, int LoopOrder, double LoopCutoffHz, int OutputOrder, double OutputCutoffHz) PllTuningForTests =>
        (_pllVcoGain, _pllLoopOrder, _pllLoopCutoffHz, _pllOutputOrder, _pllOutputCutoffHz);

    /// <summary>Test-only visibility into the CURRENTLY APPLIED zero-crossing tuning -- same reasoning
    /// as <see cref="PllTuningForTests"/> immediately above.</summary>
    internal (ZeroCrossingSmoothingMode SmoothingMode, int OutputOrder, double OutputCutoffHz, double SmoothingFrequencyHz) ZeroCrossingTuningForTests =>
        (_zeroCrossingSmoothingMode, _zeroCrossingOutputOrder, _zeroCrossingOutputCutoffHz, _zeroCrossingSmoothingFrequencyHz);

    /// <summary>Test-only visibility into <see cref="_zeroCrossingDemodulator"/>'s OWN currently-
    /// applied smoothing mode (not the decoder-level tracking field above) -- code-review round 1
    /// finding: without reading the live instance itself, a dropped
    /// <c>_zeroCrossingDemodulator.SetTuning</c> call in <see cref="ApplyPendingZeroCrossingTuningRequest"/>
    /// could pass the whole suite undetected, since the tracking fields update independently.</summary>
    internal ZeroCrossingSmoothingMode DemodulatorSmoothingModeForTests => _zeroCrossingDemodulator.SmoothingModeForTests;

    /// <summary>Same reasoning as <see cref="DemodulatorSmoothingModeForTests"/> immediately above, for
    /// <see cref="_afcZeroCrossingCounter"/>'s own live instance.</summary>
    internal ZeroCrossingSmoothingMode AfcCounterSmoothingModeForTests => _afcZeroCrossingCounter.SmoothingModeForTests;

    /// <summary>Test-only visibility into the RX BPF preset this instance was actually constructed
    /// with -- production code has no need to read this back (and <see cref="_searchBandpassFilter"/>
    /// itself can't answer this, since it's null for <see cref="RxBpfPreset.Off"/>). Same reasoning as
    /// <see cref="DemodTypeForTests"/> above.</summary>
    internal RxBpfPreset RxBpfPresetForTests => _rxBpfPreset;

    /// <summary>Test-only visibility into the RX buffer mode this instance was actually constructed
    /// with -- production code has no need to read this back (nothing external needs to know the mode;
    /// <see cref="_rxBufferMode"/> is read internally by TryAutoSync/TryResolveSyncAnchorCorrection).
    /// Same reasoning as <see cref="DemodTypeForTests"/>/<see cref="RxBpfPresetForTests"/> above.</summary>
    internal RxBufferMode RxBufferModeForTests => _rxBufferMode;

    /// <summary>Test-only visibility into <see cref="_zeroCrossingDemodulator"/>'s own running
    /// frequency estimate -- production code has no need to read this back (only
    /// <see cref="DemodulatedFrequencyAt"/>'s consumption of <c>ProcessSample</c>'s return value
    /// matters there). Round-1 D0-audit finding: lets a test prove <c>Clear()</c> was actually called
    /// on this instance at <see cref="EndOfImage"/>/<see cref="InitializeAfc"/>, not just that the
    /// call compiles.</summary>
    internal double ZeroCrossingDemodulatorCurrentFrequencyHzForTests => _zeroCrossingDemodulator.CurrentFrequencyHzForTests;

    /// <summary>Test-only visibility into the AFC-enable flag currently in effect -- the last value
    /// drained by <see cref="ApplyPendingDecoderFlagsRequest"/>, which is the constructed one until
    /// something sets <see cref="AfcEnabled"/>. Production code has no need to read this back. Same reasoning as
    /// <see cref="DemodTypeForTests"/> above: exists so a <see cref="RestartableSstvDecoder"/> test can
    /// prove the value actually reached the LIVE inner decoder after a periodic rebuild, not just that
    /// the wrapper still remembers what it was told (round-8 D2-audit finding: this flag and its four
    /// siblings below had no such accessor despite <see cref="RestartableSstvDecoder"/>'s
    /// <c>CreateInner</c> forwarding all five).</summary>
    internal bool AfcEnabledForTests => _afcEnabled;

    /// <summary>Test-only visibility into the sync-restart-enable flag this instance was actually
    /// constructed with. Same reasoning as <see cref="AfcEnabledForTests"/> above.</summary>
    internal bool SyncRestartEnabledForTests => _syncRestartEnabled;

    /// <summary>Test-only visibility into the Auto Sync-enable flag this instance was actually
    /// constructed with. Same reasoning as <see cref="AfcEnabledForTests"/> above.</summary>
    internal bool AutoSyncEnabledForTests => _autoSyncEnabled;

    /// <summary>Test-only visibility into the Auto Stop-enable flag this instance was actually
    /// constructed with. Same reasoning as <see cref="AfcEnabledForTests"/> above.</summary>
    internal bool AutoStopEnabledForTests => _autoStopEnabled;

    /// <summary>Test-only visibility into the clamped squelch-preset index this instance was actually
    /// constructed with (see <see cref="_senseLevel"/>'s own doc comment for the clamping rule). Same
    /// reasoning as <see cref="AfcEnabledForTests"/> above.</summary>
    internal int SenseLevelForTests => _senseLevel;

    /// <summary>Test-only visibility into the RX buffer subsystem's own staging buffer -- null only
    /// for <see cref="RxBufferMode.Off"/>; non-null for <see cref="RxBufferMode.On"/>
    /// (<see cref="RxLineStagingBuffer"/>, RAM) and, as of this Phase 7 sub-piece,
    /// <see cref="RxBufferMode.Extended"/> too (<c>RxDiskLineStagingBuffer</c>, disk-backed -- see
    /// <see cref="_rxLineStagingBuffer"/>'s own doc comment). Declared type is the interface (an
    /// earlier Phase 7 sub-piece's own extraction) -- every real call site in the shipped Phase 3-6
    /// tests only ever touches <c>Count</c>/<c>LineCount</c> against this property, both interface
    /// members, so this kept compiling unchanged through that extraction.</summary>
    internal IRxLineStagingBuffer? RxLineStagingBufferForTests => _rxLineStagingBuffer;

    /// <summary>Test-only visibility into the output-row cursor (<see cref="_nextLine"/>, in BITMAP
    /// ROWS -- 2x transmission-line count for paired-channel modes, see <see cref="PerformReplay"/>'s
    /// own reconciliation doc comment). RX buffer subsystem Phase 6c: lets a test verify
    /// <see cref="PerformReplay"/>'s own reconciliation independently of trusting its internals.</summary>
    internal int NextLineForTests => _nextLine;

    /// <summary>Test-only visibility into the signed replay origin <see cref="PerformReplay"/> most
    /// recently computed (<see cref="ReplayOriginCalculator.ComputeOrigin"/>'s own return value) -- null
    /// until the first replay pass runs. RX buffer subsystem Phase 6c.</summary>
    internal int? LastReplayOriginForTests => _lastReplayOriginForTests;

    /// <summary>Test-only visibility into <see cref="_replayPassCountForTests"/> -- see that field's
    /// own doc comment for the cumulative-not-per-image convention.</summary>
    internal int ReplayPassCountForTests => _replayPassCountForTests;

    /// <summary>Test-only visibility into <see cref="_rawFloorClampForwardCountForTests"/> -- lets a
    /// test prove PerformReplay's own raw-floor clamp-forward fallback branch (the one that reuses the
    /// retired forward-jump landing formula for the narrow case a backward snap would violate
    /// <see cref="_bufferBase"/>) actually fired, rather than inferring it indirectly.</summary>
    internal int RawFloorClampForwardCountForTests => _rawFloorClampForwardCountForTests;

    /// <summary>Test-only visibility into <see cref="_rxBufferAnchorSample"/> -- RX buffer subsystem
    /// Phase 6c round-4, corrected D3+D8+D9 coupled round 3 (the general "staged + in-flight" count
    /// phrasing this comment previously repeated is exactly what that field's own doc comment now
    /// calls out as the round-1 regression's root cause -- removed here rather than left to
    /// contradict it). Buffered-replay fix (§15 item 2): PerformReplay no longer re-anchors this field
    /// at all (see that field's own doc comment) -- it equals <see cref="ConsumedSamplesForTests"/> only
    /// right at a fresh lock (InitializeSlant), not "immediately after a replay pass" as an earlier
    /// version of this comment claimed.</summary>
    internal int RxBufferAnchorSampleForTests => _rxBufferAnchorSample;

    /// <summary>Test-only visibility into <see cref="_rxBufferBaseTransmissionLine"/>. Buffered-replay
    /// fix (§15 item 2): stays 0 for a whole reception now (the staging buffer is never truncated/
    /// re-based anymore), so it can no longer serve as a "did a replay pass run" oracle the way it used
    /// to -- see <see cref="ReplayPassCountForTests"/> for that.</summary>
    internal int RxBufferBaseTransmissionLineForTests => _rxBufferBaseTransmissionLine;

    /// <summary>Test-only visibility into the Auto-Slant sync-envelope detector -- the one AFC
    /// retunes (ultracode audit finding #1). Null until <see cref="InitializeSlant"/> runs for a
    /// non-AVT mode.</summary>
    internal SyncEnvelopeDetector? SyncEnvelopeDetectorForTests => _syncEnvelopeDetector;

    /// <summary>Test-only visibility into one of the 4 remaining VIS-time tone detectors AFC must NOT
    /// retune (ultracode audit finding #1's scope correction) -- pre-lock-only reads, `readonly` for
    /// the decoder's whole lifetime; retuning them would bias the NEXT image's header search instead
    /// of helping this one. §5.3 moved the other 3 of the original 7 (`_visDataD19Detector`/
    /// `_fskSpaceDetector`/`_visDataD12Detector`) into the retuned group -- see their own accessors
    /// below.</summary>
    internal SyncEnvelopeDetector SyncBypass1200DetectorForTests => _syncBypass1200Detector;

    /// <summary>Test-only visibility into the 4 VIS/sync-tone detectors §5.3 added to AFC's retune
    /// scope, alongside <see cref="SyncEnvelopeDetectorForTests"/> -- see
    /// <see cref="ApplyGatedAfcUpdate"/>'s own comment for why these specific 3 (plus
    /// <see cref="VisLockStateMachine"/>'s own 4, exposed via <c>_visLockStateMachine</c> directly in
    /// tests) are genuinely read post-lock, unlike <see cref="SyncBypass1200DetectorForTests"/>'s
    /// group.</summary>
    internal (double D19, double FskSpace, double D12) NewlyRetunedDetectorFrequenciesForTests =>
        (_visDataD19Detector.AppliedCenterFrequencyHzForTests, _fskSpaceDetector.AppliedCenterFrequencyHzForTests,
         _visDataD12Detector.AppliedCenterFrequencyHzForTests);

    /// <summary>Test-only visibility into <see cref="VisLockStateMachine"/>'s own 4 detector
    /// frequencies -- see <see cref="VisLockStateMachine.DetectorFrequenciesForTests"/>.</summary>
    internal (double D11, double D12, double D13, double D19) VisLockDetectorFrequenciesForTests =>
        _visLockStateMachine.DetectorFrequenciesForTests;

    /// <summary>Test-only visibility into the Auto-Slant tracker itself -- needed to distinguish
    /// manual ReSync's two suppression scopes from the outside: the one-line gate
    /// (<see cref="_suppressNextSlantProcessLine"/>) must leave <see cref="SlantTracker.TotalLinesObservedForTests"/>
    /// completely untouched for that one line, while the whole-image gate
    /// (<see cref="_slantCorrectionsDisabledForRestOfImage"/>) still advances it by exactly one per
    /// line via <see cref="SlantTracker.ProcessLineHistoryOnly"/>. Null until <see cref="InitializeSlant"/>
    /// runs for a non-AVT mode.</summary>
    internal SlantTracker? SlantTrackerForTests => _slantTracker;

    /// <summary>Test-only visibility into the decoder-lifetime <see cref="LevelAgc"/> instance --
    /// lets a test drive <see cref="LevelAgc.Do"/>/<see cref="LevelAgc.Fix"/> directly with exact,
    /// known values, then assert against the real <see cref="SignalPeakLevel"/>/
    /// <see cref="IsLevelOverdriven"/> production properties (not a duplicate of their math written
    /// independently in the test, which a code-level audit found couldn't actually discriminate a
    /// wrong divisor or a <c>&gt;</c>-vs-<c>&gt;=</c> threshold bug).</summary>
    internal LevelAgc LevelAgcForTests => _levelAgc;

    /// <summary>Test-only visibility into the within-line sample accumulator Auto Slant advances --
    /// should always satisfy 0 &lt;= this &lt; <see cref="EffectiveSamplesPerLineForTests"/> even
    /// across a rate-change commit (ultracode audit finding #10).</summary>
    internal double SlantIdealSamplesSoFarInLineForTests => _slantIdealSamplesSoFarInLine;

    /// <summary>Test-only visibility into the current (possibly Auto-Slant-corrected) samples-per-line.</summary>
    internal double EffectiveSamplesPerLineForTests => _effectiveSamplesPerLine;

    /// <summary>Test-only visibility into <see cref="_correctSlantRequested"/> -- lets a test prove
    /// the stale-request clear at a fresh lock (<see cref="InitializeSlant"/>) actually ran, rather
    /// than inferring it indirectly from "no replay happened" (which the entry gate's own
    /// cumulative-line-count check would also produce for an unrelated reason on a fresh lock, making
    /// that inference vacuous -- auditor code-review finding, Phase 8c round 1).</summary>
    internal bool CorrectSlantRequestedForTests => _correctSlantRequested;

    /// <summary>Test-only proof that <see cref="TryCorrectSlant"/>'s entry path invokes the shared
    /// representability/projection guard before starting its search.</summary>
    internal int ManualSlantEntrySafetyCheckCountForTests => _manualSlantEntrySafetyCheckCountForTests;

    /// <summary>Test-only proof that each computed correction candidate passes through the shared
    /// representability/projection guard before it seeds another iteration or commits.</summary>
    internal int ManualSlantCandidateSafetyCheckCountForTests => _manualSlantCandidateSafetyCheckCountForTests;

    /// <summary>Test-only visibility into the current decode read cursor -- manual ReSync
    /// (<see cref="RequestReSync"/>) is the first feature that shifts this outside the normal per-line
    /// loop's own advancement, so tests need to observe it directly.</summary>
    internal int ConsumedSamplesForTests => _consumedSamples;

    /// <summary>Test-only visibility into the captured last-completed-line sync-peak position manual
    /// ReSync reads from -- <see langword="null"/> once a correction has applied (or before any line
    /// has completed since lock). Read this BEFORE the <see cref="PushSamples"/> call that triggers a
    /// pending <see cref="RequestReSync"/> request, not after -- it's nulled as part of applying the
    /// correction.</summary>
    internal double? LastLineSyncPeakPositionForTests => _lastLineSyncPeakPosition;

    /// <summary>Test-only visibility into <see cref="_suppressNextSlantProcessLine"/> -- buffered-replay
    /// fix (§15 item 2): PerformReplay's own tail now sets this alongside
    /// <see cref="LastLineSyncPeakPositionForTests"/> after the redraw walk, excluding the first
    /// post-snap line's own observation from Auto-Sync.</summary>
    internal bool SuppressNextSlantProcessLineForTests => _suppressNextSlantProcessLine;

    /// <summary>Test-only visibility into the same "ofp" (<c>SSTVSET.m_OFP</c>) value
    /// <see cref="PerformReSync"/> computes internally, so tests can hand-derive an expected skip from
    /// <see cref="LastLineSyncPeakPositionForTests"/> without re-deriving that computation a second,
    /// independently-fallible time. <see langword="null"/> before any mode is locked.</summary>
    internal int? SyncPeakOffsetSamplesForTests => _mode is null ? null : ComputeSyncPeakOffsetSamples(_mode);

    /// <summary>Test-only visibility into the in-progress manual-ReSync skip still left to drain --
    /// reaches 0 exactly when <see cref="DrainPendingSkip"/> has fully applied a correction.</summary>
    internal int PendingSkipSamplesForTests => _pendingSkipSamples;

    /// <summary>Un-stub-RX-tab Piece A: test-only visibility into the absolute sample index the
    /// current line's timing is anchored to -- must stay in LOCKSTEP with
    /// <see cref="ConsumedSamplesForTests"/> around a notch toggle's disable-direction cursor
    /// rewind, or the current line's own width would silently stretch/shrink.</summary>
    internal double IdealLineStartSampleForTests => _idealLineStartSample;

    /// <summary>Un-stub-RX-tab Piece A: test-only visibility into the absolute sample index Auto
    /// Slant's own tracking has processed up to -- a notch toggle's disable-direction cursor rewind
    /// must NOT move this (see <c>ApplyNotchDisableShift</c>'s own doc comment for why re-feeding
    /// <see cref="_syncEnvelopeDetector"/>/<see cref="RxLineStagingBuffer"/> over the same span would
    /// be wrong).</summary>
    internal int SlantProcessedUpToForTests => _slantProcessedUpTo;

    /// <summary>Un-stub-RX-tab Piece A: test-only visibility into whether the notch is currently
    /// enabled, without needing a decoded-output side effect to infer it.</summary>
    internal bool NotchEnabledForTests => _notchFilter is not null;

    /// <summary>Un-stub-RX-tab Piece A: test-only visibility into the notch's current center
    /// frequency -- <see langword="null"/> while disabled.</summary>
    internal double? NotchFrequencyForTests => _notchFilter?.Frequency;

    /// <summary>Test-only visibility into the notch's own tap count (0 while disabled) -- lets a test
    /// derive the exact, known skip size (<c>Tap / 2</c>) ApplyPendingNotchRequest's enable direction
    /// feeds into DrainPendingSkip, instead of depending on a data-dependent measurement like
    /// PerformReSync's own sync-offset (buffered-replay fix, §15 item 2, used to precisely construct
    /// the DrainPendingSkip post-snap-window guard test).</summary>
    internal int NotchTapForTests => _notchFilter?.Tap ?? 0;

    /// <summary>Un-stub-RX-tab Piece B: test-only visibility into which VIS/sync-envelope source
    /// channel 0's current (or most recent) capture latched at arm time.</summary>
    internal bool ScopeCaptureChannel0UsesD19ForTests => _scopeCaptureChannel0.UsesD19;

    /// <summary>Test-only: how many times <see cref="TryAutoSync"/> itself has applied a correction
    /// (distinct from a manual <see cref="RequestReSync"/> call, which shares the same underlying
    /// <see cref="ApplySyncCorrection"/> tail but is not counted here) -- the regression check for
    /// round-1 plan-review's own most severe finding (a wrong anchor base causing continuous spurious
    /// auto-resyncs on a good signal) needs to observe THIS specifically staying at 0.</summary>
    internal int AutoSyncTriggerCountForTests => _autoSyncTriggerCountForTests;

    /// <summary>Test-only: the actual <c>(_autoSlantEnabled ? 5 : 2) * _autoSyncBaseMult</c> value
    /// branch 1 last computed and used for its own two threshold comparisons (auditor plan-review
    /// finding, batch 6) -- <see langword="null"/> until branch 1 has evaluated at least once
    /// (requires <c>n &lt; 4</c>, where <c>n</c> is <see cref="CountAutoSyncCluster"/>'s own
    /// "how many of the last 16 readings cluster near the current position" count -- NOT a simple
    /// observation-index warmup counter; a clean, well-synced signal stays clustered (<c>n &gt;= 4</c>)
    /// almost immediately and can revisit <c>n &lt; 4</c> later too, e.g. right after a genuine sync
    /// glitch). Lets a test verify the SELECTED threshold directly, without needing to empirically tune
    /// a real-audio splice scenario that happens to trigger at one threshold value but not the other --
    /// both threshold values move the trigger window in opposite directions simultaneously (a looser
    /// "small step" ceiling but a stricter "large jump" floor, or vice versa), which the existing
    /// splice-based tests in AutoSyncTests.cs were never designed to isolate.</summary>
    internal int? LastBranch1ThresholdForTests => _lastBranch1ThresholdForTests;

    /// <summary>Test-only visibility into Auto Sync's own observation counter (port of
    /// <c>m_AutoStopACnt</c>) -- gates the 8-line warmup and the 14-vs-10 cluster threshold.</summary>
    internal int AutoSyncObservationCountForTests => _autoSyncObservationCount;

    /// <summary>Test-only visibility into Auto Sync's own stable-reference position (port of
    /// <c>m_AutoSyncPos</c>, <see langword="null"/> = legacy's <c>0x7fffffff</c> sentinel).</summary>
    internal int? AutoSyncReferencePositionForTests => _autoSyncReferencePosition;

    /// <summary>Test-only visibility into Auto Sync's own retrigger cooldown (port of
    /// <c>m_AutoSyncDis</c>).</summary>
    internal int AutoSyncCooldownForTests => _autoSyncCooldown;

    /// <summary>Test-only visibility into <see cref="_slantCorrectionsDisabledForRestOfImage"/> (port
    /// of <c>m_AutoSyncCount</c>'s own gate) -- RX buffer subsystem Phase 6d round-2: lets a test
    /// directly confirm a manual ReSync/Auto-Sync trigger actually set this flag, without depending on
    /// indirect symptoms.</summary>
    internal bool SlantCorrectionsDisabledForRestOfImageForTests => _slantCorrectionsDisabledForRestOfImage;

    /// <summary>Test-only: <see cref="ComputeAutoSyncPosition"/>'s own real return value from the last
    /// time <see cref="TryAutoSync"/> ran -- see <see cref="_lastComputedAutoSyncPositionForTests"/>'s
    /// own doc comment for why this is captured internally rather than recomputed from outside.</summary>
    internal int? LastComputedAutoSyncPositionForTests => _lastComputedAutoSyncPositionForTests;

    /// <summary>Test-only visibility into Auto Stop's own counter (port of <c>m_AutoStopCnt</c>).</summary>
    internal int AutoStopCntForTests => _autoStopCnt;

    /// <summary>Test-only: how many times Auto Stop's own trigger has fired.</summary>
    internal int AutoStopTriggerCountForTests => _autoStopTriggerCountForTests;

    /// <summary>Test-only: the envelope-spread value (<c>_slantLineMaxEnvelope - _slantLineMinEnvelope</c>)
    /// last observed by <see cref="TryAutoSync"/>, for pinning the 5000/8192 threshold scale against
    /// this port's own <see cref="SyncEnvelopeDetector"/> output rather than assuming it matches
    /// legacy's raw <c>m_SyncMax - m_SyncMin</c> scale.</summary>
    internal double? LastAutoStopEnvelopeSpreadForTests => _lastAutoStopEnvelopeSpreadForTests;

    /// <summary>Test-only visibility into the currently-locked mode (<see langword="null"/> when
    /// idle/between images) -- <see cref="ForceMode"/> is the first feature whose own test suite
    /// needs to distinguish "idle" from "locked" independently of <see cref="ModeDetected"/> having
    /// fired yet (a non-AVT commit can be locked with a pending, not-yet-announced anchor).</summary>
    internal SstvModeDefinition? ModeForTests => _mode;

    /// <summary>Diagnostic-only: the currently ACTIVE persistent mode lock (already-drained value,
    /// not <see cref="_pendingModeLockRequest"/>) -- see <see cref="_lockedMode"/>'s own doc
    /// comment.</summary>
    internal SstvModeDefinition? LockedModeForTests => _lockedMode;

    /// <summary>Test-only visibility into whether AVT training is currently in flight --
    /// <see cref="ForceMode"/>'s own teardown is the first production code that needs to abort this
    /// from outside <see cref="TryResolveAvtTraining"/>'s own exit points/<see cref="EndOfImage"/>.</summary>
    internal bool AvtTrainingPendingForTests => _avtTrainingPending;

    /// <summary>Test-only visibility into a still-unresolved, not-yet-<see cref="ModeDetected"/>
    /// anchor-correction commit -- see <see cref="PerformForceMode"/>'s own doc comment for why a
    /// stale entry here is the exact hazard its clear step exists to prevent.</summary>
    internal SstvModeDefinition? PendingAnchorCorrectionModeForTests => _pendingAnchorCorrectionMode;

    /// <summary>Test-only entry point for <see cref="TryCorrectSlant"/> -- lets a test drive the
    /// search directly and inspect its result, without depending on the request/drain plumbing (a
    /// later RX buffer subsystem Phase 8 sub-piece). Mirrors the established
    /// <see cref="PerformReplayForTests"/> precedent (a thin pass-through wrapper around an
    /// otherwise-private method).</summary>
    internal bool TryCorrectSlantForTests() => TryCorrectSlant();

    /// <summary>Test-only entry point for <see cref="InitializeSlant"/> -- lets a test reach a fully
    /// initialized slant/staging-buffer state (in particular, a real, non-zero
    /// <see cref="EffectiveSamplesPerLineForTests"/>) for a mode locked via <see cref="ForceMode"/>,
    /// without needing a real audio decode. <see cref="ForceMode"/> alone is NOT sufficient for this:
    /// its own <c>Commit()</c> sets <c>_mode</c> immediately, but defers <see cref="InitializeSlant"/>
    /// itself to <c>FinalizeAnchorAndStartDecoding</c>, which only runs once
    /// <c>TryResolveSyncAnchorCorrection</c> succeeds against real buffered audio (immediately, only
    /// for AVT) -- a real gap an earlier draft of the Phase 8 test plan assumed away and had to be
    /// corrected against actual behavior, not inferred. Mirrors the established
    /// <see cref="PerformReplayForTests"/> precedent.</summary>
    internal void InitializeSlantForTests(SstvModeDefinition mode) => InitializeSlant(mode);

    /// <summary>Test-only entry point for <see cref="PerformReplay"/> -- lets a test drive a single
    /// replay pass directly and deterministically, without depending on the automatic triggers'
    /// (RX buffer subsystem Phase 6d) own timing. Mirrors the established <c>InitializeAfcForTests</c>
    /// precedent (a thin pass-through wrapper around an otherwise-private method). Stays
    /// <see langword="void"/> even though <see cref="PerformReplay"/> itself now returns
    /// <see langword="bool"/> (D0-audit round-10 structural fix) -- <see cref="ExecuteWithDeferredSubscriberFailures"/>
    /// only has an <see cref="Action"/> overload, and no existing test reads a return value here
    /// (<c>ReplayEngineTests.cs</c>'s own <c>Assert.Throws&lt;InvalidOperationException&gt;(decoder.PerformReplayForTests)</c>
    /// depends on this staying a plain method group).</summary>
    internal void PerformReplayForTests() => ExecuteWithDeferredSubscriberFailures(() => PerformReplay());

    /// <summary>Test-only: when <see langword="true"/>, <see cref="TryProcessBuffer"/>'s own automatic
    /// replay drain (RX buffer subsystem Phase 6d) becomes a no-op (the pending flag is still cleared,
    /// so it never accumulates across pushes -- only the <see cref="PerformReplay"/> call itself is
    /// skipped). For a test that wants to drive replay ONLY via <see cref="PerformReplayForTests"/>, at
    /// controlled moments, without an automatic trigger firing unpredictably during the same
    /// <see cref="PushSamples"/> calls and corrupting the test's own row-count/pixel-content
    /// bookkeeping.</summary>
    internal bool SuppressAutomaticReplayForTests { get; set; }

    /// <summary>Test-only: when set, PerformReplay's own raw-floor clamp-forward check
    /// (`backwardTargetConsumedSamples &lt; _bufferBase`) compares against this value INSTEAD of the
    /// real <see cref="_bufferBase"/> -- the only way to deterministically force that branch without
    /// fighting a manual Correct Slant search's own real convergence dynamics (empirically hard to land
    /// precisely; see <c>PerformReplay_RawFloorClampForward_TouchesOnlyTheLandingPoint</c>'s own doc
    /// comment). Read exactly once per PerformReplay call, at the one comparison site -- never written
    /// by production code, and does not affect any other <see cref="_bufferBase"/> read site (`Rel()`
    /// etc.) in this class, so it cannot corrupt real buffer-trim bookkeeping.</summary>
    internal int? BufferBaseOverrideForTests { get; set; }
}
