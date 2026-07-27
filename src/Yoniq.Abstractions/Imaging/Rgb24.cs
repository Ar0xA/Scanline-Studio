namespace Yoniq.Abstractions.Imaging;

/// <summary>
/// Minimal pixel type for the Phase 1 SSTV DSP core (spec/06-sstv-dsp.md). The full imaging
/// pipeline (spec/07-image-pipeline.md, ImageSharp-based) is Phase 4 — this exists only so the
/// DSP core has something to encode from / decode into without pulling in that dependency early.
/// </summary>
public readonly record struct Rgb24(byte R, byte G, byte B);
