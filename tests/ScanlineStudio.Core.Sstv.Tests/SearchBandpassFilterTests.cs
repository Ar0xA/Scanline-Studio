using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="SearchBandpassFilter"/>, tested independently before any wiring
/// into <see cref="AnalogFmSstvDecoder"/>, per this project's chop-into-pieces methodology.
/// </summary>
public class SearchBandpassFilterTests
{
    // Independently computed (Python, NOT derived from or captured against the C# implementation --
    // same discipline as HilbertFmDemodulatorTests' own MakeHilbert fixtures, for the same reason:
    // a self-consistent transcription error wouldn't be caught by comparing code against itself).
    private static readonly double[] ExpectedH24At11025Hz =
    [
        -0.038096964571744996, -0.01678574993448184, 0.00778593709189614, -0.023322606600573854,
        -0.07827624688574886, -0.07206435465060877, -0.011669582976266994, -0.010844587542183507,
        -0.11127234745613829, -0.169945136839944, -0.025262005072730728, 0.2531541785449182,
        0.39689385795922316, 0.2531541785449182, -0.025262005072730728, -0.169945136839944,
        -0.11127234745613829, -0.010844587542183507, -0.011669582976266994, -0.07206435465060877,
        -0.07827624688574886, -0.023322606600573854, 0.00778593709189614, -0.01678574993448184,
        -0.038096964571744996,
    ];

    // H1 (Band-1 item 4b, locked/normal variant, 1100-2600Hz) -- independently computed the same way,
    // same discipline as ExpectedH24At11025Hz above.
    private static readonly double[] ExpectedH1_24At11025Hz =
    [
        -0.05096687968271917, -0.0345552361313546, 0.02657058339308721, 0.048251208785099066,
        0.012623798255956765, 0.006520677333613534, 0.06098040885923243, 0.06010707964532414,
        -0.07896923273412564, -0.21446854318153516, -0.12973139123562366, 0.13741684586246364,
        0.2869316211841456, 0.13741684586246364, -0.12973139123562366, -0.21446854318153516,
        -0.07896923273412564, 0.06010707964532414, 0.06098040885923243, 0.006520677333613534,
        0.012623798255956765, 0.048251208785099066, 0.02657058339308721, -0.0345552361313546,
        -0.05096687968271917,
    ];

    // Kaiser-branch (att>=21) fixtures, independently computed the same way (Python, not derived from
    // the C# implementation) -- one at Narrow's att=40 (exercises alpha's `21<=att<50` threshold arm,
    // fir.cpp:365) and one at VeryNarrow's att=50 (exercises the `att>=50` arm, fir.cpp:362). One each
    // suffices since together they cover both arms, not an arbitrary sample size.
    private static readonly double[] ExpectedH1NarrowAtt40Tap64At11025Hz =
    [
        9.370389741475935e-05, -0.0006658761585431034, -0.0019699959841094927, 0.00041808963946636074,
        0.0054037930012744125, 0.005794339686164738, -0.00021325480249232464, -0.004274291842989352,
        -0.0013948272073252262, 9.652476477038244e-05, -0.006989342746955894, -0.012154731538551157,
        -0.0016692893052850006, 0.015167485965459226, 0.015961744371584343, 0.0018812826727468684,
        -0.00208988419733074, 0.008624966435145827, 0.004936581597846851, -0.024735810218937514,
        -0.041954781062132185, -0.01323457771471915, 0.02804760081288949, 0.027057350821584066,
        0.0012605884504510716, 0.018228888292564548, 0.06851934181420938, 0.04567008380831989,
        -0.08961183826825, -0.1959669155552911, -0.10550460496461679, 0.12885599748194937,
        0.25563992986256895, 0.12885599748194937, -0.10550460496461679, -0.1959669155552911,
        -0.08961183826825, 0.04567008380831989, 0.06851934181420938, 0.018228888292564548,
        0.0012605884504510716, 0.027057350821584066, 0.02804760081288949, -0.01323457771471915,
        -0.041954781062132185, -0.024735810218937514, 0.004936581597846851, 0.008624966435145827,
        -0.00208988419733074, 0.0018812826727468684, 0.015961744371584343, 0.015167485965459226,
        -0.0016692893052850006, -0.012154731538551157, -0.006989342746955894, 9.652476477038244e-05,
        -0.0013948272073252262, -0.004274291842989352, -0.00021325480249232464, 0.005794339686164738,
        0.0054037930012744125, 0.00041808963946636074, -0.0019699959841094927, -0.0006658761585431034,
        9.370389741475935e-05,
    ];

    private static readonly double[] ExpectedH1VeryNarrowAtt50Tap96At11025Hz =
    [
        0.0004659648002722804, 0.0008717355364200276, 0.0003440890901034994, -0.0006853316108306848,
        -0.0008840573566414323, -0.00018734349031883707, -0.00016278391967903166, -0.0012530390501830628,
        -0.0013149903534615587, 0.001150134018572487, 0.0036690562759162607, 0.002685923536507964,
        -0.0007367546762971709, -0.0019451047593191221, -0.00013503450750382162, -0.0001657023583521331,
        -0.004209582132265421, -0.006292044380906334, -0.0006642740996192202, 0.0076069077780323525,
        0.008265501624925186, 0.0014430552225439136, -0.0018655920704423444, 0.002279178352705776,
        0.0028996369423198915, -0.007815232576216448, -0.017764303766360314, -0.010119922844153511,
        0.009122404548258382, 0.016498800866004755, 0.006184649143446931, -0.0001311741088086046,
        0.010698948036716085, 0.01691887364615966, -0.005905040211087668, -0.03864998882527877,
        -0.03723163962329314, 0.0010421791583385897, 0.026544109927053017, 0.011346872707366723,
        -0.001657372798209082, 0.034803559868562324, 0.0781071472868093, 0.032413523441753106,
        -0.10344112690125404, -0.1866055099736229, -0.08805398529163573, 0.12493417765185179,
        0.23583116262332668, 0.12493417765185179, -0.08805398529163573, -0.1866055099736229,
        -0.10344112690125404, 0.032413523441753106, 0.0781071472868093, 0.034803559868562324,
        -0.001657372798209082, 0.011346872707366723, 0.026544109927053017, 0.0010421791583385897,
        -0.03723163962329314, -0.03864998882527877, -0.005905040211087668, 0.01691887364615966,
        0.010698948036716085, -0.0001311741088086046, 0.006184649143446931, 0.016498800866004755,
        0.009122404548258382, -0.010119922844153511, -0.017764303766360314, -0.007815232576216448,
        0.0028996369423198915, 0.002279178352705776, -0.0018655920704423444, 0.0014430552225439136,
        0.008265501624925186, 0.0076069077780323525, -0.0006642740996192202, -0.006292044380906334,
        -0.004209582132265421, -0.0001657023583521331, -0.00013503450750382162, -0.0019451047593191221,
        -0.0007367546762971709, 0.002685923536507964, 0.0036690562759162607, 0.001150134018572487,
        -0.0013149903534615587, -0.0012530390501830628, -0.00016278391967903166, -0.00018734349031883707,
        -0.0008840573566414323, -0.0006853316108306848, 0.0003440890901034994, 0.0008717355364200276,
        0.0004659648002722804,
    ];

    // syncRestartEnabled=false: H1's fcl shifts to 1200Hz (lfq, sstv.cpp:1524) even at Wide/att=20 --
    // independently computed the same way, confirming the shift is real, not just "code compiles."
    private static readonly double[] ExpectedH1SyncRestartOffTap24At11025Hz =
    [
        -0.048730780177901306, -0.04402451085756292, 0.00805055381490643, 0.02948255561126511,
        0.0029039726048739065, 0.008527264588484828, 0.07138651590588148, 0.0758291837249199,
        -0.05990282987339344, -0.1988052552798684, -0.12924757222620878, 0.11735121557056338,
        0.25706434774497244, 0.11735121557056338, -0.12924757222620878, -0.1988052552798684,
        -0.05990282987339344, 0.0758291837249199, 0.07138651590588148, 0.008527264588484828,
        0.0029039726048739065, 0.02948255561126511, 0.00805055381490643, -0.04402451085756292,
        -0.048730780177901306,
    ];

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_Tap24At11025Hz()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 400.0, 2500.0, att: 20.0);

        Assert.Equal(ExpectedH24At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH24At11025Hz[i]) < 1e-12, $"index {i}: expected {ExpectedH24At11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_H1_Tap24At11025Hz()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 1100.0, 2600.0, att: 20.0);

        Assert.Equal(ExpectedH1_24At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH1_24At11025Hz[i]) < 1e-12, $"index {i}: expected {ExpectedH1_24At11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_KaiserBranch_FullArray_MatchesIndependentlyComputedFixture_NarrowAtt40()
    {
        var h = SearchBandpassFilter.MakeFilter(64, 11025.0, 1100.0, 2500.0, att: 40.0);

        Assert.Equal(ExpectedH1NarrowAtt40Tap64At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH1NarrowAtt40Tap64At11025Hz[i]) < 1e-12,
                $"index {i}: expected {ExpectedH1NarrowAtt40Tap64At11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_KaiserBranch_FullArray_MatchesIndependentlyComputedFixture_VeryNarrowAtt50()
    {
        var h = SearchBandpassFilter.MakeFilter(96, 11025.0, 1100.0, 2400.0, att: 50.0);

        Assert.Equal(ExpectedH1VeryNarrowAtt50Tap96At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH1VeryNarrowAtt50Tap96At11025Hz[i]) < 1e-12,
                $"index {i}: expected {ExpectedH1VeryNarrowAtt50Tap96At11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_SyncRestartOff_H1FclShiftsTo1200Hz_MatchesIndependentlyComputedFixture()
    {
        // Round-1 blocker fix: legacy's lfq = (m_SyncRestart ? 1100 : 1200) + g_dblToneOffset
        // (sstv.cpp:1524) means H1's fcl genuinely shifts with sync-restart, at every preset -- this
        // pins the shift at Wide/att=20 against an independently-computed fixture, not just a
        // parameter pass-through assumption.
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 1200.0, 2600.0, att: 20.0);

        Assert.Equal(ExpectedH1SyncRestartOffTap24At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH1SyncRestartOffTap24At11025Hz[i]) < 1e-12,
                $"index {i}: expected {ExpectedH1SyncRestartOffTap24At11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void Constructor_SyncRestartDisabled_H1FclActuallyShiftsTo1200Hz()
    {
        // Phase-1 auditor code-review finding: the fixture test above pins the 1200Hz shift by calling
        // MakeFilter directly with 1200.0 -- it does NOT exercise the constructor's own
        // syncRestartEnabled=false branch, so a regression back to a hardcoded 1100.0 there (the exact
        // round-1 blocker) would pass the whole suite undetected. This closes that gap: construct
        // through the real public API and confirm the causal impulse response matches
        // ExpectedH1SyncRestartOffTap24At11025Hz exactly, the same technique as
        // ProcessSample_ImpulseResponse_IsCausal_NotCentered.
        const int tap = 24;
        var filter = new SearchBandpassFilter(11025, RxBpfPreset.Wide, syncRestartEnabled: false);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0, useLocked: true);
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0, useLocked: true);
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - ExpectedH1SyncRestartOffTap24At11025Hz[n]) < 1e-12,
                $"n={n}: expected {ExpectedH1SyncRestartOffTap24At11025Hz[n]}, got {outputs[n]}");
        }
    }

    [Fact]
    public void Constructor_NarrowPreset_H1CoefficientsActuallyReachBuiltFilter()
    {
        // Phase-1 auditor code-review finding: VeryNarrow's H1 is covered by
        // FrequencyResponse_MatchesIndependentlyComputedMagnitude_VeryNarrowH1 and Wide's by the causal
        // impulse test above, but Narrow's H1 (fch=2500, att=40) never reached a constructed filter in
        // any test -- a typo in the preset switch's Narrow row would be caught by nothing. Closes that
        // gap via the same causal-impulse technique against the independently-computed Narrow fixture.
        const int tap = 64;
        var filter = new SearchBandpassFilter(11025, RxBpfPreset.Narrow, syncRestartEnabled: true);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0, useLocked: true);
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0, useLocked: true);
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - ExpectedH1NarrowAtt40Tap64At11025Hz[n]) < 1e-12,
                $"n={n}: expected {ExpectedH1NarrowAtt40Tap64At11025Hz[n]}, got {outputs[n]}");
        }
    }

    [Theory]
    [InlineData(0, -0.009694264305853777)]
    [InlineData(1, -0.009375920786516312)]
    [InlineData(24, -0.002969475993237702)]
    [InlineData(48, 0.10099476437764031)]
    [InlineData(72, -0.002969475993237702)]
    [InlineData(95, -0.009375920786516312)]
    [InlineData(96, -0.009694264305853777)]
    public void MakeFilter_SelectedValues_MatchIndependentlyComputedFixture_Tap96At44100Hz(int index, double expected)
    {
        var h = SearchBandpassFilter.MakeFilter(96, 44100.0, 400.0, 2500.0, att: 20.0);

        Assert.True(Math.Abs(h[index] - expected) < 1e-12, $"index {index}: expected {expected}, got {h[index]}");
    }

    [Theory]
    [InlineData(0, -0.012515335642196635)]
    [InlineData(1, -0.013149626347786018)]
    [InlineData(24, 0.014974239922528401)]
    [InlineData(48, 0.07045841471626878)]
    [InlineData(72, 0.014974239922528401)]
    [InlineData(95, -0.013149626347786018)]
    [InlineData(96, -0.012515335642196635)]
    public void MakeFilter_SelectedValues_MatchIndependentlyComputedFixture_H1_Tap96At44100Hz(int index, double expected)
    {
        var h = SearchBandpassFilter.MakeFilter(96, 44100.0, 1100.0, 2600.0, att: 20.0);

        Assert.True(Math.Abs(h[index] - expected) < 1e-12, $"index {index}: expected {expected}, got {h[index]}");
    }

    [Fact]
    public void MakeFilter_ArrayLength_IsTapPlusOne()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 400.0, 2500.0, att: 20.0);

        Assert.Equal(25, h.Length);
    }

    [Theory]
    [InlineData(24, 11025.0, 400.0, 2500.0, 20.0)]
    [InlineData(96, 44100.0, 400.0, 2500.0, 20.0)]
    [InlineData(24, 11025.0, 1100.0, 2600.0, 20.0)]
    [InlineData(96, 44100.0, 1100.0, 2600.0, 20.0)]
    [InlineData(64, 11025.0, 1100.0, 2500.0, 40.0)]
    [InlineData(96, 11025.0, 1100.0, 2400.0, 50.0)]
    public void MakeFilter_IsSymmetric_ForEvenTap(int tap, double sampleRate, double fcl, double fch, double att)
    {
        // Round-1 correction: symmetry is only structurally guaranteed for EVEN tap -- this port has 9
        // reachable preset/rate combinations, all even (see class doc comment); the last two rows above
        // cover the Kaiser branch specifically, confirming the window doesn't break symmetry.
        var h = SearchBandpassFilter.MakeFilter(tap, sampleRate, fcl, fch, att);

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(h[tap - n] - h[n]) < 1e-12, $"n={n}: H[{tap - n}]={h[tap - n]}, H[{n}]={h[n]} -- not symmetric");
        }
    }

    [Theory]
    [InlineData(23, 11025.0, 400.0, 2500.0, 20.0)]
    [InlineData(25, 11025.0, 1100.0, 2600.0, 20.0)]
    public void MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric(int tap, double sampleRate, double fcl, double fch, double att)
    {
        // S29 (spec/14-roadmap.md): a guard for the odd-tap trailing-zero divergence this class's own
        // doc comment already documents (see the comment above MakeFilter itself) but that, until now,
        // had no executable assertion pinning it -- only MakeFilter_IsSymmetric_ForEvenTap exists, and
        // it deliberately excludes odd tap. Legacy's real mirroring loops (fir.cpp:421-426) write
        // exactly 2*(tap/2)+1 entries (integer division) -- for an EVEN tap that's tap+1 (the whole
        // array); for an ODD tap it's only tap entries, leaving the array's last slot (index tap) never
        // written, so it stays at whatever the caller's buffer already held. This port's own array is
        // C#'s guaranteed-zero-init, matching legacy's own H1/H2 member buffers (also zero-initialized
        // at construction). Neither of this port's two currently-reachable tap counts (24@11025Hz,
        // 96@44100Hz) is odd, so this divergence is real but latent -- this test exists so a future
        // refactor that "helpfully" fully populates the trailing slot (looking like it's fixing an
        // off-by-one) fails loudly instead of silently diverging from legacy's real behavior.
        var h = SearchBandpassFilter.MakeFilter(tap, sampleRate, fcl, fch, att);

        Assert.Equal(tap + 1, h.Length);
        Assert.Equal(0.0, h[tap]); // the untouched trailing slot -- exact, never written by either mirroring loop
        Assert.NotEqual(0.0, h[tap - 1]); // sanity: the slot just before it WAS written, a real coefficient
        Assert.NotEqual(h[0], h[tap]); // not symmetric the way the even-tap case is -- h[0] is real, h[tap] is the untouched zero
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessSample_ImpulseResponse_IsCausal_NotCentered(bool useLocked)
    {
        // Round-1 finding, the single most important test in this file: the convolution window is
        // CAUSAL -- H[0] pairs with the NEWEST sample -- not centered. A centered window would pass
        // every other test in this file identically while silently introducing a tap/2-sample anchor
        // error. Feed a unit impulse, then zeros: output[n] must equal h[n] exactly (H[0] on the
        // impulse's own call, H[tap] tap calls later), confirming H[0] pairs with the sample just fed
        // in, not tap/2 samples in the past or future. Parametrized over both H1/H2 (Band-1 item 4b) --
        // both share the SAME delay line, so this same causal-addressing property must hold for either
        // coefficient table.
        const int tap = 24;
        var h = SearchBandpassFilter.MakeFilter(tap, 11025.0, useLocked ? 1100.0 : 400.0, useLocked ? 2600.0 : 2500.0, att: 20.0);
        var filter = new SearchBandpassFilter(11025, RxBpfPreset.Wide, syncRestartEnabled: true);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0, useLocked); // impulse
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0, useLocked); // then zeros
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - h[n]) < 1e-12, $"useLocked={useLocked}, n={n}: expected h[{n}]={h[n]}, got {outputs[n]}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessSample_BeforeStreamStart_IsZeroPadded(bool useLocked)
    {
        // The delay line starts zero-initialized (matching CFIR2::Create's own zero-memset delay line,
        // confirmed never reset mid-stream on RX) -- the very first ProcessSample call sees only the
        // one real input sample, with every "prior" delay-line slot still at its zero-init value.
        var filter = new SearchBandpassFilter(11025, RxBpfPreset.Wide, syncRestartEnabled: true);

        var output = filter.ProcessSample(5.0, useLocked);
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, useLocked ? 1100.0 : 400.0, useLocked ? 2600.0 : 2500.0, att: 20.0);
        Assert.True(Math.Abs(output - h[0] * 5.0) < 1e-9, $"useLocked={useLocked}: expected {h[0] * 5.0}, got {output}");
    }

    [Fact]
    public void ProcessSample_SwitchingToLocked_ReusesExistingDelayLineHistory_NoSeparateWarmUp()
    {
        // Band-1 item 4b's central design claim, verified directly rather than just asserted in a
        // comment: legacy's CFIR2::Do(d, hp) maintains ONE shared delay line and picks which
        // coefficient table to dot-product against per call -- it is NOT two independent filter
        // objects, so switching to H1 mid-stream needs no separate warm-up. Proof: feed several
        // H2-selected samples (building up real delay-line history), then switch to H1 for one sample
        // -- the result must match independently convolving H1's OWN coefficients against that SAME
        // accumulated history (computed here by hand from the raw inputs, not by calling the filter
        // again), not a fresh/zeroed delay line's worth of H1 output.
        const int tap = 24;
        var rawInputs = new double[] { 3.0, -1.5, 2.25, 0.5, -4.0, 1.0, 0.75 }; // arbitrary, fewer than tap+1
        var filter = new SearchBandpassFilter(11025, RxBpfPreset.Wide, syncRestartEnabled: true);

        foreach (var input in rawInputs)
        {
            filter.ProcessSample(input, useLocked: false); // H2, building up shared delay-line history
        }

        var switchInput = 2.0;
        var actual = filter.ProcessSample(switchInput, useLocked: true); // switch to H1 for this one sample

        // Hand-reconstruct the shared delay line's contents as of this call: newest-first, switchInput
        // then rawInputs reversed, zero-padded to tap+1 -- matches CFIR2::Do's own addressing exactly.
        var expectedDelayLine = new double[tap + 1];
        expectedDelayLine[0] = switchInput;
        for (var i = 0; i < rawInputs.Length; i++)
        {
            expectedDelayLine[i + 1] = rawInputs[rawInputs.Length - 1 - i];
        }

        var h1 = SearchBandpassFilter.MakeFilter(tap, 11025.0, 1100.0, 2600.0, att: 20.0);
        var expected = 0.0;
        for (var i = 0; i <= tap; i++)
        {
            expected += expectedDelayLine[i] * h1[i];
        }

        Assert.True(Math.Abs(actual - expected) < 1e-9, $"expected {expected} (H1 against the H2-built-up shared history), got {actual}");
    }

    [Theory]
    [InlineData(100.0, 0.12949)]
    [InlineData(400.0, 0.58250)]
    [InlineData(1100.0, 1.03998)]
    [InlineData(1200.0, 1.03892)]
    [InlineData(1900.0, 1.06642)]
    [InlineData(2500.0, 0.54012)]
    [InlineData(3000.0, 0.09498)]
    [InlineData(4000.0, 0.00241)]
    public void FrequencyResponse_MatchesIndependentlyComputedMagnitude(double toneHz, double expectedGain)
    {
        // Independently computed (Python DFT-style summation over the SAME independently-computed
        // coefficients used above, not derived from the C# convolution implementation) -- confirms the
        // filter actually behaves as a bandpass (near-unity in-band, real attenuation out-of-band),
        // not just "matches the coefficient formula" in isolation. 400-2500Hz is the passband; 1100/
        // 1200/1900Hz are the real VIS-bit/sync/leader tones this port needs to pass through.
        const int sampleRate = 11025;
        const double amplitude = 1000.0;
        var filter = new SearchBandpassFilter(sampleRate, RxBpfPreset.Wide, syncRestartEnabled: true);

        // Long enough for the FIR's own causal window to fully fill with steady-tone content (tap=24
        // samples) plus a full cycle margin at the lowest tested frequency, so the measured peak is
        // genuine steady-state, not a startup transient.
        var sampleCount = sampleRate; // 1 second, generous
        var settleSamples = 100; // comfortably past the tap=24 window fill
        var peak = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            var input = amplitude * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            var output = Math.Abs(filter.ProcessSample(input, useLocked: false));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        var measuredGain = peak / amplitude;
        Assert.True(Math.Abs(measuredGain - expectedGain) < 0.02, $"toneHz={toneHz}: expected gain {expectedGain}, measured {measuredGain}");
    }

    [Theory]
    [InlineData(100.0, 0.01420)]
    [InlineData(700.0, 0.07779)]
    [InlineData(1100.0, 0.51134)]
    [InlineData(1200.0, 0.77769)]
    [InlineData(1900.0, 0.98048)]
    [InlineData(2600.0, 0.53444)]
    [InlineData(3200.0, 0.00611)]
    [InlineData(4000.0, 0.01027)]
    public void FrequencyResponse_MatchesIndependentlyComputedMagnitude_H1(double toneHz, double expectedGain)
    {
        // Same discipline as the H2 test above, for H1's own passband (1100-2600Hz). 1200/1900Hz are
        // the same real VIS-bit/sync/leader tones; 1100/2600Hz are H1's own passband edges (H2's are
        // 400/2500Hz -- deliberately different, not a copy-paste of the same edge frequencies).
        const int sampleRate = 11025;
        const double amplitude = 1000.0;
        var filter = new SearchBandpassFilter(sampleRate, RxBpfPreset.Wide, syncRestartEnabled: true);

        var sampleCount = sampleRate;
        var settleSamples = 100;
        var peak = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            var input = amplitude * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            var output = Math.Abs(filter.ProcessSample(input, useLocked: true));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        var measuredGain = peak / amplitude;
        Assert.True(Math.Abs(measuredGain - expectedGain) < 0.02, $"toneHz={toneHz}: expected gain {expectedGain}, measured {measuredGain}");
    }

    [Theory]
    [InlineData(100.0, 0.00065)]
    [InlineData(900.0, 0.00239)]
    [InlineData(1100.0, 0.50021)]
    [InlineData(1200.0, 0.90439)]
    [InlineData(1900.0, 0.99954)]
    [InlineData(2400.0, 0.49993)]
    [InlineData(2700.0, 0.00146)]
    [InlineData(4000.0, 0.00014)]
    public void FrequencyResponse_MatchesIndependentlyComputedMagnitude_VeryNarrowH1(double toneHz, double expectedGain)
    {
        // Round-1 test requirement: at least one narrower preset must show the Kaiser window actually
        // produces a MEASURABLY sharper roll-off, not just different coefficient numbers. Compare the
        // 900Hz/2700Hz points here (just outside VeryNarrow's 1100-2400Hz passband) against H1's own
        // Wide-preset gains at the same tones (FrequencyResponse_MatchesIndependentlyComputedMagnitude_H1
        // doesn't test 900/2700 directly, but its 700Hz/3200Hz neighbors show ~0.078/0.006 -- notably
        // looser than VeryNarrow's ~0.0024/0.0015 here at points even closer to the passband edge),
        // confirming VeryNarrow's att=50 Kaiser window is a real, measurable improvement in stopband
        // rejection, not merely "a filter that runs." Independently computed the same way as the other
        // FrequencyResponse_* tests (Python steady-state simulation of the SAME independently-computed
        // Kaiser-windowed coefficients, not derived from the C# convolution implementation).
        const int sampleRate = 11025;
        const double amplitude = 1000.0;
        var filter = new SearchBandpassFilter(sampleRate, RxBpfPreset.VeryNarrow, syncRestartEnabled: true);

        var sampleCount = sampleRate;
        var settleSamples = 200; // VeryNarrow's tap=96 window needs longer to fully fill than Wide's tap=24
        var peak = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            var input = amplitude * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            var output = Math.Abs(filter.ProcessSample(input, useLocked: true));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        var measuredGain = peak / amplitude;
        Assert.True(Math.Abs(measuredGain - expectedGain) < 0.02, $"toneHz={toneHz}: expected gain {expectedGain}, measured {measuredGain}");
    }

    [Theory]
    [InlineData(RxBpfPreset.Wide, 24)]
    [InlineData(RxBpfPreset.Narrow, 64)]
    [InlineData(RxBpfPreset.VeryNarrow, 96)]
    public void Constructor_PerPreset_TapCountReachesBuiltFilter(RxBpfPreset preset, int expectedTap)
    {
        // Round-1 test requirement: confirm the exact tap count from the preset table (sstv.cpp:1522-
        // 1550) reaches the actually-constructed filter, not just that MakeFilter itself computes the
        // right thing in isolation. Uses the same causal-impulse-response technique as
        // ProcessSample_ImpulseResponse_IsCausal_NotCentered: feed an impulse then zeros, and the
        // response must be exactly expectedTap+1 samples long with the LAST one still non-zero (proving
        // the filter really has expectedTap taps, not fewer) while one sample further has decayed back
        // to true zero (proving it's not more).
        var filter = new SearchBandpassFilter(11025, preset, syncRestartEnabled: true);

        var outputs = new double[expectedTap + 2];
        outputs[0] = filter.ProcessSample(1.0, useLocked: true);
        for (var n = 1; n < outputs.Length; n++)
        {
            outputs[n] = filter.ProcessSample(0.0, useLocked: true);
        }

        Assert.NotEqual(0.0, outputs[expectedTap]);
        Assert.Equal(0.0, outputs[expectedTap + 1]);
    }
}
