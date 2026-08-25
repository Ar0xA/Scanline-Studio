using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ScanlineStudio.Abstractions.Audio;
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

    // Sound-FIFO-buffer-size backlog item: proves opening with a non-default period size/count
    // doesn't break device open or the real audio round-trip -- the exact resulting hardware
    // buffer size isn't reliably observable across backends (miniaudio's own docs say
    // periodSizeInFrames/periodSizeInMilliseconds are hints, not guarantees), so "still opens,
    // audio still flows" is the honest bar here, not byte-exact latency verification.
    [RequiresPipeWireFact]
    public async Task Write_WithNonDefaultPeriodSizeAndCount_StillOpensAndRoundTripsRealAudio()
    {
        var sinkName = $"sstv_playback_period_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Playback_Period_Test", out var moduleIdOutput);
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
            var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var captureSession = new MiniAudioCaptureSession(
                monitor!.Id, SampleRate, NullLogger.Instance, periodSizeInFrames: 512, periods: 4);
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

            using var playbackSession = new MiniAudioPlaybackSession(
                sink!.Id, SampleRate, periodSizeInFrames: 512, periods: 4);

            var tone = GenerateSineTone(frequencyHz: 1000, durationSeconds: 2, SampleRate);
            var offset = 0;
            while (offset < tone.Length)
            {
                var chunkLength = Math.Min(2048, tone.Length - offset);
                var written = playbackSession.Write(new ReadOnlySpan<float>(tone, offset, chunkLength));
                if (written == 0)
                {
                    await Task.Delay(5);
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
            Assert.True(peak > 0.1f, $"Captured-back audio was effectively silent (peak={peak}) -- non-default period size/count broke real audio flow.");
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Stereo-TX-toggle backlog item: proves a mono-written signal is genuinely duplicated to BOTH
    // output channels, not just "opens without crashing" -- captures the sink's monitor once with
    // AudioChannelSource.Left and once with .Right and requires both to be loud.
    [RequiresPipeWireFact]
    public async Task Write_WithStereoTxEnabled_DuplicatesMonoSignalToBothOutputChannels()
    {
        var sinkName = $"sstv_stereo_tx_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Stereo_Tx_Test", out var moduleIdOutput);
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

            using var playbackSession = new MiniAudioPlaybackSession(sink!.Id, SampleRate, stereoTx: true);

            var leftPeakTask = CapturePeakAsync(monitor!.Id, AudioChannelSource.Left);
            var rightPeakTask = CapturePeakAsync(monitor.Id, AudioChannelSource.Right);

            var tone = GenerateSineTone(frequencyHz: 1000, durationSeconds: 3, SampleRate);
            var offset = 0;
            while (offset < tone.Length)
            {
                var chunkLength = Math.Min(2048, tone.Length - offset);
                var written = playbackSession.Write(new ReadOnlySpan<float>(tone, offset, chunkLength));
                if (written == 0)
                {
                    await Task.Delay(5);
                    continue;
                }

                offset += written;
            }

            var leftPeak = await leftPeakTask;
            var rightPeak = await rightPeakTask;

            Assert.True(leftPeak > 0.1f, $"Left channel should carry the duplicated mono TX signal -- got peak={leftPeak}.");
            Assert.True(rightPeak > 0.1f, $"Right channel should carry the duplicated mono TX signal -- got peak={rightPeak}.");
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Audit-relevant regression test for the real underrun-padding bug fixed as part of adding
    // stereo TX (see playback_session_data_callback's own comment in native/scanline_audio.c): a
    // naive mono-shaped memset would only ever zero the L channel's bytes on underrun, leaving R
    // with stale/garbage backend memory. Opens stereo-TX playback and writes NOTHING at all, so
    // every single callback underruns -- both captured channels must still read back silence.
    [RequiresPipeWireFact]
    public async Task Write_StereoTxWithNoDataWritten_UnderrunsSilentlyOnBothChannels()
    {
        var sinkName = $"sstv_stereo_tx_underrun_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Stereo_Tx_Underrun_Test", out var moduleIdOutput);
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

            using var playbackSession = new MiniAudioPlaybackSession(sink!.Id, SampleRate, stereoTx: true);

            // Deliberately never call Write -- give the real-time callback ample time to fire
            // repeatedly against an empty ring (guaranteed underrun every time).
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.True(playbackSession.UnderrunCount > 0, "Expected genuine underruns with nothing ever written -- test setup itself is wrong if this is 0.");

            // Tier A Batch 1 re-audit round 8: discriminates "counts real-time CALLBACKS that
            // underran" (the actual, documented, correct semantics -- scanline_audio.c's own
            // underrun_count field comment) from "counts FRAMES padded with silence" (what the
            // native header's own doc comment wrongly said before this round's fix). 2 seconds of
            // continuous underrun at SampleRate would drop on the order of 2*SampleRate (~88,200)
            // frames if this were a frame counter -- real-time callbacks firing that often (every
            // ~11 microseconds) isn't a real backend period size. A generously loose upper bound,
            // comfortably above any real callback count for a 2-second window at any reasonable
            // period size, still catches a regression back to frame-counting by roughly two orders
            // of magnitude.
            Assert.True(playbackSession.UnderrunCount < 20000, $"UnderrunCount ({playbackSession.UnderrunCount}) is high enough to suggest it's counting padded FRAMES, not underrun CALLBACKS -- 2s at {SampleRate}Hz would pad ~{2 * SampleRate} frames if so, but real-time callbacks don't fire anywhere near that often.");

            var leftPeak = await CapturePeakAsync(monitor!.Id, AudioChannelSource.Left, requiredSeconds: 1);
            var rightPeak = await CapturePeakAsync(monitor.Id, AudioChannelSource.Right, requiredSeconds: 1);

            Assert.True(leftPeak < 0.01f, $"Left channel should be silence on underrun, not garbage -- got peak={leftPeak}.");
            Assert.True(rightPeak < 0.01f, $"Right channel should be silence on underrun, not garbage (the exact bug this fix closes) -- got peak={rightPeak}.");
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [RequiresPipeWireFact]
    public async Task Write_ZeroLengthSpan_ReturnsZero_DoesNotThrow()
    {
        // Round-1 functional-audit regression test, mirroring MiniAudioRingTests' equivalent case:
        // Span<T>.GetPinnableReference returns a null ref for a zero-length span, so `fixed` pins
        // NULL -- the native shim's own NULL guard then returned -1, silently violating this
        // method's own documented "0..data.Length" contract. Gated on real PipeWire hardware, same
        // as every other test in this file, since MiniAudioPlaybackSession's constructor always
        // opens a real device -- there is no hardware-free construction path for this type.
        var sinkName = $"sstv_playback_zero_write_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Playback_Zero_Write_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();

            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            using var playbackSession = new MiniAudioPlaybackSession(sink!.Id, SampleRate);

            var writeCount = playbackSession.Write(ReadOnlySpan<float>.Empty);

            Assert.Equal(0, writeCount);
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    private static async Task<float> CapturePeakAsync(string deviceId, AudioChannelSource channelSource, int requiredSeconds = 1)
    {
        var receivedChunks = new List<float[]>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredSamples = SampleRate * requiredSeconds;

        using var session = new MiniAudioCaptureSession(deviceId, SampleRate, NullLogger.Instance, channelSource: channelSource);
        session.SamplesAvailable += chunk =>
        {
            lock (receivedChunks)
            {
                receivedChunks.Add(chunk.ToArray());
                if (receivedChunks.Sum(c => c.Length) > requiredSamples)
                {
                    allReceived.TrySetResult();
                }
            }
        };

        var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(allReceived.Task, completed);

        lock (receivedChunks)
        {
            var allSamples = receivedChunks.SelectMany(c => c).ToArray();
            return allSamples.Length == 0 ? 0f : allSamples.Max(Math.Abs);
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
