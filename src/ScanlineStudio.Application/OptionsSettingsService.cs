using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Sstv;
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
        OperatorGrid: new OperatorSettings().Grid,
        // Same `?? true` resolution as ScanlineStudio.Host.Program's ISstvDecoder registration --
        // SstvDecoderSettings itself deliberately never hardcodes a non-null default (see that
        // record's own doc comment), so both read sites apply it identically.
        AutoSyncEnabled: new SstvDecoderSettings().AutoSyncEnabled ?? true,
        AutoSlantEnabled: new SstvDecoderSettings().AutoSlantEnabled ?? true,
        // AutoStopEnabled's desired default is false, unlike every other decoder toggle here --
        // see SstvDecoderSettings.AutoStopEnabled's own doc comment (legacy's real fresh-install
        // default, sys.m_AutoStop = 0, Main.cpp:900).
        AutoStopEnabled: new SstvDecoderSettings().AutoStopEnabled ?? false,
        SyncRestartEnabled: new SstvDecoderSettings().SyncRestartEnabled ?? true,
        SenseLevel: new SstvDecoderSettings().SenseLevel ?? 1,
        QrzLookupEnabled: new QrzLookupSettings().Enabled ?? false,
        QrzLookupUsername: new QrzLookupSettings().Username,
        QrzLookupPassword: new QrzLookupSettings().Password,
        CaptureChannelSource: new AudioDeviceSettings().CaptureChannelSource,
        StereoTxEnabled: new AudioDeviceSettings().StereoTxEnabled,
        // AppPerformanceSettings.ProcessPriority's own desired "unset" default is null ("don't touch
        // the OS default"), which is also ProcessPriorityClass.Normal in every practical sense -- see
        // that record's own doc comment. AppPriorityIsHigh: false round-trips that correctly.
        AppPriorityIsHigh: new AppPerformanceSettings().ProcessPriority == System.Diagnostics.ProcessPriorityClass.High,
        CwIdMode: new StationIdSettings().CwIdMode,
        CwText: new StationIdSettings().CwText ?? StationIdSettings.DefaultCwText,
        CwWpm: new StationIdSettings().CwWpm ?? StationIdSettings.DefaultCwWpm,
        CwToneFrequencyHz: new StationIdSettings().CwToneFrequencyHz ?? StationIdSettings.DefaultCwToneFrequencyHz,
        FskIdTxEnabled: new StationIdSettings().FskIdTxEnabled,
        FskIdRxEnabled: new StationIdSettings().FskIdRxEnabled);

    public async Task<OptionsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        _loadedSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);

        var localization = _loadedSettings.GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings) ?? new LocalizationSettings();
        var audio = _loadedSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
        var radio = _loadedSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();
        var operatorSettings = _loadedSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings) ?? new OperatorSettings();
        var decoder = _loadedSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
        var qrzLookup = _loadedSettings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();
        var appPerformance = _loadedSettings.GetSection(AppPerformanceSettings.SectionKey, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings) ?? new AppPerformanceSettings();
        var stationId = _loadedSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings) ?? new StationIdSettings();

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
            OperatorGrid: operatorSettings.Grid,
            AutoSyncEnabled: decoder.AutoSyncEnabled ?? true,
            AutoSlantEnabled: decoder.AutoSlantEnabled ?? true,
            AutoStopEnabled: decoder.AutoStopEnabled ?? false,
            SyncRestartEnabled: decoder.SyncRestartEnabled ?? true,
            SenseLevel: decoder.SenseLevel ?? 1,
            QrzLookupEnabled: qrzLookup.Enabled ?? false,
            QrzLookupUsername: qrzLookup.Username,
            QrzLookupPassword: qrzLookup.Password,
            CaptureChannelSource: audio.CaptureChannelSource,
            StereoTxEnabled: audio.StereoTxEnabled,
            AppPriorityIsHigh: appPerformance.ProcessPriority == System.Diagnostics.ProcessPriorityClass.High,
            CwIdMode: stationId.CwIdMode,
            CwText: stationId.CwText ?? StationIdSettings.DefaultCwText,
            CwWpm: stationId.CwWpm ?? StationIdSettings.DefaultCwWpm,
            CwToneFrequencyHz: stationId.CwToneFrequencyHz ?? StationIdSettings.DefaultCwToneFrequencyHz,
            FskIdTxEnabled: stationId.FskIdTxEnabled,
            FskIdRxEnabled: stationId.FskIdRxEnabled);
    }

    public async Task SaveAsync(OptionsSnapshot snapshot, CancellationToken ct = default)
    {
        var previousAudio = _loadedSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
        var previousRadio = _loadedSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();
        var previousDecoder = _loadedSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
        var previousStationId = _loadedSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings) ?? new StationIdSettings();

        var settings = _loadedSettings
            .WithSection(LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = snapshot.CultureCode }, LocalizationSettingsJsonContext.Default.LocalizationSettings)
            .WithSection(
                AudioDeviceSettings.SectionKey,
                previousAudio with
                {
                    CaptureDeviceId = snapshot.CaptureDeviceId,
                    PlaybackDeviceId = snapshot.PlaybackDeviceId,
                    SampleRate = snapshot.SampleRate,
                    CaptureChannelSource = snapshot.CaptureChannelSource,
                    StereoTxEnabled = snapshot.StereoTxEnabled,
                },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)
            .WithSection(
                AppPerformanceSettings.SectionKey,
                // null (not ProcessPriorityClass.Normal) for "Normal" -- matches
                // AppPerformanceSettings.ProcessPriority's own "unset = don't touch the OS default"
                // contract (see that record's own doc comment); Program.cs's read site already
                // treats null as a no-op, functionally equivalent for a freshly-launched process.
                new AppPerformanceSettings { ProcessPriority = snapshot.AppPriorityIsHigh ? System.Diagnostics.ProcessPriorityClass.High : null },
                AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings)
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
                OperatorSettingsJsonContext.Default.OperatorSettings)
            .WithSection(
                SstvDecoderSettings.SectionKey,
                // AfcEnabled is preserved as-is -- this dialog has no control for it (legacy's AFC
                // is always-on with no user-facing toggle of its own, see that field's own doc
                // comment). AutoStopEnabled/SyncRestartEnabled wired 2026-08-12, after fixing their
                // own loc text's field/label semantics mismatch (see OptionsWindowView.axaml's own
                // comment at that row). SenseLevel wired same batch, one item later -- no loc-text
                // fix needed for it (already accurate, see AnalogFmSstvDecoder.SenseLevelPresets'
                // own doc comment for the legacy source).
                previousDecoder with
                {
                    AutoSyncEnabled = snapshot.AutoSyncEnabled,
                    AutoSlantEnabled = snapshot.AutoSlantEnabled,
                    AutoStopEnabled = snapshot.AutoStopEnabled,
                    SyncRestartEnabled = snapshot.SyncRestartEnabled,
                    SenseLevel = snapshot.SenseLevel,
                },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
            .WithSection(
                QrzLookupSettings.SectionKey,
                new QrzLookupSettings { Enabled = snapshot.QrzLookupEnabled, Username = snapshot.QrzLookupUsername, Password = snapshot.QrzLookupPassword },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings)
            .WithSection(
                StationIdSettings.SectionKey,
                // NrRstEnabled/NrRstText are preserved as-is -- same reasoning as AfcEnabled above,
                // this dialog has no control for either yet (see OptionsSnapshot's own doc comment).
                previousStationId with
                {
                    CwIdMode = snapshot.CwIdMode,
                    // "?? string.Empty", not the raw snapshot value: a cleared TextBox must persist
                    // as an explicit empty string, not null -- null round-trips back through
                    // LoadAsync's "?? DefaultCwText" fallback as "DE %m" again, silently undoing the
                    // user's clear on next load/save (auditor round 2 finding).
                    CwText = snapshot.CwText ?? string.Empty,
                    CwWpm = snapshot.CwWpm,
                    CwToneFrequencyHz = snapshot.CwToneFrequencyHz,
                    FskIdTxEnabled = snapshot.FskIdTxEnabled,
                    FskIdRxEnabled = snapshot.FskIdRxEnabled,
                },
                StationIdSettingsJsonContext.Default.StationIdSettings);

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
