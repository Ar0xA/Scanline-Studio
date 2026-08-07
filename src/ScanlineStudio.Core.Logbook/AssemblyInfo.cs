using System.Runtime.CompilerServices;

// Lets the test project assert GridTrackerStreamer's exact UDP datagram byte layout directly
// (BuildLoggedAdifDatagram) without a real socket -- a malformed datagram fails silently on the
// GridTracker end, so a byte-exact unit test matters here the same way DSP golden-vector tests do
// elsewhere in this codebase.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Logbook.Tests")]
