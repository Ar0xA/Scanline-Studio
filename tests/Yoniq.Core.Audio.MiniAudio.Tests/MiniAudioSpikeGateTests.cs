using System.Diagnostics;
using Yoniq.Core.Audio.MiniAudio;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 1's own gate: proves the specific thing PortAudio's ALSA-only Linux backend was
/// found to fail at (see spec/05-audio-engine.md's Backend choice section) is genuinely fixed by
/// miniaudio's PulseAudio backend, not just assumed fixed because its docs say PulseAudio is
/// supported. Four checks, not one -- "the sink appears in some list" alone wouldn't have caught
/// PortAudio's own failure mode (visible-but-unusable, or reachable only through a generic
/// catch-all alias):
/// 1. The resolved backend is genuinely PulseAudio (miniaudio's real default backend priority list
///    tries sndio/audio4/oss first on some platforms and always includes a silent "null" backend
///    as the lowest-priority fallback -- confirmed directly against the pinned miniaudio.h -- so an
///    explicit backend list is required, and this asserts it actually took effect).
/// 2. A uniquely-named virtual sink appears as its own distinct playback device (not folded into a
///    generic alias).
/// 3. That same sink's monitor appears as its own distinct capture device.
/// 4. Capturing from that monitor while real audio plays into the sink yields genuinely non-silent
///    samples -- visibility alone doesn't prove usability.
///
/// Requires a real PulseAudio/PipeWire-pulse server and `pactl`/`ffmpeg`/`paplay` on PATH --
/// true in this project's dev sandbox, but not universal (Windows/macOS CI runners have no pactl
/// at all, and even a Linux runner isn't guaranteed a running audio server). Uses
/// <see cref="RequiresPipeWireFactAttribute"/> (piece Audio 7) so those environments report an
/// honest skip instead of a hard CI failure.
/// </summary>
public class MiniAudioSpikeGateTests
{
    [RequiresPipeWireFact]
    public void MiniAudio_ResolvesPulseAudioAndSeesRealVirtualCable()
    {
        var sinkName = $"sstv_gate_test_{Guid.NewGuid():N}";
        var sinkDescription = "SSTV_Gate_Test";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description={sinkDescription}", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        Process? toneProcess = null;
        try
        {
            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            // Via MiniAudioContext's ref-counted acquire, not the raw native call directly: the
            // native context is a process-wide singleton, and xunit can run different test classes
            // concurrently in the same process by default -- going through the same acquire/release
            // path every other consumer (MiniAudioDeviceEnumerator, the future capture/playback
            // engine) uses is what keeps this test safe to run alongside them, not a coincidence.
            var backendName = MiniAudioContext.Acquire();

            try
            {
                Assert.Equal("PulseAudio", backendName);

                var playbackDevices = new NativeAudio.DeviceInfo[64];
                var playbackCount = NativeAudio.yoniq_audio_enumerate_devices(isCapture: 0, playbackDevices, playbackDevices.Length);
                Assert.True(playbackCount > 0, "No playback devices enumerated at all.");

                var sinkFound = FindDeviceContaining(playbackDevices, playbackCount, sinkName);
                Assert.True(sinkFound is not null, $"Virtual sink '{sinkName}' was not found among {playbackCount} enumerated playback devices.");

                var captureDevices = new NativeAudio.DeviceInfo[64];
                var captureCount = NativeAudio.yoniq_audio_enumerate_devices(isCapture: 1, captureDevices, captureDevices.Length);
                Assert.True(captureCount > 0, "No capture devices enumerated at all.");

                var monitorFound = FindDeviceContaining(captureDevices, captureCount, $"{sinkName}.monitor");
                Assert.True(monitorFound is not null, $"Virtual sink's monitor was not found among {captureCount} enumerated capture devices.");

                var monitorIdBytes = NativeAudio.EncodeFixedString(monitorFound!, NativeAudio.IdSize);
                var captureResult = NativeAudio.yoniq_audio_spike_capture_test(monitorIdBytes, durationMs: 2000, out var peak);
                Assert.Equal(0, captureResult);
                Assert.True(peak > 0.0f, $"Captured audio from the virtual cable's monitor was silent (peak={peak}) -- device opened but no real audio flowed.");
            }
            finally
            {
                MiniAudioContext.Release();
            }
        }
        finally
        {
            if (toneProcess is not null && !toneProcess.HasExited)
            {
                toneProcess.Kill(entireProcessTree: true);
            }

            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    private static string? FindDeviceContaining(NativeAudio.DeviceInfo[] devices, int count, string needle)
    {
        for (var i = 0; i < count; i++)
        {
            var id = NativeAudio.DecodeFixedString(devices[i].Id);
            var name = NativeAudio.DecodeFixedString(devices[i].Name);
            if (id.Contains(needle, StringComparison.OrdinalIgnoreCase) || name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    private static Process StartToneIntoSink(string sinkName, int durationSeconds)
    {
        // ffmpeg generates a plain sine tone, piped as WAV straight into paplay targeting the
        // named sink -- real audio genuinely flowing into it, not a synthetic/mocked signal.
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList =
            {
                "-c",
                $"ffmpeg -f lavfi -i \"sine=frequency=1000:duration={durationSeconds}\" -f wav - 2>/dev/null | paplay --device={sinkName}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start tone-generation process.");
        Thread.Sleep(500); // let the pipeline actually start producing audio before the test proceeds
        return process;
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
