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
}
