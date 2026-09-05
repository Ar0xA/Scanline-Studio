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

    [Fact]
    public void CompareGuard_RejectsAChangedUsabilityBar()
    {
        // Everything the original guard checked is identical here. Lowering the bar from 30 to 25
        // moves every floor in the run, so a model-and-rate-only guard would have passed this.
        var before = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5);
        var after = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, meanBar: 25.0);

        Assert.Throws<InvalidOperationException>(() => ImpairmentSweepHarness.AssertComparable(before, after));
    }

    [Fact]
    public void CompareGuard_RejectsAChangedMeasurementFilter()
    {
        // Every recorded band power is defined relative to the filter's real response, so a filter
        // edit silently redefines the numbers being diffed.
        var before = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, filterSpecs: ["measurement-H2: 1023 taps"]);
        var after = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, filterSpecs: ["measurement-H2: 511 taps"]);

        Assert.Throws<InvalidOperationException>(() => ImpairmentSweepHarness.AssertComparable(before, after));
    }

    [Fact]
    public void ANaNDelta_RoundTripsInsteadOfThrowing()
    {
        // A point where no seed decodes records NaN. System.Text.Json throws on writing one unless
        // named floating-point literals are allowed, and that write is the per-mode incremental save
        // that exists so a killed sweep still leaves results.
        IReadOnlyList<ImpairmentModeReport> report =
            [new ImpairmentModeReport("robot-36", [new ImpairmentPoint(0.0, double.NaN, false)], NoiseFloorDb: null)];

        var json = JsonSerializer.Serialize(report, ImpairmentSweepHarness.ReportJsonOptions);
        var round = JsonSerializer.Deserialize<List<ImpairmentModeReport>>(json, ImpairmentSweepHarness.ReportJsonOptions)!;

        Assert.True(double.IsNaN(round[0].Points[0].Delta));
    }

    [Fact]
    public void CompareGuard_RejectsAChangedNoiseGenerationVersion()
    {
        // The resampler trim correction shifted every stream by 15 samples while leaving every other
        // recorded property identical, so without this the guard would diff across it.
        var before = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, generation: "v1");
        var after = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, generation: "v2-warmup-trim");

        Assert.Throws<InvalidOperationException>(() => ImpairmentSweepHarness.AssertComparable(before, after));
    }

    [Fact]
    public void CompareGuard_RejectsADifferentCorpus()
    {
        // The identity hash exists so a run against a changed or remounted drive is detectable. The
        // guard is the only place that can act on it.
        var before = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, corpusHash: "E9D66EDCED7A696E");
        var after = Report("paderborn-real-v1", 44100, "H2 400-2500Hz", 5, corpusHash: "0000000000000000");

        Assert.Throws<InvalidOperationException>(() => ImpairmentSweepHarness.AssertComparable(before, after));
    }

    [Fact]
    public void AReportCarryingRetiredFields_StillDeserializes()
    {
        // The two reports already on disk carry the per-mode NoiseStream* members that were replaced
        // by per-seed provenance. Reading them must not throw.
        const string retired = """
            [
              {
                "ModeId": "robot-36",
                "Points": [],
                "NoiseFloorDb": 16.0,
                "NoiseStreamFiles": [ "a.wav" ],
                "NoiseStreamClipRmsSpreadDb": 1.9,
                "NoiseStreamDay": "19_12_04"
              }
            ]
            """;

        var report = JsonSerializer.Deserialize<List<ImpairmentModeReport>>(retired, ImpairmentSweepHarness.ReportJsonOptions)!;

        Assert.Equal("robot-36", report[0].ModeId);
        Assert.Equal(16.0, report[0].NoiseFloorDb);
    }

    private static IReadOnlyList<ImpairmentModeReport> Report(
        string noiseModel, int sampleRate, string calibrationBand, int seedCount,
        double meanBar = 30.0, string floorRule = "test",
        IReadOnlyList<string>? filterSpecs = null, string? generation = null, string? corpusHash = null)
    {
        var run = new ImpairmentRunMetadata(
            noiseModel, sampleRate, calibrationBand, seedCount,
            FloorAxis: calibrationBand,
            FloorRule: floorRule,
            MeanUsableDeltaBar: meanBar,
            PercentileUsableDeltaBar: 60.0,
            FilterSpecs: filterSpecs ?? [],
            NoiseGenerationVersion: generation,
            CorpusIdentityHash: corpusHash);

        return [new ImpairmentModeReport("robot-36", [], NoiseFloorDb: null, Run: run)];
    }
}
