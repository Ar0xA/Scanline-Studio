namespace ScanlineStudio.Abstractions.Cw;

/// <summary>Payload for <c>ISstvSessionService.CwIdDecoded</c> (fsk_cwid.md B-P2) -- the Application-
/// layer event a decoded CW ID window produces, one per capture window, mirroring
/// <c>ScanlineStudio.Abstractions.Sstv.FskStationIdDecodedInfo</c>'s own shape for the FSK-ID
/// pipeline. <see cref="ReceptionSequence"/> is the reception the window belongs to (stamped at arm
/// time, same "0 means unset" contract as <c>ISstvDecoder.ReceptionSequence</c> itself) -- CW results
/// are structurally late-arriving (capture-window close plus background decode, well after the image
/// and well after any FSK ID), so this is what lets a subscriber reject a decode that belongs to a
/// reception that's no longer current (fsk_cwid.md A5's stale guard, a hard prerequisite before any
/// UI wiring consumes this event). <see cref="Text"/> is the decoder's raw output;
/// <see cref="Callsign"/> is the SEPARATE result of running <see cref="Text"/> through
/// <c>ScanlineStudio.Core.Cw.CwIdCallsignExtractor</c> ("DE W1AW/M" -&gt; "W1AW/M"), null if no
/// callsign-shaped token was found. <see cref="Confidence"/>/<see cref="ToneHz"/>/<see cref="Wpm"/>
/// are forwarded from the underlying <see cref="CwDecodeResult"/> unchanged.</summary>
public sealed record CwIdDecodedInfo(
    long ReceptionSequence,
    string Text,
    string? Callsign,
    double Confidence,
    double? ToneHz,
    double? Wpm,
    CwDecoderBackend Backend);
