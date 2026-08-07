using System.Net;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

    public Exception? ThrowOnSend { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        return ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
