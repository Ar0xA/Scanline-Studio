using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Piece 10 step 1 (see spec/14-roadmap.md and PROJECT_BRIEF.md's "Piece 10" entry for the full
/// legacy citation trail): pins <see cref="SstvModeRegistry.GetPeakPickParameters"/> against legacy's
/// real 5-group <c>m_KSS</c>/<c>m_KSB</c> switch (`sstv.cpp:1110-1179`), one assertion per mode, not
/// derived from any other value already in this codebase -- covers every mode in
/// <see cref="SstvModeRegistry.All"/> so a future registry addition without a matching entry here
/// fails loudly (<see cref="AllModesCovered"/>), matching <c>SyncPeakOffsetTests</c>' own established
/// pattern (piece 8a). <see cref="GetKsbSamples_MatchesHandComputedValue"/> spot-checks the actual
/// arithmetic (truncation, floor-to-1) against a few values independently hand-computed from source
/// during this piece's own plan review, rather than re-deriving all 43 modes x N sample rates.
/// </summary>
public class PeakPickParametersTests
{
    // Group A: m_KSS = m_KS - m_KS/480; m_KSB = m_KSS/1280 (sstv.cpp:1111-1118)
    private const double GroupAFactor = 479.0 / 480.0;
    private const double GroupADivisor = 1280.0;

    // Group B: m_KSS = m_KS - m_KS/1280; m_KSB = m_KSS/1280 (sstv.cpp:1120-1127)
    private const double GroupBFactor = 1279.0 / 1280.0;
    private const double GroupBDivisor = 1280.0;

    // Group C: m_KSS = m_KS (no trim); m_KSB = m_KSS/1280 (sstv.cpp:1129-1146)
    private const double GroupCFactor = 1.0;
    private const double GroupCDivisor = 1280.0;

    // Group D: m_KSS = m_KS - m_KS/640; m_KSB = m_KSS/1024 (sstv.cpp:1148-1155)
    private const double GroupDFactor = 639.0 / 640.0;
    private const double GroupDDivisor = 1024.0;

    // Group E (default): m_KSS = m_KS - m_KS/240; m_KSB = m_KSS/640 (sstv.cpp:1156-1160)
    private const double GroupEFactor = 239.0 / 240.0;
    private const double GroupEDivisor = 640.0;

    public static readonly TheoryData<SstvModeDefinition, double, double> ExpectedParameters = new()
    {
        // Group A
        { SstvModeRegistry.Pd120, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.Pd160, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.Pd180, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.Pd240, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.Pd290, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.P3, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.P5, GroupAFactor, GroupADivisor },
        { SstvModeRegistry.P7, GroupAFactor, GroupADivisor },

        // Group B
        { SstvModeRegistry.Mp73, GroupBFactor, GroupBDivisor },
        { SstvModeRegistry.Mn73, GroupBFactor, GroupBDivisor },
        { SstvModeRegistry.ScottieDx, GroupBFactor, GroupBDivisor },

        // Group C
        { SstvModeRegistry.Sc2180, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mp115, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mp140, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mp175, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mr90, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mr115, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mr140, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mr175, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Ml180, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Ml240, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Ml280, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Ml320, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mn110, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mn140, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mc110, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mc140, GroupCFactor, GroupCDivisor },
        { SstvModeRegistry.Mc180, GroupCFactor, GroupCDivisor },

        // Group D
        { SstvModeRegistry.Mr73, GroupDFactor, GroupDDivisor },

        // Group E (default) -- includes PD50/PD90, confirmed absent from group A's own case list
        { SstvModeRegistry.Robot36, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Robot72, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Avt, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.ScottieS1, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.ScottieS2, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.MartinM1, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.MartinM2, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Sc260, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Sc2120, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.R24, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Rm8, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Rm12, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Pd50, GroupEFactor, GroupEDivisor },
        { SstvModeRegistry.Pd90, GroupEFactor, GroupEDivisor },
    };

    [Theory]
    [MemberData(nameof(ExpectedParameters))]
    public void GetPeakPickParameters_MatchesLegacyGroup(SstvModeDefinition mode, double expectedFactor, double expectedDivisor)
    {
        var actual = SstvModeRegistry.GetPeakPickParameters(mode);
        Assert.Equal(expectedFactor, actual.KssTrimFactor, precision: 10);
        Assert.Equal(expectedDivisor, actual.KsbDivisor, precision: 10);
    }

    [Fact]
    public void AllModesCovered()
    {
        var coveredIds = ExpectedParameters.Select(row => ((SstvModeDefinition)row[0]!).Id).ToHashSet();
        var allIds = SstvModeRegistry.All.Select(m => m.Id).ToHashSet();

        Assert.Equal(allIds, coveredIds);
    }

    [Fact]
    public void NeverPeakPicks_IsTrueOnlyForScottieDx()
    {
        foreach (var mode in SstvModeRegistry.All)
        {
            var expected = mode == SstvModeRegistry.ScottieDx;
            Assert.Equal(expected, SstvModeRegistry.NeverPeakPicks(mode));
        }
    }

    // Hand-computed independently from source during this piece's plan review (round 3/4), not
    // re-derived from GetPeakPickParameters -- these are the exact worked examples that established
    // the guard-unreachability finding, so they double as a correctness check on the arithmetic
    // itself (truncation, not rounding; floor-to-1 when truncation reaches 0).
    [Theory]
    // AVT: m_KS=125.0ms=1378.125 samples @11025Hz; m_KSS=1378.125*239/240=1372.383;
    // m_KSB=int(1372.383/640)=2.
    [InlineData("avt", 11025, 2)]
    // PD290 (the worst-case peak-pick-to-pixel-width ratio, ~0.62, per round 4's independent
    // re-derivation): m_KS=228.80ms=2522.52 samples @11025Hz; m_KSS=2522.52*479/480=2517.265;
    // m_KSB=int(2517.265/1280)=1.
    [InlineData("pd290", 11025, 1)]
    public void GetKsbSamples_MatchesHandComputedValue(string modeId, int sampleRate, int expectedKsbSamples)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        Assert.Equal(expectedKsbSamples, SstvModeRegistry.GetKsbSamples(mode, sampleRate));
    }

    [Fact]
    public void GetKsbSamples_NeverZero_EvenAtLowSampleRates()
    {
        // The universal floor (sstv.cpp:1179, if(!m_KSB) m_KSB++;) -- spot-check a genuinely short
        // mono mode at a low sample rate, where truncation is most likely to reach 0 before the floor.
        var rm8 = SstvModeRegistry.Rm8;
        foreach (var sampleRate in new[] { 6000, 8000, 11025 })
        {
            Assert.True(SstvModeRegistry.GetKsbSamples(rm8, sampleRate) >= 1);
        }
    }
}
