using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// ui_transition_plan.md step 12 (Auto-save RX audio): <see cref="ISstvDecoder.ReceptionSequence"/>'s
/// contract, isolated from the rest of the feature per this project's own "chop into pieces, test
/// each piece before wiring" convention -- these tests exercise only the identity primitive itself,
/// not the audio capture buffer or the correlator that will consume it.
/// </summary>
public class ReceptionSequenceTests
{
    [Fact]
    public void AnalogFmSstvDecoder_FreshInstance_StartsAtZero()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        Assert.Equal(0, decoder.ReceptionSequence);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_OneModeDetected_BumpsToOne()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        var mode = SstvModeRegistry.Robot36;
        var samples = await EncodeFlatImageAsync(mode, decoder.SampleRate);

        decoder.PushSamples(samples);

        Assert.Equal(1, decoder.ReceptionSequence);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_DominantOrderingRestart_BumpsOncePerModeDetected_NeverOnDecodeRestarted()
    {
        // Same truncated-first-transmission scenario as MidReceptionRestartTests -- a hard cut
        // mid-image into a new header, the dominant ordering (DecodeRestarted for the abandoned
        // image, THEN ModeDetected for the new one, confirmed by that test's own restartCount==1
        // alongside detectedModes.Count==2). ReceptionSequence must land at exactly 2 -- one bump per
        // real ModeDetected, none from the DecodeRestarted in between.
        var mode = SstvModeRegistry.MartinM1;
        var encoder = new AnalogFmSstvEncoder(11025);

        var samples1 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, CreateFlatImage(mode)))
        {
            samples1.Add(sample);
        }

        var truncatedSamples1 = samples1.Take(samples1.Count * 3 / 10).ToArray();

        var samples2 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, CreateFlatImage(mode)))
        {
            samples2.Add(sample);
        }

        var combined = truncatedSamples1.Concat(samples2).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var modeDetectedCount = 0;
        var restartCount = 0;
        var sequenceAtEachModeDetected = new List<long>();
        decoder.ModeDetected += _ =>
        {
            modeDetectedCount++;
            sequenceAtEachModeDetected.Add(decoder.ReceptionSequence);
        };
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(combined);

        Assert.Equal(2, modeDetectedCount);
        Assert.Equal(1, restartCount);
        Assert.Equal(2, decoder.ReceptionSequence);
        // Not just the final value -- each ModeDetected must see the value AS OF ITS OWN raise (this
        // is what "bumped BEFORE the raise" buys a subscriber), not a stale pre-increment snapshot.
        Assert.Equal([1L, 2L], sequenceAtEachModeDetected);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_TwoIndependentSubscribers_ObserveTheIdenticalValueForTheSameRaise()
    {
        // The whole point of bumping before fan-out (see ISstvDecoder.ReceptionSequence's own doc
        // comment): every subscriber of the SAME raise reads the SAME value regardless of
        // subscription order -- this is what lets the audio side and the history recorder agree on
        // one reception's identity without coordinating with each other.
        var decoder = new AnalogFmSstvDecoder(11025);
        var mode = SstvModeRegistry.Robot36;
        var samples = await EncodeFlatImageAsync(mode, decoder.SampleRate);

        long? seenByFirstSubscriber = null;
        long? seenBySecondSubscriber = null;
        decoder.ModeDetected += _ => seenByFirstSubscriber = decoder.ReceptionSequence;
        decoder.ModeDetected += _ => seenBySecondSubscriber = decoder.ReceptionSequence;

        decoder.PushSamples(samples);

        Assert.Equal(1L, seenByFirstSubscriber);
        Assert.Equal(1L, seenBySecondSubscriber);
    }

    [Fact]
    public void RestartableSstvDecoder_FreshInstance_StartsAtZero()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true);
        Assert.Equal(0, decoder.ReceptionSequence);
    }

    [Fact]
    public async Task RestartableSstvDecoder_SurvivesAPeriodicSwap_NeverResetsOrReuses()
    {
        // Same idle-silence-crosses-warningThresholdSamples technique as
        // StationIdDecodeEnabled_ConstructorValue_SurvivesAPeriodicSwap above -- proves this is the
        // WRAPPER's own counter, not delegated to whatever the current _inner happens to report (a
        // per-inner counter would reset to 0 and reuse ids across the swap this test forces). The
        // thresholds are set well ABOVE a real Robot36 image's own sample count (~397K at 11025 Hz)
        // so the real-audio push below doesn't itself trip the swap -- only the idle silence after it
        // does, keeping the two triggers cleanly separated.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 500_000, criticalThresholdSamples: 5_000_000);
        var mode = SstvModeRegistry.Robot36;
        var samples = await EncodeFlatImageAsync(mode, RestartableSstvDecoder.ProductionSampleRate);

        decoder.PushSamples(samples);
        Assert.Equal(1, decoder.ReceptionSequence);
        Assert.Equal(0, decoder.RestartCountForTests); // sanity: the real-audio push alone must not have swapped

        // The threshold check runs at the START of a PushSamples call, against the cumulative count
        // BEFORE this call's own samples are added -- so crossing warningThresholdSamples=500_000
        // takes two more pushes: one to actually push the count past it, and a following one whose
        // own check then observes the already-crossed total and swaps (same two-call shape
        // StationIdDecodeEnabled_ConstructorValue_SurvivesAPeriodicSwap above uses).
        decoder.PushSamples(new float[150_000]); // idle silence -- pushes cumulative count past 500_000
        decoder.PushSamples(new float[1]); // idle silence -- this call's own check now sees it crossed

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.Equal(1, decoder.ReceptionSequence); // unaffected by the swap itself -- no ModeDetected fired for it

        decoder.PushSamples(samples);
        Assert.Equal(2, decoder.ReceptionSequence); // continues from the WRAPPER's own counter, not reset by the fresh inner
    }

    private static async Task<float[]> EncodeFlatImageAsync(SstvModeDefinition mode, int sampleRate)
    {
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, CreateFlatImage(mode)))
        {
            samples.Add(sample);
        }

        return [.. samples];
    }

    private static ArrayImageSource CreateFlatImage(SstvModeDefinition mode)
    {
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        return new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
    }
}
