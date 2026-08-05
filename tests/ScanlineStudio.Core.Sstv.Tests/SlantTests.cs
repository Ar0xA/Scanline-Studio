using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted, staged tests for the Auto Slant port (<see cref="TankFilter"/>,
/// <see cref="SyncEnvelopeDetector"/>, <see cref="SlantTracker"/>, porting legacy's <c>CIIRTANK</c>,
/// the <c>d12</c>/<c>d19</c> envelope detector, and <c>AutoStopJob</c>'s slant-specific branch
/// respectively). Each class is checked independently before any of them are wired into
/// <see cref="AnalogFmSstvDecoder"/>, so a mistake in one layer doesn't hide inside a full-pipeline
/// pass/fail the way it would in only an end-to-end test.
/// </summary>
public class SlantTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void TankFilter_ResonatesMuchMoreAtTunedFrequencyThanFarAway()
    {
        var onFreq = SteadyStateAmplitude(1200, 1200);
        var offFreq = SteadyStateAmplitude(1200, 1900);

        // A resonant bandpass at 100Hz bandwidth should pass a tone 700Hz away with only a small
        // fraction of the gain it gives a tone dead-center.
        Assert.True(onFreq > offFreq * 5, $"on-freq={onFreq}, off-freq={offFreq}");
    }

    [Fact]
    public void SyncEnvelopeDetector_TunedTone_ProducesStrongerEnvelopeThanOffTuneTone()
    {
        var onFreq = SteadyStateEnvelope(1200, 1200);
        var offFreq = SteadyStateEnvelope(1200, 1900);

        Assert.True(onFreq > offFreq * 3, $"on-freq={onFreq}, off-freq={offFreq}");
    }

    [Fact]
    public void SlantTracker_NoDrift_NeverReportsACorrection()
    {
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        double? lastResult = null;
        for (var line = 0; line < 200; line++)
        {
            lastResult = tracker.ProcessLine(0.0) ?? lastResult;
        }

        Assert.Null(lastResult);
    }

    [Fact]
    public void SlantTracker_ConsistentDrift_LocksACorrectionCloseToTheTrueRate()
    {
        // Simulate a receiver whose true samples-per-line is 1% higher than assumed. This is a
        // CLOSED-LOOP simulation, not a fixed linear drift sequence: each line, the position fed to
        // ProcessLine is re-derived from (true cumulative samples so far) minus (this test's own
        // running total using whatever samples-per-line the tracker's *last* correction implied) --
        // mirroring exactly what AnalogFmSstvDecoder.ApplySlantTracking does with its mutable
        // _effectiveSamplesPerLine. A first version of this test fed a fixed per-line increment
        // instead, decoupled from the tracker's own corrections -- that was a fair model of the
        // pre-fix implementation (which never updated its internal nominal value either) but stopped
        // matching reality once SlantTracker was fixed to keep its internal rate/nominal pair in
        // sync the way legacy's SetSampFreq() does (see SlantTracker's own doc comment on that bug).
        //
        // SlantTracker's output is a corrected *sample rate* (matching legacy's SSTVSET.m_SampFreq),
        // not a corrected samples-per-line value (Main.cpp:3994-3997).
        const double nominalSamplesPerLine = SampleRate * 0.15; // 6615 samples/line at 44100Hz
        const double trueSamplesPerLine = nominalSamplesPerLine * 1.01;
        var expectedCorrectedRate = SampleRate * (trueSamplesPerLine / nominalSamplesPerLine);

        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        double? lastResult = null;
        var trueCumulative = 0.0;
        var assumedCumulative = 0.0;
        var assumedSamplesPerLine = nominalSamplesPerLine;
        for (var line = 0; line < 300; line++)
        {
            trueCumulative += trueSamplesPerLine;
            assumedCumulative += assumedSamplesPerLine;

            var result = tracker.ProcessLine(trueCumulative - assumedCumulative);
            if (result is not null)
            {
                lastResult = result;
                assumedSamplesPerLine = nominalSamplesPerLine / SampleRate * result.Value;
            }
        }

        Assert.NotNull(lastResult);
        Assert.Equal(expectedCorrectedRate, lastResult!.Value, tolerance: expectedCorrectedRate * 0.002);
    }

    [Theory]
    [InlineData("robot-36", 64, 128, 160, 240 - 36)] // default group, Robot36.ImageHeight=240
    [InlineData("mn73", 48, 64, 72, 110)] // override group A
    [InlineData("pd160", 48, 80, 126, 160)]
    [InlineData("pd290", 64, 128, 160, 240)]
    [InlineData("p3", 64, 200, 360, 448)]
    [InlineData("p5", 64, 200, 300, 380)]
    [InlineData("p7", 64, 128, 220, 280)]
    public void GetAutoSlantThresholdPositions_MatchesLegacyPerModeGrouping(string modeId, int p0, int p1, int p2, int p3)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        Assert.Equal(new[] { p0, p1, p2, p3 }, SstvModeRegistry.GetAutoSlantThresholdPositions(mode));
    }

    [Fact]
    public void GetSyncSegmentOffsetMs_Robot36_SyncIsAtLineStart()
    {
        Assert.Equal(0.0, SstvModeRegistry.GetSyncSegmentOffsetMs(SstvModeRegistry.Robot36));
    }

    [Fact]
    public void GetSyncSegmentOffsetMs_ScottieS1_SyncIsMidLineAfterGAndB()
    {
        // LineSCT: separator-G-separator-B-SYNC-separator-R. Sync starts after 1.5(sep)+138.24(G)+1.5(sep)+138.24(B).
        var mode = SstvModeRegistry.ScottieS1;
        Assert.Equal(1.5 + 138.24 + 1.5 + 138.24, SstvModeRegistry.GetSyncSegmentOffsetMs(mode), tolerance: 0.001);
    }

    [Fact]
    public void GetSyncSegmentOffsetMs_Mn73_NarrowSyncIsAtLineStart()
    {
        Assert.Equal(0.0, SstvModeRegistry.GetSyncSegmentOffsetMs(SstvModeRegistry.Mn73));
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_RealisticClockMismatch_DecodesWithinTolerance()
    {
        // Simulate a real clock mismatch: encode as if the true sample rate is 0.05% (500ppm) higher
        // than what the decoder is told -- already a generous/pessimistic real crystal-oscillator
        // tolerance (typical sound card clocks are within tens of ppm) -- exactly what Auto Slant
        // exists to detect and correct for (unlike AFC, which corrects a *frequency* offset, this is
        // a *timing/rate* offset -- see SlantTracker's doc comment). Without slant correction, every
        // line's pixel windows would progressively misalign against the encoder's, growing worse
        // line by line.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detectedMode);
        Assert.NotNull(decodedImage);

        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);
        Assert.True(averageDelta <= 10.0, $"Average per-channel delta {averageDelta:F2} exceeded tolerance 10 -- slant correction should have kept this within normal round-trip tolerance despite the 500ppm rate mismatch.");
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_SevereClockMismatch_ConvergesButDoesNotFullyCorrect()
    {
        // A 1% mismatch is a large, atypical clock error for real hardware (500-2000x a normal
        // crystal's tolerance) -- included not as a realistic scenario but to document a real,
        // verified characteristic of legacy's own algorithm: AutoStopJob's m_ASBitMask permanently
        // disables each of its 5 confidence tiers once used (Main.cpp:4006-4010), so for a single
        // long transmission it converges quickly early on and then locks -- it does NOT keep
        // re-adjusting for the rest of the image, even if a full correction hasn't been reached.
        // For a small (realistic) mismatch that's irrelevant (it converges before the residual
        // matters); for a mismatch this large over a 240-line image, the residual compounds to a
        // visible decode error by the end -- correctly reproducing legacy's own limitation, not a
        // bug in the port. This test only checks that correction is clearly happening (average delta
        // far below what zero correction would produce), not that it's perfect.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        IImageSource? decodedImage = null;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(decodedImage);
        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);

        // Uncorrected, a 1% mismatch over Robot36's 240 lines accumulates to roughly a full line's
        // width of misalignment by the end (see SlantTracker's doc comment's residual-drift math) --
        // an uncorrected decode of this scenario averages well over 100. Convergence should get this
        // well under half that, even though legacy's own design means it won't reach the normal <=10
        // round-trip tolerance for a mismatch this severe.
        Assert.True(averageDelta < 60.0, $"Average per-channel delta {averageDelta:F2} -- expected meaningfully better than an uncorrected decode (~100+) even though full correction isn't expected for a mismatch this severe.");
    }

    private static double ComputeAveragePerChannelDelta(IImageSource expected, IImageSource actual)
    {
        double totalDelta = 0;
        var sampleCount = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            for (var x = 0; x < expected.Width; x++)
            {
                totalDelta += Math.Abs(expectedLine[x].R - actualLine[x].R);
                totalDelta += Math.Abs(expectedLine[x].G - actualLine[x].G);
                totalDelta += Math.Abs(expectedLine[x].B - actualLine[x].B);
                sampleCount += 3;
            }
        }

        return totalDelta / sampleCount;
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static double SteadyStateAmplitude(double resonantFrequencyHz, double inputFrequencyHz)
    {
        var filter = new TankFilter();
        filter.SetFreq(resonantFrequencyHz, SampleRate, 100.0);
        return DriveAndMeasurePeak(filter.Process, inputFrequencyHz);
    }

    private static double SteadyStateEnvelope(double resonantFrequencyHz, double inputFrequencyHz)
    {
        var detector = new SyncEnvelopeDetector(SampleRate, resonantFrequencyHz);
        return DriveAndMeasurePeak(detector.ProcessSample, inputFrequencyHz);
    }

    private static double DriveAndMeasurePeak(Func<double, double> process, double inputFrequencyHz)
    {
        var phaseIncrement = 2 * Math.PI * inputFrequencyHz / SampleRate;
        var phase = 0.0;
        var peak = 0.0;

        // Run long enough for the resonator/lowpass to reach steady state, then measure peak over
        // one more full cycle at the input frequency.
        var settleSamples = (int)(0.5 * SampleRate);
        var measureSamples = (int)(SampleRate / inputFrequencyHz) + 1;

        for (var i = 0; i < settleSamples; i++)
        {
            phase += phaseIncrement;
            process(Math.Sin(phase));
        }

        for (var i = 0; i < measureSamples; i++)
        {
            phase += phaseIncrement;
            var output = process(Math.Sin(phase));
            peak = Math.Max(peak, Math.Abs(output));
        }

        return peak;
    }
}
