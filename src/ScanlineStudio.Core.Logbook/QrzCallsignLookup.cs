using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Core.Logbook;

/// <summary>See <see cref="IQrzCallsignLookup"/>. Wire contract verified directly against QRZ's own
/// <a href="https://www.qrz.com/docs/xml/current_spec.html">XML Interface Specification</a> (not
/// legacy's naive substring parsing, which this deliberately does NOT replicate -- legacy
/// `qrzcom.cpp`/`Main.cpp` search for literal `&lt;fname&gt;`/`&lt;/fname&gt;` etc. in the raw
/// response text; this uses a real XML parser against the API's actual default namespace,
/// `http://xmldata.qrz.com`, which every response element is qualified with).
///
/// <b>Session cache</b>: a single cached `(credential fingerprint, session key)` pair, guarded by
/// <see cref="_sessionLock"/>. The fingerprint is a SHA-256 hash of the username+password pair --
/// keyed on the CREDENTIAL PAIR, not just username, so a password change under the same username is
/// detected immediately rather than silently reusing a session opened under the stale password
/// (QRZ XML sessions can remain server-side valid for hours). The plaintext password itself is
/// never retained past the login call that used it -- only the one-way hash + the opaque session
/// token are cached, preserving <see cref="IQrzCallsignLookup"/>'s own "credentials as an explicit
/// per-call parameter, never stored inside the service" contract.
///
/// <see cref="TestCredentialsAsync"/> deliberately never reads or writes this cache -- see its own
/// doc comment.</summary>
public sealed partial class QrzCallsignLookup : IQrzCallsignLookup, IDisposable
{
    private const string Endpoint = "https://xmldata.qrz.com/xml/current/";
    private const string HttpClientName = "QrzXmlLookup";
    private const string Agent = "ScanlineStudio1.0";

    private static readonly XNamespace QrzNamespace = "http://xmldata.qrz.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QrzCallsignLookup> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private (string Fingerprint, string SessionKey)? _cachedSession;

    public QrzCallsignLookup(IHttpClientFactory httpClientFactory, ILogger<QrzCallsignLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void Dispose() => _sessionLock.Dispose();

    public async Task<QrzLoginResult> TestCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            var (key, error) = await LoginAsync(username, password, ct).ConfigureAwait(false);
            if (key is null)
            {
                Log.TestFailed(_logger, error);
                return new QrzLoginResult(false, error ?? "Login failed with no reason given.");
            }

            Log.TestSucceeded(_logger);
            return new QrzLoginResult(true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the linked HttpClient.Timeout could have fired here, since a genuine caller
            // cancellation (ct itself) would have set ct.IsCancellationRequested.
            Log.TestTimedOut(_logger);
            return new QrzLoginResult(false, "The request to QRZ.com timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.TestRequestFailed(_logger, ex);
            return new QrzLoginResult(false, ex.Message);
        }
    }

    public async Task<QrzCallsignLookupResult> LookupAsync(string callsign, string username, string password, CancellationToken ct = default)
    {
        try
        {
            var fingerprint = ComputeFingerprint(username, password);

            var (sessionKey, loginError) = await GetOrRefreshSessionKeyAsync(fingerprint, username, password, ct).ConfigureAwait(false);
            if (sessionKey is null)
            {
                // GetOrRefreshSessionKeyAsync already logged the specific reason.
                return new QrzCallsignLookupResult(false, null, null, null, loginError ?? "Login failed with no reason given.");
            }

            var outcome = await LookupWithSessionAsync(callsign, sessionKey, ct).ConfigureAwait(false);
            if (!outcome.SessionExpired)
            {
                return outcome.ToResult();
            }

            // Session expired mid-use (a real possibility even right after a successful login on a
            // slow connection) -- fresh login, no cache read this time since we already know it's
            // stale, retry the lookup exactly once more before giving up.
            var (freshKey, retryLoginError) = await ForceLoginAndCacheAsync(fingerprint, username, password, ct).ConfigureAwait(false);
            if (freshKey is null)
            {
                return new QrzCallsignLookupResult(false, null, null, null, retryLoginError ?? "Login failed with no reason given.");
            }

            var retryOutcome = await LookupWithSessionAsync(callsign, freshKey, ct).ConfigureAwait(false);
            return retryOutcome.ToResult();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the linked HttpClient.Timeout could have fired here, since a genuine caller
            // cancellation (ct itself) would have set ct.IsCancellationRequested.
            Log.LookupTimedOut(_logger);
            return new QrzCallsignLookupResult(false, null, null, null, "The request to QRZ.com timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.LookupRequestFailed(_logger, ex);
            return new QrzCallsignLookupResult(false, null, null, null, ex.Message);
        }
    }

    private Task<(string? SessionKey, string? Error)> GetOrRefreshSessionKeyAsync(string fingerprint, string username, string password, CancellationToken ct) =>
        WithSessionLockAsync(fingerprint, username, password, forceLogin: false, ct);

    private Task<(string? SessionKey, string? Error)> ForceLoginAndCacheAsync(string fingerprint, string username, string password, CancellationToken ct) =>
        WithSessionLockAsync(fingerprint, username, password, forceLogin: true, ct);

    private async Task<(string? SessionKey, string? Error)> WithSessionLockAsync(string fingerprint, string username, string password, bool forceLogin, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceLogin && _cachedSession is { } cached && cached.Fingerprint == fingerprint)
            {
                return (cached.SessionKey, null);
            }

            // No cache hit (or a forced re-login after a mid-use expiry) -- stay holding the lock
            // through the login itself (not just the cache read/write around it) so two concurrent
            // callers with different/no cached credentials can't both race a login and let the
            // loser silently overwrite the winner's cache entry.
            var (key, error) = await LoginAsync(username, password, ct).ConfigureAwait(false);
            if (key is null)
            {
                Log.LookupLoginFailed(_logger, error);
                return (null, error);
            }

            _cachedSession = (fingerprint, key);
            return (key, null);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Never logs the constructed request URI -- it contains <paramref name="password"/>
    /// as a plaintext query parameter.</summary>
    private async Task<(string? Key, string? Error)> LoginAsync(string username, string password, CancellationToken ct)
    {
        var url = $"{Endpoint}?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&agent={Agent}";
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var doc = XDocument.Parse(body);
        var session = doc.Root?.Element(QrzNamespace + "Session");
        var key = session?.Element(QrzNamespace + "Key")?.Value;
        var error = session?.Element(QrzNamespace + "Error")?.Value;
        return (string.IsNullOrEmpty(key) ? null : key, string.IsNullOrEmpty(error) ? null : error);
    }

    private async Task<LookupOutcome> LookupWithSessionAsync(string callsign, string sessionKey, CancellationToken ct)
    {
        var url = $"{Endpoint}?s={Uri.EscapeDataString(sessionKey)}&callsign={Uri.EscapeDataString(callsign)}";
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var doc = XDocument.Parse(body);
        var session = doc.Root?.Element(QrzNamespace + "Session");
        var key = session?.Element(QrzNamespace + "Key")?.Value;
        if (string.IsNullOrEmpty(key))
        {
            // <Key> absent -- the session was invalid/expired for this lookup (e.g. "Session
            // Timeout"), distinct from a per-callsign miss below, which keeps a valid <Key>.
            return LookupOutcome.Expired;
        }

        var sessionError = session?.Element(QrzNamespace + "Error")?.Value;
        if (!string.IsNullOrEmpty(sessionError))
        {
            // Session still valid, this specific lookup failed (e.g. "Not found: xx1xxx") -- do
            // NOT evict the cached session over this.
            return LookupOutcome.Failed(sessionError);
        }

        var callsignElement = doc.Root?.Element(QrzNamespace + "Callsign");
        if (callsignElement is null)
        {
            return LookupOutcome.Failed("QRZ returned no Callsign record and no error.");
        }

        var fname = callsignElement.Element(QrzNamespace + "fname")?.Value;
        var lname = callsignElement.Element(QrzNamespace + "name")?.Value;
        var addr2 = callsignElement.Element(QrzNamespace + "addr2")?.Value;
        var country = callsignElement.Element(QrzNamespace + "country")?.Value;
        var grid = callsignElement.Element(QrzNamespace + "grid")?.Value;

        // Same composition as legacy qrzcom::qrzname (qrzcom.cpp:23-45): full name from
        // fname+name, QTH from addr2 with country appended in parens when present.
        var name = string.IsNullOrEmpty(fname) && string.IsNullOrEmpty(lname)
            ? null
            : $"{fname} {lname}".Trim();
        var qth = !string.IsNullOrEmpty(country)
            ? $"{addr2} ({country})".Trim()
            : (string.IsNullOrEmpty(addr2) ? null : addr2);

        return LookupOutcome.Succeeded(name, qth, string.IsNullOrEmpty(grid) ? null : grid);
    }

    private static string ComputeFingerprint(string username, string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username + "\0" + password)));

    private readonly record struct LookupOutcome(bool SessionExpired, bool Success, string? Name, string? Qth, string? Grid, string? ErrorReason)
    {
        // Code-review fix: a real ErrorReason, not null -- LookupAsync's retry-once path calls
        // ToResult() directly on a SECOND SessionExpired outcome without re-checking SessionExpired
        // itself (a pathological double-expiry, or any unexpected response shape lacking BOTH
        // <Session><Key> and <Session><Error> -- see the `key is null` branch above, reachable for
        // more than just true session timeouts). Without a real reason here, that path rendered a
        // visible-but-empty red error box (ObjectConverters.IsNotNull sees a non-null, empty string).
        public static LookupOutcome Expired { get; } = new(SessionExpired: true, Success: false, null, null, null, "QRZ session expired or returned an unexpected response.");

        public static LookupOutcome Failed(string reason) => new(SessionExpired: false, Success: false, null, null, null, reason);

        public static LookupOutcome Succeeded(string? name, string? qth, string? grid) =>
            new(SessionExpired: false, Success: true, name, qth, grid, null);

        public QrzCallsignLookupResult ToResult() => new(Success, Name, Qth, Grid, ErrorReason);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "QRZ credentials test succeeded")]
        public static partial void TestSucceeded(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ credentials test failed: {Reason}")]
        public static partial void TestFailed(ILogger logger, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ credentials test request failed")]
        public static partial void TestRequestFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ credentials test timed out")]
        public static partial void TestTimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ login failed during a callsign lookup: {Reason}")]
        public static partial void LookupLoginFailed(ILogger logger, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ callsign lookup request failed")]
        public static partial void LookupRequestFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ callsign lookup timed out")]
        public static partial void LookupTimedOut(ILogger logger);
    }
}
