using Yoniq.Abstractions.Radio;
using Yoniq.Core.Radio;
using Yoniq.Settings;

namespace Yoniq.Application.Tests;

public sealed class RadioSessionServiceTests
{
    [Fact]
    public async Task ConnectUsingSettingsAsync_NoSectionConfigured_ConnectsWithNoneConnectionSpec()
    {
        var controller = new FakeRadioController();
        var settingsStore = new FakeSettingsStore();
        var service = new RadioSessionService(controller, settingsStore);

        await service.ConnectUsingSettingsAsync();

        var spec = Assert.Single(controller.ConnectCalls);
        Assert.IsType<NoneConnectionSpec>(spec);
    }

    [Fact]
    public async Task ConnectUsingSettingsAsync_RigctldConfigured_ConnectsWithRigctldConnectionSpec()
    {
        var controller = new FakeRadioController();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RadioConnectionSettings.SectionKey,
                new RadioConnectionSettings { BackendId = "rigctld", Host = "127.0.0.1", Port = 4532 },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var service = new RadioSessionService(controller, settingsStore);

        await service.ConnectUsingSettingsAsync();

        var spec = Assert.IsType<RigctldConnectionSpec>(Assert.Single(controller.ConnectCalls));
        Assert.Equal("127.0.0.1", spec.Host);
        Assert.Equal(4532, spec.Port);
    }

    [Fact]
    public async Task SetPttAsync_DelegatesToController()
    {
        var controller = new FakeRadioController();
        var service = new RadioSessionService(controller, new FakeSettingsStore());

        await service.SetPttAsync(true);
        await service.SetPttAsync(false);

        Assert.Equal([true, false], controller.PttCalls);
    }
}
