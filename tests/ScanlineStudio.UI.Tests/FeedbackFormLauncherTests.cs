using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

public sealed class FeedbackFormLauncherTests : IDisposable
{
    // SHA-256 of the real web3forms access key: the plain key must never be committed.
    private const string AccessKeySha256 = "7c83c66b86c55348de8f6f3d49dfd82118a55162acd14b0bd83f07cac5b36c31";

    private readonly string _root = Directory.CreateTempSubdirectory("feedback-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AssetDir => Path.Combine(_root, "assets");

    private string OutputDir => Path.Combine(_root, "out");

    private void WriteAssets(string template = "<p>{{APP_VERSION}}|{{OS}}|{{RUNTIME}}</p><style>{{FORM_STYLE}}</style><script>{{FORM_SCRIPT}}</script>")
    {
        Directory.CreateDirectory(AssetDir);
        File.WriteAllText(Path.Combine(AssetDir, "bug.html"), "BUG " + template);
        File.WriteAllText(Path.Combine(AssetDir, "feedback.html"), "FEEDBACK " + template);
        File.WriteAllText(Path.Combine(AssetDir, "form.css"), "body{}");
        File.WriteAllText(Path.Combine(AssetDir, "form.js"), "var x = 1;");
    }

    private FeedbackFormLauncher CreateLauncher(FakeUrlLauncher urlLauncher, string language = "en", string version = "1.2.3") =>
        new(urlLauncher, new FixedCultureLocalization(language), NullLogger<FeedbackFormLauncher>.Instance,
            AssetDir, OutputDir, version, "Linux x64", ".NET 8.0.1");

    [Theory]
    [InlineData(FeedbackForm.Bug, "bug.html", "BUG ")]
    [InlineData(FeedbackForm.Feedback, "feedback.html", "FEEDBACK ")]
    public async Task OpenAsync_WritesFilledCopy_AndOpensIt(FeedbackForm form, string fileName, string marker)
    {
        WriteAssets();
        var urlLauncher = new FakeUrlLauncher();

        await CreateLauncher(urlLauncher).OpenAsync(form);

        var expectedPath = Path.Combine(OutputDir, fileName);
        Assert.Equal([expectedPath], urlLauncher.OpenedUrls);
        var page = await File.ReadAllTextAsync(expectedPath);
        Assert.Equal(marker + "<p>1.2.3|Linux x64|.NET 8.0.1</p><style>body{}</style><script>var x = 1;</script>", page);
    }

    [Fact]
    public async Task OpenAsync_HtmlEncodesReportedValues()
    {
        WriteAssets();
        var urlLauncher = new FakeUrlLauncher();

        await CreateLauncher(urlLauncher, version: "<b>\"x\"&").OpenAsync(FeedbackForm.Bug);

        var page = await File.ReadAllTextAsync(urlLauncher.OpenedUrls.Single());
        Assert.Contains("&lt;b&gt;&quot;x&quot;&amp;", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_UsesTranslatedTemplate_WhenPresent()
    {
        WriteAssets();
        Directory.CreateDirectory(Path.Combine(AssetDir, "de"));
        File.WriteAllText(Path.Combine(AssetDir, "de", "bug.html"), "DE {{APP_VERSION}}");
        var urlLauncher = new FakeUrlLauncher();

        await CreateLauncher(urlLauncher, language: "de").OpenAsync(FeedbackForm.Bug);

        Assert.Equal("DE 1.2.3", await File.ReadAllTextAsync(urlLauncher.OpenedUrls.Single()));
    }

    [Fact]
    public async Task OpenAsync_FallsBackToEnglish_WhenTranslationMissing()
    {
        WriteAssets();
        var urlLauncher = new FakeUrlLauncher();

        await CreateLauncher(urlLauncher, language: "de").OpenAsync(FeedbackForm.Feedback);

        Assert.StartsWith("FEEDBACK ", await File.ReadAllTextAsync(urlLauncher.OpenedUrls.Single()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_MissingAssets_DoesNotThrowOrOpen()
    {
        var urlLauncher = new FakeUrlLauncher();

        await CreateLauncher(urlLauncher).OpenAsync(FeedbackForm.Bug);

        Assert.Empty(urlLauncher.OpenedUrls);
    }

    [Fact]
    public void DefaultOutputDirectory_OnLinux_HasNoHiddenFolder()
    {
        var path = FeedbackFormLauncher.DefaultOutputDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(Path.IsPathFullyQualified(path));
        if (OperatingSystem.IsLinux() && path.StartsWith(home, StringComparison.Ordinal))
        {
            Assert.DoesNotContain(
                Path.GetRelativePath(home, path).Split(Path.DirectorySeparatorChar),
                segment => segment.StartsWith('.'));
        }
    }

    [Fact]
    public async Task OpenAsync_CancelledToken_Throws()
    {
        WriteAssets();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateLauncher(new FakeUrlLauncher()).OpenAsync(FeedbackForm.Bug, cts.Token));
    }

    private static string RepoFeedbackDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "assets", "feedback");
    }

    [Theory]
    [InlineData("bug.html")]
    [InlineData("feedback.html")]
    public void BundledTemplate_HasEveryPlaceholder_AndFillsCompletely(string fileName)
    {
        var dir = RepoFeedbackDir();
        var template = File.ReadAllText(Path.Combine(dir, fileName));
        foreach (var placeholder in new[] { "{{APP_VERSION}}", "{{OS}}", "{{RUNTIME}}", "{{FORM_STYLE}}", "{{FORM_SCRIPT}}" })
        {
            Assert.Contains(placeholder, template, StringComparison.Ordinal);
        }

        var filled = FeedbackFormLauncher.Fill(
            template,
            File.ReadAllText(Path.Combine(dir, "form.css")),
            File.ReadAllText(Path.Combine(dir, "form.js")),
            "v", "os", "rt");
        Assert.DoesNotContain("{{", filled, StringComparison.Ordinal);
        Assert.Contains("https://api.web3forms.com/submit", filled, StringComparison.Ordinal);
    }

    [Fact]
    public void BundledScript_NeverClosesTheInlineScriptTag()
    {
        var script = File.ReadAllText(Path.Combine(RepoFeedbackDir(), "form.js"));
        Assert.DoesNotContain("</script", script, StringComparison.OrdinalIgnoreCase);
    }

    // Mirrors form.js's unmask(); the bundled check-feedback.mjs runs the real JS against the same hash.
    [Fact]
    public void BundledScript_MaskedKeyDecodesToTheRealKey_AndNoFileHoldsItInPlainText()
    {
        var dir = RepoFeedbackDir();
        var script = File.ReadAllText(Path.Combine(dir, "form.js"));
        var mask = System.Text.RegularExpressions.Regex
            .Matches(script[..script.IndexOf("];", StringComparison.Ordinal)], "0x([0-9a-f]{2})")
            .Select(m => Convert.ToByte(m.Groups[1].Value, 16))
            .ToArray();
        var blob = System.Text.RegularExpressions.Regex.Match(script, "KEY_BLOB = \"([^\"]+)\"").Groups[1].Value;
        var masked = Convert.FromBase64String(blob);
        var key = new string(masked.Select((b, i) => (char)(b ^ mask[i % mask.Length])).ToArray());

        Assert.Equal(AccessKeySha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(key, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private sealed class FixedCultureLocalization(string language) : ILocalizationService
    {
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [CultureInfo.GetCultureInfo(language)];

        public CultureInfo CurrentCulture { get; } = CultureInfo.GetCultureInfo(language);

#pragma warning disable CS0067
        public event Action? CultureChanged;
#pragma warning restore CS0067

        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key, params object[] args) => key;

        public bool TryGetString(string key, out string value)
        {
            value = string.Empty;
            return false;
        }
    }
}
