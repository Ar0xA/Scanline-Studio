using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Flrig;

/// <summary>See spec/03-cat-layer.md's flrig section. Resolves <see cref="FlrigConnectionSpec"/> to a
/// <see cref="FlrigClientProtocol"/> constructed over a named <see cref="HttpClient"/>.</summary>
public sealed class FlrigProtocolFactory : IRadioProtocolFactory
{
    /// <summary>The named <c>IHttpClientFactory</c> client this backend uses -- must match the name
    /// passed to <c>services.AddHttpClient(...)</c> in <c>ScanlineStudio.Host.Program</c> (same
    /// duplicated-literal pattern <c>QrzCallsignLookup</c>'s own <c>HttpClientName</c> constant already
    /// uses for the QRZ clients).</summary>
    private const string HttpClientName = "Flrig";

    /// <summary>flrig's own default XML-RPC endpoint path (verified against a local flrig source
    /// clone, <c>src/xmlrpcpp/XmlRpcClient.cpp</c>). The server itself never parses the request path
    /// at all, so this is conventional on the client side, not enforced by flrig.</summary>
    private const string RpcPath = "/RPC2";

    /// <summary>Bounds each whole public-method transaction on <see cref="FlrigClientProtocol"/> --
    /// see that class's own doc comment for why this differs from a blanket <c>HttpClient.Timeout</c>.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Bounds each individual sub-poll inside the frequency-set verification loop and the
    /// PTT-set confirmation readback -- see <see cref="FlrigClientProtocol"/>'s own doc comment.</summary>
    private static readonly TimeSpan VerifyPollTimeout = TimeSpan.FromSeconds(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public FlrigProtocolFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public bool CanHandle(RadioConnectionSpec spec) => spec is FlrigConnectionSpec;

    public IRadioProtocol Create(RadioConnectionSpec spec)
    {
        if (spec is not FlrigConnectionSpec flrigSpec)
        {
            throw new ArgumentException(
                $"{nameof(FlrigProtocolFactory)} can only create protocols for " +
                $"{nameof(FlrigConnectionSpec)} -- call {nameof(CanHandle)} first.",
                nameof(spec));
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        httpClient.BaseAddress = new Uri($"http://{flrigSpec.Host}:{flrigSpec.Port}{RpcPath}");

        return new FlrigClientProtocol(
            httpClient, RequestTimeout, VerifyPollTimeout, _loggerFactory.CreateLogger<FlrigClientProtocol>());
    }
}
