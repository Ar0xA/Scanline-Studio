using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Round-2-review addition: <see cref="MidReceptionRestartTests"/> already proves piece 6c's
/// mid-reception re-verification (<c>TryVisLockStateMachine(_consumedSamples)</c>, called once per
/// decoded line while locked) fires for a transmission that was header-locked -- but the m_sint1
/// decoder-ordering fix (<see cref="SyncScanInterleaveTests"/>) has a documented side effect (see
/// <c>AnalogFmSstvDecoder.Commit</c>'s own comment) that this path is now ALSO reachable for a
/// transmission that was sync-bypass-locked (via m_sint1/m_sint2/m_sint3, no real header ever seen).
/// Before the fix, that combination left <c>_visLockProcessedUpTo</c> pinned at the buffer's end by
/// the old sequential full-buffer scan, making piece 6c's per-line check an accidental no-op for the
/// rest of such a transmission. This test proves the path now genuinely engages (not just that it
/// stays silent, which <see cref="SyncScanInterleaveTests"/>' zero-restart-count assertion already
/// covers) -- using the same truncate-then-append-a-second-transmission shape as
/// <see cref="MidReceptionRestartTests"/>, but with the FIRST transmission built the same
/// header-stripped way as <see cref="SyncScanInterleaveTests"/> so it locks via m_sint1 alone rather
/// than via a real VIS header.
/// </summary>
public class PiecesSixCReachabilityTests
{
    private const int SampleRate = 44100;

    [Fact]
    public async Task TruncatedSyncBypassLockedFirstTransmission_StillAbandonsAndDecodesTheSecond()
    {
        var robot36 = SstvModeRegistry.Robot36;
        var robot36Image = CreateGradientTestImage(robot36.ImageWidth, robot36.ImageHeight);
        var robot36Samples = await EncodeAsync(robot36, robot36Image);

        // Same header-stripping technique as SyncScanInterleaveTests, so this transmission locks via
        // TrySyncIntervalDetectionStep's m_sint1 alone, never via a real VIS header/VisLockStateMachine.
        //
        // Round-1-review finding (auditor, SHOULD item 5's own review): missing
        // VisHeader.OutHeadNormalDurationMs -- see SyncBypassDetectionTests' own identical fix
        // comment for the full explanation.
        var headerDurationMs = VisHeader.OutHeadNormalDurationMs + VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs;
        var headerSampleCount = (int)Math.Round(headerDurationMs / 1000.0 * SampleRate);
        var robot36Body = robot36Samples.Skip(headerSampleCount).ToArray();

        // Truncated to 30%, the same fraction MidReceptionRestartTests uses for a header-locked
        // first transmission -- SyncBypass1DetectionTests already proves m_sint1 locks from only a
        // few lines' worth of periodicity, so 30% of Robot 36's own transmission lines is far more
        // than enough for a lock to have already happened well before this cut.
        var truncatedRobot36Body = robot36Body.Take(robot36Body.Length * 3 / 10).ToArray();

        var martinM1 = SstvModeRegistry.MartinM1;
        var martinM1Image = CreateGradientTestImage(martinM1.ImageWidth, martinM1.ImageHeight);
        var martinM1Samples = await EncodeAsync(martinM1, martinM1Image);

        var combined = truncatedRobot36Body.Concat(martinM1Samples).ToArray();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedModesInOrder = new List<SstvModeDefinition>();
        var restartCount = 0;
        decoder.ModeDetected += m => detectedModesInOrder.Add(m);
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(combined);

        // If piece 6c's mid-reception check were still an accidental no-op for a sync-bypass-locked
        // transmission (the pre-fix behavior), the decoder would just keep trying to decode the
        // truncated Robot 36 body's remaining "lines" from Martin M1's own audio, never separately
        // detecting Martin M1 via its real header, and restartCount would stay 0. Detecting Martin M1
        // as a second, distinct mode with exactly one restart is the direct, positive signature that
        // TryVisLockStateMachine(_consumedSamples) actually ran and found Martin M1's header mid-line.
        Assert.Equal(["robot-36", "martin-m1"], detectedModesInOrder.Select(m => m.Id));
        Assert.Equal(1, restartCount);
    }

    private static async Task<float[]> EncodeAsync(SstvModeDefinition mode, ArrayImageSource image)
    {
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            samples.Add(sample);
        }

        return samples.ToArray();
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
