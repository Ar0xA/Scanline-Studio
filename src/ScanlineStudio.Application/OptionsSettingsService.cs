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
        TxSampleRateOffsetHz: new AudioDeviceSettings().TxSampleRateOffsetHz,
        // Options stub backlog item 3 -- absent -> legacy's real CSSTVMOD constructor defaults
        // (true/24/false/2000.0, see AudioDeviceSettings.TxBpfEnabled's own doc comment).
        TxBpfEnabled: new AudioDeviceSettings().TxBpfEnabled ?? true,
        TxBpfTapCount: new AudioDeviceSettings().TxBpfTapCount ?? 24,
        TxLpfEnabled: new AudioDeviceSettings().TxLpfEnabled ?? false,
        TxLpfFrequencyHz: new AudioDeviceSettings().TxLpfFrequencyHz ?? 2000.0,
        RadioBackendId: new RadioConnectionSettings().BackendId,
        // "?? fallback" -- Host/Port have no property initializer of their own (see that property's
        // own doc comment for why), unlike FlrigHost/FlrigPort below, which already resolve correctly
        // from a fresh RadioConnectionSettings() construction (this Defaults snapshot is never
        // deserialized, so the STJ trap doesn't apply here, but a genuinely-absent default still
        // needs an explicit fallback regardless).
        RigctldHost: new RadioConnectionSettings().Host ?? RadioConnectionSettings.RigctldHostFallback,
        RigctldPort: new RadioConnectionSettings().Port ?? RadioConnectionSettings.RigctldPortFallback,
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
        // Null means unset -- same "?? Default" resolution CwText/DefaultCwText uses (see
        // OperatorSettings.DefaultRst's own doc comment for the confirmed STJ initializer trap this
        // avoids).
        DefaultRst: new OperatorSettings().DefaultRst ?? OperatorSettings.DefaultRstFallback,
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
        // Options stub backlog item 1 -- absent -> legacy's real CPLL constructor defaults
        // (1.0/1/1500.0/3/900.0, see SstvDecoderSettings.PllVcoGain's own doc comment), same
        // resolution shape as DemodType/RxBpfPreset/RxBufferMode above.
        PllVcoGain: new SstvDecoderSettings().PllVcoGain ?? 1.0,
        PllLoopOrder: new SstvDecoderSettings().PllLoopOrder ?? 1,
        PllLoopCutoffHz: new SstvDecoderSettings().PllLoopCutoffHz ?? 1500,
        PllOutputOrder: new SstvDecoderSettings().PllOutputOrder ?? 3,
        PllOutputCutoffHz: new SstvDecoderSettings().PllOutputCutoffHz ?? 900,
        // Options stub backlog item 2 -- absent -> legacy's real CFQC constructor defaults
        // (Iir/3/900.0/2200.0, see SstvDecoderSettings.ZeroCrossingSmoothingMode's own doc comment).
        ZeroCrossingSmoothingMode: new SstvDecoderSettings().ZeroCrossingSmoothingMode ?? ZeroCrossingSmoothingMode.Iir,
        ZeroCrossingOutputOrder: new SstvDecoderSettings().ZeroCrossingOutputOrder ?? 3,
        ZeroCrossingOutputCutoffHz: new SstvDecoderSettings().ZeroCrossingOutputCutoffHz ?? 900,
        ZeroCrossingSmoothingFrequencyHz: new SstvDecoderSettings().ZeroCrossingSmoothingFrequencyHz ?? 2200,
        QrzLookupEnabled: new QrzLookupSettings().Enabled ?? false,
        QrzLookupUsername: new QrzLookupSettings().Username,
        QrzLookupPassword: new QrzLookupSettings().Password,
        CaptureChannelSource: new AudioDeviceSettings().CaptureChannelSource,
        StereoTxEnabled: new AudioDeviceSettings().StereoTxEnabled,
        CwIdMode: new StationIdSettings().CwIdMode,
        CwText: new StationIdSettings().CwText ?? StationIdSettings.DefaultCwText,
        CwWpm: new StationIdSettings().CwWpm ?? StationIdSettings.DefaultCwWpm,
        CwToneFrequencyHz: new StationIdSettings().CwToneFrequencyHz ?? StationIdSettings.DefaultCwToneFrequencyHz,
        FskIdTxEnabled: new StationIdSettings().FskIdTxEnabled,
        FskIdRxEnabled: new StationIdSettings().FskIdRxEnabled,
        NrRstEnabled: new StationIdSettings().NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled,
        NrRstText: new StationIdSettings().NrRstText,
        SoundFileMmvPath: new StationIdSettings().SoundFileMmvPath,
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
            TxSampleRateOffsetHz: audio.TxSampleRateOffsetHz,
            TxBpfEnabled: audio.TxBpfEnabled ?? true,
            TxBpfTapCount: audio.TxBpfTapCount ?? 24,
            TxLpfEnabled: audio.TxLpfEnabled ?? false,
            TxLpfFrequencyHz: audio.TxLpfFrequencyHz ?? 2000.0,
            RadioBackendId: radio.BackendId,
            // Not raw radio.Host/Port -- a settings.json that never had a rigctld section (e.g. the
            // operator only ever used Hamlib/flrig before, then switches the backend picker to
            // rigctld in the dialog) deserializes these as null, same reasoning FlrigHost/Port below
            // already apply. RigctldHost specifically checks IsNullOrWhiteSpace, not just "?? null"
            // (code-review finding, 2026-09-01) -- an operator who clears the TextBox and it persists
            // "" rather than null hits the identical blank-field symptom the fallback exists to fix;
            // an empty host is never a legitimate value here regardless (ToConnectionSpec's own
            // `{ Length: > 0 }` guard already rejects it), so there is no real user intent to
            // preserve by leaving a blank string alone.
            RigctldHost: string.IsNullOrWhiteSpace(radio.Host) ? RadioConnectionSettings.RigctldHostFallback : radio.Host,
            RigctldPort: radio.Port ?? RadioConnectionSettings.RigctldPortFallback,
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
            DefaultRst: operatorSettings.DefaultRst ?? OperatorSettings.DefaultRstFallback,
            AutoSyncEnabled: decoder.AutoSyncEnabled ?? true,
            AutoSlantEnabled: decoder.AutoSlantEnabled ?? true,
            AutoStopEnabled: decoder.AutoStopEnabled ?? false,
            SyncRestartEnabled: decoder.SyncRestartEnabled ?? true,
            SenseLevel: decoder.SenseLevel ?? 1,
            DemodType: decoder.DemodType ?? DemodType.Hilbert,
            RxBpfPreset: decoder.RxBpfPreset ?? RxBpfPreset.Wide,
            RxBufferMode: decoder.RxBufferMode ?? RxBufferMode.On,
            PllVcoGain: decoder.PllVcoGain ?? 1.0,
            PllLoopOrder: decoder.PllLoopOrder ?? 1,
            PllLoopCutoffHz: decoder.PllLoopCutoffHz ?? 1500,
            PllOutputOrder: decoder.PllOutputOrder ?? 3,
            PllOutputCutoffHz: decoder.PllOutputCutoffHz ?? 900,
            ZeroCrossingSmoothingMode: decoder.ZeroCrossingSmoothingMode ?? ZeroCrossingSmoothingMode.Iir,
            ZeroCrossingOutputOrder: decoder.ZeroCrossingOutputOrder ?? 3,
            ZeroCrossingOutputCutoffHz: decoder.ZeroCrossingOutputCutoffHz ?? 900,
            ZeroCrossingSmoothingFrequencyHz: decoder.ZeroCrossingSmoothingFrequencyHz ?? 2200,
            QrzLookupEnabled: qrzLookup.Enabled ?? false,
            QrzLookupUsername: qrzLookup.Username,
            QrzLookupPassword: qrzLookup.Password,
            CaptureChannelSource: audio.CaptureChannelSource,
            StereoTxEnabled: audio.StereoTxEnabled,
            CwIdMode: stationId.CwIdMode,
            CwText: stationId.CwText ?? StationIdSettings.DefaultCwText,
            CwWpm: stationId.CwWpm ?? StationIdSettings.DefaultCwWpm,
            CwToneFrequencyHz: stationId.CwToneFrequencyHz ?? StationIdSettings.DefaultCwToneFrequencyHz,
            FskIdTxEnabled: stationId.FskIdTxEnabled,
            FskIdRxEnabled: stationId.FskIdRxEnabled,
            NrRstEnabled: stationId.NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled,
            NrRstText: stationId.NrRstText,
            SoundFileMmvPath: stationId.SoundFileMmvPath,
            AdifUdpDestinations: adifUdp.Destinations ?? []);
    }

    public async Task SaveAsync(OptionsSnapshot snapshot, CancellationToken ct = default)
    {
        // T0-2: the whole load-modify-save sequence below is now one atomic ISettingsStore.UpdateAsync
        // call -- see this method's own history for WHY it must re-read fresh rather than reuse
        // _loadedSettings (auditor blocker finding, preserved verbatim in the mutate lambda below).
        // sampleRateToPersist escapes the lambda via this outer local (a lambda may legally write to
        // a captured outer variable) so the Log.Saved call after the atomic update reflects what was
        // actually persisted, not a stale re-derivation.
        var sampleRateToPersist = 0;
        var settings = await _settingsStore.UpdateAsync(currentSettings =>
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
        // Reading fresh from currentSettings (the exact snapshot UpdateAsync loaded under its own
        // lock, immediately before calling this lambda) closes the whole class, not just this one
        // field -- the same staleness risk applies to every section below, present or future,
        // written by anything other than this dialog's own Save.

        // Tier B audit finding: these three (Localization/Operator/QrzLookup) used to build a fresh
        // `new X { ... }` instead of `previous with { ... }` like every OTHER section here --
        // harmless today only because none of these three records currently has a field the dialog
        // doesn't own (confirmed field-by-field), but it's the exact sibling-inconsistency shape
        // this whole sweep keeps finding: the day a non-dialog field is added to any of these three
        // (a QRZ session-cache token, an operator default-power field, ...), every Options Save
        // would silently reset it, with no existing test able to catch it. Read previous* up front
        // and preserve via `with` uniformly, matching AfcEnabled/ClientId's own established pattern.
        var previousLocalization = currentSettings.GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings) ?? new LocalizationSettings();
        var previousOperator = currentSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings) ?? new OperatorSettings();
        var previousQrzLookup = currentSettings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();
        var previousAudio = currentSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
        sampleRateToPersist = SstvSampleRate.IsSupported(snapshot.SampleRate)
            ? snapshot.SampleRate
            : SstvSampleRate.NormalizePersisted(previousAudio.SampleRate);
        // Same "reject, preserve the prior valid value" shape as SampleRate above -- legacy's own
        // TxSampOffChange (Option.cpp:1142-1146) accepts a typed value only within +/-1500Hz and
        // otherwise silently keeps whatever m_TxSampOff already held, rather than clamping to the
        // boundary.
        var txSampleRateOffsetToPersist = snapshot.TxSampleRateOffsetHz is >= -1500.0 and <= 1500.0
            ? snapshot.TxSampleRateOffsetHz
            : previousAudio.TxSampleRateOffsetHz;
        // Options stub backlog item 3 -- same "reject, preserve the prior valid value" shape as
        // TxSampleRateOffsetHz above: legacy's own Save-handler validation for these 2 numeric TX
        // fields (Option.cpp:452-459) has no else branch either, i.e. an out-of-range typed value is
        // simply never applied, keeping whatever the field already held. Tap count additionally
        // rounds to the nearest even value (see AudioDeviceSettings.TxBpfEnabled's own doc comment).
        var txBpfTapCountToPersist = snapshot.TxBpfTapCount is >= 2 and <= 512
            ? (snapshot.TxBpfTapCount % 2 == 0 ? snapshot.TxBpfTapCount : snapshot.TxBpfTapCount - 1)
            : previousAudio.TxBpfTapCount ?? 24;
        var txLpfFrequencyToPersist = snapshot.TxLpfFrequencyHz is >= 100.0 and <= 3000.0
            ? snapshot.TxLpfFrequencyHz
            : previousAudio.TxLpfFrequencyHz ?? 2000.0;
        var previousRadio = currentSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();
        var previousDecoder = currentSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
        var previousStationId = currentSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings) ?? new StationIdSettings();
        // MigrateIfNeeded (not a plain GetSection ?? new X()) -- same reasoning as LoadAsync above:
        // a user who opens the dialog and immediately hits Save, with only a legacy GridTracker
        // section on disk, must persist the MIGRATED ClientId/destination, not silently drop it back
        // to an empty AdifUdpStreamingSettings just because the new section was never explicitly
        // read through this exact call before. Confirmed pure/synchronous -- safe inside mutate.
        var previousAdifUdp = AdifUdpStreamingSettings.MigrateIfNeeded(currentSettings);

        return currentSettings
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
                    TxSampleRateOffsetHz = txSampleRateOffsetToPersist,
                    TxBpfEnabled = snapshot.TxBpfEnabled,
                    TxBpfTapCount = txBpfTapCountToPersist,
                    TxLpfEnabled = snapshot.TxLpfEnabled,
                    TxLpfFrequencyHz = txLpfFrequencyToPersist,
                    CaptureChannelSource = snapshot.CaptureChannelSource,
                    StereoTxEnabled = snapshot.StereoTxEnabled,
                },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)
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
                // "?? string.Empty", not the raw snapshot value: a cleared TextBox must persist as an
                // explicit empty string, not null -- null round-trips back through LoadAsync's
                // "?? DefaultRstFallback" fallback as "595" again, silently undoing the user's clear
                // on next load/save (same CwText/DefaultCwText trap, see OperatorSettings.DefaultRst's
                // own doc comment).
                previousOperator with { Callsign = snapshot.Callsign, Name = snapshot.OperatorName, Grid = snapshot.OperatorGrid, DefaultRst = snapshot.DefaultRst ?? string.Empty },
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
                    // Options stub backlog item 1, wired 2026-08-28 -- same fallback shape as
                    // DemodType/RxBpfPreset/RxBufferMode above.
                    PllVcoGain = snapshot.PllVcoGain,
                    PllLoopOrder = snapshot.PllLoopOrder,
                    PllLoopCutoffHz = snapshot.PllLoopCutoffHz,
                    PllOutputOrder = snapshot.PllOutputOrder,
                    PllOutputCutoffHz = snapshot.PllOutputCutoffHz,
                    // Options stub backlog item 2 -- same fallback shape as PLL above.
                    ZeroCrossingSmoothingMode = snapshot.ZeroCrossingSmoothingMode,
                    ZeroCrossingOutputOrder = snapshot.ZeroCrossingOutputOrder,
                    ZeroCrossingOutputCutoffHz = snapshot.ZeroCrossingOutputCutoffHz,
                    ZeroCrossingSmoothingFrequencyHz = snapshot.ZeroCrossingSmoothingFrequencyHz,
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
                    // Same "no ?? string.Empty guard needed" reasoning as NrRstText above --
                    // SoundFileMmvPath has no first-run pre-fill default to accidentally resurrect
                    // (StationIdSettings.SoundFileMmvPath's own doc comment).
                    SoundFileMmvPath = snapshot.SoundFileMmvPath,
                },
                StationIdSettingsJsonContext.Default.StationIdSettings)
            .WithSection(
                AdifUdpStreamingSettings.SectionKey,
                // ClientId is preserved as-is -- same reasoning as AfcEnabled above, this dialog
                // has no control for it (see OptionsSnapshot's own doc comment).
                previousAdifUdp with { Destinations = snapshot.AdifUdpDestinations },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings);
        }, ct).ConfigureAwait(false);

        _loadedSettings = settings;
        Log.Saved(_logger, snapshot.RadioBackendId, sampleRateToPersist, snapshot.CultureCode);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Options saved: radioBackend={RadioBackendId}, sampleRate={SampleRate}, culture={CultureCode}")]
        public static partial void Saved(ILogger logger, string radioBackendId, int sampleRate, string? cultureCode);
    }
}
