namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// The native shim's audio context (`yoniq_audio_context_init`/`_uninit`) is a process-wide
/// singleton -- calling init twice without an uninit in between fails. But
/// <see cref="MiniAudioDeviceEnumerator"/>, <see cref="MiniAudioCaptureSession"/>, and
/// <see cref="MiniAudioPlaybackSession"/> each need it independently, and neither should have to
/// know or care whether the others are also using it. This reference-counts acquisition in managed
/// code so any number of consumers can each construct/dispose on their own schedule: the native
/// context is only actually initialized on the first acquire and only actually torn down on the
/// last release.
///
/// Deliberate exception to CLAUDE.md's "avoid static mutable state" rule, recorded here rather
/// than left as a silent violation (per an opus-driven review's own suggested resolution): the
/// thing being modeled -- a single OS-level native library context that generically cannot be
/// initialized twice in one process -- is a hard constraint of `miniaudio`/PulseAudio/WASAPI/
/// CoreAudio themselves, not an arbitrary architectural choice this codebase made. A DI-registered
/// singleton wrapping the same ref-count would be equivalent in every way that matters (one
/// instance, shared by everything that resolves it) except needing a composition root to register
/// it with -- which doesn't exist yet, since nothing yet composes <see cref="MiniAudioCaptureSession"/>/
/// <see cref="MiniAudioPlaybackSession"/> into a full `IAudioEngine` implementation (still an open
/// piece). Revisit this as a constructor-injected singleton once that composition root exists,
/// rather than adding one prematurely for a consumer that doesn't exist yet. All access below is
/// already serialized under <see cref="Lock"/>, so this is not merely "shared mutable state with
/// no synchronization" -- concurrent Acquire/Release from any number of threads is safe today.
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
