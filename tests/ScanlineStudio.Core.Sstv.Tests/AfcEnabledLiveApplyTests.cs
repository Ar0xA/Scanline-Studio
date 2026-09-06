using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>AfcEnabled is live-settable and takes effect MID-IMAGE, matching legacy's own
/// <c>SBAFCClick</c> (<c>Main.cpp:6013-6016</c>): it assigns <c>m_afc</c> and calls <c>InitAFC()</c>
/// on the OFF transition only, and <c>m_afc</c> is then read per sample
/// (<c>sstv.cpp:2258</c>/<c>2263</c>/<c>2267</c>/<c>2270</c>).
///
/// These tests cover the toggle's own edges. <see cref="AfcOffsetGateTests"/> remains the standing
/// gate proving AFC actually corrects a known tuning error at all.</summary>
public sealed class AfcEnabledLiveApplyTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void AfcEnabled_LiveSetterAppliesOnNextPushSamples()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate, afcEnabled: true);
        Assert.True(decoder.AfcEnabled);

        decoder.AfcEnabled = false;
        Assert.True(decoder.AfcEnabled, "Deferred -- must not apply until the next PushSamples call drains it.");

        decoder.PushSamples(new float[8]);
        Assert.False(decoder.AfcEnabled);
    }

    /// <summary>The OFF edge must do legacy's whole <c>InitAFC()</c>, not just stop measuring: the
    /// tracker goes away, the reported correction goes null, and the retune returns to nominal. A
    /// version that only nulled the tracker would leave the five AFC-retuned resonators stranded at
    /// the last correction with nothing left to update them.</summary>
    [Fact]
    public async Task AfcEnabled_OffMidImage_DropsTheCorrectionAndResetsTheRetune()
    {
        var (decoder, samples) = await BuildAsync(offsetHz: 60.0, afcEnabled: true);
        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected = m;

        // Both pushes stop SHORT of the end. End-of-image nulls the tracker, the correction and the
        // retune by itself, so running the transmission out would make every assertion below pass
        // whatever the toggle did.
        var half = samples.Length / 2;
        var threeQuarters = (samples.Length / 4) * 3;
        decoder.PushSamples(samples.AsMemory(0, half));

        Assert.NotNull(detected);
        Assert.True(decoder.HasAfcTrackerForTests, "AFC should have locked within the first half of the image.");
        Assert.NotNull(decoder.SyncFrequencyCorrectionHz);
        Assert.NotNull(decoder.LastAppliedAfcRetuneHzForTests);

        decoder.AfcEnabled = false;
        decoder.PushSamples(samples.AsMemory(half, threeQuarters - half));

        Assert.False(decoder.HasAfcTrackerForTests);
        Assert.Null(decoder.SyncFrequencyCorrectionHz);
        Assert.Null(decoder.LastAppliedAfcRetuneHzForTests);

        // The three assertions above are NOT enough on their own, and this is the whole reason these
        // four exist: `_lastAppliedAfcRetuneHz = null` is assigned one line ABOVE the five Retune(0)
        // calls, so deleting every one of them still leaves all three passing. Read the detectors'
        // realized centre frequencies directly instead. The five-detector reset is the exact item a
        // principal review caught as one short, so it must be pinned by a test rather than by
        // review attention.
        Assert.Equal(1200.0, decoder.SyncEnvelopeDetectorForTests?.AppliedCenterFrequencyHzForTests ?? 1200.0, tolerance: 0.01);
        var (d19, fskSpace, d12) = decoder.NewlyRetunedDetectorFrequenciesForTests;
        Assert.Equal(1900.0, d19, tolerance: 0.01);
        Assert.Equal(VisHeader.NarrowSpaceFrequencyHz, fskSpace, tolerance: 0.01);
        Assert.Equal(1200.0, d12, tolerance: 0.01);
        Assert.Equal(1200.0, decoder.VisLockDetectorFrequenciesForTests.D12, tolerance: 0.01);
    }

    /// <summary>The ON edge builds a FRESH tracker mid-image and it locks. Legacy does not call
    /// <c>InitAFC</c> on this edge, and nothing touches AFC state while the flag is off, so starting
    /// from nominal is legacy-equivalent.
    ///
    /// The cursor re-base is asserted too: with the tracker null the cursor is frozen, so on the ON
    /// edge it must jump forward rather than let the rebuilt tracker walk audio that was already
    /// decoded uncorrected.</summary>
    [Fact]
    public async Task AfcEnabled_OnMidImage_BuildsAFreshTrackerThatLocks()
    {
        // On-frequency, unlike the off-edge test: with AFC constructed off there is nothing to pull a
        // mistuned header back into range, so an offset transmission may never lock at all and the
        // test would pass or fail for a reason that has nothing to do with the toggle.
        var (decoder, samples) = await BuildAsync(offsetHz: 0.0, afcEnabled: false);
        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected = m;

        // Stops short of the end, same reason as the off-edge test above.
        var half = samples.Length / 2;
        var threeQuarters = (samples.Length / 4) * 3;
        decoder.PushSamples(samples.AsMemory(0, half));

        Assert.NotNull(detected);
        Assert.False(decoder.HasAfcTrackerForTests, "AFC was constructed off -- no tracker should exist.");
        Assert.Null(decoder.SyncFrequencyCorrectionHz);

        decoder.AfcEnabled = true;
        decoder.PushSamples(samples.AsMemory(half, threeQuarters - half));

        Assert.True(decoder.HasAfcTrackerForTests);
        Assert.NotNull(decoder.SyncFrequencyCorrectionHz);

        // ApplyAfcCorrections early-returns while the tracker is null, so the cursor stayed wherever
        // the constructed-off decoder left it -- at 0, since a tracker never existed. The ON edge
        // re-bases it to _consumedSamples. Without that it would start from 0 and walk the new
        // tracker across the whole first half.
        Assert.True(
            decoder.AfcProcessedUpToForTests > half / 2,
            $"AFC cursor {decoder.AfcProcessedUpToForTests} was not re-based past the audio decoded while AFC was off.");
    }

    /// <summary>Before a mode locks there is no tracker to tear down and no resonator that was ever
    /// retuned, so both edges must be no-ops rather than throwing or disturbing header search.</summary>
    [Fact]
    public void AfcEnabled_TogglingBeforeAnyLock_IsANoOp()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate, afcEnabled: true);

        decoder.AfcEnabled = false;
        decoder.PushSamples(new float[SampleRate]);
        decoder.AfcEnabled = true;
        decoder.PushSamples(new float[SampleRate]);

        Assert.True(decoder.AfcEnabled);
        Assert.False(decoder.HasAfcTrackerForTests);
        Assert.Null(decoder.LastAppliedAfcRetuneHzForTests);
    }

    private static async Task<(AnalogFmSstvDecoder Decoder, float[] Samples)> BuildAsync(double offsetHz, bool afcEnabled)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == "martin-m1");
        var source = ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var samples = new List<float>();
        await foreach (var s in encoder.EncodeWithCarrierOffsetAsync(mode, source, offsetHz))
        {
            samples.Add(s);
        }

        // Sync restart pinned off: a mid-reception restart re-enters InitializeAfc, which would apply
        // the toggle by a different path and let these tests pass or fail for the wrong reason.
        return (new AnalogFmSstvDecoder(SampleRate, afcEnabled: afcEnabled, syncRestartEnabled: false), samples.ToArray());
    }
}
