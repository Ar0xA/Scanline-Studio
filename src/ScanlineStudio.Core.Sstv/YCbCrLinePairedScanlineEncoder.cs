using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>MP/PD/MN-family: Y(odd line), R-Y, B-Y, Y(even line) — one chroma pair shared between two
/// luma lines. Read from <c>TMmsstv::LineMP</c>/<c>LinePD</c>/<c>LineMN</c> (`Main.cpp:6733`/`:6686`/
/// `:6803`) — all three structurally identical; MP/PD differ only in sync/porch/scan durations, MN
/// additionally uses the narrow band (carried here by <c>mode.LuminanceMinHz</c>/<c>MaxHz</c>, not a
/// separate code path).</summary>
internal sealed class YCbCrLinePairedScanlineEncoder : SegmentSequenceScanlineEncoder
{
    public override int RowsPerTransmissionLine => 2;

    protected override ScanChannel ResolveChannel(string channelName) => channelName switch
    {
        "Y1" => ScanChannel.Y1,
        "Y2" => ScanChannel.Y2,
        "RY" => ScanChannel.RY,
        "BY" => ScanChannel.BY,
        _ => throw UnknownChannel(channelName),
    };

    protected override double PixelValue(SstvModeDefinition mode, IImageSource image, int lineIndex, ScanChannel channel, int x)
    {
        var sourceRow = channel == ScanChannel.Y2 ? lineIndex + 1 : lineIndex; // Y1/RY/BY read the odd (first) row
        var pixel = image.GetScanline(sourceRow)[x];
        var (y, rMinusY, bMinusY) = YCbCr.FromRgb(pixel.R, pixel.G, pixel.B);
        return channel switch
        {
            ScanChannel.Y1 or ScanChannel.Y2 => y,
            ScanChannel.RY => rMinusY,
            ScanChannel.BY => bMinusY,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }
}
