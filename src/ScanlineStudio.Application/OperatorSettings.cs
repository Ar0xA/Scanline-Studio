namespace ScanlineStudio.Application;

/// <summary>The operator's own identity. Lives in <c>ScanlineStudio.Application</c> rather than a
/// <c>ScanlineStudio.Core.*</c> project since it doesn't belong to any single hardware/DSP domain;
/// this is the orchestration layer TX-overlay-macro and logbook features read it from --
/// <see cref="IMacroTextResolver"/> is the first consumer of <see cref="Name"/>/<see cref="Grid"/>.
/// <see cref="Name"/>/<see cref="Grid"/> have no legacy MMSSTV equivalent (legacy's own macro
/// engine, <c>MacroText</c>, has no "my name"/"my QTH" token at all -- only <c>%m</c> for callsign)
/// -- added deliberately, matching mock2's own "MY NAME"/"MY GRID" insert-field chips
/// (`TxImageEditorPaneView.axaml`) and real-world SSTV overlay conventions other software already
/// supports.</summary>
public sealed record OperatorSettings
{
    public const string SectionKey = "Operator";

    /// <summary>RST default plan (2026-09-01): the near-universal "signal report" applied when
    /// <see cref="DefaultRst"/> is unset (see that property's own doc comment for the full
    /// reasoning behind `"595"`, not ham radio's classic `"599"`). This is the one canonical source
    /// of `"595"` -- nowhere else in the codebase should hardcode it a second time.</summary>
    public const string DefaultRstFallback = "595";

    public string? Callsign { get; init; }

    public string? Name { get; init; }

    /// <summary>Maidenhead grid square (or any free-text location the operator prefers) --
    /// deliberately loose/unvalidated, matching <see cref="Callsign"/>'s own untyped-string
    /// precedent; nothing in this pass computes distance/bearing from it.</summary>
    public string? Grid { get; init; }

    /// <summary>RST default plan (2026-09-01): the near-universal "signal report" prefilled into the
    /// Log QSO form's RST Sent/Received fields, seeded fresh on every new-entry prefill (both fields
    /// from this ONE value -- SSTV's real-world convention doesn't distinguish direction). `"595"`
    /// (<see cref="DefaultRstFallback"/>), not ham radio's classic `"599"` -- confirmed against
    /// actual legacy YONIQ source (`yoniq-old/YONIQ-main/Main.cpp`'s own `LogRST` combo-box default
    /// list, ADIF-export `RST_RCVD` fallback, and multiple QSO-start-switch literals all use
    /// `"595"`; `"599"` appears nowhere in that file). SSTV operators exchange R-S-V
    /// (Readability/Strength/Video), not ham radio's classic R-S-T, and near-universally report
    /// "595" regardless of actual picture quality -- matching this app's own
    /// `RxImagePaneViewModel.DecodedNrRst`/`{rsv}` template-macro conventions elsewhere.
    ///
    /// Deliberately NO non-null property initializer here, unlike an earlier version of this field
    /// -- System.Text.Json does not honor an <see langword="init"/>-only property's C# initializer
    /// default when that property is absent from the JSON payload (the same confirmed STJ
    /// limitation <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.CaptureThreadPriority</c> and
    /// <c>ScanlineStudio.Core.Sstv.StationIdSettings</c> document -- caught here by
    /// <c>OperatorSettingsTests.RoundTripsThroughItsJsonContext_WithNameGridAndDefaultRstUnset</c>,
    /// which failed against the initializer-based version (the actual upgrade-path backfill this
    /// initializer would have broken is covered one layer up, by
    /// <c>OptionsWindowViewModelTests.Constructor_WithNoDefaultRstKeyOnDisk_FallsBackTo595</c>).
    /// <see langword="null"/> means "unset,
    /// apply <see cref="DefaultRstFallback"/>" at every read site
    /// (<c>OptionsSettingsService.Defaults</c>/<c>LoadAsync</c>), same "null means apply the
    /// documented default" convention <c>StationIdSettings.CwText</c>/<c>DefaultCwText</c> already
    /// use -- never add a non-null property-initializer default here again. Still freely editable in
    /// Options -- an operator clearing the box persists an explicit empty string (not null), so it
    /// reads back as blank, not resurrected to "595" -- same "?? string.Empty" Save-side trap-avoidance
    /// <c>OptionsSettingsService.SaveAsync</c> already applies to <c>CwText</c>.</summary>
    public string? DefaultRst { get; init; }
}
