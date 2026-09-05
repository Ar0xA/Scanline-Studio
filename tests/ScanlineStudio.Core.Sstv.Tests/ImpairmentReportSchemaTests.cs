using System.Text.Json;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Schema and comparability guards for impairment reports. Both exist to stop a reader --
/// human or test -- from putting two numbers side by side that do not mean the same thing.</summary>
public sealed class ImpairmentReportSchemaTests
{
    private const string OldReportJson = """
        [
          {
            "ModeId": "robot-36",
            "Points": [ { "SnrDb": 20.0, "Delta": 4.5, "DecodedCorrectly": true } ],
            "NoiseFloorDb": 6.0
          }
        ]
        """;

    [Fact]
    public void AnOldReport_DeserializesWithNullNewFields_NotZero()
    {
        var report = JsonSerializer.Deserialize<List<ImpairmentModeReport>>(OldReportJson, ImpairmentSweepHarness.ReportJsonOptions)!;

        var point = report[0].Points[0];
        Assert.Null(report[0].Run);
        // 0.0 is a perfectly plausible dB reading, so a non-nullable double here would present a
        // missing field as a real measurement. That is why every new numeric field is double?.
        Assert.Null(point.TotalBandSnrDb);
        Assert.Null(point.InBandSnrDb);
        Assert.Null(point.NoiseBandPowerH1Db);
        Assert.Null(point.NoiseBandPowerH2Db);
        Assert.Null(point.NoiseBandPowerH3Db);
        Assert.Null(point.NoiseBandPowerTotalDb);
        Assert.Null(point.PerLineDelta95);
        Assert.Null(point.UsableByPercentile);
        Assert.Null(point.SeedOutcomes);
        Assert.Null(report[0].NoiseStreamClipRmsSpreadDb);

        // The fields that DO carry a value still round-trip.
        Assert.Equal(20.0, point.SnrDb);
        Assert.Equal(4.5, point.Delta);
        Assert.True(point.DecodedCorrectly);
        Assert.Equal(6.0, report[0].NoiseFloorDb);
    }

    [Fact]
    public void CompareGuard_AcceptsTwoOldReports()
    {
        var report = JsonSerializer.Deserialize<List<ImpairmentModeReport>>(OldReportJson, ImpairmentSweepHarness.ReportJsonOptions)!;

        // Both sides predate the run metadata, so both are awgn-v1 at 44100Hz on the total band.
        ImpairmentSweepHarness.AssertComparable(report, report);
    }

    [Theory]
    [InlineData("paderborn-real-v1", 44100, "H2 400-2500Hz", 5)]        // different model
    [InlineData("awgn-v1", 48000, "total-band", 2)]                      // different rate
    [InlineData("awgn-v1", 44100, "H2 400-2500Hz", 2)]                   // different calibration band
    [InlineData("awgn-v1", 44100, "total-band", 5)]                      // different seed count
    public void CompareGuard_RejectsAnyMismatchInTheComparabilityTuple(
        string noiseModel, int sampleRate, string calibrationBand, int seedCount)
    {
        var before = Report("awgn-v1", 44100, "total-band", 2);
        var after = Report(noiseModel, sampleRate, calibrationBand, seedCount);

        var ex = Assert.Throws<InvalidOperationException>(() => ImpairmentSweepHarness.AssertComparable(before, after));

        Assert.Contains("not on the same footing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareGuard_RejectsASeedCountBumpWithinTheSameModel()
    {
        // The planned follow-up "5 realizations is not enough, raise it" leaves the model tag
        // unchanged, so a model-only guard would pass and diff two different floor estimators.
        var before = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5);
        var after = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 9);

        Assert.Throws<InvalidOperationException>(() => ImpairmentSweepHarness.AssertComparable(before, after));
    }

    [Fact]
    public void CompareGuard_AcceptsTwoRunsOnTheSameFooting()
    {
        var before = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5);
        var after = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5);

        ImpairmentSweepHarness.AssertComparable(before, after);
    }

    private static IReadOnlyList<ImpairmentModeReport> Report(string noiseModel, int sampleRate, string calibrationBand, int seedCount)
    {
        var run = new ImpairmentRunMetadata(
            noiseModel, sampleRate, calibrationBand, seedCount,
            FloorAxis: calibrationBand,
            FloorRule: "test",
            MeanUsableDeltaBar: 30.0,
            PercentileUsableDeltaBar: 60.0,
            FilterSpecs: []);

        return [new ImpairmentModeReport("robot-36", [], NoiseFloorDb: null, Run: run)];
    }
}
