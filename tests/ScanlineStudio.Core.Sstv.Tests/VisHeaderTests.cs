namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted regression tests for header-mechanism bugs found by an independent Opus-driven
/// verification pass against legacy source: RM12's non-standard VIS parity bit, and AVT's missing
/// triple-VIS + training-sequence preamble. These check <see cref="VisHeader"/> directly against
/// reference values derived from the legacy source, not just round-trip self-consistency.
/// </summary>
public class VisHeaderTests
{
    private static readonly double[] OutHeadNormalExpectedFrequencies = [1900.0, 1500.0, 1900.0, 1500.0, 2300.0, 1500.0, 2300.0, 1500.0];
    private static readonly double[] OutHeadNarrowExpectedFrequencies = [1900.0, 2300.0, 1900.0, 2300.0];

    [Fact]
    public void GenerateSegments_Rm12ForcedParity_TransmitsLegacyByte0x86()
    {
        // sstv.cpp:1997 -- legacy's real RM12 byte is 0x86, whose parity bit does not match even
        // parity computed from its 7 data bits (6 = 0b0000110 -> computed parity would give 0x06).
        var segments = VisHeader.GenerateSegments(SstvModeRegistry.Rm12.VisCode, VisHeader.Rm12ForcedParityBit).ToList();

        Assert.Equal(0x86, ExtractVisByte(segments));
    }

    [Fact]
    public void GenerateSegments_NormalMode_ComputesEvenParity()
    {
        // Robot36: legacy's real byte is 0x88 (sstv.cpp), which *does* follow even parity, so the
        // default (non-forced) computed-parity path must still reproduce it exactly.
        var segments = VisHeader.GenerateSegments(SstvModeRegistry.Robot36.VisCode).ToList();

        Assert.Equal(0x88, ExtractVisByte(segments));
    }

    [Fact]
    public void GenerateAvtSegments_TotalHeaderDuration_MatchesLegacy()
    {
        var headerDurationMs = VisHeader.GenerateAvtSegments(SstvModeRegistry.Avt.VisCode).Sum(s => s.DurationMs);

        // Legacy's own RX budget for this exact preamble, sstv.cpp:2140: 3 VIS blocks (910ms each,
        // Main.cpp:7430) + the training sequence (Main.cpp:7563-7575: 32 * (1 marker + 16 bits) *
        // 9.7646ms + a trailing 0.30514375ms blip).
        var expectedMs = 3 * VisHeader.AvtVisBlockDurationMs + VisHeader.AvtTrainingSequenceDurationMs;
        Assert.Equal(8042.24754375, expectedMs, precision: 6);
        Assert.Equal(expectedMs, headerDurationMs, precision: 6);
    }

    [Fact]
    public void ScottiePostVisPulse_MatchesLegacyConstants()
    {
        // Main.cpp:7576-7578: mp->Write(1200, 9.0).
        Assert.Equal(1200.0, VisHeader.ScottiePostVisPulseFrequencyHz);
        Assert.Equal(9.0, VisHeader.ScottiePostVisPulseDurationMs);
    }

    [Fact]
    public void GenerateOutHeadSegments_Normal_MatchesLegacysExactToneSequence()
    {
        // Main.cpp:7282-7291 (case 0, non-narrow arm): 1900,1500,1900,1500,2300,1500,2300,1500,
        // each 100ms.
        var segments = VisHeader.GenerateOutHeadSegments(narrow: false).ToList();

        Assert.Equal(
            OutHeadNormalExpectedFrequencies,
            segments.Select(s => s.FrequencyHz));
        Assert.All(segments, s => Assert.Equal(100.0, s.DurationMs));
        Assert.Equal(800.0, segments.Sum(s => s.DurationMs));
        Assert.Equal(VisHeader.OutHeadNormalDurationMs, segments.Sum(s => s.DurationMs));
    }

    [Fact]
    public void GenerateOutHeadSegments_Narrow_MatchesLegacysExactToneSequence()
    {
        // Main.cpp:7276-7280 (case 0, narrow arm): 1900,2300,1900,2300, each 100ms.
        var segments = VisHeader.GenerateOutHeadSegments(narrow: true).ToList();

        Assert.Equal(
            OutHeadNarrowExpectedFrequencies,
            segments.Select(s => s.FrequencyHz));
        Assert.All(segments, s => Assert.Equal(100.0, s.DurationMs));
        Assert.Equal(400.0, segments.Sum(s => s.DurationMs));
        Assert.Equal(VisHeader.OutHeadNarrowDurationMs, segments.Sum(s => s.DurationMs));
    }

    [Theory]
    [InlineData("robot-36", false)]
    [InlineData("avt", false)] // SHOULD item 5's own key case: AVT gets the NORMAL burst, not a
                                // special AVT-only header (Main.cpp:7392-7429 -- OutHEAD runs before
                                // the narrow-vs-normal branch that AVT's own 3x-VIS-repeat lives
                                // inside, so it's a normal-mode leader like everyone else's).
    [InlineData("mn110", true)]
    public async Task AnalogFmSstvEncoder_RealOutput_StartsWithOutHeadBurst_BeforeVisHeader(string modeId, bool narrow)
    {
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var image = new ScanlineStudio.Core.Imaging.ArrayImageSource(
            mode.ImageWidth, mode.ImageHeight, new ScanlineStudio.Abstractions.Imaging.Rgb24[mode.ImageWidth * mode.ImageHeight]);
        var encoder = new AnalogFmSstvEncoder(sampleRate);

        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            samples.Add(sample);
            if (samples.Count >= (int)(0.5 * sampleRate)) // 500ms is enough to cover either variant's 2nd tone
            {
                break; // only need the leading OutHEAD region, not the whole (multi-second) transmission
            }
        }

        // A real ordering/wiring check, not just a duration/sample-count check (which a segment
        // appended at the wrong position could still pass): measures the actual tone frequency at
        // t=150ms via zero-crossing rate over a clean interior window (110-190ms, clear of both
        // segment-boundary transients) -- this is the SECOND OutHEAD tone for both variants (normal:
        // 1500Hz; narrow: 2300Hz), which is NOT what would be playing at t=150ms if OutHEAD were
        // missing entirely (VIS's own leader is a single unbroken 300ms of 1900Hz, so a
        // no-OutHEAD encode would still read ~1900Hz at t=150ms, not 1500/2300Hz).
        var windowStart = (int)(0.110 * sampleRate);
        var windowEnd = (int)(0.190 * sampleRate);
        var measuredHz = EstimateFrequencyByZeroCrossings(samples, windowStart, windowEnd, sampleRate);
        var expectedHz = narrow ? 2300.0 : 1500.0;

        Assert.True(
            Math.Abs(measuredHz - expectedHz) < 50.0,
            $"[{modeId}] measured ~{measuredHz:F0}Hz at t=150ms, expected ~{expectedHz}Hz (OutHEAD's 2nd tone) -- OutHEAD may be missing, misordered, or wrong-variant.");
    }

    private static double EstimateFrequencyByZeroCrossings(IReadOnlyList<float> samples, int start, int end, int sampleRate)
    {
        var crossings = 0;
        for (var i = start + 1; i < end; i++)
        {
            if (Math.Sign(samples[i]) != Math.Sign(samples[i - 1]) && samples[i] != 0)
            {
                crossings++;
            }
        }

        var windowSeconds = (end - start) / (double)sampleRate;
        return crossings / 2.0 / windowSeconds; // 2 zero-crossings per full cycle
    }

    [Fact]
    public void SearchCeilings_MatchIndependentlyHandDerivedValues()
    {
        // Band-1 S3 fix (pre-Phase-2 audit): pins these against values hand-derived directly from
        // AnalogFmSstvDecoder.TryDecodeVisDataBits'/TryDecodeNarrowModeHeader's own existing (and
        // untouched by this piece) inline formulas, so a future edit to either method's ceiling
        // can't silently desync from these shared constants without a test failing.
        //
        // Normal (8-bit as of S10 -- FirstByteBitCount, 7 data bits + parity, spec/14-roadmap.md):
        // leader*2(600) + break(10) + retryMargin(200) + confirmHold(15) + 8*bit(240) = 1065
        Assert.Equal(1065.0, VisHeader.NormalSearchCeilingMs, precision: 6);
        // Extended (16-bit): 600 + 10 + 200 + 15 + 16*30(480) = 1305
        Assert.Equal(1305.0, VisHeader.ExtendedSearchCeilingMs, precision: 6);
        // Narrow: leader(300) + guard*2(200) + bit*(1+24)(550) + retryMargin(200) = 1250.
        // Functional-audit fix (D6, round 3): the leader term was previously missing from this
        // constant's own definition entirely -- headerStart marks where the 300ms leader begins
        // (matching NarrowHeaderTotalDurationMs's own leader-inclusive definition), so omitting it
        // here meant the "+200 retry margin" was providing zero real margin beyond the packet's own
        // bare minimum duration (950ms coincided with NarrowHeaderTotalDurationMs's own 950ms by
        // arithmetic accident, not by design). 1250 is the corrected value.
        Assert.Equal(1250.0, VisHeader.NarrowSearchCeilingMs, precision: 6);
        // Max across all three -- the extended ceiling is the largest.
        Assert.Equal(1305.0, VisHeader.MaxSearchCeilingMs, precision: 6);
    }

    /// <summary>Segment layout from <see cref="VisHeader.GenerateSegments"/>: 0=leader, 1=break,
    /// 2=leader, 3=start bit, 4-10=7 data bits (LSB first), 11=parity, 12=stop bit.</summary>
    private static int ExtractVisByte(IReadOnlyList<(double FrequencyHz, double DurationMs)> segments)
    {
        var value = 0;
        for (var bitIndex = 0; bitIndex < 8; bitIndex++)
        {
            var bit = segments[4 + bitIndex].FrequencyHz == VisHeader.Bit1FrequencyHz ? 1 : 0;
            value |= bit << bitIndex;
        }

        return value;
    }
}
