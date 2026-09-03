using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Core.Cw.Tests;

/// <summary>Round-trip tests: real audio generated via <see cref="CwTestAudio"/> (itself a thin
/// renderer over the already-verified <c>CwMorseGenerator</c>), decoded via the real
/// <see cref="ClassicalCwDecoder"/>. Necessary but NOT sufficient on its own (CLAUDE.md §4) --
/// <see cref="MorseAlphabetTests"/> and <see cref="CwIdCallsignExtractorTests"/> each isolate-test
/// their own piece independently first, so a bug here is a DSP timing/envelope bug specifically, not
/// confusable with a lookup-table or text-parsing bug.</summary>
public class ClassicalCwDecoderTests
{
    private const double BaselineToneHz = 1000;
    private const double BaselineWpm = 28;
    private const int BaselineSampleRate = 11025;

    [Fact]
    public async Task DecodeAsync_RealCwId_DecodesTheExactText()
    {
        var samples = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, BaselineWpm, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);
        Assert.True(result.Confidence > 0.5, $"Expected high confidence on clean audio, got {result.Confidence}");
        Assert.NotNull(result.ToneHz);
        AssertClose(BaselineToneHz, result.ToneHz!.Value, 20);
        Assert.NotNull(result.Wpm);
        AssertClose(BaselineWpm, result.Wpm!.Value, BaselineWpm * 0.1);

        // Auditor round 2 finding R2: without this, the per-character confidence rewrite (round 1's
        // 3 blockers) has NO test protection -- reverting it to a flat 1.0 leaves every other test
        // green, since the FSK-collision test now short-circuits in SeedDotEstimate before ever
        // reaching ClassifyAndDecode. "DE W1AW" has 6 real characters (the space is literal, not a
        // flushed character), all clean, so each should score well above the confidence floor.
        Assert.Equal(6, result.Characters.Count);
        Assert.All(result.Characters, c => Assert.True(c.Confidence > 0.7,
            $"Expected character '{c.Character}' to score above 0.7 on clean audio, got {c.Confidence}"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(28)]
    [InlineData(35)]
    [InlineData(50)]
    public async Task DecodeAsync_AcrossTheFullWpmRange_DecodesCorrectlyAndEstimatesWpm(double wpm)
    {
        // 5-50 WPM is step 4's own stated search range (fsk_cwid.md §8.4) -- 5 and 50 are the
        // endpoints, deliberately included (an earlier draft's fixture list never touched either
        // endpoint, which is exactly why a too-narrow dot-range bug went undetected at plan-review).
        var samples = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, wpm, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);
        Assert.NotNull(result.Wpm);
        AssertClose(wpm, result.Wpm!.Value, Math.Max(1.0, wpm * 0.1));
    }

    [Theory]
    [InlineData(200)]  // outside DeepCW's own 400-1200 Hz band, inside the real 100-3000 Hz range.
    [InlineData(600)]
    [InlineData(800)]
    [InlineData(1000)]
    [InlineData(1200)]
    [InlineData(2000)]
    [InlineData(2800)]
    public async Task DecodeAsync_AcrossTheFullToneRange_DecodesCorrectlyAndEstimatesTone(double toneHz)
    {
        var samples = CwTestAudio.GenerateCwId("DE W1AW", toneHz, BaselineWpm, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);
        Assert.NotNull(result.ToneHz);
        AssertClose(toneHz, result.ToneHz!.Value, 20);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task DecodeAsync_AcrossRealSampleRates_DecodesCorrectly(int sampleRate)
    {
        var samples = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, BaselineWpm, sampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, sampleRate);

        Assert.Equal("DE W1AW", result.Text);
    }

    [Fact]
    public async Task DecodeAsync_ShortIdPaddedWithSilenceInA12SecondWindow_StillDecodes()
    {
        // fsk_cwid.md §8.4 step 3's own called-out case: a fixed 10th/90th-percentile threshold
        // would collapse toward the noise floor here, since mark time is a small fraction of the
        // whole window -- the Otsu-on-active-region approach must not have the same failure.
        var id = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, BaselineWpm, BaselineSampleRate);
        var leadingSilence = CwTestAudio.Silence(2000, BaselineSampleRate);
        var trailingSilence = CwTestAudio.Silence(9000, BaselineSampleRate);
        var windowed = CwTestAudio.Concat(leadingSilence, id, trailingSilence);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(windowed, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);
    }

    [Fact]
    public async Task DecodeAsync_ShortIdInA20SecondWindow_StillDecodes()
    {
        var id = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, BaselineWpm, BaselineSampleRate);
        var leadingSilence = CwTestAudio.Silence(2000, BaselineSampleRate);
        var trailingSilence = CwTestAudio.Silence(17000, BaselineSampleRate);
        var windowed = CwTestAudio.Concat(leadingSilence, id, trailingSilence);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(windowed, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);
    }

    [Fact]
    public async Task DecodeAsync_SilenceOnly_ReportsNoTone()
    {
        var samples = CwTestAudio.Silence(5000, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, BaselineSampleRate);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Confidence);
        Assert.Null(result.ToneHz);
        Assert.Null(result.Wpm);
    }

    [Fact]
    public async Task DecodeAsync_NoiseOnlyWithNonZeroFloor_ReportsNoTone()
    {
        // Auditor finding: the only prior "no CW" case used exact digital zero, which short-circuits
        // at DetectKeying's `peak &lt;= 0` check before any gate/threshold logic runs at all. A real
        // production window is mostly noise with no CW content and a genuinely non-zero floor -- this
        // is the property that actually matters, and the exact-zero case can't exercise it.
        var noise = CwTestAudio.AddNoise(CwTestAudio.Silence(12000, BaselineSampleRate), snrDb: 0, seed: 99);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(noise, BaselineSampleRate);

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task DecodeAsync_FskIdShapedCompetingSignal_IsRejectedNotMisreadAsCw()
    {
        // Auditor finding: plan-review round 4's F1 fix (score every tone candidate through the full
        // pipeline, don't gate on the first structural pass) was implemented but never exercised
        // against the actual collision case it defends -- a real FSK-ID packet's own mark/space
        // shift-keyed tone, which superficially looks like on/off CW keying on either of its two
        // component frequencies. Built with the SAME production encoder real FSK-ID transmissions use
        // (this test project already has InternalsVisibleTo into Core.Sstv), not a hand-rolled fixture.
        var fskSamples = CwTestAudio.Render(FskStationIdEncoder.Generate("W1AW", null), BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(fskSamples, BaselineSampleRate);

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void SeedDotEstimate_FskCollisionMarkDurations_RejectsAsNonCw()
    {
        // Auditor round 2 nit N3: the round-trip test above proves the FULL PIPELINE rejects the FSK
        // packet, but a future change could make it fail for an unrelated reason and stay green. This
        // pins the actual mechanism -- the exact mark-duration list measured by hand while diagnosing
        // the original "3IM E" false positive (19.95ms x5, 39.91ms, 64.85ms x2, 84.81ms x2), which
        // clears the pre-existing gap-occupancy check but is caught by the new per-cluster outlier
        // check (the 39.91ms mark sits 71% from its own assigned cluster's centre).
        var marks = new List<double> { 19.95, 19.95, 19.95, 84.81, 64.85, 19.95, 19.95, 84.81, 64.85, 39.91 };

        var seed = ClassicalCwDecoder.SeedDotEstimate(marks);

        Assert.Null(seed);
    }

    [Fact]
    public void ClassifyAndDecode_UnrecognizedPattern_ReportsUnknownWithPenalizedConfidence()
    {
        // Auditor round 2 finding R2: without this, the unknown-character penalty (round 1's blocker
        // #2) has no direct test -- six clean, well-timed dits ("......") isn't a real MorseAlphabet
        // entry (the table tops out at 5 elements), so a perfect timing fit must still be penalized,
        // not scored ~1.0 just because the elements themselves were well-formed.
        const double dot = 40.0;
        var intervals = new List<(bool IsMark, double DurationMs)>();
        for (var i = 0; i < 6; i++)
        {
            if (i > 0)
            {
                intervals.Add((false, dot)); // intra-element gap.
            }

            intervals.Add((true, dot)); // a clean dit.
        }

        var result = ClassicalCwDecoder.ClassifyAndDecode(intervals, dot, toneHz: 1000);

        var character = Assert.Single(result.Characters);
        Assert.True(character.IsUnknown);
        Assert.True(character.Confidence <= 0.1,
            $"Expected the unknown-character penalty to cap confidence at <= 0.1, got {character.Confidence}");
    }

    [Fact]
    public async Task DecodeAsync_EmptyBuffer_ReportsNoToneWithoutThrowing()
    {
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(ReadOnlyMemory<float>.Empty, BaselineSampleRate);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Confidence);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(5)]
    [InlineData(0)]
    public async Task DecodeAsync_ModerateNoise_StillDecodesTheCorrectText(double snrDb)
    {
        var clean = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, BaselineWpm, BaselineSampleRate);
        var noisy = CwTestAudio.AddNoise(clean, snrDb, seed: 42);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(noisy, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);

        // Auditor round 2 finding R2: pin the confidence FLOOR's own margin, not just the text -- all
        // three SNR levels here measure exactly 1.0 (block-quantization already absorbs this much
        // noise), comfortably above MinAcceptableConfidence (0.4); a regression that erodes that
        // margin should fail here before it ever gets close to the floor.
        Assert.True(result.Confidence > 0.7, $"Expected confidence comfortably above the 0.4 floor at {snrDb} dB, got {result.Confidence}");
    }

    [Fact]
    public async Task DecodeAsync_SevereNoise_DoesNotThrow_AndReturnsEitherCorrectTextOrNoTone()
    {
        // -3 dB is a deliberately harsh case -- not asserted to decode correctly (that would be an
        // unrealistic guarantee for this v1 classical decoder), only that it degrades safely
        // (fsk_cwid.md §8.4's own stated known-weakness list) rather than throwing or hanging.
        var clean = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, BaselineWpm, BaselineSampleRate);
        var noisy = CwTestAudio.AddNoise(clean, snrDb: -3, seed: 7);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(noisy, BaselineSampleRate);

        Assert.True(result.Text == "DE W1AW" || result.Text == string.Empty,
            $"Expected either the correct decode or a safe 'no tone', got '{result.Text}'.");
    }

    [Fact]
    public async Task DecodeAsync_ToneOffsetWithinGoertzelBinWidth_StillDecodes()
    {
        // A real operator's tone isn't guaranteed to land exactly on a swept 10 Hz step -- a small,
        // realistic offset must not break the tone-find step.
        var samples = CwTestAudio.GenerateCwId("DE W1AW", 1003.7, BaselineWpm, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, BaselineSampleRate);

        Assert.Equal("DE W1AW", result.Text);
    }

    [Fact]
    public async Task DecodeAsync_CallsignWithPortableSuffix_DecodesTheSlashCorrectly()
    {
        var samples = CwTestAudio.GenerateCwId("DE W1AW/M", BaselineToneHz, BaselineWpm, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var result = await decoder.DecodeAsync(samples, BaselineSampleRate);

        Assert.Equal("DE W1AW/M", result.Text);
    }

    [Fact]
    public async Task DecodeAsync_TwoDifferentSpeedsProduceDifferentEstimatedWpm()
    {
        // The primary evidence adaptive-WPM decoding actually works, not just that a hardcoded
        // constant happens to match one fixture -- fsk_cwid.md §10's own stated bar for this.
        var slow = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, 10, BaselineSampleRate);
        var fast = CwTestAudio.GenerateCwId("DE W1AW", BaselineToneHz, 35, BaselineSampleRate);
        var decoder = new ClassicalCwDecoder();

        var slowResult = await decoder.DecodeAsync(slow, BaselineSampleRate);
        var fastResult = await decoder.DecodeAsync(fast, BaselineSampleRate);

        Assert.Equal("DE W1AW", slowResult.Text);
        Assert.Equal("DE W1AW", fastResult.Text);
        Assert.NotNull(slowResult.Wpm);
        Assert.NotNull(fastResult.Wpm);
        Assert.True(fastResult.Wpm!.Value > slowResult.Wpm!.Value * 2,
            $"Expected the fast fixture's estimated WPM ({fastResult.Wpm}) to be meaningfully " +
            $"higher than the slow fixture's ({slowResult.Wpm}), not a fixed constant either way.");
    }

    private static void AssertClose(double expected, double actual, double tolerance)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} within {tolerance}, got {actual}");
}
