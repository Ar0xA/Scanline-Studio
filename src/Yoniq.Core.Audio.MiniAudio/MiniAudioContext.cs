namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// The native shim's audio context (`yoniq_audio_context_init`/`_uninit`) is a process-wide
/// singleton -- calling init twice without an uninit in between fails. But
/// <see cref="MiniAudioDeviceEnumerator"/> and the future capture/playback engine (piece Audio 5/6)
/// each need it independently, and neither should have to know or care whether the other is also
/// using it. This reference-counts acquisition in managed code so any number of consumers can each
/// construct/dispose on their own schedule: the native context is only actually initialized on the
/// first acquire and only actually torn down on the last release.
/// </summary>
internal static class MiniAudioContext
{
    private static readonly object Lock = new();
    private static int _refCount;
    private static string? _backendName;

    /// <summary>Acquires a reference to the shared native context, initializing it for real on the
    /// first call. Returns the resolved backend's name (e.g. "PulseAudio"). Every successful call
    /// must be matched by exactly one <see cref="Release"/>.</summary>
    public static string Acquire()
    {
        lock (Lock)
        {
            if (_refCount == 0)
            {
                var backendNameBuffer = new byte[NativeAudio.BackendNameSize];
                var result = NativeAudio.yoniq_audio_context_init(backendNameBuffer);
                if (result != 0)
                {
                    throw new InvalidOperationException($"Failed to initialize the miniaudio context (error {result}).");
                }

                _backendName = NativeAudio.DecodeFixedString(backendNameBuffer);
            }

            _refCount++;
            return _backendName!;
        }
    }

    /// <summary>Releases a reference acquired via <see cref="Acquire"/>, tearing down the native
    /// context for real once the last reference is released.</summary>
    public static void Release()
    {
        lock (Lock)
        {
            if (_refCount <= 0)
            {
                throw new InvalidOperationException("Release() called without a matching Acquire().");
            }

            _refCount--;
            if (_refCount == 0)
            {
                NativeAudio.yoniq_audio_context_uninit();
                _backendName = null;
            }
        }
    }
}
