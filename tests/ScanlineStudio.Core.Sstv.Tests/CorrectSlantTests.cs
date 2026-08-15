using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 8, sub-piece (b) -- isolated tests for
/// <see cref="AnalogFmSstvDecoder.TryCorrectSlantForTests"/> against hand-crafted staging-buffer
/// fixtures, before any request-method/drain-point wiring exists (a later sub-piece). Every fixture
/// here locks a real mode via <see cref="ISstvDecoder.ForceMode"/> (draining it with one
/// <c>PushSamples</c> call, matching <c>RequestReSync</c>'s own established deferred-request test
/// pattern) and then writes DIRECTLY into the exposed staging buffer via
/// <see cref="IRxLineStagingBuffer.TryAppendLine"/> -- no real audio decode involved, full control
/// over the sync-envelope data the search actually reads.
///
/// Uses <see cref="SstvModeRegistry.Robot36"/> at <c>sampleRate: 1000</c> throughout:
/// <see cref="SstvModeDefinition.LineDurationMs"/> for Robot36 is exactly 150.0 (9+3+88+4.5+1.5+44),
/// so at this rate <c>_effectiveSamplesPerLine</c> (this port's own <c>m_TW</c>-equivalent) starts at
/// exactly 150.0 samples -- a clean, hand-computable width for every fixture below.
///
/// Sub-piece (d) adds ONE exception to the "no real audio" rule above:
/// <see cref="TryCorrectSlant_AgainstRealDecodedAudio_FindsACorrectionCloseToTheTrueMismatch"/> runs
/// the algorithm against a genuinely demodulated staging buffer, at this project's own established
/// 44100Hz/500ppm scenario -- complementing, not replacing, the synthetic fixtures above (those prove
/// exact control-flow correctness against hand-traced inputs; this proves the algorithm still finds a
/// sane, correctly-directioned correction once real demodulation noise/jitter is in the sync-envelope
/// data, which no synthetic fixture can stand in for).
///
/// Sub-piece (d) also adds a genuinely exact analytical fixture,
/// <see cref="TryCorrectSlant_AnalyticalFixture_ConvergesExactlyToTheHandComputedRate"/>, run at
/// <c>sampleRate: 44100</c> (not this class's own default 1000 -- see that test's own doc comment for
/// why the sample rate itself is the lever that makes single-iteration convergence achievable with
/// integer-only peak positions). An earlier version of this file's own doc comment claimed the
/// synthetic fixtures above "already prove analytically" exact convergence -- auditor code-review
/// (Phase 8d round 1) correctly flagged that as false at the time (every fixture above asserts
/// DIRECTION only); it's accurate now that this one exists.
/// </summary>
public class CorrectSlantTests
{
    private const int SampleRate = 1000;
    private const int NominalLineWidth = 150; // Robot36.LineDurationMs (150.0) / 1000 * SampleRate (1000)

    private static AnalogFmSstvDecoder LockedDecoder(RxBufferMode rxBufferMode = RxBufferMode.On, int sampleRate = SampleRate)
    {
        var decoder = new AnalogFmSstvDecoder(sampleRate, rxBufferMode: rxBufferMode);
        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[1]); // drains the deferred ForceMode request -- sets _mode, but NOT _effectiveSamplesPerLine (see InitializeSlantForTests's own doc comment: that's deferred to a real sync-anchor correction for any non-AVT mode)
        decoder.InitializeSlantForTests(SstvModeRegistry.Robot36); // sets _effectiveSamplesPerLine = LineDurationMs/1000*sampleRate and clears the (already-empty) staging buffer, matching what a real lock would eventually do
        return decoder;
    }

    /// <summary>One line's worth of sync-envelope data: a single sharp peak (well above the
    /// `max-min >= 4800` amplitude-contrast gate) at <paramref name="peakIndex"/> (wrapped into
    /// <c>[0, width)</c>), zero elsewhere. The demodulated stream is never read by this algorithm
    /// (confirmed directly against both legacy loop bodies during planning) -- passed as all zeros.
    /// Asserts the append actually succeeded -- code-review finding: a silently rejected line here
    /// (e.g. a capacity miscalculation in a test's own setup) would otherwise leave a fixture that
    /// looks staged but isn't, making downstream assertions pass vacuously instead of for the reason
    /// each test claims.</summary>
    private static void AppendLine(IRxLineStagingBuffer buffer, int width, int peakIndex, double peakValue = 20000.0)
    {
        var demod = new double[width];
        var sync = new double[width];
        sync[((peakIndex % width) + width) % width] = peakValue;
        Assert.True(buffer.TryAppendLine(demod, sync));
    }

    [Fact]
    public void TryCorrectSlant_NoStagingBuffer_ReturnsFalse()
    {
        using var decoder = LockedDecoder(RxBufferMode.Off);

        Assert.False(decoder.TryCorrectSlantForTests());
    }

    [Fact]
    public void TryCorrectSlant_FewerThan16CumulativeLines_ReturnsFalse()
    {
        using var decoder = LockedDecoder();
        var buffer = decoder.RxLineStagingBufferForTests!;
        for (var i = 0; i < 15; i++)
        {
            AppendLine(buffer, NominalLineWidth, 2);
        }

        Assert.Equal(15, buffer.LineCount);
        Assert.False(decoder.TryCorrectSlantForTests());
        Assert.Equal(NominalLineWidth, decoder.EffectiveSamplesPerLineForTests); // untouched -- entry gate rejected before any search state changed
    }

    [Fact]
    public void TryCorrectSlant_RamModeWithoutHeadroomForOneMoreLine_ReturnsFalse()
    {
        // Round-1/round-2 plan-review's own "entry gate +1 line" check (Main.cpp:5268-5270), now
        // exercised end to end through the real decoder, not just HasHeadroomForSamples in isolation
        // (already covered by its own dedicated tests, sub-piece 8a). 16 tiny lines to clear the
        // LineCount>=16 gate cheaply, then one big final line sized to leave less than one more
        // NominalLineWidth of headroom.
        using var decoder = LockedDecoder(RxBufferMode.On);
        var buffer = decoder.RxLineStagingBufferForTests!;
        for (var i = 0; i < 16; i++)
        {
            buffer.TryAppendLine([0.0], [0.0]);
        }

        Assert.Equal(16, buffer.LineCount);
        Assert.True(buffer.HasHeadroomForSamples(NominalLineWidth), "Test setup problem: still expected headroom before the big filler line.");

        const int capacitySamples = 257 * 1100 * SampleRate / 1000; // RxLineStagingBuffer's own formula, recomputed independently
        var fillerLength = capacitySamples - buffer.Count - 1; // leaves Count at capacitySamples-1 after this append
        var filler = new double[fillerLength];
        Assert.True(buffer.TryAppendLine(filler, filler));
        Assert.False(buffer.HasHeadroomForSamples(NominalLineWidth), "Test setup problem: expected no headroom for one more line after the filler.");

        Assert.False(decoder.TryCorrectSlantForTests());
        Assert.Equal(NominalLineWidth, decoder.EffectiveSamplesPerLineForTests);
    }

    [Fact]
    public void TryCorrectSlant_ExtendedModeAlwaysHasHeadroom_CommitsARealCorrection()
    {
        // Disk-backed capture has no real capacity notion (Phase 7's own design) -- confirms the
        // entry gate's own headroom check doesn't accidentally block Extended mode the way it
        // deliberately blocks RAM mode above. Code-review finding on an earlier version of this test:
        // asserting only "doesn't throw" cannot fail for the reason the test exists (a disk-buffer
        // LineCount/gate regression would pass just as silently) -- reusing the SAME known-positive-
        // drift fixture as TryCorrectSlant_KnownPositiveDrift_CommitsARateHigherThanNominal below and
        // asserting a real commit actually PROVES the gate was passed, not just that nothing crashed
        // (this port's On/Extended search math is identical, only the storage backend differs).
        using var decoder = LockedDecoder(RxBufferMode.Extended);
        var buffer = decoder.RxLineStagingBufferForTests!;
        const int rows = 25;
        for (var row = 0; row < rows; row++)
        {
            var truePosition = 2.0 + 0.2 * row;
            AppendLine(buffer, NominalLineWidth, (int)Math.Round(truePosition));
        }

        var startRate = decoder.EffectiveSamplesPerLineForTests / (SstvModeRegistry.Robot36.LineDurationMs / 1000.0);

        var result = decoder.TryCorrectSlantForTests();

        Assert.True(result, "A genuine, sustained positive drift should converge to a real, committed correction in Extended mode too.");
        var committedRate = decoder.EffectiveSamplesPerLineForTests / (SstvModeRegistry.Robot36.LineDurationMs / 1000.0);
        Assert.True(committedRate > startRate, $"Expected a corrected rate above the nominal {startRate}Hz, got {committedRate}Hz.");
    }

    [Fact]
    public void TryCorrectSlant_HasWriteFailed_ReturnsFalseWithoutSearching()
    {
        // spec/18-path-to-1.0.md High item 6. Defense-in-depth, not a bug-reproduction test:
        // TryCorrectSlant's own existing HasHeadroomForSamples checks (entry and pre-commit)
        // already fully protect correctness on a failed buffer -- RxDiskLineStagingBuffer's own
        // HasHeadroomForSamples is `=> !_hasWriteFailed`, so this test's own assertions would pass
        // identically with the new explicit HasWriteFailed guard removed. It documents the explicit
        // early-exit's own behavior (skip the search entirely once already known-failed) -- see
        // TryCorrectSlant's own doc comment on that guard, and
        // /home/artien/.claude/plans/rx-buffer-write-failure-guard.md's Fix section, for the full
        // reasoning on why it's kept anyway.
        using var decoder = LockedDecoder(RxBufferMode.Extended);
        var buffer = decoder.RxLineStagingBufferForTests!;
        const int rows = 25;
        for (var row = 0; row < rows; row++)
        {
            var truePosition = 2.0 + 0.2 * row;
            AppendLine(buffer, NominalLineWidth, (int)Math.Round(truePosition));
        }

        var staging = (RxDiskLineStagingBuffer)buffer;
        staging.CorruptWriteStreamForTests();

        // Whether THIS specific append is admitted before the consumer observes the corruption is
        // itself a race (RxDiskLineStagingBufferTests.cs's own established precedent) -- deliberately
        // not asserted either way, only staged so SOMETHING is guaranteed to hit the corrupted
        // stream once the background consumer catches up.
        _ = buffer.TryAppendLine(new double[NominalLineWidth], new double[NominalLineWidth]);

        _ = buffer.DemodulatedAt(0); // forces a drain -- guarantees HasWriteFailed is observed from here on
        Assert.True(buffer.HasWriteFailed, "Test setup problem: write failure never latched.");

        var startWidth = decoder.EffectiveSamplesPerLineForTests;
        var result = decoder.TryCorrectSlantForTests();

        Assert.False(result);
        Assert.Equal(startWidth, decoder.EffectiveSamplesPerLineForTests);
    }

    [Fact]
    public void TryCorrectSlant_NoRealDrift_ConvergesWithoutARealCommit()
    {
        // Baseline: every line's sync peak sits at the SAME position -- the true signal already
        // matches the assumed line width exactly, so the regression should find (near) zero slope
        // and the search should converge immediately without a real rate change.
        using var decoder = LockedDecoder();
        var buffer = decoder.RxLineStagingBufferForTests!;
        for (var row = 0; row < 20; row++)
        {
            AppendLine(buffer, NominalLineWidth, 2);
        }

        var result = decoder.TryCorrectSlantForTests();

        Assert.False(result, "A perfectly steady signal (zero true drift) should never produce a real commit.");
        Assert.Equal(NominalLineWidth, decoder.EffectiveSamplesPerLineForTests);
    }

    [Fact]
    public void TryCorrectSlant_KnownPositiveDrift_CommitsARateHigherThanNominal()
    {
        // A small, deliberately gentle synthetic drift (0.2 samples/row, well inside a single line's
        // bounds for the row count used here -- no wraparound branches triggered by this fixture,
        // see the two dedicated wraparound tests below for those) -- the sync peak's TRUE position is
        // modeled as a function of ABSOLUTE row index (matching how a real clock mismatch actually
        // behaves: a fixed true pulse period, independent of whatever line width the search currently
        // assumes), not as an offset re-derived per search iteration -- this is what makes the
        // fixture self-consistent across the algorithm's own iterative re-partitioning.
        //
        // Exact hand-computed convergence isn't asserted here. Sub-piece 8d's own investigation into
        // a single-iteration-convergence analytical fixture (deferred here from sub-piece 8b) found it
        // genuinely infeasible, not merely deferred further: convergence requires |k0/width| <
        // 0.1/11025 (~0.00136 samples/row at this fixture's width=150), but every peak position this
        // algorithm can ever report is a plain integer array index (`ps = xx`, an exact within-row
        // sample position -- there is no sub-sample centroid/interpolation anywhere in the search,
        // confirmed by reading every `ps` assignment), so the smallest representable nonzero slope
        // over any practical row count is at least an order of magnitude too coarse to converge on
        // iteration 0. A slope large enough to be integer-representable (e.g. this fixture's own 0.2
        // samples/row, or the exactly-derivable k0=1 case worked out by hand while investigating this)
        // therefore ALWAYS needs multiple iterations, and each non-converged iteration re-partitions
        // the SAME absolute sync-pulse data against a NEW candidate line width -- making a fully
        // closed-form final answer require hand-simulating that whole re-partitioning process, which
        // was judged too easy to get subtly wrong (this exact session already caught 3-4 real
        // hand-derivation errors on far simpler traces) for the verification cost to be worth it,
        // for both author and reviewer. What IS hand-verifiable and asserted here: since every
        // accepted row's peak position increases monotonically with row index by construction, the
        // regression's own slope must be positive, and the corrected rate must therefore come out
        // HIGHER than the nominal starting
        // rate -- a real, directionally-correct commit, not just "some difference or other."
        using var decoder = LockedDecoder();
        var buffer = decoder.RxLineStagingBufferForTests!;
        const int rows = 25;
        for (var row = 0; row < rows; row++)
        {
            var truePosition = 2.0 + 0.2 * row;
            AppendLine(buffer, NominalLineWidth, (int)Math.Round(truePosition));
        }

        var startRate = decoder.EffectiveSamplesPerLineForTests / (SstvModeRegistry.Robot36.LineDurationMs / 1000.0);

        var result = decoder.TryCorrectSlantForTests();

        Assert.True(result, "A genuine, sustained positive drift should converge to a real, committed correction.");
        var committedRate = decoder.EffectiveSamplesPerLineForTests / (SstvModeRegistry.Robot36.LineDurationMs / 1000.0);
        Assert.True(committedRate > startRate, $"Expected a corrected rate above the nominal {startRate}Hz for a positive-slope fixture, got {committedRate}Hz.");
    }

    [Fact]
    public void TryCorrectSlant_WraparoundAbort_BposDriftsNegative_CompletesSafelyWithoutARealCommit()
    {
        // Engineers the EXACT sequence that reaches Main.cpp:5341-5342's `goto _nx` abort (the
        // `bpos < 0` branch's own `ps >= width/8 && ps < width/4` case), hand-traced against
        // NominalLineWidth=150 (width/8=18.75, width/4=37.5, width*3/4=112.5):
        //
        // Rows 0-2 (3 rows): peak at position 2 -- seeds the histogram's own bpos estimate near 2,
        // and gives the regression pass its mandatory 2-row settling margin (the `n >= 2` gate)
        // before real accumulation can start -- row 2's own boundary is the FIRST one that
        // accumulates (y=2, ps=2, m=1).
        // Row 3: peak at RAW position 145. At this row boundary bpos~=2 (<= width/4=37.5), and
        // ps=145 >= width*3/4=112.5, so `ps -= width` -> ps=-5. Acceptance check: ABS(-5-2)=7 <=
        // searchWindow(15 on iteration 0) -- accepted, bpos becomes -5 (now genuinely negative);
        // accumulates a SECOND point (y=3, ps=-5, m=2).
        // Row 4: peak at RAW position 25. At this row boundary bpos=-5 (< 0), and ps=25: NOT
        // >= width/4=37.5, but IS >= width/8=18.75 -- the abort condition. The whole fit pass ends
        // here via `break`, exactly like legacy's own `goto _nx`, with only m=2 accumulated so far
        // (a DELIBERATELY minimal warm-up, not 14 rows -- an earlier version of this fixture used
        // 14 warm-up rows, which by the time the abort fired had already accumulated m=14 (>=6) real
        // regression points and produced a genuine, CORRECT commit off that data -- not a bug, just
        // the wrong fixture for what this test means to isolate: the abort path itself, with too
        // little data around it to mean anything, per the `m<6` behavior confirmed below).
        //
        // A row's own accumulated ps/max/min is only RESOLVED at the START of the NEXT row (the
        // `yy != y` check) -- so row 4's own abort-triggering boundary needs a trailing line appended
        // after it, or the loop ends exactly at row 4's own data without ever resolving it. Rows 5-15
        // (11 more, peak at position 2, matching the histogram's own dominant bin so it doesn't shift
        // bpos's own seed) exist ONLY to satisfy the entry gate's cumulative-line-count >= 16 minimum
        // and to give row 4 its resolving trailing line -- the pass breaks at row 4's own boundary
        // (roughly sample 750 of 2400+ staged), well before ever reaching their actual data.
        //
        // With only m=2 (< 6) accumulated at the abort, the regression solve's own `fq` default (the
        // unchanged current candidate) triggers a trivial convergence and NO real commit -- see
        // TryCorrectSlant's own doc comment on why `m<6` does NOT always mean "no commit" in general
        // (a later, non-zero iteration would commit its OWN previous candidate) -- but this is
        // iteration 0, so the "previous candidate" is just the unchanged starting rate.
        using var decoder = LockedDecoder();
        var buffer = decoder.RxLineStagingBufferForTests!;
        for (var row = 0; row < 3; row++)
        {
            AppendLine(buffer, NominalLineWidth, 2);
        }

        AppendLine(buffer, NominalLineWidth, 145); // row 3 -- triggers the ps -= width wrap, bpos becomes negative
        AppendLine(buffer, NominalLineWidth, 25); // row 4 -- lands in the [width/8, width/4) abort zone, m=2 accumulated so far
        for (var row = 0; row < 11; row++)
        {
            AppendLine(buffer, NominalLineWidth, 2); // rows 5-15 -- resolves row 4's boundary (triggering the abort) and pads to the entry gate's 16-line minimum; never actually scanned past the abort
        }

        Assert.Equal(16, buffer.LineCount); // code-review finding: without this, an under-staged fixture would pass the entry gate's rejection vacuously instead of actually exercising the abort path below

        var exception = Record.Exception(() => decoder.TryCorrectSlantForTests());

        Assert.Null(exception);
        Assert.Equal(NominalLineWidth, decoder.EffectiveSamplesPerLineForTests); // too little accumulated data (m=2 < 6) for a real commit
    }

    [Fact]
    public void TryCorrectSlant_WraparoundAbort_BposDriftsAboveWidth_CompletesSafelyWithoutARealCommit()
    {
        // Mirrors the negative-drift test above, engineering Main.cpp:5349-5350's own `goto _nx`
        // abort (the `bpos >= width` branch's own `ps >= width*3/4 && ps < width*7/8` case), hand-
        // traced against NominalLineWidth=150 (width/4=37.5, width*3/4=112.5, width*7/8=131.25):
        //
        // Rows 0-2 (3 rows): peak at position 148 -- seeds bpos near 148 (the "bpos >= width*3/4"
        // branch's own territory, since 148 is also < width=150 so the DIFFERENT ">= width" branch
        // doesn't apply yet), and gives the regression pass its 2-row settling margin -- row 2's own
        // boundary is the first to accumulate (y=2, ps=148, m=1).
        // Row 3: peak at RAW position 5. At this row boundary bpos~=148 (>= width*3/4=112.5, and
        // < width=150), and ps=5 < width/4=37.5, so `ps += width` -> ps=155. Acceptance check:
        // ABS(155-148)=7 <= 15 -- accepted, bpos becomes 155 (now genuinely >= width); accumulates a
        // second point (y=3, ps=155, m=2).
        // Row 4: peak at RAW position 120. At this row boundary bpos=155 (>= width=150), and
        // ps=120: NOT < width*3/4=112.5, but IS < width*7/8=131.25 -- the abort condition, with only
        // m=2 accumulated (same deliberately-minimal warm-up reasoning as the negative-drift test
        // above -- a 14-row warm-up here produced a genuine, correct m>=6 commit instead of
        // isolating the abort path itself).
        //
        // Rows 5-15 (11 more, peak at position 148, matching the histogram's own dominant bin) exist
        // only to resolve row 4's own boundary (triggering the abort) and satisfy the entry gate's
        // 16-line minimum -- never actually scanned past the abort.
        using var decoder = LockedDecoder();
        var buffer = decoder.RxLineStagingBufferForTests!;
        for (var row = 0; row < 3; row++)
        {
            AppendLine(buffer, NominalLineWidth, 148);
        }

        AppendLine(buffer, NominalLineWidth, 5); // row 3 -- triggers the ps += width wrap, bpos becomes >= width
        AppendLine(buffer, NominalLineWidth, 120); // row 4 -- lands in the [width*3/4, width*7/8) abort zone, m=2 accumulated so far
        for (var row = 0; row < 11; row++)
        {
            AppendLine(buffer, NominalLineWidth, 148); // rows 5-15 -- resolves row 4's boundary and pads to the entry gate's 16-line minimum
        }

        Assert.Equal(16, buffer.LineCount); // code-review finding: same rationale as the negative-drift test above

        var exception = Record.Exception(() => decoder.TryCorrectSlantForTests());

        Assert.Null(exception);
        Assert.Equal(NominalLineWidth, decoder.EffectiveSamplesPerLineForTests); // too little accumulated data (m=2 < 6) for a real commit
    }

    [Fact]
    public void TryCorrectSlant_RamModeFindsARealCorrectionButLacksTailHeadroom_Reverts()
    {
        // RX buffer subsystem Phase 8, sub-piece (d): distinct from
        // TryCorrectSlant_RamModeWithoutHeadroomForOneMoreLine_ReturnsFalse above, which exercises the
        // ENTRY gate's own `HasHeadroomForSamples(startLineWidthSamples)` check (search never even
        // starts). This exercises the TAIL's own separate check,
        // `HasHeadroomForSamples(32 * startLineWidthSamples)` -- the search runs (all 5 iterations;
        // auditor code-review, Phase 8d round 1: this fixture's own residual never drops under the
        // convergence bound, so it EXHAUSTS every iteration and commits the last candidate rather than
        // converging early -- "runs to completion" is the accurate framing, not "converges"), the last
        // candidate genuinely differs from the starting rate, and it STILL reverts (returns false,
        // _effectiveSamplesPerLine stays at nominal) purely because there isn't enough headroom for 32
        // more lines. Needs a buffer with headroom for +1 line (so the entry gate passes) but NOT +32
        // lines (so only the tail rejects) -- CapacitySamples at this fixture's SampleRate(1000) is
        // 282700 (257*1100*1000/1000), so Count must land in
        // [282700-32*150, 282700-150) = [277900, 282550). 280000 sits comfortably inside that window
        // with margin on both sides.
        //
        // Auditor code-review finding, Phase 8d round 1: an earlier version of this test asserted only
        // the reverted (false) case -- both of TryCorrectSlant's own `return false` sites (the "no real
        // rate change found" tail check and THIS tail-headroom check) are externally indistinguishable
        // from outside the method, so that alone couldn't prove the SPECIFIC reason for the revert (a
        // regression that made the search find nothing at all would leave that version green too). The
        // paired control below reuses the IDENTICAL drift-row setup with a SMALLER filler that leaves
        // real tail headroom, asserting a real COMMIT -- pinning the tail headroom check as the only
        // difference between the two outcomes.
        using var reverted = BuildDriftRowsWithFiller(targetCountBeforeSearch: 280000); // inside [277900, 282550) -- tail headroom fails
        var revertedBuffer = reverted.RxLineStagingBufferForTests!;
        Assert.False(revertedBuffer.HasHeadroomForSamples(32 * NominalLineWidth), "Test setup problem: tail's own +32-line headroom check must fail, or this fixture doesn't exercise the tail revert path at all.");

        var revertedResult = reverted.TryCorrectSlantForTests();

        Assert.False(revertedResult, "The search should have found a real correction from the drift rows, but reverted at the tail due to insufficient +32-line headroom.");
        Assert.Equal(NominalLineWidth, reverted.EffectiveSamplesPerLineForTests); // reverted -- never committed, despite real drift data being present

        using var committed = BuildDriftRowsWithFiller(targetCountBeforeSearch: 277000); // below 277900 -- tail headroom holds (277000 + 32*150 = 281800 < 282700)
        var committedBuffer = committed.RxLineStagingBufferForTests!;
        Assert.True(committedBuffer.HasHeadroomForSamples(32 * NominalLineWidth), "Test setup problem: this paired control needs tail headroom to hold, or it isn't a real control.");

        var committedResult = committed.TryCorrectSlantForTests();

        Assert.True(committedResult, "Paired control: the IDENTICAL drift data, with tail headroom restored, must commit -- proving the reverted case above was rejected BY THE TAIL CHECK specifically, not because the search found nothing.");
        Assert.NotEqual(NominalLineWidth, committed.EffectiveSamplesPerLineForTests);
    }

    /// <summary>Shared setup for the tail-headroom revert test and its paired control above: the same
    /// known-positive-drift technique as <see cref="TryCorrectSlant_KnownPositiveDrift_CommitsARateHigherThanNominal"/>
    /// (a real, sustained, hand-verified-directionally-correct drift the search will always want to
    /// commit), padded with an all-zero filler line (never passes the `max-min >= 4800` amplitude
    /// gate, so it's scanned but never accepted into the regression -- only pads <c>Count</c> toward
    /// capacity) to land at exactly <paramref name="targetCountBeforeSearch"/> before the search runs.</summary>
    private static AnalogFmSstvDecoder BuildDriftRowsWithFiller(int targetCountBeforeSearch)
    {
        var decoder = LockedDecoder();
        var buffer = decoder.RxLineStagingBufferForTests!;

        const int rows = 25;
        for (var row = 0; row < rows; row++)
        {
            var truePosition = 2.0 + 0.2 * row;
            AppendLine(buffer, NominalLineWidth, (int)Math.Round(truePosition));
        }

        Assert.True(buffer.HasHeadroomForSamples(NominalLineWidth), "Test setup problem: expected headroom for one more line before the filler (the entry gate must pass).");

        var fillerLength = targetCountBeforeSearch - buffer.Count;
        var filler = new double[fillerLength];
        Assert.True(buffer.TryAppendLine(filler, filler));

        Assert.Equal(targetCountBeforeSearch, buffer.Count);
        Assert.True(buffer.HasHeadroomForSamples(NominalLineWidth), "Test setup problem: entry gate's own +1-line headroom check must still pass.");

        return decoder;
    }

    [Fact]
    public void TryCorrectSlant_AnalyticalFixture_ConvergesExactlyToTheHandComputedRate()
    {
        // RX buffer subsystem Phase 8, sub-piece (d): a genuinely exact single-iteration-convergence
        // analytical fixture -- corrects an earlier, WRONG conclusion in this file's own history that
        // this was infeasible with integer-only peak positions (auditor code-review, Phase 8d round 1,
        // caught it; independently re-verified every number below by hand before landing this).
        //
        // The earlier "infeasible" reasoning conflated two different things: "every peak position is
        // an integer array index" (true -- `ps` is only ever assigned from `xx`, itself always an
        // exact within-row sample index) with "the smallest achievable slope is 1 sample/row" (false).
        // With integer `y` (row index) and integer `ps` (peak position), the regression's own
        // `k0 = (m*sumYL - sumL*sumY) / (m*sumYY - sumY*sumY)` is a RATIO of two integers -- its
        // smallest achievable nonzero magnitude is `1/D` for the denominator `D` a specific fixture
        // produces, not `1`. `D` grows roughly with `m^3`, so a modest row count already gives a slope
        // far finer than "one whole sample per row." The other missing piece: convergence's own bound
        // (`0.1/11025*sampleRate`) scales with SAMPLE RATE, while a fixture's achievable `k0` doesn't
        // depend on it at all -- so raising the sample rate (not the row count) is what actually buys
        // headroom. This fixture runs at 44100Hz (not this file's own default 1000Hz), where the
        // convergence band is ~44x wider than at 1000Hz -- the real reason the earlier investigation,
        // conducted only at 1000Hz, concluded (wrongly) that no construction could work.
        //
        // Construction: 25 lines, Robot36 (LineDurationMs=150.0ms exactly -> width=6615.0 samples
        // exactly at 44100Hz), sync peak at LOCAL position 2 in every row EXCEPT row 23, which gets
        // position 3 -- a single one-sample displacement on the LAST accepted row, nothing else drifts.
        //
        // Hand-traced (independently re-derived, not copied from the investigation that found this):
        // - Histogram: every row's peak folds to bin 2 (24 hits) except row 23's, which folds to bin 3
        //   (1 hit) -- bin 2 wins outright, bpos=2, deterministic, no tie-breaking ambiguity.
        // - Regression: scannedSampleCount=25*6615=165375, so row 24's own boundary is never reached
        //   (needs a 26th row to resolve, deliberately NOT added -- see below for why). Boundaries for
        //   rows 0..23 resolve; the `n>=2` settling margin means accumulation starts at row 2's own
        //   boundary. Accepted points: y=2..23 (m=22), ps=2 for every one of them EXCEPT y=23 (ps=3).
        // - Sums: sumY=275 (2+3+...+23), sumYY=4323 (sum of squares 2^2..23^2), sumL=45 (21 twos plus
        //   one three, i.e. 2*22 + 1), sumYL=573 (sumY*2 + 23's own extra +1). D = m*sumYY - sumY^2 =
        //   22*4323 - 275^2 = 95106 - 75625 = 19481.
        // - k0 = (22*573 - 45*275)/19481 = (12606-12375)/19481 = 231/19481 ~= 0.01185771...
        // - fq's own delta = k0*sampleRate/width = k0*44100/6615 = k0*(20/3) = 4620/58443
        //   ~= 0.07905139... Hz.
        // - Convergence bound: 0.1/11025*44100 = 0.4 (44100/11025=4 exactly). Compared AFTER rounding
        //   (the code reads `fq` post-NormalSampFreq, not the raw pre-rounding delta): 0.08 < 0.4 --
        //   WELL inside the bound (5x margin, not knife-edge), so this converges on ITERATION 0, no
        //   multi-iteration re-partitioning to simulate.
        // - fq = 44100 + 0.0790514 = 44100.0790514. NormalSampFreq rounds to the nearest 0.01:
        //   floor(4410007.90514+0.5)/100 = floor(4410008.40514)/100 = 44100.08.
        // - Committed line width = 150.0/1000*44100.08 = 6615.012 exactly.
        //
        // No wraparound branch fires anywhere in this trace (bpos=2 stays inside [0, width), well away
        // from any of the four cascade's own boundary zones), and the tail's own +32-line headroom
        // check passes trivially (this fixture's total staged Count, 165375, is a tiny fraction of
        // CapacitySamples at 44100Hz, 257*1100*44100/1000=12467070) -- so nothing besides the
        // regression math itself determines the outcome, making this an exact, not approximate, check.
        const int sampleRate = 44100;
        const double lineWidth = 6615.0; // Robot36.LineDurationMs (150.0) / 1000 * sampleRate (44100) -- exact
        using var decoder = LockedDecoder(sampleRate: sampleRate);
        var buffer = decoder.RxLineStagingBufferForTests!;

        const int rows = 25;
        for (var row = 0; row < rows; row++)
        {
            var peakIndex = row == 23 ? 3 : 2;
            AppendLine(buffer, (int)lineWidth, peakIndex);
        }

        Assert.Equal(25, buffer.LineCount);

        var result = decoder.TryCorrectSlantForTests();

        Assert.True(result, "Test setup problem: the hand-traced fixture should produce a real, committed correction.");
        Assert.Equal(6615.012, decoder.EffectiveSamplesPerLineForTests, tolerance: 1e-9);
    }

    [Fact]
    public void TryCorrectSlant_AgainstRealDecodedAudio_FindsACorrectionCloseToTheTrueMismatch()
    {
        // RX buffer subsystem Phase 8, sub-piece (d): the algorithm run against a REAL decoded
        // staging buffer -- this project's own established 44100Hz/500ppm mismatch scenario (the same
        // one ReplayEngineTests.cs/CorrectSlantRequestTests.cs already rely on as "reliably commits a
        // correction during decode"), rather than the synthetic hand-crafted sync-envelope arrays
        // every other fixture in this file uses. autoSlantEnabled: false isolates this from the
        // CONTINUOUS automatic tracker (same technique CorrectSlantRequestTests.cs's own isolation
        // tests use) so nothing else could have moved _effectiveSamplesPerLine before this call.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const double trueMismatchFraction = 1.0005; // 500ppm
        var trueSampleRate = (int)(declaredSampleRate * trueMismatchFraction);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        using var decoder = new AnalogFmSstvDecoder(declaredSampleRate, autoSlantEnabled: false, rxBufferMode: RxBufferMode.On);

        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && decoder.RxLineStagingBufferForTests is not { LineCount: >= 40 })
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(decoder.RxLineStagingBufferForTests is { LineCount: >= 40 }, "Test setup problem: never staged 40 real decoded lines.");

        var result = decoder.TryCorrectSlantForTests();

        Assert.True(result, "The search should find a real correction against a genuinely mismatched real signal.");
        var correctedRate = decoder.EffectiveSamplesPerLineForTests / (mode.LineDurationMs / 1000.0);
        // Auditor code-review finding, Phase 8d round 1: an earlier version of this test compared
        // against `declaredSampleRate * trueMismatchFraction` (44122.05) instead of the actual ENCODED
        // rate, `trueSampleRate` (44122, `(int)`-truncated at encode time -- the real signal this test
        // decodes was generated at the truncated value, not the untruncated one) -- immaterial at a
        // loose tolerance, but wrong once the tolerance tightens, which it now has (see below).
        //
        // Tolerance anchored on the MISMATCH MAGNITUDE, not the absolute rate: the earlier version's
        // 10%-of-44100 tolerance (~4412Hz) was ~200x the actual 22Hz drift this scenario introduces --
        // loose enough that a correction of +0.01Hz (nowhere near the real mismatch) would still pass.
        // +-5Hz here is ~25% of the true 22Hz drift -- generous for a coarse, threshold-gated,
        // 5-iteration search against REAL demodulated sync-envelope data (real noise/jitter, unlike
        // every synthetic fixture above), while still actually discriminating "found something in the
        // right ballpark" from "found nothing meaningful."
        Assert.Equal(trueSampleRate, correctedRate, tolerance: 5.0);
        Assert.True(correctedRate > declaredSampleRate, "Expected the corrected rate to be measurably above the declared (uncorrected) rate, matching the true positive mismatch.");
    }

    private static float[] Encode(SstvModeDefinition mode, IImageSource sourceImage, int sampleRate)
    {
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, sourceImage).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }
}
