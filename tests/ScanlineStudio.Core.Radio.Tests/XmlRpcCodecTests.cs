using System.Text;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Flrig;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed class XmlRpcCodecTests
{
    [Fact]
    public void BuildRequest_NoArgs_EmitsUnindentedMethodCallWithNoParams()
    {
        var bytes = XmlRpcCodec.BuildRequest("rig.get_xcvr");
        var xml = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\n\t", xml);
        Assert.Contains("<methodName>rig.get_xcvr</methodName>", xml);
        Assert.DoesNotContain("<params>", xml);
    }

    [Fact]
    public void BuildRequest_StringArg_EmitsUnindentedStringValueWithNoInjectedWhitespace()
    {
        // Round-2 auditor finding: a pretty-printed <string>\n\tUSB-D\n</string> would fail flrig's
        // exact-match parsing on every single mode set -- this must never regress.
        var bytes = XmlRpcCodec.BuildRequest("rig.set_mode", "USB-D");
        var xml = Encoding.UTF8.GetString(bytes);

        Assert.Contains("<string>USB-D</string>", xml);
        Assert.DoesNotContain("\n\t", xml);
    }

    [Fact]
    public void BuildRequest_DoubleArg_FormatsWithInvariantCultureAndDoubleTag()
    {
        var bytes = XmlRpcCodec.BuildRequest("rig.set_vfoA", 14070000.0);
        var xml = Encoding.UTF8.GetString(bytes);

        Assert.Contains("<double>14070000.0</double>", xml);
    }

    [Fact]
    public void BuildRequest_IntArg_EmitsI4Tag()
    {
        var bytes = XmlRpcCodec.BuildRequest("rig.set_ptt", 1);
        var xml = Encoding.UTF8.GetString(bytes);

        Assert.Contains("<i4>1</i4>", xml);
    }

    [Fact]
    public void BuildRequest_StringArg_XmlEscapesSpecialCharacters()
    {
        var bytes = XmlRpcCodec.BuildRequest("rig.set_mode", "A&B<C>");
        var xml = Encoding.UTF8.GetString(bytes);

        Assert.Contains("A&amp;B&lt;C&gt;", xml);
    }

    // Response shapes below are pinned to flrig's REAL, confirmed serialization (verified against a
    // local flrig source clone's bundled Xmlrpc++ library, not the general XML-RPC spec) -- see the
    // implementation plan for exact file:line citations.

    [Fact]
    public void ParseScalarResponse_BareStringValue_ReturnsRawText()
    {
        const string body = "<?xml version=\"1.0\"?><methodResponse><params><param><value>TS-2000</value></param></params></methodResponse>";

        var result = XmlRpcCodec.ParseScalarResponse(body);

        Assert.Equal("TS-2000", result);
    }

    [Fact]
    public void ParseScalarResponse_I4Value_ReturnsRawText()
    {
        const string body = "<?xml version=\"1.0\"?><methodResponse><params><param><value><i4>0</i4></value></param></params></methodResponse>";

        var result = XmlRpcCodec.ParseScalarResponse(body);

        Assert.Equal("0", result);
    }

    [Fact]
    public void ParseScalarResponse_GenuinelyEmptyValue_ReturnsNull()
    {
        // The load-bearing "attempted/applied" signal for set_mode/set_ptt/set_vfoA -- not "no
        // response." Must be distinguishable from a bare string value, not coerced to "".
        const string body = "<?xml version=\"1.0\"?><methodResponse><params><param><value></value></param></params></methodResponse>";

        var result = XmlRpcCodec.ParseScalarResponse(body);

        Assert.Null(result);
    }

    [Fact]
    public void ParseScalarResponse_FaultResponse_ThrowsRadioProtocolExceptionWithFaultString()
    {
        const string body = """
            <?xml version="1.0"?><methodResponse><fault><value><struct>
            <member><name>faultCode</name><value><i4>-1</i4></value></member>
            <member><name>faultString</name><value><string>unknown method name</string></value></member>
            </struct></value></fault></methodResponse>
            """;

        var ex = Assert.Throws<RadioProtocolException>(() => XmlRpcCodec.ParseScalarResponse(body));
        Assert.Contains("unknown method name", ex.Message);
    }

    [Fact]
    public void ParseScalarResponse_FaultIsHttp200Shaped_StillThrows()
    {
        // Faults always arrive as HTTP 200 -- this test only exercises the body-parsing side (the
        // HTTP status itself is asserted separately in FlrigClientProtocolTests), confirming the
        // codec never treats a well-formed <fault> body as a success just because it parses.
        const string body = "<?xml version=\"1.0\"?><methodResponse><fault><value><struct><member><name>faultString</name><value><string>bad</string></value></member></struct></value></fault></methodResponse>";

        Assert.Throws<RadioProtocolException>(() => XmlRpcCodec.ParseScalarResponse(body));
    }

    [Fact]
    public void ParseScalarResponse_MalformedBody_ThrowsRadioProtocolException()
    {
        Assert.Throws<RadioProtocolException>(() => XmlRpcCodec.ParseScalarResponse("not xml at all"));
    }

    [Fact]
    public void ParseArrayResponse_ValidArray_ReturnsAllEntries()
    {
        const string body = """
            <?xml version="1.0"?><methodResponse><params><param><value><array><data>
            <value>LSB</value><value>USB</value><value>CW</value>
            </data></array></value></param></params></methodResponse>
            """;

        var modes = XmlRpcCodec.ParseArrayResponse(body);

        Assert.Equal(["LSB", "USB", "CW"], modes);
    }

    [Fact]
    public void ParseArrayResponse_EmptyValueLikeFlrigsOwnOfflineBug_ReturnsEmptyListNotThrow()
    {
        // Matches flrig's own confirmed bug: rig.get_modes' offline/exception paths leave `result`
        // unassigned instead of an array -- this must be tolerated as "mode list unknown right now,"
        // not treated as an error.
        const string body = "<?xml version=\"1.0\"?><methodResponse><params><param><value></value></param></params></methodResponse>";

        var modes = XmlRpcCodec.ParseArrayResponse(body);

        Assert.Empty(modes);
    }

    [Fact]
    public void ParseArrayResponse_UnknownMethodFault_ThrowsNotAbsorbedAsEmptyList()
    {
        // An older flrig lacking rig.get_modes entirely responds with a genuine fault, which must
        // surface as a real error -- distinct from the offline/exception-bug case above.
        const string body = "<?xml version=\"1.0\"?><methodResponse><fault><value><struct><member><name>faultString</name><value><string>unknown method name</string></value></member></struct></value></fault></methodResponse>";

        Assert.Throws<RadioProtocolException>(() => XmlRpcCodec.ParseArrayResponse(body));
    }
}
