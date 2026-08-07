namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Reads ADIF 3.x text into <see cref="QsoRecord"/>s — see spec/08-logging.md's "ADIF
/// import/export" section. Unmapped/nonstandard ADIF fields are preserved (appended to
/// <see cref="QsoRecord.Notes"/>) rather than silently dropped, per that section. Throws
/// <see cref="FormatException"/> for a record missing one of ADIF's own mandatory fields
/// (<c>CALL</c>/<c>QSO_DATE</c>/<c>TIME_ON</c>) — a batch import fails loud on malformed input
/// rather than silently skipping data the caller didn't ask to skip.</summary>
public interface IAdifImporter
{
    IReadOnlyList<QsoRecord> Import(TextReader reader);
}
