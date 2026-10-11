using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Martin/Scottie/AVT/Pasokon/SC2/MC-family: N sequential same-shaped channel scans per
/// line, timing and channel order are pure <see cref="SstvModeDefinition.LineSegments"/> data.</summary>
internal sealed class RgbSequentialScanlineEncoder : SegmentSequenceScanlineEncoder
{
    protected override ScanChannel ResolveChannel(string channelName) => channelName switch
    {
        "R" => ScanChannel.R,
        "G" => ScanChannel.G,
        "B" => ScanChannel.B,
        _ => throw UnknownChannel(channelName),
    };

    protected override double PixelValue(SstvModeDefinition mode, IImageSource image, int lineIndex, ScanChannel channel, int x)
    {
        var pixel = image.GetScanline(lineIndex)[x];
        // SHOULD item 4 (spec/14-roadmap.md): value is already an integer RGB byte
        // (no GetRY step for this family, matching legacy's own ColorToFreq(cp->b.r)
        // etc. call sites, Main.cpp:6610-6629), so only YCbCr.ColorToFreq's own
        // internal integer-division truncation applies here, not FromRgb's.
        // Comprehensive-review addition: this family's own narrow half (MC110/140/180)
        // is `TMmsstv::LineMC` (Main.cpp:6827-6845), which uses `ColorToFreqNarrow`
        // (`Main.cpp:6837/6840/6843`) instead of plain `ColorToFreq` -- covered by
        // `YCbCr.ColorToFreq`'s own (min,max) generalization, not a separate call here
        // (the mode's own LuminanceMinHz/MaxHz select which band applies).
        return channel switch
        {
            ScanChannel.R => pixel.R,
            ScanChannel.G => pixel.G,
            ScanChannel.B => pixel.B,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }
}
