using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanlineStudio.Application.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.9.2-beta", "0.9.2")]
    [InlineData("0.9.1-beta", "0.9.2-beta")]
    [InlineData("0.9.2-beta", "0.10.0-beta")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0-Beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.99999999999999999999", "1.0.0-beta.100000000000000000000")]
    public void CompareTo_FollowsSemVerPrecedence(string lower, string higher)
    {
        var a = AppVersion.TryParse(lower)!;
        var b = AppVersion.TryParse(higher)!;

        Assert.True(a.CompareTo(b) < 0, $"{lower} < {higher}");
        Assert.True(b.CompareTo(a) > 0, $"{higher} > {lower}");
    }

    [Theory]
    [InlineData("v0.9.1-beta", "0.9.1-beta")]
    [InlineData("0.9.2-beta+488ee42", "0.9.2-beta")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    public void TryParse_NormalizesTagPrefixAndBuildMetadata(string text, string expected)
    {
        var version = AppVersion.TryParse(text);

        Assert.NotNull(version);
        Assert.Equal(expected, version.ToString());
        Assert.Equal(AppVersion.TryParse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("99999999999.0.0")]
    [InlineData("-1.0.0")]
    public void TryParse_RejectsInvalidVersions(string? text) => Assert.Null(AppVersion.TryParse(text));
}

public sealed class GitHubUpdateCheckerTests
{
    private static string Release(string tag, bool prerelease = true, bool draft = false, string? url = null) =>
        $$"""{"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},"html_url":"{{url ?? $"https://github.com/Ar0xA/Scanline-Studio/releases/tag/{tag}"}}","assets":[]}""";

    private static GitHubUpdateChecker CreateChecker(StubHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<GitHubUpdateChecker>.Instance);

    private static StubHandler Json(params string[] releases) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[" + string.Join(',', releases) + "]", Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task Prerelease_IsOfferedTheHighestNewerPrereleaseOrRelease()
    {
        var checker = CreateChecker(Json(Release("v0.9.1-beta"), Release("v0.9.3-beta"), Release("v0.9.2-beta")));

        var result = await checker.CheckAsync("0.9.2-beta");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.9.3-beta", result.LatestVersion?.ToString());
        Assert.Equal("https://github.com/Ar0xA/Scanline-Studio/releases/tag/v0.9.3-beta", result.ReleasePageUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task Prerelease_StableReleaseOfSameCoreIsNewer()
    {
        var checker = CreateChecker(Json(Release("v0.9.2", prerelease: false)));

        var result = await checker.CheckAsync("0.9.2-beta");

        Assert.Equal("0.9.2", result.LatestVersion?.ToString());
    }

    [Fact]
    public async Task StableVersion_IgnoresPrereleases()
    {
        var checker = CreateChecker(Json(Release("v1.1.0-beta"), Release("v1.0.1", prerelease: false)));

        var result = await checker.CheckAsync("1.0.0");

        Assert.Equal("1.0.1", result.LatestVersion?.ToString());
    }

    [Fact]
    public async Task StableVersion_OnlyNewerPrereleases_IsUpToDate()
    {
        var checker = CreateChecker(Json(Release("v1.1.0-beta")));

        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync("1.0.0")).Status);
    }

    [Fact]
    public async Task DraftsAndUnparseableTags_AreSkipped()
    {
        var checker = CreateChecker(Json(Release("v9.0.0-beta", draft: true), Release("nightly"), Release("v0.9.1-beta")));

        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync("0.9.2-beta")).Status);
    }

    [Fact]
    public async Task EmptyList_IsUpToDate()
    {
        Assert.Equal(UpdateCheckStatus.UpToDate, (await CreateChecker(Json()).CheckAsync("0.9.2-beta")).Status);
    }

    [Theory]
    [InlineData("https://evil.example/Ar0xA/Scanline-Studio/releases/tag/v1")]
    [InlineData("http://github.com/Ar0xA/Scanline-Studio/releases/tag/v1")]
    [InlineData("https://github.com/other/repo/releases/tag/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://github.com/Ar0xA/Scanline-Studio/releases/tag/v1?x=1")]
    [InlineData("https://x@github.com/Ar0xA/Scanline-Studio/releases/tag/v1")]
    [InlineData("https://github.com:8443/Ar0xA/Scanline-Studio/releases/tag/v1")]
    [InlineData("https://github.com/Ar0xA/Scanline-Studio/releases/../../other/repo")]
    [InlineData("https://github.com/Ar0xA/Scanline-Studio/releases/%2e%2e/%2e%2e/other")]
    public async Task ForeignOrUnsafeReleaseUrl_FallsBackToTheReleasesPage(string url)
    {
        var checker = CreateChecker(Json(Release("v0.9.3-beta", url: url)));

        var result = await checker.CheckAsync("0.9.2-beta");

        Assert.Equal(checker.ReleasesPageUrl, result.ReleasePageUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateCheckFailure.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateCheckFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateCheckFailure.ServerError)]
    [InlineData(HttpStatusCode.NotFound, UpdateCheckFailure.ServerError)]
    public async Task HttpErrors_MapToFailureReasons(HttpStatusCode status, UpdateCheckFailure expected)
    {
        var checker = CreateChecker(new StubHandler(_ => new HttpResponseMessage(status)));

        var result = await checker.CheckAsync("0.9.2-beta");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(checker.ReleasesPageUrl, result.ReleasePageUrl);
    }

    [Theory]
    [InlineData("{\"message\":\"not a list\"}", "application/json")]
    [InlineData("[{\"tag_name\":", "application/json")]
    [InlineData("<html>captive portal</html>", "text/html")]
    public async Task UnreadableBody_IsInvalidResponse(string body, string mediaType)
    {
        var checker = CreateChecker(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        }));

        Assert.Equal(UpdateCheckFailure.InvalidResponse, (await checker.CheckAsync("0.9.2-beta")).Failure);
    }

    [Fact]
    public async Task NullEntries_AreSkipped()
    {
        var checker = CreateChecker(Json("null", Release("v0.9.3-beta")));

        Assert.Equal("0.9.3-beta", (await checker.CheckAsync("0.9.2-beta")).LatestVersion?.ToString());
    }

    [Fact]
    public async Task UnknownCharset_IsInvalidResponse_NotAnException()
    {
        var checker = CreateChecker(new StubHandler(_ =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("[]"));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "x-bogus" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        var result = await checker.CheckAsync("0.9.2-beta");

        Assert.NotEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task NetworkError_IsOffline()
    {
        var checker = CreateChecker(new StubHandler(_ => throw new HttpRequestException("no route")));

        Assert.Equal(UpdateCheckFailure.Offline, (await checker.CheckAsync("0.9.2-beta")).Failure);
    }

    // Real SocketsHttpHandler, request redirected to a target that cannot answer; no internet access needed.
    [Theory]
    [InlineData("https://scanline-update-check-test.invalid/")]
    [InlineData("https://10.255.255.1/")]
    public async Task NoConnection_FailsFastWithConnectFailure(string unreachable)
    {
        using var socketHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(1) };
        using var redirect = new RedirectHandler(new Uri(unreachable)) { InnerHandler = socketHandler };
        var checker = new GitHubUpdateChecker(new StubHttpClientFactory(redirect, TimeSpan.FromSeconds(3)), NullLogger<GitHubUpdateChecker>.Instance);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await checker.CheckAsync("0.9.2-beta");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains(result.Failure, new[] { UpdateCheckFailure.Offline, UpdateCheckFailure.Timeout });
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Timeouts_StayShort()
    {
        Assert.True(GitHubUpdateChecker.ConnectTimeout <= TimeSpan.FromSeconds(5));
        Assert.True(GitHubUpdateChecker.RequestTimeout <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HttpClientTimeout_IsTimeout_NotCancellation()
    {
        var checker = CreateChecker(new StubHandler(_ => throw new TaskCanceledException("timeout")));

        Assert.Equal(UpdateCheckFailure.Timeout, (await checker.CheckAsync("0.9.2-beta")).Failure);
    }

    [Fact]
    public async Task CallerCancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var checker = CreateChecker(Json(Release("v0.9.3-beta")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync("0.9.2-beta", cts.Token));
    }

    [Fact]
    public async Task UnknownCurrentVersion_FailsWithoutRequest()
    {
        var handler = Json(Release("v0.9.3-beta"));

        var result = await CreateChecker(handler).CheckAsync("unknown");

        Assert.Equal(UpdateCheckFailure.UnknownCurrentVersion, result.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Request_GoesToTheReleasesListWithGitHubAcceptHeader()
    {
        var handler = Json();

        await CreateChecker(handler).CheckAsync("0.9.2-beta");

        Assert.Equal("https://api.github.com/repos/Ar0xA/Scanline-Studio/releases?per_page=30", handler.LastRequest?.RequestUri?.AbsoluteUri);
        Assert.Contains("application/vnd.github+json", handler.LastRequest?.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
    }

    private sealed class RedirectHandler(Uri target) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = target;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
