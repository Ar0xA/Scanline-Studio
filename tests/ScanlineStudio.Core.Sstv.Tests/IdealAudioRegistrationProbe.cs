using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Measures the production decoder's horizontal registration against IDEAL timing, for every mode,
/// with no contribution from this port's encoder and no legacy capture required.
///
/// <para><b>Why the three earlier attempts could not do this.</b>
/// <see cref="HorizontalRegistrationProbe"/> drives the decoder from
/// <see cref="AnalogFmSstvEncoder"/>, so an encoder timing error and a decoder registration error are
/// indistinguishable in it — the recorded Scottie failure shape.
/// <see cref="IdealTransport"/> hands the decoder an exact line origin, so sync detection, the anchor
/// correction and the demodulator delay are not in the loop at all and a registration error is
/// invisible to it by construction. <see cref="LegacyAudioRegistrationProbe"/> is non-circular but
/// covers 8 of 43 modes, and its fixtures are smooth gradients, on which a shift fit is
/// degenerate.</para>
///
/// <para><b>What this does instead.</b> The stimulus is synthesised here, directly from the mode's own
/// <c>LineSegments</c> — a phase-continuous tone per segment, scan segments mapped through the mode's
/// own declared luminance band. Segment boundaries come from cumulative time in double precision, so
/// no per-line rounding accumulates. That signal then goes through the FULL production decoder via
/// <c>PushSamples</c>, with <c>ForceMode</c> standing in for a VIS header, so sync detection and
/// anchoring run exactly as they do on the air.</para>
///
/// <para><b>The source is a hard vertical edge</b>, not a gradient. A gradient makes every shift
/// estimator degenerate — squared error confuses a luma gain difference for a displacement, and
/// correlation is nearly flat because a ramp looks like itself at any offset. A step edge has one
/// unambiguous location.</para>
///
/// <para><b>What a non-zero result means.</b> NOT a port defect on its own. Legacy registers its own
/// receive path against hand-tuned per-mode constants, which this port transcribes deliberately
/// (`SstvModeRegistry`'s sync-peak offsets, `SyncAnchorCorrector`). So this measures the port against
/// IDEAL timing, and legacy's own tuned bias is included in the answer. Deciding to remove it is a
/// `CLAUDE.md` §0a improvement decision. What makes it actionable is the residual against
/// <see cref="LegacyAudioRegistrationProbe"/> on the 8 modes where real legacy audio exists.</para>
///
/// <para>This is a measurement. It changes no shipping code.</para>
/// </summary>
public sealed class IdealAudioRegistrationProbe
{
    private const int SampleRate = 44100;

    [Fact]
    public void RegistrationAgainstIdealTiming_ForEveryMode()
    {
        var rows = new List<string>
        {
            "| mode | pitch (samples) | offset (px) | offset (ms) | rows measured |",
            "|---|---|---|---|---|",
        };

        foreach (var mode in SstvModeRegistry.All.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var edgeColumn = mode.ImageWidth / 2;
            var source = CreateVerticalEdgeImage(mode.ImageWidth, mode.ImageHeight, edgeColumn);
            var samples = SynthesiseIdealAudio(mode, source);

            var decoded = DecodeWithFullChain(mode, samples);
            if (decoded is null)
            {
                rows.Add($"| {mode.Id} | | DECODE FAILED | | |");
                continue;
            }

            var offsets = new List<double>();
            var usableRows = Math.Min(mode.ImageHeight, decoded.Height);
            for (var y = usableRows / 4; y < usableRows * 3 / 4; y++)
            {
                var found = FindEdgeColumn(decoded.GetScanline(y), mode.ImageWidth);
                if (found is not null)
                {
                    offsets.Add(found.Value - edgeColumn);
                }
            }

            if (offsets.Count < 8)
            {
                rows.Add($"| {mode.Id} | | only {offsets.Count} rows had a detectable edge | | |");
                continue;
            }

            offsets.Sort();
            var median = offsets[offsets.Count / 2];
            var lastScan = mode.LineSegments.OfType<ScanSegment>().Last();
            var pitchSamples = lastScan.DurationMs / mode.ImageWidth / 1000.0 * SampleRate;

            rows.Add(
                $"| {mode.Id} | {pitchSamples:F1} | {median:F2} | "
                + $"{median * lastScan.DurationMs / mode.ImageWidth:F3} | {offsets.Count} |");
        }

        Assert.Fail(
            "Registration against IDEAL timing, full production decoder, synthesised stimulus.\n"
            + "Negative means the decoded edge lands LEFT of where it was sent, i.e. reads are LATE.\n"
            + "This includes legacy's own tuned registration bias by design -- see the class doc.\n\n"
            + string.Join("\n", rows));
    }

    /// <summary>
    /// Phase-continuous tone per segment, built from the mode's own declared timings and band. The
    /// cumulative time is carried in double precision and each boundary is rounded once from it, so
    /// no per-line rounding error accumulates down the picture — which is exactly the class of
    /// encoder bug this probe exists to exclude.
    /// </summary>
    private static float[] SynthesiseIdealAudio(SstvModeDefinition mode, IImageSource source)
    {
        var samples = new List<float>();
        var phase = 0.0;
        var elapsedMs = 0.0;

        // A short lead-in of silence, so the first line's sync has somewhere to be found from.
        for (var i = 0; i < SampleRate / 10; i++)
        {
            samples.Add(0f);
        }

        var rowsPerLine = mode.LineSegments.OfType<ScanSegment>().Any(s => s.ChannelName == "Y2") ? 2 : 1;

        for (var line = 0; line < mode.ImageHeight; line += rowsPerLine)
        {
            foreach (var segment in mode.LineSegments)
            {
                var segmentEndMs = elapsedMs + segment.DurationMs;
                var startSample = (int)Math.Round(elapsedMs / 1000.0 * SampleRate);
                var endSample = (int)Math.Round(segmentEndMs / 1000.0 * SampleRate);

                for (var i = startSample; i < endSample; i++)
                {
                    var withinSegment = (i - startSample) / (double)Math.Max(1, endSample - startSample);
                    var frequency = FrequencyFor(mode, segment, source, line, withinSegment);
                    phase += 2.0 * Math.PI * frequency / SampleRate;
                    samples.Add((float)(Math.Sin(phase) * 0.7));
                }

                elapsedMs = segmentEndMs;
            }
        }

        return samples.ToArray();
    }

    private static double FrequencyFor(
        SstvModeDefinition mode,
        LineSegment segment,
        IImageSource source,
        int line,
        double withinSegment)
    {
        switch (segment)
        {
            case SyncSegment sync:
                return sync.FrequencyHz;

            case ToneSelectorSegment tone:
                return tone.LowFrequencyHz;

            case ScanSegment scan:
            {
                var x = Math.Clamp((int)(withinSegment * mode.ImageWidth), 0, mode.ImageWidth - 1);
                var row = scan.ChannelName == "Y2" ? Math.Min(line + 1, source.Height - 1) : line;
                var pixel = source.GetScanline(Math.Min(row, source.Height - 1))[x];

                // Chroma channels sit at mid-band on a grey-scale source, so only luma carries the
                // edge -- which is what the estimator reads.
                var value = scan.ChannelName is "RY" or "BY" or "C"
                    ? 128.0
                    : (pixel.R + pixel.G + pixel.B) / 3.0;

                return mode.LuminanceMinHz
                    + (value * (mode.LuminanceMaxHz - mode.LuminanceMinHz) / 256.0);
            }

            default:
                return mode.LuminanceMinHz;
        }
    }

    private static IImageSource? DecodeWithFullChain(SstvModeDefinition mode, float[] samples)
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        IImageSource? decodedImage = null;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.ForceMode(mode);
        decoder.PushSamples(samples);
        return decodedImage;
    }

    private static double? FindEdgeColumn(ReadOnlySpan<Rgb24> line, int width)
    {
        for (var x = 1; x < width; x++)
        {
            var previous = Luma(line[x - 1]);
            var current = Luma(line[x]);
            if (previous < 127.5 && current >= 127.5)
            {
                var span = current - previous;
                return span <= 0 ? x : x - 1 + ((127.5 - previous) / span);
            }
        }

        return null;
    }

    private static double Luma(Rgb24 pixel) => (pixel.R + pixel.G + pixel.B) / 3.0;

    private static ArrayImageSource CreateVerticalEdgeImage(int width, int height, int edgeColumn)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = (byte)(x < edgeColumn ? 0 : 255);
                pixels[(y * width) + x] = new Rgb24(level, level, level);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
