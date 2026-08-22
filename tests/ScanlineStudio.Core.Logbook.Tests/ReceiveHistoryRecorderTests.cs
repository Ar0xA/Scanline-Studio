using Microsoft.Extensions.Logging.Abstractions;
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
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

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
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

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
    public async Task CompletedImage_NotifiesReceivedImageBufferOfTheSave()
    {
        // Round-2 audit regression: the completed-image path used to go through
        // IReceivedImageBuffer.SaveAsync, which raises Saved -- the only hook
        // RxImagePaneViewModel has to correlate its Note/Flag controls and file-size readout to
        // the just-recorded frame. Switching to a direct snapshot write (see
        // RecordCompletedImageAsync's own doc comment) silently dropped that notification;
        // NotifySaved restores it. This test pins that the notification actually fires, with the
        // saved file's real path and the Generation captured at completion time -- a fake that
        // only stubs SaveAsync (as this project's fake used to, before this regression) would not
        // have caught it.
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 4);
        var receivedImage = new FakeReceivedImageBuffer { Generation = 7 };
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        for (var line = 0; line < 4; line++)
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        var entry = await historyStore.WaitForRecordAsync();

        var call = Assert.Single(receivedImage.NotifySavedCalls);
        Assert.Equal(entry.FilePath, call.Path);
        Assert.Equal(7, call.Generation);
    }

    [Fact]
    public async Task DecodeRestarted_BeforeCompletion_NeverRecordsTheAbandonedImage()
    {
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 100);
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, new FakeSettingsStore(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, FakeImage));
        decoder.RaiseDecodeRestarted(mode);

        await Task.Delay(50);
        Assert.Empty(historyStore.RecordedEntries);
    }

    // Abandoned-image save (port of legacy's m_ReqSave, sstv.cpp:2134-2137) -- see
    // ReceiveHistoryRecorder.OnDecodeRestarted's own doc comment for the full design and the two
    // rounds of plan-readiness review this went through, including a real data-corruption risk found
    // and fixed before any code was written.

    [Fact]
    public async Task DecodeRestarted_DominantOrdering_AboveThreshold_RecordsTheAbandonedImage()
    {
        // "Dominant ordering": DecodeRestarted fires BEFORE ModeDetected for the new mode -- live
        // _currentMode/_lastImage state still describes the abandoned image directly.
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 100);
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        for (var line = 0; line < 66; line++) // 66/100 >= 65% threshold (66 >= 100*65/100 = 65)
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        decoder.RaiseDecodeRestarted(mode); // fires before the new mode's own ModeDetected

        var entry = await historyStore.WaitForRecordAsync();
        Assert.Equal(mode.Id, entry.ModeId);
        Assert.Contains("_partial", entry.FilePath);
        Assert.Single(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task DecodeRestarted_DominantOrdering_ZeroLinesDecoded_DoesNotRecordOrThrow()
    {
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 100);
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        decoder.RaiseDecodeRestarted(mode); // no LineDecoded at all yet -- _lastImage/_previousLine still null

        await Task.Delay(50);
        Assert.Empty(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task DecodeRestarted_MinorityOrdering_AboveThreshold_RecordsTheAbandonedImage_ViaTheStash()
    {
        // "Minority ordering": ModeDetected for the new mode fires BEFORE DecodeRestarted for the
        // abandoned one (AVT resolving within the same call, or ForceMode into AVT) -- by the time
        // OnDecodeRestarted runs, live _currentMode/_lastImage already describe the NEW mode, so the
        // abandoned image's data must come from the OnModeDetected-side stash instead.
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var abandonedMode = MakeMode(imageHeight: 100, modeId: "abandoned-mode");
        var newMode = MakeMode(imageHeight: 50, modeId: "new-mode");
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(abandonedMode);
        for (var line = 0; line < 66; line++) // >= 65% of 100
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        decoder.RaiseModeDetected(newMode); // fires FIRST -- stashes the abandoned mode's state
        decoder.RaiseDecodeRestarted(abandonedMode); // fires AFTER -- must read the stash, not live state

        var entry = await historyStore.WaitForRecordAsync();
        Assert.Equal(abandonedMode.Id, entry.ModeId); // the ABANDONED mode's id, not the new one's
        Assert.Single(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task DecodeRestarted_MinorityOrdering_TheNewImage_StillRecordsOnceItCompletes()
    {
        // The bug this whole design exists to fix (round-1 auditor finding, real but narrower than
        // first framed): only in this minority ordering does OnModeDetected's reset run BEFORE
        // OnDecodeRestarted, so a naive `_recordedForCurrentImage = true` in OnDecodeRestarted would
        // incorrectly mark the NEW (not-yet-decoded) image as already-handled, permanently preventing
        // it from ever being recorded once it completes. Confirmed via revert-fix-confirm-fail during
        // development (reverting to the unconditional flag-set made this specific test fail while the
        // dominant-ordering equivalent still passed).
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var abandonedMode = MakeMode(imageHeight: 100, modeId: "abandoned-mode");
        var newMode = MakeMode(imageHeight: 4, modeId: "new-mode"); // small, single-scan-segment
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(abandonedMode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, FakeImage)); // well below 65% -- no abandoned save

        decoder.RaiseModeDetected(newMode); // minority ordering
        decoder.RaiseDecodeRestarted(abandonedMode);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, FakeImage));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(2, FakeImage));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(3, FakeImage)); // last line: 3 + step(1) >= 4

        var entry = await historyStore.WaitForRecordAsync();
        Assert.Equal(newMode.Id, entry.ModeId);
        Assert.Single(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task DecodeRestarted_TwoBackToBackMinorityOrderingRestarts_SecondNeverConsumesTheFirstsStash()
    {
        // The round-1 data-corruption regression: a stashed _pendingAbandon* entry from one restart
        // must never be consumed by a LATER, unrelated restart. First restart's abandoned mode is
        // BELOW threshold (must not save); second's is ABOVE threshold (must save, correctly
        // attributed to itself, not leftover state from the first).
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var firstAbandoned = MakeMode(imageHeight: 100, modeId: "first-abandoned");
        var secondAbandoned = MakeMode(imageHeight: 100, modeId: "second-abandoned");
        var finalMode = MakeMode(imageHeight: 50, modeId: "final-mode");
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        // First restart: minority ordering, well below threshold.
        decoder.RaiseModeDetected(firstAbandoned);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, FakeImage));
        decoder.RaiseModeDetected(secondAbandoned); // stashes firstAbandoned (1 line, well below 65%)
        decoder.RaiseDecodeRestarted(firstAbandoned);

        // Second restart: minority ordering, above threshold.
        for (var line = 0; line < 66; line++)
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        decoder.RaiseModeDetected(finalMode); // stashes secondAbandoned (66 lines, above 65%)
        decoder.RaiseDecodeRestarted(secondAbandoned);

        var entry = await historyStore.WaitForRecordAsync();
        Assert.Equal(secondAbandoned.Id, entry.ModeId); // never firstAbandoned's stale, below-threshold stash
        Assert.Single(historyStore.RecordedEntries);
    }

    [Fact]
    public async Task DecodeRestarted_MinorityOrdering_SameModeInstance_StillSavesAbandoned_AndKeepsRecordingTheNewImage()
    {
        // Code-level review finding: mode IDENTITY alone cannot distinguish the dominant ordering
        // from the minority one when the newly-detected mode is the SAME SstvModeDefinition instance
        // as the abandoned one -- reachable via AVT-into-AVT (a second AVT lock found mid-training,
        // or ForceMode(Avt) while an AVT image is already decoding). `_currentMode == abandonedMode`
        // alone would wrongly take the dominant branch here (live state already describes the NEW,
        // empty image by this point), losing the abandoned save AND permanently suppressing the new
        // image's own eventual completed-image record. The stash-first check (this method's own
        // OnDecodeRestarted doc comment) is what actually distinguishes these two cases.
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 100, modeId: "avt");
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        for (var line = 0; line < 66; line++) // >= 65%
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        decoder.RaiseModeDetected(mode); // SAME instance -- minority ordering
        decoder.RaiseDecodeRestarted(mode);

        var abandoned = await historyStore.WaitForRecordAsync();
        Assert.Contains("_partial", abandoned.FilePath);

        // ...and the NEW image (same mode id, but a fresh reception) must still be recordable once it
        // completes -- the bug this test guards against permanently blocked this.
        for (var line = 0; line < 100; line++)
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        await Task.Delay(200);
        Assert.Equal(2, historyStore.RecordedEntries.Count);
        Assert.Single(historyStore.RecordedEntries, e => !e.FilePath.Contains("_partial"));
    }

    [Fact]
    public async Task DecodeRestarted_ImmediatelyAfterTheFinalLine_DoesNotRecordTheCompletedImageAgainAsPartial()
    {
        // Code-level review finding: AnalogFmSstvDecoder.cs's mid-reception restart check sits at the
        // bottom of the per-line loop with no guard against having just decoded the FINAL line, so
        // DecodeRestarted can fire for an image that already completed and was already queued for a
        // normal save. Legacy doesn't double-save either -- m_ReqSave's own check is gated on m_Sync
        // (sstv.cpp:2134), already cleared by Stop() for a finished image.
        //
        // This exact ordering is also what the completed-image save used to get wrong: reading
        // IReceivedImageBuffer.Current asynchronously (after the settings-directory I/O awaited
        // inside RecordCompletedImageAsync) raced against this DecodeRestarted wiping Current to a
        // 1x1 empty placeholder first, silently saving a black 1x1 PNG instead of the real image.
        // The assertions below check the saved file's actual dimensions/pixels, not just that a
        // history row exists, so that regression can't come back unnoticed.
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 4);
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        for (var line = 0; line < 4; line++)
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, ColoredFakeImage));
        }

        var recorded = await historyStore.WaitForRecordAsync();
        decoder.RaiseDecodeRestarted(mode); // fires again for the mode that JUST completed

        await Task.Delay(200);
        Assert.Single(historyStore.RecordedEntries);
        Assert.DoesNotContain(historyStore.RecordedEntries, e => e.FilePath.Contains("_partial"));
        AssertSavedFileMatchesColoredFakeImage(recorded.FilePath);
    }

    [Fact]
    public async Task DecodeRestarted_AfterCompletion_SameModeInstance_StillRecordsTheNextImage()
    {
        // Third round-3 code-level review finding: AnalogFmSstvDecoder.cs's mid-reception restart
        // check runs after the FINAL line too (see the sibling test above), and an AVT match there
        // can resolve training within the same call -- so ModeDetected(Avt) can fire BEFORE
        // DecodeRestarted(Avt), with the SAME SstvModeRegistry.Avt instance, for an image that already
        // completed. An earlier fix attempt gated the OnModeDetected-side stash on "not yet handled",
        // which left the stash empty here and sent OnDecodeRestarted into the live-state branch --
        // poisoning _recordedForCurrentImage against the BRAND NEW image and permanently blocking it.
        // The stash must be populated unconditionally (its own _pendingAbandonRecorded flag carries
        // the "already handled" fact instead) for this to resolve correctly.
        var decoder = new FakeSstvDecoder();
        var historyStore = new FakeReceiveHistoryStore();
        var mode = MakeMode(imageHeight: 4, modeId: "avt");
        var receivedImage = new FakeReceivedImageBuffer();
        _ = new ReceiveHistoryRecorder(decoder, receivedImage, historyStore, TempImagesDirectorySettings(), NullLogger<ReceiveHistoryRecorder>.Instance);

        decoder.RaiseModeDetected(mode);
        for (var line = 0; line < 4; line++)
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        _ = await historyStore.WaitForRecordAsync(); // completed image #1

        decoder.RaiseModeDetected(mode); // same instance, fires FIRST
        decoder.RaiseDecodeRestarted(mode); // for the already-completed image

        for (var line = 0; line < 4; line++) // image #2 completes normally
        {
            decoder.RaiseLineDecoded(new DecodedImageUpdate(line, FakeImage));
        }

        await Task.Delay(200);
        Assert.Equal(2, historyStore.RecordedEntries.Count);
        Assert.DoesNotContain(historyStore.RecordedEntries, e => e.FilePath.Contains("_partial"));
    }

    // Both the completed- and abandoned-image paths write a real PNG straight from the captured
    // pixel snapshot (neither goes through IReceivedImageBuffer.SaveAsync -- see
    // RecordCompletedImageAsync's and RecordAbandonedImageAsync's own doc comments for why) --
    // without redirecting ReceiveHistorySettings.ImagesDirectory, every run of the tests above
    // would litter the machine's real Pictures folder via ReceiveHistorySettings.ResolveDirectoryAsync's
    // default.
    private static FakeSettingsStore TempImagesDirectorySettings() => new()
    {
        Settings = new AppSettings().WithSection(
            ReceiveHistorySettings.SectionKey,
            new ReceiveHistorySettings
            {
                ImagesDirectory = Path.Combine(Path.GetTempPath(), "ScanlineStudioTests", Guid.NewGuid().ToString("N")),
            },
            ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
    };

    private static SstvModeDefinition MakeMode(int imageHeight, string modeId = "test-mode") => new(
        Id: modeId,
        DisplayName: "Test Mode",
        VisCode: 1,
        ImageWidth: 4,
        ImageHeight: imageHeight,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static readonly IImageSource FakeImage = new FixedSizeImageSource(4, 4);

    // Deliberately not all-zero: a saved PNG that is the 1x1 black placeholder
    // ReceivedImageBuffer.OnDecodeRestarted wipes IReceivedImageBuffer.Current to would (bug this
    // chunk found) also read as "black," so a plain zero-filled fake could not distinguish the
    // regression from a correct save. This color could never come from that placeholder.
    private static readonly Rgb24 KnownColor = new(200, 100, 50);
    private static readonly IImageSource ColoredFakeImage = new FixedSizeImageSource(4, 4, KnownColor);

    private static void AssertSavedFileMatchesColoredFakeImage(string filePath)
    {
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(filePath);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (var pixel in accessor.GetRowSpan(y))
                {
                    Assert.Equal(KnownColor.R, pixel.R);
                    Assert.Equal(KnownColor.G, pixel.G);
                    Assert.Equal(KnownColor.B, pixel.B);
                }
            }
        });
    }

    private sealed class FixedSizeImageSource(int width, int height, Rgb24 fill = default) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => Enumerable.Repeat(fill, Width).ToArray();
    }
}
