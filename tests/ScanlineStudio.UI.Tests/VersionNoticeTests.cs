using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class VersionNoticeTests
{
    private static VersionNoticeService CreateService(FakeSettingsStore store, string version = "0.9.2-beta+abc123") =>
        new(store, NullLogger<VersionNoticeService>.Instance, version);

    private static AppSettings WithDismissed(string version) =>
        new AppSettings().WithSection(
            VersionNoticeSettings.SectionKey,
            new VersionNoticeSettings { DismissedForVersion = version },
            VersionNoticeSettingsJsonContext.Default.VersionNoticeSettings);

    [Theory]
    [InlineData("0.9.2-beta+abc123", "0.9.2-beta", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0+sha", "1.0.0", false)]
    public void CurrentVersion_StripsBuildMetadata_AndDetectsPrerelease(string informational, string expected, bool prerelease)
    {
        using var store = new FakeSettingsStore();
        var service = CreateService(store, informational);

        Assert.Equal(expected, service.CurrentVersion);
        Assert.Equal(prerelease, service.IsPrerelease);
    }

    [Fact]
    public async Task ShouldShow_NothingDismissed_True()
    {
        using var store = new FakeSettingsStore();

        Assert.True(await CreateService(store).ShouldShowAsync());
    }

    [Fact]
    public async Task ShouldShow_ThisVersionDismissed_False()
    {
        using var store = new FakeSettingsStore { Settings = WithDismissed("0.9.2-beta") };

        Assert.False(await CreateService(store).ShouldShowAsync());
    }

    [Theory]
    [InlineData("0.9.1-beta")]
    [InlineData("0.9.2-beta+abc123")]
    public async Task ShouldShow_OtherVersionDismissed_True(string dismissed)
    {
        using var store = new FakeSettingsStore { Settings = WithDismissed(dismissed) };

        Assert.True(await CreateService(store).ShouldShowAsync());
    }

    [Fact]
    public async Task ShouldShow_ReleaseVersionNotDismissed_True()
    {
        using var store = new FakeSettingsStore();

        Assert.True(await CreateService(store, "1.0.0").ShouldShowAsync());
    }

    [Fact]
    public async Task ShouldShow_UnknownVersion_False()
    {
        using var store = new FakeSettingsStore();

        Assert.False(await CreateService(store, "unknown").ShouldShowAsync());
    }

    [Fact]
    public async Task ShouldShow_SettingsLoadFails_True()
    {
        using var store = new FakeSettingsStore { LoadAsyncException = new IOException("disk") };

        Assert.True(await CreateService(store).ShouldShowAsync());
    }

    [Fact]
    public async Task Dismiss_PersistsCurrentVersion_SoTheNoticeStaysHidden()
    {
        using var store = new FakeSettingsStore();
        var service = CreateService(store);

        await service.DismissCurrentVersionAsync();

        var section = store.Settings.GetSection(VersionNoticeSettings.SectionKey, VersionNoticeSettingsJsonContext.Default.VersionNoticeSettings);
        Assert.Equal("0.9.2-beta", section?.DismissedForVersion);
        Assert.False(await service.ShouldShowAsync());
    }

    [Fact]
    public async Task Dismiss_SettingsWriteFails_DoesNotThrow()
    {
        using var store = new FakeSettingsStore { LoadAsyncException = new IOException("disk") };

        await CreateService(store).DismissCurrentVersionAsync();
    }

    [Fact]
    public void DialogViewModel_ShowsSummaryOnlyWhenPresent_AndBetaOnlyForPrerelease()
    {
        var localization = new FakeLocalizationService();
        localization.OptionalStrings[VersionNoticeDialogViewModel.ReleaseSummaryKeyPrefix + "2.0.0"] = "• First\n\n- Second\nThird ";

        var release = new VersionNoticeDialogViewModel(localization, "2.0.0", isPrerelease: false);
        var beta = new VersionNoticeDialogViewModel(localization, "2.1.0-beta", isPrerelease: true);

        Assert.True(release.ShowReleaseSummary);
        Assert.Equal(["First", "Second", "Third"], release.ReleaseItems);
        Assert.False(release.ShowBeta);
        Assert.False(beta.ShowReleaseSummary);
        Assert.True(beta.ShowBeta);
    }

    [Fact]
    public void DialogViewModel_FeedbackText_UsesMenuPathsWithoutAccessKeyMarker()
    {
        var localization = new FakeLocalizationService();

        _ = new VersionNoticeDialogViewModel(localization, "1.0.0", isPrerelease: false);

        var feedback = localization.Calls.Single(c => c.Key == "VersionNotice.Feedback");
        Assert.Equal(["MainWindow.Menu.Help → MainWindow.Menu.Help.ReportBug", "MainWindow.Menu.Help → MainWindow.Menu.Help.SendFeedback"], feedback.Args);
    }

    [Fact]
    public void BundledLocale_HasReleaseSummaryForTheBuiltVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var props = File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Build.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        var en = File.ReadAllText(Path.Combine(dir.FullName, "assets", "locale", "en.json"));

        Assert.Contains($"\"{VersionNoticeDialogViewModel.ReleaseSummaryKeyPrefix}{version}\"", en, StringComparison.Ordinal);
    }
}
