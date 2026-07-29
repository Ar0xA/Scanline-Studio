using System.Diagnostics;
using Yoniq.Core.Audio.MiniAudio;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 6: the real playback path. Proven by actually playing a synthesized tone through
/// <see cref="MiniAudioPlaybackSession"/> into a real virtual sink and capturing it back out via
/// <see cref="MiniAudioCaptureSession"/>'s own monitor -- a genuine round trip through real
/// (virtual) hardware, not a mock on either end.
/// </summary>
public class MiniAudioPlaybackSessionTests
{
    private const int SampleRate = 44100;

    [RequiresPipeWireFact]
    public async Task Write_PlaysRealAudibleTone_CapturedBackViaMonitor()
    {
        var sinkName = $"sstv_playback_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Playback_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();

            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            var receivedChunks = new List<float[]>();
            var allReceived = new TaskCompletionSource();

            using var captureSession = new MiniAudioCaptureSession(monitor!.Id, SampleRate);
            captureSession.SamplesAvailable += chunk =>
            {
                lock (receivedChunks)
                {
                    receivedChunks.Add(chunk.ToArray());
                    if (receivedChunks.Sum(c => c.Length) > SampleRate) // >1 second captured
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            using var playbackSession = new MiniAudioPlaybackSession(sink!.Id, SampleRate);

            // A real 3-second, full-scale 1000Hz tone, written in realistic-sized chunks (not one
            // giant array) -- exercising Write's own partial-acceptance/back-pressure contract at
            // the same time as proving real audio flows.
            var tone = GenerateSineTone(frequencyHz: 1000, durationSeconds: 3, SampleRate);
            var offset = 0;
            while (offset < tone.Length)
            {
                var chunkLength = Math.Min(2048, tone.Length - offset);
                var written = playbackSession.Write(new ReadOnlySpan<float>(tone, offset, chunkLength));
                if (written == 0)
                {
                    await Task.Delay(5); // ring momentarily full -- back off and retry, per the documented contract
                    continue;
                }

                offset += written;
            }

            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(allReceived.Task, completed);

            float[] allSamples;
            lock (receivedChunks)
            {
                allSamples = receivedChunks.SelectMany(c => c).ToArray();
            }

            Assert.True(allSamples.Length > 0, "No samples were ever captured back from the monitor.");
            var peak = allSamples.Max(Math.Abs);
            Assert.True(peak > 0.1f, $"Captured-back audio was effectively silent (peak={peak}) -- the tone was not actually audible on the virtual cable.");

            // Drain-on-stop contract (piece Audio 2): after writing everything, PendingFrames must
            // eventually reach zero -- proving playback genuinely consumes what was written rather
            // than leaving it stuck in the ring forever.
            await playbackSession.DrainAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, playbackSession.PendingFrames);
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    private static float[] GenerateSineTone(double frequencyHz, double durationSeconds, int sampleRate)
    {
        var sampleCount = (int)(durationSeconds * sampleRate);
        var samples = new float[sampleCount];
        var phaseIncrement = 2 * Math.PI * frequencyHz / sampleRate;
        var phase = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (float)Math.Sin(phase);
            phase += phaseIncrement;
            if (phase >= 2 * Math.PI)
            {
                phase -= 2 * Math.PI;
            }
        }

        return samples;
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
