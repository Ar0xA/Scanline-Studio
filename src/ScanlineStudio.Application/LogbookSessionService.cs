using System.Text;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Settings;
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
    private readonly IReceiveHistoryStore _receiveHistoryStore;
    private readonly QrzCredentialService _qrzCredentials;
    private readonly ILogger<LogbookSessionService> _logger;

    public LogbookSessionService(
        ILogbookRepository repository,
        IAdifExporter adifExporter,
        IAdifImporter adifImporter,
        IAdifUdpStreamer adifUdpStreamer,
        IQrzLogbookUploader qrzUploader,
        IQrzCallsignLookup qrzLookup,
        ISettingsStore settingsStore,
        IReceiveHistoryStore receiveHistoryStore,
        QrzCredentialService qrzCredentials,
        ILogger<LogbookSessionService> logger)
    {
        _repository = repository;
        _adifExporter = adifExporter;
        _adifImporter = adifImporter;
        _adifUdpStreamer = adifUdpStreamer;
        _qrzUploader = qrzUploader;
        _qrzLookup = qrzLookup;
        _settingsStore = settingsStore;
        _receiveHistoryStore = receiveHistoryStore;
        _qrzCredentials = qrzCredentials;
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
            //
            // T1-7 (production_audit.md): the caller-visible result used to discard this failure
            // reason entirely, even though it was already logged server-side -- the caller had no
            // way to surface WHY the post-persist steps failed, only that they did.
            //
            // Auditor code-review finding (2026-08-31): a first-pass fix put ex.Message in QrzError
            // -- wrong, since this catch covers settings/export/ADIF-UDP failures too, not just QRZ,
            // and QrzError is rendered through a QRZ-specific "QRZ: failed (...)" locale string
            // regardless of the real cause. Use PostPersistError instead -- see LogQsoResult's own
            // doc comment.
            Log.PostPersistStepFailed(_logger, persisted.Id, ex);
            return new LogQsoResult(persisted, 0, 0, false, QrzError: null, PostPersistError: ex.Message);
        }
    }

    public Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default) => _repository.SearchAsync(query, ct);

    public Task<QsoRecord?> GetQsoByIdAsync(string id, CancellationToken ct = default) => _repository.GetByIdAsync(id, ct);

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

        var temporaryPath = filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            await using (var writer = new StreamWriter(stream))
            {
                _adifExporter.Export(records, writer, stationCallsign);
            }

            // StreamWriter disposal can fail while flushing. Publish only after it succeeds.
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
            Log.AdifExported(_logger, filePath, records.Count);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.AdifTemporaryFileCleanupFailed(_logger, temporaryPath, ex);
            }
        }
    }

    public async Task<IReadOnlyList<QsoRecord>> ImportAdifFileAsync(string filePath, CancellationToken ct = default)
    {
        using var reader = new StreamReader(filePath, DetectAdifFallbackEncoding(filePath), detectEncodingFromByteOrderMarks: true);
        // T1-17: StreamReader.CurrentEncoding only becomes accurate once BOM detection has actually
        // run, which happens lazily on the first real read -- Peek() forces that without consuming
        // any characters. AdifImporter's own byte-count field-length slicing must use this SAME
        // encoding, or it misaligns for non-ASCII content (see IAdifImporter.Import's own doc
        // comment).
        reader.Peek();
        var parsed = _adifImporter.Import(reader, reader.CurrentEncoding);

        var imported = new List<QsoRecord>(parsed.Count);
        foreach (var record in parsed)
        {
            imported.Add(await _repository.AddAsync(record, ct).ConfigureAwait(false));
        }

        Log.AdifImported(_logger, filePath, imported.Count);
        return imported;
    }

    /// <summary>T1-17 (production_audit.md): <see cref="StreamReader"/>'s own BOM detection
    /// (<c>detectEncodingFromByteOrderMarks: true</c> above) correctly picks UTF-8/UTF-16/UTF-32
    /// from a real BOM -- the gap is the no-BOM case, where it silently falls back to this
    /// constructor's given encoding regardless of what the file actually is. A non-UTF-8 ADIF file
    /// (e.g. Windows-1252, common from other ham-logging software) with no BOM used to get silently
    /// mis-decoded as UTF-8, corrupting any accented callsign/QTH/name field before
    /// <see cref="ScanlineStudio.Core.Logbook.AdifImporter"/> ever saw it. A strict UTF-8 decode
    /// attempt (throwing on the first invalid byte sequence, NOT <see cref="Encoding.UTF8"/>'s own
    /// lossy replacement-character fallback) distinguishes "genuinely UTF-8" from "some other 8-bit
    /// encoding" -- Windows-1252 is the fallback because it's a superset of ASCII that can decode
    /// ANY byte sequence without failure, the standard heuristic for this exact scenario. Needs
    /// T1-18's <c>CodePagesEncodingProvider</c> registration to resolve "Windows-1252" by name.</summary>
    private static Encoding DetectAdifFallbackEncoding(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252);
        }
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

            if (!IsQrzLookupEnabledWithUsername(qrzLookupSettings))
            {
                return new QrzCallsignLookupResult(false, null, null, null, "QRZ lookup is not configured in Options.");
            }

            // User-started lookup: the only non-Options path allowed to show a keyring unlock prompt.
            var password = await _qrzCredentials.ReadAsync(allowPrompt: true, ct).ConfigureAwait(false);
            return password.Status switch
            {
                CredentialReadStatus.Found when !string.IsNullOrEmpty(password.Secret) =>
                    await _qrzLookup.LookupAsync(callsign, qrzLookupSettings.Username!, password.Secret, ct).ConfigureAwait(false),
                CredentialReadStatus.Unavailable =>
                    new QrzCallsignLookupResult(false, null, null, null, "The QRZ password is in the system keyring, which is locked or unavailable."),
                _ => new QrzCallsignLookupResult(false, null, null, null, "QRZ lookup is not configured in Options."),
            };
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
            // IsPresentAsync never prompts: this runs from a background UI gate, not a user action.
            return IsQrzLookupEnabledWithUsername(qrzLookupSettings) && await _qrzCredentials.IsPresentAsync(ct).ConfigureAwait(false);
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
    /// <see cref="IsQrzLookupConfiguredAsync"/> -- one source of truth for the settings.json half of
    /// "configured"; the password half is <see cref="QrzCredentialService"/>'s.</summary>
    private static bool IsQrzLookupEnabledWithUsername(QrzLookupSettings settings) =>
        settings.Enabled == true && !string.IsNullOrEmpty(settings.Username);

    public Task<QrzLoginResult> TestQrzLookupCredentialsAsync(string username, string password, CancellationToken ct = default) =>
        _qrzLookup.TestCredentialsAsync(username, password, ct);

    public async Task<bool> DeleteQsoAsync(string id, CancellationToken ct = default)
    {
        // Best-effort, clear-then-delete (not delete-then-clear): if the process dies between these
        // two calls, the worst case is an unlinked-but-still-existing QSO (recoverable via "Open in
        // log"), not a permanently dangling ReceiveHistory.LinkedQsoId pointing at a QSO that no
        // longer exists (which the Gallery would then report as "Logged" forever, with no way for
        // the operator to find and re-log that frame). A failure here is logged, not thrown -- it
        // must never block the QSO delete itself, which is the operator's actual request.
        try
        {
            await _receiveHistoryStore.ClearLinkedQsoIdAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ClearLinkedQsoIdFailed(_logger, id, ex);
        }

        // SqliteLogbookRepository.DeleteAsync already logs the raw DB fact -- no second log line
        // needed here, same "repository logs the DB op, this service only logs its OWN composed
        // outcome" split LogQsoAsync's own QsoAdded/QsoLogged pair already establishes (that pair
        // differs in content -- ADIF-UDP/QRZ results -- which this one-step delete has nothing
        // equivalent to add).
        return await _repository.DeleteAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<QsoRecord?> FindLikelyDuplicateAsync(string callsign, DateTimeOffset startUtc, long? frequencyHz, string? excludeId, CancellationToken ct = default)
    {
        try
        {
            // Same-UTC-day window (round-1 plan-review blocker fix): unbounded callsign+band
            // matching flagged a regular sked partner worked six months ago as a "duplicate" on
            // every single contact -- the operator would learn to click through the warning, and it
            // stops being a warning. From/To reuse LogbookQuery's existing support (already honored
            // by SqliteLogbookRepository.SearchAsync), so this pushes the date filter into the query
            // instead of pulling the whole callsign history client-side.
            var dayStart = new DateTimeOffset(startUtc.UtcDateTime.Date, TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1).AddTicks(-1);
            var candidates = await _repository.SearchAsync(new LogbookQuery(callsign, dayStart, dayEnd), ct).ConfigureAwait(false);

            // Band must also match -- "no band" (null) only matches another "no band" record, never
            // treated as a wildcard (two QSOs with genuinely unknown frequencies aren't thereby "the
            // same band" just because neither has one).
            var band = AmateurBandLookup.BandFor(frequencyHz);
            return candidates.FirstOrDefault(c => c.Id != excludeId && AmateurBandLookup.BandFor(c.FrequencyHz) == band);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail OPEN (see this method's own interface doc comment): a transient DB hiccup must
            // never block logging or saving a real QSO over a duplicate check that couldn't run.
            Log.FindLikelyDuplicateFailed(_logger, callsign, ex);
            return null;
        }
    }

    public async Task<WorkedBeforeLookup> GetWorkedBeforeAsync(string callsign, CancellationToken ct = default)
    {
        try
        {
            var candidates = await _repository.SearchAsync(new LogbookQuery(callsign), ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return new WorkedBeforeLookup(WorkedBeforeOutcome.NotFound, null);
            }

            // Lexical, not chronological -- SqliteLogbookRepository's own ORDER BY sorts the
            // DateTimeOffset-as-"O"-format TEXT column, which can misorder rows logged at different
            // UTC offsets. MaxBy on the real DateTimeOffset value is required, not candidates[0].
            var latest = candidates.MaxBy(c => c.StartUtc)!;
            var info = new WorkedBeforeInfo(candidates.Count, latest.StartUtc, AmateurBandLookup.BandFor(latest.FrequencyHz));
            return new WorkedBeforeLookup(WorkedBeforeOutcome.Found, info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately NOT fail-open-to-"none found" (see this method's own interface doc
            // comment) -- unlike FindLikelyDuplicateAsync, where "couldn't check" and "confirmed no
            // duplicate" have the same safe consequence (let the QSO log), this is a dupe-avoidance
            // display: silently rendering a DB failure as "New station" would be actively wrong.
            Log.GetWorkedBeforeFailed(_logger, callsign, ex);
            return new WorkedBeforeLookup(WorkedBeforeOutcome.Failed, null);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove temporary ADIF export {Path}")]
        public static partial void AdifTemporaryFileCleanupFailed(ILogger logger, string path, Exception ex);

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clear LinkedQsoId for QSO {Id} before delete; proceeding with the delete anyway")]
        public static partial void ClearLinkedQsoIdFailed(ILogger logger, string id, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "FindLikelyDuplicateAsync({Callsign}) failed; treating as no duplicate found")]
        public static partial void FindLikelyDuplicateFailed(ILogger logger, string callsign, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "GetWorkedBeforeAsync({Callsign}) failed; reporting the check as unavailable")]
        public static partial void GetWorkedBeforeFailed(ILogger logger, string callsign, Exception exception);
    }
}
