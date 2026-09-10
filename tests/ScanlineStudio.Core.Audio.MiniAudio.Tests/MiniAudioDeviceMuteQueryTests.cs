using System.Diagnostics;
using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 9: real OS device mute state. Reuses the same real virtual-cable pattern as
/// <see cref="MiniAudioSpikeGateTests"/>/<see cref="MiniAudioDeviceEnumeratorTests"/> -- proves
/// <see cref="MiniAudioDeviceMuteQuery"/> actually observes a real PulseAudio/PipeWire-pulse
/// sink's mute state, independently verified via `pactl set-sink-mute` rather than only reading
/// the value back through the same code path that set it.
/// </summary>
public class MiniAudioDeviceMuteQueryTests
{
    /// <summary>
    /// TT1-15 (production_audit.md): the coverage gap its sibling
    /// <see cref="MiniAudioDeviceEnumerator"/> already closed with
    /// <c>DisposeAsync_RacingConcurrentRefreshAsync_NeverThrowsUnexpectedlyOrCorruptsState</c>.
    ///
    /// <para>What makes this safe is NOT the managed lock. <c>IsDeviceMutedAsync</c> checks
    /// <c>_disposed</c> under <c>_gate</c> and then runs the native call on the thread pool OUTSIDE
    /// that lock, while <c>Dispose</c> flips the flag under <c>_gate</c> and calls
    /// <c>MiniAudioContext.Release()</c> outside it — so a caller really can pass the check and only
    /// then have the context torn down under it. The native shim is what closes it:
    /// <c>scanline_audio_get_device_mute</c> holds <c>g_context_mutex</c> across its whole body and
    /// re-checks <c>g_context_initialized</c>, and <c>scanline_audio_context_uninit</c> takes that
    /// same mutex. So the orphan call either completes before teardown or returns -1, which surfaces
    /// as a null rather than a crash. The Windows and macOS paths never touch the shared context at
    /// all.</para>
    ///
    /// <para>That is a native-side invariant a managed-only reading of this class would miss, and
    /// nothing in the managed code states it. This test exists so that removing the mutex, or
    /// reordering the shim, fails here rather than in the field.</para>
    /// </summary>
    [RequiresPipeWireFact]
    public async Task DisposeRacingAnInFlightMuteQuery_NeverCorruptsStateAndAlwaysRejectsLaterCalls()
    {
        var device = await ResolveAnyPlaybackDeviceAsync();

        for (var i = 0; i < 20; i++)
        {
            var muteQuery = new MiniAudioDeviceMuteQuery();
            var inFlight = muteQuery.IsDeviceMutedAsync(device, isCapture: false);

            // Deliberately not awaited first -- races Dispose against whatever stage the native
            // call happens to be at.
            muteQuery.Dispose();

            // Any outcome is acceptable for the raced call itself, including a null from the shim's
            // own initialized-check. What must NOT happen is a crash or a corrupted refcount.
            await Record.ExceptionAsync(() => inFlight);

            // After disposal every further call must consistently throw, never silently run against
            // a torn-down context. ThrowIfDisposed runs synchronously before Task.Run, so this is a
            // void-returning lambda rather than Assert.ThrowsAsync -- same reason as the sibling
            // enumerator test.
            Assert.Throws<ObjectDisposedException>(() => { _ = muteQuery.IsDeviceMutedAsync(device, isCapture: false); });

            // A double release would throw InvalidOperationException("Release() called without a
            // matching Acquire()") and corrupt the process-wide refcount for every other consumer.
            muteQuery.Dispose();
        }

        // The refcount survived 20 acquire/release cycles raced against in-flight work: a fresh
        // consumer still works. A corrupted count would have thrown above or would fail here.
        using var afterwards = new MiniAudioDeviceMuteQuery();
        _ = await afterwards.IsDeviceMutedAsync(device, isCapture: false);
    }

    private static async Task<AudioDeviceInfo> ResolveAnyPlaybackDeviceAsync()
    {
        using var enumerator = new MiniAudioDeviceEnumerator(Microsoft.Extensions.Logging.Abstractions.NullLogger<MiniAudioDeviceEnumerator>.Instance);
        await enumerator.RefreshAsync();
        var devices = enumerator.OutputDevices;
        Assert.True(devices.Count > 0, "No playback devices were enumerated -- is a PulseAudio/PipeWire-pulse server running?");
        return devices[0];
    }

    [RequiresPipeWireFact]
    public async Task IsDeviceMutedAsync_PlaybackSink_ReflectsTheRealPulseAudioMuteState()
    {
        var sinkName = $"sstv_mute_test_{Guid.NewGuid():N}";
        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Mute_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            var device = await ResolveDeviceAsync(sinkName, isCapture: false);
            using var muteQuery = new MiniAudioDeviceMuteQuery();

            Assert.Equal(false, await muteQuery.IsDeviceMutedAsync(device, isCapture: false));

            // Independent oracle: pactl sets the mute, not this same code path.
            RunPactl($"set-sink-mute {sinkName} 1", out _);
            Assert.Equal(true, await muteQuery.IsDeviceMutedAsync(device, isCapture: false));

            RunPactl($"set-sink-mute {sinkName} 0", out _);
            Assert.Equal(false, await muteQuery.IsDeviceMutedAsync(device, isCapture: false));
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    private static async Task<AudioDeviceInfo> ResolveDeviceAsync(string nameNeedle, bool isCapture)
    {
        using var enumerator = new MiniAudioDeviceEnumerator(Microsoft.Extensions.Logging.Abstractions.NullLogger<MiniAudioDeviceEnumerator>.Instance);
        await enumerator.RefreshAsync();
        var devices = isCapture ? enumerator.InputDevices : enumerator.OutputDevices;
        var device = devices.FirstOrDefault(d => d.Id.Contains(nameNeedle, StringComparison.OrdinalIgnoreCase));
        Assert.True(device is not null, $"Device matching '{nameNeedle}' was not found among {devices.Count} enumerated devices.");
        return device!;
    }

    // Same shape as MiniAudioSpikeGateTests/MiniAudioDeviceEnumeratorTests' own private helper --
    // reads both stdout/stderr concurrently to avoid the classic pipe-buffer deadlock.
    private static void RunPactl(string arguments, out string output)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pactl",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pactl.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();
        output = stdoutTask.Result;
    }
}
