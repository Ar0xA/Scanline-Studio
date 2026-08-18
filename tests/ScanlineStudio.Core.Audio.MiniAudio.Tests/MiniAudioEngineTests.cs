using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Core.Audio.MiniAudio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            var receivedChunks = new List<float[]>();
            var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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

    // Round-1 functional-audit finding (Batch 1, native/managed audio boundary): before this fix,
    // MiniAudioEngine.OnCaptureSamplesAvailable forwarded to SamplesCaptured via a single bare
    // `?.Invoke(samples)` -- MiniAudioCaptureSession's own drain loop DOES isolate handler
    // exceptions (DrainLoop, per-handler try/catch), but that guarantee only ever protected the
    // engine's own single registered forwarder handler. It never reached the engine's real
    // multi-subscriber case (production has both a decoder handler and a waterfall handler on
    // IAudioEngine.SamplesCaptured, via SstvSessionService) -- one throwing handler would have
    // stopped every handler registered after it in invocation-list order from ever running, for
    // every subsequent chunk too (Invoke's fail-fast semantics on a multicast delegate). This test
    // proves the fix: a first handler that throws on every single chunk must not prevent a second,
    // independently-registered handler from continuing to receive every chunk.
    [RequiresPipeWireFact]
    public async Task SamplesCaptured_WhenOneSubscriberThrows_OtherSubscribersStillReceiveEveryChunk()
    {
        var sinkName = $"sstv_engine_subscriber_isolation_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Subscriber_Isolation_Test", out var moduleIdOutput);
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

            var throwingHandlerCallCount = 0;
            var survivingHandlerSampleCount = 0;
            var survivorReceivedEnough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);

            // Registered first deliberately -- a bare `?.Invoke` walks the invocation list in
            // registration order and stops entirely on the first unhandled exception, so this
            // ordering is what would have starved the second handler under the pre-fix behavior.
            engine.SamplesCaptured += _ =>
            {
                Interlocked.Increment(ref throwingHandlerCallCount);
                throw new InvalidOperationException("Deliberate test failure -- every chunk, by design.");
            };
            engine.SamplesCaptured += chunk =>
            {
                if (Interlocked.Add(ref survivingHandlerSampleCount, chunk.Length) > 44100) // >1 second
                {
                    survivorReceivedEnough.TrySetResult();
                }
            };

            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);

            var completed = await Task.WhenAny(survivorReceivedEnough.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(survivorReceivedEnough.Task, completed);

            Assert.True(Volatile.Read(ref throwingHandlerCallCount) > 0, "The throwing handler itself never ran -- test setup is wrong.");

            // Round-2 functional-audit addition: proves CaptureLastSubscriberException/
            // CaptureSubscriberExceptionCount still observe a SamplesCaptured subscriber throwing
            // now that OnCaptureSamplesAvailable catches it internally (round-1 fix) instead of
            // letting it propagate into the session's own DrainLoop, which is what those two
            // properties used to (accidentally) rely on to ever see anything -- without this
            // assertion, round 1's isolation fix could silently regress both diagnostics to always
            // report "nothing ever threw" and nothing here would catch it.
            Assert.NotNull(engine.CaptureLastSubscriberException);
            Assert.IsType<InvalidOperationException>(engine.CaptureLastSubscriberException);
            Assert.True(engine.CaptureSubscriberExceptionCount > 0, "CaptureSubscriberExceptionCount should reflect the throwing handler's failures.");

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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);

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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);

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
        await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        await engine.StopCaptureAsync();
        await engine.StopCaptureAsync(); // twice -- still a no-op, matching every session's Dispose convention
    }

    [Fact]
    public async Task CaptureOverrunCount_IsReachableThroughTheIAudioEngineInterface_AndZeroBeforeAnyCapture()
    {
        // Regression guard for the interface promotion (spec/17-rx-telemetry-feasibility.md's
        // "Buffer · XRUN" readout): this property used to be concrete-class-only, deliberately NOT
        // on IAudioEngine -- a caller that only ever holds the interface type (every real production
        // caller, via ISstvSessionService) must be able to reach it.
        IAudioEngine engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        try
        {
            Assert.Equal(0, engine.CaptureOverrunCount);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartCaptureAsync_WithUnknownDeviceId_ThrowsAudioDeviceUnavailableException()
    {
        await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        var bogusDevice = new AudioDeviceInfo("this-device-does-not-exist", "Bogus", MaxInputChannels: 1, MaxOutputChannels: 0, SupportedSampleRates: []);

        await Assert.ThrowsAsync<AudioDeviceUnavailableException>(() => engine.StartCaptureAsync(bogusDevice, sampleRate: 44100));
    }

    [Fact]
    public async Task StartCaptureAsync_WithOverLongDeviceId_ThrowsAudioDeviceUnavailableException()
    {
        // Exercises the ArgumentException (NativeAudio.EncodeFixedString) translation path
        // specifically -- a real, reachable failure mode distinct from "open failed", confirmed by
        // reading MiniAudioDeviceEnumerator's own identical handling of this case.
        await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            toneProcess = StartToneIntoSink(sinkName, durationSeconds: 5);

            var stopReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyStoppedFromCallback = 0;

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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

    // Round-1-engine-review finding: the self-join guard above only ever exercised the UNCONTENDED
    // path (one Stop caller, no lock contention). If a second, external StopCaptureAsync call won
    // _captureLock BEFORE the in-callback one asked for it, the previous implementation could
    // deadlock: the external caller would claim the session and dispatch its Dispose() to a pool
    // thread (Task.Run), which blocks on the session's own unbounded _drainThread.Join() -- while
    // the drain thread itself (running the callback, mid-way through its own StopCaptureAsync
    // call) is blocked waiting for the very same lock the external caller already holds. Fixed by
    // releasing _captureLock as soon as the session reference is claimed (see
    // MiniAudioEngine.ClaimCaptureSessionAsync's own doc comment), before ever calling Dispose.
    //
    // Round-2-engine-review fix: an earlier version of this test signaled the external caller and
    // then called StopCaptureAsync immediately, which let the drain thread reliably win the lock
    // race in practice (queuing/dispatching the external Task.Run's continuation takes longer than
    // a few field reads) -- meaning this test could pass identically whether or not the fix above
    // actually worked. The Thread.Sleep(100) in the callback below deliberately gives the external
    // caller time to win the lock first, so this test genuinely forces the external-caller-holds-
    // the-lock interleaving rather than relying on scheduling luck to exercise it.
    //
    // This does NOT cover the narrower, still-open race documented on MiniAudioEngine's own class
    // doc comment (a contended WaitAsync itself resuming this call's continuation on a different
    // thread than the one it started on) -- that requires forcing contention at the exact moment
    // of the DRAIN THREAD's own lock wait, not the external caller's, which this interleaving
    // doesn't produce.
    [RequiresPipeWireFact]
    public async Task StopCaptureAsync_ContendedByExternalCallerWhileSelfDisposing_DoesNotDeadlock()
    {
        var sinkName = $"sstv_engine_selfjoin_race_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_SelfJoin_Race_Test", out var moduleIdOutput);
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

            var selfStopReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var externalStopStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyStoppedFromCallback = 0;

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
            engine.SamplesCaptured += chunk =>
            {
                if (chunk.Length > 0 && Interlocked.Exchange(ref alreadyStoppedFromCallback, 1) == 0)
                {
                    // Round-2-engine-review fix: signal-then-immediately-call left the two Stop
                    // calls' actual arrival at _captureLock effectively unraced in practice (the
                    // drain thread reliably won, since queuing the external Task.Run's continuation
                    // takes longer than a few field reads) -- so this test could pass identically
                    // whether or not the deadlock it's named for was actually fixed. A short,
                    // deliberate delay here gives the external caller time to actually acquire
                    // _captureLock FIRST, so this call genuinely contends for it instead of finding
                    // it free.
                    externalStopStarting.TrySetResult();
                    Thread.Sleep(100);
                    engine.StopCaptureAsync().GetAwaiter().GetResult();
                    selfStopReturned.TrySetResult();
                }
            };

            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);

            var externalStopTask = Task.Run(async () =>
            {
                await externalStopStarting.Task.ConfigureAwait(false);
                await engine.StopCaptureAsync().ConfigureAwait(false);
            });

            var allDone = Task.WhenAll(selfStopReturned.Task, externalStopTask);
            var completed = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(allDone, completed); // else one of the two Stop calls hung -- the contended-lock deadlock regressed
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
        var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }

    // Round-3-engine-review finding: DisposeAsync_CalledConcurrentlyTwice_BothCompleteWithoutHanging
    // below moved from [Fact] to [RequiresPipeWireFact] when it was rewritten to use a real capture
    // session (needed to genuinely exercise concurrency -- see its own comment), which means the
    // concurrent-launch (not-yet-awaited-either-call) shape it tests no longer runs in CI on
    // windows/macos or a headless Linux runner. This hardware-free variant keeps that shape under
    // CI coverage, even though with nothing started it necessarily degenerates to the same trivial
    // case DisposeAsync_IsIdempotent_WhenCalledTwice already covers sequentially -- named to be
    // honest about that rather than imply it proves genuine concurrency.
    [Fact]
    public async Task DisposeAsync_CalledConcurrentlyTwice_WithNothingStarted_BothComplete()
    {
        var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        var dispose1 = engine.DisposeAsync().AsTask();
        var dispose2 = engine.DisposeAsync().AsTask();
        await Task.WhenAll(dispose1, dispose2);
    }

    // Round-1-engine-review finding: a second concurrent DisposeAsync caller used to return
    // immediately once _disposed was latched, before the first caller's teardown had necessarily
    // finished. Fixed with a TaskCompletionSource the second caller awaits instead.
    //
    // Round-2-engine-review fix: an earlier version of this test used an engine with nothing ever
    // started, so the first DisposeAsync call never actually suspended (SemaphoreSlim.WaitAsync
    // completes synchronously when uncontended, MiniAudioContext.Release() is synchronous) --
    // dispose2 simply observed an already-completed ValueTask, exercising zero real concurrency
    // despite the test's own name and comment. Using a real, started capture session instead
    // (native close runs on its own background thread, bounded by CloseTimeout) guarantees the
    // first DisposeAsync call genuinely suspends, so the second caller's wait on _disposedSignal is
    // actually exercised rather than trivially satisfied.
    [RequiresPipeWireFact]
    public async Task DisposeAsync_CalledConcurrentlyTwice_BothCompleteWithoutHanging()
    {
        var sinkName = $"sstv_engine_concurrent_dispose_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Concurrent_Dispose_Test", out var moduleIdOutput);
        var moduleId = moduleIdOutput.Trim();
        Assert.False(string.IsNullOrEmpty(moduleId), "pactl load-module did not return a module id -- is a PulseAudio/PipeWire-pulse server running?");

        try
        {
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);

            var dispose1 = engine.DisposeAsync().AsTask();
            var dispose2 = engine.DisposeAsync().AsTask();

            var allDone = Task.WhenAll(dispose1, dispose2);
            var completed = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(allDone, completed);

            // Both callers must observe a genuinely torn-down engine, not just "returned".
            await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartCaptureAsync(monitor!, sampleRate: 44100));
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    // Piece Engine 4: DisposeAsync's real scope -- stop capture AND drain-then-stop playback if
    // both are active at once, not just whichever one happens to have been exercised by an earlier,
    // narrower test. Real virtual sink/monitor, real sessions on both sides simultaneously.
    [RequiresPipeWireFact]
    public async Task DisposeAsync_WithBothCaptureAndPlaybackActive_StopsBothCleanly()
    {
        var sinkName = $"sstv_engine_dispose_both_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Dispose_Both_Test", out var moduleIdOutput);
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

            var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
            await engine.StartCaptureAsync(monitor!, sampleRate: 44100);
            await engine.StartPlaybackAsync(sink!, sampleRate: 44100);
            engine.EnqueuePlaybackSamples(GenerateSineTone(frequencyHz: 1000, durationSeconds: 0.2, sampleRate: 44100));

            var disposeTask = engine.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(disposeTask, completed); // else DisposeAsync hung tearing down one of the two live sessions

            // Idempotent even with both having been live.
            await engine.DisposeAsync();
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    [Fact]
    public async Task StartCaptureAsync_AfterDisposeAsync_ThrowsObjectDisposedException()
    {
        var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        await engine.DisposeAsync();

        var device = new AudioDeviceInfo("id", "name", MaxInputChannels: 1, MaxOutputChannels: 0, SupportedSampleRates: []);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartCaptureAsync(device, sampleRate: 44100));
    }

    [Fact]
    public async Task StartPlaybackAsync_AfterDisposeAsync_ThrowsObjectDisposedException()
    {
        var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        await engine.DisposeAsync();

        var device = new AudioDeviceInfo("id", "name", MaxInputChannels: 0, MaxOutputChannels: 1, SupportedSampleRates: []);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartPlaybackAsync(device, sampleRate: 44100));
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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();

            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            var monitor = enumerator.InputDevices.FirstOrDefault(d => d.Id.Contains($"{sinkName}.monitor", StringComparison.OrdinalIgnoreCase));
            Assert.True(monitor is not null, $"Virtual sink's monitor was not found among {enumerator.InputDevices.Count} enumerated input devices.");

            var receivedChunks = new List<float[]>();

            await using var captureEngine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
            captureEngine.SamplesCaptured += chunk =>
            {
                lock (receivedChunks)
                {
                    receivedChunks.Add(chunk.ToArray());
                }
            };
            await captureEngine.StartCaptureAsync(monitor!, sampleRate);

            await using var playbackEngine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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

    // Piece Engine 5a: engine-level real-audio integrity. CaptureOverrunCount/PlaybackUnderrunCount
    // are precise, non-fuzzy signals straight from the native counters -- far better than an
    // amplitude-based proxy for continuity (see the duration-based sanity check above's own
    // comment on why that approach can't cleanly attribute a shortfall to any one cause). Also
    // proves SamplesCaptured genuinely quiesces once StopCaptureAsync returns (guaranteed
    // structurally by the session's own drain-thread join, but verified empirically here rather
    // than only by inspection).
    //
    // Real finding while writing this test, not assumed: a first version asserted
    // PlaybackUnderrunCount == 0 for the whole run and failed (2 underruns) -- root cause is a
    // genuine, expected startup race, not a bug. MiniAudioPlaybackSession's constructor starts the
    // device (ma_device_start) before this test's write loop has enqueued anything, so the
    // real-time pull callback can legitimately fire against an empty ring a couple of times right
    // at startup, before the first EnqueuePlaybackSamples call -- exactly the "expected underrun,
    // not itself a verdict" case IAudioEngine's own overrun/underrun doc comments already call out.
    // Fixed by comparing two mid-steady-playback snapshots (no growth expected in between) instead
    // of asserting a global zero that startup alone can legitimately violate.
    [RequiresPipeWireFact]
    public async Task EngineIntegrity_ShortRealAudioRoundTrip_HasNoOverrunsOrUnderrunsAndQuiescesAfterStop()
    {
        const int sampleRate = 44100;
        const double toneDurationSeconds = 1.5;

        var sinkName = $"sstv_engine_integrity_test_{Guid.NewGuid():N}";

        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_Integrity_Test", out var moduleIdOutput);
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

            var receivedChunkCount = 0;
            await using var captureEngine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
            captureEngine.SamplesCaptured += _ => Interlocked.Increment(ref receivedChunkCount);
            await captureEngine.StartCaptureAsync(monitor!, sampleRate);

            await using var playbackEngine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
            await playbackEngine.StartPlaybackAsync(sink!, sampleRate);

            var tone = GenerateSineTone(frequencyHz: 1000, toneDurationSeconds, sampleRate);
            var offset = 0;
            var midpointUnderruns = -1;
            var midpointOverruns = -1;
            while (offset < tone.Length)
            {
                if (midpointUnderruns < 0 && offset >= tone.Length / 2)
                {
                    // Snapshot once steady playback is well underway -- past any legitimate startup
                    // underrun, see this test's own comment above.
                    midpointUnderruns = playbackEngine.PlaybackUnderrunCount;
                    midpointOverruns = captureEngine.CaptureOverrunCount;
                }

                var chunkLength = Math.Min(2048, tone.Length - offset);
                var written = playbackEngine.EnqueuePlaybackSamples(new ReadOnlyMemory<float>(tone, offset, chunkLength));
                if (written == 0)
                {
                    await Task.Delay(5);
                    continue;
                }

                offset += written;
            }

            // Snapshot before Stop -- both properties throw once their underlying session is
            // disposed. Comparing against the midpoint snapshot (not zero) is the real invariant:
            // no NEW drops accumulated during steady-state playback/capture, tolerating whatever
            // legitimate startup-only underrun/overrun already happened before the midpoint.
            var playbackUnderrunsBeforeStop = playbackEngine.PlaybackUnderrunCount;
            var captureOverrunsBeforeStop = captureEngine.CaptureOverrunCount;

            Assert.Equal(midpointUnderruns, playbackUnderrunsBeforeStop);
            Assert.Equal(midpointOverruns, captureOverrunsBeforeStop);

            await playbackEngine.StopPlaybackAsync();
            await Task.Delay(300); // let the capture drain thread deliver whatever it already buffered

            var countAfterCaptureStillRunning = Volatile.Read(ref receivedChunkCount);
            Assert.True(countAfterCaptureStillRunning > 0, "No samples were ever captured back from the monitor.");

            await captureEngine.StopCaptureAsync();

            // Post-stop quiescence: nothing can increment this after StopCaptureAsync has returned
            // (the drain thread is joined by then) -- verified empirically, not just by inspection.
            var countRightAfterStop = Volatile.Read(ref receivedChunkCount);
            await Task.Delay(200);
            var countAfterWaiting = Volatile.Read(ref receivedChunkCount);
            Assert.Equal(countRightAfterStop, countAfterWaiting);
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
            using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
            await enumerator.RefreshAsync();
            var sink = enumerator.OutputDevices.FirstOrDefault(d => d.Id.Contains(sinkName, StringComparison.OrdinalIgnoreCase));
            Assert.True(sink is not null, $"Virtual sink '{sinkName}' was not found among {enumerator.OutputDevices.Count} enumerated output devices.");

            await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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
        await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        await engine.StopPlaybackAsync();
        await engine.StopPlaybackAsync();
    }

    [Fact]
    public async Task EnqueuePlaybackSamples_WhenNeverStarted_ThrowsInvalidOperationException()
    {
        await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        Assert.Throws<InvalidOperationException>(() => engine.EnqueuePlaybackSamples(new float[10]));
    }

    // Round-1-engine-review finding: EnqueuePlaybackSamples had no disposed check at all -- after
    // DisposeAsync, _playbackSession is null regardless, so this used to throw
    // InvalidOperationException ("call StartPlaybackAsync first"), which would then itself throw
    // ObjectDisposedException -- a confusing, indirect error. Fixed with an explicit check.
    [Fact]
    public async Task EnqueuePlaybackSamples_AfterDisposeAsync_ThrowsObjectDisposedException()
    {
        var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
        await engine.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => engine.EnqueuePlaybackSamples(new float[10]));
    }

    [Fact]
    public async Task StartPlaybackAsync_WithUnknownDeviceId_ThrowsAudioDeviceUnavailableException()
    {
        await using var engine = new MiniAudioEngine(NullLogger<MiniAudioEngine>.Instance);
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
