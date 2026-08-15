namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>One configured forwarding target for <see cref="IAdifUdpStreamer"/> -- a plain
/// host/port an external logging program (GridTracker, N1MM Logger+, Log4OM, or anything else that
/// listens for the WSJT-X network protocol's <c>LoggedADIF</c> message) is expected to be listening
/// on. Deliberately generic, not per-service -- see <c>IAdifUdpStreamer</c>'s own doc comment.
///
/// Every property nullable, never a property-initializer default -- same reasoning as
/// <c>AdifUdpStreamingSettings</c>'s own doc comment: <c>System.Text.Json</c> silently deserializes
/// a property missing from an already-persisted JSON element to the CLR default, not a C# initializer
/// value; a hand-edited/partial destination entry must not crash into a non-nullable
/// <see cref="string"/>. Validated/defaulted only at the read site.</summary>
public sealed record AdifUdpDestination
{
    public bool? Enabled { get; init; }

    public string? Name { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }
}
