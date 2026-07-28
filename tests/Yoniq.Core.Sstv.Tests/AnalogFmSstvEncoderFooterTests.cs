namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Targeted tests for the post-image footer tone (<c>Main.cpp:6994-7013</c>'s "no FSK ID configured"
/// branch — see <see cref="AnalogFmSstvEncoder.GenerateFooterSegments"/>'s doc comment for what's
/// deliberately deferred). Checked directly against the segment sequence rather than only through a
/// full round-trip, since the decoder never needs to interpret the footer (it stops once it has
/// <c>mode.ImageHeight</c> lines) — a round-trip test alone couldn't distinguish "footer emitted
/// correctly" from "footer missing entirely."
/// </summary>
public class AnalogFmSstvEncoderFooterTests
{
    [Fact]
    public void GenerateFooterSegments_NormalMode_EmitsTrailingCarrierThenAlternatingTones()
    {
        // Main.cpp:6999-7005 (!VOX && !fTxNarrow): WriteC(1500, min(m_TW, SampFreq/2)), then
        // Write(1900,100), Write(1500,100), Write(1900,100), Write(1500,100).
        var mode = SstvModeRegistry.Robot36; // LineDurationMs = 150, well under the 500ms cap
        var segments = AnalogFmSstvEncoder.GenerateFooterSegments(mode).ToList();

        Assert.Equal(5, segments.Count);
        Assert.Equal((1500.0, mode.LineDurationMs), segments[0]);
        Assert.Equal((1900.0, 100.0), segments[1]);
        Assert.Equal((1500.0, 100.0), segments[2]);
        Assert.Equal((1900.0, 100.0), segments[3]);
        Assert.Equal((1500.0, 100.0), segments[4]);
    }

    [Fact]
    public void GenerateFooterSegments_NormalMode_CapsTrailingCarrierAt500Ms()
    {
        // Main.cpp:6998/7000: SSTVSET.m_TW > tw ? tw : SSTVSET.m_TW, tw = SampFreq/2 samples = 500ms.
        var mode = SstvModeRegistry.ScottieDx; // LineDurationMs = 1050.3, well over the cap
        var segments = AnalogFmSstvEncoder.GenerateFooterSegments(mode).ToList();

        Assert.Equal((1500.0, 500.0), segments[0]);
    }

    [Fact]
    public void GenerateFooterSegments_NarrowMode_EmitsSingleTrailingCarrierOnly()
    {
        // Main.cpp:7006-7008 (fTxNarrow): WriteC(1900, min(m_TW, SampFreq/2)), nothing else.
        var mode = SstvModeRegistry.Mn73; // LineDurationMs = 570, over the cap
        var segments = AnalogFmSstvEncoder.GenerateFooterSegments(mode).ToList();

        Assert.Equal([(1900.0, 500.0)], segments);
    }
}
