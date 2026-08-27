using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Logbook;
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
/// <c>AdifUdpStreamingSettings.ClientId</c> has NO field here -- no dialog control exists for it
/// (only <see cref="AdifUdpDestinations"/>, the list-valued field, is dialog-editable);
/// <see cref="OptionsSettingsService.SaveAsync"/> preserves it as-is (same pattern as
/// <c>SstvDecoderSettings.AfcEnabled</c>), never silently reset by a Save from this dialog.
/// <c>StationIdSettings.NrRstEnabled</c>/<c>NrRstText</c> WERE the same shape until 2026-08-15 (see
/// <see cref="NrRstEnabled"/>/<see cref="NrRstText"/> below) -- the RX-side equivalent,
/// <c>RxImagePaneViewModel.DecodedNrRst</c>, is a display-only decoded value with no settings
/// section at all, a separate and still-open gap, not fixed by this change.</summary>
public sealed record OptionsSnapshot(
    string? CultureCode,
    string? CaptureDeviceId,
    string? PlaybackDeviceId,
    // See AudioDeviceSettings.CaptureDeviceName/PlaybackDeviceName's own doc comment -- a fallback
    // recovery aid for backend device-id churn, not itself a primary key.
    string? CaptureDeviceName,
    string? PlaybackDeviceName,
    int SampleRate,
    // Stub survey Tier 3, "Clock calibration" piece 1 -- see AudioDeviceSettings.TxSampleRateOffsetHz's
    // own doc comment.
    double TxSampleRateOffsetHz,
    string RadioBackendId,
    string? RigctldHost,
    int? RigctldPort,
    uint? HamlibModel,
    string? HamlibLibraryPath,
    string? HamlibSerialPort,
    int? HamlibBaudRate,
    string? HamlibPttType,
    string? HamlibPttPort,
    string? FlrigHost,
    int? FlrigPort,
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
    CwIdMode CwIdMode,
    string? CwText,
    int CwWpm,
    double CwToneFrequencyHz,
    bool FskIdTxEnabled,
    bool FskIdRxEnabled,
    bool NrRstEnabled,
    string? NrRstText,
    DemodType DemodType,
    RxBpfPreset RxBpfPreset,
    RxBufferMode RxBufferMode,
    IReadOnlyList<AdifUdpDestination> AdifUdpDestinations);
