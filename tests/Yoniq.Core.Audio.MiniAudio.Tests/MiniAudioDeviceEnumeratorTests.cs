using System.Diagnostics;
using Yoniq.Core.Audio.MiniAudio;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 4: the real <see cref="IAudioDeviceEnumerator"/> implementation, built on top of
/// piece Audio 1's enumeration and native-format probing. Reuses the same real virtual-cable
/// pattern as <see cref="MiniAudioSpikeGateTests"/> -- this is the production-shaped surface that
/// spike proved the mechanism for, so the same real device is the right thing to check it against.
/// </summary>
public class MiniAudioDeviceEnumeratorTests
{
    [RequiresPipeWireFact]
    public async Task RefreshAsync_FindsRealVirtualCable_AsDistinctPlaybackAndCaptureDevices()
    {
        var sinkName = $"sstv_enum_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Enum_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();

            Assert.True(enumerator.OutputDevices.Count > 0, "No output devices enumerated at all.");
            Assert.True(enumerator.InputDevices.Count > 0, "No input devices enumerated at all.");

            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");
            Assert.True(sink!.MaxOutputChannels >= 0, "MaxOutputChannels should never be negative.");
            Assert.Equal(0, sink.MaxInputChannels); // an output-only entry must not claim input channels

            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");
            Assert.True(monitor!.MaxInputChannels >= 0, "MaxInputChannels should never be negative.");
            Assert.Equal(0, monitor.MaxOutputChannels); // an input-only entry must not claim output channels

            // Honest reporting, not asserted to any specific value: SupportedSampleRates may
            // legitimately be empty (this shim's own doc comment on yoniq_audio_get_native_formats
            // says probing can report "any" via a 0 entry, or fail outright on some devices) --
            // the only thing that must never happen is a negative/garbage value sneaking through.
            Assert.All(sink.SupportedSampleRates, rate => Assert.True(rate > 0));
            Assert.All(monitor.SupportedSampleRates, rate => Assert.True(rate > 0));
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [RequiresPipeWireFact]
    public async Task TwoEnumeratorInstances_CanBeUsedConcurrently()
    {
        // Exercises MiniAudioContext's ref-counting directly: two independent
        // MiniAudioDeviceEnumerator instances (matching how a device enumerator and a separate
        // capture/playback engine will each own their own reference in piece Audio 5/6) must not
        // fight over the process-wide native context. Deliberately does NOT assert the two see an
        // identical device count -- xunit can run other test classes concurrently in the same
        // process (confirmed: this test itself intermittently observed a real device-count
        // difference from a virtual sink another test created/destroyed mid-run), so the only
        // thing genuinely guaranteed here is that neither instance throws or corrupts the other's
        // view -- not that the system's real device list is frozen for the duration of the test.
        using var first = new MiniAudioDeviceEnumerator();
        using var second = new MiniAudioDeviceEnumerator();

        await first.RefreshAsync();
        await second.RefreshAsync();

        Assert.True(first.OutputDevices.Count > 0);
        Assert.True(second.OutputDevices.Count > 0);
        Assert.True(first.InputDevices.Count > 0);
        Assert.True(second.InputDevices.Count > 0);
    }

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
        output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }
}
