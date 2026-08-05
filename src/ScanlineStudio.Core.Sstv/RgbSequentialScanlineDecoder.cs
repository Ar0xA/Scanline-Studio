using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

internal sealed class RgbSequentialScanlineDecoder : IScanlineDecoder
{
    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        PixelSampleReader reader,
        Rgb24[] pixels)
    {
        // Mirrors the encoder's running-accumulator approach (see AnalogFmSstvEncoder) so pixel
        // window boundaries line up with where the encoder actually placed them, rather than each
        // side independently rounding ms->samples and drifting apart over 320 pixels x 3 channels.
        var idealSamplesSoFar = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is ScanSegment scan)
            {
                // Milestone-audit MUST fix (spec/14-roadmap.md, "Milestone audit, Phase 1+2", finding
                // 1): legacy's real per-channel segment BOUNDARIES (Main.cpp:4454-4503's `ps < m_KS`/
                // `ps < m_CG`/`ps < m_CB` checks) are defined using the FULL, untrimmed nominal channel
                // span -- only the pixel-INDEX-within-a-segment mapping (`x = ps*Width/m_KSS`, `ps`
                // already relative to the segment's own start) uses the trimmed divisor. Any leftover
                // time at a segment's tail (once x would reach Width) is simply never assigned a pixel
                // in legacy -- discarded, not folded into where the NEXT segment starts. An earlier
                // version of this method accumulated idealSamplesSoFar by the TRIMMED total across the
                // whole scan segment (i.e. by summing perPixelDurationMs*Width = scan.DurationMs*trim,
                // not scan.DurationMs itself), so every scan segment after the first started early --
                // compounding across channels (measured: up to ~4px drift by the last channel in some
                // modes). Fixed by tracking the segment's own START position separately from the
                // trimmed intra-segment pixel walk, and advancing idealSamplesSoFar by the segment's
                // FULL untrimmed duration once the pixel loop finishes, discarding the trimmed
                // leftover tail exactly as legacy's own "x would exceed Width" boundary does.
                var segmentStartSample = idealSamplesSoFar;
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth
                    * SstvModeRegistry.GetPixelPitchTrimFactor(mode, scan.ChannelName);
                var pixelWalk = 0.0;
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    var startSample = lineStartSample + (int)Math.Round(segmentStartSample + pixelWalk);
                    pixelWalk += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(segmentStartSample + pixelWalk);

                    // Every RgbSequential-family channel peak-picks in legacy (Main.cpp:4459/4470/4481's
                    // default: case) except Scottie DX -- PixelSampleReader itself resolves that
                    // exception (NeverPeakPicks), so this decoder calls ReadPeakPicked uniformly for
                    // every mode/channel and never needs to know Scottie DX exists as a special case.
                    var freq = reader.ReadPeakPicked(startSample, endSample);
                    // Divisor is 256, not 255 -- matches the encoder and legacy's ColorToFreq inverse.
                    var value = (byte)Math.Clamp(
                        (freq - mode.LuminanceMinHz) / (mode.LuminanceMaxHz - mode.LuminanceMinHz) * 256.0,
                        0,
                        255);

                    var index = lineIndex * mode.ImageWidth + x;
                    pixels[index] = SetChannel(pixels[index], scan.ChannelName, value);
                }

                idealSamplesSoFar = segmentStartSample + scan.DurationMs / 1000.0 * sampleRate;
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
            }
        }
    }

    private static Rgb24 SetChannel(Rgb24 pixel, string channelName, byte value) => channelName switch
    {
        "R" => pixel with { R = value },
        "G" => pixel with { G = value },
        "B" => pixel with { B = value },
        _ => throw new NotSupportedException($"Unknown channel '{channelName}'."),
    };
}
