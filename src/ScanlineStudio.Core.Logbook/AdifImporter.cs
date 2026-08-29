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
    /// <summary>Fields consumed unconditionally, regardless of value. <c>MODE</c>/<c>SUBMODE</c>/
    /// <c>APP_SCANLINESTUDIO_SSTVMODE</c>/<c>APP_SCANLINESTUDIO_RADIOMODE</c>/<c>QSL_SENT</c>/
    /// <c>QSL_RCVD</c> are deliberately NOT here — they're only excluded from the unmapped-fields
    /// bag (<see cref="BuildNotes"/>) when <see cref="MapFields"/> actually recovers a value from
    /// them, so an unrecognized <c>MODE</c> token (e.g. a third-party <c>MODE=SSTV</c> with no
    /// submode/app field, an unrecognized <c>SUBMODE</c> alongside a recognized <c>MODE</c>, or any
    /// token <see cref="AdifRadioModeMapping.FromAdif"/> doesn't recognize) survives in
    /// <c>COMMENT</c> instead of being silently dropped -- and, same reasoning, a
    /// <c>QSL_SENT</c>/<c>QSL_RCVD</c> value this app's own plain-boolean model can't represent
    /// (ADIF's full enumeration also has <c>N</c>/<c>R</c>/<c>Q</c>/<c>I</c>, not just <c>Y</c>)
    /// survives the same way instead of silently downgrading another logger's real data to "false"
    /// on a round-trip export.</summary>
    private static readonly HashSet<string> MappedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALL", "QSO_DATE", "TIME_ON", "QSO_DATE_OFF", "TIME_OFF", "FREQ",
        "RST_SENT", "RST_RCVD", "NAME", "QTH", "GRIDSQUARE",
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
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteLength) || byteLength < 0)
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
        var consumedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fields.TryGetValue("MODE", out var modeValue))
        {
            if (modeValue.Equals("SSTV", StringComparison.OrdinalIgnoreCase))
            {
                var recoveredSomething = false;

                if (fields.TryGetValue("APP_SCANLINESTUDIO_SSTVMODE", out var appMode))
                {
                    sstvModeId = appMode;
                    consumedFields.Add("APP_SCANLINESTUDIO_SSTVMODE");
                    if (fields.ContainsKey("SUBMODE"))
                    {
                        // AdifExporter always writes SUBMODE alongside APP_SCANLINESTUDIO_SSTVMODE for
                        // third-party interop even though this branch doesn't need it to recover
                        // sstvModeId -- still a recognized, expected companion field, not an unmapped one.
                        consumedFields.Add("SUBMODE");
                    }
                    recoveredSomething = true;
                }
                else if (fields.TryGetValue("SUBMODE", out var subMode))
                {
                    sstvModeId = subMode.ToLowerInvariant();
                    consumedFields.Add("SUBMODE");
                    recoveredSomething = true;
                }

                // MODE=SSTV carries no RF-sideband info of its own (ADIF has no MODE+MODE pairing), so
                // the RadioMode that was ALSO set on export (QsoRecord.Mode and SstvModeId are
                // independent, both-optional fields -- see spec/08-logging.md's "auto-fill" section)
                // rides in this app-specific escape-hatch field instead, mirroring how
                // APP_SCANLINESTUDIO_SSTVMODE already preserves SstvModeId losslessly rather than
                // relying on SUBMODE's lossy uppercase form.
                if (fields.TryGetValue("APP_SCANLINESTUDIO_RADIOMODE", out var radioModeValue) &&
                    Enum.TryParse<RadioMode>(radioModeValue, ignoreCase: true, out var parsedRadioMode))
                {
                    mode = parsedRadioMode;
                    consumedFields.Add("APP_SCANLINESTUDIO_RADIOMODE");
                    recoveredSomething = true;
                }

                if (recoveredSomething)
                {
                    consumedFields.Add("MODE");
                }
                // else: MODE=SSTV present but nothing recoverable from it -- leave MODE unconsumed so
                // "MODE=SSTV" itself survives via BuildNotes instead of vanishing.
            }
            else
            {
                fields.TryGetValue("SUBMODE", out var submodeValue);
                var (resolved, submodeRecognized) = AdifRadioModeMapping.FromAdif(modeValue, submodeValue);
                mode = resolved;
                if (resolved != RadioMode.Unknown)
                {
                    consumedFields.Add("MODE");
                    if (submodeRecognized)
                    {
                        consumedFields.Add("SUBMODE");
                    }
                }
            }
        }

        // ui_transition_plan.md step 15, piece (b): only a Y/y (QSL_SENT) or Y/y/V/v (QSL_RCVD --
        // ADIF's "Verified" value also means received) value is consumed and mapped to true. Any
        // other value (N/R/Q/I, or an unrecognized token) is left UNCONSUMED so it survives via
        // BuildNotes's unmapped-fields bag instead of being silently downgraded to false -- see
        // MappedFields's own doc comment for why these two fields aren't in that static set.
        var qslSent = false;
        if (fields.TryGetValue("QSL_SENT", out var qslSentValue) && qslSentValue.StartsWith("Y", StringComparison.OrdinalIgnoreCase))
        {
            qslSent = true;
            consumedFields.Add("QSL_SENT");
        }

        var qslReceived = false;
        if (fields.TryGetValue("QSL_RCVD", out var qslReceivedValue)
            && (qslReceivedValue.StartsWith("Y", StringComparison.OrdinalIgnoreCase) || qslReceivedValue.StartsWith("V", StringComparison.OrdinalIgnoreCase)))
        {
            qslReceived = true;
            consumedFields.Add("QSL_RCVD");
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
            Notes: BuildNotes(fields, consumedFields),
            ReceivedImageId: null,
            QslSent: qslSent,
            QslReceived: qslReceived);
    }

    /// <summary>Preserves anything not mapped onto a <see cref="QsoRecord"/> property (spec's
    /// "raw-fields bag" requirement) by appending it to <c>COMMENT</c> rather than dropping it.
    /// <paramref name="consumedFields"/> is the caller's record-specific set of field names it
    /// actually recovered a value from -- MODE-family fields (<c>MODE</c>/<c>SUBMODE</c>/
    /// <c>APP_SCANLINESTUDIO_SSTVMODE</c>/<c>APP_SCANLINESTUDIO_RADIOMODE</c>) and QSL fields
    /// (<c>QSL_SENT</c>/<c>QSL_RCVD</c>) alike -- see <see cref="MappedFields"/>'s own doc comment
    /// for why those six fields aren't in the static set.</summary>
    private static string? BuildNotes(Dictionary<string, string> fields, HashSet<string> consumedFields)
    {
        var comment = fields.GetValueOrDefault("COMMENT");
        var unmapped = fields.Where(kv => !MappedFields.Contains(kv.Key) && !consumedFields.Contains(kv.Key))
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
