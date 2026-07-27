using System.Runtime.CompilerServices;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Generic continuous-phase FM encoder driven entirely by <see cref="SstvModeDefinition"/> data —
/// no per-mode code, per spec/06-sstv-dsp.md's "modes are data" design goal.
/// </summary>
public sealed class AnalogFmSstvEncoder : ISstvEncoder
{
    public AnalogFmSstvEncoder(int sampleRate = 11025)
    {
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public async IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var phase = 0.0;

        // Running accumulator, not "round(durationMs -> samples) per segment": with ~245,000
        // individual per-pixel segments in a full image, independently rounding each one's sample
        // count biases every pixel the same direction and the error accumulates linearly (over a
        // second of drift by the end of the image). Tracking ideal elapsed samples as a running
        // total and taking the difference keeps rounding error bounded to +/-0.5 sample forever.
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;

        foreach (var (frequencyHz, durationMs) in GenerateFrequencySegments(mode, image))
        {
            ct.ThrowIfCancellationRequested();

            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            var targetEmitted = (long)Math.Round(idealSamplesSoFar);
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            var phaseIncrement = 2 * Math.PI * frequencyHz / SampleRate;

            for (var i = 0; i < samplesToEmit; i++)
            {
                phase += phaseIncrement;
                if (phase >= 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }

                yield return (float)Math.Sin(phase);
            }
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateFrequencySegments(
        SstvModeDefinition mode,
        IImageSource image)
    {
        foreach (var segment in VisHeader.GenerateSegments(mode.VisCode))
        {
            yield return segment;
        }

        for (var y = 0; y < mode.ImageHeight; y++)
        {
            foreach (var lineSegment in mode.LineSegments)
            {
                switch (lineSegment)
                {
                    case SyncSegment sync:
                        yield return (sync.FrequencyHz, sync.DurationMs);
                        break;

                    case ScanSegment scan:
                        var perPixelDurationMs = scan.DurationMs / mode.ImageWidth;
                        for (var x = 0; x < mode.ImageWidth; x++)
                        {
                            // Re-fetched per pixel (not hoisted) because ReadOnlySpan<T> can't be
                            // stored across a yield-return boundary in an iterator state machine.
                            var value = GetChannelValue(image.GetScanline(y)[x], scan.ChannelName);
                            var frequencyHz = mode.LuminanceMinHz
                                + value / 255.0 * (mode.LuminanceMaxHz - mode.LuminanceMinHz);
                            yield return (frequencyHz, perPixelDurationMs);
                        }

                        break;
                }
            }
        }
    }

    internal static byte GetChannelValue(Rgb24 pixel, string channelName) => channelName switch
    {
        "R" => pixel.R,
        "G" => pixel.G,
        "B" => pixel.B,
        _ => throw new NotSupportedException($"Unknown channel '{channelName}'."),
    };
}
