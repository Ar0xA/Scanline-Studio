using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Application;

/// <summary>Plain, UI-safe view over every settings section the Options dialog edits -- exists so
/// `ScanlineStudio.UI` never has to reference the concrete `ScanlineStudio.Core.Audio`/
/// `ScanlineStudio.Core.Radio`/`ScanlineStudio.Core.Localization` settings-section record types
/// directly (that would violate the "UI only depends on Application/Abstractions" layering rule --
/// `UiLayeringArchitectureTests`). <see cref="OptionsSettingsService"/> is the only thing that
/// translates between this and the real per-module sections.</summary>
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
    bool AppPriorityIsHigh);
