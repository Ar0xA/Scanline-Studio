using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

/// <summary>BACKLOG.md R7: <see cref="HelpGuidePathResolver.Resolve"/> is the pure path-resolution
/// logic behind <c>MainViewModel.UserGuidePath</c>, tested against a temp directory rather than
/// the real <see cref="AppContext.BaseDirectory"/>.</summary>
public sealed class HelpGuidePathResolverTests
{
    [Fact]
    public void EnglishCulture_ResolvesRootIndex()
    {
        var root = Directory.CreateTempSubdirectory("help-resolver-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "en");

            var resolved = HelpGuidePathResolver.Resolve(root, "en");

            Assert.Equal(Path.Combine(root, "index.html"), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TranslatedGuidePresent_ResolvesLanguageSubdirectory()
    {
        var root = Directory.CreateTempSubdirectory("help-resolver-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "en");
            Directory.CreateDirectory(Path.Combine(root, "ja"));
            File.WriteAllText(Path.Combine(root, "ja", "index.html"), "ja");

            var resolved = HelpGuidePathResolver.Resolve(root, "ja");

            Assert.Equal(Path.Combine(root, "ja", "index.html"), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TranslatedGuideMissing_FallsBackToEnglish()
    {
        var root = Directory.CreateTempSubdirectory("help-resolver-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "en");

            var resolved = HelpGuidePathResolver.Resolve(root, "ja");

            Assert.Equal(Path.Combine(root, "index.html"), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
