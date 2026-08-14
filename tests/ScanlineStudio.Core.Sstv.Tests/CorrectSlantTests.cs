using ScanlineStudio.Abstractions.Sstv;

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
/// </summary>
public class CorrectSlantTests
{
    private const int SampleRate = 1000;
    private const int NominalLineWidth = 150; // Robot36.LineDurationMs (150.0) / 1000 * SampleRate (1000)

    private static AnalogFmSstvDecoder LockedDecoder(RxBufferMode rxBufferMode = RxBufferMode.On)
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: rxBufferMode);
        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[1]); // drains the deferred ForceMode request -- sets _mode, but NOT _effectiveSamplesPerLine (see InitializeSlantForTests's own doc comment: that's deferred to a real sync-anchor correction for any non-AVT mode)
        decoder.InitializeSlantForTests(SstvModeRegistry.Robot36); // sets _effectiveSamplesPerLine = 150.0 and clears the (already-empty) staging buffer, matching what a real lock would eventually do
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
        // Exact hand-computed convergence isn't asserted here (the search is iterative and
        // self-referential -- each non-converged iteration re-partitions the SAME absolute data
        // against a new candidate line width, making a fully closed-form expected result genuinely
        // involved to derive by hand; a fixture engineered for single-iteration convergence, with a
        // hand-computed k0/fq asserted to a stated tolerance, is sub-piece 8d's own explicit scope).
        // What IS hand-verifiable and asserted here: since every accepted row's peak position
        // increases monotonically with row index by construction, the regression's own slope must be
        // positive, and the corrected rate must therefore come out HIGHER than the nominal starting
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
}
