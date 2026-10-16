using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Application;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    Failed,
}

/// <summary>Why a check failed; the UI maps each value to a localized message.</summary>
public enum UpdateCheckFailure
{
    None,
    UnknownCurrentVersion,
    Offline,
    Timeout,
    RateLimited,
    ServerError,
    InvalidResponse,
}

/// <param name="LatestVersion">Set only for <see cref="UpdateCheckStatus.UpdateAvailable"/>.</param>
/// <param name="ReleasePageUrl">The release's GitHub page, already validated to be on this repository's releases path.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    AppVersion? LatestVersion = null,
    Uri? ReleasePageUrl = null,
    UpdateCheckFailure Failure = UpdateCheckFailure.None);

public interface IUpdateChecker
{
    /// <summary>The repository's releases page, used when no specific release URL is available.</summary>
    Uri ReleasesPageUrl { get; }

    /// <summary>Asks GitHub for newer releases. Never throws, except when <paramref name="ct"/> itself is cancelled.
    /// A prerelease current version is offered newer prereleases and releases; a release version only releases.</summary>
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default);
}

public sealed partial class GitHubUpdateChecker : IUpdateChecker
{
    /// <summary>Named <c>IHttpClientFactory</c> client; registered in Program.cs with a timeout and User-Agent.</summary>
    public const string HttpClientName = "GitHubReleases";

    /// <summary>No route, no DNS or a dead Wi-Fi fails within this time instead of the OS's much longer connect timeout.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Upper bound for the whole request, so a connected but stalled GitHub never keeps the user waiting long.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private const string Owner = "Ar0xA";
    private const string Repository = "Scanline-Studio";

    // /releases/latest skips prereleases and returns 404 while every release is a beta, so read the list.
    private static readonly Uri ReleasesApiUrl = new($"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=30");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubUpdateChecker> _logger;

    public GitHubUpdateChecker(IHttpClientFactory httpClientFactory, ILogger<GitHubUpdateChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Uri ReleasesPageUrl { get; } = new($"https://github.com/{Owner}/{Repository}/releases");

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var current = AppVersion.TryParse(currentVersion);
        if (current is null)
        {
            Log.UnknownCurrentVersion(_logger, currentVersion);
            return Failed(UpdateCheckFailure.UnknownCurrentVersion);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                Log.HttpFailed(_logger, (int)response.StatusCode);
                return Failed(UpdateCheckFailure.RateLimited);
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.HttpFailed(_logger, (int)response.StatusCode);
                return Failed(UpdateCheckFailure.ServerError);
            }

            var releases = await response.Content
                .ReadFromJsonAsync(GitHubReleaseJsonContext.Default.GitHubReleaseArray, ct)
                .ConfigureAwait(false);
            if (releases is null)
            {
                Log.InvalidResponse(_logger, null);
                return Failed(UpdateCheckFailure.InvalidResponse);
            }

            var result = PickNewest(current, releases);
            Log.Checked(_logger, current, result.LatestVersion);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only HttpClient.Timeout can cancel here; a caller cancellation sets ct.
            Log.TimedOut(_logger);
            return Failed(UpdateCheckFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            Log.Offline(_logger, ex);
            return Failed(UpdateCheckFailure.Offline);
        }
        catch (JsonException ex)
        {
            Log.InvalidResponse(_logger, ex);
            return Failed(UpdateCheckFailure.InvalidResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else from a 200 body (unknown charset, unexpected shape) must not escape:
            // the Help-menu command has no catch of its own.
            Log.InvalidResponse(_logger, ex);
            return Failed(UpdateCheckFailure.InvalidResponse);
        }
    }

    /// <summary>Highest non-draft release newer than <paramref name="current"/>, honouring the channel rule.</summary>
    public UpdateCheckResult PickNewest(AppVersion current, IEnumerable<GitHubRelease?> releases)
    {
        AppVersion? best = null;
        Uri? bestUrl = null;
        foreach (var release in releases)
        {
            if (release is null || release.Draft || (release.Prerelease && !current.IsPrerelease))
            {
                continue;
            }

            var version = AppVersion.TryParse(release.TagName);
            if (version is null || version.IsPrerelease && !current.IsPrerelease || version.CompareTo(current) <= 0)
            {
                continue;
            }

            if (best is null || version.CompareTo(best) > 0)
            {
                best = version;
                bestUrl = IsOwnReleasePage(release.HtmlUrl) ? new Uri(release.HtmlUrl!) : ReleasesPageUrl;
            }
        }

        return best is null
            ? new UpdateCheckResult(UpdateCheckStatus.UpToDate)
            : new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, best, bestUrl);
    }

    /// <summary>The URL comes from the network and is handed to the OS shell, so only this repository's
    /// https release pages are accepted.</summary>
    public static bool IsOwnReleasePage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && !uri.OriginalString.Contains("..", StringComparison.Ordinal)
        && !uri.OriginalString.Contains('%', StringComparison.Ordinal)
        && uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/releases/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private UpdateCheckResult Failed(UpdateCheckFailure failure) =>
        new(UpdateCheckStatus.Failed, Failure: failure, ReleasePageUrl: ReleasesPageUrl);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Update check skipped: current version {Version} is not a valid version")]
        public static partial void UnknownCurrentVersion(ILogger logger, string version);

        [LoggerMessage(Level = LogLevel.Information, Message = "Update check: current {Current}, newer available {Latest}")]
        public static partial void Checked(ILogger logger, AppVersion current, AppVersion? latest);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Update check: GitHub returned HTTP {StatusCode}")]
        public static partial void HttpFailed(ILogger logger, int statusCode);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Update check timed out")]
        public static partial void TimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Update check could not reach GitHub")]
        public static partial void Offline(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Update check: GitHub response was not a valid release list")]
        public static partial void InvalidResponse(ILogger logger, Exception? ex);
    }
}

/// <summary>The fields of a GitHub release this app reads.</summary>
public sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }
}

[JsonSerializable(typeof(GitHubRelease[]))]
public sealed partial class GitHubReleaseJsonContext : JsonSerializerContext;
