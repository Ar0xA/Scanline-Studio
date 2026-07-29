using Yoniq.Abstractions.Audio;

namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// Implements <see cref="IAudioDeviceEnumerator"/> against the native shim's device enumeration
/// (piece Audio 1) and native-format probing (piece Audio 4).
///
/// <see cref="AudioDeviceInfo.SupportedSampleRates"/> is populated honestly from whatever the
/// backend reports via <c>yoniq_audio_get_native_formats</c> -- but per that function's own doc
/// comment, probing opens/queries the device (slower than enumeration, can fail on a busy device),
/// and on backends that resample/mix server-side (PulseAudio/PipeWire in particular) the reported
/// format may just reflect the server's *current* format, not a real capability list. Callers
/// (the device picker UI) must never filter devices out by exact rate support -- the engine
/// converts to whatever rate the DSP layer asks for regardless (see spec/05-audio-engine.md).
/// </summary>
public sealed class MiniAudioDeviceEnumerator : IAudioDeviceEnumerator, IDisposable
{
    private const int MaxDevices = 128;
    private const int MaxNativeFormats = 64; // matches ma_device_info.nativeDataFormats' own fixed size

    private bool _disposed;

    public MiniAudioDeviceEnumerator()
    {
        MiniAudioContext.Acquire();
        InputDevices = [];
        OutputDevices = [];
    }

    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; private set; }

    /// <summary>Opus-review fix: this used to do all its work synchronously on the calling thread
    /// (per spec this is the UI thread) and return an already-completed <see cref="Task"/> --
    /// violating CLAUDE.md's "all hardware communication must be asynchronous" rule outright, not
    /// just in spirit. Now runs on the thread pool via <see cref="Task.Run(Action)"/>.
    /// <paramref name="ct"/> only prevents the work from *starting* if already cancelled -- once
    /// running, there is no way to cancel it partway through, because the underlying native calls
    /// (<c>yoniq_audio_get_native_formats</c> in particular) are themselves blocking with no
    /// cancellation or timeout of their own. Those same native calls share the process-wide
    /// context's PulseAudio mainloop with <c>ma_wait_for_operation__pulse</c> -- the exact call
    /// Piece Audio 8 found can block forever if the server never responds (see
    /// <see cref="MiniAudioCaptureSession.CloseTimeout"/>'s doc comment) -- so a wedged
    /// server can still hang this call's background thread indefinitely; moving it off the
    /// calling thread bounds the blast radius to that one thread pool thread rather than the
    /// caller, but does not eliminate the underlying unbounded wait. A native-side timeout on
    /// enumeration/probing would be needed to close that gap fully; not yet done.</summary>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        return Task.Run(
            () =>
            {
                InputDevices = Enumerate(isCapture: true);
                OutputDevices = Enumerate(isCapture: false);
            },
            ct);
    }

    private static List<AudioDeviceInfo> Enumerate(bool isCapture)
    {
        var nativeDevices = new NativeAudio.DeviceInfo[MaxDevices];
        var count = NativeAudio.yoniq_audio_enumerate_devices(isCapture ? 1 : 0, nativeDevices, MaxDevices);
        if (count < 0)
        {
            return [];
        }

        var result = new List<AudioDeviceInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var id = NativeAudio.DecodeFixedString(nativeDevices[i].Id);
            var name = NativeAudio.DecodeFixedString(nativeDevices[i].Name);
            var (maxChannels, sampleRates) = ProbeNativeFormats(id, isCapture);

            result.Add(isCapture
                ? new AudioDeviceInfo(id, name, MaxInputChannels: maxChannels, MaxOutputChannels: 0, sampleRates)
                : new AudioDeviceInfo(id, name, MaxInputChannels: 0, MaxOutputChannels: maxChannels, sampleRates));
        }

        return result;
    }

    private static (int MaxChannels, IReadOnlyList<int> SampleRates) ProbeNativeFormats(string deviceId, bool isCapture)
    {
        byte[] idBytes;
        try
        {
            idBytes = NativeAudio.EncodeFixedString(deviceId, NativeAudio.IdSize);
        }
        catch (ArgumentException)
        {
            return (0, []); // an id too long for our own ABI's fixed buffer -- can't probe it, degrade honestly
        }

        var formats = new NativeAudio.NativeFormat[MaxNativeFormats];
        var count = NativeAudio.yoniq_audio_get_native_formats(idBytes, isCapture ? 1 : 0, formats, MaxNativeFormats);
        if (count <= 0)
        {
            // Probing failed (busy device, backend this shim doesn't yet support probing on,
            // etc.) -- degrade honestly to "no constraint reported" rather than guessing.
            return (0, []);
        }

        var maxChannels = 0;
        var sampleRates = new SortedSet<int>();
        for (var i = 0; i < count; i++)
        {
            // 0 means "any" per miniaudio's own convention (see yoniq_audio_get_native_formats'
            // doc comment) -- not a real constraint to report.
            if (formats[i].Channels > maxChannels)
            {
                maxChannels = formats[i].Channels;
            }

            if (formats[i].SampleRate > 0)
            {
                sampleRates.Add(formats[i].SampleRate);
            }
        }

        return (maxChannels, sampleRates.ToArray());
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            MiniAudioContext.Release();
            _disposed = true;
        }
    }
}
