namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Payload for <see cref="ISstvDecoder.StationIdDecoded"/> -- exactly one of
/// <see cref="Callsign"/>/<see cref="CompactNr"/>/<see cref="NrText"/> is set per event, mirroring
/// <c>ScanlineStudio.Core.Sstv.NarrowFskHeaderDecoder</c>'s own <c>FskDecodeResult</c> station-ID
/// fields (see that type's doc comment for why the compact-numeric and string-form NR/RST are kept
/// separate rather than collapsed). Lives here (not <c>ScanlineStudio.Core.Sstv</c>, where the
/// decoder that raises it lives) so <see cref="ISstvDecoder"/> can declare the event without
/// <c>ScanlineStudio.Abstractions</c> depending on <c>ScanlineStudio.Core.Sstv</c>.
///
/// <see cref="ReceptionSequence"/> (fsk_cwid.md A1): the reception this decode belongs to, stamped
/// by <c>RestartableSstvDecoder.OnStationIdDecoded</c> from its own <see cref="ISstvDecoder.ReceptionSequence"/>
/// counter at forward time -- <c>AnalogFmSstvDecoder</c> itself doesn't own that counter, so it can't
/// stamp this. Default <c>0</c>, the same "no identity" value <see cref="ISstvDecoder.ReceptionSequence"/>'s
/// own contract reserves (see <c>RxAudioAutoSaver.cs:98-101</c>) -- a decode that somehow arrives
/// before any reception has ever started carries that sentinel, not a real sequence.</summary>
public sealed record FskStationIdDecodedInfo(
    string? Callsign = null,
    uint? CompactNr = null,
    string? NrText = null,
    long ReceptionSequence = 0);
