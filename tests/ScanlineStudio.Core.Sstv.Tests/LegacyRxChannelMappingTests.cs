using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// The RX counterpart to <see cref="LegacyTxChannelOrderTests"/>. `CLAUDE.md` §4 states that TX and
/// RX are separate legacy code paths and that neither may be inferred from the other. That file
/// covers TX. This one covers RX.
///
/// <para><b>Legacy really does group modes differently in the two directions</b>, which is what makes
/// this a distinct guard rather than a restatement. On TX, `r24` has its own `LineR24` while `robot-72`
/// has `LineR72`; on RX both sit in ONE branch alongside every MR and ML mode
/// (`Main.cpp:4315-4325`). On TX, PD, MP and MN are three separate functions; on RX they are a single
/// branch (`:4367-4380`).</para>
///
/// <para><b>Scottie is the mode the incident was about, and the two directions really do differ.</b>
/// TX writes separator-G, separator-B, sync, separator-R (`LineSCT`, `:6620`). Legacy's RX branch
/// reads R first, then G, then B (`:4222`/`:4242`/`:4258`, legacy's own `// R` / `// G` / `// B`
/// markers), because it anchors the line origin on the sync.</para>
///
/// <para><b>The port does not anchor that way.</b> It walks the segment list from index 0, so it
/// reads G, B, sync, R and reaches the same picture by mapping each segment to its own component.
/// That difference is a decoder-anchoring choice and is NOT what this file tests. What it tests is
/// that every segment reaches the component legacy would put it in.</para>
///
/// <para><b>The encoder is deliberately not involved.</b> A round trip cannot test this: the port
/// uses one segment list for both directions, so a swapped channel would be swapped symmetrically and
/// would reconcile perfectly. Instead each scan segment is filled with a distinct known value taken
/// straight from its <c>ChannelName</c>, and only the decoder runs. If the decoder maps a segment to
/// the wrong output component, the value comes back in the wrong place.</para>
///
/// <para><b>What this file pins, and what it does not.</b> It pins the segment-to-component
/// MAPPING: a segment named G must reach the green output. It does NOT independently pin segment
/// ORDER — the port's decoders route by channel NAME, so a registry list in the wrong order still
/// passes every row here. Order is pinned once, in `LegacyTxChannelOrderTests`, against legacy TX
/// source. The registry's segment list is shared by both directions, so that one pin covers both.
/// Do not read a green run here as independent confirmation of order.</para>
/// </summary>
public sealed class LegacyRxChannelMappingTests
{
    private const int SampleRate = 11025;

    // Distinct, well-separated, and inside every mode's luminance range once scaled. Deliberately not
    // 0 or 255: a clamp bug at either rail would otherwise look like a pass.
    private const byte HighValue = 200;
    private const byte MidValue = 128;
    private const byte LowValue = 40;

    public static TheoryData<string> RgbFamilyModes() => new()
    {
        // Legacy RX branch `smSCT1/SCT2/SCTDX` (Main.cpp:4222) -- R, G, B, sync-aligned.
        "scottie-s1", "scottie-s2", "scottie-dx",
        // Martin RX: `default:` branch, MRT special case (Main.cpp:4455-4490) -- scan 1 G, 2 B, 3 R.
        "martin-m1", "martin-m2",
        "sc2-180", "sc2-120", "sc2-60",
        "p3", "p5", "p7",
        "avt",
        // Narrow RGB.
        "mc110", "mc140", "mc180",
    };

    public static TheoryData<string> YCbCrFamilyModes() => new()
    {
        // Legacy RX branch `smR24/R72/MR*/ML*` (Main.cpp:4315) -- one branch for eleven modes that
        // TX splits across three functions.
        "r24", "robot-72", "mr73", "mr90", "mr115", "mr140", "mr175",
        "ml180", "ml240", "ml280", "ml320",
        // Legacy RX branch `smPD*/MP*/MN*` (Main.cpp:4367) -- one branch for fourteen modes that TX
        // splits across three functions.
        "pd50", "pd90", "pd120", "pd160", "pd180", "pd240", "pd290",
        "mp73", "mp115", "mp140", "mp175",
        "mn73", "mn110", "mn140",
    };

    [Theory]
    [MemberData(nameof(RgbFamilyModes))]
    public void RgbDecoder_MapsEachScanSegmentToItsOwnOutputComponent(string modeId)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var values = new Dictionary<string, byte> { ["R"] = HighValue, ["G"] = MidValue, ["B"] = LowValue };

        var pixel = DecodeOneLineWithPerChannelValues(mode, values);

        // Each component must carry ITS OWN segment's value. A swapped decoder returns the same three
        // numbers in the wrong slots, which is why all three are distinct.
        AssertNear(HighValue, pixel.R, modeId, "R");
        AssertNear(MidValue, pixel.G, modeId, "G");
        AssertNear(LowValue, pixel.B, modeId, "B");
    }

    [Theory]
    [MemberData(nameof(YCbCrFamilyModes))]
    public void YCbCrDecoder_DoesNotSwapTheTwoChromaChannels(string modeId)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        // R-Y high with B-Y low must yield red above blue. This is the assertion that survives the
        // colour maths: the exact RGB values depend on YCbCr reconstruction, but the ORDER of the two
        // chroma channels is a pure mapping question, and swapping them inverts the relationship.
        var redward = DecodeOneLineWithPerChannelValues(
            mode,
            new Dictionary<string, byte> { ["Y"] = MidValue, ["Y1"] = MidValue, ["Y2"] = MidValue, ["C"] = MidValue, ["RY"] = HighValue, ["BY"] = LowValue });

        Assert.True(
            redward.R > redward.B,
            $"[{modeId}] R-Y high and B-Y low should decode redder than bluer, got R={redward.R} B={redward.B}. " +
            "The two chroma channels look swapped -- legacy's RX branch reads R-Y before B-Y.");

        var blueward = DecodeOneLineWithPerChannelValues(
            mode,
            new Dictionary<string, byte> { ["Y"] = MidValue, ["Y1"] = MidValue, ["Y2"] = MidValue, ["C"] = MidValue, ["RY"] = LowValue, ["BY"] = HighValue });

        Assert.True(
            blueward.B > blueward.R,
            $"[{modeId}] R-Y low and B-Y high should decode bluer than redder, got R={blueward.R} B={blueward.B}.");
    }

    /// <summary>
    /// Robot 36 is the only mode whose chroma IDENTITY is carried in the signal rather than fixed by
    /// the layout: a 1500 Hz tone before the chroma scan means R-Y, 2300 Hz means B-Y
    /// (`SstvModeRegistry.cs`'s `ToneSelectorSegment`, legacy `m_DSEL` at `Main.cpp:4289-4296`).
    ///
    /// <para>That makes it the one place where a decoder could look correct while ignoring the
    /// signal entirely — deciding from line parity instead, which agrees with our own encoder on
    /// every line and disagrees with any transmission that starts on the other phase, or any decode
    /// that joins mid-picture. This test drives the SAME line index with both tones, so parity is
    /// held constant and only the tone changes.</para>
    ///
    /// <para><b>Not covered here:</b> legacy also has an ambiguity fallback. When the selector tone
    /// is too weak to call (|d| &lt; 64) it TOGGLES the previous selection (`Main.cpp:4293-4295`),
    /// which is parity-like. The port implements it (`RobotScanlineDecoder.cs`). Both tones below
    /// are decisive (|d| = 128), so this test never reaches that path.</para>
    /// </summary>
    [Theory]
    [InlineData(1500.0, true)]
    [InlineData(2300.0, false)]
    public void Robot36Decoder_TakesChromaIdentityFromTheToneSelector_NotLineParity(
        double toneFrequencyHz, bool expectRedward)
    {
        var mode = SstvModeRegistry.Robot36;
        var values = new Dictionary<string, byte> { ["Y"] = MidValue, ["C"] = HighValue };

        var pixel = DecodeOneLineWithPerChannelValues(mode, values, toneSelectorOverrideHz: toneFrequencyHz);

        // A high chroma value read as R-Y pushes red up; read as B-Y it pushes blue up. Line index is
        // 0 in both runs, so a parity-driven decoder would return the same answer twice.
        if (expectRedward)
        {
            Assert.True(
                pixel.R > pixel.B,
                $"A 1500 Hz selector means R-Y, so a high chroma value should decode redder. Got R={pixel.R} B={pixel.B}.");
        }
        else
        {
            Assert.True(
                pixel.B > pixel.R,
                $"A 2300 Hz selector means B-Y, so a high chroma value should decode bluer. Got R={pixel.R} B={pixel.B}. " +
                "Identical results for both tones mean the decoder is using line parity instead of reading the signal.");
        }
    }

    [Fact]
    public void EveryRegisteredMode_IsCoveredByExactlyOneFamilyTable()
    {
        // Same completeness guarantee as the TX table: a mode added to the registry without a row
        // here would otherwise escape RX coverage entirely and nothing would say so.
        var rgb = RgbFamilyModes().Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
        var ycbcr = YCbCrFamilyModes().Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);

        var overlap = rgb.Intersect(ycbcr, StringComparer.Ordinal).ToArray();
        Assert.True(overlap.Length == 0, $"Modes listed in both family tables: {string.Join(", ", overlap)}.");

        // Two deliberate exclusions, named rather than silently missing. rm8/rm12 are monochrome --
        // one Y scan, no channel mapping to get wrong. robot-36 carries its chroma identity in a tone
        // rather than in the layout, so it has its own test above instead of a family row.
        var coveredElsewhere = new[] { "rm8", "rm12", "robot-36" };
        var expected = SstvModeRegistry.All.Select(m => m.Id).Except(coveredElsewhere, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        var actual = rgb.Concat(ycbcr).OrderBy(id => id, StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    public static TheoryData<string> LinePairedLumaModes()
    {
        var data = new TheoryData<string>();
        foreach (var mode in SstvModeRegistry.All
            .Where(m => m.LineSegments.OfType<ScanSegment>().Any(s => s.ChannelName == "Y2"))
            .OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            data.Add(mode.Id);
        }

        return data;
    }

    [Fact]
    public void LinePairedLumaModes_CoversLegacysFourteenPdMpMnModes()
    {
        // Legacy's own RX branch carries exactly fourteen case labels (Main.cpp:4367-4380): seven PD,
        // four MP, three MN. A registry change that drops one must fail here, not silently shrink the
        // theory below to a table that still passes.
        Assert.Equal(14, LinePairedLumaModes().Count());
    }

    [Theory]
    [MemberData(nameof(LinePairedLumaModes))]
    public void LinePairedDecoder_SendsTheFirstLumaScanToTheFirstRow(string modeId)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        // PD/MP/MN send one chroma pair and TWO luma scans per transmitted line, filling two image
        // rows. Legacy writes the first luma to the upper row and the fourth segment to the lower one
        // (Main.cpp:4409-4429, `gp` then `gp2`). Swapping those two destinations flips the picture
        // vertically in pairs -- a visible defect that the mapping rows above cannot see, because
        // they drive both luma scans with the same value and read only the upper row.
        var pixels = DecodeOneLineToPixels(
            mode,
            new Dictionary<string, byte> { ["Y"] = MidValue, ["Y1"] = HighValue, ["Y2"] = LowValue, ["C"] = MidValue, ["RY"] = MidValue, ["BY"] = MidValue });

        var upper = pixels[mode.ImageWidth / 2];
        var lower = pixels[mode.ImageWidth + (mode.ImageWidth / 2)];

        // Chroma is held neutral, so brightness alone separates the two rows. The gap is about 480
        // summed across the components; 40 is a floor well clear of reconstruction rounding.
        Assert.True(
            Brightness(upper) > Brightness(lower) + 40,
            $"[{modeId}] the bright first luma scan should land in the upper row and the dark second " +
            $"scan in the lower one, got upper={Brightness(upper)} lower={Brightness(lower)}. " +
            "The decoder looks like it sends Y1 and Y2 to the wrong rows.");
    }

    private static int Brightness(Rgb24 pixel) => pixel.R + pixel.G + pixel.B;

    /// <summary>
    /// Builds a frequency trace directly from the mode's own scan segments — each segment filled with
    /// the value its channel name maps to — then runs ONLY the decoder over it.
    /// </summary>
    private static Rgb24 DecodeOneLineWithPerChannelValues(
        SstvModeDefinition mode,
        IReadOnlyDictionary<string, byte> valuesByChannel,
        double? toneSelectorOverrideHz = null)
    {
        // Mid-line, so neither edge transient nor the first-pixel settling affects the reading.
        return DecodeOneLineToPixels(mode, valuesByChannel, toneSelectorOverrideHz)[mode.ImageWidth / 2];
    }

    private static Rgb24[] DecodeOneLineToPixels(
        SstvModeDefinition mode,
        IReadOnlyDictionary<string, byte> valuesByChannel,
        double? toneSelectorOverrideHz = null)
    {
        var decoder = ScanlineCodecFactory.CreateDecoder(mode.ColorEncoding);
        var samplesPerLine = mode.LineDurationMs * SampleRate / 1000.0;

        // Cumulative segment boundaries, so a sample index resolves to the segment covering it.
        var boundaries = new List<(double EndMs, double FrequencyHz)>();
        var cumulative = 0.0;
        foreach (var segment in mode.LineSegments)
        {
            cumulative += segment.DurationMs;
            var frequency = segment switch
            {
                ScanSegment scan => ValueToFrequency(valuesByChannel[scan.ChannelName], mode),
                SyncSegment sync => sync.FrequencyHz,
                // Robot 36's chroma selector. Defaults to the LOW tone (R-Y) so every other mode's
                // trace is deterministic; the Robot 36 test drives both tones explicitly.
                ToneSelectorSegment tone => toneSelectorOverrideHz ?? tone.LowFrequencyHz,
                _ => mode.LuminanceMinHz,
            };
            boundaries.Add((cumulative, frequency));
        }

        double FrequencyAt(int sampleIndex)
        {
            var offsetMs = sampleIndex / (double)SampleRate * 1000.0;
            foreach (var boundary in boundaries)
            {
                if (offsetMs < boundary.EndMs)
                {
                    return boundary.FrequencyHz;
                }
            }

            return boundaries[^1].FrequencyHz;
        }

        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        var reader = new PixelSampleReader(
            FrequencyAt,
            SstvModeRegistry.GetKsbSamples(mode, SampleRate),
            (int)Math.Round(samplesPerLine),
            mode.LuminanceMinHz,
            SstvModeRegistry.NeverPeakPicks(mode));

        decoder.DecodeLine(mode, SampleRate, 0, 0, reader, pixels);

        return pixels;
    }

    private static double ValueToFrequency(byte value, SstvModeDefinition mode) =>
        mode.LuminanceMinHz + (value * (mode.LuminanceMaxHz - mode.LuminanceMinHz) / 256.0);

    private static void AssertNear(byte expected, byte actual, string modeId, string component)
    {
        // Tolerance covers the port's own truncation in the frequency-to-value conversion
        // (docs/known-decode-defects.md §1 Fault A), not a channel error -- a swap moves a value by
        // 80 or more here, far outside this band.
        Assert.True(
            Math.Abs(expected - actual) <= 6,
            $"[{modeId}] the {component} component decoded as {actual}, expected about {expected}. " +
            "A value from a different channel landing here means the decoder's segment-to-component " +
            "mapping disagrees with legacy's RX branch.");
    }
}
