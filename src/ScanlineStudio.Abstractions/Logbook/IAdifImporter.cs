using System.Text;

namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Reads ADIF 3.x text into <see cref="QsoRecord"/>s — see spec/08-logging.md's "ADIF
/// import/export" section. Unmapped/nonstandard ADIF fields are preserved (appended to
/// <see cref="QsoRecord.Notes"/>) rather than silently dropped, per that section. Throws
/// <see cref="FormatException"/> for a record missing one of ADIF's own mandatory fields
/// (<c>CALL</c>/<c>QSO_DATE</c>/<c>TIME_ON</c>) — a batch import fails loud on malformed input
/// rather than silently skipping data the caller didn't ask to skip.</summary>
public interface IAdifImporter
{
    /// <summary>T1-17 (production_audit.md): ADIF field lengths are BYTE counts in the source
    /// file's own encoding, not character counts -- <paramref name="sourceEncoding"/> must be the
    /// SAME encoding the caller used to decode <paramref name="reader"/>'s underlying bytes into
    /// text, or the byte-count length slicing below misaligns for any non-ASCII content (every
    /// encoding agrees on ASCII's own single-byte-per-character mapping, so this only matters once
    /// accented/non-Latin text is involved). Defaults to UTF-8, matching every existing caller's own
    /// implicit assumption and this codebase's own <c>AdifExporter</c> output encoding.</summary>
    IReadOnlyList<QsoRecord> Import(TextReader reader, Encoding? sourceEncoding = null);
}
