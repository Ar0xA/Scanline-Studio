using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Robot 72-style: Y, R-Y, B-Y each scanned every line, no alternation. Channel identity
/// comes from <see cref="ScanSegment.ChannelName"/> ("Y"/"RY"/"BY"), timing/order is pure data —
/// see <c>TMmsstv::LineR72</c> (`Main.cpp`).</summary>
internal sealed class YCbCrSequentialScanlineEncoder : SegmentSequenceScanlineEncoder
{
    protected override ScanChannel ResolveChannel(string channelName) => channelName switch
    {
        "Y" => ScanChannel.Y,
        "RY" => ScanChannel.RY,
        "BY" => ScanChannel.BY,
        _ => throw UnknownChannel(channelName),
    };

    protected override double PixelValue(SstvModeDefinition mode, IImageSource image, int lineIndex, ScanChannel channel, int x)
    {
        var pixel = image.GetScanline(lineIndex)[x];
        var (y, rMinusY, bMinusY) = YCbCr.FromRgb(pixel.R, pixel.G, pixel.B);
        return channel switch
        {
            ScanChannel.Y => y,
            ScanChannel.RY => rMinusY,
            ScanChannel.BY => bMinusY,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }
}
