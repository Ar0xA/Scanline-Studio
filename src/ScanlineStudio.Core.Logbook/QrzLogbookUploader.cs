using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Core.Logbook;

/// <summary>See <see cref="IQrzLogbookUploader"/>. Wire contract verified directly against QRZ's
/// own <a href="https://www.qrz.com/docs/logbook/QRZLogbookAPI.html">Logbook API Developer
/// Guide</a>: <c>POST https://logbook.qrz.com/api</c>, url-encoded form body
/// <c>KEY=...&amp;ACTION=INSERT&amp;ADIF=...</c>; response is the same <c>name=value&amp;...</c>
/// format (<c>RESULT=OK|FAIL|REPLACE</c>, <c>LOGID</c>, <c>COUNT</c>, <c>REASON</c> on failure).
/// Only <c>INSERT</c> is implemented (the only action this feature needs) — <c>STATUS</c> and
/// other actions are out of scope. First <c>IHttpClientFactory</c> consumer in this codebase; no
/// prior repo convention to match.</summary>
public sealed partial class QrzLogbookUploader : IQrzLogbookUploader
{
    private const string Endpoint = "https://logbook.qrz.com/api";
    private const string HttpClientName = "QrzLogbookApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QrzLogbookUploader> _logger;

    public QrzLogbookUploader(IHttpClientFactory httpClientFactory, ILogger<QrzLogbookUploader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Best-effort per <see cref="IQrzLogbookUploader"/>'s implicit contract (mirrors
    /// <c>AdifUdpStreamer.SendLoggedQsoAsync</c>'s own documented one, after that class's
    /// auditor pass found the same gap): catches every realistic failure mode (network failure,
    /// an unreadable/malformed response body) and reports it via <see cref="QrzUploadResult"/>
    /// rather than throwing, so a QRZ outage or misconfiguration never blocks the caller from
    /// persisting the QSO locally. Deliberately does not catch <see cref="OperationCanceledException"/>.</summary>
    public async Task<QrzUploadResult> UploadAsync(string adifText, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["KEY"] = apiKey,
                ["ACTION"] = "INSERT",
                ["ADIF"] = adifText,
            });

            var response = await client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var fields = ParseNameValueResponse(body);

            if (!fields.TryGetValue("RESULT", out var result))
            {
                Log.UploadMalformedResponse(_logger, body);
                return new QrzUploadResult(false, null, "Malformed response from QRZ (no RESULT field).");
            }

            if (result.Equals("OK", StringComparison.OrdinalIgnoreCase) || result.Equals("REPLACE", StringComparison.OrdinalIgnoreCase))
            {
                var logId = fields.GetValueOrDefault("LOGID");
                Log.UploadSucceeded(_logger, logId);
                return new QrzUploadResult(true, logId, null);
            }

            var reason = fields.GetValueOrDefault("REASON") ?? "QRZ rejected the upload with no REASON given.";
            Log.UploadRejected(_logger, reason);
            return new QrzUploadResult(false, null, reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.UploadRequestFailed(_logger, ex);
            return new QrzUploadResult(false, null, ex.Message);
        }
    }

    private static Dictionary<string, string> ParseNameValueResponse(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                // '+' as a space is the application/x-www-form-urlencoded convention (distinct
                // from plain URI percent-encoding, which Uri.UnescapeDataString alone does not
                // treat '+' as space for) -- QRZ's response uses the same encoding as its request
                // body, so decode both the same way.
                fields[UnescapeFormValue(parts[0])] = UnescapeFormValue(parts[1]);
            }
        }

        return fields;
    }

    private static string UnescapeFormValue(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "QSO uploaded to QRZ.com logbook, LOGID={LogId}")]
        public static partial void UploadSucceeded(ILogger logger, string? logId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ.com rejected the logbook upload: {Reason}")]
        public static partial void UploadRejected(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ.com logbook upload request failed")]
        public static partial void UploadRequestFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ.com logbook upload returned an unparseable response: {Body}")]
        public static partial void UploadMalformedResponse(ILogger logger, string body);
    }
}
