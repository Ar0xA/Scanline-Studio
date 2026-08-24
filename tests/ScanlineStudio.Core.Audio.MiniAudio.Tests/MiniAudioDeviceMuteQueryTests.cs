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
