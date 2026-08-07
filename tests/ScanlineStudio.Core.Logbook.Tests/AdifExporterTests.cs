using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class AdifExporterTests
{
    [Fact]
    public void Export_SingleQso_ProducesAValidHeaderAndOneRecord()
    {
        var exporter = new AdifExporter();
        var record = new QsoRecord(
            "1", "n0call",
            new DateTimeOffset(2026, 8, 7, 14, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 7, 14, 35, 0, TimeSpan.Zero),
            14230000, RadioMode.Usb, "martin1", "59", "58",
            "Alice", "Somewhere", "jo31", "Germany", "Nice signal", "img-1");

        var writer = new StringWriter();
        exporter.Export([record], writer, stationCallsign: "w1aw");
        var adif = writer.ToString();

        Assert.Contains("<ADIF_VER:5>3.1.4", adif);
        Assert.Contains("<PROGRAMID:14>ScanlineStudio", adif);
        Assert.Contains("<EOH>", adif);
        Assert.Contains("<CALL:6>N0CALL", adif);
        Assert.Contains("<QSO_DATE:8>20260807", adif);
        Assert.Contains("<TIME_ON:6>143000", adif);
        Assert.Contains("<QSO_DATE_OFF:8>20260807", adif);
        Assert.Contains("<TIME_OFF:6>143500", adif);
        Assert.Contains("<FREQ:9>14.230000", adif);
        Assert.Contains("<MODE:4>SSTV", adif);
        Assert.Contains("<SUBMODE:7>MARTIN1", adif);
        Assert.Contains("<APP_SCANLINESTUDIO_SSTVMODE:7>martin1", adif);
        Assert.Contains("<RST_SENT:2>59", adif);
        Assert.Contains("<RST_RCVD:2>58", adif);
        Assert.Contains("<GRIDSQUARE:4>JO31", adif);
        Assert.Contains("<STATION_CALLSIGN:4>W1AW", adif);
        Assert.Contains("<EOR>", adif);
    }

    [Fact]
    public void Export_NoSstvModeId_FallsBackToRadioModeMapping()
    {
        var exporter = new AdifExporter();
        var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, RadioMode.Cw, null, null, null, null, null, null, null, null, null);

        var writer = new StringWriter();
        exporter.Export([record], writer);

        Assert.Contains("<MODE:2>CW", writer.ToString());
    }

    [Fact]
    public void Export_NullOptionalFields_OmitsTheirTags()
    {
        var exporter = new AdifExporter();
        var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null);

        var writer = new StringWriter();
        exporter.Export([record], writer);
        var adif = writer.ToString();

        Assert.DoesNotContain("MODE", adif);
        Assert.DoesNotContain("FREQ", adif);
        Assert.DoesNotContain("GRIDSQUARE", adif);
        Assert.DoesNotContain("STATION_CALLSIGN", adif);
    }
}
