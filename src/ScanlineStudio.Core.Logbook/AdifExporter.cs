using System.Globalization;
using System.Text;
using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Core.Logbook;

/// <summary>See <see cref="IAdifExporter"/>. <c>MODE</c>/<c>SUBMODE</c> handling: when
/// <see cref="QsoRecord.SstvModeId"/> is set (the expected case for this app), emits ADIF's own
/// <c>MODE=SSTV</c> plus <c>SUBMODE</c> (uppercased id) for third-party interop, and a
/// non-standard <c>APP_SCANLINESTUDIO_SSTVMODE</c> field carrying the exact lowercase id so
/// <see cref="AdifImporter"/> can recover it losslessly rather than guess from <c>SUBMODE</c>'s
/// casing. Field length is a UTF-8 <b>byte</b> count, per ADIF's own <c>&lt;name:length&gt;</c>
/// definition — not <see cref="string.Length"/> (UTF-16 char count) — <see cref="AdifImporter"/>
/// slices on the matching byte count, so a non-ASCII <c>NAME</c>/<c>QTH</c>/<c>COMMENT</c> round-
/// trips correctly and, just as importantly, the single-QSO payload this feeds to
/// <c>AdifUdpStreamer</c>'s <c>LoggedADIF</c> UDP message stays byte-exact (a wrong length
/// there corrupts the datagram with no error surfaced on either end).</summary>
public sealed class AdifExporter : IAdifExporter
{
    private const string ProgramId = "ScanlineStudio";
    private const string AdifVersion = "3.1.4";

    public void Export(IEnumerable<QsoRecord> records, TextWriter writer, string? stationCallsign = null)
    {
        WriteField(writer, "ADIF_VER", AdifVersion);
        WriteField(writer, "PROGRAMID", ProgramId);
        writer.Write("<EOH>\n");

        foreach (var record in records)
        {
            WriteRecord(writer, record, stationCallsign);
            writer.Write("<EOR>\n");
        }
    }

    private static void WriteRecord(TextWriter writer, QsoRecord record, string? stationCallsign)
    {
        WriteField(writer, "CALL", record.Callsign.ToUpperInvariant());
        WriteField(writer, "QSO_DATE", record.StartUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        WriteField(writer, "TIME_ON", record.StartUtc.UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture));

        if (record.EndUtc is { } endUtc)
        {
            WriteField(writer, "QSO_DATE_OFF", endUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            WriteField(writer, "TIME_OFF", endUtc.UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture));
        }

        if (record.FrequencyHz is { } frequencyHz)
        {
            WriteField(writer, "FREQ", (frequencyHz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture));
        }

        if (record.SstvModeId is { } sstvModeId)
        {
            WriteField(writer, "MODE", "SSTV");
            WriteField(writer, "SUBMODE", sstvModeId.ToUpperInvariant());
            WriteField(writer, "APP_SCANLINESTUDIO_SSTVMODE", sstvModeId);
        }
        else if (record.Mode is { } mode && AdifRadioModeMapping.ToAdif(mode) is { } adifMode)
        {
            WriteField(writer, "MODE", adifMode);
        }

        WriteField(writer, "RST_SENT", record.RstSent);
        WriteField(writer, "RST_RCVD", record.RstReceived);
        WriteField(writer, "NAME", record.Name);
        WriteField(writer, "QTH", record.Qth);
        WriteField(writer, "GRIDSQUARE", record.GridSquare?.ToUpperInvariant());
        WriteField(writer, "COUNTRY", record.Country);
        WriteField(writer, "COMMENT", record.Notes);
        WriteField(writer, "STATION_CALLSIGN", stationCallsign?.ToUpperInvariant());
    }

    private static void WriteField(TextWriter writer, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        writer.Write('<');
        writer.Write(name);
        writer.Write(':');
        writer.Write(Encoding.UTF8.GetByteCount(value));
        writer.Write('>');
        writer.Write(value);
        writer.Write('\n');
    }
}
