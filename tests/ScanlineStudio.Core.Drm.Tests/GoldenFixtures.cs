using System.Text.Json;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>Loads the QSSTV golden fixtures copied to the test output directory.</summary>
internal static class GoldenFixtures
{
    public static string TablesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "QsstvGolden", "tables");

    public static JsonDocument LoadTable(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TablesDir, fileName)));

    public static int[] Ints(this JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    public static byte[] Bits(string bits) => bits.Select(c => (byte)(c == '1' ? 1 : 0)).ToArray();

    public static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "QsstvGolden");

    /// <summary>All TX case directories (those with a manifest.json).</summary>
    public static IEnumerable<string> CaseIds() =>
        Directory.GetDirectories(GoldenDir)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)!;

    public static JsonDocument LoadManifest(string caseId) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, caseId, "manifest.json")));

    /// <summary>A stage's raw bytes split into blocks, using block_size or block_sizes from the manifest.</summary>
    public static byte[][] LoadStageBlocks(string caseId, JsonElement manifest, string stage)
    {
        JsonElement s = manifest.GetProperty("stages").GetProperty(stage);
        byte[] data = ReadStored(caseId, s.GetProperty("stored").GetString()!);
        int blocks = s.GetProperty("blocks").GetInt32();
        int[] sizes = s.TryGetProperty("block_sizes", out JsonElement bs)
            ? bs.Ints()
            : Enumerable.Repeat(s.GetProperty("block_size").GetInt32(), blocks).ToArray();
        var result = new byte[blocks][];
        int pos = 0;
        for (int i = 0; i < blocks; i++)
        {
            result[i] = data[pos..(pos + sizes[i])];
            pos += sizes[i];
        }

        if (pos != data.Length)
            throw new InvalidDataException($"{caseId}/{stage}: blocks cover {pos} of {data.Length} bytes.");
        return result;
    }

    /// <summary>Reads a stored stage file, gunzipping *.gz.</summary>
    public static byte[] ReadStored(string caseId, string stored)
    {
        string path = Path.GetFullPath(Path.Combine(GoldenDir, caseId, stored));
        if (!path.EndsWith(".gz", StringComparison.Ordinal))
            return File.ReadAllBytes(path);

        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}
