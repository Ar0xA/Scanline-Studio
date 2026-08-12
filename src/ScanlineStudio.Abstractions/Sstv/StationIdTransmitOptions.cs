namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Fully-resolved CW-ID/FSK station-ID configuration for ONE transmission, passed into
/// <see cref="ISstvEncoder.EncodeAsync"/>. Deliberately lives in <c>ScanlineStudio.Abstractions</c>
/// (not <c>ScanlineStudio.Core.Sstv</c>, where the settings record and macro-text resolution this is
/// built from actually live) so the pure-DSP encoder never needs to reference
/// <c>ScanlineStudio.Settings</c>/<c>ScanlineStudio.Application</c> -- <c>ScanlineStudio.Application</c>'s
/// <c>SstvSessionService</c> is the only place that loads persisted settings/resolves macro tokens
/// and constructs one of these, once per <c>TransmitAsync</c> call, right before encoding starts.
///
/// <see cref="CwResolvedText"/> is already macro-resolved (via <c>IMacroTextResolver</c>, which needs
/// <c>OperatorSettings</c> this type's owner assembly can't reference) -- but <see cref="Callsign"/>/
/// <see cref="NrRstText"/> are passed through largely as-configured; <c>AnalogFmSstvEncoder</c> itself
/// applies the legacy-faithful settings-boundary normalization (uppercase/trim/16-char cap on the
/// callsign, <c>Option.cpp:445-448</c>; a length cap on the NR/RST text) immediately before encoding,
/// since that normalization is pure wire-format policy with no Application-layer dependency of its
/// own -- see <c>AnalogFmSstvEncoder.NormalizeCallsignForStationId</c>.</summary>
public sealed record StationIdTransmitOptions
{
    /// <summary>Nothing enabled -- produces byte-identical TX output to every code path that existed
    /// before this feature. The default used whenever a caller passes no
    /// <see cref="StationIdTransmitOptions"/> at all to <see cref="ISstvEncoder.EncodeAsync"/>.</summary>
    public static readonly StationIdTransmitOptions None = new();

    /// <summary><c>sys.m_CWID == 1</c> AND the raw (pre-macro) configured CW text is non-empty
    /// (<c>Main.cpp:6969</c>'s <c>!sys.m_CWIDText.IsEmpty()</c>) -- both gates already applied by the
    /// caller; this field alone decides whether <c>AnalogFmSstvEncoder</c> emits any CW-ID
    /// segments.</summary>
    public bool CwEnabled { get; init; }

    /// <summary>Macro-resolved CW-ID text (<c>%m</c>/<c>%D</c>/<c>%T</c>/<c>{name}</c>/<c>{grid}</c>
    /// already substituted). Only meaningful when <see cref="CwEnabled"/> is <see langword="true"/>.</summary>
    public string CwResolvedText { get; init; } = string.Empty;

    /// <summary><c>sys.m_CWIDFreq</c>, Hz. Defaults to legacy's own compiled-in default
    /// (<c>Main.cpp:907</c>) purely so an accidentally-unset value still produces an audible, in-range
    /// tone rather than 0Hz/DC -- the real default lives on <c>StationIdSettings.DefaultCwToneFrequencyHz</c>
    /// and is what callers should actually apply.</summary>
    public double CwToneFrequencyHz { get; init; } = 1000;

    /// <summary>WPM -- converted to a dot duration inside <c>AnalogFmSstvEncoder</c> via
    /// <c>CwMorseGenerator.MillisecondsPerDotFromWpm</c> (internal to <c>ScanlineStudio.Core.Sstv</c>,
    /// so this type carries the raw WPM rather than a pre-converted duration). Defaults to legacy's
    /// own compiled-in default (<c>Main.cpp:905</c>) for the same reason as
    /// <see cref="CwToneFrequencyHz"/>.</summary>
    public int CwWpm { get; init; } = 28;

    /// <summary><c>sys.m_TXFSKID</c>. Gates BOTH the post-image FSK-ID packet emission and which
    /// footer-tone shape gets emitted (<c>Main.cpp:6997/7010</c>) -- independent of
    /// <see cref="Callsign"/> being empty, which only gates packet emission (see
    /// <c>FskStationIdEncoder.Generate</c>'s own doc comment).</summary>
    public bool FskIdEnabled { get; init; }

    /// <summary>The operator's callsign, as-configured (NOT yet normalized -- see this type's own
    /// doc comment for where that happens). An empty value yields no FSK-ID packet at all, matching
    /// legacy's <c>!sys.m_Call.IsEmpty()</c> trigger gate (<c>Main.cpp:7018</c>).</summary>
    public string Callsign { get; init; } = string.Empty;

    /// <summary>Raw NR/RST exchange text. <see langword="null"/> means "don't send the sub-packet at
    /// all," matching legacy's <c>Log.m_LogSet.m_FSKNR</c> gate AND <c>FskStationIdEncoder.Generate</c>'s
    /// own "pass null when disabled" contract -- the caller collapses gate+value into this one
    /// nullable field rather than carrying a separate bool.</summary>
    public string? NrRstText { get; init; }
}
