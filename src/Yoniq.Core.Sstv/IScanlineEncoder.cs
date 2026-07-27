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
    IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex);
}
