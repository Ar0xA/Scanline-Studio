using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application;

/// <summary>Plain, UI-safe view over every settings section the Options dialog edits -- exists so
/// `ScanlineStudio.UI` never has to reference the concrete `ScanlineStudio.Core.Audio`/
/// `ScanlineStudio.Core.Radio`/`ScanlineStudio.Core.Localization` settings-section record types
/// directly (that would violate the "UI only depends on Application/Abstractions" layering rule --
/// `UiLayeringArchitectureTests`). <see cref="OptionsSettingsService"/> is the only thing that
/// translates between this and the real per-module sections. <see cref="CwIdMode"/> is a plain
/// <c>ScanlineStudio.Abstractions.Sstv</c> enum (see its own doc comment for why it lives there,
/// not on <c>StationIdSettings</c> alongside the rest of that record's fields), so reusing it
/// directly here doesn't violate that rule.
///
/// <c>StationIdSettings.NrRstEnabled</c>/<c>NrRstText</c> deliberately have NO fields here -- no
/// Options-dialog mockup row exists for them yet (the CW-ID/FSK station-ID subsystem's Phase 5 left
/// the RX-side equivalent, <c>RxImagePaneViewModel.DecodedNrRst</c>, unbound for the same reason).
/// <see cref="OptionsSettingsService.SaveAsync"/> preserves whatever those two fields already were
/// (same pattern as <c>SstvDecoderSettings.AfcEnabled</c>, which also has no dialog control),
/// they're never silently reset by a Save from this dialog.</summary>
public sealed record OptionsSnapshot(
    string? CultureCode,
    string? CaptureDeviceId,
    string? PlaybackDeviceId,
    int SampleRate,
    string RadioBackendId,
    string? RigctldHost,
    int? RigctldPort,
    uint? HamlibModel,
    string? HamlibSerialPort,
    int? HamlibBaudRate,
    string? HamlibPttType,
    string? Callsign,
    string? OperatorName,
    string? OperatorGrid,
    bool AutoSyncEnabled,
    bool AutoSlantEnabled,
    bool QrzLookupEnabled,
    string? QrzLookupUsername,
    string? QrzLookupPassword,
    bool AutoStopEnabled,
    bool SyncRestartEnabled,
    int SenseLevel,
    AudioChannelSource CaptureChannelSource,
    bool StereoTxEnabled,
    bool AppPriorityIsHigh,
    CwIdMode CwIdMode,
    string? CwText,
    int CwWpm,
    double CwToneFrequencyHz,
    bool FskIdTxEnabled,
    bool FskIdRxEnabled);
