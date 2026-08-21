using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Pins every mode's identification code (VIS/extended-VIS/narrow) and output dimensions against
/// legacy source, independently of <c>SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming</c>
/// (which only pins segment-duration totals). Closes a coverage gap flagged by Tier A Batch 5
/// chunk 5a (docs/functional-audit-playbook.md): without this, a transposed code between two
/// same-duration modes, or a wrong image width/height, would pass the rest of the suite.
///
/// Normal VIS codes are the legacy VIS-decode switch's full byte (`sstv.cpp:1993-2074`) with the
/// parity bit masked off (<c>&amp; 0x7f</c>) -- <see cref="SstvModeDefinition.VisCode"/> stores the
/// parity-stripped 7-bit code, parity itself is <see cref="VisHeader"/>'s concern (chunk 5b), not
/// this table's. Extended-VIS and narrow-mode codes are the raw bytes legacy sends as-is
/// (`Main.cpp:7500-7538` and `:7402-7421`), no parity involved. Dimensions cross-checked against
/// `GetBitmapSize`/`GetPictureSize` (`sstv.cpp:607-653`) and each family's TX loop bound.
/// </summary>
public class SstvModeRegistryTests
{
    public static IEnumerable<object[]> AllModes() =>
        SstvModeRegistry.All.Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Mode_MatchesLegacyIdentificationCodesAndDimensions(SstvModeDefinition mode)
    {
        var expected = Expected[mode.Id];

        Assert.Equal(expected.VisCode, mode.VisCode);
        Assert.Equal(expected.ExtendedVisCode, mode.ExtendedVisCode);
        Assert.Equal(expected.NarrowModeCode, mode.NarrowModeCode);
        Assert.Equal(expected.ImageWidth, mode.ImageWidth);
        Assert.Equal(expected.ImageHeight, mode.ImageHeight);
    }

    [Fact]
    public void Expected_CoversEveryRegisteredMode()
    {
        Assert.Equal(SstvModeRegistry.All.Select(m => m.Id).OrderBy(id => id), Expected.Keys.OrderBy(id => id));
    }

    private sealed record ExpectedMode(int VisCode, int? ExtendedVisCode, int? NarrowModeCode, int ImageWidth, int ImageHeight);

    // Transcribed independently from yoniq-old/YONIQ-main/sstv.cpp:1993-2074 (normal VIS switch,
    // parity-stripped), Main.cpp:7500-7538 (extended VIS bytes), Main.cpp:7402-7421 (narrow FSK mode
    // bytes), and each mode's ImageWidth/ImageHeight as independently re-verified against legacy
    // GetBitmapSize/GetPictureSize/TX loop bounds by Tier A Batch 5 chunk 5a's audit round.
    private static readonly Dictionary<string, ExpectedMode> Expected = new()
    {
        ["martin-m1"] = new(44, null, null, 320, 256),
        ["martin-m2"] = new(40, null, null, 320, 256),
        ["scottie-s1"] = new(60, null, null, 320, 256),
        ["scottie-s2"] = new(56, null, null, 320, 256),
        ["scottie-dx"] = new(76, null, null, 320, 256),
        ["robot-36"] = new(8, null, null, 320, 240),
        ["robot-72"] = new(12, null, null, 320, 240),
        ["r24"] = new(4, null, null, 320, 120),
        ["avt"] = new(68, null, null, 320, 240),
        ["p3"] = new(113, null, null, 640, 496),
        ["p5"] = new(114, null, null, 640, 496),
        ["p7"] = new(115, null, null, 640, 496),
        ["mr73"] = new(0, 0x45, null, 320, 256),
        ["mr90"] = new(0, 0x46, null, 320, 256),
        ["mr115"] = new(0, 0x49, null, 320, 256),
        ["mr140"] = new(0, 0x4a, null, 320, 256),
        ["mr175"] = new(0, 0x4c, null, 320, 256),
        ["ml180"] = new(0, 0x85, null, 640, 496),
        ["ml240"] = new(0, 0x86, null, 640, 496),
        ["ml280"] = new(0, 0x89, null, 640, 496),
        ["ml320"] = new(0, 0x8a, null, 640, 496),
        ["mp73"] = new(0, 0x25, null, 320, 256),
        ["mp115"] = new(0, 0x29, null, 320, 256),
        ["mp140"] = new(0, 0x2a, null, 320, 256),
        ["mp175"] = new(0, 0x2c, null, 320, 256),
        ["pd50"] = new(93, null, null, 320, 256),
        ["pd90"] = new(99, null, null, 320, 256),
        ["pd120"] = new(95, null, null, 640, 496),
        ["pd160"] = new(98, null, null, 512, 400),
        ["pd180"] = new(96, null, null, 640, 496),
        ["pd240"] = new(97, null, null, 640, 496),
        ["pd290"] = new(94, null, null, 800, 616),
        ["mn73"] = new(0, null, 0x02, 320, 256),
        ["mn110"] = new(0, null, 0x04, 320, 256),
        ["mn140"] = new(0, null, 0x05, 320, 256),
        ["mc110"] = new(0, null, 0x14, 320, 256),
        ["mc140"] = new(0, null, 0x15, 320, 256),
        ["mc180"] = new(0, null, 0x16, 320, 256),
        ["rm8"] = new(2, null, null, 320, 240),
        ["rm12"] = new(6, null, null, 320, 240),
        ["sc2-180"] = new(55, null, null, 320, 256),
        ["sc2-120"] = new(63, null, null, 320, 256),
        ["sc2-60"] = new(59, null, null, 320, 256),
    };
}
