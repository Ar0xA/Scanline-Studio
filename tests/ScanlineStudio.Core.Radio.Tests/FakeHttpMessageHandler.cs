using System.Net;

namespace ScanlineStudio.Core.Radio.Tests;

// Mirrors ScanlineStudio.Core.Logbook.Tests/FakeHttpMessageHandler.cs exactly -- same shape,
// duplicated per-project rather than shared, matching this codebase's existing convention of a
// project-local fixture fake (e.g. FakeRadioTransport lives in Core.Radio, not a shared test-utils
// assembly).
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    /// <summary>Every request body seen, in order -- lets a test assert a specific RPC was never
    /// sent at all (e.g. SetModeAsync's own pre-check must skip `rig.set_mode` entirely when no
    /// candidate token is in the cached mode list), not just inspect the last one.</summary>
    public List<string?> AllRequestBodies { get; } = [];

    public Func<HttpRequestMessage, string?, HttpResponseMessage>? ResponseFactory { get; set; }

    public Exception? ThrowOnSend { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        AllRequestBodies.Add(LastRequestBody);

        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        return ResponseFactory?.Invoke(request, LastRequestBody) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
