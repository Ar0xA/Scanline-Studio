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
///
/// Round-1-review addition: confirmed (not just reasoned about) that Robot 36 completes cleanly
/// (all of its lines decoded) before Martin M1 is detected via its own real header at
/// <c>EndOfImage</c> -- exactly <c>["robot-36", "martin-m1"]</c> with zero <c>DecodeRestarted</c>
/// events, measured directly rather than assumed, so both tests below pin that exact shape instead
/// of the weaker "M1 appears somewhere" check an earlier version of this file used.
///
/// Round-2-review clarification: the chunked-streaming variant below is a chunk-size-invariance
/// check on this fix specifically (proving <c>TryInterleavedHeaderScan</c>'s per-sample state
/// behaves identically regardless of how <c>PushSamples</c> is called), not a second, independently
/// discriminating regression test -- at <c>chunkSize = 1024</c> it would very likely pass even
/// against the pre-fix, two-sequential-full-buffer-scans shape, since a 1024-sample head start is
/// nowhere near enough for the old code's <c>VisLockStateMachine</c> pass to reach Martin M1's
/// header several seconds ahead of Robot 36's own lock point. The bulk-push variant above is the one
/// empirically confirmed (via <c>git stash</c>) to fail pre-fix; see its own assertion helper below.
/// </summary>
public class SyncScanInterleaveTests
{
    private const int SampleRate = 44100;

    [Fact]
    public async Task EarlierSyncIntervalMatch_PreemptsLaterVisHeaderLock()
    {
        var combined = await BuildCombinedFixtureAsync();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedModesInOrder = new List<SstvModeDefinition>();
        var restartCount = 0;
        decoder.ModeDetected += m => detectedModesInOrder.Add(m);
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(combined);

        AssertExpectedOutcome(detectedModesInOrder, restartCount);
    }

    // Round-1-review addition: this whole fix is about a bulk-vs-streaming ordering divergence
    // (TryDecodeHeader's own doc comment), so the one obvious case the original single-bulk-push
    // test couldn't distinguish is whether the fix actually holds under genuine streaming too, not
    // just a single PushSamples call handing every mechanism the entire buffer at once. The
    // interleave is per-sample state (see TryInterleavedHeaderScan's own doc comment), so this is
    // expected to behave identically -- confirmed here empirically rather than left as a plausible
    // but unverified assumption.
    [Fact]
    public async Task EarlierSyncIntervalMatch_PreemptsLaterVisHeaderLock_UnderChunkedStreaming()
    {
        var combined = await BuildCombinedFixtureAsync();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedModesInOrder = new List<SstvModeDefinition>();
        var restartCount = 0;
        decoder.ModeDetected += m => detectedModesInOrder.Add(m);
        decoder.DecodeRestarted += _ => restartCount++;

        const int chunkSize = 1024;
        for (var offset = 0; offset < combined.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, combined.Length - offset);
            decoder.PushSamples(new ReadOnlyMemory<float>(combined, offset, length));
        }

        AssertExpectedOutcome(detectedModesInOrder, restartCount);
    }

    private static void AssertExpectedOutcome(List<SstvModeDefinition> detectedModesInOrder, int restartCount)
    {
        // Confirmed discrimination empirically (this project's own precedent, e.g.
        // PllScaleBridgeTests), not assumed: temporarily reverting TryDecodeHeader/
        // TryInterleavedHeaderScan back to the old two-sequential-full-buffer-scans shape and
        // re-running the bulk-push test fails with detectedModesInOrder[0].Id == "martin-m1" --
        // VisLockStateMachine sweeping the whole buffer finds Martin M1's real header before the
        // sync-bypass detectors (where m_sint1 lives) ever see sample 0 of the Robot 36 content
        // sitting right at the start of the same buffer.
        Assert.Equal(["robot-36", "martin-m1"], detectedModesInOrder.Select(m => m.Id));

        // Robot 36's entire (headerless) body decodes cleanly to completion before Martin M1 is
        // separately detected via EndOfImage -- not a mid-reception restart. Measured directly
        // (Console-instrumented run showed restartCount==0 for the bulk-push case before this
        // assertion was written), not assumed from the mode sequence alone.
        Assert.Equal(0, restartCount);
    }

    private static async Task<float[]> BuildCombinedFixtureAsync()
    {
        var robot36 = SstvModeRegistry.Robot36;
        var robot36Image = CreateGradientTestImage(robot36.ImageWidth, robot36.ImageHeight);
        var robot36Samples = await EncodeAsync(robot36, robot36Image);

        // Strip exactly the VIS header, same as SyncBypass1DetectionTests -- leaving only the raw,
        // periodic sync+image-line data a real headerless transmission would present. This is the
        // exact fixture already proven (by that test) to lock via m_sint1 alone.
        var headerDurationMs = VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs;
        var headerSampleCount = (int)Math.Round(headerDurationMs / 1000.0 * SampleRate);
        var robot36Body = robot36Samples.Skip(headerSampleCount).ToArray();

        var martinM1 = SstvModeRegistry.MartinM1;
        var martinM1Image = CreateGradientTestImage(martinM1.ImageWidth, martinM1.ImageHeight);
        var martinM1Samples = await EncodeAsync(martinM1, martinM1Image);

        return robot36Body.Concat(martinM1Samples).ToArray();
    }

    private static async Task<float[]> EncodeAsync(SstvModeDefinition mode, Yoniq.Core.Imaging.ArrayImageSource image)
    {
        var encoder = new AnalogFmSstvEncoder(SampleRate);
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
