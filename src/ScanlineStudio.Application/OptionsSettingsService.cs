using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Sstv;
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
        CaptureDeviceName: new AudioDeviceSettings().CaptureDeviceName,
        PlaybackDeviceName: new AudioDeviceSettings().PlaybackDeviceName,
        SampleRate: new AudioDeviceSettings().SampleRate,
        RadioBackendId: new RadioConnectionSettings().BackendId,
        RigctldHost: new RadioConnectionSettings().Host,
        RigctldPort: new RadioConnectionSettings().Port,
        HamlibModel: new RadioConnectionSettings().HamlibModel,
        HamlibLibraryPath: new RadioConnectionSettings().HamlibLibraryPath,
        HamlibSerialPort: new RadioConnectionSettings().SerialPort,
        HamlibBaudRate: new RadioConnectionSettings().BaudRate,
        HamlibPttType: new RadioConnectionSettings().PttType,
        HamlibPttPort: new RadioConnectionSettings().PttPort,
        FlrigHost: new RadioConnectionSettings().FlrigHost,
        FlrigPort: new RadioConnectionSettings().FlrigPort,
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
        // Absent -> Hilbert (legacy's real compiled-in default) -- same "?? Hilbert" resolution
        // ScanlineStudio.Host.Program's ISstvDecoder registration applies (that read site also
        // clamps a present-but-out-of-range value; this dialog's own ApplyFromSnapshot does the
        // equivalent Enum.IsDefined clamp for the same reason, see that method's own comment).
        DemodType: new SstvDecoderSettings().DemodType ?? DemodType.Hilbert,
        // Absent -> Wide (legacy's real compiled-in default) -- same "?? Wide" resolution
        // ScanlineStudio.Host.Program's ISstvDecoder registration applies (that read site also
        // clamps a present-but-out-of-range value; this dialog's own ApplyFromSnapshot does the
        // equivalent Enum.IsDefined clamp for the same reason, see that method's own comment).
        RxBpfPreset: new SstvDecoderSettings().RxBpfPreset ?? RxBpfPreset.Wide,
        // Absent -> On (legacy's real compiled-in default, Main.cpp:899) -- same "?? On" resolution
        // ScanlineStudio.Host.Program's ISstvDecoder registration applies (that read site also
        // clamps a present-but-out-of-range value; this dialog's own ApplyFromSnapshot does the
        // equivalent Enum.IsDefined clamp for the same reason, see SstvDecoderSettings.RxBufferMode's
        // own doc comment).
        RxBufferMode: new SstvDecoderSettings().RxBufferMode ?? RxBufferMode.On,
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
        FskIdRxEnabled: new StationIdSettings().FskIdRxEnabled,
        NrRstEnabled: new StationIdSettings().NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled,
        NrRstText: new StationIdSettings().NrRstText,
        // Immutable empty, never a shared mutable List<T> -- this property is static, so a mutable
        // default would be a single shared instance every caller could accidentally mutate.
        AdifUdpDestinations: []);

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
        // MigrateIfNeeded (not a plain GetSection) so this dialog shows exactly what
        // AdifUdpStreamer.SendLoggedQsoAsync will actually send -- an upgrading user's already-
        // working legacy GridTracker config must appear here, not read back as an empty list.
        var adifUdp = AdifUdpStreamingSettings.MigrateIfNeeded(_loadedSettings);

        return new OptionsSnapshot(
            CultureCode: localization.CultureCode,
            CaptureDeviceId: audio.CaptureDeviceId,
            PlaybackDeviceId: audio.PlaybackDeviceId,
            CaptureDeviceName: audio.CaptureDeviceName,
            PlaybackDeviceName: audio.PlaybackDeviceName,
            SampleRate: SstvSampleRate.NormalizePersisted(audio.SampleRate),
            RadioBackendId: radio.BackendId,
            RigctldHost: radio.Host,
            RigctldPort: radio.Port,
            HamlibModel: radio.HamlibModel,
            HamlibLibraryPath: radio.HamlibLibraryPath,
            HamlibSerialPort: radio.SerialPort,
            HamlibBaudRate: radio.BaudRate,
            HamlibPttType: radio.PttType,
            HamlibPttPort: radio.PttPort,
            // "?? default", not raw radio.FlrigHost/Port -- a settings.json predating this backend
            // (or any future hand-edit dropping these keys) deserializes them as null regardless of
            // RadioConnectionSettings' own init defaults, same reasoning as AutoSyncEnabled/etc.
            // above use "?? true" for.
            FlrigHost: radio.FlrigHost ?? "127.0.0.1",
            FlrigPort: radio.FlrigPort ?? 12345,
            Callsign: operatorSettings.Callsign,
            OperatorName: operatorSettings.Name,
            OperatorGrid: operatorSettings.Grid,
            AutoSyncEnabled: decoder.AutoSyncEnabled ?? true,
            AutoSlantEnabled: decoder.AutoSlantEnabled ?? true,
            AutoStopEnabled: decoder.AutoStopEnabled ?? false,
            SyncRestartEnabled: decoder.SyncRestartEnabled ?? true,
            SenseLevel: decoder.SenseLevel ?? 1,
            DemodType: decoder.DemodType ?? DemodType.Hilbert,
            RxBpfPreset: decoder.RxBpfPreset ?? RxBpfPreset.Wide,
            RxBufferMode: decoder.RxBufferMode ?? RxBufferMode.On,
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
            FskIdRxEnabled: stationId.FskIdRxEnabled,
            NrRstEnabled: stationId.NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled,
            NrRstText: stationId.NrRstText,
            AdifUdpDestinations: adifUdp.Destinations ?? []);
    }

    public async Task SaveAsync(OptionsSnapshot snapshot, CancellationToken ct = default)
    {
        // Auditor blocker finding: this used to read every "previous*" value off _loadedSettings --
        // the snapshot captured once when LoadAsync ran at dialog-open time, not the current file.
        // Anything written to a section this dialog doesn't fully own (e.g. AudioDeviceSettings.
        // TxVolumePercent, set live and immediately by the Radio/CAT tab's own Pwr slider + Tune
        // button WHILE this same dialog is open, via SstvSessionService.SetTxVolumePercentAsync --
        // completely independent of this dialog's usual load-once/save-on-click flow) would get
        // silently reverted back to its dialog-open value the moment the user clicked Save in the
        // SAME session -- exactly the failure that feature exists to prevent (dial power on the
        // meter, hit Save, transmit at the wrong power with the UI still showing the right number).
        // Re-reading fresh from disk right here closes the whole class, not just this one field --
        // the same staleness risk applies to every section below, present or future, written by
        // anything other than this dialog's own Save.
        var currentSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);

        // Tier B audit finding: these four (Localization/AppPerformance/Operator/QrzLookup) used to
        // build a fresh `new X { ... }` instead of `previous with { ... }` like every OTHER section
        // here -- harmless today only because none of these four records currently has a field the
        // dialog doesn't own (confirmed field-by-field), but it's the exact sibling-inconsistency
        // shape this whole sweep keeps finding: the day a non-dialog field is added to any of these
        // four (a QRZ session-cache token, an operator default-power field, ...), every Options Save
        // would silently reset it, with no existing test able to catch it. Read previous* up front
        // and preserve via `with` uniformly, matching AfcEnabled/ClientId's own established pattern.
        var previousLocalization = currentSettings.GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings) ?? new LocalizationSettings();
        var previousAppPerformance = currentSettings.GetSection(AppPerformanceSettings.SectionKey, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings) ?? new AppPerformanceSettings();
        var previousOperator = currentSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings) ?? new OperatorSettings();
        var previousQrzLookup = currentSettings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();
        var previousAudio = currentSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
        var sampleRateToPersist = SstvSampleRate.IsSupported(snapshot.SampleRate)
            ? snapshot.SampleRate
            : SstvSampleRate.NormalizePersisted(previousAudio.SampleRate);
        var previousRadio = currentSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();
        var previousDecoder = currentSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
        var previousStationId = currentSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings) ?? new StationIdSettings();
        // MigrateIfNeeded (not a plain GetSection ?? new X()) -- same reasoning as LoadAsync above:
        // a user who opens the dialog and immediately hits Save, with only a legacy GridTracker
        // section on disk, must persist the MIGRATED ClientId/destination, not silently drop it back
        // to an empty AdifUdpStreamingSettings just because the new section was never explicitly
        // read through this exact call before.
        var previousAdifUdp = AdifUdpStreamingSettings.MigrateIfNeeded(currentSettings);

        var settings = currentSettings
            .WithSection(LocalizationSettings.SectionKey, previousLocalization with { CultureCode = snapshot.CultureCode }, LocalizationSettingsJsonContext.Default.LocalizationSettings)
            .WithSection(
                AudioDeviceSettings.SectionKey,
                previousAudio with
                {
                    CaptureDeviceId = snapshot.CaptureDeviceId,
                    PlaybackDeviceId = snapshot.PlaybackDeviceId,
                    CaptureDeviceName = snapshot.CaptureDeviceName,
                    PlaybackDeviceName = snapshot.PlaybackDeviceName,
                    // Legacy Options preserves the previous valid rate when the typed value is
                    // outside 5000..CLOCKMAX (Option.cpp:421-424); startup's invalid-value behavior
                    // is deliberately different and falls back to 11025.
                    SampleRate = sampleRateToPersist,
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
                previousAppPerformance with { ProcessPriority = snapshot.AppPriorityIsHigh ? System.Diagnostics.ProcessPriorityClass.High : null },
                AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings)
            .WithSection(
                RadioConnectionSettings.SectionKey,
                previousRadio with
                {
                    BackendId = snapshot.RadioBackendId,
                    Host = snapshot.RigctldHost,
                    Port = snapshot.RigctldPort,
                    HamlibModel = snapshot.HamlibModel,
                    HamlibLibraryPath = snapshot.HamlibLibraryPath,
                    SerialPort = snapshot.HamlibSerialPort,
                    BaudRate = snapshot.HamlibBaudRate,
                    PttType = snapshot.HamlibPttType,
                    PttPort = snapshot.HamlibPttPort,
                    FlrigHost = snapshot.FlrigHost,
                    FlrigPort = snapshot.FlrigPort,
                },
                RadioSettingsJsonContext.Default.RadioConnectionSettings)
            .WithSection(
                OperatorSettings.SectionKey,
                previousOperator with { Callsign = snapshot.Callsign, Name = snapshot.OperatorName, Grid = snapshot.OperatorGrid },
                OperatorSettingsJsonContext.Default.OperatorSettings)
            .WithSection(
                SstvDecoderSettings.SectionKey,
                // AfcEnabled is preserved as-is -- this dialog has no control for it (legacy's AFC
                // is always-on with no user-facing toggle of its own, see that field's own doc
                // comment). AutoStopEnabled/SyncRestartEnabled wired 2026-08-12, after fixing their
                // own loc text's field/label semantics mismatch (see OptionsWindowView.axaml's own
                // comment at that row). SenseLevel wired same batch, one item later -- no loc-text
                // fix needed for it (already accurate, see AnalogFmSstvDecoder.SenseLevelPresets'
                // own doc comment for the legacy source). DemodType wired 2026-08-12 (demod-type
                // runtime-dispatch subsystem, Phase 4) -- see SstvDecoderSettings.DemodType's own doc
                // comment for the absent-vs-out-of-range fallback shape. RxBpfPreset wired 2026-08-12
                // (RX BPF subsystem, Phase 4) -- same fallback shape as DemodType, see
                // SstvDecoderSettings.RxBpfPreset's own doc comment. RxBufferMode wired 2026-08-15
                // (RX buffer subsystem, Phase 9) -- same fallback shape as DemodType/RxBpfPreset, see
                // SstvDecoderSettings.RxBufferMode's own doc comment (already a live, consumed
                // decoder setting since Phase 3 -- this dialog was the only missing piece).
                previousDecoder with
                {
                    AutoSyncEnabled = snapshot.AutoSyncEnabled,
                    AutoSlantEnabled = snapshot.AutoSlantEnabled,
                    AutoStopEnabled = snapshot.AutoStopEnabled,
                    SyncRestartEnabled = snapshot.SyncRestartEnabled,
                    SenseLevel = snapshot.SenseLevel,
                    DemodType = snapshot.DemodType,
                    RxBpfPreset = snapshot.RxBpfPreset,
                    RxBufferMode = snapshot.RxBufferMode,
                },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
            .WithSection(
                QrzLookupSettings.SectionKey,
                previousQrzLookup with { Enabled = snapshot.QrzLookupEnabled, Username = snapshot.QrzLookupUsername, Password = snapshot.QrzLookupPassword },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings)
            .WithSection(
                StationIdSettings.SectionKey,
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
                    // No "?? string.Empty" trap here unlike CwText above: LoadAsync reads
                    // NrRstText raw with no "?? DefaultXxx" fallback (StationIdSettings.NrRstText's
                    // own doc comment -- null/empty both mean "nothing to send," no first-run hint
                    // text exists to accidentally resurrect), so persisting the raw snapshot value
                    // verbatim is correct.
                    NrRstEnabled = snapshot.NrRstEnabled,
                    NrRstText = snapshot.NrRstText,
                },
                StationIdSettingsJsonContext.Default.StationIdSettings)
            .WithSection(
                AdifUdpStreamingSettings.SectionKey,
                // ClientId is preserved as-is -- same reasoning as AfcEnabled above, this dialog
                // has no control for it (see OptionsSnapshot's own doc comment).
                previousAdifUdp with { Destinations = snapshot.AdifUdpDestinations },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings);

        await _settingsStore.SaveAsync(settings, ct).ConfigureAwait(false);
        _loadedSettings = settings;
        Log.Saved(_logger, snapshot.RadioBackendId, sampleRateToPersist, snapshot.CultureCode);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Options saved: radioBackend={RadioBackendId}, sampleRate={SampleRate}, culture={CultureCode}")]
        public static partial void Saved(ILogger logger, string radioBackendId, int sampleRate, string? cultureCode);
    }
}
