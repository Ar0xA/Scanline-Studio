using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>See <see cref="ILogbookSessionService"/>. Composes <c>ScanlineStudio.Core.Logbook</c>'s
/// building blocks (repository, ADIF exporter/importer, ADIF-UDP streamer, QRZ upload, QRZ
/// lookup) — none of which know about each other or about settings sections outside their own.
/// This is the one place that does: reads <see cref="OperatorSettings"/> for the ADIF
/// <c>STATION_CALLSIGN</c>, <see cref="QrzUploadSettings"/> for the upload enabled/API-key gate,
/// and <see cref="QrzLookupSettings"/> for the lookup enabled/username/password gate (unlike
/// ADIF-UDP streaming, which <c>AdifUdpStreamer</c> gates internally against its own settings
/// section, per-destination — <see cref="IQrzLogbookUploader"/>/<see cref="IQrzCallsignLookup"/>
/// both take credentials as an explicit per-call parameter instead, so there is no symmetric
/// internal gate to rely on there).</summary>
public sealed partial class LogbookSessionService : ILogbookSessionService
{
    private readonly ILogbookRepository _repository;
    private readonly IAdifExporter _adifExporter;
    private readonly IAdifImporter _adifImporter;
    private readonly IAdifUdpStreamer _adifUdpStreamer;
    private readonly IQrzLogbookUploader _qrzUploader;
    private readonly IQrzCallsignLookup _qrzLookup;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<LogbookSessionService> _logger;

    public LogbookSessionService(
        ILogbookRepository repository,
        IAdifExporter adifExporter,
        IAdifImporter adifImporter,
        IAdifUdpStreamer adifUdpStreamer,
        IQrzLogbookUploader qrzUploader,
        IQrzCallsignLookup qrzLookup,
        ISettingsStore settingsStore,
        ILogger<LogbookSessionService> logger)
    {
        _repository = repository;
        _adifExporter = adifExporter;
        _adifImporter = adifImporter;
        _adifUdpStreamer = adifUdpStreamer;
        _qrzUploader = qrzUploader;
        _qrzLookup = qrzLookup;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<LogQsoResult> LogQsoAsync(QsoRecord record, CancellationToken ct = default)
    {
        // Persistence is unconditional and happens first -- everything after this line is
        // best-effort and must never undo or block on it.
        var persisted = await _repository.AddAsync(record, ct).ConfigureAwait(false);

        // Round-1 code-review finding (Tier A Batch 10 chunk 10c, real bug fixed): this doc comment's
        // own "must never ... block on it" claim wasn't actually enforced -- everything below used to
        // run unguarded, so a throw here (settings load, ADIF export, or ADIF-UDP send -- e.g. a
        // permissions error reading settings.json) propagated OUT of LogQsoAsync AFTER the record was
        // already committed. Both real UI callers (LogbookPaneViewModel/QsoLinkWindowViewModel) treat
        // any thrown exception here as "logging failed" and re-enable their own retry affordance --
        // a user retrying then creates a genuine DUPLICATE QSO record, since the first attempt's
        // persistence already succeeded. QRZ upload itself was already exception-safe
        // (QrzLogbookUploader.UploadAsync catches everything but cancellation) -- only this settings/
        // export/ADIF-UDP span needed the same treatment.
        try
        {
            var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var stationCallsign = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)?.Callsign;

            var writer = new StringWriter();
            _adifExporter.Export([persisted], writer, stationCallsign);
            var adifText = writer.ToString();

            var adifUdpResult = await _adifUdpStreamer.SendLoggedQsoAsync(adifText, ct).ConfigureAwait(false);

            var qrzUploaded = false;
            string? qrzError = null;
            var qrzSettings = appSettings.GetSection(QrzUploadSettings.SectionKey, QrzUploadSettingsJsonContext.Default.QrzUploadSettings) ?? new QrzUploadSettings();
            if (qrzSettings.Enabled == true && !string.IsNullOrEmpty(qrzSettings.ApiKey))
            {
                var qrzResult = await _qrzUploader.UploadAsync(adifText, qrzSettings.ApiKey, ct).ConfigureAwait(false);
                qrzUploaded = qrzResult.Success;
                qrzError = qrzResult.ErrorReason;
            }

            Log.QsoLogged(_logger, persisted.Id, adifUdpResult.SentCount, adifUdpResult.EnabledCount, qrzUploaded);
            return new LogQsoResult(persisted, adifUdpResult.SentCount, adifUdpResult.EnabledCount, qrzUploaded, qrzError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The QSO IS already logged (line 53 succeeded) -- report that honestly, with degraded
            // best-effort telemetry (nothing sent/uploaded), rather than throwing and making a caller
            // believe logging itself failed.
            Log.PostPersistStepFailed(_logger, persisted.Id, ex);
            return new LogQsoResult(persisted, 0, 0, false, null);
        }
    }

    public Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default) => _repository.SearchAsync(query, ct);

    /// <summary>Edits an already-logged QSO in place -- thin delegation to
    /// <see cref="ILogbookRepository.UpdateAsync"/> (already fully implemented). Deliberately does
    /// NOT re-push via ADIF-UDP or QRZ the way <see cref="LogQsoAsync"/> does: <see cref="_qrzUploader"/>'s
    /// real QRZ Logbook API call is INSERT-only (see <see cref="IQrzLogbookUploader"/>'s own doc
    /// comment) -- a re-push here would file as a SECOND, duplicate contact at QRZ's end, not an
    /// update; real re-push support would need QRZ's own OPTION=REPLACE/LOGID tracking, which
    /// doesn't exist anywhere in this codebase. <see cref="_adifUdpStreamer"/>'s own
    /// `SendLoggedQsoAsync` is an equally one-shot "QSO logged" UDP datagram with no update
    /// semantics either. A future re-push feature would need real work in both of those classes
    /// first, not just a call site change here.</summary>
    public Task UpdateQsoAsync(QsoRecord record, CancellationToken ct = default) => _repository.UpdateAsync(record, ct);

    public async Task ExportAdifFileAsync(string filePath, LogbookQuery query, CancellationToken ct = default)
    {
        var records = await _repository.SearchAsync(query, ct).ConfigureAwait(false);

        // Matches LogQsoAsync's own STATION_CALLSIGN lookup above -- this call site used to omit it
        // entirely (a real, pre-existing gap this method's own first real UI caller, the Logbook
        // pane's Export button, is what makes user-visible: an exported file missing
        // STATION_CALLSIGN doesn't import cleanly into LoTW/eQSL).
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var stationCallsign = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)?.Callsign;

        await using var writer = new StreamWriter(filePath);
        _adifExporter.Export(records, writer, stationCallsign);
        Log.AdifExported(_logger, filePath, records.Count);
    }

    public async Task<IReadOnlyList<QsoRecord>> ImportAdifFileAsync(string filePath, CancellationToken ct = default)
    {
        using var reader = new StreamReader(filePath);
        var parsed = _adifImporter.Import(reader);

        var imported = new List<QsoRecord>(parsed.Count);
        foreach (var record in parsed)
        {
            imported.Add(await _repository.AddAsync(record, ct).ConfigureAwait(false));
        }

        Log.AdifImported(_logger, filePath, imported.Count);
        return imported;
    }

    public async Task<QrzCallsignLookupResult> LookupCallsignAsync(string callsign, CancellationToken ct = default)
    {
        // Tier B audit finding: this method's own interface doc comment claims the same "never
        // throws, always returns a result" contract as LogQsoAsync -- but unlike LogQsoAsync's own
        // settings read (wrapped in a try/catch since Tier A Batch 10 chunk 10c, for the identical
        // reason), this one ran unguarded. A hand-edited settings.json with a malformed QrzLookup
        // section throws JsonException out of GetSection; an unreadable settings.json throws
        // UnauthorizedAccessException out of LoadAsync (not caught by JsonSettingsStore's own
        // whole-file-parse catch, which only covers IOException). Contained today by
        // RxImagePaneViewModel.LookupQrzAsync's own catch-all, but the contract violation is real.
        try
        {
            var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var qrzLookupSettings = appSettings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();

            if (!IsQrzLookupConfigured(qrzLookupSettings))
            {
                return new QrzCallsignLookupResult(false, null, null, null, "QRZ lookup is not configured in Options.");
            }

            return await _qrzLookup.LookupAsync(callsign, qrzLookupSettings.Username!, qrzLookupSettings.Password!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LookupCallsignSettingsReadFailed(_logger, callsign, ex);
            return new QrzCallsignLookupResult(false, null, null, null, ex.Message);
        }
    }

    public async Task<bool> IsQrzLookupConfiguredAsync(CancellationToken ct = default)
    {
        try
        {
            var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var qrzLookupSettings = appSettings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();
            return IsQrzLookupConfigured(qrzLookupSettings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same "never throws" contract as LookupCallsignAsync above -- a settings-read
            // failure resolves to "not configured", the same outcome a genuinely unconfigured
            // lookup would produce, rather than surfacing as a thrown exception to a UI gate.
            Log.IsQrzLookupConfiguredReadFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>Shared by <see cref="LookupCallsignAsync"/> and
    /// <see cref="IsQrzLookupConfiguredAsync"/> -- one source of truth for what "configured" means,
    /// so the button-gating check and the actual lookup's own gate can never drift apart.</summary>
    private static bool IsQrzLookupConfigured(QrzLookupSettings settings) =>
        settings.Enabled == true && !string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(settings.Password);

    public Task<QrzLoginResult> TestQrzLookupCredentialsAsync(string username, string password, CancellationToken ct = default) =>
        _qrzLookup.TestCredentialsAsync(username, password, ct);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "QSO logged: {Id} (ADIF-UDP sent={AdifUdpSentCount}/{AdifUdpEnabledCount}, QRZ uploaded={QrzUploaded})")]
        public static partial void QsoLogged(ILogger logger, string id, int adifUdpSentCount, int adifUdpEnabledCount, bool qrzUploaded);

        [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Count} QSO(s) to {FilePath}")]
        public static partial void AdifExported(ILogger logger, string filePath, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Count} QSO(s) from {FilePath}")]
        public static partial void AdifImported(ILogger logger, string filePath, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QSO {Id} was persisted, but the ADIF-UDP/QRZ best-effort steps afterward failed")]
        public static partial void PostPersistStepFailed(ILogger logger, string id, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LookupCallsignAsync({Callsign}) failed reading QRZ lookup settings")]
        public static partial void LookupCallsignSettingsReadFailed(ILogger logger, string callsign, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "IsQrzLookupConfiguredAsync failed reading QRZ lookup settings")]
        public static partial void IsQrzLookupConfiguredReadFailed(ILogger logger, Exception exception);
    }
}
