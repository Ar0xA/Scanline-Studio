namespace ScanlineStudio.Abstractions.Cw;

/// <summary>Decodes a captured CW-ID audio window into text (fsk_cwid.md §8.4/§8.5). New capability,
/// not a legacy port -- YONIQ never decoded CW on receive, so there is no source function this
/// interface's shape is verified against; it exists to let <c>ScanlineStudio.Core.Cw</c>'s classical
/// decoder (v1, always available) and an optional future DeepCW sidecar backend (§8.5, gated on the
/// license question in §7) be swapped/composed behind one contract, distinguished in the result via
/// <see cref="CwIdDecodedInfo.Backend"/>. Implementations must not throw for "no tone found" -- that
/// is a normal <see cref="CwDecodeResult"/> with an empty <see cref="CwDecodeResult.Text"/> and
/// <c>Confidence = 0</c>, not an exceptional outcome.</summary>
public interface ICwIdDecoder
{
    Task<CwDecodeResult> DecodeAsync(ReadOnlyMemory<float> samples, int sampleRate, CancellationToken ct = default);
}
