namespace Yoniq.Abstractions.Audio;

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> InputDevices { get; }

    IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }

    Task RefreshAsync(CancellationToken ct = default);
}
