using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Shared signal builders for the sync-pulse SNR work: our own encoder's output, truncated to the
/// first N transmission lines, plus the exact raw-timeline sample where every line's sync pulse starts.
/// </summary>
internal static class SyncSnrTestSignals
{
    /// <summary>All registered modes that carry a sync segment (every mode except AVT).</summary>
    public static IReadOnlyList<SstvModeDefinition> ModesWithSync { get; } =
        SstvModeRegistry.All.Where(m => m != SstvModeRegistry.Avt).ToList();

    public static IImageSource SourceFor(SstvModeDefinition mode) =>
        mode.ColorEncoding == ColorEncoding.MonoAveragedPaired
            ? ImpairmentSweepHarness.CreateGrayscaleGradientTestImage(mode.ImageWidth, mode.ImageHeight)
            : ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

    /// <summary>
    /// Encodes <paramref name="mode"/> at <paramref name="sampleRate"/> and keeps only the header plus
    /// <paramref name="transmissionLines"/> lines (plus a short tail), or the whole picture when
    /// <paramref name="transmissionLines"/> is null. <c>SyncStarts[k]</c> is transmission line k's
    /// sync onset in the returned stream, in samples.
    /// </summary>
    /// <remarks>
    /// Truth comes from the encoder's own arithmetic, not an estimate: a segment's first sample is
    /// <c>(long)</c> of the running ideal sample count (the same accumulation <c>EncodeBatchedAsyncCore</c>
    /// does), the tone switches half a sample before it, and the symmetric TX BPF delays everything by
    /// exactly <c>tap/2</c> samples.
    /// </remarks>
    public static async Task<(float[] Samples, double[] SyncStarts)> EncodeAsync(
        SstvModeDefinition mode,
        int sampleRate,
        int? transmissionLines,
        IImageSource? source = null)
    {
        source ??= SourceFor(mode);
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var syncHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var syncOffsetMs = SstvModeRegistry.GetSyncSegmentOffsetMs(mode);
        const double txBpfDelay = TxOutputBandpassFilter.DefaultTapCount / 2;

        var ideal = 0.0;
        foreach (var (_, durationMs) in HeaderSegments(mode))
        {
            ideal += durationMs / 1000.0 * sampleRate;
        }

        var syncStarts = new List<double>();
        var totalLines = mode.ImageHeight / lineEncoder.RowsPerTransmissionLine;
        var keptLines = transmissionLines is { } n ? Math.Min(n, totalLines) : totalLines;
        var endSample = 0L;
        for (var line = 0; line < totalLines; line++)
        {
            var lineOffsetMs = 0.0;
            var found = false;
            foreach (var (frequencyHz, durationMs) in lineEncoder.GenerateLine(mode, source, line * lineEncoder.RowsPerTransmissionLine))
            {
                if (!found && Math.Abs(frequencyHz - syncHz) < 0.01 && Math.Abs(lineOffsetMs - syncOffsetMs) < 0.01)
                {
                    syncStarts.Add((long)ideal - 0.5 + txBpfDelay);
                    found = true;
                }

                ideal += durationMs / 1000.0 * sampleRate;
                lineOffsetMs += durationMs;
            }

            if (!found)
            {
                throw new InvalidOperationException($"[{mode.Id}] line {line}: no {syncHz} Hz segment at {syncOffsetMs} ms.");
            }

            if (line == keptLines - 1)
            {
                endSample = (long)ideal + (sampleRate / 20);
            }
        }

        var samples = new List<float>((int)Math.Min(endSample, int.MaxValue / 2));
        await foreach (var batch in encoder.EncodeBatchedAsync(mode, source))
        {
            var take = (int)Math.Min(batch.Length, endSample - samples.Count);
            samples.AddRange(batch[..take].ToArray());

            if (samples.Count >= endSample)
            {
                break;
            }
        }

        return (samples.ToArray(), syncStarts.ToArray());
    }

    /// <summary>The same header segment order <c>GenerateFrequencySegments</c> emits before line 0.</summary>
    private static IEnumerable<(double FrequencyHz, double DurationMs)> HeaderSegments(SstvModeDefinition mode)
    {
        foreach (var segment in VisHeader.GenerateOutHeadSegments(mode.NarrowModeCode is not null))
        {
            yield return segment;
        }

        if (mode.NarrowModeCode is { } narrowCode)
        {
            foreach (var segment in VisHeader.GenerateNarrowModeSegments(narrowCode))
            {
                yield return segment;
            }
        }
        else if (mode.ExtendedVisCode is { } extendedCode)
        {
            foreach (var segment in VisHeader.GenerateExtendedSegments(extendedCode))
            {
                yield return segment;
            }
        }
        else
        {
            var forcedParityBit = mode == SstvModeRegistry.Rm12 ? VisHeader.Rm12ForcedParityBit : (int?)null;
            foreach (var segment in VisHeader.GenerateSegments(mode.VisCode, forcedParityBit))
            {
                yield return segment;
            }

            if (SstvModeRegistry.IsScottieFamily(mode))
            {
                yield return (VisHeader.ScottiePostVisPulseFrequencyHz, VisHeader.ScottiePostVisPulseDurationMs);
            }
        }
    }

    /// <summary>Pushes <paramref name="samples"/> in fixed chunks, like a live capture does.</summary>
    public static void PushChunked(AnalogFmSstvDecoder decoder, float[] samples, int chunk = 4096)
    {
        for (var offset = 0; offset < samples.Length; offset += chunk)
        {
            decoder.PushSamples(samples.AsMemory(offset, Math.Min(chunk, samples.Length - offset)));
        }
    }
}
