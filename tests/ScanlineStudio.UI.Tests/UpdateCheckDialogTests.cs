using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class UpdateCheckDialogTests
{
    private static readonly Uri ReleaseUrl = new("https://github.com/Ar0xA/Scanline-Studio/releases/tag/v1.0.1-beta");

    private static UpdateCheckResult Available(string version = "1.0.1-beta") =>
        new(UpdateCheckStatus.UpdateAvailable, AppVersion.TryParse(version), ReleaseUrl);

    private static UpdateCheckDialogViewModel Create(UpdateCheckResult result, UpdatePreferences initial, FakeUrlLauncher? launcher = null) =>
        new(new FakeLocalizationService(), launcher ?? new FakeUrlLauncher(), result, "1.0.0-beta", initial);

    [Fact]
    public void Available_ShowsDownloadAndSkip_AndOpensTheReleasePage()
    {
        var launcher = new FakeUrlLauncher();
        var dialog = Create(Available(), new(false, null), launcher);

        dialog.OpenPageCommand.Execute(null);

        Assert.True(dialog.ShowOpenPage);
        Assert.True(dialog.ShowSkipVersion);
        Assert.Equal("UpdateCheck.OpenDownloadPage", dialog.OpenPageLabel);
        Assert.Equal([ReleaseUrl.AbsoluteUri], launcher.OpenedUrls);
    }

    [Fact]
    public void UpToDate_HidesDownloadAndSkip_KeepsNeverCheckbox()
    {
        var dialog = Create(new(UpdateCheckStatus.UpToDate), new(true, "0.9.0"));

        Assert.False(dialog.ShowOpenPage);
        Assert.False(dialog.ShowSkipVersion);
        Assert.True(dialog.NeverNotify);
        Assert.Equal(new UpdatePreferences(true, "0.9.0"), dialog.ToPreferences());
    }

    [Fact]
    public void Failed_ShowsReleasesPageLink_AndLocalizedReason()
    {
        var dialog = Create(new(UpdateCheckStatus.Failed, ReleasePageUrl: new Uri("https://github.com/Ar0xA/Scanline-Studio/releases"), Failure: UpdateCheckFailure.Timeout), new(false, null));

        Assert.True(dialog.ShowOpenPage);
        Assert.Equal("UpdateCheck.OpenReleasesPage", dialog.OpenPageLabel);
        Assert.Equal("UpdateCheck.Failed.Timeout", dialog.Message);
    }

    [Theory]
    [InlineData(null, true, "1.0.1-beta")]
    [InlineData("1.0.1-beta", false, null)]
    [InlineData("0.9.0", false, "0.9.0")]
    [InlineData("0.9.0", true, "1.0.1-beta")]
    public void SkipCheckbox_UpdatesOnlyThisVersionsSkip(string? initialSkipped, bool tick, string? expectedSkipped)
    {
        var dialog = Create(Available(), new(false, initialSkipped));
        dialog.SkipThisVersion = tick;

        Assert.Equal(expectedSkipped, dialog.ToPreferences().SkippedVersion);
    }

    [Fact]
    public void SkipCheckbox_StartsTickedWhenThisVersionWasSkipped()
    {
        Assert.True(Create(Available(), new(false, "1.0.1-beta")).SkipThisVersion);
    }

    [Fact]
    public async Task PreferencesService_RoundTrips_AndDefaultsWhenMissing()
    {
        using var store = new FakeSettingsStore();
        var service = new UpdatePreferencesService(store, NullLogger<UpdatePreferencesService>.Instance);

        Assert.Equal(new UpdatePreferences(UpdateCheckSettings.DefaultNeverNotify, null), await service.LoadAsync());

        await service.SaveAsync(new(true, "1.0.1-beta"));

        Assert.Equal(new UpdatePreferences(true, "1.0.1-beta"), await service.LoadAsync());
    }

    [Fact]
    public async Task PreferencesService_LoadFailure_ReturnsDefaults_SaveFailureDoesNotThrow()
    {
        using var store = new FakeSettingsStore { LoadAsyncException = new IOException("disk") };
        var service = new UpdatePreferencesService(store, NullLogger<UpdatePreferencesService>.Instance);

        Assert.Equal(new UpdatePreferences(false, null), await service.LoadAsync());
        await service.SaveAsync(new(true, null));
    }
}
