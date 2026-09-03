using System.Text.RegularExpressions;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Cw;

/// <summary>Extracts a callsign token from decoded CW-ID text (fsk_cwid.md §8.3) -- e.g.
/// <c>"DE W1AW/M"</c> -&gt; <c>"W1AW/M"</c>. New capability, not a legacy port -- YONIQ never decoded
/// CW on receive, so this has no source function to verify against; legacy's post-image CW-ID text
/// is always exactly <c>"DE %m"</c> (this port's own `StationIdSettings`, mirrored from
/// `Main.cpp:906`'s `CWIDTEXT` default), which is the shape this extractor targets.
///
/// Public (auditor round-2 finding on B-P1, resolved in B-P2): <see cref="ScanlineStudio.Application.SstvSessionService"/>
/// runs a decoded <see cref="CwDecodeResult.Text"/> through this directly to build
/// <see cref="CwIdDecodedInfo.Callsign"/> -- an Application-layer caller needs this to be visible
/// outside <c>Core.Cw</c>, and it has no internal state to hide (a pure function over already-decoded
/// text).</summary>
public static partial class CwIdCallsignExtractor
{
    // Practical ITU-style amateur callsign shape, not a strict validator: 1-3 alphanumeric prefix
    // chars, at least one digit, 1-4 letter suffix, optional "/" portable-station or
    // cross-country-operation suffix (e.g. "/M", "/P", "/VE3ABC"). A token containing U+FFFD
    // (ClassicalCwDecoder's own "unknown character" marker, fsk_cwid.md §8.4 step 6) can never match
    // this character class, so a decode with an unresolved character is rejected as non-callsign for
    // free -- matching §8.4 step 6's own stated consumer expectation, not a separate check here.
    [GeneratedRegex(@"^[A-Z0-9]{1,3}[0-9][A-Z]{1,4}(/[A-Z0-9]{1,6})?$")]
    private static partial Regex CallsignShape();

    /// <summary>Looks for the token immediately after a standalone "DE" word (case-insensitive,
    /// matching legacy's own CW-ID convention) and validates/normalizes it as a callsign via
    /// <see cref="StationIdCallsignNormalizer"/> (the same shared normalization the FSK-ID pipeline
    /// uses, so a CW-decoded callsign compares equal to an FSK-decoded one for the same station).
    /// Returns <see langword="null"/> if no "DE" is found, or the following token doesn't look
    /// callsign-shaped -- a genuinely garbled decode should surface as "no callsign found," not a
    /// wrong one.</summary>
    public static string? Extract(string decodedText)
    {
        var tokens = decodedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!string.Equals(tokens[i], "DE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Shape-checked case-insensitively (real decoded text is always uppercase already, since
            // MorseAlphabet.Lookup only ever returns table characters '0'-'Z', but this method has no
            // documented uppercase-only precondition on its input, so a lowercase candidate must not
            // silently fail the shape check before Normalize ever gets a chance to fix the casing).
            var candidate = tokens[i + 1];
            if (CallsignShape().IsMatch(candidate.ToUpperInvariant()))
            {
                return StationIdCallsignNormalizer.Normalize(candidate);
            }
        }

        return null;
    }
}
