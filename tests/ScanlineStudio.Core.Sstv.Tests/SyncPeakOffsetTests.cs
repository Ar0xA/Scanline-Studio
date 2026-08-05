using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Piece 8a (of the Robot-36-at-11025Hz decode-gap fix, see spec/14-roadmap.md's live investigation
/// log): pins <see cref="SstvModeRegistry.GetSyncPeakOffsetMs"/> against legacy's real <c>m_OFP</c>
/// literals (`sstv.cpp:657-1108`), transcribed directly from source, one assertion per <c>case</c> --
/// not derived from any other value already in this codebase. Covers every mode in
/// <see cref="SstvModeRegistry.All"/> so a future registry addition without a matching entry here
/// fails loudly (via <see cref="AllModesCovered"/>) rather than silently falling through to the
/// method's own exception only at run time.
/// </summary>
public class SyncPeakOffsetTests
{
    public static readonly TheoryData<SstvModeDefinition, double> ExpectedOffsets = new()
    {
        { SstvModeRegistry.Robot36, 10.7 },
        { SstvModeRegistry.Robot72, 10.7 },
        { SstvModeRegistry.Avt, 0.0 },
        { SstvModeRegistry.ScottieS1, 10.7 },
        { SstvModeRegistry.ScottieS2, 10.8 },
        { SstvModeRegistry.ScottieDx, 10.2 },
        { SstvModeRegistry.MartinM1, 7.2 },
        { SstvModeRegistry.MartinM2, 7.4 },
        { SstvModeRegistry.Sc2180, 7.8 },
        { SstvModeRegistry.Sc2120, 7.5 },
        { SstvModeRegistry.Sc260, 7.9 },
        { SstvModeRegistry.Pd50, 19.3 },
        { SstvModeRegistry.Pd90, 18.9 },
        { SstvModeRegistry.Pd120, 19.4 },
        { SstvModeRegistry.Pd160, 18.9 },
        { SstvModeRegistry.Pd180, 18.9 },
        { SstvModeRegistry.Pd240, 18.9 },
        { SstvModeRegistry.Pd290, 18.9 },
        { SstvModeRegistry.P3, 7.8 },
        { SstvModeRegistry.P5, 9.2 },
        { SstvModeRegistry.P7, 11.5 },
        { SstvModeRegistry.Mr73, 10.6 },
        { SstvModeRegistry.Mr90, 10.6 },
        { SstvModeRegistry.Mr115, 10.6 },
        { SstvModeRegistry.Mr140, 10.6 },
        { SstvModeRegistry.Mr175, 10.6 },
        { SstvModeRegistry.Mp73, 10.5 },
        { SstvModeRegistry.Mp115, 10.5 },
        { SstvModeRegistry.Mp140, 10.5 },
        { SstvModeRegistry.Mp175, 10.5 },
        { SstvModeRegistry.Ml180, 10.6 },
        { SstvModeRegistry.Ml240, 10.6 },
        { SstvModeRegistry.Ml280, 10.6 },
        { SstvModeRegistry.Ml320, 10.6 },
        { SstvModeRegistry.R24, 8.1 },
        { SstvModeRegistry.Rm8, 8.2 },
        { SstvModeRegistry.Rm12, 8.0 },
        { SstvModeRegistry.Mn73, 10.5 },
        { SstvModeRegistry.Mn110, 10.5 },
        { SstvModeRegistry.Mn140, 10.5 },
        { SstvModeRegistry.Mc110, 8.95 },
        { SstvModeRegistry.Mc140, 8.75 },
        { SstvModeRegistry.Mc180, 8.75 },
    };

    [Theory]
    [MemberData(nameof(ExpectedOffsets))]
    public void GetSyncPeakOffsetMs_MatchesLegacyMOfp(SstvModeDefinition mode, double expectedMs)
    {
        Assert.Equal(expectedMs, SstvModeRegistry.GetSyncPeakOffsetMs(mode), precision: 5);
    }

    [Fact]
    public void AllModesCovered()
    {
        var coveredIds = ExpectedOffsets.Select(row => ((SstvModeDefinition)row[0]!).Id).ToHashSet();
        var allIds = SstvModeRegistry.All.Select(m => m.Id).ToHashSet();

        Assert.Equal(allIds, coveredIds);
    }
}
