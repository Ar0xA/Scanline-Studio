namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Writes <see cref="QsoRecord"/>s as an ADIF 3.x file — see spec/08-logging.md's "ADIF
/// import/export" section. Deliberately pure (a <see cref="TextWriter"/>, no file I/O) so the same
/// implementation backs both the batch file-export use case and a single-QSO payload for
/// <see cref="IAdifUdpStreamer"/>'s <c>LoggedADIF</c> UDP message (which is itself just a complete
/// one-record ADIF file, see that interface's own doc comment).</summary>
public interface IAdifExporter
{
    /// <param name="stationCallsign">The logging operator's own callsign (ADIF
    /// <c>STATION_CALLSIGN</c>) — not part of <see cref="QsoRecord"/> itself (that's
    /// <c>OperatorSettings.Callsign</c>, owned by <c>ScanlineStudio.Application</c>, a layer above
    /// <c>Core.Logbook</c>); the caller supplies it. Omitted from the output entirely if null.</param>
    void Export(IEnumerable<QsoRecord> records, TextWriter writer, string? stationCallsign = null);
}
