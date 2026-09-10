namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. Backend-agnostic operating mode; each <c>IRadioProtocol</c>
/// maps its own backend's mode vocabulary onto this set (e.g. Hamlib/rigctld's <c>USB</c>/<c>LSB</c>/
/// <c>PKTUSB</c>/... tokens — see spec/04-rigctld.md). A mode token a given backend reports that has no
/// obvious mapping here must resolve to <see cref="Unknown"/>, never throw — an unrecognized-but-valid
/// wire value is not a protocol error.
///
/// <para><b>Member order is an on-disk contract.</b> This enum persists as a plain integer —
/// <c>FrequencyPreset.Mode</c> reaches <c>settings.json</c> that way, and no
/// <c>JsonStringEnumConverter</c> is registered anywhere in this repo (see
/// <c>AppearanceSettings</c>, <c>RxBufferMode</c> and <c>DemodType</c>, which carry this same
/// warning). Reordering or removing a member silently remaps every already-persisted value.
/// Trimming which modes a PICKER offers is safe; trimming this enum is not.</para></summary>
public enum RadioMode { Lsb, Usb, Cw, CwR, Am, Fm, Rtty, RttyR, Data, DataR, Pkt, Unknown }
