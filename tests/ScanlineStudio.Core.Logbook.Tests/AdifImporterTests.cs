using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class AdifImporterTests
{
    [Theory]
    [InlineData("<EOH>")]
    [InlineData("é<eOh><EOR>")]
    public void Import_HeaderlessCommentTreatsDelimitersAsData(string comment)
    {
        var length = System.Text.Encoding.UTF8.GetByteCount(comment);
        var adif = $"<CALL:5>PA0AA<QSO_DATE:8>20260907<TIME_ON:4>1200<COMMENT:{length}:S>{comment}<EOR>";
        var record = Assert.Single(new AdifImporter().Import(new StringReader(adif)));
        Assert.Equal("PA0AA", record.Callsign);
        Assert.Equal(comment, record.Notes);
    }

    [Fact]
    public void Import_HeaderFieldSkipsLiteralDelimitersUntilRealHeaderEnd()
    {
        const string value = "é<EOH><EOR><CALL:3>BAD";
        var length = System.Text.Encoding.UTF8.GetByteCount(value);
        var adif = $"Header<USERDEF:{length}:S>{value}<eOh><CALL:5>PA0AA<QSO_DATE:8>20260907<TIME_ON:4>1200<EOR>";
        Assert.Equal("PA0AA", Assert.Single(new AdifImporter().Import(new StringReader(adif))).Callsign);
    }

    [Fact]
    public void Import_HeaderlessRecordBeforeStrayHeaderEndIsRetained()
    {
        const string record = "<CALL:5>PA0AA<QSO_DATE:8>20260907<TIME_ON:4>1200<EOR>";
        Assert.Equal(2, new AdifImporter().Import(new StringReader(record + "<EOH>" + record)).Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("no-length")]
    [InlineData("2147483648")]
    public void Import_HeaderScanMatchesMalformedAndZeroLengthRecordRules(string length)
    {
        var adif = $"<COMMENT:{length}><CALL:5>PA0AA<QSO_DATE:8>20260907<TIME_ON:4>1200<EOR>";
        Assert.Equal("PA0AA", Assert.Single(new AdifImporter().Import(new StringReader(adif))).Callsign);
    }

    [Fact]
    public void Import_TruncatedHeaderValueDoesNotFindEmbeddedHeaderEnd()
    {
        Assert.Empty(new AdifImporter().Import(new StringReader("<COMMENT:2147483647><EOH><CALL:5>PA0AA<EOR>")));
    }

    [Fact]
    public void Export_ThenImport_RoundTripsTheRecord()
    {
        var exporter = new AdifExporter();
        var importer = new AdifImporter();
        var original = new QsoRecord(
            "1", "N0CALL",
            new DateTimeOffset(2026, 8, 7, 14, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 7, 14, 35, 0, TimeSpan.Zero),
            14230000, RadioMode.Usb, "martin1", "59", "58",
            "Alice", "Somewhere", "JO31", "Germany", "Nice signal", null, QslSent: true, QslReceived: true);

        var writer = new StringWriter();
        exporter.Export([original], writer);
        var imported = Assert.Single(importer.Import(new StringReader(writer.ToString())));

        Assert.Equal(original.Callsign, imported.Callsign);
        Assert.Equal(original.StartUtc, imported.StartUtc);
        Assert.Equal(original.EndUtc, imported.EndUtc);
        Assert.Equal(original.FrequencyHz, imported.FrequencyHz);
        Assert.Equal(original.Mode, imported.Mode); // recovered via APP_SCANLINESTUDIO_RADIOMODE, not MODE
        Assert.Equal(original.SstvModeId, imported.SstvModeId);
        Assert.Equal(original.RstSent, imported.RstSent);
        Assert.Equal(original.RstReceived, imported.RstReceived);
        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(original.Qth, imported.Qth);
        Assert.Equal(original.GridSquare, imported.GridSquare);
        Assert.Equal(original.Country, imported.Country);
        Assert.Equal(original.Notes, imported.Notes);
        Assert.True(imported.QslSent);
        Assert.True(imported.QslReceived);
    }

    [Fact]
    public void Import_QslRcvdVerified_MapsToTrue()
    {
        // ui_transition_plan.md step 15, piece (b): ADIF's QSL_RCVD enumeration includes "V"
        // (Verified), which also means received -- not just "Y".
        var importer = new AdifImporter();
        var adif = "<CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<QSL_RCVD:1>V<EOR>\n";

        var imported = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.True(imported.QslReceived);
    }

    [Fact]
    public void Import_QslSentOrRcvdNotY_DoesNotMapToTrue_PreservesTheRawValueInNotes()
    {
        // Round-2 plan-review decision: a value this app's plain-boolean model can't represent
        // (N/R/Q/I) must survive on round-trip, not silently downgrade to false-as-if-unknown.
        var importer = new AdifImporter();
        var adif = "<CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<QSL_SENT:1>R<QSL_RCVD:1>N<EOR>\n";

        var imported = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.False(imported.QslSent);
        Assert.False(imported.QslReceived);
        Assert.Contains("QSL_SENT=R", imported.Notes);
        Assert.Contains("QSL_RCVD=N", imported.Notes);
    }

    [Fact]
    public void Export_ThenImport_NonSstvRadioMode_RoundTripsMode()
    {
        var exporter = new AdifExporter();
        var importer = new AdifImporter();
        var original = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, RadioMode.Fm, null, null, null, null, null, null, null, null, null, false, false);

        var writer = new StringWriter();
        exporter.Export([original], writer);
        var imported = Assert.Single(importer.Import(new StringReader(writer.ToString())));

        Assert.Equal(RadioMode.Fm, imported.Mode);
        Assert.Null(imported.SstvModeId);
    }

    [Fact]
    public void Import_RealWorldShapedThirdPartyFile_ParsesEveryRecordAndPreservesUnmappedFields()
    {
        // Shaped like a real-world export from a generic third-party logger: a free-text header
        // comment before the tags (common in the wild, not just our own writer's style), fields
        // back-to-back with no whitespace on one line, a contest-logger-only field (STX) this app
        // has no QsoRecord property for, and CRLF line endings.
        const string adif = "Generated by Generic Logger v3\r\n" +
            "<ADIF_VER:5>3.1.0<PROGRAMID:13>GenericLogger<EOH>\r\n" +
            "<call:4>W1AW<qso_date:8>20250115<time_on:4>1230<band:3>20m<mode:3>SSB<rst_sent:2>59<rst_rcvd:2>59<gridsquare:6>FN31pr<stx:1>5<eor>\r\n" +
            "<CALL:6>DL2QSK<QSO_DATE:8>20250116<TIME_ON:6>081500<MODE:4>SSTV<SUBMODE:8>SCOTTIE1<COMMENT:9>Good copy<EOR>\r\n";

        var importer = new AdifImporter();
        var records = importer.Import(new StringReader(adif));

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal("W1AW", first.Callsign);
        Assert.Equal(new DateTimeOffset(2025, 1, 15, 12, 30, 0, TimeSpan.Zero), first.StartUtc);
        Assert.Equal(RadioMode.Usb, first.Mode); // bare MODE=SSB with no SUBMODE defaults to Usb
        Assert.Equal("FN31pr", first.GridSquare);
        Assert.NotNull(first.Notes);
        Assert.Contains("STX=5", first.Notes);
        Assert.Contains("BAND=20m", first.Notes);

        var second = records[1];
        Assert.Equal("DL2QSK", second.Callsign);
        Assert.Equal("scottie1", second.SstvModeId);
        Assert.Equal("Good copy", second.Notes);
    }

    [Theory]
    [InlineData("SSB", "LSB", RadioMode.Lsb)]
    [InlineData("SSB", "USB", RadioMode.Usb)]
    [InlineData("SSB", null, RadioMode.Usb)] // no submode defaults to Usb, the far more common sideband
    [InlineData("PKT", null, RadioMode.Pkt)]
    [InlineData("PKTUSB", null, RadioMode.Pkt)] // Hamlib's non-standard token, accepted for backward compat
    [InlineData("CW", null, RadioMode.Cw)]
    [InlineData("RTTY", null, RadioMode.Rtty)]
    public void Import_ModeAndSubmode_MapsToExpectedRadioMode(string adifMode, string? adifSubmode, RadioMode expected)
    {
        var submodeTag = adifSubmode is null ? "" : $"<SUBMODE:{adifSubmode.Length}>{adifSubmode}";
        var adif = $"<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<MODE:{adifMode.Length}>{adifMode}{submodeTag}<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal(expected, record.Mode);
    }

    [Fact]
    public void Import_SsbWithUnrecognizedSubmode_DefaultsToUsbButPreservesSubmodeInNotes()
    {
        const string adif = "<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<MODE:3>SSB<SUBMODE:5>DIGIU<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal(RadioMode.Usb, record.Mode); // unrecognized submode falls back to the documented default
        Assert.NotNull(record.Notes);
        Assert.Contains("SUBMODE=DIGIU", record.Notes); // but the unrecognized submode itself is not lost
    }

    [Fact]
    public void Export_ThenImport_SstvRecordWithRadioMode_RoundTripsBothModeAndSstvModeId()
    {
        var exporter = new AdifExporter();
        var importer = new AdifImporter();
        var original = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, RadioMode.Lsb, "scottie2", null, null, null, null, null, null, null, null, false, false);

        var writer = new StringWriter();
        exporter.Export([original], writer);
        var imported = Assert.Single(importer.Import(new StringReader(writer.ToString())));

        Assert.Equal(RadioMode.Lsb, imported.Mode);
        Assert.Equal("scottie2", imported.SstvModeId);
    }

    [Fact]
    public void Import_SstvModeWithNoRadioModeAppField_LeavesModeNull()
    {
        const string adif = "<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<MODE:4>SSTV<SUBMODE:7>MARTIN1<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Null(record.Mode);
        Assert.Equal("martin1", record.SstvModeId);
    }

    [Fact]
    public void Import_UnrecognizedModeToken_PreservedInNotesNotSilentlyDropped()
    {
        const string adif = "<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<MODE:5>DSTAR<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal(RadioMode.Unknown, record.Mode);
        Assert.NotNull(record.Notes);
        Assert.Contains("MODE=DSTAR", record.Notes);
    }

    [Fact]
    public void Import_SstvModeWithNoSubmodeOrAppField_PreservesRawModeInNotes()
    {
        const string adif = "<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<MODE:4>SSTV<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Null(record.SstvModeId);
        Assert.NotNull(record.Notes);
        Assert.Contains("MODE=SSTV", record.Notes);
    }

    [Fact]
    public void Export_ThenImport_UsbLsbAndPktModes_RoundTrip()
    {
        var exporter = new AdifExporter();
        var importer = new AdifImporter();

        foreach (var mode in new[] { RadioMode.Usb, RadioMode.Lsb, RadioMode.Pkt })
        {
            var original = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, mode, null, null, null, null, null, null, null, null, null, false, false);
            var writer = new StringWriter();
            exporter.Export([original], writer);
            var imported = Assert.Single(importer.Import(new StringReader(writer.ToString())));

            Assert.Equal(mode, imported.Mode);
        }
    }

    [Fact]
    public void Import_NegativeFieldLength_SkipsTagInsteadOfThrowing()
    {
        // NumberStyles.Integer accepts a leading '-'; a malformed <CALL:-5> must be skipped per this
        // class's own "malformed tag is skipped, not aborting" contract, not throw on the negative slice.
        const string adif = "<EOH><CALL:-5>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143000<CALL:6>N0CALL<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal("N0CALL", record.Callsign);
    }

    [Fact]
    public void Import_MissingRequiredCallField_ThrowsFormatException()
    {
        const string adif = "<ADIF_VER:5>3.1.0<EOH><QSO_DATE:8>20250115<TIME_ON:4>1230<EOR>";

        var importer = new AdifImporter();

        Assert.Throws<FormatException>(() => importer.Import(new StringReader(adif)));
    }

    [Fact]
    public void Import_TimeOnWithSeconds_ParsesCorrectly()
    {
        const string adif = "<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:6>143045<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal(new DateTimeOffset(2026, 8, 7, 14, 30, 45, TimeSpan.Zero), record.StartUtc);
    }

    [Fact]
    public void Import_NonAsciiFieldValue_SlicesByUtf8ByteCountNotCharCount()
    {
        // Test-suite fixes phase 1, item 6: this class's own byte-exact-slicing invariant
        // (AdifImporter's own doc comment) previously had no test exercising a non-ASCII field
        // value. "Jörg" is 4 chars but 5 UTF-8 bytes ('ö' is 2 bytes) -- <NAME:5> is therefore
        // correct ADIF, not a malformed length. Characterization test: this already passes today.
        // The CALLSIGN assertion is what actually detects a char-count-instead-of-byte-count bug --
        // a wrong slice here would misalign every field parsed after NAME, corrupting CALL too.
        const string adif = "<EOH><NAME:5>Jörg<CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:4>1200<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal("Jörg", record.Name);
        Assert.Equal("N0CALL", record.Callsign);
    }

    [Theory]
    [InlineData("2026", "143000")] // short date (4 chars, needs 8)
    [InlineData("20260807", "1")] // short time (1 char, needs 4 or 6)
    [InlineData("20269999", "143000")] // out-of-range month (99) -- previously threw ArgumentOutOfRangeException, not FormatException
    [InlineData("abcdefgh", "143000")] // non-digit date
    public void Import_MalformedDateOrTime_ThrowsFormatException(string date, string time)
    {
        // Test-suite fixes phase 1, item 6: ParseDateTime's previous unguarded AsSpan slicing threw
        // ArgumentOutOfRangeException for the first 3 cases here (a length-only guard would not have
        // caught the out-of-range-month case) -- every OTHER malformed-ADIF-input path in this class
        // throws FormatException (see Import_MissingRequiredCallField_ThrowsFormatException above),
        // so this was a real inconsistency, not a hypothetical one.
        var adif = $"<EOH><CALL:6>N0CALL<QSO_DATE:{date.Length}>{date}<TIME_ON:{time.Length}>{time}<EOR>";

        var importer = new AdifImporter();

        Assert.Throws<FormatException>(() => importer.Import(new StringReader(adif)));
    }

    [Theory]
    [InlineData("1200")] // 4-digit HHmm -- mandatory, real ADIF files routinely omit seconds
    [InlineData("120045")] // 6-digit HHmmss
    public void Import_MandatoryTimeFormats_StillParseCorrectly(string time)
    {
        var adif = $"<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:{time.Length}>{time}<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal(12, record.StartUtc.Hour);
        Assert.Equal(0, record.StartUtc.Minute);
    }

    [Fact]
    public void Import_FiveCharTime_NarrowedFromSilentTruncation_ThrowsFormatException()
    {
        // Test-suite fixes phase 1, item 6: previously silently accepted (any string >= 4 chars) and
        // treated as HHmm, ignoring the trailing digit -- a deliberate acceptance narrowing to
        // ADIF's own defined 4- and 6-digit time formats specifically. Documented here as a
        // conscious behavior change, not an incidental one.
        const string adif = "<EOH><CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:5>14300<EOR>";

        var importer = new AdifImporter();

        Assert.Throws<FormatException>(() => importer.Import(new StringReader(adif)));
    }

    [Fact]
    public void Import_NoHeaderAtAll_StillParsesRecords()
    {
        const string adif = "<CALL:6>N0CALL<QSO_DATE:8>20260807<TIME_ON:4>1200<EOR>";

        var importer = new AdifImporter();
        var record = Assert.Single(importer.Import(new StringReader(adif)));

        Assert.Equal("N0CALL", record.Callsign);
    }
}
