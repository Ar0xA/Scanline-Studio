using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class ReceiveHistoryRecorderTests
{
    [Fact]
    public async Task SingleScanSegmentMode_RecordsExactlyOnce_OnlyAfterTheLastLine()
    {
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 4);
        _ = new ReceiveHistoryRecorder(decoder, new FakeReceivedImageBuffer(), historyStore, new FakeSettingsStore());

        decoder.RaiseModeDetected(mode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        Assert.Empty(historyStore.RecordedEntries);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, FakeImage));
        Assert.Empty(historyStore.RecordedEntries);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(2, FakeImage));
        Assert.Empty(historyStore.RecordedEntries);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(3, FakeImage)); // last line: 3 + step(1) >= 4
        var entry = await historyStore.WaitForRecordAsync();

        Assert.Equal(mode.Id, entry.ModeId);
        Assert.Single(historyStore.RecordedEntries);

        // Further (unexpected) events for the same image must not record again.
        decoder.RaiseLineDecoded(new DecodedImageUpdate(3, FakeImage));
        await Task.Delay(50);
        Assert.Single(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task PairedLineMode_LearnsTheStepAndRecordsOnlyAfterTheLastGroup()
    {
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 6); // paired: groups at 0, 2, 4
        _ = new ReceiveHistoryRecorder(decoder, new FakeReceivedImageBuffer(), historyStore, new FakeSettingsStore());

        decoder.RaiseModeDetected(mode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        Assert.Empty(historyStore.RecordedEntries);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(2, FakeImage)); // step learned = 2
        Assert.Empty(historyStore.RecordedEntries);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(4, FakeImage)); // last group: 4 + 2 >= 6
        var entry = await historyStore.WaitForRecordAsync();

        Assert.Equal(mode.Id, entry.ModeId);
        Assert.Single(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task DecodeRestarted_BeforeCompletion_NeverRecordsTheAbandonedImage()
    {
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 100);
        _ = new ReceiveHistoryRecorder(decoder, new FakeReceivedImageBuffer(), historyStore, new FakeSettingsStore());

        decoder.RaiseModeDetected(mode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, FakeImage));
        decoder.RaiseDecodeRestarted(mode);

        await Task.Delay(50);
        Assert.Empty(historyStore.RecordedEntries);
    }

    private static SstvModeDefinition MakeMode(int imageHeight) => new(
        Id: "test-mode",
        DisplayName: "Test Mode",
        VisCode: 1,
        ImageWidth: 4,
        ImageHeight: imageHeight,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static readonly IImageSource FakeImage = new FixedSizeImageSource(4, 4);

    private sealed class FixedSizeImageSource(int width, int height) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => new Rgb24[Width];
    }
}
