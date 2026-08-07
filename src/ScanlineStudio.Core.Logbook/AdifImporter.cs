using System.Globalization;
using System.Text;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook;

/// <summary>See <see cref="IAdifImporter"/>. Reads the whole input up front (<c>ReadToEnd</c>),
/// re-encodes it as UTF-8 bytes, and works with byte-offset slicing rather than incremental
/// <see cref="TextReader"/> reads or char-index slicing — ADIF's own field framing
/// (<c>&lt;name:length&gt;</c> followed by exactly <c>length</c> UTF-8 <b>bytes</b>, which may
/// themselves contain <c>&lt;</c>/newlines) requires byte-exact slicing to correctly parse a
/// non-ASCII value (matches <see cref="AdifExporter"/>'s own byte-count field lengths). Scanning
/// for the ASCII delimiter bytes <c>&lt;</c>/<c>&gt;</c> directly against the UTF-8 byte array is
/// safe even though the surrounding value bytes may be multi-byte — a core UTF-8 property is that
/// no continuation byte of a multi-byte sequence ever equals an ASCII byte value.</summary>
public sealed class AdifImporter : IAdifImporter
{
    private static readonly HashSet<string> MappedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALL", "QSO_DATE", "TIME_ON", "QSO_DATE_OFF", "TIME_OFF", "FREQ", "MODE", "SUBMODE",
        "APP_SCANLINESTUDIO_SSTVMODE", "RST_SENT", "RST_RCVD", "NAME", "QTH", "GRIDSQUARE",
        "COUNTRY", "COMMENT", "STATION_CALLSIGN",
    };

    public IReadOnlyList<QsoRecord> Import(TextReader reader)
    {
        var bytes = Encoding.UTF8.GetBytes(reader.ReadToEnd());
        var position = SkipHeader(bytes);
        var records = new List<QsoRecord>();

        while (position < bytes.Length)
        {
            var (fields, nextPosition, foundEor) = ParseRecord(bytes, position);
            position = nextPosition;
            if (!foundEor)
            {
                break;
            }

            records.Add(MapFields(fields));
        }

        return records;
    }

    /// <summary>Scans for the first <c>&lt;eoh&gt;</c> tag (case-insensitive, like every other ADIF
    /// tag) using the same tag-scanning logic as <see cref="ParseRecord"/> rather than a fixed
    /// ASCII substring search — a real header could in principle spell it any case.</summary>
    private static int SkipHeader(byte[] bytes)
    {
        var position = 0;
        while (true)
        {
            var tagStart = Array.IndexOf(bytes, (byte)'<', position);
            if (tagStart < 0)
            {
                return 0;
            }

            var tagEnd = Array.IndexOf(bytes, (byte)'>', tagStart);
            if (tagEnd < 0)
            {
                return 0;
            }

            var tagContent = Encoding.UTF8.GetString(bytes, tagStart + 1, tagEnd - tagStart - 1);
            if (tagContent.Equals("eoh", StringComparison.OrdinalIgnoreCase))
            {
                return tagEnd + 1;
            }

            position = tagEnd + 1;
        }
    }

    /// <summary>Scans one record's fields starting at <paramref name="position"/> (a byte offset),
    /// stopping at the next <c>&lt;eor&gt;</c> marker. A field tag with no <c>:length</c> part
    /// (malformed, or a stray header-only tag like a repeated <c>&lt;eoh&gt;</c>) is skipped rather
    /// than aborting the whole import.</summary>
    private static (Dictionary<string, string> Fields, int NextPosition, bool FoundEor) ParseRecord(byte[] bytes, int position)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var tagStart = Array.IndexOf(bytes, (byte)'<', position);
            if (tagStart < 0)
            {
                return (fields, bytes.Length, false);
            }

            var tagEnd = Array.IndexOf(bytes, (byte)'>', tagStart);
            if (tagEnd < 0)
            {
                return (fields, bytes.Length, false);
            }

            var tagContent = Encoding.UTF8.GetString(bytes, tagStart + 1, tagEnd - tagStart - 1);
            position = tagEnd + 1;

            if (tagContent.Equals("eor", StringComparison.OrdinalIgnoreCase))
            {
                return (fields, position, true);
            }

            var parts = tagContent.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteLength))
            {
                continue;
            }

            byteLength = Math.Min(byteLength, bytes.Length - position);
            var value = Encoding.UTF8.GetString(bytes, position, byteLength);
            position += byteLength;

            if (byteLength > 0)
            {
                fields[parts[0].ToUpperInvariant()] = value;
            }
        }
    }

    private static QsoRecord MapFields(Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue("CALL", out var callsign) || string.IsNullOrWhiteSpace(callsign))
        {
            throw new FormatException("ADIF record is missing required field CALL.");
        }

        if (!fields.TryGetValue("QSO_DATE", out var qsoDate))
        {
            throw new FormatException($"ADIF record for {callsign} is missing required field QSO_DATE.");
        }

        if (!fields.TryGetValue("TIME_ON", out var timeOn))
        {
            throw new FormatException($"ADIF record for {callsign} is missing required field TIME_ON.");
        }

        var startUtc = ParseDateTime(qsoDate, timeOn);

        DateTimeOffset? endUtc = null;
        if (fields.TryGetValue("TIME_OFF", out var timeOff))
        {
            var dateOff = fields.GetValueOrDefault("QSO_DATE_OFF", qsoDate);
            endUtc = ParseDateTime(dateOff, timeOff);
        }

        long? frequencyHz = null;
        if (fields.TryGetValue("FREQ", out var freq) &&
            double.TryParse(freq, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqMhz))
        {
            frequencyHz = (long)Math.Round(freqMhz * 1_000_000.0, MidpointRounding.AwayFromZero);
        }

        string? sstvModeId = null;
        RadioMode? mode = null;
        if (fields.TryGetValue("MODE", out var modeValue))
        {
            if (modeValue.Equals("SSTV", StringComparison.OrdinalIgnoreCase))
            {
                sstvModeId = fields.TryGetValue("APP_SCANLINESTUDIO_SSTVMODE", out var appMode)
                    ? appMode
                    : fields.TryGetValue("SUBMODE", out var subMode) ? subMode.ToLowerInvariant() : null;
            }
            else
            {
                mode = AdifRadioModeMapping.FromAdif(modeValue);
            }
        }

        return new QsoRecord(
            Id: Guid.NewGuid().ToString(),
            Callsign: callsign,
            StartUtc: startUtc,
            EndUtc: endUtc,
            FrequencyHz: frequencyHz,
            Mode: mode,
            SstvModeId: sstvModeId,
            RstSent: fields.GetValueOrDefault("RST_SENT"),
            RstReceived: fields.GetValueOrDefault("RST_RCVD"),
            Name: fields.GetValueOrDefault("NAME"),
            Qth: fields.GetValueOrDefault("QTH"),
            GridSquare: fields.GetValueOrDefault("GRIDSQUARE"),
            Country: fields.GetValueOrDefault("COUNTRY"),
            Notes: BuildNotes(fields),
            ReceivedImageId: null);
    }

    /// <summary>Preserves anything not mapped onto a <see cref="QsoRecord"/> property (spec's
    /// "raw-fields bag" requirement) by appending it to <c>COMMENT</c> rather than dropping it.</summary>
    private static string? BuildNotes(Dictionary<string, string> fields)
    {
        var comment = fields.GetValueOrDefault("COMMENT");
        var unmapped = fields.Where(kv => !MappedFields.Contains(kv.Key))
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();

        if (unmapped.Count == 0)
        {
            return comment;
        }

        var unmappedText = $"[unmapped: {string.Join("; ", unmapped)}]";
        return string.IsNullOrEmpty(comment) ? unmappedText : $"{comment}\n{unmappedText}";
    }

    private static DateTimeOffset ParseDateTime(string date, string time)
    {
        var year = int.Parse(date.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var month = int.Parse(date.AsSpan(4, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(date.AsSpan(6, 2), CultureInfo.InvariantCulture);
        var hour = int.Parse(time.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var minute = int.Parse(time.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var second = time.Length >= 6 ? int.Parse(time.AsSpan(4, 2), CultureInfo.InvariantCulture) : 0;
        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
    }
}
