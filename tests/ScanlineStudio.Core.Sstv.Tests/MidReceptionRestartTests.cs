using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// End-to-end proof of piece 6d: the actual mid-reception abandon-and-restart scenario piece 6c
/// wires up (legacy's case-0 trigger, `sstv.cpp:1946-1950`, carries no `!m_Sync` guard on the
/// transition itself -- keeps running even while already locked, at legacy's own shipped defaults;
/// see <see cref="AnalogFmSstvDecoder"/>'s piece-6c comment for the full `m_SyncRestart` nuance,
/// found by round-2 review). A first transmission's audio is truncated partway through its own image body
/// (simulating a real station cutting out, or a second station overriding it), immediately followed
/// by a second, complete transmission -- no footer, no gap, just a hard cut mid-image into a new
/// header. This is the scenario <see cref="EndOfImageResetTests"/> does not cover: that test's two
/// transmissions are both complete, so <see cref="AnalogFmSstvDecoder.EndOfImage"/> (piece 6a) is
/// what resolves it, not the mid-reception restart path (piece 6c) this test targets specifically.
/// </summary>
public class MidReceptionRestartTests
{
    [Fact]
    public async Task TruncatedFirstTransmission_AbandonsAndDecodesTheSecond()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage1 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 0);
        var sourceImage2 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 64);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples1 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage1))
        {
            samples1.Add(sample);
        }

        // Truncate well past the header (~910ms) but well before the image completes -- lands
        // partway through the image body, matching a real cut/override mid-reception.
        var truncatedSamples1 = samples1.Take(samples1.Count * 3 / 10).ToArray();

        var samples2 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage2))
        {
            samples2.Add(sample);
        }

        var combined = truncatedSamples1.Concat(samples2).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        var decodedImages = new List<IImageSource>();
        var restartCount = 0;
        decoder.ModeDetected += m =>
        {
            detectedModes.Add(m);
            decodedImages.Add(null!);
        };
        decoder.DecodeRestarted += _ => restartCount++;
        decoder.LineDecoded += update => decodedImages[^1] = update.Image;

        decoder.PushSamples(combined);

        Assert.Equal(2, detectedModes.Count);
        Assert.Equal(mode.Id, detectedModes[0].Id);
        Assert.Equal(mode.Id, detectedModes[1].Id);
        Assert.Equal(1, restartCount);

        // Measured, not assumed: 11.84 (re-measured after piece 7c; was 9.81 before it). Resolved via
        // VisLockStateMachine's own anchor -- as of 7c, that anchor's own tolerance grew from 10.0 to
        // 13.0 (see VisLockStateMachineDecoderTests' doc comment for why: CLVL's AGC now correctly
        // hard-clips a full-amplitude tone, adding real, legacy-faithful noise to the exact trigger
        // sample). 14.0 gives the same kind of headroom here, not a new imprecision introduced by
        // piece 6c/6d specifically.
        var delta = MeasureDelta(sourceImage2, decodedImages[1]);
        Assert.True(delta <= 14.0, $"Second (real) transmission average per-channel delta {delta:F2} exceeded tolerance.");
    }

    [Fact]
    public async Task TruncatedFirstTransmission_WithSyncRestartDisabled_NeverAbandonsOrRestarts()
    {
        // Same scenario as TruncatedFirstTransmission_AbandonsAndDecodesTheSecond above, but with the
        // new SyncRestart toggle (port of legacy's real m_SyncRestart, sstv.cpp:1486, default 1) set
        // to false -- the mid-reception restart trigger this test's sibling exercises (TryVisLockState
        // Machine's own MID-reception, still-locked scan) must not run at all. The first (truncated)
        // image is instead decoded straight through as (corrupted) garbage past the real truncation
        // point until its own ImageHeight is reached normally -- no abandonment, so DecodeRestarted
        // must never fire. Once that first image completes and _mode goes null again, ordinary
        // pre-lock header detection is free to find the second transmission's still-undetected real
        // header in whatever buffer remains -- so a second ModeDetected is still expected; the
        // discriminating assertion is restartCount, not how many ModeDetected events fire.
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage1 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 0);
        var sourceImage2 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 64);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples1 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage1))
        {
            samples1.Add(sample);
        }

        var truncatedSamples1 = samples1.Take(samples1.Count * 3 / 10).ToArray();

        var samples2 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage2))
        {
            samples2.Add(sample);
        }

        var combined = truncatedSamples1.Concat(samples2).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate, syncRestartEnabled: false);
        var detectedModeCount = 0;
        var restartCount = 0;
        decoder.ModeDetected += _ => detectedModeCount++;
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(combined);

        Assert.Equal(0, restartCount); // the discriminating assertion -- see this test's own doc comment
        Assert.True(detectedModeCount >= 1, "Never even detected the first transmission -- test setup problem.");
    }

    // D0-audit round-11/12: an end-to-end regression test was attempted here (a station-ID FSK
    // burst spliced into a locked MartinM1 reception, syncRestartEnabled: false, asserting
    // StationIdDecoded still fires) to cover round 11's TryNarrowFskScan-hoisting fix. It does not
    // pass, and -- checked directly -- the SAME construction still fails even with
    // syncRestartEnabled: true (the pre-round-11 code path, unaffected by that fix): narrow-FSK
    // never delivers a StationIdDecoded result while genuinely locked mid-reception through
    // BandpassFilteredSampleAt's `useLocked` (H1) filter selection, in either setting. This means
    // round 12's specific justification for expecting the test to "just work" (FSK tone frequencies
    // sit inside the locked filter's nominal passband) was not sufficient in practice -- something
    // else in the locked-filter path prevents narrow-FSK demodulation from completing, a real,
    // separate, pre-existing gap this file has apparently never had ANY test coverage for (every
    // existing StationIdDecoded test is either pre-lock, `StationIdDoesNotAbortScanTests`, or a
    // post-image FOOTER burst that plays only after EndOfImage has already nulled `_mode` again,
    // `AnalogFmSstvEncoderStationIdWiringTests` -- neither exercises `_mode is not null` at burst
    // time). Round 11's own gating fix is independently correct by direct code tracing (confirmed
    // by two separate audit rounds); this gap is orthogonal to it, not a hole in it. Left
    // deliberately unfixed and untested here -- diagnosing WHY locked-filter narrow-FSK doesn't
    // decode is a new, separate investigation, not a one-line follow-up to this fix.
    private static ArrayImageSource CreateGradientTestImage(int width, int height, int offset)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)((x * 255 / Math.Max(1, width - 1) + offset) % 256),
                    G: (byte)((y * 255 / Math.Max(1, height - 1) + offset) % 256),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static double MeasureDelta(IImageSource expected, IImageSource actual)
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
}
