namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Functional-audit addition (D6, round 1): <see cref="VisBitDecision.TryDecide"/> is the shared
/// stateless VIS-bit decision rule (`sstv.cpp:1981-1988`) both <see cref="VisLockStateMachine"/> and
/// <see cref="AnalogFmSstvDecoder"/>'s fixed-window header path rely on -- before this file, the
/// weak/ambiguous REJECT arm (as opposed to the accept-and-pick-a-winner arm) had no direct test
/// anywhere in the suite; it was only reached incidentally, via a break-tone test exercising a
/// different code path entirely. A change to the reject condition (e.g. dropping either half of the
/// `||`) would very plausibly have passed the whole existing suite.
/// </summary>
public class VisBitDecisionTests
{
    [Fact]
    public void TryDecide_BothTonesWeakerThanReference_Rejects()
    {
        // sstv.cpp:1981's first half: (d11<d19 && d13<d19) -- both candidate tones weaker than the
        // 1900Hz reference, regardless of how far apart they are from each other.
        var accepted = VisBitDecision.TryDecide(d11: 100, d13: 50, d19: 500, slvl2: 10, out var bit);

        Assert.False(accepted);
        Assert.Equal(0, bit);
    }

    [Fact]
    public void TryDecide_BothTonesStrongButTooCloseToCall_Rejects()
    {
        // sstv.cpp:1981's second half: fabs(d11-d13) < m_SLvl2 -- both tones clearly beat d19, but
        // are too close to each other to confidently call a winner.
        var accepted = VisBitDecision.TryDecide(d11: 600, d13: 605, d19: 500, slvl2: 10, out var bit);

        Assert.False(accepted);
        Assert.Equal(0, bit);
    }

    [Fact]
    public void TryDecide_D11ClearlyLouder_AcceptsBitOne()
    {
        var accepted = VisBitDecision.TryDecide(d11: 800, d13: 400, d19: 500, slvl2: 10, out var bit);

        Assert.True(accepted);
        Assert.Equal(1, bit);
    }

    [Fact]
    public void TryDecide_D13ClearlyLouder_AcceptsBitZero()
    {
        var accepted = VisBitDecision.TryDecide(d11: 400, d13: 800, d19: 500, slvl2: 10, out var bit);

        Assert.True(accepted);
        Assert.Equal(0, bit);
    }

    [Fact]
    public void TryDecide_OnlyD11WeakerThanReference_StillAcceptsOnTheSeparationMargin()
    {
        // Legacy's reject condition requires BOTH tones weak (&&, not ||) -- one strong tone with the
        // other weak is not itself a reject reason, only the separation-margin check is. d13 (600)
        // clears d19 (500); d11 (100) does not -- (d11<d19 && d13<d19) is false since d13 fails its
        // half, so this falls through to the separation check, which passes (|100-600|=500 >= 10).
        var accepted = VisBitDecision.TryDecide(d11: 100, d13: 600, d19: 500, slvl2: 10, out var bit);

        Assert.True(accepted);
        Assert.Equal(0, bit); // d13 > d11, so bit = 0
    }

    [Fact]
    public void TryDecide_JustInsideTheSeparationThreshold_Rejects()
    {
        // fabs(d11-d13) < slvl2 -- a diff strictly less than slvl2 rejects.
        var accepted = VisBitDecision.TryDecide(d11: 509, d13: 500, d19: 100, slvl2: 10, out var bit);

        Assert.False(accepted);
        Assert.Equal(0, bit);
    }

    [Fact]
    public void TryDecide_ExactlyAtTheSeparationThreshold_Accepts()
    {
        // Functional-audit fix (D6, round 2): the previous version of this test (named
        // "...Rejects") used diff=9 against slvl2=10, which rejects under EITHER `<` or `<=` --
        // it never actually exercised the boundary its own name and comment claimed to pin. The
        // real discriminating case is diff EXACTLY equal to slvl2: legacy's `fabs(d11-d13) <
        // m_SLvl2` (sstv.cpp:1981) is a STRICT less-than, so a diff of exactly slvl2 does NOT
        // satisfy the reject condition and must be accepted -- a `<=` mutation would flip this to
        // a reject, which is exactly what this test now catches.
        var accepted = VisBitDecision.TryDecide(d11: 510, d13: 500, d19: 100, slvl2: 10, out var bit);

        Assert.True(accepted);
        Assert.Equal(1, bit);
    }

    [Fact]
    public void TryDecide_JustOutsideTheSeparationThreshold_Accepts()
    {
        var accepted = VisBitDecision.TryDecide(d11: 511, d13: 500, d19: 100, slvl2: 10, out var bit);

        Assert.True(accepted);
        Assert.Equal(1, bit);
    }
}
