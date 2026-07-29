using System.Diagnostics;
using Yoniq.Abstractions.Audio;
using Yoniq.Core.Audio.MiniAudio;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Engine 1: <see cref="MiniAudioEngine"/>'s capture-only lifecycle, exercised the same way
/// every other real-hardware piece in this project has been -- a real virtual sink/monitor, not a
/// mock.
/// </summary>
public class MiniAudioEngineTests
{
    [RequiresPipeWireFact]
    public async Task StartCaptureAsync_FiresSamplesCaptured_FromVirtualCableMonitor()
    {
        var sinkName = $"sstv_engine_capture_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Capture_Test", out var moduleIdOutput);
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
            var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var engine = new MiniAudioEngine();
            engine.SamplesCaptured += chunk =>
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

            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);

            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(allReceived.Task, completed);

            float[] allSamples;
            lock (receivedChunks)
            {
                allSamples = receivedChunks.SelectMany(c => c).ToArray();
            }

            Assert.True(allSamples.Length > 0, "No samples were ever received via SamplesCaptured.");
            var peak = allSamples.Max(Math.Abs);
            Assert.True(peak > 0.0f, $"Captured audio was silent (peak={peak}) -- engine opened but no real audio flowed through.");

            await engine.StopCaptureAsync();
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

    [RequiresPipeWireFact]
    public async Task StartCaptureAsync_WhenAlreadyStarted_ThrowsInvalidOperationException()
    {
        var sinkName = $"sstv_engine_double_start_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Double_Start_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            await using var engine = new MiniAudioEngine();
            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartCaptureAsync(monitor!, sampleRate: 44100));

            await engine.StopCaptureAsync();
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [Fact]
    public async Task StopCaptureAsync_WhenNeverStarted_IsIdempotentNoOp()
    {
        await using var engine = new MiniAudioEngine();
        await engine.StopCaptureAsync();
        await engine.StopCaptureAsync(); // twice -- still a no-op, matching every session's Dispose convention
    }

    [Fact]
    public async Task StartCaptureAsync_WithUnknownDeviceId_ThrowsAudioDeviceUnavailableException()
    {
        await using var engine = new MiniAudioEngine();
        var bogusDevice = new AudioDeviceInfo("this-device-does-not-exist", "Bogus", MaxInputChannels: 1, MaxOutputChannels: 0, SupportedSampleRates: []);

        await Assert.ThrowsAsync<AudioDeviceUnavailableException>(() => engine.StartCaptureAsync(bogusDevice, sampleRate: 44100));
    }

    [Fact]
    public async Task StartCaptureAsync_WithOverLongDeviceId_ThrowsAudioDeviceUnavailableException()
    {
        // Exercises the ArgumentException (NativeAudio.EncodeFixedString) translation path
        // specifically -- a real, reachable failure mode distinct from "open failed", confirmed by
        // reading MiniAudioDeviceEnumerator's own identical handling of this case.
        await using var engine = new MiniAudioEngine();
        var overLongId = new string('x', 512);
        var bogusDevice = new AudioDeviceInfo(overLongId, "Bogus", MaxInputChannels: 1, MaxOutputChannels: 0, SupportedSampleRates: []);

        await Assert.ThrowsAsync<AudioDeviceUnavailableException>(() => engine.StartCaptureAsync(bogusDevice, sampleRate: 44100));
    }

    // Engine-level mirror of MiniAudioCaptureSessionTests.
    // Dispose_CalledFromWithinSamplesAvailableCallback_DoesNotSelfJoinDeadlock -- proves the
    // engine's own inline-vs-Task.Run dispatch (see MiniAudioEngine.DisposeCaptureSessionAsync)
    // correctly detects re-entrancy from the drain thread and avoids the deadlock Task.Run alone
    // would introduce (found by this project's Opus plan-review pass, not discovered by accident).
    [RequiresPipeWireFact]
    public async Task StopCaptureAsync_CalledFromWithinSamplesCapturedCallback_DoesNotSelfJoinDeadlock()
    {
        var sinkName = $"sstv_engine_selfjoin_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_SelfJoin_Test", out var moduleIdOutput);
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

            var stopReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyStoppedFromCallback = 0;

            await using var engine = new MiniAudioEngine();
            engine.SamplesCaptured += chunk =>
            {
                if (chunk.Length > 0 && Interlocked.Exchange(ref alreadyStoppedFromCallback, 1) == 0)
                {
                    // The actual hazard: calling StopCaptureAsync (synchronously blocking on it via
                    // .GetAwaiter().GetResult(), since this handler is not itself async) from the
                    // same thread that is currently invoking this very callback.
                    engine.StopCaptureAsync().GetAwaiter().GetResult();
                    stopReturned.TrySetResult();
                }
            };

            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);

            var completed = await Task.WhenAny(stopReturned.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(stopReturned.Task, completed); // else StopCaptureAsync hung -- the self-join guard regressed
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

    [Fact]
    public async Task DisposeAsync_IsIdempotent_WhenCalledTwice()
    {
        var engine = new MiniAudioEngine();
        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlaybackMembers_ThrowNotSupported_UntilPieceEngine2()
    {
        await using var engine = new MiniAudioEngine();
        var device = new AudioDeviceInfo("id", "name", MaxInputChannels: 0, MaxOutputChannels: 1, SupportedSampleRates: []);

        await Assert.ThrowsAsync<NotSupportedException>(() => engine.StartPlaybackAsync(device, sampleRate: 44100));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.StopPlaybackAsync());
        Assert.Throws<NotSupportedException>(() => engine.EnqueuePlaybackSamples(new float[10]));
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

    // Opus-review fix (shared convention across this test project): reading only stdout via
    // ReadToEnd() before WaitForExit(), while stderr is also redirected but never drained, is the
    // classic pipe-buffer deadlock. Reading both streams concurrently avoids it regardless of
    // which stream fills first.
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
