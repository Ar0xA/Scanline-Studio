using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class ReceiveHistoryDrmFolderTests
{
    [Fact]
    public async Task ResolveDrmFilesDirectoryAsync_Unset_IsDrmInsideTheImagesFolder()
    {
        var store = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ReceiveHistorySettings.SectionKey,
                new ReceiveHistorySettings { ImagesDirectory = "/tmp/images" },
                ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
        };

        Assert.Equal(Path.Combine("/tmp/images", "drm"), await ReceiveHistorySettings.ResolveDrmFilesDirectoryAsync(store));
    }

    [Fact]
    public async Task ResolveDrmFilesDirectoryAsync_NoSection_FollowsTheDefaultImagesFolder()
    {
        var store = new FakeSettingsStore();

        Assert.Equal(
            Path.Combine(await ReceiveHistorySettings.ResolveDirectoryAsync(store), "drm"),
            await ReceiveHistorySettings.ResolveDrmFilesDirectoryAsync(store));
    }

    [Fact]
    public async Task ResolveDrmFilesDirectoryAsync_Set_IsUsedAsIs()
    {
        var store = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ReceiveHistorySettings.SectionKey,
                new ReceiveHistorySettings { ImagesDirectory = "/tmp/images", DrmFilesDirectory = "/tmp/drmfiles" },
                ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
        };

        Assert.Equal("/tmp/drmfiles", await ReceiveHistorySettings.ResolveDrmFilesDirectoryAsync(store));
    }
}
