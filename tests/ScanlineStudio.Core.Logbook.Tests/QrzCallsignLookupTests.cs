using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class QrzCallsignLookupTests
{
    private const string LoginSuccessXml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <QRZDatabase version="1.34" xmlns="http://xmldata.qrz.com">
        <Session><Key>SESSIONKEY1</Key><Count>1</Count></Session>
        </QRZDatabase>
        """;

    private const string LoginFailureXml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <QRZDatabase version="1.34" xmlns="http://xmldata.qrz.com">
        <Session><Error>Username/password incorrect</Error></Session>
        </QRZDatabase>
        """;

    private const string LookupSuccessXml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <QRZDatabase version="1.34" xmlns="http://xmldata.qrz.com">
        <Callsign><call>W1AW</call><fname>Hiram</fname><name>Maxim</name><addr2>Newington</addr2><country>United States</country><grid>FN31pr</grid></Callsign>
        <Session><Key>SESSIONKEY1</Key><Count>2</Count></Session>
        </QRZDatabase>
        """;

    private const string LookupMissXml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <QRZDatabase version="1.34" xmlns="http://xmldata.qrz.com">
        <Session><Key>SESSIONKEY1</Key><Error>Not found: xx1xxx</Error></Session>
        </QRZDatabase>
        """;

    private const string SessionExpiredXml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <QRZDatabase version="1.34" xmlns="http://xmldata.qrz.com">
        <Session><Error>Session Timeout</Error></Session>
        </QRZDatabase>
        """;

    private static bool IsLoginRequest(HttpRequestMessage request) => request.RequestUri!.Query.Contains("username=");

    [Fact]
    public async Task LookupAsync_LoginThenLookupSucceeds_ReturnsComposedNameAndQth()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(IsLoginRequest(request) ? LoginSuccessXml : LookupSuccessXml),
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.LookupAsync("W1AW", "user", "pass");

        Assert.True(result.Success);
        Assert.Equal("Hiram Maxim", result.Name);
        Assert.Equal("Newington (United States)", result.Qth);
        Assert.Equal("FN31pr", result.Grid);
        Assert.Null(result.ErrorReason);
    }

    [Fact]
    public async Task LookupAsync_LoginFails_ReturnsFailure_NoLookupAttempted()
    {
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginFailureXml) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.LookupAsync("W1AW", "user", "wrongpass");

        Assert.False(result.Success);
        Assert.Equal("Username/password incorrect", result.ErrorReason);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task LookupAsync_SecondCallWithSameCredentials_ReusesCachedSession_OnlyOneLogin()
    {
        var loginCount = 0;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                if (IsLoginRequest(request))
                {
                    loginCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginSuccessXml) };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LookupSuccessXml) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        await lookup.LookupAsync("W1AW", "user", "pass");
        await lookup.LookupAsync("AA7BQ", "user", "pass");

        Assert.Equal(1, loginCount);
    }

    [Fact]
    public async Task LookupAsync_PasswordChangedSameUsername_TriggersFreshLogin_NotStaleCache()
    {
        // Auditor-caught plan-review fix: the session cache is keyed on a fingerprint of the
        // CREDENTIAL PAIR, not just username -- a password change under the same username must not
        // silently reuse a session opened under the stale password.
        var loginCount = 0;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                if (IsLoginRequest(request))
                {
                    loginCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginSuccessXml) };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LookupSuccessXml) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        await lookup.LookupAsync("W1AW", "user", "pass1");
        await lookup.LookupAsync("W1AW", "user", "pass2");

        Assert.Equal(2, loginCount);
    }

    [Fact]
    public async Task LookupAsync_SessionExpiredMidLookup_RetriesOnceWithFreshLogin_Succeeds()
    {
        var lookupCallCount = 0;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                if (IsLoginRequest(request))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginSuccessXml) };
                }

                lookupCallCount++;
                // First lookup (using a cache-miss-triggered login's key) reports the session
                // expired; the retry after a forced fresh login succeeds.
                var body = lookupCallCount == 1 ? SessionExpiredXml : LookupSuccessXml;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.LookupAsync("W1AW", "user", "pass");

        Assert.True(result.Success);
        Assert.Equal("Hiram Maxim", result.Name);
        Assert.Equal(2, lookupCallCount);
    }

    [Fact]
    public async Task LookupAsync_SessionExpiresEvenAfterTheForcedRetry_ReturnsFailureWithARealReason_NotNull()
    {
        // Code-review fix regression test: a pathological double-expiry (or any unexpected response
        // shape lacking both <Session><Key> and <Session><Error>) used to return ErrorReason: null,
        // which rendered as a visible-but-empty error box in the UI.
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(IsLoginRequest(request) ? LoginSuccessXml : SessionExpiredXml),
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.LookupAsync("W1AW", "user", "pass");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorReason));
    }

    [Fact]
    public async Task LookupAsync_PerCallsignMiss_DoesNotEvictCache()
    {
        var loginCount = 0;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                if (IsLoginRequest(request))
                {
                    loginCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginSuccessXml) };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LookupMissXml) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var first = await lookup.LookupAsync("XX1XXX", "user", "pass");
        var second = await lookup.LookupAsync("XX1XXX", "user", "pass");

        Assert.False(first.Success);
        Assert.Equal("Not found: xx1xxx", first.ErrorReason);
        Assert.False(second.Success);
        // A per-callsign miss keeps <Key>, so the session stays valid -- the second call must not
        // need a second login.
        Assert.Equal(1, loginCount);
    }

    [Fact]
    public async Task LookupAsync_CredentialsAndCallsignAreUrlEscaped()
    {
        HttpRequestMessage? capturedLoginRequest = null;
        HttpRequestMessage? capturedLookupRequest = null;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                if (IsLoginRequest(request))
                {
                    capturedLoginRequest = request;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginSuccessXml) };
                }

                capturedLookupRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LookupSuccessXml) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        await lookup.LookupAsync("W1AW/P", "user", "p@ss;word&more");

        Assert.NotNull(capturedLoginRequest);
        Assert.Contains("p%40ss%3Bword%26more", capturedLoginRequest!.RequestUri!.Query);
        Assert.NotNull(capturedLookupRequest);
        Assert.Contains("W1AW%2FP", capturedLookupRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task TestCredentialsAsync_ValidCredentials_ReturnsSuccess()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginSuccessXml) },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.TestCredentialsAsync("user", "pass");

        Assert.True(result.Success);
        Assert.Null(result.ErrorReason);
    }

    [Fact]
    public async Task TestCredentialsAsync_InvalidCredentials_ReturnsFailureWithReason()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LoginFailureXml) },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.TestCredentialsAsync("user", "wrongpass");

        Assert.False(result.Success);
        Assert.Equal("Username/password incorrect", result.ErrorReason);
    }

    [Fact]
    public async Task TestCredentialsAsync_NeverReadsOrCorruptsTheLiveCache()
    {
        // Auditor-caught plan-review fix: a Test click with WRONG credentials must not be
        // satisfied by (or evict) a different, still-valid cached session from a prior real lookup.
        var loginCount = 0;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                if (IsLoginRequest(request))
                {
                    loginCount++;
                    var query = request.RequestUri!.Query;
                    var succeeds = query.Contains("password=goodpass");
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(succeeds ? LoginSuccessXml : LoginFailureXml),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LookupSuccessXml) };
            },
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        // Real lookup establishes and caches a valid session.
        var lookupResult = await lookup.LookupAsync("W1AW", "user", "goodpass");
        Assert.True(lookupResult.Success);
        Assert.Equal(1, loginCount);

        // Testing wrong credentials must force its own fresh login (not reuse the cache) and fail.
        var testResult = await lookup.TestCredentialsAsync("user", "badpass");
        Assert.False(testResult.Success);
        Assert.Equal(2, loginCount);

        // The original good session must still be usable without a third login.
        var secondLookup = await lookup.LookupAsync("AA7BQ", "user", "goodpass");
        Assert.True(secondLookup.Success);
        Assert.Equal(2, loginCount);
    }

    [Fact]
    public async Task LookupAsync_RequestTimesOut_ReturnsFailureNotAnException()
    {
        var handler = new FakeHttpMessageHandler
        {
            ThrowOnSend = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.LookupAsync("W1AW", "user", "pass");

        Assert.False(result.Success);
        Assert.Equal("The request to QRZ.com timed out.", result.ErrorReason);
    }

    [Fact]
    public async Task LookupAsync_GenuineCancellation_ThrowsRatherThanReturningFailure()
    {
        var handler = new FakeHttpMessageHandler();
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.LookupAsync("W1AW", "user", "pass", cts.Token));
    }

    [Fact]
    public async Task TestCredentialsAsync_RequestTimesOut_ReturnsFailureNotAnException()
    {
        var handler = new FakeHttpMessageHandler
        {
            ThrowOnSend = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        };
        var lookup = new QrzCallsignLookup(new FakeHttpClientFactory(handler), NullLogger<QrzCallsignLookup>.Instance);

        var result = await lookup.TestCredentialsAsync("user", "pass");

        Assert.False(result.Success);
        Assert.Equal("The request to QRZ.com timed out.", result.ErrorReason);
    }
}
