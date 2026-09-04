using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// T1-1 (production_audit.md)'s required gate: an explicit before/after test asserting bit-exact
/// <c>double</c> equality between <see cref="SearchBandpassFilter"/>'s current circular-buffer-based
/// <c>ProcessSample</c> and a test-local reference reproducing the PRE-change linear-array
/// (<c>Array.Copy</c>-shift) implementation exactly, sample-for-sample, at every reachable tap count
/// and sample rate, including a mid-stream coefficient-table switch -- not just the existing coarse
/// image-delta/frequency-response tests in <c>SearchBandpassFilterTests.cs</c>, which use identical
/// input at every sample and so cannot distinguish a delay-line index permutation from correct
/// behavior. Two-round auditor plan-review before implementation (per CLAUDE.md §7's DSP cadence);
/// round 1 found a real blocker in the sibling <see cref="HilbertFmDemodulator"/> class (see
/// <c>HilbertFmDemodulatorCircularBufferParityTests.cs</c>) -- this filter has no equivalent raw
/// point-read outside its own delay-line class, confirmed by the same round's own grep.
/// </summary>
public class SearchBandpassFilterCircularBufferParityTests
{
    /// <summary>Literal reproduction of <see cref="SearchBandpassFilter.ProcessSample"/>'s PRE-change
    /// implementation (a plain <c>double[]</c> delay line, shifted via <c>Array.Copy</c> every call) --
    /// the oracle this test compares the real, current circular-buffer implementation against. Uses the
    /// real <see cref="SearchBandpassFilter.MakeFilter"/> for its own H1/H2 coefficient tables (coefficient
    /// generation is unchanged by this item and is already covered by <c>SearchBandpassFilterTests.cs</c>'s
    /// own fixture tests -- using it here isolates this test to the delay-line addressing specifically).</summary>
    private sealed class LinearReferenceFilter
    {
        private readonly double[] _h2;
        private double[] _h1;
        private readonly double[] _z;
        private readonly int _tap;
        private readonly int _sampleRate;
        private readonly double _h1Fch;
        private readonly double _h1Att;

        public LinearReferenceFilter(int tap, int sampleRate, double h1Fcl, double h1Fch, double h1Att)
        {
            _tap = tap;
            _sampleRate = sampleRate;
            _h1Fch = h1Fch;
            _h1Att = h1Att;
            _h1 = SearchBandpassFilter.MakeFilter(tap, sampleRate, fcl: h1Fcl, fch: h1Fch, att: h1Att);
            _h2 = SearchBandpassFilter.MakeFilter(tap, sampleRate, fcl: 400.0, fch: 2500.0, att: 20.0);
            _z = new double[tap + 1];
        }

        public void UpdateSyncRestart(bool syncRestartEnabled)
        {
            var h1Fcl = syncRestartEnabled ? 1100.0 : 1200.0;
            _h1 = SearchBandpassFilter.MakeFilter(_tap, _sampleRate, fcl: h1Fcl, fch: _h1Fch, att: _h1Att);
        }

        public double ProcessSample(double input, bool useLocked)
        {
            Array.Copy(_z, 0, _z, 1, _tap);
            _z[0] = input;

            var h = useLocked ? _h1 : _h2;
            var sum = 0.0;
            for (var i = 0; i <= _tap; i++)
            {
                sum += _z[i] * h[i];
            }

            return sum;
        }
    }

    // Wide/Narrow/VeryNarrow -> (multiplier, h1Fch, h1Att), matching SearchBandpassFilter's own
    // constructor switch exactly (see that class's own doc comment for the source table).
    private static (int Multiplier, double H1Fch, double H1Att) PresetTable(RxBpfPreset preset) => preset switch
    {
        RxBpfPreset.Wide => (24, 2600.0, 20.0),
        RxBpfPreset.Narrow => (64, 2500.0, 40.0),
        RxBpfPreset.VeryNarrow => (96, 2400.0, 50.0),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    [Theory]
    [InlineData(RxBpfPreset.Wide, 11025, 24)]
    [InlineData(RxBpfPreset.Narrow, 11025, 64)]
    [InlineData(RxBpfPreset.VeryNarrow, 11025, 96)]
    [InlineData(RxBpfPreset.Wide, 44100, 96)]
    [InlineData(RxBpfPreset.Narrow, 44100, 256)]
    [InlineData(RxBpfPreset.VeryNarrow, 44100, 384)]
    [InlineData(RxBpfPreset.Wide, 8000, 17)] // odd tap: (int)(24*8000/11025.0) = 17 -- plan-review round 2
                                              // finding: "2 reachable sample rates" undersold the real
                                              // domain (SstvSampleRate spans 5000-48500Hz); this case
                                              // exercises the odd-tap path this filter's other 8
                                              // combinations never reach.
    [InlineData(RxBpfPreset.Narrow, 22050, 128)]
    public void ProcessSample_MatchesLinearReferenceImplementation_BitExact_AcrossManyWrapsAndMidStreamSwitch(
        RxBpfPreset preset, int sampleRate, int expectedTap)
    {
        var (multiplier, h1Fch, h1Att) = PresetTable(preset);
        var tap = (int)(multiplier * sampleRate / 11025.0);
        Assert.Equal(expectedTap, tap); // sanity: pins the exact tap this case claims to exercise

        var candidate = new SearchBandpassFilter(sampleRate, preset, syncRestartEnabled: true);
        var reference = new LinearReferenceFilter(tap, sampleRate, h1Fcl: 1100.0, h1Fch: h1Fch, h1Att: h1Att);

        var capacity = tap + 1;
        var sampleCount = (capacity * 10) + 7; // >= 10x capacity, deliberately not an exact multiple of it
        var syncRestartFlipAt = capacity + 3; // mid-stream, deliberately not a multiple of capacity --
                                               // a head-phase bug could otherwise hide behind a
                                               // coincidental full-cycle alignment
        var random = new Random(unchecked((preset.GetHashCode() * 397) ^ sampleRate)); // fixed per case

        for (var i = 0; i < sampleCount; i++)
        {
            if (i == syncRestartFlipAt)
            {
                // UpdateSyncRestart rebuilds H1 only -- doesn't touch the delay line -- so this
                // perturbation proves the circular buffer's in-flight history survives a coefficient
                // swap correctly, matching the delay-line-inert contract UpdateSyncRestart's own doc
                // comment describes.
                candidate.UpdateSyncRestart(syncRestartEnabled: false);
                reference.UpdateSyncRestart(syncRestartEnabled: false);
            }

            // Varied magnitude, not constant/all-zero/all-same-magnitude -- those can't distinguish an
            // index permutation from correct behavior, since any permutation of equal values sums identically.
            var input = (random.NextDouble() - 0.5) * 20000.0;
            var useLocked = i % 3 != 0; // exercises both H1 and H2 coefficient tables, not just one

            // useNarrow: false throughout -- this test's own scope is the delay-line/circular-buffer
            // addressing shared by H1/H2/H3 alike (class doc comment), not H3's own coefficient
            // correctness, which SearchBandpassFilterTests.cs's dedicated BuildNarrowLockedFilter tests
            // cover instead.
            var candidateOutput = candidate.ProcessSample(input, useLocked, useNarrow: false);
            var referenceOutput = reference.ProcessSample(input, useLocked);

            Assert.Equal(referenceOutput, candidateOutput); // bit-exact -- no precision/tolerance argument
        }
    }
}
