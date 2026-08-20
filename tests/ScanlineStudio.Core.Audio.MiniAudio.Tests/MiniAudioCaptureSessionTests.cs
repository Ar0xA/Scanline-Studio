using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Core.Audio.MiniAudio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
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

            using var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100, NullLogger.Instance);
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

            // Piece Engine 0: the default 16384-frame ring against a 5s tone at 44100Hz has no
            // reason to overflow in this short, clean run -- matches this codebase's existing
            // convention of not trying to deterministically force a hardware-timing-dependent
            // counter (see UnderrunCount, exercised the same way), just proving the counter is
            // reachable and sane during real, real-time-callback-driven capture.
            Assert.Equal(0, session.OverrunCount);
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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            var disposeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyDisposedFromCallback = 0;

            using var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100, NullLogger.Instance);
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

    // Band-1 fix (pre-Phase-2 audit): a throwing subscriber used to be silently swallowed with zero
    // trace, and a single try/catch around the whole invocation list meant one throwing subscriber
    // starved every other subscriber of delivery too. This test exercises both halves of the fix
    // against a real, continuously-running drain thread (not a synthetic/mocked one): a first
    // handler throws on every chunk, a second handler counts its own invocations -- if the
    // multicast-abort bug were still present, the second handler's count would stay 0.
    [RequiresPipeWireFact]
    public async Task SamplesAvailable_SubscriberThrows_IsRecordedAndDoesNotStarveOtherSubscribers()
    {
        var sinkName = $"sstv_throw_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Throw_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        Process? toneProcess = null;
        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            var survivingHandlerInvocations = 0;
            var enoughInvocations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thrownException = new InvalidOperationException("Deliberate test exception from a SamplesAvailable subscriber.");

            using var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100, NullLogger.Instance);
            session.SamplesAvailable += _ => throw thrownException;
            session.SamplesAvailable += chunk =>
            {
                if (chunk.Length > 0 && Interlocked.Increment(ref survivingHandlerInvocations) >= 3)
                {
                    enoughInvocations.TrySetResult();
                }
            };

            var completed = await Task.WhenAny(enoughInvocations.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(enoughInvocations.Task, completed);

            // The surviving handler kept firing after the throwing one -- proves per-handler
            // isolation, not just that the drain thread itself survived.
            Assert.True(Volatile.Read(ref survivingHandlerInvocations) >= 3);

            Assert.Same(thrownException, session.LastSubscriberException);
            Assert.True(session.SubscriberExceptionCount >= 3, $"Expected the throwing subscriber to have been counted at least 3 times, got {session.SubscriberExceptionCount}.");
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
    public async Task Constructor_GivenDrainThreadPriority_AppliesItToTheDrainThread()
    {
        var sinkName = $"sstv_priority_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Priority_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            using var explicitPrioritySession = new MiniAudioCaptureSession(
                monitor!.Id, sampleRate: 44100, NullLogger.Instance, drainThreadPriority: ThreadPriority.AboveNormal);
            Assert.Equal(ThreadPriority.AboveNormal, explicitPrioritySession.DrainThreadPriority);

            using var defaultPrioritySession = new MiniAudioCaptureSession(monitor.Id, sampleRate: 44100, NullLogger.Instance);
            Assert.Equal(ThreadPriority.Normal, defaultPrioritySession.DrainThreadPriority);
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Tier A Batch 1 re-audit round 2/3: an out-of-range drainThreadPriority (reachable via
    // AudioDeviceSettings.CaptureThreadPriority round-tripping through System.Text.Json with no
    // JsonStringEnumConverter registered -- STJ's default numeric enum (de)serialization does not
    // range-validate) used to reach _drainThread.Priority = priority AFTER the native device had
    // already opened, so a failure there orphaned a live, undrained capture device plus a permanent
    // MiniAudioContext reference -- nothing could ever close either, since the object never escaped
    // the constructor. Fixed by validating before the native open at all. Hardware-free ([Fact], not
    // [RequiresPipeWireFact]): the validation is the very first statement in the constructor, before
    // MiniAudioContext.Acquire() or anything device-related, so no real audio device is ever
    // touched regardless of whether one exists in this environment.
    [Fact]
    public void Constructor_GivenOutOfRangeDrainThreadPriority_ThrowsBeforeTouchingAnyDevice()
    {
        var exception = Record.Exception(() => new MiniAudioCaptureSession(
            "nonexistent-device-id", sampleRate: 44100, NullLogger.Instance, drainThreadPriority: (ThreadPriority)42));

        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    // Tier A Batch 1 re-audit round 3: the sibling gap round 2's own fix above left open --
    // AudioDeviceSettings.CaptureChannelSource is the OTHER settings-fed enum this constructor
    // takes, reachable through the identical unvalidated JSON round-trip. Left unguarded, an
    // out-of-range value would silently open the device STEREO and extract Left (ChannelSelect's
    // own native default), with no exception -- a real, previously-undetectable RX-goes-silently-
    // dead scenario if the actual signal is only on Right. Non-vacuous: without this guard, this
    // call reaches the native open for a nonexistent device and throws InvalidOperationException
    // instead, not ArgumentOutOfRangeException. Hardware-free for the same reason as the sibling
    // test above -- the validation runs before MiniAudioContext.Acquire()/the native open.
    [Fact]
    public void Constructor_GivenOutOfRangeChannelSource_ThrowsBeforeTouchingAnyDevice()
    {
        var exception = Record.Exception(() => new MiniAudioCaptureSession(
            "nonexistent-device-id", sampleRate: 44100, NullLogger.Instance, channelSource: (AudioChannelSource)7));

        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    // Stereo-capture-source backlog item: proves Left/Right channel selection actually routes
    // distinct content, not just "opens without crashing" -- a stereo source with an audible tone
    // on Left and silence on Right, captured once with AudioChannelSource.Left and once with
    // .Right against the SAME monitor, must show opposite loud/silent results.
    [RequiresPipeWireFact]
    public async Task Constructor_ChannelSourceLeftVsRight_CapturesDistinctChannelContent()
    {
        var sinkName = $"sstv_stereo_capture_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Stereo_Capture_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        Process? sourceProcess = null;
        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            sourceProcess = StartLeftLoudRightSilentStereoIntoSink(sinkName, durationSeconds: 8);

            // Concurrent, not sequential -- both need to observe the same limited-duration source
            // playing, so running them one after another would risk the source finishing before
            // the second capture even starts.
            var leftPeakTask = CapturePeakAsync(monitor!.Id, AudioChannelSource.Left);
            var rightPeakTask = CapturePeakAsync(monitor.Id, AudioChannelSource.Right);
            var leftPeak = await leftPeakTask;
            var rightPeak = await rightPeakTask;

            Assert.True(leftPeak > 0.1f, $"Left-selected capture should be loud (the audible source channel) -- got peak={leftPeak}.");
            Assert.True(rightPeak < 0.01f, $"Right-selected capture should be near-silent (the silent source channel) -- got peak={rightPeak}.");
        }
        finally
        {
            if (sourceProcess is not null && !sourceProcess.HasExited)
            {
                sourceProcess.Kill(entireProcessTree: true);
            }

            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Tier A Batch 1 re-audit round 5: HasStopped/OverrunCount had zero test coverage for their own
    // _lifetimeLock-based post-dispose guard, unlike MiniAudioRing's identical ObjectDisposedException
    // pair (MiniAudioRingTests.Write_Throws_AfterDispose/Read_Throws_AfterDispose) and
    // MiniAudioPlaybackSession's own concurrent-stress test below. Simple, direct pair first.
    [RequiresPipeWireFact]
    public async Task HasStoppedAndOverrunCount_ThrowObjectDisposedException_AfterDispose()
    {
        var sinkName = $"sstv_capture_dispose_guard_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Capture_Dispose_Guard_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100, NullLogger.Instance);
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => session.HasStopped);
            Assert.Throws<ObjectDisposedException>(() => session.OverrunCount);
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Companion stress test, mirroring MiniAudioPlaybackSessionTests'
    // ConcurrentUseAndDispose_NeverThrowsAnythingOtherThanObjectDisposedException exactly -- the
    // same _lifetimeLock fix protects both classes, and only the playback side had a test proving
    // it holds under real concurrent contention rather than just a clean sequential dispose. This
    // also exercises the round-5 finding's own read-lock/write-lock contention (userThread genuinely
    // blocks on HasStopped/OverrunCount's EnterReadLock while session.Dispose() below holds the
    // write lock), but NOT the round-5 stall's own timing specifically -- code-review correction:
    // session.Dispose() is synchronous on THIS (the main test) thread, so any block userThread hits
    // resolves (one way or another) before Dispose() itself returns, i.e. before
    // userThread.Join(10s) is ever reached; the 10s bound is a generic safety margin against the
    // test hanging for some OTHER reason (e.g. a self-join regression), not specifically sized to
    // absorb the round-5 stall.
    [RequiresPipeWireFact]
    public async Task ConcurrentUseAndDispose_NeverThrowsAnythingOtherThanObjectDisposedException()
    {
        var sinkName = $"sstv_capture_race_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Capture_Race_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        Process? toneProcess = null;
        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            var session = new MiniAudioCaptureSession(monitor!.Id, sampleRate: 44100, NullLogger.Instance);
            using var start = new Barrier(2);

            var userThread = new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (true)
                    {
                        _ = session.HasStopped;
                        _ = session.OverrunCount;
                        _ = session.LastSubscriberException;
                        _ = session.SubscriberExceptionCount;
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
            if (toneProcess is not null && !toneProcess.HasExited)
            {
                toneProcess.Kill(entireProcessTree: true);
            }

            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    private static async Task<float> CapturePeakAsync(string deviceId, AudioChannelSource channelSource)
    {
        var receivedChunks = new List<float[]>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new MiniAudioCaptureSession(deviceId, sampleRate: 44100, NullLogger.Instance, channelSource: channelSource);
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

        lock (receivedChunks)
        {
            var allSamples = receivedChunks.SelectMany(c => c).ToArray();
            return allSamples.Length == 0 ? 0f : allSamples.Max(Math.Abs);
        }
    }

    private static Process StartLeftLoudRightSilentStereoIntoSink(string sinkName, int durationSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList =
            {
                "-c",
                $"ffmpeg -f lavfi -i \"sine=frequency=1000:duration={durationSeconds}\" " +
                $"-f lavfi -i \"anullsrc=r=44100:cl=mono:d={durationSeconds}\" " +
                "-filter_complex \"[0:a][1:a]amerge=inputs=2[a]\" -map \"[a]\" -f wav - 2>/dev/null " +
                $"| paplay --device={sinkName}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start stereo source-generation process.");
        Thread.Sleep(500); // let the pipeline actually start producing audio before the test proceeds
        return process;
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
