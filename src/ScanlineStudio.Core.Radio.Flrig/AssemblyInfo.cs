using System.Runtime.CompilerServices;

// XmlRpcCodec and FlrigModeTokens stay internal -- FlrigClientProtocol is the only intended caller.
// Tests need direct access to exercise the wire-format codec and mode-token table in isolation
// (same InternalsVisibleTo pattern ScanlineStudio.Core.Radio.Hamlib already uses for IHamlibNative),
// not just through IRadioProtocol's public surface.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Radio.Tests")]
