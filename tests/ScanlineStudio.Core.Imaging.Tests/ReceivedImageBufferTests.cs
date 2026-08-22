using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ReceivedImageBufferTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 2,
        ImageHeight: 2,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    [Fact]
    public void OnLineDecoded_CopiesWholeImage_NotJustTheReportedRow()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        var pixels = new Rgb24[4];
        pixels[0] = new Rgb24(1, 1, 1);
        pixels[1] = new Rgb24(2, 2, 2);
        pixels[2] = new Rgb24(3, 3, 3); // row 1 -- already filled even though this update reports row 0,
        pixels[3] = new Rgb24(4, 4, 4); // simulating a paired-mode decoder (RowsPerTransmissionLine = 2).
        var liveImage = new MutableTestImageSource(2, 2, pixels);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, liveImage));

        var current = buffer.Current;
        Assert.Equal(new Rgb24(1, 1, 1), current.GetScanline(0)[0]);
        Assert.Equal(new Rgb24(2, 2, 2), current.GetScanline(0)[1]);
        Assert.Equal(new Rgb24(3, 3, 3), current.GetScanline(1)[0]);
        Assert.Equal(new Rgb24(4, 4, 4), current.GetScanline(1)[1]);
    }

    [Fact]
    public void Current_ReturnsSnapshot_UnaffectedByLaterMutationOfTheLiveBuffer()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        var pixels = new Rgb24[4];
        pixels[0] = new Rgb24(1, 1, 1);
        var liveImage = new MutableTestImageSource(2, 2, pixels);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, liveImage));

        var snapshot = buffer.Current;
        pixels[0] = new Rgb24(99, 99, 99); // decoder "keeps writing" into the same live array

        Assert.Equal(new Rgb24(1, 1, 1), snapshot.GetScanline(0)[0]);
    }

    [Fact]
    public void OnDecodeRestarted_ResetsToAnEmptyImage()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        var pixels = new Rgb24[4];
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(2, 2, pixels)));

        decoder.RaiseDecodeRestarted(TestMode);

        Assert.Equal(1, buffer.Current.Width);
        Assert.Equal(1, buffer.Current.Height);
    }

    [Fact]
    public void Updated_FiresOnModeDetectedAndLineDecodedAndDecodeRestarted()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        var updateCount = 0;
        buffer.Updated += () => updateCount++;

        decoder.RaiseModeDetected(TestMode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, new Rgb24[1])));
        decoder.RaiseDecodeRestarted(TestMode);

        Assert.Equal(3, updateCount);
    }

    [Fact]
    public void Progress_FreshBuffer_IsNull()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);

        Assert.Null(buffer.Progress);
    }

    [Fact]
    public void Progress_AfterModeDetected_IsZero()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);

        decoder.RaiseModeDetected(TestMode);

        Assert.Equal(0.0, buffer.Progress);
    }

    [Fact]
    public void Progress_FirstLineDecoded_StepNotYetLearned_FallsBackToLineOverHeight()
    {
        // 10-row image, single-row-per-event family (step unknowable from just one event).
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(3, image));

        Assert.Equal(3.0 / 10.0, buffer.Progress);
    }

    [Fact]
    public void Progress_SecondLineDecoded_StepLearned_UsesLinePlusStep()
    {
        // Single-row-per-event family: step = 1.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image));

        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, image));

        Assert.Equal(2.0 / 10.0, buffer.Progress);
    }

    [Fact]
    public void Progress_PairedLineFamily_StepLearnedAsTwo_ComputesCorrectly()
    {
        // PD/MP/RM8/RM12-shaped: Line advances by 2 per event.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image));

        decoder.RaiseLineDecoded(new DecodedImageUpdate(2, image));

        Assert.Equal(4.0 / 10.0, buffer.Progress);
    }

    [Fact]
    public void Progress_FinalCompletingEvent_ReachesExactlyOne()
    {
        // 10-row image, step-1 family: events at 0,1,2,...,9. The Line=9 event has
        // Line+step == 10 == imageHeight exactly (never overshoots), so (Line+step)/imageHeight is
        // exactly 1.0 with no special-casing needed -- Tier B audit finding, correcting an earlier
        // version of this comment that claimed a dedicated "snap" branch was load-bearing here (it
        // was provably dead code: Clamp's own upper bound already produced 1.0 in every reachable
        // case).
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, image));

        decoder.RaiseLineDecoded(new DecodedImageUpdate(9, image));

        Assert.Equal(1.0, buffer.Progress);
    }

    [Fact]
    public void Progress_ReplayedLinesGoingBackwards_DoesNotRelearnTheStep()
    {
        // Tier B audit finding: AnalogFmSstvDecoder's replay path (and a user-triggered Correct
        // Slant redraw) can re-emit LineDecoded for already-decoded rows, restarting back at Line=0
        // mid-image. _observedStep must NOT be re-derived from that backward jump (which would
        // compute a negative/wrong step) -- ComputeProgress's own guard only ever learns the step
        // once (_observedStep is null), so a replayed Line=0 after the step is already learned must
        // leave it untouched. Verified indirectly: if the step were corrupted to 0-1=-1 by the
        // replayed jump, the next real line's progress would differ from the value below.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image)); // step not yet learned
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, image)); // step learned = 1

        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image)); // replay jumps back to row 0

        decoder.RaiseLineDecoded(new DecodedImageUpdate(3, image)); // a real line, post-replay
        Assert.Equal(4.0 / 10.0, buffer.Progress); // (3 + step(1)) / 10 -- only correct if step is still 1
    }

    [Fact]
    public void Progress_DecodeRestarted_ResetsToNull()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 10, new Rgb24[10])));

        decoder.RaiseDecodeRestarted(TestMode);

        Assert.Null(buffer.Progress);
    }

    [Fact]
    public async Task SaveAsync_WritesARealPngFileMatchingCurrentPixels()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        var pixels = new[] { new Rgb24(10, 20, 30) };
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, pixels)));
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-received-image-test-{Guid.NewGuid()}.png");

        try
        {
            await buffer.SaveAsync(path);

            using var loaded = await SixLabors.ImageSharp.Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(path);
            var pixel = loaded[0, 0];
            Assert.Equal(10, pixel.R);
            Assert.Equal(20, pixel.G);
            Assert.Equal(30, pixel.B);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_RaisesSaved_WithTheDestinationPath()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, new[] { new Rgb24(1, 2, 3) })));
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-received-image-test-{Guid.NewGuid()}.png");

        string? raisedPath = null;
        buffer.Saved += (p, _) => raisedPath = p;

        try
        {
            await buffer.SaveAsync(path);
            Assert.Equal(path, raisedPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_ASavedSubscriberThatThrows_DoesNotFaultTheSaveTask()
    {
        // Regression test for a real bug an auditor caught: Saved is raised INSIDE SaveAsync's own
        // Task.Run body, and ReceiveHistoryRecorder (the sole production caller of SaveAsync) awaits
        // that exact Task before recording the RX history row. An uncaught subscriber exception would
        // fault the save itself, so a fully-successful image write would still lose its history row.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, new[] { new Rgb24(1, 2, 3) })));
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-received-image-test-{Guid.NewGuid()}.png");

        buffer.Saved += (_, _) => throw new InvalidOperationException("simulated subscriber failure");

        try
        {
            // Must complete without throwing, and the file must still have been written -- the
            // subscriber's own failure must not undo or fault the underlying save.
            await buffer.SaveAsync(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generation_BumpsOnModeDetectedAndOnDecodeRestarted_NotOnLineDecoded()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);

        Assert.Equal(0, buffer.Generation);

        decoder.RaiseModeDetected(TestMode);
        Assert.Equal(1, buffer.Generation);

        // Same image, just a new decoded row -- must NOT bump the generation.
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, new[] { new Rgb24(1, 2, 3) })));
        Assert.Equal(1, buffer.Generation);

        decoder.RaiseDecodeRestarted(TestMode);
        Assert.Equal(2, buffer.Generation);
    }

    [Fact]
    public async Task SaveAsync_RaisesSaved_WithTheGenerationCapturedAtInvocationTime_NotAtCompletionTime()
    {
        // Regression test for a real race an auditor round-2 review caught: the generation must be
        // captured when SaveAsync is CALLED (alongside its own snapshot of Current), not whenever the
        // background write happens to finish -- otherwise a ModeDetected arriving WHILE the save is
        // still in flight would already have bumped Generation before Saved ever fires, making every
        // save look "stale" even though it correctly captured the frame that was current when the
        // caller actually invoked it.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder, NullLogger<ReceivedImageBuffer>.Instance);
        decoder.RaiseModeDetected(TestMode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, new[] { new Rgb24(1, 2, 3) })));
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-received-image-test-{Guid.NewGuid()}.png");

        int? raisedGeneration = null;
        buffer.Saved += (_, g) => raisedGeneration = g;

        try
        {
            var saveTask = buffer.SaveAsync(path);
            // Simulates a NEW mode detection arriving while the save above is still in flight.
            decoder.RaiseModeDetected(TestMode);
            await saveTask;

            Assert.Equal(1, raisedGeneration);
            Assert.Equal(2, buffer.Generation);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
