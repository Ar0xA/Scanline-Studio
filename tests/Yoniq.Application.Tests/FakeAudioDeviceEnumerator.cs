using Yoniq.Abstractions.Audio;

namespace Yoniq.Application.Tests;

internal sealed class FakeAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; set; } = [];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; set; } = [];

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}
