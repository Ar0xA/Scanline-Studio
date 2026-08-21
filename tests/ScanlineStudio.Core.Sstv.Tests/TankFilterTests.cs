namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Closes a coverage gap flagged by Tier A Batch 7 chunk 7a (docs/functional-audit-playbook.md):
/// <see cref="TankFilter"/>'s only prior coverage (SlantTests.cs) asserted just
/// <c>onFreq &gt; offFreq*5</c>, loose enough to pass with the legacy `#if 0` a0 variant
/// (`fir.cpp:53-54`), without the `1e-37` denormal flush, or with an entirely different resonant
/// bandpass. These pin actual output values, independently computed from the same formula
/// (`CIIRTANK::SetFreq`/`Do`, `fir.cpp:46-74`) this class ports, not read back from the port itself.
/// </summary>
public class TankFilterTests
{
    [Fact]
    public void SetFreqThenProcess_MatchesIndependentlyComputedCiirTankValues()
    {
        // 1200Hz/100Hz-bandwidth tank at 11025Hz -- matches SyncEnvelopeDetector's own default
        // construction. Values hand-derived (Python, double precision) from CIIRTANK::SetFreq/Do's
        // exact formulas, not from this class's own output.
        var filter = new TankFilter();
        filter.SetFreq(1200.0, 11025.0, 100.0);

        double[] inputs = [1.0, 0.5, -0.3, 0.2, 0.0];
        double[] expected =
        [
            0.03438413361890946,
            0.06899857016301576,
            0.06116540121388911,
            0.033858341260988786,
            -0.006762740101593043,
        ];

        for (var i = 0; i < inputs.Length; i++)
        {
            Assert.Equal(expected[i], filter.Process(inputs[i]), precision: 12);
        }
    }

    [Fact]
    public void SetFreq_ZeroBandwidth_UsesTheUnscaledA0Branch()
    {
        // fir.cpp:56/60 -- CIIRTANK::SetFreq has a `bw != 0 ? scaled : bare-sin` branch for a0.
        // bandwidth=0 is the untested branch; pin it against the same hand-derived formula.
        var filter = new TankFilter();
        filter.SetFreq(1200.0, 11025.0, 0.0);

        var expectedA0 = Math.Sin(2 * Math.PI * 1200.0 / 11025.0);
        var output = filter.Process(1.0);

        Assert.Equal(expectedA0, output, precision: 12);
    }
}
