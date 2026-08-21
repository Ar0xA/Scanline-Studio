namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Closes a coverage gap flagged by Tier A Batch 7 chunk 7a (docs/functional-audit-playbook.md):
/// <see cref="Vco"/> had ZERO test coverage anywhere in the suite before this file -- only
/// referenced in an unrelated DI composition-root test. Pins actual output values, independently
/// computed (Python, double precision) from `CVCO::SetGain`/`SetFreeFreq`/`Do`'s exact formulas
/// (`sstv.cpp:94-148`), not read back from this class's own output.
/// </summary>
public class VcoTests
{
    [Fact]
    public void Process_ZeroControlInput_TracksTheCenterFrequencyAlone()
    {
        // 1500Hz center at 11025Hz, gain=100, fed a constant zero control input -- pure free-running
        // oscillation at the center frequency, isolating SetFreeFrequency's own phase-increment
        // formula from SetGain's control-input scaling.
        var vco = new Vco(11025.0, 1500.0);
        vco.SetGain(100.0);

        double[] expected =
        [
            0.7544758509208144,
            0.990366961494838,
            0.5455349012105487,
            -0.2742675106749304,
            -0.9055536888925841,
        ];

        foreach (var expectedValue in expected)
        {
            Assert.Equal(expectedValue, vco.Process(0.0), precision: 12);
        }
    }

    [Fact]
    public void Process_VaryingControlInput_MatchesIndependentlyComputedValues()
    {
        // Same 1500Hz/11025Hz/gain=100 setup, now with a genuinely varying control input --
        // exercises SetGain's actual scaling of the control term, not just the center-frequency term.
        var vco = new Vco(11025.0, 1500.0);
        vco.SetGain(100.0);

        double[] controls = [0.5, -0.3, 0.1, 0.0, 0.2];
        double[] expected =
        [
            0.7728692065106031,
            0.9887244007028443,
            0.5311269897685177,
            -0.2906681127049333,
            -0.9172729889159759,
        ];

        for (var i = 0; i < controls.Length; i++)
        {
            Assert.Equal(expected[i], vco.Process(controls[i]), precision: 12);
        }
    }

    [Fact]
    public void Constructor_DefaultGain_MatchesLegacysOwnCtorDefault()
    {
        // sstv.cpp:79 -- CVCO's ctor default (m_c1 = m_TableSize/16.0), which this port's own
        // constructor now sets too even though every real caller calls SetGain before Process
        // (Batch 7 chunk 7a fix). Table size is (int)(sampleRate*2) per sstv.cpp:77.
        var vco = new Vco(11025.0, 0.0); // 0Hz center isolates the gain term entirely
        var tableSize = (int)(11025.0 * 2);
        var expectedGainTableUnits = tableSize / 16.0;

        // A single sample of nonzero control input at 0Hz center: phase = control*gainTableUnits.
        var output = vco.Process(1.0);
        var sinTable = BuildReferenceSinTable(tableSize);
        var expectedPhase = (int)(expectedGainTableUnits % tableSize);

        Assert.Equal(sinTable[expectedPhase], output, precision: 12);
    }

    private static double[] BuildReferenceSinTable(int tableSize)
    {
        var table = new double[tableSize];
        var angleIncrement = 2.0 * Math.PI / tableSize;
        for (var i = 0; i < tableSize; i++)
        {
            table[i] = Math.Sin(i * angleIncrement);
        }

        return table;
    }
}
