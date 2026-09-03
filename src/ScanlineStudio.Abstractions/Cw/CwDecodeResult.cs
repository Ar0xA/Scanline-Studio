namespace ScanlineStudio.Abstractions.Cw;

/// <summary>The result of one <see cref="ICwIdDecoder.DecodeAsync"/> call over a single capture
/// window (fsk_cwid.md §8.4). <see cref="Text"/> is empty and <see cref="Confidence"/> is <c>0</c>
/// when no tone/no valid CW timing was found in the window (§8.4 steps 1/3) -- callers must not
/// treat an empty <see cref="Text"/> as an error, only as "nothing decodable in this window."
/// <see cref="ToneHz"/>/<see cref="Wpm"/> are the decoder's own estimates (§8.4 steps 1/4), null
/// alongside an empty <see cref="Text"/>. <see cref="Characters"/> preserves per-character detail
/// (including unknowns, see <see cref="CwDecodedCharacter"/>) for diagnostics beyond the flattened
/// <see cref="Text"/> string.</summary>
public sealed record CwDecodeResult(
    string Text,
    double Confidence,
    double? ToneHz,
    double? Wpm,
    IReadOnlyList<CwDecodedCharacter> Characters);
