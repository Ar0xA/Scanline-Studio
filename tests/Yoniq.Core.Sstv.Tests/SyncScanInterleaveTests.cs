using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Proves the m_sint1 decoder-ordering fix (spec/14-roadmap.md: "m_sint1's decoder-level priority
/// is effectively inverted from legacy's real per-sample interleaving") for real, deterministically
/// -- not by trying to synthesize <see cref="VisLockStateMachine"/>'s own documented false-positive
/// risk (its own doc comment says that's hand-verified reachable but no fixture has triggered it;
/// a test built on engineering one would be fragile and would only incidentally exercise this fix).
///
/// Instead: concatenate a real, headerless Robot 36 transmission (recognizable via
/// <c>TrySyncIntervalDetectionStep</c>'s own <c>m_sint1</c> alone, same fixture-construction as
/// <see cref="SyncBypass1DetectionTests"/>) directly before a real, header-included Martin M1
/// transmission. Before the fix, <c>AnalogFmSstvDecoder.TryDecodeHeader</c> tried
/// <c>VisLockStateMachine</c> over the *entire* available buffer before the sync-bypass detectors
/// (where m_sint1 lives) ever saw a single sample -- so it would find Martin M1's real header
/// *first*, deep inside a buffer that also contains a perfectly recognizable Robot 36 transmission
/// starting at sample 0. After the fix (<c>TryInterleavedHeaderScan</c>), both mechanisms advance
/// sample-by-sample in lockstep, so Robot 36 -- needing only a few lines' worth of sync-pulse
/// periodicity, appearing at the very start of the buffer -- is recognized well before
/// <c>VisLockStateMachine</c> ever reaches Martin M1's header, deep inside the same buffer.
/// </summary>
public class SyncScanInterleaveTests
{
    [Fact]
    public async Task EarlierSyncIntervalMatch_PreemptsLaterVisHeaderLock()
    {
        const int sampleRate = 44100;

        var robot36 = SstvModeRegistry.Robot36;
        var robot36Image = CreateGradientTestImage(robot36.ImageWidth, robot36.ImageHeight);
        var robot36Samples = await EncodeAsync(robot36, robot36Image, sampleRate);

        // Strip exactly the VIS header, same as SyncBypass1DetectionTests -- leaving only the raw,
        // periodic sync+image-line data a real headerless transmission would present. This is the
        // exact fixture already proven (by that test) to lock via m_sint1 alone.
        var headerDurationMs = VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs;
        var headerSampleCount = (int)Math.Round(headerDurationMs / 1000.0 * sampleRate);
        var robot36Body = robot36Samples.Skip(headerSampleCount).ToArray();

        var martinM1 = SstvModeRegistry.MartinM1;
        var martinM1Image = CreateGradientTestImage(martinM1.ImageWidth, martinM1.ImageHeight);
        var martinM1Samples = await EncodeAsync(martinM1, martinM1Image, sampleRate);

        var combined = robot36Body.Concat(martinM1Samples).ToArray();

        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var detectedModesInOrder = new List<SstvModeDefinition>();
        decoder.ModeDetected += m => detectedModesInOrder.Add(m);
        decoder.DecodeRestarted += _ => { }; // no assertion needed here, just documenting this can fire

        decoder.PushSamples(combined);

        // Confirmed discrimination empirically (this project's own precedent, e.g. PllScaleBridgeTests),
        // not assumed: temporarily reverting TryDecodeHeader/TryInterleavedHeaderScan back to the old
        // two-sequential-full-buffer-scans shape and re-running this exact test fails with
        // detectedModesInOrder[0].Id == "martin-m1" -- VisLockStateMachine sweeping the whole buffer
        // finds Martin M1's real header before the sync-bypass detectors (where m_sint1 lives) ever
        // see sample 0 of the Robot 36 content sitting right at the start of the same buffer.
        Assert.NotEmpty(detectedModesInOrder);
        Assert.Equal(robot36.Id, detectedModesInOrder[0].Id);
        Assert.Contains(detectedModesInOrder, m => m.Id == martinM1.Id);
    }

    private static async Task<float[]> EncodeAsync(SstvModeDefinition mode, Yoniq.Core.Imaging.ArrayImageSource image, int sampleRate)
    {
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            samples.Add(sample);
        }

        return samples.ToArray();
    }

    private static Yoniq.Core.Imaging.ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Yoniq.Abstractions.Imaging.Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Yoniq.Abstractions.Imaging.Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new Yoniq.Core.Imaging.ArrayImageSource(width, height, pixels);
    }
}
