using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.MiniAudio;

/// <summary>
/// Implements <see cref="IAudioDeviceMuteQuery"/> against the native shim's device-scoped mute
/// query (`scanline_audio_get_device_mute`, piece Audio 9). Holds a <see cref="MiniAudioContext"/>
/// reference for its own lifetime, same ref-counted-singleton pattern as
/// <see cref="MiniAudioDeviceEnumerator"/>.
///
/// Native calls block on real inter-process work (a PulseAudio server round-trip, a COM call, a
/// CoreAudio call) -- runs via <see cref="Task.Run(Action)"/> rather than on the calling (expected:
/// UI) thread, matching this project's established pattern for the audio device enumerator's own
/// <c>RefreshAsync</c>.
/// </summary>
public sealed class MiniAudioDeviceMuteQuery : IAudioDeviceMuteQuery, IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;

    public MiniAudioDeviceMuteQuery()
    {
        MiniAudioContext.Acquire();
    }

    public Task<bool?> IsDeviceMutedAsync(AudioDeviceInfo device, bool isCapture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();

        return Task.Run(
            () =>
            {
                var deviceIdBytes = NativeAudio.EncodeFixedString(device.Id, NativeAudio.IdSize);
                var result = NativeAudio.scanline_audio_get_device_mute(deviceIdBytes, isCapture ? 1 : 0, out var isMuted);
                if (result != 0)
                {
                    return (bool?)null;
                }

                return isMuted != 0;
            },
            ct);
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        MiniAudioContext.Release();
    }
}
