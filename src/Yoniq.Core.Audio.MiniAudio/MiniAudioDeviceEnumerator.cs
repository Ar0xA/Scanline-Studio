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
///
/// Third-opus-review note on <see cref="InputDevices"/>/<see cref="OutputDevices"/>: reading both
/// as two separate properties is NOT guaranteed to observe the same underlying
/// <see cref="RefreshAsync"/> call's results -- a concurrent refresh can complete between the two
/// reads, so a caller can see this instance's newest input list paired with its previous output
/// list. A second-opus-review fix attempted to close this by swapping both lists from one
/// snapshot object, which keeps each *individual* property internally consistent (never a torn
/// single list) but does nothing for the *pair*, since <see cref="IAudioDeviceEnumerator"/> only
/// exposes them as two independent members -- there is no way to read both atomically through
/// this interface as it's currently shaped. Fixing that for real needs an interface change (e.g. a
/// single combined snapshot member); not done here to avoid changing public interface shape as
/// part of a bug-fix pass. In practice this is a minor, self-correcting UI inconsistency (the next
/// refresh reconciles it), not data corruption -- documented honestly rather than claimed fixed.
/// </summary>
public sealed class MiniAudioDeviceEnumerator : IAudioDeviceEnumerator, IDisposable, IAsyncDisposable
{
    private const int MaxDevices = 128;
    private const int MaxNativeFormats = 64; // matches ma_device_info.nativeDataFormats' own fixed size

    private sealed record DeviceSnapshot(IReadOnlyList<AudioDeviceInfo> InputDevices, IReadOnlyList<AudioDeviceInfo> OutputDevices);

    private static readonly DeviceSnapshot EmptySnapshot = new([], []);

    // Third-opus-review fix: _disposed and _refreshTask are now both guarded by the SAME lock
    // (_gate), and RefreshAsync's disposed-check + task-creation happen inside it too -- the
    // second-opus-review version used Interlocked.Exchange for _disposed and a separate lock only
    // around reading/writing _refreshTask, with the disposed-check in RefreshAsync outside any
    // lock at all. That left a real TOCTOU: RefreshAsync could pass its disposed-check, then
    // Dispose could run entirely (flip disposed, capture the *old* _refreshTask, wait on it,
    // release the context) before RefreshAsync went on to actually start its new Task.Run --
    // leaving a fresh background enumeration touching the native context after Dispose released
    // it, which is exactly the hazard this whole mechanism exists to prevent. A single lock around
    // "check disposed, then (start a refresh) or (mark disposed and capture the task to wait on)"
    // makes the two operations properly mutually exclusive.
    private readonly object _gate = new();
    private bool _disposed;
    private Task _refreshTask = Task.CompletedTask;
    private volatile DeviceSnapshot _snapshot = EmptySnapshot;

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
    /// Second/third-opus-review fix: this used to be able to outlive <see cref="Dispose"/>
    /// entirely -- see <see cref="_gate"/>'s own doc comment for the full TOCTOU history and why
    /// disposed-check and task-tracking now share one lock.</summary>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <summary>Synchronous, bounded-blocking disposal for <see cref="IDisposable"/> consumers.
    /// Third-opus-review note: this can block the calling thread up to 5 seconds waiting for an
    /// in-flight <see cref="RefreshAsync"/> to finish -- for a UI-owned enumerator (per spec, the
    /// expected consumer), that's a real freeze risk this type's own <see cref="RefreshAsync"/>
    /// fix was originally meant to avoid. Prefer <see cref="DisposeAsync"/> (<c>await using</c>)
    /// from any context that can await; this synchronous path exists only for callers stuck with
    /// plain <c>using</c>.</summary>
    public void Dispose()
    {
        if (!TryClaimDispose(out var refreshTask))
        {
            return;
        }

        bool completedInTime;
        try
        {
            completedInTime = refreshTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The task itself finished (with a fault) within the timeout -- not hung, just
            // unsuccessful. Treat the same as completing on time.
            completedInTime = true;
        }

        ReleaseIfCompletedInTime(completedInTime);
    }

    /// <summary>Non-blocking disposal -- awaits any in-flight <see cref="RefreshAsync"/> instead
    /// of blocking the calling thread for it, so this never freezes a UI thread the way the
    /// synchronous <see cref="Dispose"/> can. Still bounded at 5 seconds, matching this project's
    /// established CloseTimeout pattern (piece Audio 8): the underlying native calls have no
    /// timeout of their own, so this cannot guarantee the refresh has actually stopped touching
    /// the native context, only that it waited a reasonable amount of time for it to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!TryClaimDispose(out var refreshTask))
        {
            return;
        }

        // Task.WhenAny never throws even if refreshTask itself faults -- it only reports which one
        // finished first, so there is no need to observe/rethrow refreshTask's own result here;
        // only whether it settled within the bound matters for the release decision below.
        var completedTask = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        ReleaseIfCompletedInTime(completedInTime: completedTask == refreshTask);
    }

    /// <summary>Atomically marks this instance disposed (a no-op if already disposed) and captures
    /// the task the caller should wait on before deciding whether to release the native context
    /// reference. See <see cref="_gate"/>'s own doc comment for why this must share a lock with
    /// <see cref="RefreshAsync"/>.</summary>
    private bool TryClaimDispose(out Task refreshTask)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                refreshTask = Task.CompletedTask;
                return false;
            }

            _disposed = true;
            refreshTask = _refreshTask;
            return true;
        }
    }

    /// <summary>Only release if the refresh is confirmed done -- if it timed out, it may still be
    /// touching the shared native context, so releasing (and possibly triggering
    /// yoniq_audio_context_uninit) here would be the exact use-after-free this fix exists to
    /// prevent. Mirrors MiniAudioCaptureSession/PlaybackSession's TimedOutDuringClose: a
    /// deliberate, accepted leak of this instance's context reference in that rare case,
    /// preferable to a crash.</summary>
    private static void ReleaseIfCompletedInTime(bool completedInTime)
    {
        if (completedInTime)
        {
            MiniAudioContext.Release();
        }
    }
}
