using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Real-decode coverage for <see cref="ISstvDecoder.SyncSource"/> (elegant-wondering-hinton.md
/// item 5) across all 3 states -- a fresh decoder is idle, a locked mode reports Locked, and AVT
/// training in flight (before it resolves to a lock) reports AvtTraining. Deliberately exercises the
/// real decoder/decode pipeline, not a mocked one -- SyncSource is derived from the same
/// _mode/_avtTrainingPending fields IsIdle already uses, so a real push is the only way to prove the
/// derivation actually tracks decoder state, not just that it compiles.</summary>
public class SyncSourceTests
{
    [Fact]
    public void FreshDecoder_ReportsIdle()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);
    }

    [Fact]
    public async Task LockedMode_ReportsLocked_ThenIdleAgainAfterEndOfImage()
    {
        var mode = SstvModeRegistry.MartinM1;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(200, 100, 50));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025);
        var linesDecoded = 0;
        decoder.LineDecoded += _ => linesDecoded++;

        // Only the first third: enough to lock and decode real lines, nowhere near enough for the
        // image or its trailing footer to complete on its own -- same technique
        // FieldLifecycleTests.SlantPpmAndSyncFrequencyCorrectionHz_... uses for the same reason.
        decoder.PushSamples(samples.Take(samples.Count / 3).ToArray());

        Assert.True(linesDecoded > 0, "Test setup problem: expected real lines decoded before asserting Locked.");
        Assert.Equal(SstvSyncSource.Locked, decoder.SyncSource);

        // Push the rest of the encoded stream (image tail + encoder's own trailing footer) to drive
        // the decoder through its natural end-of-image teardown, same as
        // ZeroCrossingDemodulator_CurrentFrequencyResetsToTheClearedValue_AtEndOfImage does.
        decoder.PushSamples(samples.Skip(samples.Count / 3).ToArray());

        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);
    }

    [Fact]
    public async Task AvtTrainingInFlight_ReportsAvtTraining()
    {
        var avtMode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025);

        // Same small-chunk technique as ForceModeTests.ForceMode_WhileAvtTrainingIsPending_...:
        // training state can't be reached except by feeding real AVT audio incrementally.
        const int chunkSize = 128;
        var offset = 0;
        var reachedTrainingPending = false;
        for (; offset < samples.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Count - offset);
            decoder.PushSamples(samples.GetRange(offset, length).ToArray());

            if (decoder.AvtTrainingPendingForTests && decoder.ModeForTests is null)
            {
                reachedTrainingPending = true;
                break;
            }
        }

        Assert.True(reachedTrainingPending, "Never observed AVT training in flight -- test setup problem.");
        Assert.Equal(SstvSyncSource.AvtTraining, decoder.SyncSource);
    }
}
