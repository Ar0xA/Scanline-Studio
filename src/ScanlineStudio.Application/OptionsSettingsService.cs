using Microsoft.Extensions.Logging;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>Reads/writes every settings section the Options dialog edits, translating to/from
/// <see cref="OptionsSnapshot"/> -- see that record's own doc comment for why this indirection
/// exists (keeps `ScanlineStudio.UI` off every concrete `ScanlineStudio.Core.*` settings-section
/// type). Not behind an interface: exactly one implementation, one consumer type
/// (`OptionsWindowViewModel`) -- matches this project's own "don't add abstractions beyond what's
/// needed" convention (e.g. `AppDockFactory` is likewise a concrete class, not an interface).</summary>
public sealed partial class OptionsSettingsService
{
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<OptionsSettingsService> _logger;

    private AppSettings _loadedSettings = new();

    public OptionsSettingsService(ISettingsStore settingsStore, ILogger<OptionsSettingsService> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>Derived from each real settings-section record's own default field values, never
    /// hardcoded again at the call site -- so a future change to e.g. <see cref="AudioDeviceSettings"/>'s
    /// default sample rate can't silently drift out of sync with what "reset to default" shows in
    /// the Options dialog.</summary>
    public static OptionsSnapshot Defaults { get; } = new(
        CultureCode: new LocalizationSettings().CultureCode,
        CaptureDeviceId: new AudioDeviceSettings().CaptureDeviceId,
        PlaybackDeviceId: new AudioDeviceSettings().PlaybackDeviceId,
        SampleRate: new AudioDeviceSettings().SampleRate,
        RadioBackendId: new RadioConnectionSettings().BackendId,
        RigctldHost: new RadioConnectionSettings().Host,
        RigctldPort: new RadioConnectionSettings().Port,
        HamlibModel: new RadioConnectionSettings().HamlibModel,
        HamlibSerialPort: new RadioConnectionSettings().SerialPort,
        HamlibBaudRate: new RadioConnectionSettings().BaudRate,
        HamlibPttType: new RadioConnectionSettings().PttType,
        Callsign: new OperatorSettings().Callsign,
        OperatorName: new OperatorSettings().Name,
        OperatorGrid: new OperatorSettings().Grid);

    public async Task<OptionsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        _loadedSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);

        var localization = _loadedSettings.GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings) ?? new LocalizationSettings();
        var audio = _loadedSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
        var radio = _loadedSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();
        var operatorSettings = _loadedSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings) ?? new OperatorSettings();

        return new OptionsSnapshot(
            CultureCode: localization.CultureCode,
            CaptureDeviceId: audio.CaptureDeviceId,
            PlaybackDeviceId: audio.PlaybackDeviceId,
            SampleRate: audio.SampleRate,
            RadioBackendId: radio.BackendId,
            RigctldHost: radio.Host,
            RigctldPort: radio.Port,
            HamlibModel: radio.HamlibModel,
            HamlibSerialPort: radio.SerialPort,
            HamlibBaudRate: radio.BaudRate,
            HamlibPttType: radio.PttType,
            Callsign: operatorSettings.Callsign,
            OperatorName: operatorSettings.Name,
            OperatorGrid: operatorSettings.Grid);
    }

    public async Task SaveAsync(OptionsSnapshot snapshot, CancellationToken ct = default)
    {
        var previousAudio = _loadedSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
        var previousRadio = _loadedSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();

        var settings = _loadedSettings
            .WithSection(LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = snapshot.CultureCode }, LocalizationSettingsJsonContext.Default.LocalizationSettings)
            .WithSection(
                AudioDeviceSettings.SectionKey,
                previousAudio with { CaptureDeviceId = snapshot.CaptureDeviceId, PlaybackDeviceId = snapshot.PlaybackDeviceId, SampleRate = snapshot.SampleRate },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)
            .WithSection(
                RadioConnectionSettings.SectionKey,
                previousRadio with
                {
                    BackendId = snapshot.RadioBackendId,
                    Host = snapshot.RigctldHost,
                    Port = snapshot.RigctldPort,
                    HamlibModel = snapshot.HamlibModel,
                    SerialPort = snapshot.HamlibSerialPort,
                    BaudRate = snapshot.HamlibBaudRate,
                    PttType = snapshot.HamlibPttType,
                },
                RadioSettingsJsonContext.Default.RadioConnectionSettings)
            .WithSection(
                OperatorSettings.SectionKey,
                new OperatorSettings { Callsign = snapshot.Callsign, Name = snapshot.OperatorName, Grid = snapshot.OperatorGrid },
                OperatorSettingsJsonContext.Default.OperatorSettings);

        await _settingsStore.SaveAsync(settings, ct).ConfigureAwait(false);
        _loadedSettings = settings;
        Log.Saved(_logger, snapshot.RadioBackendId, snapshot.SampleRate, snapshot.CultureCode);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Options saved: radioBackend={RadioBackendId}, sampleRate={SampleRate}, culture={CultureCode}")]
        public static partial void Saved(ILogger logger, string radioBackendId, int sampleRate, string? cultureCode);
    }
}
