namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Targeted regression tests for header-mechanism bugs found by an independent Opus-driven
/// verification pass against legacy source: RM12's non-standard VIS parity bit, and AVT's missing
/// triple-VIS + training-sequence preamble. These check <see cref="VisHeader"/> directly against
/// reference values derived from the legacy source, not just round-trip self-consistency.
/// </summary>
public class VisHeaderTests
{
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
        // Main.cpp:7429) + the training sequence (Main.cpp:7563-7575: 32 * (1 marker + 16 bits) *
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
        // Narrow: guard*2(200) + bit*(1+24)(550) + retryMargin(200) = 950
        Assert.Equal(950.0, VisHeader.NarrowSearchCeilingMs, precision: 6);
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
