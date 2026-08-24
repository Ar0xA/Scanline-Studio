using System.Net;
using System.Text.RegularExpressions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Flrig;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed partial class FlrigClientProtocolTests
{
    private static FlrigClientProtocol CreateProtocol(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:12345/RPC2") },
            requestTimeout: TimeSpan.FromSeconds(5),
            verifyPollTimeout: TimeSpan.FromMilliseconds(200));

    [GeneratedRegex("<methodName>([^<]+)</methodName>")]
    private static partial Regex MethodNameRegex();

    private static string MethodNameOf(string? body) => body is null ? "" : MethodNameRegex().Match(body).Groups[1].Value;

    private static HttpResponseMessage StringResponse(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"<?xml version=\"1.0\"?><methodResponse><params><param><value>{value}</value></param></params></methodResponse>") };

    private static HttpResponseMessage IntResponse(int value) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"<?xml version=\"1.0\"?><methodResponse><params><param><value><i4>{value}</i4></value></param></params></methodResponse>") };

    private static HttpResponseMessage EmptyResponse() =>
        new(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\"?><methodResponse><params><param><value></value></param></params></methodResponse>") };

    private static HttpResponseMessage ArrayResponse(params string[] values) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data>" +
                string.Concat(values.Select(v => $"<value>{v}</value>")) +
                "</data></array></value></param></params></methodResponse>"),
        };

    [Fact]
    public async Task PollAsync_HappyPath_ReturnsParsedRadioState()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.get_xcvr" => StringResponse("TS-2000"),
                "rig.get_vfoA" => StringResponse("14070000"),
                "rig.get_mode" => StringResponse("USB"),
                "rig.get_ptt" => IntResponse(0),
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        var state = await protocol.PollAsync(CancellationToken.None);

        Assert.Equal(14070000, state.FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.False(state.IsTransmitting);
        Assert.Equal(RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency | RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl, protocol.Capabilities);
    }

    [Fact]
    public async Task PollAsync_XcvrOffline_ThrowsRadioProtocolException()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) == "rig.get_xcvr" ? StringResponse("") : throw new InvalidOperationException("Should not be called"),
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetFrequencyAsync_Refused_ThrowsImmediatelyWithoutVerifyPolling()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) == "rig.set_vfoA" ? IntResponse(0) : throw new InvalidOperationException("Should not be called"),
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetFrequencyAsync(14070000, CancellationToken.None));
        Assert.DoesNotContain(handler.AllRequestBodies, b => MethodNameOf(b) == "rig.get_vfoA");
    }

    [Fact]
    public async Task SetFrequencyAsync_PollUntilMatchConfirms_Succeeds()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.set_vfoA" => EmptyResponse(),
                "rig.get_vfoA" => StringResponse("14070000"),
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await protocol.SetFrequencyAsync(14070000, CancellationToken.None);
    }

    [Fact]
    public async Task SetFrequencyAsync_NeverConfirms_ThrowsAfterVerifyBudget()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.set_vfoA" => EmptyResponse(),
                "rig.get_vfoA" => StringResponse("7040000"), // never matches the requested 14070000
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetFrequencyAsync(14070000, CancellationToken.None));
    }

    [Fact]
    public async Task SetModeAsync_CandidateInRigsModeList_AppliesWithoutReadback()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.get_modes" => ArrayResponse("LSB", "USB", "CW"),
                "rig.set_mode" => IntResponse(1),
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await protocol.SetModeAsync(RadioMode.Usb, CancellationToken.None);

        Assert.Contains(handler.AllRequestBodies, b => MethodNameOf(b) == "rig.set_mode" && b!.Contains("<string>USB</string>"));
    }

    [Fact]
    public async Task SetModeAsync_NoCandidateInRigsModeList_FailsWithoutSendingSetMode()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.get_modes" => ArrayResponse("FOO", "BAR"),
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetModeAsync(RadioMode.Usb, CancellationToken.None));
        Assert.DoesNotContain(handler.AllRequestBodies, b => MethodNameOf(b) == "rig.set_mode");
    }

    [Fact]
    public async Task SetModeAsync_Refused_Throws()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.get_modes" => ArrayResponse("USB"),
                "rig.set_mode" => IntResponse(0),
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetModeAsync(RadioMode.Usb, CancellationToken.None));
    }

    [Fact]
    public async Task SetModeAsync_ModesListUnavailable_FailsCleanlyWithoutSendingSetMode()
    {
        // Matches flrig's own confirmed bug (rig.get_modes returns an empty/unassigned value on its
        // offline/exception paths, not an array) -- must not fabricate "this rig supports no modes"
        // as a permanent state, but a single call still fails cleanly rather than sending a blind guess.
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) == "rig.get_modes" ? EmptyResponse() : throw new InvalidOperationException("Should not be called"),
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetModeAsync(RadioMode.Usb, CancellationToken.None));
        Assert.DoesNotContain(handler.AllRequestBodies, b => MethodNameOf(b) == "rig.set_mode");
    }

    [Fact]
    public async Task SetPttAsync_Refused_Throws()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) == "rig.set_ptt" ? IntResponse(0) : throw new InvalidOperationException("Should not be called"),
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetPttAsync(true, CancellationToken.None));
        Assert.DoesNotContain(handler.AllRequestBodies, b => MethodNameOf(b) == "rig.get_ptt");
    }

    [Fact]
    public async Task SetPttAsync_AttemptedAndConfirmed_Succeeds()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.set_ptt" => EmptyResponse(),
                "rig.get_ptt" => IntResponse(1),
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await protocol.SetPttAsync(true, CancellationToken.None);
    }

    [Fact]
    public async Task SetPttAsync_UnkeyReadbackTimesOut_StillThrowsNotFalselyConfirmed()
    {
        // Code-review finding: the readback timing out (rig.get_ptt never answers) must be treated
        // as unconfirmed for BOTH directions, not just tx=true -- the original bug reported a timed-
        // out un-key readback (tx=false) as a confirmed un-key, exactly the case
        // RadioSessionService.TryUnkeyWithRetryAsync depends on this method to catch, not paper over.
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.set_ptt" => EmptyResponse(),
                "rig.get_ptt" => throw new OperationCanceledException(), // simulates the sub-poll's own timeout
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetPttAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task SetPttAsync_AttemptedButNotConfirmed_Throws()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) => MethodNameOf(body) switch
            {
                "rig.set_ptt" => EmptyResponse(),
                "rig.get_ptt" => IntResponse(0), // requested ON, readback still reports OFF
                _ => throw new InvalidOperationException($"Unexpected RPC: {MethodNameOf(body)}"),
            },
        };
        await using var protocol = CreateProtocol(handler);

        await Assert.ThrowsAsync<RadioProtocolException>(() => protocol.SetPttAsync(true, CancellationToken.None));
    }

    [Fact]
    public async Task SetVfoA_SendsFrequencyAsXmlRpcDoubleNotInt()
    {
        // Round-1 auditor finding: flrig's Xmlrpc++ library enforces the registered type tag
        // strictly -- an <i4> here gets rejected outright, never reaching the rig.
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = (_, body) =>
            {
                if (MethodNameOf(body) == "rig.set_vfoA")
                {
                    capturedBody = body;
                    return EmptyResponse();
                }

                return StringResponse("14070000");
            },
        };
        await using var protocol = CreateProtocol(handler);

        await protocol.SetFrequencyAsync(14070000, CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.Contains("<double>14070000.0</double>", capturedBody);
    }
}
