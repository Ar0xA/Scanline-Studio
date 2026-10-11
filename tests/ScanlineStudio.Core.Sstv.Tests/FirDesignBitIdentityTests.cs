namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Pins every production FIR designer (<see cref="SearchBandpassFilter.MakeFilter"/>,
/// <see cref="TxOutputBandpassFilter.MakeFilter"/>, <see cref="NotchFilter.MakeFilter"/>) bit-for-bit
/// to <see cref="FrozenFirDesignOracle"/>, compared with <see cref="BitConverter.DoubleToInt64Bits"/>
/// on every element, plus array length and the untouched zero trailing slot for odd taps. A tolerance
/// is deliberately absent: any ulp change in a coefficient is a behaviour change.
/// </summary>
public class FirDesignBitIdentityTests
{
    private static readonly int[] SampleRates = [6000, 8000, 11025, 22050, 44100, 48000];

    // SearchBandpassFilter's preset tap multipliers (constructor switch).
    private static readonly int[] SearchTapMultipliers = [24, 64, 96];

    private static readonly double[] SearchAttenuations = [10.0, 20.0, 21.0, 40.0, 49.9, 50.0, 60.0];

    private static readonly int[] TxTaps = [2, 4, 24, 128, 512];

    private static readonly double[] NotchCentresHz = [1000.0, 1049.9, 1050.0, 1500.0, 2350.0, 2350.1, 2400.0, 3000.0];

    public static TheoryData<int> Rates()
    {
        var data = new TheoryData<int>();
        foreach (var rate in SampleRates)
        {
            data.Add(rate);
        }

        return data;
    }

    /// <summary>Every (fcl, fch) the SearchBandpassFilter builds: H2, H1 for both SyncRestart states
    /// and every preset, and H3 for every narrow band and preset offset.</summary>
    private static List<(double Fcl, double Fch)> SearchBands()
    {
        var bands = new List<(double, double)> { (400.0, 2500.0) };
        foreach (var fcl in new[] { 1100.0, 1200.0 })
        {
            foreach (var fch in new[] { 2600.0, 2500.0, 2400.0 })
            {
                bands.Add((fcl, fch));
            }
        }

        foreach (var (low, high) in new[] { (1600.0, 2500.0), (1700.0, 2400.0), (1650.0, 2500.0) })
        {
            foreach (var offset in new[] { -200.0, -100.0, 0.0 })
            {
                bands.Add((low + offset, high));
            }
        }

        return bands.Distinct().ToList();
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Search_MatchesFrozenOracle_BitForBit(int sampleRate)
    {
        var bands = SearchBands();
        Assert.Equal(16, bands.Count);

        var cases = 0;
        var failures = new List<string>();
        foreach (var multiplier in SearchTapMultipliers)
        {
            var tap = (int)(multiplier * sampleRate / 11025.0);
            foreach (var (fcl, fch) in bands)
            {
                foreach (var att in SearchAttenuations)
                {
                    cases++;
                    var expected = FrozenFirDesignOracle.Search.MakeFilter(tap, sampleRate, fcl, fch, att);
                    var actual = SearchBandpassFilter.MakeFilter(tap, sampleRate, fcl: fcl, fch: fch, att: att);
                    CollectMismatch(failures, $"search tap={tap} fs={sampleRate} fcl={fcl} fch={fch} att={att}", tap, expected, actual);
                }
            }
        }

        Assert.Equal(SearchTapMultipliers.Length * 16 * SearchAttenuations.Length, cases);
        Assert.True(failures.Count == 0, $"{failures.Count}/{cases} cases differ; first: {string.Join(" | ", failures.Take(3))}");
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Tx_MatchesFrozenOracle_BitForBit(int sampleRate)
    {
        var cases = 0;
        var failures = new List<string>();
        foreach (var tap in TxTaps)
        {
            cases++;
            var expected = FrozenFirDesignOracle.Tx.MakeFilter(sampleRate, tap);
            var actual = TxOutputBandpassFilter.MakeFilter(sampleRate, tap);
            CollectMismatch(failures, $"tx tap={tap} fs={sampleRate}", tap, expected, actual);
        }

        Assert.Equal(TxTaps.Length, cases);
        Assert.True(failures.Count == 0, $"{failures.Count}/{cases} cases differ; first: {string.Join(" | ", failures.Take(3))}");
    }

    [Fact]
    public void Tx_DefaultTapOverload_MatchesFrozenOracle_BitForBit()
    {
        var failures = new List<string>();
        CollectMismatch(
            failures,
            "tx default tap fs=11025",
            TxOutputBandpassFilter.DefaultTapCount,
            FrozenFirDesignOracle.Tx.MakeFilter(11025.0),
            TxOutputBandpassFilter.MakeFilter(11025.0));

        Assert.Empty(failures);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Notch_MatchesFrozenOracle_BitForBit(int sampleRate)
    {
        var tap = new NotchFilter(sampleRate).Tap;
        var cases = 0;
        var failures = new List<string>();
        foreach (var centre in NotchCentresHz)
        {
            // CNotch::SetNotchFreq's bandwidth rule (fir.cpp:279-292), mirrored from NotchFilter.ComputeBandwidth.
            var halfBandwidth = centre is < 1050.0 or > 2350.0 ? 30.0 : 15.0;
            var fcl = centre - halfBandwidth;
            var fch = centre + halfBandwidth;

            cases++;
            var expected = FrozenFirDesignOracle.Notch.MakeFilter(tap, sampleRate, fcl, fch);
            var actual = NotchFilter.MakeFilter(tap, sampleRate, fcl: fcl, fch: fch);
            CollectMismatch(failures, $"notch tap={tap} fs={sampleRate} centre={centre}", tap, expected, actual);
        }

        Assert.Equal(NotchCentresHz.Length, cases);
        Assert.True(failures.Count == 0, $"{failures.Count}/{cases} cases differ; first: {string.Join(" | ", failures.Take(3))}");
    }

    private static void CollectMismatch(List<string> failures, string label, int tap, double[] expected, double[] actual)
    {
        if (expected.Length != tap + 1 || actual.Length != tap + 1)
        {
            failures.Add($"{label}: length expected {expected.Length} actual {actual.Length}, want {tap + 1}");
            return;
        }

        if (tap % 2 != 0
            && (BitConverter.DoubleToInt64Bits(expected[tap]) != 0 || BitConverter.DoubleToInt64Bits(actual[tap]) != 0))
        {
            failures.Add($"{label}: odd-tap trailing slot is not +0.0");
            return;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (BitConverter.DoubleToInt64Bits(expected[i]) != BitConverter.DoubleToInt64Bits(actual[i]))
            {
                failures.Add($"{label}: index {i} expected {expected[i]:R} actual {actual[i]:R}");
                return;
            }
        }
    }
}
