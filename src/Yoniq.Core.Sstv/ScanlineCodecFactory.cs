using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>Maps a mode's <see cref="ColorEncoding"/> family to the strategy that knows how to
/// encode/decode its scanlines — see that enum's doc comment for why this is composition (one
/// implementation per family) rather than a single generic interpreter.</summary>
internal static class ScanlineCodecFactory
{
    public static IScanlineEncoder CreateEncoder(ColorEncoding colorEncoding) => colorEncoding switch
    {
        ColorEncoding.RgbSequential => new RgbSequentialScanlineEncoder(),
        ColorEncoding.YCbCrRobot => new RobotScanlineEncoder(),
        ColorEncoding.YCbCrSequential => new YCbCrSequentialScanlineEncoder(),
        ColorEncoding.YCbCrLinePaired => new YCbCrLinePairedScanlineEncoder(),
        ColorEncoding.MonoAveragedPaired => new MonoAveragedPairedScanlineEncoder(),
        _ => throw new NotSupportedException($"No scanline encoder for {colorEncoding}."),
    };

    public static IScanlineDecoder CreateDecoder(ColorEncoding colorEncoding) => colorEncoding switch
    {
        ColorEncoding.RgbSequential => new RgbSequentialScanlineDecoder(),
        ColorEncoding.YCbCrRobot => new RobotScanlineDecoder(),
        ColorEncoding.YCbCrSequential => new YCbCrSequentialScanlineDecoder(),
        ColorEncoding.YCbCrLinePaired => new YCbCrLinePairedScanlineDecoder(),
        ColorEncoding.MonoAveragedPaired => new MonoAveragedPairedScanlineDecoder(),
        _ => throw new NotSupportedException($"No scanline decoder for {colorEncoding}."),
    };
}
