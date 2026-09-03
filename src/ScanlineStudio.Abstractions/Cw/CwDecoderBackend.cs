namespace ScanlineStudio.Abstractions.Cw;

/// <summary>Which <see cref="ICwIdDecoder"/> implementation produced a <see cref="CwDecodeResult"/> --
/// lets the UI/log annotate the source, lets a future DeepCW-sidecar A/B report (fsk_cwid.md §8.5)
/// compare backends, and lets a sidecar-failed-fell-back-to-classical event be distinguished from a
/// classical-only result. New capability, not a legacy port -- YONIQ never decoded CW on receive.</summary>
public enum CwDecoderBackend
{
    Classical,
    DeepCwSidecar,
}
