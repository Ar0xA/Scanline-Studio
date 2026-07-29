using System.Diagnostics;
using Yoniq.Core.Audio.MiniAudio;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 5: the real capture path, exercised end to end against the same real virtual-cable
/// pattern as pieces Audio 1/4 -- a named sink, real audio playing into it via ffmpeg/paplay,
/// captured from its monitor through <see cref="MiniAudioCaptureSession"/>'s own drain thread and
/// <see cref="MiniAudioCaptureSession.SamplesAvailable"/> event (not the synchronous, one-shot
/// spike test from piece Audio 1 -- this is the real, continuous, event-driven path).
/// </summary>
public class MiniAudioCaptureSessionTests
{
    [RequiresPipeWireFact]
    public async Task SamplesAvailable_FiresWithRealNonSilentAudio_FromVirtualCableMonitor()
    {
        var sinkName = $"sstv_capture_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Capture_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        Process? toneProcess = null;
        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            var receivedChunks = new List<float[]>();
            var allReceived = new TaskCompletionSource();

            using var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100);
            session.SamplesAvailable += chunk =>
            {
                lock (receivedChunks)
                {
                    receivedChunks.Add(chunk.ToArray());
                    if (receivedChunks.Sum(c => c.Length) > 44100) // >1 second captured
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(allReceived.Task, completed);

            float[] allSamples;
            lock (receivedChunks)
            {
                allSamples = receivedChunks.SelectMany(c => c).ToArray();
            }

            Assert.True(allSamples.Length > 0, "No samples were ever received via SamplesAvailable.");
            var peak = allSamples.Max(Math.Abs);
            Assert.True(peak > 0.0f, $"Captured audio was silent (peak={peak}) -- session opened but no real audio flowed through the drain path.");
            Assert.False(session.HasStopped, "Session should not report stopped while the device is still active.");
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

    private static Process StartToneIntoSink(string sinkName, int durationSeconds)
    {
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
