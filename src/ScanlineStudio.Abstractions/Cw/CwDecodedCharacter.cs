namespace ScanlineStudio.Abstractions.Cw;

/// <summary>One decoded character from a <see cref="CwDecodeResult"/> (fsk_cwid.md §8.4 steps 6-7).
/// Named <see cref="Character"/>, not the plan doc's literal "Char" -- CA1720 (identifier contains a
/// type name, `char`) rejects that name in this codebase, where warnings are errors.
/// <see cref="Pattern"/> is the raw dit/dah string (e.g. <c>"-.."</c>) the timing classifier actually
/// produced, kept even when the pattern doesn't resolve to a real character, for diagnostics/logging.
/// <see cref="IsUnknown"/> is <see langword="true"/> when <see cref="Pattern"/> has no match in the
/// Morse table -- <c>?</c> is deliberately NOT used as the unknown sentinel, because <c>?</c> is
/// itself a real, decodable Morse character (<c>0xcc06</c> in the legacy table); an unknown pattern
/// instead renders as U+FFFD in <see cref="CwDecodeResult.Text"/>, and <see cref="Character"/> is
/// <c>'\0'</c> on an unknown pattern (never a placeholder character that could be mistaken for a real
/// decode). <see cref="Confidence"/> is this character's own element/gap timing-fit score (§8.4 step
/// 7), independent of the whole-result <see cref="CwDecodeResult.Confidence"/>, so a future UI could
/// render low-confidence characters differently.</summary>
public sealed record CwDecodedCharacter(char Character, string Pattern, bool IsUnknown, double Confidence);
