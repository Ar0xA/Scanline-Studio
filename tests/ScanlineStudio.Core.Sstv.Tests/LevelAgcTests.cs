namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="LevelAgc"/> (porting legacy's <c>CLVL</c>, <c>m_agcfast</c>-only
/// branch), checked directly against the ported formula before this class is wired into any decoder
/// call site -- that wiring is separately scoped, later work.
/// </summary>
public class LevelAgcTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void BeforeFirstFix_CurMaxIsZeroAndAgcIsUnity()
    {
        var agc = new LevelAgc(SampleRate);

        // Feed fewer samples than one Fix() window needs (100ms).
        for (var i = 0; i < 100; i++)
        {
            agc.Do(20000.0);
            agc.Fix();
        }

        Assert.Equal(0.0, agc.CurMax);
        Assert.Equal(5.0, agc.Agc(5.0)); // m_agc still 1.0 (Init default, sstv.h:252) -- unchanged
    }

    [Fact]
    public void QuietWindow_FloorsAtDefaultGain()
    {
        var agc = new LevelAgc(SampleRate);
        var cntMax = (int)(SampleRate * 100 / 1000.0);

        // Peak amplitude 20 <= 32 -- legacy's floor branch, sstv.h:289-291.
        for (var i = 0; i < cntMax; i++)
        {
            agc.Do(20.0);
            agc.Fix();
        }

        Assert.Equal(20.0, agc.CurMax);
        Assert.Equal(16384.0 / 32.0, agc.Agc(1.0));
    }

    [Fact]
    public void LoudWindow_AdaptsGainToObservedPeak()
    {
        var agc = new LevelAgc(SampleRate);
        var cntMax = (int)(SampleRate * 100 / 1000.0);

        // Peak amplitude 10000 > 32 -- legacy's adaptive branch, sstv.h:286-287.
        for (var i = 0; i < cntMax; i++)
        {
            agc.Do(i == cntMax / 2 ? 10000.0 : 100.0);
            agc.Fix();
        }

        Assert.Equal(10000.0, agc.CurMax);
        Assert.Equal(16384.0 / 10000.0, agc.Agc(1.0));
    }

    [Fact]
    public void FixIsSelfThrottled_CallingEverySampleMatchesCallingOncePerWindow()
    {
        var perSample = new LevelAgc(SampleRate);
        var oncePerWindow = new LevelAgc(SampleRate);
        var cntMax = (int)(SampleRate * 100 / 1000.0);
        var random = new Random(7);

        for (var window = 0; window < 5; window++)
        {
            for (var i = 0; i < cntMax; i++)
            {
                var sample = random.NextDouble() * 5000.0;
                perSample.Do(sample);
                perSample.Fix(); // called every sample
                oncePerWindow.Do(sample);
            }

            oncePerWindow.Fix(); // called once, after the window's worth of samples
        }

        Assert.Equal(oncePerWindow.CurMax, perSample.CurMax);
        Assert.Equal(oncePerWindow.Agc(1.0), perSample.Agc(1.0));
    }

    [Fact]
    public void Init_ResetsToPowerOnDefaults_EvenAfterAdaptingToALoudWindow()
    {
        // ultracode audit finding #6: legacy resets CLVL at every TX<->RX transition
        // (Sound.cpp:398,443) -- Init() must fully undo whatever gain/peak state a prior RX session
        // adapted to, not just partially.
        var agc = new LevelAgc(SampleRate);
        var cntMax = (int)(SampleRate * 100 / 1000.0);
        for (var i = 0; i < cntMax; i++)
        {
            agc.Do(10000.0);
            agc.Fix();
        }

        Assert.Equal(10000.0, agc.CurMax); // sanity: it really did adapt first

        agc.Init();

        Assert.Equal(0.0, agc.CurMax);
        Assert.Equal(5.0, agc.Agc(5.0)); // m_agc back to 1.0

        // A subsequent quiet window must behave exactly as it would on a freshly-constructed
        // instance -- not carry over any trace of the pre-Init _max accumulator.
        agc.Do(20.0);
        agc.Fix();
        Assert.Equal(0.0, agc.CurMax); // one sample is nowhere near cntMax -- Fix() still no-ops
    }
}
