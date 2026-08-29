namespace ScanlineStudio.Host.Tests;

public sealed class HelpContentPackagingTests
{
    [Fact]
    public void OfflineUserGuide_IsPackagedWithAllRuntimeAssets()
    {
        var helpDirectory = Path.Combine(AppContext.BaseDirectory, "help");
        var expectedFiles = new[] { "index.html", "styles.css", "help-search.js", "help.js" };

        foreach (var fileName in expectedFiles)
        {
            Assert.True(
                File.Exists(Path.Combine(helpDirectory, fileName)),
                $"Packaged help asset is missing: help/{fileName}");
        }

        var index = File.ReadAllText(Path.Combine(helpDirectory, "index.html"));
        Assert.Contains("<title>Scanline Studio User Guide</title>", index, StringComparison.Ordinal);
        Assert.Contains("src=\"help-search.js\"", index, StringComparison.Ordinal);
        Assert.Contains("src=\"help.js\"", index, StringComparison.Ordinal);
        Assert.Contains("href=\"styles.css\"", index, StringComparison.Ordinal);
    }
}
