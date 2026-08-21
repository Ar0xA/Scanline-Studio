namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Piece 10 step 2 (see PROJECT_BRIEF.md/spec/14-roadmap.md's "Piece 10" entry): isolated tests for
/// <see cref="PixelSampleReader"/> before anything wires it in -- zero behavior change to the
/// production decode path, nothing calls this class yet as of this test file.
/// </summary>
public class PixelSampleReaderTests
{
    // Matches AnalogFmSstvDecoder's own production construction (clamp-to-bounds over a fixed list) --
    // a thin helper so individual tests can express their fixture as a plain List<double>, the same
    // way the reader's real doc comment describes its intended production use.
    private static PixelSampleReader FromList(List<double> frequencies, int ksbSamples, int lineEndSampleExclusive, double luminanceMinHz, bool neverPeakPicks) =>
        new(index => frequencies[Math.Clamp(index, 0, frequencies.Count - 1)], ksbSamples, lineEndSampleExclusive, luminanceMinHz, neverPeakPicks);

    [Fact]
    public void ReadPeakPicked_PicksTheHigherFrequency_NotTheLower()
    {
        // Round-3/4 plan review's sign-direction finding: legacy picks the HIGHER raw demodulated
        // value, which corresponds to the HIGHER frequency in this port's own monotonic Hz-to-luma
        // mapping. A sign inversion here would be invisible to any round-trip test (encoder/decoder
        // would still agree with each other) -- this is the dedicated check that catches it.
        var reader = FromList([1600.0, 2000.0], ksbSamples: 1, lineEndSampleExclusive: 10, luminanceMinHz: 1500.0, neverPeakPicks: false);
        Assert.Equal(2000.0, reader.ReadPeakPicked(0, 0));

        // Reversed: now index 0 is higher, index 1 (the peek-ahead target) is lower -- still expect
        // the higher one, proving this isn't just "always pick the peek-ahead sample."
        var reversed = FromList([2000.0, 1600.0], ksbSamples: 1, lineEndSampleExclusive: 10, luminanceMinHz: 1500.0, neverPeakPicks: false);
        Assert.Equal(2000.0, reversed.ReadPeakPicked(0, 0));
    }

    [Fact]
    public void ReadPeakPicked_TiesKeepTheBareSample_StrictLessThanOnly()
    {
        // Main.cpp:4062's `*ip < *(ip+m_KSB)` is strict -- a tie must keep the bare (first) sample,
        // not the peek-ahead one. Using `<=` here would invert behavior on every flat region.
        var reader = FromList([1900.0, 1900.0], ksbSamples: 1, lineEndSampleExclusive: 10, luminanceMinHz: 1500.0, neverPeakPicks: false);
        Assert.Equal(1900.0, reader.ReadPeakPicked(0, 0));
    }

    [Fact]
    public void ReadPeakPicked_PastLineEnd_FloorsAtLuminanceMinHz_NotJustBare()
    {
        // Round-2's plan review wrongly concluded "bare always wins past the line end" (traced to the
        // wrong buffer's clamp). Round 3 corrected this: legacy's real trailing pad value (-16384)
        // equals the mode's own LuminanceMinHz exactly, and the boundary rule is a FLOOR at that
        // value, not "return whatever the bare sample happens to be." This test deliberately
        // constructs a synthetic short line (bare sample itself BELOW LuminanceMinHz) to distinguish
        // the two: a "bare always wins" implementation would return the bare value unchanged; the
        // correct floor implementation clamps it up to LuminanceMinHz. Confirmed unreachable at every
        // currently-registered real mode (see PixelSampleReader's own doc comment) -- this synthetic
        // construction is deliberate, not modeling any specific real mode's numbers.
        const double luminanceMinHz = 1500.0;
        var reader = FromList([1400.0], ksbSamples: 5, lineEndSampleExclusive: 1, luminanceMinHz: luminanceMinHz, neverPeakPicks: false);

        // startSample(0) + ksbSamples(5) = 5 >= lineEndSampleExclusive(1) -- guard fires.
        Assert.Equal(luminanceMinHz, reader.ReadPeakPicked(0, 0));
    }

    [Fact]
    public void ReadPeakPicked_PastLineEnd_BareAboveFloor_StaysAtBare()
    {
        // Complementary case: when the bare sample is already above LuminanceMinHz, the floor is a
        // no-op (Math.Max picks the bare value) -- confirms this isn't accidentally clamping every
        // boundary read down to the floor, only ever flooring genuinely-low values up.
        const double luminanceMinHz = 1500.0;
        var reader = FromList([1900.0], ksbSamples: 5, lineEndSampleExclusive: 1, luminanceMinHz: luminanceMinHz, neverPeakPicks: false);
        Assert.Equal(1900.0, reader.ReadPeakPicked(0, 0));
    }

    [Fact]
    public void ReadPeakPicked_WithinLine_NeverTouchesTheFloor()
    {
        // Sanity check that the floor logic only activates at the actual boundary condition, not for
        // ordinary in-line reads even when the bare sample is naturally low.
        const double luminanceMinHz = 1500.0;
        var reader = FromList([1400.0, 1450.0, 1900.0], ksbSamples: 1, lineEndSampleExclusive: 10, luminanceMinHz: luminanceMinHz, neverPeakPicks: false);

        // index 0 (1400) vs index 1 (1450): picks 1450, not floored to 1500 -- this is a genuine
        // in-line comparison, not the boundary case.
        Assert.Equal(1450.0, reader.ReadPeakPicked(0, 0));
    }

    [Fact]
    public void ReadPeakPicked_NeverPeakPicks_AlwaysReturnsBare_EvenWhenPeekIsHigher()
    {
        // Scottie DX's exception -- resolved entirely inside the reader, not by any caller.
        var reader = FromList([1600.0, 2000.0], ksbSamples: 1, lineEndSampleExclusive: 10, luminanceMinHz: 1500.0, neverPeakPicks: true);

        Assert.Equal(1600.0, reader.ReadPeakPicked(0, 0));
        Assert.Equal(reader.ReadBare(0, 0), reader.ReadPeakPicked(0, 0));
    }

    [Fact]
    public void Constructor_KsbSamplesLessThanOne_ThrowsArgumentOutOfRangeException()
    {
        // Legacy guarantees ksbSamples >= 1 (sstv.cpp:1179's `if(!m_KSB) m_KSB++`) -- a 0 would
        // silently degrade every peak-pick to a bare read with no test failing otherwise.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PixelSampleReader(_ => 0.0, ksbSamples: 0, lineEndSampleExclusive: 10, luminanceMinHz: 1500.0, neverPeakPicks: false));
    }

    [Fact]
    public void ReadBare_ClampsToBufferBounds_MatchingPreExistingBehavior()
    {
        // Matches AnalogFmSstvDecoder's pre-piece-10 SampleFrequencyAt exactly -- this method is a
        // pure extraction, not a behavior change.
        var reader = FromList([1700.0, 1800.0, 1900.0], ksbSamples: 1, lineEndSampleExclusive: 10, luminanceMinHz: 1500.0, neverPeakPicks: false);

        Assert.Equal(1700.0, reader.ReadBare(-5, -5));
        Assert.Equal(1900.0, reader.ReadBare(500, 500));
    }
}
