namespace Yoniq.Abstractions.Imaging;

/// <summary>
/// Minimal pixel type for the SSTV DSP core (spec/06-sstv-dsp.md) and the minimal Phase-3 image
/// pipeline (spec/07-image-pipeline.md) — the DSP core has something to encode from/decode into
/// without depending on ImageSharp itself; <c>Yoniq.Core.Imaging.ImageFileLoader</c>/
/// <c>ReceivedImageBuffer</c> convert to/from this type at the ImageSharp boundary explicitly
/// (never by casting/blitting — see those classes' own doc comments). Full crop/resize/filter/overlay
/// tooling (`ITransmitImagePreparer`) is still Phase 4.
/// </summary>
public readonly record struct Rgb24(byte R, byte G, byte B);
