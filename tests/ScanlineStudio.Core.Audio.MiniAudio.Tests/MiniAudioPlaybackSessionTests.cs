using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ScanlineStudio.Core.Audio.MiniAudio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();

            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            var receivedChunks = new List<float[]>();
            // RunContinuationsAsynchronously: without it, TrySetResult below (called from inside
            // MiniAudioCaptureSession's own drain-thread callback) would run this await's
            // continuation synchronously on that drain thread -- and if that continuation ever
            // called back into disposing that same session, it would self-join and hang forever.
            // See MiniAudioCaptureSession.Dispose's own defensive fix for the production-code side
            // of this; found via this exact test hanging during an opus-review fix pass.
            var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var captureSession = new MiniAudioCaptureSession(monitor!.Id, SampleRate, NullLogger.Instance);
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

    // Third-opus-review fix: exercises the ReaderWriterLockSlim fix for real, against a real
    // device -- not just the same lock pattern proven in isolation by MiniAudioRingTests'
    // equivalent stress test. Hammers Write/PendingFrames/UnderrunCount/HasStopped from another
    // thread while Dispose() runs concurrently on this one; the only acceptable outcome on the
    // other thread is a normal call or ObjectDisposedException -- anything else (a crash, a
    // native-level use-after-free, a hang) fails the test.
    [RequiresPipeWireFact]
    public async Task ConcurrentUseAndDispose_NeverThrowsAnythingOtherThanObjectDisposedException()
    {
        var sinkName = $"sstv_playback_race_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Playback_Race_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            var session = new MiniAudioPlaybackSession(sink!.Id, SampleRate);
            var buffer = new float[256];
            using var start = new Barrier(2);

            var userThread = new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (true)
                    {
                        session.Write(buffer);
                        _ = session.PendingFrames;
                        _ = session.UnderrunCount;
                        _ = session.HasStopped;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Expected once Dispose wins the race.
                }
            })
            {
                IsBackground = true,
            };
            userThread.Start();

            start.SignalAndWait();
            session.Dispose();

            Assert.True(userThread.Join(TimeSpan.FromSeconds(10)), "User thread did not observe Dispose within the expected bound.");
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

    // Opus-review fix: reading only stdout via ReadToEnd() before WaitForExit(), while stderr is
    // also redirected but never drained, is the classic pipe-buffer deadlock -- if pactl ever
    // writes enough to stderr to fill its OS pipe buffer, it blocks writing to a stream nobody is
    // reading, while this thread blocks reading a stream (stdout) that will never produce more
    // data because the child is stuck. Reading both streams concurrently (not sequentially) avoids
    // it regardless of which stream fills first.
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
