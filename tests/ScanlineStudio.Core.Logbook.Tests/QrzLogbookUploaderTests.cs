using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class QrzLogbookUploaderTests
{
    [Fact]
    public async Task UploadAsync_ResultOk_ReturnsSuccessWithLogId()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("RESULT=OK&LOGID=12345&COUNT=1") },
        };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);

        var result = await uploader.UploadAsync("<call:6>N0CALL<eor>", "test-key");

        Assert.True(result.Success);
        Assert.Equal("12345", result.LogId);
        Assert.Null(result.ErrorReason);
    }

    [Fact]
    public async Task UploadAsync_ResultReplace_TreatedAsSuccess()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("RESULT=REPLACE&LOGID=999&COUNT=1") },
        };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);

        var result = await uploader.UploadAsync("<call:6>N0CALL<eor>", "test-key");

        Assert.True(result.Success);
        Assert.Equal("999", result.LogId);
    }

    [Fact]
    public async Task UploadAsync_ResultFail_ReturnsReasonDecodedFromFormEncoding()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("RESULT=FAIL&REASON=Invalid+API+Key&COUNT=0") },
        };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);

        var result = await uploader.UploadAsync("<call:6>N0CALL<eor>", "bad-key");

        Assert.False(result.Success);
        Assert.Null(result.LogId);
        Assert.Equal("Invalid API Key", result.ErrorReason);
    }

    [Fact]
    public async Task UploadAsync_MalformedResponse_ReturnsFailureNotAnException()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not a valid response") },
        };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);

        var result = await uploader.UploadAsync("<call:6>N0CALL<eor>", "test-key");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorReason);
    }

    [Fact]
    public async Task UploadAsync_RequestThrows_ReturnsFailureNotAnException()
    {
        var handler = new FakeHttpMessageHandler { ThrowOnSend = new HttpRequestException("connection refused") };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);

        var result = await uploader.UploadAsync("<call:6>N0CALL<eor>", "test-key");

        Assert.False(result.Success);
        Assert.Equal("connection refused", result.ErrorReason);
    }

    [Fact]
    public async Task UploadAsync_SendsExpectedFormParameters()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("RESULT=OK&LOGID=1&COUNT=1") },
        };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);
        // Deliberately contains characters that need percent-encoding (&, <, >, newline) so this
        // test proves the full ADIF VALUE round-trips byte-for-byte, not just that an "ADIF=" key
        // is present -- chunk 1 of this sweep found two silent-data-loss bugs in this exact ADIF
        // pipeline, and a presence-only assertion would not have caught either.
        const string adif = "<call:6>N0CALL<comment:15>Tom & Jerry <3\r\n<eor>";

        await uploader.UploadAsync(adif, "my-api-key&special=1");

        Assert.Equal("https://logbook.qrz.com/api", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);

        using var expectedContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["KEY"] = "my-api-key&special=1",
            ["ACTION"] = "INSERT",
            ["ADIF"] = adif,
        });
        var expectedBody = await expectedContent.ReadAsStringAsync();
        Assert.Equal(expectedBody, handler.LastRequestBody);
    }

    [Fact]
    public async Task UploadAsync_RequestTimesOut_ReturnsFailureNotAnException()
    {
        var handler = new FakeHttpMessageHandler
        {
            ThrowOnSend = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        };
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);

        var result = await uploader.UploadAsync("<call:6>N0CALL<eor>", "test-key");

        Assert.False(result.Success);
        Assert.Equal("The request to QRZ.com timed out.", result.ErrorReason);
    }

    [Fact]
    public async Task UploadAsync_GenuineCancellation_ThrowsRatherThanReturningFailure()
    {
        var handler = new FakeHttpMessageHandler();
        var uploader = new QrzLogbookUploader(new FakeHttpClientFactory(handler), NullLogger<QrzLogbookUploader>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploader.UploadAsync("<call:6>N0CALL<eor>", "test-key", cts.Token));
    }
}
