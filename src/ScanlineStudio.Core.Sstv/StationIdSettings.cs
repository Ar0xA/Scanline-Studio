using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Persisted CW-ID/FSK station-ID configuration -- see the CW-ID/FSK station-ID subsystem
/// implementation plan for the full legacy citation trail. <see cref="ScanlineStudio.Application.SstvSessionService"/>
/// is the only reader (resolves this alongside <c>OperatorSettings</c>/<c>IMacroTextResolver</c> into
/// a <see cref="ScanlineStudio.Abstractions.Sstv.StationIdTransmitOptions"/> right before each
/// transmission -- see that type's own doc comment for why the split exists). The Options dialog's
/// Identification tab (CW-ID/FSK station-ID subsystem Phase 6) writes this section; every field
/// defaults to legacy's own compiled-in default, so an absent section behaves exactly like a fresh
/// legacy install with nothing configured.
///
/// Several properties are nullable even though their real default isn't the CLR default (<c>0</c>/
/// <c>false</c>) -- System.Text.Json does not honor an <c>init</c>-only property's C# initializer
/// default when that property is absent from the JSON payload (the same confirmed STJ limitation
/// <see cref="ScanlineStudio.Core.Audio.AudioDeviceSettings.CaptureThreadPriority"/> documents) -- treat
/// <see langword="null"/> as "unset, apply the documented default" at every read site, never add a
/// non-null property-initializer default here.</summary>
public sealed record StationIdSettings
{
    public const string SectionKey = "StationId";

    /// <summary><c>Main.cpp:905</c>.</summary>
    public const int DefaultCwWpm = 28;

    /// <summary><c>Main.cpp:907</c>.</summary>
    public const double DefaultCwToneFrequencyHz = 1000;

    /// <summary><c>Main.cpp:906</c> -- legacy's first-run pre-fill for <see cref="CwText"/>. Purely a
    /// UI nicety (never fires while <see cref="CwIdMode"/> itself defaults to <see cref="CwIdMode.Off"/>),
    /// applied the same "absent means apply the documented default" way as <see cref="DefaultCwWpm"/>.</summary>
    public const string DefaultCwText = "DE %m";

    /// <summary><c>LogFile.cpp:378</c>: <c>Log.m_LogSet.m_FSKNR</c> defaults to 1 (enabled), not 0 --
    /// the one field on this record whose legacy default is "on."</summary>
    public const bool DefaultNrRstEnabled = true;

    /// <summary><c>Main.cpp:7021-7025</c>. Default <see cref="CwIdMode.Off"/> matches legacy's own
    /// zero-initialized <c>sys.m_CWID</c> (<c>Main.cpp</c>'s startup defaults set it to 0 elsewhere in
    /// the same block as the other station-ID fields below) and the enum's own CLR default -- plain
    /// non-nullable, no STJ trap.</summary>
    public CwIdMode CwIdMode { get; init; }

    /// <summary><c>sys.m_CWIDText</c> -- raw, pre-macro-resolution text (<see cref="ScanlineStudio.Application.IMacroTextResolver"/>
    /// resolves it at TX time, not here). <see langword="null"/>/empty both mean "nothing configured,"
    /// matching <c>OutputCWID</c>'s own <c>!sys.m_CWIDText.IsEmpty()</c> gate
    /// (<c>Main.cpp:6969</c>) -- checked on this RAW field, not the resolved text. Legacy pre-fills
    /// this to <see cref="DefaultCwText"/> even though <see cref="CwIdMode"/> itself defaults to
    /// <see cref="CwIdMode.Off"/> (so it never fires by default either way) -- purely a
    /// first-run-UI nicety, not behaviorally load-bearing; the CLR default here stays
    /// <see langword="null"/> (matching this record's own no-non-null-initializer STJ-trap rule).
    /// UNLIKE <see cref="CwWpm"/>/<see cref="CwToneFrequencyHz"/>'s "null means apply the documented
    /// default" rule, <see cref="DefaultCwText"/> is applied ONLY by
    /// <c>ScanlineStudio.Application.OptionsSettingsService</c>'s Options-dialog display path (so
    /// the empty box shows the legacy first-run hint) -- <c>SstvSessionService</c>'s TX-time
    /// resolution reads this field raw and treats <see langword="null"/>/empty as "nothing
    /// configured" per the <c>OutputCWID</c> gate cited above, exactly matching legacy's own
    /// zero-initialized-then-user-typed-over field, not a "null == DE %m" substitution.</summary>
    public string? CwText { get; init; }

    /// <summary>WPM, wired to actually drive CW-ID dot length (a deliberate, user-approved deviation
    /// from an apparent legacy bug -- see <see cref="CwMorseGenerator"/>'s own doc comment). Nullable
    /// per this record's own doc comment -- <see langword="null"/> means "apply <see cref="DefaultCwWpm"/>."</summary>
    public int? CwWpm { get; init; }

    /// <summary><c>sys.m_CWIDFreq</c>. Nullable per this record's own doc comment -- <see langword="null"/>
    /// means "apply <see cref="DefaultCwToneFrequencyHz"/>."</summary>
    public double? CwToneFrequencyHz { get; init; }

    /// <summary><c>sys.m_TXFSKID</c> (<c>Main.cpp:903</c>, default 0/false) -- plain non-nullable, no
    /// STJ trap. Gates BOTH the post-image FSK-ID packet emission (jointly with the operator's own
    /// callsign being non-empty, checked at the TX-options-resolution boundary, not here) AND which
    /// footer-tone shape gets emitted (<c>Main.cpp:6997/7010</c>) -- unlike the callsign-emptiness
    /// check, the footer-tone branch depends on THIS flag alone.</summary>
    public bool FskIdTxEnabled { get; init; }

    /// <summary><c>m_fskdecode</c> (<c>sstv.h:708</c>, <c>.ini</c> key <c>RXFSKID</c>, legacy default
    /// 0/false).</summary>
    public const bool DefaultFskIdRxEnabled = true;

    /// <summary>User-directed override (2026-09-18): defaults to
    /// <see cref="DefaultFskIdRxEnabled"/> (<see langword="true"/>) in this app, NOT legacy's own
    /// 0/false -- a local RX-decode default is off-air behavior (nothing transmitted changes), not
    /// the wire-observable protocol CLAUDE.md §0/§0a's "never deviate from legacy" rule covers, so
    /// this needed only the user's own decision, not interop justification. Nullable per this
    /// record's own doc comment -- an empirically-verified real STJ trap (a non-null <c>bool</c>
    /// property initializer is silently ignored for an absent JSON key; confirmed with a standalone
    /// repro against this exact source-gen shape before relying on it, not assumed) -- <see
    /// langword="null"/> means "apply <see cref="DefaultFskIdRxEnabled"/>." Consumed by
    /// <see cref="ScanlineStudio.Core.Sstv.AnalogFmSstvDecoder.StationIdDecodeEnabled"/>.</summary>
    public bool? FskIdRxEnabled { get; init; }

    /// <summary>fsk_cwid.md §8.3: new capability, no legacy equivalent (YONIQ never decoded CW on
    /// receive).</summary>
    public const bool DefaultCwIdRxEnabled = true;

    /// <summary>User-directed override (2026-09-18): defaults to <see cref="DefaultCwIdRxEnabled"/>
    /// (<see langword="true"/>), explicitly AHEAD of fsk_cwid.md §12's own "not default-on before the
    /// classical decoder has been exercised on the legacy fixture" rule -- BACKLOG.md item B-P4 (a
    /// real capture from the legacy Windows binary, needed to build a golden-vector test for
    /// <see cref="ScanlineStudio.Core.Cw.ClassicalCwDecoder"/>) is still open as of this change, so
    /// this decoder now runs by default on real off-air audio with only round-trip (self) tests
    /// behind it, not a verified-against-a-known-real-answer test -- flagged to the user before this
    /// override, decision stands regardless. Nullable for the same real, verified STJ-trap reason
    /// <see cref="FskIdRxEnabled"/>'s own doc comment explains -- <see langword="null"/> means "apply
    /// <see cref="DefaultCwIdRxEnabled"/>." Consumed by
    /// <see cref="ScanlineStudio.Application.SstvSessionService"/>'s CW-ID capture arm.</summary>
    public bool? CwIdRxEnabled { get; init; }

    /// <summary>fsk_cwid.md §8.2: how many seconds of post-image audio the CW-ID capture window
    /// spans. Nullable per this record's own doc comment -- <see langword="null"/> means "apply
    /// <see cref="DefaultCwIdRxWindowSeconds"/>." Clamped to <see cref="MinCwIdRxWindowSeconds"/>-
    /// <see cref="MaxCwIdRxWindowSeconds"/> at the read site, not here (this record does no
    /// validation of its own, matching every other field's "validate at the boundary" convention).</summary>
    public int? CwIdRxWindowSeconds { get; init; }

    /// <summary>fsk_cwid.md §8.1: "DE W1AW" is ~2.4s at 28 WPM, ~6.7s at 10 WPM; "DE W1AW/M" at
    /// 10 WPM approaches 9s; FSK ID with NR/RST precedes it by ~1-2s. 12s covers every realistic
    /// ID with margin.</summary>
    public const int DefaultCwIdRxWindowSeconds = 12;

    /// <summary>fsk_cwid.md §8.2's own stated floor.</summary>
    public const int MinCwIdRxWindowSeconds = 5;

    /// <summary>fsk_cwid.md §8.1: matches DeepCW's own stated max window (§6).</summary>
    public const int MaxCwIdRxWindowSeconds = 20;

    /// <summary><c>Log.m_LogSet.m_FSKNR</c>. Nullable per this record's own doc comment --
    /// <see langword="null"/> means "apply <see cref="DefaultNrRstEnabled"/> (true)."</summary>
    public bool? NrRstEnabled { get; init; }

    /// <summary>Legacy's real NR/RST source is the live "His RST" exchange field on the logging
    /// window (<c>HisRST->Text</c>, <c>Main.cpp:6928</c>) -- this port has no "current QSO" concept
    /// to source that from (see the implementation plan's RX-side note making the same call for
    /// auto-fill), so v1 treats it as a simple, separately user-configured static value instead of
    /// live contest-exchange automation. <see langword="null"/>/empty both mean nothing to send.</summary>
    public string? NrRstText { get; init; }

    /// <summary><c>sys.m_MMVID</c> -- the sound-file path for <see cref="CwIdMode.SoundFile"/>
    /// (`Main.cpp:6847-6902`'s <c>OutputMMV</c>). <see langword="null"/>/empty means unconfigured,
    /// matching legacy's own <c>!sys.m_MMVID.IsEmpty()</c> gate -- same "no first-run hint text to
    /// accidentally resurrect" convention as <see cref="NrRstText"/>, NOT <see cref="CwText"/>'s
    /// (which needs its own "?? string.Empty" persistence guard specifically because it has a
    /// pre-fill default this field has no equivalent of). `ScanlineStudio.Application.SstvSessionService`
    /// reads, parses, and resamples the file this points to (via <c>MmvSoundFile</c>); this settings
    /// record itself does no file I/O.</summary>
    public string? SoundFileMmvPath { get; init; }
}
