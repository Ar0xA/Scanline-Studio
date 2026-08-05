namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="MonoAveragedPairedScanlineDecoder"/>'s known, documented int-
/// truncation divergence from legacy (S21, spec/14-roadmap.md) -- not exercised via the full
/// <c>DecodeLine</c> pixel pipeline, per this project's chop-into-pieces methodology: this is a pure
/// numerical property of the gray-level formula, independent of scanline/segment/pixel-array plumbing.
/// </summary>
public class MonoAveragedPairedScanlineDecoderTests
{
    // C#'s `(int)` cast on a double truncates toward zero, same as C++'s implicit double-to-int
    // conversion (the semantics `d *= <double>` on a C++ int variable actually uses) -- NOT
    // Math.Truncate's own toward-zero behavior confused with Math.Floor's toward-negative-infinity
    // one; spelled out here since C#'s `(int)` cast is what this method actually needs to match, not
    // either named Math method.
    private static int TruncateTowardZero(double value) => value >= 0 ? (int)value : -(int)(-value);

    // Legacy's real two-truncation chain (Main.cpp:4058-4073's GetPictureLevel, which itself calls
    // Main.cpp:4038-4057's GetPixelLevel exactly once per pixel -- the truncation chain is unaffected
    // by GetPictureLevel's own m_KSB peak-vs-lag selection, so collapsing that one hop here doesn't
    // change the math -- then Main.cpp:4437-4440's RM8/RM12 branch), replicated independently of
    // MonoAveragedPairedScanlineDecoder's own C# formula -- this is the reference this port's own
    // formula is measured against, not a restatement of it. sys.m_DemOff defaults to 0,
    // sys.m_DemWhite/m_DemBlack both default to 128.0/16384.0 (ComLib.h:242-243, Main.cpp:875-877),
    // and sys.m_DemCalibration defaults to 0 (Main.cpp:878), so GetPixelLevel's calibration branch is
    // never live on the default path this port models -- this single default pair covers this port's
    // whole reachable domain, not an incomplete scope.
    private static int LegacyTwoTruncationChain(int rawInt16Sample)
    {
        const double demWhiteOrBlack = 128.0 / 16384.0; // identical for both signs at the shipped default
        var d1 = TruncateTowardZero(rawInt16Sample * demWhiteOrBlack); // GetPixelLevel's own truncation
        var d2 = TruncateTowardZero(d1 * MonoAveragedPairedScanlineDecoder.RmGainFactor); // the RM8/RM12 branch's own `d *= gain`
        var withBias = d2 + 128;
        return Math.Clamp(withBias, 0, 255); // Limit256
    }

    // This port's own actual formula (MonoAveragedPairedScanlineDecoder.cs's DecodeLine, lines ~65-75),
    // reproduced here in terms of the same rawInt16Sample domain so both sides can be swept together --
    // NOT reading it via reflection or duplicating logic MonoAveragedPairedScanlineDecoder doesn't
    // actually have; d1_exact below is exactly what GetPixelLevel WOULD return before its own
    // truncation, matching this port's own frequency-domain rawValue-128 term (already proven
    // algebraically identical to legacy's pre-truncation value, see this class's own doc comment/
    // Piece 12's plan-review).
    private static int PortSingleTruncationChain(int rawInt16Sample)
    {
        const double demWhiteOrBlack = 128.0 / 16384.0;
        var d1Exact = rawInt16Sample * demWhiteOrBlack;
        var corrected = d1Exact * MonoAveragedPairedScanlineDecoder.RmGainFactor + 128.0;
        var clamped = Math.Clamp(corrected, 0.0, 255.0);
        return (byte)clamped; // matches DecodeLine's own (byte)Math.Clamp(corrected, 0, 255)
    }

    [Fact]
    public void IntTruncationDivergence_MatchesLegacysExactTwoTruncationChain_WithinMeasuredBound()
    {
        // S21 (spec/14-roadmap.md): "record the already-measured tolerance rationale" -- the existing
        // doc comment in MonoAveragedPairedScanlineDecoder.cs said "max a couple of levels," an
        // estimate, not a measurement. This sweeps every achievable input in this port's own AGC'd
        // sample domain (+-16384, matching AgcSampleAt's own clamp elsewhere in this codebase) and
        // finds the EXACT bound: a maximum divergence of 2 levels, never more, and asymmetric
        // (legacy-minus-port never goes below -1 or above +2) -- not a symmetric +-2.
        var maxAbsDiff = 0;
        var minSignedDiff = int.MaxValue;
        var maxSignedDiff = int.MinValue;

        for (var raw = -16384; raw <= 16384; raw++)
        {
            var legacy = LegacyTwoTruncationChain(raw);
            var port = PortSingleTruncationChain(raw);
            var signedDiff = legacy - port;

            maxAbsDiff = Math.Max(maxAbsDiff, Math.Abs(signedDiff));
            minSignedDiff = Math.Min(minSignedDiff, signedDiff);
            maxSignedDiff = Math.Max(maxSignedDiff, signedDiff);
        }

        Assert.Equal(2, maxAbsDiff);
        Assert.Equal(-1, minSignedDiff);
        Assert.Equal(2, maxSignedDiff);

        // Well inside the existing 10.0 round-trip / 15.0-25.0 golden-vector tolerances this
        // decoder's own output already has to clear.
        Assert.True(maxAbsDiff < 10.0);
    }
}
