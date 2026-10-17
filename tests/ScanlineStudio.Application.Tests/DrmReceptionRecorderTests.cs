using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;
using SixLabors.ImageSharp;
using ImageSharpRgb24 = SixLabors.ImageSharp.PixelFormats.Rgb24;

namespace ScanlineStudio.Application.Tests;

/// <summary>P7a-2 step 7: every received DRM file kept raw and never overwritten, images also in the gallery/history.</summary>
public sealed class DrmReceptionRecorderTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _images = Directory.CreateTempSubdirectory("drm-recorder-").FullName;

    public void Dispose() => Directory.Delete(_images, recursive: true);

    private string DrmDir => Path.Combine(_images, "drm");

    private sealed record Rig(DrmReceptionRecorder Recorder, FakeSstvSessionServiceForCorrelation Session, RecordingHistoryStore Store,
        GatedWriter Writer, List<DrmStoredFile> Stored);

    private Rig Create(bool codecAvailable = false)
    {
        var session = new FakeSstvSessionServiceForCorrelation();
        var settings = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ReceiveHistorySettings.SectionKey, new ReceiveHistorySettings { ImagesDirectory = _images },
                ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
        };
        var store = new RecordingHistoryStore();
        var writer = new GatedWriter();
        var decoder = new DrmImageDecoder(new StubJpeg2000Codec(codecAvailable), NullLogger<DrmImageDecoder>.Instance);
        var recorder = new DrmReceptionRecorder(session, decoder, writer, store, settings, new NoRadio(), NullLogger<DrmReceptionRecorder>.Instance);
        var stored = new List<DrmStoredFile>();
        recorder.FileStored += f => { lock (stored) stored.Add(f); };
        return new Rig(recorder, session, store, writer, stored);
    }

    private static byte[] Png()
    {
        using var image = new Image<ImageSharpRgb24>(4, 3, new ImageSharpRgb24(10, 200, 30));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] Jpeg()
    {
        using var image = new Image<ImageSharpRgb24>(8, 8, new ImageSharpRgb24(200, 10, 30));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task AnImage_IsKeptRaw_AndRecordedToHistoryAsDrm()
    {
        Rig rig = Create();
        byte[] png = Png();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("card.jp2", png));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        DrmStoredFile stored = Assert.Single(rig.Stored);
        Assert.Equal(Path.Combine(DrmDir, "card.jp2"), stored.RawPath);
        Assert.Equal(png, File.ReadAllBytes(stored.RawPath));
        ReceiveHistoryEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Same(entry, stored.HistoryEntry);
        Assert.Equal("drm", entry.ModeId);
        Assert.Equal("PA0TST", entry.DecodedCallsign);
        Assert.Equal(StationIdSources.Drm, entry.DecodedCallsignSource);

        // ReceptionId 0 is the guard RxStationIdAttacher and RxAudioAutoSaver skip on (RxStationIdAttacherTests covers the skip).
        Assert.Equal(0, entry.ReceptionId);
        Assert.Equal(_images, Path.GetDirectoryName(entry.FilePath));
        Assert.Matches(@"^\d{8}-\d{9}_drm_[0-9a-f]{8}\.png$", Path.GetFileName(entry.FilePath));
        Assert.Equal((4, 3), (rig.Writer.Written[entry.FilePath].Width, rig.Writer.Written[entry.FilePath].Height));
    }

    [Fact]
    public async Task AJp2NamedJpeg_IsDecodedFromItsContent()
    {
        Rig rig = Create();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("photo.jp2", Jpeg()));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        Assert.Single(rig.Store.Entries);
    }

    [Fact]
    public async Task ANameCollision_NeverOverwrites()
    {
        Rig rig = Create();
        Directory.CreateDirectory(DrmDir);
        File.WriteAllText(Path.Combine(DrmDir, "note.txt"), "existing");
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("note.txt", "first"u8.ToArray()));
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("note.txt", "second"u8.ToArray()));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        Assert.Equal("existing", File.ReadAllText(Path.Combine(DrmDir, "note.txt")));
        Assert.Equal(["first", "second"], rig.Stored.Select(s => File.ReadAllText(s.RawPath)));
        Assert.Equal(3, Directory.GetFiles(DrmDir).Length);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    public async Task AHostileName_StaysInsideTheFolder(string name)
    {
        Rig rig = Create();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File(name, [1, 2, 3]));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        string raw = Assert.Single(rig.Stored).RawPath;
        Assert.Equal(Path.GetFullPath(DrmDir), Path.GetDirectoryName(Path.GetFullPath(raw)));
    }

    [Fact]
    public async Task AHistoryRowFailure_KeepsTheRawFile_StillReportsIt_AndFailsTheDrain()
    {
        Rig rig = Create();
        rig.Store.ThrowOnRecord = new IOException("database locked");
        byte[] png = Png();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("card.jp2", png));

        Assert.False(await rig.Recorder.DrainAsync().WaitAsync(Wait));
        DrmStoredFile stored = Assert.Single(rig.Stored);
        Assert.Null(stored.HistoryEntry);
        Assert.Equal(png, File.ReadAllBytes(stored.RawPath));
    }

    [Fact]
    public async Task ANonImage_GoesToTheFolderOnly()
    {
        Rig rig = Create();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("chat.txt", "hello"u8.ToArray()));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        Assert.Null(Assert.Single(rig.Stored).HistoryEntry);
        Assert.Empty(rig.Store.Entries);
        Assert.Empty(rig.Writer.Written);
    }

    [Fact]
    public async Task AHybridStub_IsStoredRawOnly()
    {
        Rig rig = Create();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("de_x.jpg", Jpeg(), hybrid: true));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        Assert.Empty(rig.Store.Entries);
        Assert.True(File.Exists(Assert.Single(rig.Stored).RawPath));
    }

    [Fact]
    public async Task WithTheCodecUnavailable_AJp2IsStillKeptRaw()
    {
        Rig rig = Create(codecAvailable: false);
        byte[] jp2 = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, 0, 0, 0, 0];
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("pic.jp2", jp2));
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));

        Assert.Equal(jp2, File.ReadAllBytes(Assert.Single(rig.Stored).RawPath));
        Assert.Empty(rig.Store.Entries);
    }

    [Fact]
    public async Task Drain_WaitsForAGatedSave_AndRefusesLaterFiles()
    {
        Rig rig = Create();
        rig.Writer.Gate = new TaskCompletionSource();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("a.png", Png()));
        Task<bool> drain = rig.Recorder.DrainAsync();
        Assert.True(SpinWait.SpinUntil(() => rig.Writer.Entered, Wait));
        Assert.False(drain.IsCompleted);
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("late.txt", [1]));

        rig.Writer.Gate.SetResult();
        Assert.True(await drain.WaitAsync(Wait));
        Assert.Equal("a.png", Assert.Single(rig.Stored).File.FileName);
        Assert.False(File.Exists(Path.Combine(DrmDir, "late.txt")));
    }

    [Fact]
    public async Task TheHandler_OnlyEnqueues()
    {
        Rig rig = Create();
        rig.Writer.Gate = new TaskCompletionSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("a.png", Png()));
        rig.Session.RaiseDrmFileReceived(FakeDrmReceiver.File("b.png", Png()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        rig.Writer.Gate.SetResult();
        Assert.True(await rig.Recorder.DrainAsync().WaitAsync(Wait));
        Assert.Equal(["a.png", "b.png"], rig.Stored.Select(s => s.File.FileName));
    }

    private sealed class GatedWriter : IImageSourceWriter
    {
        public Dictionary<string, IImageSource> Written { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public volatile bool Entered;

        public async Task WritePngAsync(IImageSource source, string path, CancellationToken ct = default)
        {
            Entered = true;
            if (Gate is { } g)
                await g.Task.ConfigureAwait(false);
            lock (Written)
                Written[path] = source;
        }
    }

    private sealed class StubJpeg2000Codec(bool available) : IJpeg2000Codec
    {
        public bool IsAvailable => available;

        public string? UnavailableReason => available ? null : "library missing";

        public byte[] EncodeRgb24(ReadOnlySpan<byte> rgb24, int width, int height, int stride, int compressionRatio) => throw new NotSupportedException();

        public IImageSource Decode(ReadOnlySpan<byte> data) =>
            available ? new ArrayImageSource(2, 2, new Rgb24[4]) : throw new InvalidOperationException("library missing");
    }

    private sealed class NoRadio : IRadioStateProvider
    {
        public RadioState? Current => null;
    }

    private sealed class RecordingHistoryStore : IReceiveHistoryStore
    {
        public List<ReceiveHistoryEntry> Entries { get; } = [];

        public event Action<ReceiveHistoryEntry>? Recorded;

        public event Action<ReceiveHistoryEntry>? Deleted;

        public Exception? ThrowOnRecord { get; set; }

        public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
        {
            if (ThrowOnRecord is not null)
                return Task.FromException(ThrowOnRecord);
            lock (Entries)
                Entries.Add(entry);
            Recorded?.Invoke(entry);
            return Task.CompletedTask;
        }

        public void RaiseDeleted(ReceiveHistoryEntry entry) => Deleted?.Invoke(entry);

        public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? callsignSource, string? nrRst, string? cwId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
