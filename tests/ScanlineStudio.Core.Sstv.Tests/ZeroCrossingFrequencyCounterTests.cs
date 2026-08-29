using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="ZeroCrossingFrequencyCounter"/>, including its new (Phase 1
/// of the demod-type runtime-dispatch plan) <see cref="ZeroCrossingFrequencyCounter.Clear"/> method
/// -- each tested independently before any wiring into <see cref="AnalogFmSstvDecoder"/>, per this
/// project's chop-into-pieces methodology. <see cref="AfcTests"/> already covers pure/offset-tone
/// convergence for this class's AFC-measurement role; this file adds narrow-band coverage and the
/// new <c>Clear()</c> method neither existing test file touches.
/// </summary>
public class ZeroCrossingFrequencyCounterTests
{
    private const int SampleRate = 44100;

    private static double SampleSineWaveFrequency(ZeroCrossingFrequencyCounter counter, double targetHz, double durationMs)
    {
        var sampleCount = (int)(durationMs / 1000.0 * SampleRate);
        var phaseIncrement = 2 * Math.PI * targetHz / SampleRate;
        var phase = 0.0;
        var freq = 0.0;

        for (var i = 0; i < sampleCount; i++)
        {
            phase += phaseIncrement;
            freq = counter.ProcessSample(Math.Sin(phase));
        }

        return freq;
    }

    [Theory]
    [InlineData(2044.0)]
    [InlineData(2172.0)]
    [InlineData(2300.0)]
    public void ProcessSample_NarrowBandTone_AfterSetWidth_ConvergesToActualFrequency(double targetHz)
    {
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        counter.SetWidth(isNarrow: true);

        var freq = SampleSineWaveFrequency(counter, targetHz, durationMs: 100);

        Assert.Equal(targetHz, freq, tolerance: 5.0);
    }

    [Fact]
    public void ProcessSample_NarrowBandTone_WithoutSetWidth_ClampsOutsideWideBounds()
    {
        // What discriminates wide-vs-narrow at this class's own clamp boundary is the LOW clamp
        // (1000Hz wide vs. 1800Hz narrow, NARROW_AFCLOW -- the high clamp is 2400Hz for both, so it
        // can't discriminate): a real measured frequency below 1800Hz but at/above 1000Hz reads back
        // as itself under the wide default, but clamps to 1800 if narrow is active instead.
        var wideCounter = new ZeroCrossingFrequencyCounter(SampleRate);
        var wideFreq = SampleSineWaveFrequency(wideCounter, targetHz: 1200.0, durationMs: 100);
        Assert.Equal(1200.0, wideFreq, tolerance: 5.0); // wide low clamp (1000) doesn't interfere

        var narrowCounter = new ZeroCrossingFrequencyCounter(SampleRate);
        narrowCounter.SetWidth(isNarrow: true);
        var narrowFreq = SampleSineWaveFrequency(narrowCounter, targetHz: 1200.0, durationMs: 100);
        Assert.Equal(1800.0, narrowFreq, tolerance: 1.0); // narrow low clamp (NARROW_AFCLOW=1800) kicks in
    }

    [Fact]
    public void Clear_ResetsRunningEstimate_StaleValueDoesNotSurviveIntoNextTransmission()
    {
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        var settled = SampleSineWaveFrequency(counter, targetHz: 1200.0, durationMs: 100);
        Assert.Equal(1200.0, settled, tolerance: 5.0); // sanity: real state built up before Clear()

        counter.Clear();

        // Clear() resets the RAW running estimate (_currentFrequencyHz) to the width-dependent
        // cleared value (0Hz here, since this counter is still wide -- see the sibling narrow-width
        // test below for the ~1564Hz case) -- but deliberately does NOT reset the output IIR
        // filter's own Z-state (see Clear()'s own doc comment: legacy's CFQC::Clear doesn't touch
        // m_iir either), so the FILTERED readout decays toward 0 over subsequent samples rather than
        // snapping there instantly. Feed a generous run of continued silence (no new zero crossing,
        // so the raw estimate stays at the Clear()-reset 0) and confirm the filtered output actually
        // converges to 0, proving the stale 1200Hz estimate was really replaced, not just masked by
        // filter lag.
        double afterClear = 0;
        for (var i = 0; i < SampleRate; i++) // 1 full second, generous settle
        {
            afterClear = counter.ProcessSample(0.0);
        }

        Assert.Equal(0.0, afterClear, tolerance: 1.0);
    }

    [Fact]
    public void Clear_DoesNotResetWidth_NarrowClampStaysNarrowAfterClear()
    {
        // Clear() and SetWidth() are separate legacy calls (CFQC::Clear vs CFQC::SetWidth) -- proves
        // this port keeps them independent too, matching AnalogFmSstvDecoder.cs's own planned call
        // sites (Clear() at InitializeAfc/EndOfImage, SetWidth() at narrow-mode transitions).
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        counter.SetWidth(isNarrow: true);
        counter.Clear();

        var freq = SampleSineWaveFrequency(counter, targetHz: 1200.0, durationMs: 100);

        // Still narrow after Clear() -- the narrow low clamp (1800) should still apply, not the wide
        // one (1000), proving SetWidth's effect survived the Clear() call.
        Assert.Equal(1800.0, freq, tolerance: 1.0);
    }

    [Fact]
    public void Clear_AfterSetWidthNarrow_ResetsToNarrowZeroFqEquivalent_Not0Hz()
    {
        // Round-1 code-review finding: legacy's ZEROFQ (sstv.h:397, -1900/400) is a FIXED normalized
        // value that denormalizes to a DIFFERENT real-Hz value depending on the CURRENT width when
        // read -- 0Hz wide (1900 + (-4.75)*400), but ~1564Hz narrow (2172 + (-4.75)*128). Genuinely
        // reachable, not a theoretical corner: legacy's own CSSTVDEM::Start calls SetWidth BEFORE
        // Clear() (sstv.cpp:1719,1722), so a narrow-mode Start() really does clear to ~1564Hz, not
        // 0Hz. An earlier version of Clear() hardcoded 0.0 unconditionally -- wrong for this case.
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        counter.SetWidth(isNarrow: true);

        // Build up real state at a different frequency first, so a stale-value bug (still reading
        // the pre-Clear() estimate) wouldn't accidentally land near 1564Hz either.
        var settled = SampleSineWaveFrequency(counter, targetHz: 2172.0, durationMs: 100);
        Assert.Equal(2172.0, settled, tolerance: 5.0);

        counter.Clear();

        double afterClear = 0;
        for (var i = 0; i < SampleRate; i++) // 1 full second, generous settle for the output filter
        {
            afterClear = counter.ProcessSample(0.0);
        }

        Assert.Equal(1564.0, afterClear, tolerance: 2.0);
    }

    [Fact]
    public void SetWidth_MidStream_RescalesLiveHeldEstimate_NotJustClearedValue()
    {
        // Round-2 code-review finding: legacy stores m_fq NORMALIZED, so ANY width change
        // re-denormalizes whatever is CURRENTLY HELD -- a real measurement, not just Clear()'s reset
        // value. An earlier version of SetWidth only recomputed the cleared value, leaving a live
        // held estimate frozen at its old real-Hz number across a mid-stream flip until the next
        // zero crossing corrected it.
        //
        // Hand-crafted square wave (not a sine) for a reproducible crossing, but the exact expected
        // Hz is measured empirically from a control run rather than hand-derived from offset/count
        // arithmetic -- an earlier version of this test hand-computed count=19.5 and got it wrong:
        // ProcessSample's own _prevSample starts at 0.0 (CLR default), and 0.0 counts as ">= 0" in
        // the crossing check, so the VERY FIRST call (-1.0 against that implicit 0.0) is itself
        // treated as a (unrecorded, count<1) crossing with its own side effect on
        // _fractionalCrossingSampleIndex -- a real, easy-to-miss detail of this class's own state
        // machine (and, per CFQC's identical m_d=0 initial state, legacy's too), not something worth
        // re-deriving by hand when the class itself can just be asked.
        static void FeedCraftedSequence(ZeroCrossingFrequencyCounter c)
        {
            for (var i = 0; i < 20; i++)
            {
                c.ProcessSample(-1.0);
            }

            c.ProcessSample(1.0); // the one real, recorded crossing
        }

        static double SettleOnHeldValue(ZeroCrossingFrequencyCounter c)
        {
            // No new zero crossing during this loop -- continued +1.0 matches the last real sample's
            // sign, so the output filter converges on whatever raw value is currently held (a fresh
            // measurement, or -- after SetWidth -- the rescaled one) rather than a new measurement.
            double last = 0;
            for (var i = 0; i < SampleRate; i++) // 1 full second, generous settle
            {
                last = c.ProcessSample(1.0);
            }

            return last;
        }

        var controlCounter = new ZeroCrossingFrequencyCounter(SampleRate);
        FeedCraftedSequence(controlCounter);
        var measuredWideHz = SettleOnHeldValue(controlCounter); // ground truth, no SetWidth involved

        // Legacy's rescale: newCenter + (oldHz - oldCenter)/oldBwh * newBwh (wide center=1900,
        // bwh=400; narrow center=2172, bwh=128 -- NARROW_BWH, sstv.h:445).
        var expectedRescaledHz = 2172.0 + (measuredWideHz - 1900.0) / 400.0 * 128.0;

        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        FeedCraftedSequence(counter);
        counter.SetWidth(isNarrow: true); // rescales the just-recorded measurement, no new crossing yet
        var afterRescale = SettleOnHeldValue(counter);

        Assert.Equal(expectedRescaledHz, afterRescale, tolerance: 2.0);
    }

    [Fact]
    public void ProcessSample_CrossingCloserThanOneSampleToThePrevious_HoldsPreviousEstimate()
    {
        // Closes a coverage gap flagged by Tier A Batch 7 chunk 7c (docs/functional-audit-playbook.md):
        // CFQC::Do's `if(count >= 1.0)` gate (sstv.cpp:421/449) discards any half-period shorter
        // than one sample (it would imply a frequency above Nyquist) and KEEPS the previous
        // estimate -- while still advancing the crossing-position bookkeeping the gate itself reads
        // from. Reachable through sub-sample interpolation alone. Driven from a FRESH counter (not
        // one that's already settled on a real tone) so every sample index and crossing offset is
        // exactly known, rather than depending on unknown state left over from a settling phase.
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        var beforeGatedCrossing = counter.CurrentFrequencyHzForTests; // the cleared default (0Hz, wide)

        counter.ProcessSample(1.0); // sampleIndex 0->1, no crossing (prevSample starts at 0.0, not <0)
        counter.ProcessSample(-1.0); // crossing at sampleIndex 1: offset=0.5, count=1-0-0.5=0.5, gated

        // Without the gate this would become SampleRate*0.5/0.5 = 44100Hz, clamped to 2400Hz --
        // instead the estimate must stay exactly at its pre-crossing value.
        Assert.Equal(beforeGatedCrossing, counter.CurrentFrequencyHzForTests);
    }

    // Options stub backlog item 2 (docs/plans/options-stub-item2-zerocrossing-tuning-plan.md):
    // SmoothingMode/SetTuning coverage. Auditor plan-review finding: a "does behavior differ between
    // modes" check isn't a parity assertion -- every test below is an exact, tolerance-stated
    // comparison, not a vague qualitative one.

    [Fact]
    public void ProcessSample_OffMode_ReturnsRawHeldValueBitExact_NoFiltering()
    {
        // default: m_out = m_fq (sstv.cpp:482) -- the raw, unfiltered sample-and-hold estimate,
        // exactly what CurrentFrequencyHzForTests already exposes.
        var counter = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Off, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);
        var phaseIncrement = 2 * Math.PI * 1200.0 / SampleRate;
        var phase = 0.0;

        for (var i = 0; i < 500; i++)
        {
            phase += phaseIncrement;
            var output = counter.ProcessSample(Math.Sin(phase));
            Assert.Equal(counter.CurrentFrequencyHzForTests, output); // exact, no tolerance
        }
    }

    [Fact]
    public void ProcessSample_FirMode_MatchesAnIndependentMovingAverageOfTheRawHeldValues()
    {
        // The raw zero-crossing measurement (_currentFrequencyHz) is identical regardless of
        // smoothing mode -- only the OUTPUT stage differs. Drive a raw-only (Off-mode) counter and a
        // FIR-mode counter through the IDENTICAL sample sequence: the FIR output at every step must
        // equal an independent MovingAverage fed the raw-only counter's own CurrentFrequencyHzForTests
        // at that same step -- an exact, per-sample dispatch-correctness assertion, not a converged
        // steady-state check (which can't discriminate IIR from FIR from Off, since all three have DC
        // gain 1 at a constant tone).
        const double smoothingHz = 8000; // window = (int)(44100/8000) = 5
        var rawCounter = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Off, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: smoothingHz);
        var firCounter = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Fir, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: smoothingHz);
        var reference = new MovingAverage(5);

        var phaseIncrement = 2 * Math.PI * 1200.0 / SampleRate;
        var phase = 0.0;

        for (var i = 0; i < 500; i++)
        {
            phase += phaseIncrement;
            var sample = Math.Sin(phase);
            rawCounter.ProcessSample(sample);
            var firOutput = firCounter.ProcessSample(sample);
            var expected = reference.Add(rawCounter.CurrentFrequencyHzForTests);

            Assert.Equal(expected, firOutput, precision: 9);
        }
    }

    [Fact]
    public void SetTuning_WindowSizeTruncation_PinnedExactly()
    {
        // Auditor plan-review finding: pin the exact truncation, not just "some averaging happens."
        // sampleRate=11025, smoothingFrequencyHz=2200 -> (int)(11025/2200) = (int)5.011... = 5 (hand-
        // computed here, independently of the production formula, so a production off-by-one bug
        // can't hide behind re-deriving the same expression on both sides).
        var rawCounter = new ZeroCrossingFrequencyCounter(11025, ZeroCrossingSmoothingMode.Off, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);
        var firCounter = new ZeroCrossingFrequencyCounter(11025, ZeroCrossingSmoothingMode.Fir, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);
        var reference = new MovingAverage(5); // hand-pinned, not re-derived

        var phaseIncrement = 2 * Math.PI * 1200.0 / 11025;
        var phase = 0.0;

        for (var i = 0; i < 200; i++)
        {
            phase += phaseIncrement;
            var sample = Math.Sin(phase);
            rawCounter.ProcessSample(sample);
            var firOutput = firCounter.ProcessSample(sample);
            var expected = reference.Add(rawCounter.CurrentFrequencyHzForTests);

            Assert.Equal(expected, firOutput, precision: 9);
        }
    }

    [Fact]
    public void SetTuning_AppliedLive_NoInterveningSetWidth_StillSwitchesSmoothing()
    {
        var counter = new ZeroCrossingFrequencyCounter(SampleRate); // starts Iir
        var iirSettled = SampleSineWaveFrequency(counter, targetHz: 1200.0, durationMs: 50);
        Assert.Equal(1200.0, iirSettled, tolerance: 5.0); // sanity: filtering was actually happening

        counter.SetTuning(ZeroCrossingSmoothingMode.Off, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);

        // Off mode is bit-exact to the raw estimate -- immediately after SetTuning, no settling delay
        // needed, unlike a filter's own transient.
        var afterSwitch = counter.ProcessSample(1.0);
        Assert.Equal(counter.CurrentFrequencyHzForTests, afterSwitch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(33)]
    [InlineData(1000)]
    public void Constructor_OutOfRangeOutputOrder_ClampsTo1Or32(int rawOrder)
    {
        var clampedOrder = Math.Clamp(rawOrder, 1, 32);
        var reference = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Iir, clampedOrder, outputCutoffHz: 900, smoothingFrequencyHz: 2200);
        var underTest = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Iir, rawOrder, outputCutoffHz: 900, smoothingFrequencyHz: 2200);

        var expected = SampleSineWaveFrequency(reference, targetHz: 1200.0, durationMs: 50);
        var actual = SampleSineWaveFrequency(underTest, targetHz: 1200.0, durationMs: 50);

        Assert.Equal(expected, actual, precision: 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(1_000_000.0)] // above Nyquist -- must clamp to sampleRate*0.45, not destabilize
    public void Constructor_OutOfRangeOutputCutoffHz_ClampsToBothBounds(double rawCutoffHz)
    {
        var clampedCutoffHz = Math.Clamp(rawCutoffHz, 1.0, SampleRate * 0.45);
        var reference = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Iir, outputOrder: 3, clampedCutoffHz, smoothingFrequencyHz: 2200);
        var underTest = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Iir, outputOrder: 3, rawCutoffHz, smoothingFrequencyHz: 2200);

        var expected = SampleSineWaveFrequency(reference, targetHz: 1200.0, durationMs: 50);
        var actual = SampleSineWaveFrequency(underTest, targetHz: 1200.0, durationMs: 50);

        Assert.Equal(expected, actual, precision: 9);
        Assert.False(double.IsNaN(actual));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(100.0)] // below the real 500Hz floor
    [InlineData(20000.0)] // above the real 8000Hz ceiling
    public void SetTuning_OutOfRangeSmoothingFrequencyHz_ClampsToBothBounds(double rawSmoothingHz)
    {
        var clampedSmoothingHz = Math.Clamp(rawSmoothingHz, 500.0, 8000.0);
        var reference = new ZeroCrossingFrequencyCounter(SampleRate);
        reference.SetTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 3, outputCutoffHz: 900, clampedSmoothingHz);
        var underTest = new ZeroCrossingFrequencyCounter(SampleRate);
        underTest.SetTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 3, outputCutoffHz: 900, rawSmoothingHz);

        var expected = SampleSineWaveFrequency(reference, targetHz: 1200.0, durationMs: 50);
        var actual = SampleSineWaveFrequency(underTest, targetHz: 1200.0, durationMs: 50);

        Assert.Equal(expected, actual, precision: 9);
        Assert.False(double.IsNaN(actual));
    }

    [Fact]
    public void SetTuning_NaNCutoffAndSmoothingFrequency_DoesNotPropagateNaN()
    {
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);

        counter.SetTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 3, outputCutoffHz: double.NaN, smoothingFrequencyHz: double.NaN);

        var actual = SampleSineWaveFrequency(counter, targetHz: 1200.0, durationMs: 50);
        Assert.False(double.IsNaN(actual));

        // A NaN input substitutes the field's own legacy default (900/2200) before clamping --
        // matches a counter built with those defaults directly.
        var reference = new ZeroCrossingFrequencyCounter(SampleRate);
        reference.SetTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);
        var expected = SampleSineWaveFrequency(reference, targetHz: 1200.0, durationMs: 50);

        Assert.Equal(expected, actual, precision: 9);
    }

    [Fact]
    public void SetTuning_OutOfRangeSmoothingMode_FallsBackToOff_NotIir()
    {
        // sstv.cpp:482's `default:` case is Off, NOT Iir -- a completely different fallback rule from
        // the ABSENT-value default (Iir, CFQC's own ctor). Confirm the dispatch actually lands on Off
        // by comparing against a counter explicitly constructed with Off.
        var reference = new ZeroCrossingFrequencyCounter(SampleRate, ZeroCrossingSmoothingMode.Off, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);
        var underTest = new ZeroCrossingFrequencyCounter(SampleRate, (ZeroCrossingSmoothingMode)99, outputOrder: 3, outputCutoffHz: 900, smoothingFrequencyHz: 2200);

        var expected = SampleSineWaveFrequency(reference, targetHz: 1200.0, durationMs: 50);
        var actual = SampleSineWaveFrequency(underTest, targetHz: 1200.0, durationMs: 50);

        Assert.Equal(expected, actual, precision: 9);
    }
}
