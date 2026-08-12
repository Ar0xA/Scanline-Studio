namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Payload for <see cref="ISstvDecoder.StationIdDecoded"/> -- exactly one of
/// <see cref="Callsign"/>/<see cref="CompactNr"/>/<see cref="NrText"/> is set per event, mirroring
/// <c>ScanlineStudio.Core.Sstv.NarrowFskHeaderDecoder</c>'s own <c>FskDecodeResult</c> station-ID
/// fields (see that type's doc comment for why the compact-numeric and string-form NR/RST are kept
/// separate rather than collapsed). Lives here (not <c>ScanlineStudio.Core.Sstv</c>, where the
/// decoder that raises it lives) so <see cref="ISstvDecoder"/> can declare the event without
/// <c>ScanlineStudio.Abstractions</c> depending on <c>ScanlineStudio.Core.Sstv</c>.</summary>
public sealed record FskStationIdDecodedInfo(
    string? Callsign = null,
    uint? CompactNr = null,
    string? NrText = null);
