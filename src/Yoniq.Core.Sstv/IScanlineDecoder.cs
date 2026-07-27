using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Per-family decode strategy — counterpart to <see cref="IScanlineEncoder"/>. Shared
/// infrastructure (VIS detection, PLL demodulation, sample buffering) lives in
/// <see cref="AnalogFmSstvDecoder"/>, which selects the right strategy via
/// <see cref="ScanlineCodecFactory"/> once VIS reveals the mode. Implementations may hold mutable
/// state across calls (e.g. <c>RobotScanlineDecoder</c> caching the previous line's other chroma
/// channel) — a new decoder strategy instance is created per <see cref="AnalogFmSstvDecoder"/>
/// session, so this state never leaks across unrelated decodes.
/// </summary>
internal interface IScanlineDecoder
{
    /// <summary>See <see cref="IScanlineEncoder.RowsPerTransmissionLine"/>.</summary>
    int RowsPerTransmissionLine => 1;

    void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        Func<int, int, double> averageFrequencyInWindow,
        Rgb24[] pixels);
}
