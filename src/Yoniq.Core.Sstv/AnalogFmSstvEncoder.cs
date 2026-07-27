using System.Runtime.CompilerServices;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Generic continuous-phase FM encoder. Shared infrastructure (VIS header, phase accumulation) is
/// family-agnostic; the actual per-line frequency sequence is delegated to a
/// <see cref="IScanlineEncoder"/> selected via <see cref="ScanlineCodecFactory"/> — see
/// <see cref="ColorEncoding"/>'s doc comment for why different families need different codec
/// logic rather than one generic interpreter.
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
        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var phase = 0.0;

        // Running accumulator, not "round(durationMs -> samples) per segment": with ~245,000
        // individual per-pixel segments in a full image, independently rounding each one's sample
        // count biases every pixel the same direction and the error accumulates linearly (over a
        // second of drift by the end of the image). Tracking ideal elapsed samples as a running
        // total and taking the difference keeps rounding error bounded to +/-0.5 sample forever.
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;

        foreach (var (frequencyHz, durationMs) in GenerateFrequencySegments(mode, image, lineEncoder))
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
        IImageSource image,
        IScanlineEncoder lineEncoder)
    {
        var headerSegments = mode.ExtendedVisCode is { } extendedCode
            ? VisHeader.GenerateExtendedSegments(extendedCode)
            : VisHeader.GenerateSegments(mode.VisCode);

        foreach (var segment in headerSegments)
        {
            yield return segment;
        }

        for (var y = 0; y < mode.ImageHeight; y++)
        {
            foreach (var segment in lineEncoder.GenerateLine(mode, image, y))
            {
                yield return segment;
            }
        }
    }
}
