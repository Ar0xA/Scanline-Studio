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

    // Second-opus-review fix: an int, not a bool, so Dispose can use Interlocked.Exchange to make
    // "check and mark disposed" a single atomic step -- see Dispose's own comment for why a plain
    // `if (!_disposed) { ...; _disposed = true; }` allows two concurrent Dispose calls to both
    // pass the check and both run the teardown.
    private int _disposed;

    // Second-opus-review fix: both device lists are now read from a single snapshot reference,
    // swapped in one atomic assignment by RefreshAsync -- previously they were two independent
    // auto-properties written as two separate statements, so a reader on another thread could
    // observe a torn pair (new InputDevices alongside a stale OutputDevices) mid-refresh.
    private sealed record DeviceSnapshot(IReadOnlyList<AudioDeviceInfo> InputDevices, IReadOnlyList<AudioDeviceInfo> OutputDevices);

    private static readonly DeviceSnapshot EmptySnapshot = new([], []);

    private volatile DeviceSnapshot _snapshot = EmptySnapshot;

    // Second-opus-review fix: tracks the most recent RefreshAsync so Dispose can wait for it
    // rather than racing it -- see Dispose's own comment for the full reasoning.
    private readonly object _refreshLock = new();
    private Task _refreshTask = Task.CompletedTask;

    public MiniAudioDeviceEnumerator()
    {
        MiniAudioContext.Acquire();
    }

    public IReadOnlyList<AudioDeviceInfo> InputDevices => _snapshot.InputDevices;

    public IReadOnlyList<AudioDeviceInfo> OutputDevices => _snapshot.OutputDevices;

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
    /// enumeration/probing would be needed to close that gap fully; not yet done.
    ///
    /// Second-opus-review fix: this used to be able to outlive <see cref="Dispose"/> entirely --
    /// nothing tracked the in-flight <see cref="Task"/>, so a caller that disposed this instance
    /// while a refresh was still running left a background thread touching the shared native
    /// context concurrently with <see cref="MiniAudioContext.Release"/> tearing it down (the exact
    /// unsynchronized-shared-mainloop hazard the native context mutex exists to prevent, just via
    /// teardown instead of a peer call). Now tracked in <see cref="_refreshTask"/> so
    /// <see cref="Dispose"/> can wait for it first.</summary>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_refreshLock)
        {
            _refreshTask = Task.Run(
                () =>
                {
                    var inputs = Enumerate(isCapture: true);
                    var outputs = Enumerate(isCapture: false);
                    _snapshot = new DeviceSnapshot(inputs, outputs);
                },
                ct);
            return _refreshTask;
        }
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
        // Interlocked.Exchange, not `if (!_disposed) { ...; _disposed = true; }`: the plain form
        // lets two concurrent Dispose calls both observe `false` and both run the teardown below
        // (double-Release, underflowing MiniAudioContext's refcount). Exchange makes "check and
        // claim" a single atomic step -- only the caller that actually flips it from 0 proceeds.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Bounded wait for any in-flight RefreshAsync, matching this project's established
        // CloseTimeout pattern (piece Audio 8) rather than either blocking forever or racing it:
        // the underlying native calls have no timeout of their own, so this cannot guarantee the
        // refresh has actually stopped touching the native context, only that we waited a
        // reasonable amount of time for it to finish on its own.
        Task refreshTask;
        lock (_refreshLock)
        {
            refreshTask = _refreshTask;
        }

        bool completedInTime;
        try
        {
            completedInTime = refreshTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The task itself finished (with a fault, e.g. OperationCanceledException) within the
            // timeout -- not hung, just unsuccessful. Treat the same as completing on time.
            completedInTime = true;
        }

        // Only release if the refresh is confirmed done -- if it timed out, it may still be
        // touching the shared native context, so releasing (and possibly triggering
        // yoniq_audio_context_uninit) here would be the exact use-after-free this fix exists to
        // prevent. Mirrors MiniAudioCaptureSession/PlaybackSession's TimedOutDuringClose: a
        // deliberate, accepted leak of this instance's context reference in that rare case,
        // preferable to a crash.
        if (completedInTime)
        {
            MiniAudioContext.Release();
        }
    }
}
