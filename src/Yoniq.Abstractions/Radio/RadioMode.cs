namespace Yoniq.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. Backend-agnostic operating mode; each <c>IRadioProtocol</c>
/// maps its own backend's mode vocabulary onto this set (e.g. Hamlib/rigctld's <c>USB</c>/<c>LSB</c>/
/// <c>PKTUSB</c>/... tokens — see spec/04-rigctld.md). A mode token a given backend reports that has no
/// obvious mapping here must resolve to <see cref="Unknown"/>, never throw — an unrecognized-but-valid
/// wire value is not a protocol error.</summary>
public enum RadioMode { Lsb, Usb, Cw, CwR, Am, Fm, Rtty, RttyR, Data, DataR, Pkt, Unknown }
