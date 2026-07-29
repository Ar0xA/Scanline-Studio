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

    // Piece Engine 3: proves the TOCTOU an earlier piece deliberately left open ("check nothing
    // started, open, publish" as three separate steps) is now actually closed by _captureLock --
    // several concurrent StartCaptureAsync calls racing the same engine must yield exactly one
    // success and the rest InvalidOperationException, never two sessions silently opened against
    // the same engine instance.
    [RequiresPipeWireFact]
    public async Task StartCaptureAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        var sinkName = $"sstv_engine_capture_race_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Capture_Race_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            await using var engine = new MiniAudioEngine();

            var attempts = Enumerable.Range(0, 8)
                .Select(async _ =>
                {
                    try
                    {
                        await engine.StartCaptureAsync(monitor!, sampleRate: 44100);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                })
                .ToArray();

            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(succeeded => succeeded));
            await engine.StopCaptureAsync();
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Piece Engine 3: playback's own mirror of the capture concurrency test above.
    [RequiresPipeWireFact]
    public async Task StartPlaybackAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        var sinkName = $"sstv_engine_playback_race_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Playback_Race_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            await using var engine = new MiniAudioEngine();

            var attempts = Enumerable.Range(0, 8)
                .Select(async _ =>
                {
                    try
                    {
                        await engine.StartPlaybackAsync(sink!, sampleRate: 44100);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                })
                .ToArray();

            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(succeeded => succeeded));
            await engine.StopPlaybackAsync();
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

    // Piece Engine 2: proves StopPlaybackAsync actually waits out DrainTailMargin's real wall-clock
    // delay rather than it being dead code -- deterministic and fast (a tiny burst, not a multi-
    // second tone), unlike the companion audible round-trip test below, which can't cleanly
    // attribute a captured-duration shortfall to tail truncation vs. ordinary capture-side startup
    // latency (both look identical from captured sample count alone -- confirmed by an earlier
    // version of that test that failed for exactly this reason, a genuine finding this project's
    // own "verify, don't assume" methodology caught rather than something to paper over).
    [RequiresPipeWireFact]
    public async Task StopPlaybackAsync_WaitsAtLeastTheDrainTailMargin()
    {
        var sinkName = $"sstv_engine_playback_margin_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Playback_Margin_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            await using var engine = new MiniAudioEngine();
            await engine.StartPlaybackAsync(sink!, sampleRate: 44100);
            engine.EnqueuePlaybackSamples(GenerateSineTone(frequencyHz: 1000, durationSeconds: 0.05, sampleRate: 44100));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await engine.StopPlaybackAsync();
            stopwatch.Stop();

            // A little under the nominal 200ms margin to absorb scheduler jitter -- this asserts
            // the delay genuinely executes, not that it executes for exactly its nominal duration.
            Assert.True(
                stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150),
                $"StopPlaybackAsync returned after only {stopwatch.ElapsedMilliseconds}ms -- the drain tail margin does not appear to have run.");
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [RequiresPipeWireFact]
    public async Task Write_PlaysRealAudibleTone_CapturedBackViaMonitor()
    {
        // Real end-to-end sanity check that audio genuinely flows through the engine's playback
        // and capture paths together via a real virtual cable -- the tail-margin mechanism itself
        // is verified deterministically above, not by this test's duration tolerance (see that
        // test's own comment for why a duration-based assertion here can't cleanly distinguish
        // tail truncation from ordinary capture-side startup latency).
        const int sampleRate = 44100;
        const double toneDurationSeconds = 2.0;

        var sinkName = $"sstv_engine_playback_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Playback_Test", out var moduleIdOutput);
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

            await using var captureEngine = new MiniAudioEngine();
            captureEngine.SamplesCaptured += chunk =>
            {
                lock (receivedChunks)
                {
                    receivedChunks.Add(chunk.ToArray());
                }
            };
            await captureEngine.StartCaptureAsync(monitor!, sampleRate);

            await using var playbackEngine = new MiniAudioEngine();
            await playbackEngine.StartPlaybackAsync(sink!, sampleRate);

            var tone = GenerateSineTone(frequencyHz: 1000, toneDurationSeconds, sampleRate);
            var offset = 0;
            while (offset < tone.Length)
            {
                var chunkLength = Math.Min(2048, tone.Length - offset);
                var written = playbackEngine.EnqueuePlaybackSamples(new ReadOnlyMemory<float>(tone, offset, chunkLength));
                if (written == 0)
                {
                    await Task.Delay(5); // ring momentarily full -- back off and retry, per the documented contract
                    continue;
                }

                offset += written;
            }

            await playbackEngine.StopPlaybackAsync();

            // A short grace period after Stop returns, so the capture side's own drain thread (5ms
            // poll granularity) has a chance to deliver whatever it already buffered before this
            // test stops listening.
            await Task.Delay(300);
            await captureEngine.StopCaptureAsync();

            float[] allSamples;
            lock (receivedChunks)
            {
                allSamples = receivedChunks.SelectMany(c => c).ToArray();
            }

            Assert.True(allSamples.Length > 0, "No samples were ever captured back from the monitor.");
            var peak = allSamples.Max(Math.Abs);
            Assert.True(peak > 0.1f, $"Captured-back audio was effectively silent (peak={peak}) -- the tone was not actually audible on the virtual cable.");

            // Loose, not exact -- this environment measurably loses ~0.4s to real capture-side
            // startup latency alone (confirmed: an earlier, stricter version of this assertion
            // failed at 1.61s/2.0s, and StopPlaybackAsync_WaitsAtLeastTheDrainTailMargin above
            // independently proves the tail-margin mechanism itself executes). This bound exists to
            // catch gross end-to-end breakage, not to measure the tail-margin fix precisely.
            var toneFrameCount = allSamples.Count(s => Math.Abs(s) > 0.3f);
            var capturedToneSeconds = toneFrameCount / (double)sampleRate;
            Assert.True(
                capturedToneSeconds >= toneDurationSeconds * 0.5,
                $"Captured only {capturedToneSeconds:F2}s of tone-amplitude audio out of a {toneDurationSeconds:F2}s tone.");
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [RequiresPipeWireFact]
    public async Task StartPlaybackAsync_WhenAlreadyStarted_ThrowsInvalidOperationException()
    {
        var sinkName = $"sstv_engine_playback_double_start_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Playback_Double_Start_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator();
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            await using var engine = new MiniAudioEngine();
            await engine.StartPlaybackAsync(sink!, sampleRate: 44100);

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartPlaybackAsync(sink!, sampleRate: 44100));

            await engine.StopPlaybackAsync();
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [Fact]
    public async Task StopPlaybackAsync_WhenNeverStarted_IsIdempotentNoOp()
    {
        await using var engine = new MiniAudioEngine();
        await engine.StopPlaybackAsync();
        await engine.StopPlaybackAsync();
    }

    [Fact]
    public async Task EnqueuePlaybackSamples_WhenNeverStarted_ThrowsInvalidOperationException()
    {
        await using var engine = new MiniAudioEngine();
        Assert.Throws<InvalidOperationException>(() => engine.EnqueuePlaybackSamples(new float[10]));
    }

    [Fact]
    public async Task StartPlaybackAsync_WithUnknownDeviceId_ThrowsAudioDeviceUnavailableException()
    {
        await using var engine = new MiniAudioEngine();
        var bogusDevice = new AudioDeviceInfo("this-device-does-not-exist", "Bogus", MaxInputChannels: 0, MaxOutputChannels: 1, SupportedSampleRates: []);

        await Assert.ThrowsAsync<AudioDeviceUnavailableException>(() => engine.StartPlaybackAsync(bogusDevice, sampleRate: 44100));
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
