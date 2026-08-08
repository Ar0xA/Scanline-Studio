using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>See <see cref="ILogbookSessionService"/>. Composes <c>ScanlineStudio.Core.Logbook</c>'s
/// four building blocks (repository, ADIF exporter/importer, GridTracker streamer, QRZ uploader) —
/// none of which know about each other or about settings sections outside their own. This is the
/// one place that does: reads <see cref="OperatorSettings"/> for the ADIF <c>STATION_CALLSIGN</c>
/// and <see cref="QrzUploadSettings"/> for the enabled/API-key gate (unlike GridTracker streaming,
/// which <c>GridTrackerStreamer</c> gates internally against its own settings section —
/// <see cref="IQrzLogbookUploader"/> takes the API key as an explicit per-call parameter instead,
/// so there is no symmetric internal gate to rely on there).</summary>
public sealed partial class LogbookSessionService : ILogbookSessionService
{
    private readonly ILogbookRepository _repository;
    private readonly IAdifExporter _adifExporter;
    private readonly IAdifImporter _adifImporter;
    private readonly IGridTrackerStreamer _gridTrackerStreamer;
    private readonly IQrzLogbookUploader _qrzUploader;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<LogbookSessionService> _logger;

    public LogbookSessionService(
        ILogbookRepository repository,
        IAdifExporter adifExporter,
        IAdifImporter adifImporter,
        IGridTrackerStreamer gridTrackerStreamer,
        IQrzLogbookUploader qrzUploader,
        ISettingsStore settingsStore,
        ILogger<LogbookSessionService> logger)
    {
        _repository = repository;
        _adifExporter = adifExporter;
        _adifImporter = adifImporter;
        _gridTrackerStreamer = gridTrackerStreamer;
        _qrzUploader = qrzUploader;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<LogQsoResult> LogQsoAsync(QsoRecord record, CancellationToken ct = default)
    {
        // Persistence is unconditional and happens first -- everything after this line is
        // best-effort and must never undo or block on it.
        var persisted = await _repository.AddAsync(record, ct).ConfigureAwait(false);

        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var stationCallsign = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)?.Callsign;

        var writer = new StringWriter();
        _adifExporter.Export([persisted], writer, stationCallsign);
        var adifText = writer.ToString();

        var gridTrackerSent = await _gridTrackerStreamer.SendLoggedQsoAsync(adifText, ct).ConfigureAwait(false);

        var qrzUploaded = false;
        string? qrzError = null;
        var qrzSettings = appSettings.GetSection(QrzUploadSettings.SectionKey, QrzUploadSettingsJsonContext.Default.QrzUploadSettings) ?? new QrzUploadSettings();
        if (qrzSettings.Enabled == true && !string.IsNullOrEmpty(qrzSettings.ApiKey))
        {
            var qrzResult = await _qrzUploader.UploadAsync(adifText, qrzSettings.ApiKey, ct).ConfigureAwait(false);
            qrzUploaded = qrzResult.Success;
            qrzError = qrzResult.ErrorReason;
        }

        Log.QsoLogged(_logger, persisted.Id, gridTrackerSent, qrzUploaded);
        return new LogQsoResult(persisted, gridTrackerSent, qrzUploaded, null, qrzError);
    }

    public Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default) => _repository.SearchAsync(query, ct);

    /// <summary>Edits an already-logged QSO in place -- thin delegation to
    /// <see cref="ILogbookRepository.UpdateAsync"/> (already fully implemented). Deliberately does
    /// NOT re-push to GridTracker or QRZ the way <see cref="LogQsoAsync"/> does: <see cref="_qrzUploader"/>'s
    /// real QRZ Logbook API call is INSERT-only (see <see cref="IQrzLogbookUploader"/>'s own doc
    /// comment) -- a re-push here would file as a SECOND, duplicate contact at QRZ's end, not an
    /// update; real re-push support would need QRZ's own OPTION=REPLACE/LOGID tracking, which
    /// doesn't exist anywhere in this codebase. <see cref="_gridTrackerStreamer"/>'s own
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "QSO logged: {Id} (GridTracker sent={GridTrackerSent}, QRZ uploaded={QrzUploaded})")]
        public static partial void QsoLogged(ILogger logger, string id, bool gridTrackerSent, bool qrzUploaded);

        [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Count} QSO(s) to {FilePath}")]
        public static partial void AdifExported(ILogger logger, string filePath, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Count} QSO(s) from {FilePath}")]
        public static partial void AdifImported(ILogger logger, string filePath, int count);
    }
}
