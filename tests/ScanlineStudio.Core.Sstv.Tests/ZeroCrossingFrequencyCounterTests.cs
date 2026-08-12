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
}
