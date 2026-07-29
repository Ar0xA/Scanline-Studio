using System.Diagnostics;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;
using Yoniq.Core.Sstv;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Engine 5b: the real payoff test -- the real audio stack (<see cref="MiniAudioEngine"/>)
/// and the real SSTV DSP stack (<c>Yoniq.Core.Sstv</c>) running together for the first time,
/// rather than either through <c>FakeAudioEngine</c> (DSP-only tests) or a pure in-memory sample
/// buffer (<c>ResamplerQualityRoundTripTests</c>). Framed as an integration smoke test, not the
/// primary correctness gate for either stack -- both are already thoroughly proven independently
/// (<c>Yoniq.Core.Sstv.Tests.SstvRoundTripTests</c> for the DSP core; the real-hardware
/// <see cref="MiniAudioEngineTests"/>/<see cref="MiniAudioPlaybackSessionTests"/> suites for the
/// audio engine).
///
/// Uses R24 (<see cref="SstvModeRegistry.R24"/>), the shortest real mode in the table (200ms/line
/// x 120 lines = 24s, 320x120px), not Martin M1's ~114s, to keep this test's real-time playback
/// duration reasonable. Staged asserts (mode detected -> full line count decoded -> image
/// tolerance) so a failure localizes to a specific stage instead of only reporting a final image
/// mismatch.
///
/// <see cref="AnalogFmSstvDecoder"/> has no slant/clock-drift correction yet (see its own doc
/// comment) -- some tolerance loosening beyond the pure in-memory round trips is expected here,
/// since this test runs through two independent real miniaudio devices (their own resamplers, real
/// -- if virtual -- device timing), not a single in-process sample buffer.
/// </summary>
public class MiniAudioEngineSstvRoundTripTests
{
    private const int SampleRate = 44100;

    [RequiresPipeWireFact]
    public async Task EncodeThenDecode_ThroughRealMiniAudioEngine_RoundTripsWithinTolerance()
    {
        var mode = SstvModeRegistry.R24;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var encodedSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            encodedSamples.Add(sample);
        }

        var tone = encodedSamples.ToArray();

        var sinkName = $"sstv_engine_e2e_test_{Guid.NewGuid():N}";
        RunPactl($"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=SSTV_Engine_E2E_Test", out var moduleIdOutput);
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

            var decoder = new AnalogFmSstvDecoder(SampleRate);
            SstvModeDefinition? detectedMode = null;
            DecodedImageUpdate? lastUpdate = null;
            decoder.ModeDetected += m => detectedMode = m;
            // Both fired synchronously from within PushSamples below, which this test only ever
            // calls from one thread (the capture engine's own drain thread, via SamplesCaptured) --
            // no additional locking needed for these two field writes.
            decoder.LineDecoded += update => lastUpdate = update;

            await using var captureEngine = new MiniAudioEngine();
            captureEngine.SamplesCaptured += chunk => decoder.PushSamples(chunk);
            await captureEngine.StartCaptureAsync(monitor!, SampleRate);

            await using var playbackEngine = new MiniAudioEngine();
            await playbackEngine.StartPlaybackAsync(sink!, SampleRate);

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

            // Generous grace period -- unlike the shorter integrity tests, this needs to let a
            // realistic amount of already-buffered audio finish flowing through the capture drain
            // thread and decoder before this test stops listening.
            await Task.Delay(1000);
            await captureEngine.StopCaptureAsync();

            // Stage 1: mode/VIS detected.
            Assert.NotNull(detectedMode);
            Assert.Equal(mode.Id, detectedMode!.Id);

            // Stage 2: (close to) the full expected line count decoded -- not necessarily the very
            // last line, since capture start/stop has its own real-world latency at each end, but
            // enough to prove this wasn't a near-total decode failure.
            Assert.NotNull(lastUpdate);
            Assert.True(
                lastUpdate!.Line >= mode.ImageHeight - 5,
                $"Only decoded up to line {lastUpdate.Line} of {mode.ImageHeight} expected lines.");

            // Stage 3: image tolerance. Measured directly on this project's own dev sandbox (real
            // virtual-cable round trip, no slant/clock-drift correction in AnalogFmSstvDecoder yet
            // -- see the class doc comment): ~32.7 average per-channel delta, confirmed stable
            // across repeated runs. Set with real headroom above that measurement, per this
            // project's own "measure first, escalate/tighten only if needed" precedent (see
            // ResamplerQualityRoundTripTests) -- not a guessed round number.
            var delta = AveragePerChannelDelta(sourceImage, lastUpdate.Image);
            Assert.True(
                delta <= 38.0,
                $"Average per-channel delta ({delta:F2}) exceeded the real-hardware round-trip tolerance.");
        }
        finally
        {
            RunPactl($"unload-module {moduleId}", out _);
        }
    }

    private static double AveragePerChannelDelta(IImageSource expected, IImageSource actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < expected.Height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);

            for (var x = 0; x < expected.Width; x++)
            {
                totalDelta += Math.Abs(expectedLine[x].R - actualLine[x].R);
                totalDelta += Math.Abs(expectedLine[x].G - actualLine[x].G);
                totalDelta += Math.Abs(expectedLine[x].B - actualLine[x].B);
                sampleCount += 3;
            }
        }

        return totalDelta / sampleCount;
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
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
