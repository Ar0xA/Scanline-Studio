using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio;

/// <summary>
/// Test double for <see cref="IAudioDeviceMuteQuery"/>. In-memory only, no native dependency. A
/// device with no entry yet reads as <see langword="null"/> (matching the real
/// <c>MiniAudioDeviceMuteQuery</c>'s "unsupported, not an error" contract for a never-queried
/// device) -- tests that want a concrete starting value must seed it explicitly via
/// <see cref="SetMuted"/> before exercising the code under test.
/// </summary>
public sealed class FakeAudioDeviceMuteQuery : IAudioDeviceMuteQuery
{
    private readonly Dictionary<(string Id, bool IsCapture), bool> _muted = [];
    private readonly HashSet<(string Id, bool IsCapture)> _unsupported = [];

    /// <summary>Test setup helper -- seeds a device's mute state.</summary>
    public void SetMuted(string deviceId, bool isCapture, bool muted)
    {
        _muted[(deviceId, isCapture)] = muted;
    }

    /// <summary>Test setup helper -- makes a specific device behave as if the backend doesn't
    /// support a mute query (JACK, or an ALSA device with no suitable mixer switch): reads as
    /// <see langword="null"/>.</summary>
    public void MarkUnsupported(string deviceId, bool isCapture)
    {
        _unsupported.Add((deviceId, isCapture));
    }

    public Task<bool?> IsDeviceMutedAsync(AudioDeviceInfo device, bool isCapture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var key = (device.Id, isCapture);
        if (_unsupported.Contains(key))
        {
            return Task.FromResult((bool?)null);
        }

        return Task.FromResult(_muted.TryGetValue(key, out var value) ? (bool?)value : null);
    }
}
