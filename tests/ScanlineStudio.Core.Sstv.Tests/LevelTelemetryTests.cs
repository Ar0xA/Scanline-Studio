using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Tests for the decode-time signal telemetry properties added to <see cref="ISstvDecoder"/>
/// (`SignalPeakLevel`/`IsLevelOverdriven`/`SyncFrequencyCorrectionHz`/`BufferedSampleCount`) --
/// Tier A of spec/14-roadmap.md's "Decode-time signal telemetry" gap (the sub-slice exposing
/// already-computed internal state; SNR/noise-floor/notch/L-R-levels/squelch are explicitly out of
/// scope, see `~/.claude/plans/steady-humming-osprey.md`). Same auditor-driven discipline as
/// <see cref="SlantTests"/>'s own SlantPpm/SyncOffsetSamples tests: null-case coverage for the
/// AFC-backed property (same AbandonInProgressImage risk class SlantPpm needed fixing for), and
/// explicit "0 before first push, not 0 while idle" coverage for BufferedSampleCount (a round-1
/// plan-review finding that the naive claim was false).
/// </summary>
public class LevelTelemetryTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void AnalogFmSstvDecoder_SignalPeakLevel_ActuallyReads32768RescaledCurMax()
    {
        // Code-level audit finding: an earlier version of this test drove a standalone LevelAgc
        // instance and re-derived the /32768.0 math independently in the test itself, never once
        // reading the real SignalPeakLevel production property -- a wrong divisor baked into that
        // property (e.g. /32767.0) would have shipped green. Fixed to drive the DECODER's own
        // LevelAgcForTests instance directly (exact, known values -- not a real demodulated signal,
        // whose BPF gain isn't independently known), then assert against the actual production
        // property.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        FeedConstantAmplitudeUntilFixed(decoder.LevelAgcForTests, rawAmplitude: 16384.0); // exactly half of legacy's ~32768 domain

        Assert.Equal(0.5, decoder.SignalPeakLevel, tolerance: 0.0001);
    }

    [Theory]
    [InlineData(24578.0, true)] // exactly at threshold -- ">=", not ">"
    [InlineData(24577.999, false)]
    [InlineData(30000.0, true)]
    [InlineData(1000.0, false)]
    public void AnalogFmSstvDecoder_IsLevelOverdriven_ActuallyReadsThe24578ThresholdOnTheRealProperty(double rawAmplitude, bool expectedOverdriven)
    {
        // Same code-level-audit fix as the SignalPeakLevel test above: this must assert
        // decoder.IsLevelOverdriven (the real property, exercising its actual ">=" comparison),
        // not an independently-recomputed `agc.CurMax >= 24578.0` expression that could disagree
        // with the property's own implementation (e.g. a ">" typo) and still pass. Notably: before
        // this fix, NO test anywhere in this file asserted IsLevelOverdriven == true even once --
        // the entire "true" branch was untested.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        FeedConstantAmplitudeUntilFixed(decoder.LevelAgcForTests, rawAmplitude);

        Assert.Equal(expectedOverdriven, decoder.IsLevelOverdriven);
    }

    [Fact]
    public void AnalogFmSstvDecoder_BeforeAnyPush_SignalPeakLevelAndBufferedSampleCountAreZero()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate);

        Assert.Equal(0.0, decoder.SignalPeakLevel);
        Assert.False(decoder.IsLevelOverdriven);
        Assert.Equal(0, decoder.BufferedSampleCount);
        Assert.Null(decoder.SyncFrequencyCorrectionHz);
    }

    [Fact]
    public void AnalogFmSstvDecoder_AfterResetAgc_SignalPeakLevelIsZero_BeforeTheNextWindowCompletes()
    {
        // Precondition made explicit (round-2 plan-review nit): this only holds until a subsequent
        // push completes another ~100ms Fix() window -- asserted here BEFORE ever pushing anything
        // post-reset, not relying on timing to keep it true.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        decoder.PushSamples(GenerateConstantAmplitudeSignal(0.8, SampleRate)); // 1 full second, well past a 100ms window -- drives CurMax non-zero
        Assert.NotEqual(0.0, decoder.SignalPeakLevel); // sanity: the pre-reset push actually moved it

        decoder.ResetAgc();

        Assert.Equal(0.0, decoder.SignalPeakLevel);
        Assert.False(decoder.IsLevelOverdriven);
    }

    [Theory]
    [InlineData(0.9, true)] // well above the ~0.75 (24578/32768) threshold even after BPF attenuation
    [InlineData(0.3, false)] // well below it
    public void AnalogFmSstvDecoder_RealSyncToneSignal_SignalPeakLevelAndIsLevelOverdrivenReflectActualAmplitude(double amplitude, bool expectedOverdriven)
    {
        // End-to-end companion to the exact-value LevelAgcForTests-driven tests above: proves the
        // properties reflect a REAL demodulated signal through the actual capture/BPF/AGC pipeline
        // (AgcSampleAt), not just a directly-driven LevelAgc instance. 1200Hz matches the sync
        // tone this port's search bandpass filter is centered on.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        decoder.PushSamples(GenerateConstantAmplitudeSignal(amplitude, SampleRate)); // 1 full second, well past a 100ms window

        Assert.Equal(expectedOverdriven, decoder.IsLevelOverdriven);
    }

    [Fact]
    public void AnalogFmSstvDecoder_BufferedSampleCount_NonzeroButBoundedWhileIdleStreaming()
    {
        // Narrower companion to BufferTrimTests.cs's own more thorough
        // BufferedSampleCount_StaysBounded_ForLongNeverLockingStream -- this one exists specifically
        // to pin the round-1 plan-review correction (the property is NOT "0 while idle", it retains
        // a bounded pre-lock search window) as its own explicit, named claim.
        // Matches BufferTrimTests.cs's own scale (11025Hz, 30 real seconds -- its own comment notes
        // the pre-lock retention window stays under 10 real seconds at that rate, so this needs to
        // push comfortably more than that to actually observe trimming, not just an unfilled buffer).
        const int sampleRate = 11025;
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var random = new Random(Seed: 999);
        const int totalSamples = sampleRate * 30;
        var buffer = new float[512];
        var totalPushed = 0;

        for (var pushed = 0; pushed < totalSamples; pushed += buffer.Length)
        {
            for (var j = 0; j < buffer.Length; j++)
            {
                buffer[j] = (float)(random.NextDouble() * 0.02 - 0.01); // low-level noise, never locks
            }

            decoder.PushSamples(buffer);
            totalPushed += buffer.Length;
        }

        Assert.True(decoder.BufferedSampleCount > 0, "Expected a nonzero retained pre-lock search window while idle-streaming, not 0.");
        Assert.True(decoder.BufferedSampleCount < totalPushed, "Expected bounded retention (less than everything ever pushed), not unbounded growth.");
    }

    [Fact]
    public void AnalogFmSstvDecoder_AvtMode_SyncFrequencyCorrectionHzStaysNull()
    {
        // AVT has no AFC tracking -- InitializeAfc nulls _afcTracker for it, mirroring Auto Slant's
        // own AVT exclusion (same pattern SlantTests.cs's own AVT test already pins for SlantPpm).
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        decoder.ForceMode(SstvModeRegistry.Avt);
        decoder.PushSamples(new float[64]);

        Assert.Equal(SstvModeRegistry.Avt.Id, decoder.ModeForTests?.Id);
        Assert.Null(decoder.SyncFrequencyCorrectionHz);
    }

    [Fact]
    public void AnalogFmSstvDecoder_AfcDisabledBySetting_SyncFrequencyCorrectionHzStaysNull()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate, afcEnabled: false);
        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[64]);

        Assert.Equal(SstvModeRegistry.Robot36.Id, decoder.ModeForTests?.Id);
        Assert.Null(decoder.SyncFrequencyCorrectionHz);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_DuringAPendingAvtTrainingWindow_SyncFrequencyCorrectionHzIsNullDespiteTheAbandonedTrackerStillBeingAlive()
    {
        // Same real bug class SlantPpm needed fixing for (see SlantTests.cs's own
        // AnalogFmSstvDecoder_DuringAPendingAvtTrainingWindow_... test and doc comment): a chunked
        // push spanning the up-to-~7.1s AVT mid-reception training pending window, during which
        // _mode is null but AbandonInProgressImage deliberately leaves the abandoned mode's
        // _afcTracker alive. A naive `_afcTracker?.CorrectionHz` would leak the abandoned image's
        // stale correction for that whole window instead of returning null.
        var firstMode = SstvModeRegistry.MartinM1;
        var firstImage = CreateGradientTestImage(firstMode.ImageWidth, firstMode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(11025);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(firstMode, firstImage))
        {
            firstSamples.Add(sample);
        }

        var truncatedFirstSamples = firstSamples.Take(firstSamples.Count * 3 / 10).ToArray();

        var avtMode = SstvModeRegistry.Avt;
        var avtPixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(avtPixels, new Rgb24(10, 20, 30));
        var avtImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, avtPixels);
        var avtSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, avtImage))
        {
            avtSamples.Add(sample);
        }

        var combined = truncatedFirstSamples.Concat(avtSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var sawPendingWindow = false;

        const int chunkSize = 500;
        for (var offset = 0; offset < combined.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, combined.Length - offset);
            decoder.PushSamples(combined.AsMemory(offset, length));

            if (decoder.ModeForTests is null && decoder.HasAfcTrackerForTests)
            {
                sawPendingWindow = true;
                Assert.Null(decoder.SyncFrequencyCorrectionHz);
            }
        }

        Assert.True(sawPendingWindow, "Never observed _mode == null with a still-alive AFC tracker -- test setup no longer exercises the pending-AVT-training window this test targets.");
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_RealMistunedDecode_SyncFrequencyCorrectionHzBecomesNonZeroOnceAfcLocks()
    {
        // Positive-path reachability check: reuses AfcTests.cs's own real encode-at-a-mistuned-rate,
        // decode-at-the-declared-rate pattern (AnalogFmSstvDecoder_RealMistunedDecode_..., same
        // 500ppm mismatch) -- a genuine carrier offset a same-process synthetic round-trip
        // otherwise has none of -- so AFC actually locks onto a real, nonzero correction during
        // this decode, confirming the property is live end-to-end, not stuck at its pre-lock 0.0
        // default for the whole decode. (AfcTracker has no test-only instance accessor the way
        // SlantTracker does, so this can't cross-check the property against a second independent
        // read the way SlantTests.cs's own wiring test does -- the property here IS a trivial
        // passthrough with no extra math, so the null-case tests above already cover its real risk
        // surface; this test's job is only to confirm the wiring is actually reachable.) Sampled
        // from inside LineDecoded, not after PushSamples returns: a bulk push that completes the
        // whole image also runs EndOfImage() (nulling _afcTracker) before PushSamples returns.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(200, 120, 60));
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
        double? lastNonZeroValue = null;
        decoder.LineDecoded += _ =>
        {
            if (decoder.SyncFrequencyCorrectionHz is { } value && value != 0.0)
            {
                lastNonZeroValue = value;
            }
        };

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(lastNonZeroValue); // AFC actually locked onto a real, nonzero correction during this decode
    }

    private static void FeedConstantAmplitudeUntilFixed(LevelAgc agc, double rawAmplitude)
    {
        // LevelAgc.Fix() self-throttles until _cntMax (100ms worth) samples have been fed via Do() --
        // feed comfortably past that.
        var samplesFor150Ms = (int)(SampleRate * 0.15);
        for (var i = 0; i < samplesFor150Ms; i++)
        {
            agc.Do(rawAmplitude);
            agc.Fix();
        }
    }

    private static float[] GenerateConstantAmplitudeSignal(double amplitude, int sampleCount)
    {
        var samples = new float[sampleCount];
        var phaseIncrement = 2 * Math.PI * 1200.0 / SampleRate;
        var phase = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            phase += phaseIncrement;
            samples[i] = (float)(amplitude * Math.Sin(phase));
        }

        return samples;
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
}
