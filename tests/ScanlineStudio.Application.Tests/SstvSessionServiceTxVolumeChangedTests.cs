using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>CQ-05-F1: every committed Pwr change raises TxVolumePercentChanged, so the header slider
/// can follow writes made elsewhere.</summary>
public sealed class SstvSessionServiceTxVolumeChangedTests
{
    private static (SstvSessionService Service, FakeSettingsStore Store) Create(int? persistedPercent = null)
    {
        var store = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { TxVolumePercent = persistedPercent },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var enumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [11025])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var service = new SstvSessionService(
            new FakeAudioEngine(), enumerator, new FakeAudioDeviceMuteQuery(), store, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance);
        return (service, store);
    }

    [Fact]
    public async Task SetTxVolumePercentAsync_RaisesOnceWithClampedValue_VersionsIncrease()
    {
        var (service, _) = Create();
        var changes = new List<TxVolumeChange>();
        service.TxVolumePercentChanged += changes.Add;

        await service.SetTxVolumePercentAsync(150);
        await service.SetTxVolumePercentAsync(40);

        Assert.Equal([100, 40], changes.Select(c => c.Percent));
        Assert.True(changes[1].Version > changes[0].Version);
    }

    [Fact]
    public async Task SetTxVolumePercentAsync_SaveFails_RaisesNothing()
    {
        var (service, store) = Create();
        var changes = new List<TxVolumeChange>();
        service.TxVolumePercentChanged += changes.Add;
        store.SaveAsyncExceptionOnce = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => service.SetTxVolumePercentAsync(40));

        Assert.Empty(changes);
    }

    [Fact]
    public async Task SetTxVolumePercentAsync_ThrowingSubscriber_DoesNotFailWriteOrSkipOthers()
    {
        var (service, _) = Create();
        var seen = new List<int>();
        service.TxVolumePercentChanged += _ => throw new InvalidOperationException("subscriber bug");
        service.TxVolumePercentChanged += c => seen.Add(c.Percent);

        await service.SetTxVolumePercentAsync(25);

        Assert.Equal([25], seen);
        Assert.Equal(25, await service.GetTxVolumePercentAsync());
    }

    [Fact]
    public async Task ResyncTxVolumeFromSettingsAsync_RaisesThePersistedValue_WithoutWriting()
    {
        var (service, store) = Create(persistedPercent: 30);
        var before = store.Settings;
        var changes = new List<TxVolumeChange>();
        service.TxVolumePercentChanged += changes.Add;

        await service.ResyncTxVolumeFromSettingsAsync();

        Assert.Equal([30], changes.Select(c => c.Percent));
        Assert.Same(before, store.Settings);
    }

    [Fact]
    public async Task ResyncTxVolumeFromSettingsAsync_VersionFollowsAnEarlierWrite()
    {
        var (service, _) = Create();
        var changes = new List<TxVolumeChange>();
        service.TxVolumePercentChanged += changes.Add;

        await service.SetTxVolumePercentAsync(60);
        await service.ResyncTxVolumeFromSettingsAsync();

        Assert.Equal([60, 60], changes.Select(c => c.Percent));
        Assert.True(changes[1].Version > changes[0].Version);
    }
}
