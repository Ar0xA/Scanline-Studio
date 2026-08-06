using System.Text.Json;
using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class SqliteReceiveHistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_ThenQueryAsync_RoundTripsTheEntry()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);
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
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);
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
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);
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
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);
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

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);
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

    [Fact]
    public async Task GetImagesDirectoryAsync_NoSectionConfigured_ReturnsTheDefaultPicturesFolder()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);

            var directory = await store.GetImagesDirectoryAsync();

            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "History");
            Assert.Equal(expected, directory);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task GetImagesDirectoryAsync_SectionConfigured_ReturnsTheConfiguredFolder_ForTheGalleryTabsStorageCard()
    {
        var dbPath = TempDbPath();
        try
        {
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    ReceiveHistorySettings.SectionKey,
                    new ReceiveHistorySettings { ImagesDirectory = "/custom/rx/history" },
                    ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
            };
            var store = new SqliteReceiveHistoryStore(settingsStore, dbPath);

            var directory = await store.GetImagesDirectoryAsync();

            Assert.Equal("/custom/rx/history", directory);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ExceedsDefaultRetentionLimit_DeletesOldestEntriesBeyond32()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), dbPath);
            var now = DateTimeOffset.UtcNow;

            // 33 entries, oldest to newest -- one more than the legacy-verified default of 32
            // (ReceiveHistorySettings.DefaultMaxEntries's own doc comment has the exact legacy
            // source citations).
            for (var i = 0; i < 33; i++)
            {
                await store.RecordAsync(new ReceiveHistoryEntry($"entry-{i}", now.AddMinutes(i), "robot36", $"/tmp/{i}.png", null));
            }

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(32, results.Count);
            Assert.DoesNotContain(results, r => r.Id == "entry-0");
            Assert.Contains(results, r => r.Id == "entry-32");
            Assert.Contains(results, r => r.Id == "entry-1");
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ConfiguredRetentionLimit_UsesTheConfiguredValueNotTheDefault()
    {
        var dbPath = TempDbPath();
        try
        {
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    ReceiveHistorySettings.SectionKey,
                    new ReceiveHistorySettings { MaxEntries = 2 },
                    ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
            };
            var store = new SqliteReceiveHistoryStore(settingsStore, dbPath);
            var now = DateTimeOffset.UtcNow;

            await store.RecordAsync(new ReceiveHistoryEntry("first", now, "robot36", "/tmp/a.png", null));
            await store.RecordAsync(new ReceiveHistoryEntry("second", now.AddMinutes(1), "robot36", "/tmp/b.png", null));
            await store.RecordAsync(new ReceiveHistoryEntry("third", now.AddMinutes(2), "robot36", "/tmp/c.png", null));

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(["third", "second"], results.Select(r => r.Id));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ExistingSectionPredatesTheMaxEntriesField_FallsBackTo32NotZero()
    {
        var dbPath = TempDbPath();
        try
        {
            // Simulates a settings.json saved before MaxEntries existed on this section: the JSON
            // object genuinely has no "MaxEntries" property at all (not even null) -- the exact
            // shape System.Text.Json silently defaults to the CLR default (0) for, not the
            // property initializer, per ReceiveHistorySettings.MaxEntries's own doc comment. A
            // regression here would mean every existing installation's history gets truncated to
            // zero the moment this field shipped.
            var sections = new Dictionary<string, JsonElement>
            {
                [ReceiveHistorySettings.SectionKey] = JsonDocument.Parse("""{"ImagesDirectory":"/custom/rx/history"}""").RootElement,
            };
            var settingsStore = new FakeSettingsStore { Settings = new AppSettings { Sections = sections } };
            var store = new SqliteReceiveHistoryStore(settingsStore, dbPath);
            var now = DateTimeOffset.UtcNow;

            for (var i = 0; i < 33; i++)
            {
                await store.RecordAsync(new ReceiveHistoryEntry($"entry-{i}", now.AddMinutes(i), "robot36", $"/tmp/{i}.png", null));
            }

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(32, results.Count);
        }
        finally
        {
            DeleteDb(dbPath);
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
