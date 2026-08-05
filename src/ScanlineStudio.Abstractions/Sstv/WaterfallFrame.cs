namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>One rolling FFT magnitude frame for waterfall/scope visualization — see
/// spec/06-sstv-dsp.md's `IWaterfallSource` note and spec/09-ui.md's custom-drawn waterfall control
/// (this is the "already-computed magnitude-frame data" that spec hands the view-model; no FFT/filter
/// math lives in `ScanlineStudio.UI` itself). <see cref="MagnitudesDb"/> covers bins <c>0..N/2-1</c> only
/// (real-valued input, upper half is the mirror image and carries no extra information).</summary>
public sealed record WaterfallFrame(IReadOnlyList<float> MagnitudesDb, double BinWidthHz, DateTimeOffset ObservedAt);
