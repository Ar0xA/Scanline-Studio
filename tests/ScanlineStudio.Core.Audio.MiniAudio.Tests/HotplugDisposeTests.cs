using System.Diagnostics;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 8: a real device disappearing mid-session, tested against an actual PulseAudio/
/// PipeWire-pulse virtual sink unloaded while a session is still open on it -- not a mock. This
/// found a real, reproducible bug: disposing a <see cref="MiniAudioCaptureSession"/> or
/// <see cref="MiniAudioPlaybackSession"/> whose device had already disappeared hung indefinitely
/// (observed past 8 minutes in this project's own dev sandbox), because both close through
/// miniaudio's PulseAudio backend's `ma_wait_for_operation__pulse`, which loops on the server
/// completing an operation that, once the backing sink is gone, it never will (confirmed directly
/// against the pinned miniaudio.h -- not assumed). Fixed with a bounded-timeout close on both
/// session types (see <see cref="MiniAudioCaptureSession.CloseTimeout"/>'s doc comment); this test
/// proves the fix actually bounds Dispose's real wall-clock time, not just that it compiles.
///
/// This also falsified this piece's original premise: <c>HasStopped</c> (wired up in pieces 5/6
/// specifically to be exercised here) does NOT become true when a device disappears this way --
/// on the PulseAudio backend it only fires from an actual server-side suspend/resume. See each
/// session's own <c>HasStopped</c> doc comment for the corrected, verified claim.
/// </summary>
public class HotplugDisposeTests
{
    [RequiresPipeWireFact]
    public async Task CaptureSession_DisposeCompletesWithinTimeout_WhenDeviceDisappearsMidSession()
    {
        var sinkName = $"sstv_hotplug_capture_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=HotplugCapture", out var moduleIdOut);
        var moduleId = moduleIdOut.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        Process? toneProcess = null;
        var moduleUnloaded = false;
        MiniAudioCaptureSession? session = null;
        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 15);

            session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100);
            long totalReceived = 0;
            // See MiniAudioCaptureSessionTests' identical fix and comment: without
            // RunContinuationsAsynchronously, a synchronous TrySetResult from the drain thread
            // could run this await's continuation on that same thread, risking a self-join hang.
            var receivedSome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SamplesAvailable += chunk =>
            {
                if (System.Threading.Interlocked.Add(ref totalReceived, chunk.Length) > 0)
                {
                    receivedSome.TrySetResult();
                }
            };

            var gotData = await Task.WhenAny(receivedSome.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(receivedSome.Task, gotData); // sanity: real audio must actually flow before we can test disappearance

            RunPactl($"unload-module {moduleId}", out _);
            moduleUnloaded = true;

            // Give the "no more data" state a moment to actually settle (mirrors real hot-unplug
            // timing) before disposing -- disposing instantly after unload would be a weaker test
            // of the same thing.
            await Task.Delay(1000);

            var stopwatch = Stopwatch.StartNew();
            session.Dispose();
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(8),
                $"Dispose took {stopwatch.Elapsed} -- expected it to be bounded by the session's own ~5s close timeout, not hang indefinitely as it did before this fix.");
        }
        finally
        {
            session?.Dispose(); // no-op if already disposed above -- Dispose is idempotent
            if (toneProcess is not null && !toneProcess.HasExited)
            {
                toneProcess.Kill(entireProcessTree: true);
            }

            if (!moduleUnloaded)
            {
                RunPactl($"unload-module {moduleId}", out _);
            }
        }
    }

    [RequiresPipeWireFact]
    public async Task PlaybackSession_DisposeCompletesWithinTimeout_WhenDeviceDisappearsMidSession()
    {
        var sinkName = $"sstv_hotplug_playback_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=HotplugPlayback", out var moduleIdOut);
        var moduleId = moduleIdOut.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        var moduleUnloaded = false;
        MiniAudioPlaybackSession? session = null;
        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            session = new MiniAudioPlaybackSession(sink!.Id, sampleRate: 44100);

            var tone = GenerateSineTone(frequencyHz: 1000, durationSeconds: 3, sampleRate: 44100);
            var offset = 0;
            while (offset < tone.Length)
            {
                var chunkLength = Math.Min(2048, tone.Length - offset);
                var written = session.Write(new ReadOnlySpan<float>(tone, offset, chunkLength));
                if (written == 0)
                {
                    await Task.Delay(5);
                    continue;
                }

                offset += written;
            }

            await Task.Delay(500); // let some of it actually start playing before we pull the device out

            RunPactl($"unload-module {moduleId}", out _);
            moduleUnloaded = true;

            await Task.Delay(1000);

            var stopwatch = Stopwatch.StartNew();
            session.Dispose();
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(8),
                $"Dispose took {stopwatch.Elapsed} -- expected it to be bounded by the session's own ~5s close timeout, not hang indefinitely.");
        }
        finally
        {
            session?.Dispose();
            if (!moduleUnloaded)
            {
                RunPactl($"unload-module {moduleId}", out _);
            }
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
        Thread.Sleep(500);
        return process;
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
