using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

public sealed class DefaultTemplateSeederTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scanlinestudio-default-template-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSettingsStore _settings = new();
    private readonly TemplateStore _store;

    public DefaultTemplateSeederTests()
    {
        var writer = new ImageSourceWriter();
        var preparer = new TransmitImagePreparer(Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf"));
        _store = new TemplateStore(writer, new ImageFileLoader(), preparer, NullLogger<TemplateStore>.Instance, _root);
    }

    public void Dispose()
    {
        _settings.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DefaultTemplateSeeder CreateSeeder(ITemplateStore? store = null) =>
        new(store ?? _store, _settings, new KeyEchoLocalization(), NullLogger<DefaultTemplateSeeder>.Instance);

    private static int StarterCount => DefaultTemplateSeeder.BuildStarterSet().Count;

    [Fact]
    public void StarterSet_HasTwoCqTwoReplyTwoSeventyThree()
    {
        var keys = DefaultTemplateSeeder.BuildStarterSet().Select(s => s.NameKey).ToList();

        Assert.Equal(6, keys.Count);
        Assert.Equal(2, keys.Count(k => k.StartsWith("Templates.Default.Cq", StringComparison.Ordinal)));
        Assert.Equal(2, keys.Count(k => k.StartsWith("Templates.Default.Reply", StringComparison.Ordinal)));
        Assert.Equal(2, keys.Count(k => k.StartsWith("Templates.Default.SeventyThree", StringComparison.Ordinal)));
    }

    [Fact]
    public void StarterSet_NameKeysExistInEveryLocale()
    {
        var localeDir = Path.Combine(FindRepoRoot(), "assets", "locale");
        foreach (var file in Directory.GetFiles(localeDir, "*.json").Where(f => Path.GetFileName(f) != "locales.json"))
        {
            var json = File.ReadAllText(file);
            foreach (var (key, _) in DefaultTemplateSeeder.BuildStarterSet())
            {
                Assert.Contains($"\"{key}\":", json, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task FirstRun_AddsEveryStarterAndRecordsTheSet()
    {
        var added = await CreateSeeder().SeedIfNeededAsync();

        Assert.Equal(StarterCount, added);
        var listed = await _store.ListAsync();
        Assert.Equal(StarterCount, listed.Count);
        Assert.All(listed, t => Assert.True(File.Exists(t.ThumbnailPath)));
        Assert.Equal(DefaultTemplateSeeder.BuildStarterSet().Select(x => x.NameKey).Order(StringComparer.Ordinal), HandledKeys());
    }

    [Fact]
    public async Task SeededTemplates_LoadBackWithTheirElements()
    {
        await CreateSeeder().SeedIfNeededAsync();

        var byName = (await _store.ListAsync()).ToDictionary(t => t.Name);
        foreach (var (key, elements) in DefaultTemplateSeeder.BuildStarterSet())
        {
            var loaded = await _store.LoadAsync(byName[key].Id);
            Assert.Equal(elements, loaded.Elements);
        }
    }

    [Fact]
    public async Task SecondRun_AddsNothing_EvenAfterTheUserDeletesAStarter()
    {
        await CreateSeeder().SeedIfNeededAsync();
        var first = (await _store.ListAsync())[0];
        await _store.DeleteAsync(first.Id);

        var added = await CreateSeeder().SeedIfNeededAsync();

        Assert.Equal(0, added);
        Assert.Equal(StarterCount - 1, (await _store.ListAsync()).Count);
    }

    [Fact]
    public async Task ExistingUserTemplates_AreNeverOverwritten_AndASameNamedStarterIsSkipped()
    {
        var collidingName = DefaultTemplateSeeder.BuildStarterSet()[0].NameKey;
        var userElements = new List<PersistedTemplateElement>
        {
            new PersistedTextElement(0.5, 0.5, 0.5, 0.2, 0, false, "MY OWN", 0.1, new(1, 2, 3), "Barlow", null, 0.02),
        };
        var ownId = _store.CreateTemplateId("My own");
        await _store.SaveAsync(ownId, "My own", new PersistedTemplateDocument(userElements));
        var collidingId = _store.CreateTemplateId(collidingName);
        await _store.SaveAsync(collidingId, collidingName, new PersistedTemplateDocument(userElements));
        var ownBefore = await File.ReadAllBytesAsync(Path.Combine(_root, ownId, "template.json"));
        var collidingBefore = await File.ReadAllBytesAsync(Path.Combine(_root, collidingId, "template.json"));

        var added = await CreateSeeder().SeedIfNeededAsync();

        Assert.Equal(StarterCount - 1, added);
        Assert.Equal(StarterCount + 1, (await _store.ListAsync()).Count);
        Assert.Equal(ownBefore, await File.ReadAllBytesAsync(Path.Combine(_root, ownId, "template.json")));
        Assert.Equal(collidingBefore, await File.ReadAllBytesAsync(Path.Combine(_root, collidingId, "template.json")));
    }

    [Fact]
    public async Task FailedSave_IsRetriedNextLaunch_AndOnlyTheMissingOneIsAdded()
    {
        var firstAdded = await CreateSeeder(new FailFirstSaveStore(_store)).SeedIfNeededAsync();

        Assert.Equal(StarterCount - 1, firstAdded);
        Assert.Equal(StarterCount - 1, HandledKeys().Length);

        var retryAdded = await CreateSeeder().SeedIfNeededAsync();

        Assert.Equal(1, retryAdded);
        var names = (await _store.ListAsync()).Select(t => t.Name).ToList();
        Assert.Equal(StarterCount, names.Count);
        Assert.Equal(StarterCount, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AfterAPartialFailure_ADeletedStarterDoesNotComeBack()
    {
        await CreateSeeder(new FailFirstSaveStore(_store)).SeedIfNeededAsync();
        var deleted = (await _store.ListAsync())[0];
        await _store.DeleteAsync(deleted.Id);

        await CreateSeeder().SeedIfNeededAsync();

        var names = (await _store.ListAsync()).Select(t => t.Name).ToList();
        Assert.DoesNotContain(deleted.Name, names);
        Assert.Equal(StarterCount - 1, names.Count);
    }

    [Fact]
    public async Task AfterAPartialFailure_ARenamedStarterIsNotDuplicated()
    {
        await CreateSeeder(new FailFirstSaveStore(_store)).SeedIfNeededAsync();
        var renamed = (await _store.ListAsync())[0];
        await _store.RenameAsync(renamed.Id, "Mine now");

        await CreateSeeder().SeedIfNeededAsync();

        var names = (await _store.ListAsync()).Select(t => t.Name).ToList();
        Assert.DoesNotContain(renamed.Name, names);
        Assert.Equal(StarterCount, names.Count);
    }

    [Fact]
    public async Task ANewStarterKey_IsTheOnlyOneAddedForAnUpgradingUser()
    {
        var keys = DefaultTemplateSeeder.BuildStarterSet().Select(x => x.NameKey).ToArray();
        _settings.Settings = _settings.Settings.WithSection(DefaultTemplatesSettings.SectionKey,
            new DefaultTemplatesSettings { HandledNameKeys = keys[1..] }, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings);

        var added = await CreateSeeder().SeedIfNeededAsync();

        Assert.Equal(1, added);
        Assert.Equal(keys[0], Assert.Single(await _store.ListAsync()).Name);
    }

    [Fact]
    public async Task ASameNamedTemplate_IsMatchedIgnoringCase()
    {
        var name = DefaultTemplateSeeder.BuildStarterSet()[0].NameKey.ToUpperInvariant();
        var userElements = new List<PersistedTemplateElement> { new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new(1, 2, 3), null, 0, 1) };
        await _store.SaveAsync(_store.CreateTemplateId(name), name, new PersistedTemplateDocument(userElements));

        var added = await CreateSeeder().SeedIfNeededAsync();

        Assert.Equal(StarterCount - 1, added);
    }

    [Fact]
    public async Task UnwritableSettings_SeedNothing()
    {
        _settings.SaveAsyncExceptionOnce = new IOException("settings unavailable");

        await Assert.ThrowsAsync<IOException>(() => CreateSeeder().SeedIfNeededAsync());

        Assert.Empty(await _store.ListAsync());
    }

    private string[] HandledKeys() =>
        _settings.Settings.GetSection(DefaultTemplatesSettings.SectionKey, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings)?.HandledNameKeys ?? [];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private sealed class KeyEchoLocalization : ILocalizationService
    {
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [CultureInfo.InvariantCulture];

        public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

        public event Action? CultureChanged { add { } remove { } }

        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key, params object[] args) => key;

        public bool TryGetString(string key, out string value)
        {
            value = key;
            return true;
        }
    }

    private sealed class FailFirstSaveStore(ITemplateStore inner) : ITemplateStore
    {
        private bool _failed;

        public Task SaveAsync(string templateId, string name, PersistedTemplateDocument document, CancellationToken ct = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("disk full");
            }

            return inner.SaveAsync(templateId, name, document, ct);
        }

        public string CreateTemplateId(string name) => inner.CreateTemplateId(name);
        public string GetAssetPath(string templateId, string assetFileName) => inner.GetAssetPath(templateId, assetFileName);
        public Task<PersistedTemplateDocument> LoadAsync(string templateId, CancellationToken ct = default) => inner.LoadAsync(templateId, ct);
        public Task RenameAsync(string templateId, string newName, CancellationToken ct = default) => inner.RenameAsync(templateId, newName, ct);
        public Task<IReadOnlyList<TemplateMetadata>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task DeleteAsync(string templateId, CancellationToken ct = default) => inner.DeleteAsync(templateId, ct);
        public Task ExportAsync(string templateId, string destinationZipPath, CancellationToken ct = default) => inner.ExportAsync(templateId, destinationZipPath, ct);
        public Task<string> ImportAsync(string sourceZipPath, CancellationToken ct = default) => inner.ImportAsync(sourceZipPath, ct);
        public Task<LegacyMtmImportResult> ImportLegacyMtmAsync(string mtmPath, CancellationToken ct = default) => inner.ImportLegacyMtmAsync(mtmPath, ct);
    }
}
