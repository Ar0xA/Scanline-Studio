using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Per-family encode strategy — see <see cref="ColorEncoding"/>'s doc comment for why this is
/// composition (one implementation per family) rather than a single generic interpreter. Shared
/// infrastructure (VIS header, phase accumulation) lives in <see cref="AnalogFmSstvEncoder"/>;
/// only the per-line frequency sequence is family-specific.
/// </summary>
internal interface IScanlineEncoder
{
    /// <summary>How many image rows one call to <see cref="GenerateLine"/> covers — 1 for most
    /// families, 2 for the MP/PD "line-paired" family (two Y scans sharing one chroma pair).</summary>
    int RowsPerTransmissionLine => 1;

    IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex);
}
