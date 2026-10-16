using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Imaging.OpenJpeg;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Host.Tests;

public sealed class DrmCompositionRootTests
{
    [Fact]
    public async Task DrmStateIsAppLifetime_AndTheCodecIsTheBundledOpenJpeg()
    {
        await using var provider = SstvCompositionRootTests.BuildServicesWithFakes().BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<DrmTxNaming>(), provider.GetRequiredService<DrmTxNaming>());
        Assert.Same(provider.GetRequiredService<BsrTransportIdCounter>(), provider.GetRequiredService<BsrTransportIdCounter>());
        Assert.Same(provider.GetRequiredService<IDrmTransmitter>(), provider.GetRequiredService<IDrmTransmitter>());
        var codec = Assert.IsType<OpenJpegJpeg2000Codec>(provider.GetRequiredService<IJpeg2000Codec>());
        Assert.True(codec.IsAvailable, codec.UnavailableReason);
    }

    [Fact]
    public async Task SessionService_FromTheCompositionRoot_TransmitsDrm()
    {
        var services = SstvCompositionRootTests.BuildServicesWithFakes();
        services.AddSingleton<IAudioDeviceEnumerator>(new DefaultPlaybackDevice());
        services.AddSingleton<ISettingsStore>(new ReadOnlySettingsStore());
        await using var provider = services.BuildServiceProvider();
        var audio = (FakeAudioEngine)provider.GetRequiredService<IAudioEngine>();
        var session = provider.GetRequiredService<ISstvSessionService>();
        var image = new ArrayImageSource(64, 48, new Rgb24[64 * 48]);
        var settings = new DrmTxSettings(DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam16, DrmProtectionLevel.High,
            DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.None, "PA0TST", VoxTone: false, TargetJp2Bytes: 400);

        await session.TransmitDrmAsync(image, "card.png", settings);

        Assert.Equal(48_000, audio.LastRequestedPlaybackSampleRate);
        Assert.True(audio.PlaybackSamples.Count > 0 && audio.PlaybackSamples.Count % 19_200 == 0);
    }

    private sealed class DefaultPlaybackDevice : IAudioDeviceEnumerator
    {
        public IReadOnlyList<AudioDeviceInfo> InputDevices { get; } = [];

        public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; } = [new AudioDeviceInfo("out", "Out", 0, 1, [48_000], IsDefault: true)];

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Like JsonSettingsStore, a mutate that returns its input writes nothing; TX only reads.</summary>
    private sealed class ReadOnlySettingsStore : ISettingsStore
    {
        private readonly AppSettings _settings = new();

        public IObservable<AppSettings> Changes => throw new NotSupportedException();

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_settings);

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) =>
            ReferenceEquals(mutate(_settings), _settings) ? Task.FromResult(_settings) : throw new NotSupportedException();
    }
}
