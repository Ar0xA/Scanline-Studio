using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>SQLite-backed <see cref="IReceiveHistoryStore"/> — see spec/07-image-pipeline.md's "RX
/// history" section. Thumbnail decode mirrors <c>ScanlineStudio.Core.Imaging</c>'s
/// <c>ImageFileLoader</c>/<c>StockImageLibrary</c> explicit-field-copy pixel pattern (never
/// <c>MemoryMarshal.Cast</c> between the two unrelated <c>Rgb24</c> types) — deliberately not
/// *sharing* code with that project, though: a <c>Core.Logbook</c> -> <c>Core.Imaging</c> reference
/// would be exactly the Core-to-Core edge <c>ReceivedImageBuffer</c>'s own doc comment (in
/// <c>Core.Imaging</c>) says must never happen, just the mirrored direction. A local
/// <see cref="IImageSource"/> holder here is cheap enough that sharing isn't worth the coupling.</summary>
public sealed class SqliteReceiveHistoryStore : IReceiveHistoryStore
{
    private readonly string _connectionString;
    private readonly ISettingsStore _settingsStore;

    public SqliteReceiveHistoryStore(ISettingsStore settingsStore, string? dbFilePath = null)
    {
        _settingsStore = settingsStore;

        var path = dbFilePath ?? GetDefaultDbFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        EnsureSchema();
    }

    public async Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ReceivedAt, ModeId, FilePath, LinkedQsoId FROM ReceiveHistory WHERE 1 = 1";

        if (filter.ModeId is not null)
        {
            command.CommandText += " AND ModeId = $modeId";
            command.Parameters.AddWithValue("$modeId", filter.ModeId);
        }

        if (filter.From is not null)
        {
            command.CommandText += " AND ReceivedAt >= $from";
            command.Parameters.AddWithValue("$from", filter.From.Value.ToString("O"));
        }

        if (filter.To is not null)
        {
            command.CommandText += " AND ReceivedAt <= $to";
            command.Parameters.AddWithValue("$to", filter.To.Value.ToString("O"));
        }

        command.CommandText += " ORDER BY ReceivedAt DESC";

        var results = new List<ReceiveHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new ReceiveHistoryEntry(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    public async Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(entry.FilePath, ct).ConfigureAwait(false);
        var (width, height) = FitWithinLongestSide(image.Width, image.Height, maxDimension);
        image.Mutate(x => x.Resize(width, height));

        var pixels = new Rgb24[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var source = row[x];
                    pixels[(y * width) + x] = new Rgb24(source.R, source.G, source.B);
                }
            }
        });

        return new HistoryThumbnailImageSource(width, height, pixels);
    }

    public async Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId)
            VALUES ($id, $receivedAt, $modeId, $filePath, $linkedQsoId)
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$receivedAt", entry.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("$modeId", entry.ModeId);
        command.Parameters.AddWithValue("$filePath", entry.FilePath);
        command.Parameters.AddWithValue("$linkedQsoId", (object?)entry.LinkedQsoId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => ReceiveHistorySettings.ResolveDirectoryAsync(_settingsStore, ct);

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ReceiveHistory (
                Id TEXT PRIMARY KEY,
                ReceivedAt TEXT NOT NULL,
                ModeId TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                LinkedQsoId TEXT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    private static (int Width, int Height) FitWithinLongestSide(int width, int height, int maxDimension)
    {
        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height);
        }

        var scale = width >= height ? (double)maxDimension / width : (double)maxDimension / height;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static string GetDefaultDbFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "ScanlineStudio", "history.db");
    }

    private sealed class HistoryThumbnailImageSource : IImageSource
    {
        private readonly Rgb24[] _pixels;

        public HistoryThumbnailImageSource(int width, int height, Rgb24[] pixels)
        {
            Width = width;
            Height = height;
            _pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public ReadOnlySpan<Rgb24> GetScanline(int y) => _pixels.AsSpan(y * Width, Width);
    }
}
