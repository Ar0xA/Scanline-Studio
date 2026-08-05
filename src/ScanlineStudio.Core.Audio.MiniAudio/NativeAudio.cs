using System.Runtime.InteropServices;
using System.Text;

namespace ScanlineStudio.Core.Audio.MiniAudio;

/// <summary>
/// P/Invoke bindings against our own shim (`native/yoniq_audio.c`/`.h`), never against miniaudio's
/// own structs directly -- see that shim's own doc comment for why: <c>ma_device</c>'s layout
/// varies by platform *and* by which <c>MA_NO_*</c> flags it's compiled with, so no C# struct
/// could ever safely be pinned to a specific layout for it. Every type crossing this boundary is a
/// flat, fixed-size, unconditional POD struct our own C code defines and the C compiler lays out,
/// not anything from miniaudio.h itself.
///
/// Every declaration is explicitly <see cref="CallingConvention.Cdecl"/> (opus-review fix): the
/// shim's own functions are plain C, always cdecl, but <see cref="DllImport"/>'s own default
/// (<see cref="CallingConvention.Winapi"/>) resolves to stdcall on 32-bit Windows -- harmless on
/// every target this project currently builds for (x64/ARM64 everywhere, where the two calling
/// conventions collapse to the same thing), but a real stack-corruption bug waiting for a 32-bit
/// Windows target that doesn't exist yet.
/// </summary>
internal static class NativeAudio
{
    private const string LibraryName = "yoniqaudio";

    internal const int BackendNameSize = 32;
    internal const int IdSize = 256;
    private const int NameSize = 256;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IdSize)]
        public byte[] Id;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NameSize)]
        public byte[] Name;

        public int IsDefault;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFormat
    {
        public int Channels;   // 0 means "any channel count supported"
        public int SampleRate; // 0 means "any sample rate supported"
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_context_init(byte[] backendNameOut);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void yoniq_audio_context_uninit();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_enumerate_devices(int isCapture, [Out] DeviceInfo[] outDevices, int maxCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_spike_capture_test(byte[] deviceId, int durationMs, out float peakOut);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_get_native_formats(byte[] deviceId, int isCapture, [Out] NativeFormat[] outFormats, int maxCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr yoniq_audio_ring_create(int capacityFrames, int channels);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void yoniq_audio_ring_destroy(IntPtr ring);

    // Pointer-based, not float[]: marshaling an array parameter copies on every call, which for a
    // ring buffer written/read on every audio buffer would allocate/copy continuously -- exactly
    // what piece Audio 3 exists to avoid. Callers pin their own Span/array via `fixed` and pass the
    // raw pointer instead (see MiniAudioRing).
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int yoniq_audio_ring_write(IntPtr ring, float* data, int frameCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int yoniq_audio_ring_read(IntPtr ring, float* outData, int frameCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr yoniq_audio_capture_session_open(byte[] deviceId, int sampleRate, int ringCapacityFrames);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void yoniq_audio_capture_session_close(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int yoniq_audio_capture_session_read(IntPtr session, float* outData, int frameCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_capture_session_check_and_clear_stopped(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_capture_session_overrun_count(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr yoniq_audio_playback_session_open(byte[] deviceId, int sampleRate, int ringCapacityFrames);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void yoniq_audio_playback_session_close(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int yoniq_audio_playback_session_write(IntPtr session, float* data, int frameCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_playback_session_pending_frames(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_playback_session_underrun_count(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int yoniq_audio_playback_session_check_and_clear_stopped(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int yoniq_audio_resample_f32(float* input, int inputFrameCount, int sampleRateIn, int sampleRateOut, int lpfOrder, float* output, int outputCapacityFrames);

    /// <summary>Decodes a null-terminated, fixed-size native byte buffer (UTF-8, matching this
    /// shim's own convention for device ids/names) into a C# string.</summary>
    internal static string DecodeFixedString(byte[] buffer)
    {
        var nullIndex = Array.IndexOf(buffer, (byte)0);
        var length = nullIndex >= 0 ? nullIndex : buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>Encodes a C# string into a fixed-size, null-terminated UTF-8 buffer matching this
    /// shim's own device-id convention. Throws if the encoded string (plus terminator) doesn't fit.</summary>
    internal static byte[] EncodeFixedString(string value, int size)
    {
        var buffer = new byte[size];
        var encoded = Encoding.UTF8.GetBytes(value);
        if (encoded.Length >= size)
        {
            throw new ArgumentException($"'{value}' is too long to fit in a {size}-byte native buffer.", nameof(value));
        }

        Array.Copy(encoded, buffer, encoded.Length);
        return buffer;
    }
}
