using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Runs the real encoder's per-line frequency sequence straight into the matching decoder, with NO
/// modulation, filtering or demodulation between them — the decoder's "demodulated Hz at sample i"
/// is exactly the Hz the encoder asked for at that instant.
///
/// <para>This is a diagnostic instrument, not a decode path. What it isolates is everything that is
/// NOT the DSP chain: the colour maths, the channel layout, chroma subsampling, line pairing, the
/// per-pixel read policy, and every timing decision in the scanline codecs. If a fault reproduces
/// here it cannot be blamed on the demodulator, and if it vanishes here it cannot be blamed on the
/// codecs.</para>
///
/// <para>Shared by <see cref="IdealTransportGreenBiasProbe"/> (BACKLOG D1, which attributed the
/// green cast's Fault B to the DSP chain by this exact reasoning) and
/// <see cref="NarrowModeEdgeColumnProbe"/> (BACKLOG D2).</para>
/// </summary>
internal static class IdealTransport
{
    public const int SampleRate = 11025;

    public static IImageSource Decode(SstvModeDefinition mode, IImageSource source)
    {
        var encoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var decoder = ScanlineCodecFactory.CreateDecoder(mode.ColorEncoding);
        var rowsPerLine = encoder.RowsPerTransmissionLine;

        // Continuous time, not per-segment rounding. Laying each segment down as a whole number of
        // samples would accumulate a fraction of a sample per segment across a line, which is a
        // timing error this probe is specifically trying NOT to introduce -- it would show up as
        // exactly the kind of edge artefact D2 is measuring.
        var samplesPerLine = mode.LineDurationMs * SampleRate / 1000.0;

        var lineSegments = new List<(double CumulativeEndMs, double FrequencyHz)[]>();
        for (var lineIndex = 0; lineIndex < mode.ImageHeight; lineIndex += rowsPerLine)
        {
            var cumulative = 0.0;
            var segments = encoder.GenerateLine(mode, source, lineIndex)
                .Select(seg =>
                {
                    cumulative += seg.DurationMs;
                    return (CumulativeEndMs: cumulative, seg.FrequencyHz);
                })
                .ToArray();

            // If the encoder's own line does not sum to the mode's line duration, every sample index
            // below is against the wrong clock and every number a caller prints is meaningless.
            Assert.True(
                Math.Abs(cumulative - mode.LineDurationMs) < 0.001,
                $"[{mode.Id}] encoder line {lineIndex} sums to {cumulative:F4} ms, mode says {mode.LineDurationMs:F4} ms.");

            lineSegments.Add(segments);
        }

        double FrequencyAt(int sampleIndex)
        {
            var line = (int)(sampleIndex / samplesPerLine);
            if (line < 0 || line >= lineSegments.Count)
            {
                return mode.LuminanceMinHz;
            }

            var offsetMs = (sampleIndex - (line * samplesPerLine)) / SampleRate * 1000.0;
            var segments = lineSegments[line];
            foreach (var segment in segments)
            {
                if (offsetMs < segment.CumulativeEndMs)
                {
                    return segment.FrequencyHz;
                }
            }

            return segments[^1].FrequencyHz;
        }

        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        for (var line = 0; line < lineSegments.Count; line++)
        {
            var lineStartSample = (int)Math.Round(line * samplesPerLine);
            var nextLineStartSample = (int)Math.Round((line + 1) * samplesPerLine);

            // Same construction as production (AnalogFmSstvDecoder.cs:3624-3629), so this reproduces
            // the real per-pixel read policy -- bare against peak-picked -- rather than an idealised
            // one. Getting this wrong would change the answer.
            var reader = new PixelSampleReader(
                FrequencyAt,
                SstvModeRegistry.GetKsbSamples(mode, SampleRate),
                nextLineStartSample,
                mode.LuminanceMinHz,
                SstvModeRegistry.NeverPeakPicks(mode));

            decoder.DecodeLine(mode, SampleRate, lineStartSample, line * rowsPerLine, reader, pixels);
        }

        return new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
    }
}
