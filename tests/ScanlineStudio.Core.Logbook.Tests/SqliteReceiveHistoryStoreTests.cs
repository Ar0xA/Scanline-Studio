using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class SqliteReceiveHistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_ThenQueryAsync_RoundTripsTheEntry()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null);

            await store.RecordAsync(entry);
            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            var loaded = Assert.Single(results);
            Assert.Equal(entry.Id, loaded.Id);
            Assert.Equal(entry.ModeId, loaded.ModeId);
            Assert.Equal(entry.FilePath, loaded.FilePath);
            Assert.Null(loaded.LinkedQsoId);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_FiltersByModeId()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null));
            await store.RecordAsync(new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "martin1", "/tmp/b.png", null));

            var results = await store.QueryAsync(new ReceiveHistoryFilter(ModeId: "martin1"));

            var loaded = Assert.Single(results);
            Assert.Equal("2", loaded.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_FiltersByDateRange()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(dbPath);
            var old = DateTimeOffset.UtcNow.AddDays(-10);
            var recent = DateTimeOffset.UtcNow;
            await store.RecordAsync(new ReceiveHistoryEntry("old", old, "robot36", "/tmp/a.png", null));
            await store.RecordAsync(new ReceiveHistoryEntry("recent", recent, "robot36", "/tmp/b.png", null));

            var results = await store.QueryAsync(new ReceiveHistoryFilter(From: DateTimeOffset.UtcNow.AddDays(-1)));

            var loaded = Assert.Single(results);
            Assert.Equal("recent", loaded.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_OrdersMostRecentFirst()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(dbPath);
            var now = DateTimeOffset.UtcNow;
            await store.RecordAsync(new ReceiveHistoryEntry("first", now.AddMinutes(-5), "robot36", "/tmp/a.png", null));
            await store.RecordAsync(new ReceiveHistoryEntry("second", now, "robot36", "/tmp/b.png", null));

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(["second", "first"], results.Select(r => r.Id));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task LoadThumbnailAsync_FitsWithinMaxDimensionPreservingAspectRatio()
    {
        var dbPath = TempDbPath();
        var imagePath = Path.Combine(Path.GetTempPath(), $"scanline-studio-history-thumb-test-{Guid.NewGuid()}.png");
        try
        {
            using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(8, 4))
            {
                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < 4; y++)
                    {
                        accessor.GetRowSpan(y).Fill(new SixLabors.ImageSharp.PixelFormats.Rgb24(9, 99, 199));
                    }
                });
                await image.SaveAsPngAsync(imagePath);
            }

            var store = new SqliteReceiveHistoryStore(dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", imagePath, null);

            IImageSource thumbnail = await store.LoadThumbnailAsync(entry, maxDimension: 4);

            Assert.Equal(4, thumbnail.Width);
            Assert.Equal(2, thumbnail.Height);
            var pixel = thumbnail.GetScanline(0)[0];
            Assert.Equal(9, pixel.R);
            Assert.Equal(99, pixel.G);
            Assert.Equal(199, pixel.B);
        }
        finally
        {
            DeleteDb(dbPath);
            File.Delete(imagePath);
        }
    }

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"scanline-studio-history-test-{Guid.NewGuid()}.db");

    private static void DeleteDb(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
