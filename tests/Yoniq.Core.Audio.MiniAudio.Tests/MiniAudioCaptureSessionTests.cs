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
            // RunContinuationsAsynchronously: a real, reproduced bug found during an opus-review
            // fix pass, not a hypothetical -- without this, TrySetResult below (called from inside
            // SamplesAvailable's drain-thread callback) runs this await's continuation
            // synchronously on that same drain thread. That continuation falls through to this
            // method's `using var session` disposal, whose Dispose() used to unconditionally call
            // _drainThread.Join() -- a thread joining itself, which blocks forever with no timeout
            // (confirmed: reproduced as an unresponsive test process only SIGKILL could stop).
            // MiniAudioCaptureSession.Dispose now also guards against this defensively, but this
            // fix belongs here too rather than relying solely on that guard.
            var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

    // Second-opus-review fix: directly exercises the self-join guard itself (the previous test
    // only exercised it incidentally, via a TaskCompletionSource race that happened to no longer
    // reproduce once RunContinuationsAsynchronously was added). Disposes the session from
    // *synchronously inside* its own SamplesAvailable callback -- on the drain thread -- which is
    // exactly the shape Dispose's `Thread.CurrentThread != _drainThread` check exists for. If that
    // guard ever regresses, Dispose() hangs forever (a thread joining itself), so this asserts
    // completion within a bound rather than just calling Dispose and trusting it returns.
    [RequiresPipeWireFact]
    public async Task Dispose_CalledFromWithinSamplesAvailableCallback_DoesNotSelfJoinDeadlock()
    {
        var sinkName = $"sstv_selfjoin_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SelfJoinTest", out var moduleIdOutput);
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

            var disposeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyDisposedFromCallback = 0;

            using var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100);
            session.SamplesAvailable += chunk =>
            {
                if (chunk.Length > 0 && Interlocked.Exchange(ref alreadyDisposedFromCallback, 1) == 0)
                {
                    // The actual hazard being reproduced: calling Dispose() from the same thread
                    // that is currently invoking this very callback.
                    session!.Dispose();
                    disposeReturned.TrySetResult();
                }
            };

            var completed = await Task.WhenAny(disposeReturned.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(disposeReturned.Task, completed); // else Dispose() hung -- the self-join guard regressed

            // The `using` statement's own Dispose() call at the end of this scope now runs
            // *again* on top of the one already performed inside the callback above -- exercising
            // Dispose's Interlocked.Exchange idempotency guard (piece: second-opus-review fix #A4)
            // in the same real scenario, not just in isolation.
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
