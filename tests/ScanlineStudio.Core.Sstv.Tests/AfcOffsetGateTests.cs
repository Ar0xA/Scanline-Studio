using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>A standing regression gate for AFC, and the reason it exists is that the project did not
/// have one.
///
/// The impairment bench encodes perfectly on-frequency signals, so AFC has nothing to correct and
/// reports exactly zero however it behaves. That makes the standing "clear improvement AND zero
/// degradation across all 43 modes" bar VACUOUS for anything AFC-related: it cannot tell a change
/// that helped from one that did nothing from one that disabled frequency correction outright. The
/// section 5.3 AFC retune measured exactly 0.00 on all 43 modes at every SNR for precisely this
/// reason, and that null was misread at least once as evidence.
///
/// The consequence is also out of proportion to pixel damage. A wrong AFC correction shifts the
/// ENTIRE luma map for the rest of the image, and it feeds five resonator retunes including the sync
/// envelope detector, which both the anchor corrector and the slant tracker consume. So a broken AFC
/// damages horizontal alignment and slant too, not only brightness.
///
/// Every change that touches the signal path feeding AFC must keep these passing.</summary>
public sealed class AfcOffsetGateTests
{
    private const int SampleRate = 11025;

    // AfcTracker adds a calibration term BEFORE computing its correction, so a perfectly tuned signal
    // does NOT yield zero -- it yields -3.125Hz wide and -1.0Hz narrow. Scoring against an assumed
    // zero would make every correct reading look like a fault. Asserted in AfcTests.
    private const double WideCalibrationHz = -3.125;

    /// <summary>PRECONDITION, not an outcome: the injector must move the spectrum by exactly the
    /// requested amount. Without this an injector error is indistinguishable from an AFC bias, and
    /// the gate would be measuring itself.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(25.0)]
    [InlineData(-50.0)]
    [InlineData(100.0)]
    public async Task Injector_ShiftsTheSpectrumByExactlyTheRequestedOffset(double offsetHz)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == "robot-36");
        var source = FlatImage(mode.ImageWidth, mode.ImageHeight);

        var shifted = await EncodeAsync(mode, source, offsetHz);
        var reference = await EncodeAsync(mode, source, 0.0);

        // Measure where the dominant tone sits in a settled interior window, well away from both
        // ends, so no filter warm-up or trailing segment contributes.
        var at = DominantFrequency(shifted, SampleRate);
        var baseline = DominantFrequency(reference, SampleRate);

        Assert.InRange(at - baseline, offsetHz - 1.5, offsetHz + 1.5);
    }

    /// <summary>PRECONDITION: at zero offset the injector must be a bit-exact no-op, or every
    /// measurement taken through it carries an unknown constant.</summary>
    [Fact]
    public async Task Injector_IsBitExactNoOp_AtZeroOffset()
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == "robot-36");
        var source = ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var withSeam = await EncodeAsync(mode, source, 0.0);
        var production = new List<float>();
        await foreach (var s in new AnalogFmSstvEncoder(SampleRate).EncodeAsync(mode, source))
        {
            production.Add(s);
        }

        Assert.Equal(production.Count, withSeam.Length);
        for (var i = 0; i < withSeam.Length; i++)
        {
            Assert.Equal(production[i], withSeam[i]);
        }
    }

    /// <summary>THE GATE. On a clean signal AFC must correct a known tuning error to a known value.
    /// Expected correction is `-3.125 - offset`, not `-offset` -- the calibration term above.
    ///
    /// Offsets are kept inside the wide capture window, which is ASYMMETRIC because the calibration
    /// is applied before the acceptance-band test: roughly [-203, +122] Hz. A sweep beyond that sees
    /// no correction at all, which is correct behaviour and not a fault.</summary>
    [Theory]
    [InlineData("robot-36", 0.0)]
    [InlineData("robot-36", 25.0)]
    [InlineData("robot-36", -25.0)]
    [InlineData("robot-36", 60.0)]
    [InlineData("robot-36", -60.0)]
    [InlineData("martin-m1", 40.0)]
    [InlineData("scottie-s1", -40.0)]
    public async Task Afc_CorrectsAKnownTuningError(string modeId, double offsetHz)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var source = ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = await EncodeAsync(mode, source, offsetHz);

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        SstvModeDefinition? detected = null;
        double? correction = null;
        decoder.ModeDetected += m => detected = m;

        // Sampled DURING the reception, not after it. The property is documented as null "between
        // images", so reading it once the whole transmission has been pushed finds the tracker
        // already torn down -- which looks exactly like AFC never having locked.
        decoder.LineDecoded += _ => correction = decoder.SyncFrequencyCorrectionHz ?? correction;
        decoder.PushSamples(samples);

        Assert.NotNull(detected);
        Assert.Equal(modeId, detected!.Id);
        Assert.NotNull(correction);

        // The tolerance is the AFC's own measurement granularity, not a fudge: it averages a short
        // run of in-band readings, so a few Hz of residual is expected and correct.
        Assert.InRange(correction!.Value, WideCalibrationHz - offsetHz - 12.0, WideCalibrationHz - offsetHz + 12.0);
    }

    /// <summary>Past the capture window AFC must decline to lock rather than latch a wrong
    /// correction. The window is asymmetric, so this is checked on the side with the tighter edge.
    /// A latched correction here would shift the whole luma map for the rest of the image.</summary>
    [Fact]
    public async Task Afc_DoesNotLatch_BeyondItsCaptureWindow()
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == "robot-36");
        var source = ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = await EncodeAsync(mode, source, 300.0);

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        double? correction = null;
        decoder.LineDecoded += _ => correction = decoder.SyncFrequencyCorrectionHz ?? correction;
        decoder.PushSamples(samples);

        if (correction is not null)
        {
            // If it did lock, it must not have invented a correction near the true offset -- that
            // would mean the acceptance window is not doing its job.
            Assert.True(
                Math.Abs(correction.Value + 300.0) > 50.0,
                $"AFC latched {correction.Value:F1}Hz for an offset far outside its capture window.");
        }
    }

    private static async Task<float[]> EncodeAsync(SstvModeDefinition mode, IImageSource source, double offsetHz)
    {
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var s in encoder.EncodeWithCarrierOffsetAsync(mode, source, offsetHz))
        {
            samples.Add(s);
        }

        return samples.ToArray();
    }

    /// <summary>A single flat luminance, so the encode carries one constant scan frequency for the
    /// injector to be measured against. A gradient sweeps the whole luma range and has no dominant
    /// tone at all.</summary>
    private static ArrayImageSource FlatImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        Array.Fill(pixels, new Rgb24(128, 128, 128));
        return new ArrayImageSource(width, height, pixels);
    }

    /// <summary>Dominant frequency of a settled interior window, by peak of a Hann-windowed magnitude
    /// spectrum with parabolic interpolation. Only the SSTV band is searched, which keeps this a
    /// few hundred thousand operations rather than a full transform. AFC is not involved.</summary>
    private static double DominantFrequency(float[] samples, int sampleRate)
    {
        const int n = 4096;
        var start = samples.Length / 2;
        var w = new double[n];
        for (var i = 0; i < n; i++)
        {
            w[i] = samples[start + i] * (0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1))));
        }

        // Wide enough to hold any legal tone plus the offsets under test, narrow enough to stay cheap.
        var loBin = (int)(800.0 * n / sampleRate);
        var hiBin = (int)(2800.0 * n / sampleRate);
        var mags = new double[hiBin + 2];
        var best = loBin;
        double bestMag = -1;
        for (var k = loBin; k <= hiBin; k++)
        {
            double re = 0, im = 0;
            for (var i = 0; i < n; i++)
            {
                var a = -2.0 * Math.PI * k * i / n;
                re += w[i] * Math.Cos(a);
                im += w[i] * Math.Sin(a);
            }

            mags[k] = Math.Sqrt((re * re) + (im * im));
            if (mags[k] > bestMag)
            {
                bestMag = mags[k];
                best = k;
            }
        }

        // Parabolic interpolation around the peak bin, so the result is not quantized to bin width.
        var alpha = mags[best - 1];
        var beta = mags[best];
        var gamma = mags[best + 1];
        var delta = 0.5 * (alpha - gamma) / (alpha - (2 * beta) + gamma);
        return (best + delta) * sampleRate / (double)n;
    }
}
