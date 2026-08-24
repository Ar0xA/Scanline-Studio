using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Flrig;

/// <summary>Hand-rolled minimal XML-RPC request/response codec, built to flrig's real, confirmed wire
/// shapes -- verified directly against the bundled Xmlrpc++ library in a local flrig source clone (see
/// the implementation plan for exact file:line citations), not assumed from the general XML-RPC spec:
/// <list type="bullet">
/// <item>A string value serializes bare, with no <c>&lt;string&gt;</c> tag
/// (<c>&lt;value&gt;TS-2000&lt;/value&gt;</c>).</item>
/// <item>An unassigned server-side result is coerced to a genuinely empty
/// <c>&lt;value&gt;&lt;/value&gt;</c> -- a load-bearing "attempted/applied" signal for
/// <c>rig.set_mode</c>/<c>rig.set_ptt</c>/<c>rig.set_vfoA</c>, not "no response."</item>
/// <item>Response integers serialize as <c>&lt;i4&gt;</c>, never <c>&lt;int&gt;</c>.</item>
/// <item>A fault always arrives as HTTP 200 with a <c>&lt;fault&gt;</c> body -- HTTP status is never a
/// fault discriminator, the body must always be parsed.</item>
/// </list>
/// Request bodies are emitted un-indented (<see cref="XmlWriterSettings.Indent"/> = false) --
/// deliberately: flrig's parser captures a tag's text content verbatim between delimiters, so a
/// pretty-printed <c>&lt;string&gt;\n\tUSB-D\n&lt;/string&gt;</c> would arrive as
/// <c>"\n\tUSB-D\n"</c>, which fails <c>rig.set_mode</c>'s case-sensitive exact match on every call.
/// Don't "clean up" this formatting later.</summary>
internal static class XmlRpcCodec
{
    public static byte[] BuildRequest(string methodName, params object[] args)
    {
        var methodCall = new XElement("methodCall", new XElement("methodName", methodName));
        if (args.Length > 0)
        {
            methodCall.Add(new XElement(
                "params",
                args.Select(a => new XElement("param", new XElement("value", ValueElement(a))))));
        }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
               {
                   Indent = false,
                   Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
               }))
        {
            new XDocument(methodCall).Save(writer);
        }

        return stream.ToArray();
    }

    private static XElement ValueElement(object value) => value switch
    {
        string s => new XElement("string", s),
        double d => new XElement("double", d.ToString("F1", CultureInfo.InvariantCulture)),
        int i => new XElement("i4", i.ToString(CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"Unsupported XML-RPC param type: {value.GetType()}", nameof(value)),
    };

    /// <summary>The raw text content of a successful response's single value -- <see langword="null"/>
    /// for a genuinely empty <c>&lt;value&gt;&lt;/value&gt;</c> (the "attempted/applied" signal
    /// <c>set_mode</c>/<c>set_ptt</c>/<c>set_vfoA</c> use), the bare text otherwise. Works uniformly
    /// for an untyped string value and a typed <c>&lt;i4&gt;</c>/<c>&lt;double&gt;</c> value -- the
    /// caller already knows which type it expects for a given method and parses accordingly. Throws
    /// <see cref="RadioProtocolException"/> on a fault response or an unparseable/unrecognized body.</summary>
    public static string? ParseScalarResponse(string responseBody)
    {
        var value = GetResponseValueOrThrow(responseBody);
        var typedChild = value.Elements().FirstOrDefault();
        var text = (typedChild ?? value).Value;
        return text.Length == 0 ? null : text;
    }

    /// <summary>The response's array of strings (only used for <c>rig.get_modes</c>) -- an empty list,
    /// NOT a throw, when the array is missing/malformed. This matches flrig's own confirmed bug: its
    /// <c>rig.get_modes</c> offline and exception-catch paths leave the result unassigned instead of
    /// an array (surfacing here as the same empty <c>&lt;value&gt;&lt;/value&gt;</c> shape described
    /// above), which must be tolerated as "mode list unknown right now," not treated as an error. A
    /// genuine fault (e.g. an older flrig lacking <c>rig.get_modes</c> entirely) still throws via
    /// <see cref="GetResponseValueOrThrow"/> -- that must surface as a real error, not be silently
    /// absorbed into "empty mode list" the way the bug above is deliberately tolerated.</summary>
    public static IReadOnlyList<string> ParseArrayResponse(string responseBody)
    {
        var value = GetResponseValueOrThrow(responseBody);
        var entries = value.Element("array")?.Element("data")?.Elements("value");
        return entries?.Select(v => v.Value).ToList() ?? [];
    }

    private static XElement GetResponseValueOrThrow(string responseBody)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(responseBody);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        {
            throw new RadioProtocolException("flrig returned an unparseable XML-RPC response.", ex);
        }

        var root = doc.Root;
        if (root is null || root.Name != "methodResponse")
        {
            throw new RadioProtocolException("flrig returned an unrecognized XML-RPC response shape.");
        }

        var fault = root.Element("fault");
        if (fault is not null)
        {
            throw new RadioProtocolException($"flrig XML-RPC fault: {ExtractFaultString(fault)}");
        }

        var value = root.Element("params")?.Element("param")?.Element("value");
        if (value is null)
        {
            throw new RadioProtocolException("flrig returned a methodResponse with no <value>.");
        }

        return value;
    }

    private static string ExtractFaultString(XElement fault)
    {
        var members = fault.Element("value")?.Element("struct")?.Elements("member");
        var faultStringMember = members?.FirstOrDefault(m => m.Element("name")?.Value == "faultString");
        return faultStringMember?.Element("value")?.Value ?? "(no faultString in fault response)";
    }
}
