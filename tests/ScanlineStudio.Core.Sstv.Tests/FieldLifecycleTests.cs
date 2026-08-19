using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// D0 (final whole-file field-lifecycle pass, docs/functional-audit-playbook.md's "Chunking a
/// mega-file" section) round-1 regression tests: for every mutable field, is it correctly reset or
/// preserved at every teardown/reset path. These two tests pin the round's two real findings.
/// </summary>
public class FieldLifecycleTests
{
    [Fact]
    public async Task ZeroCrossingDemodulator_CurrentFrequencyResetsToTheClearedValue_AtEndOfImage()
    {
        // Round-1 D0-audit finding: _zeroCrossingDemodulator (the main-demod-role instance, active
        // only when DemodType.ZeroCrossing is selected) was never Clear()ed at any teardown path,
        // unlike its sibling _afcZeroCrossingCounter, which is cleared at both EndOfImage and
        // InitializeAfc. Captures the running frequency estimate mid-decode (proving it genuinely
        // diverges from its cleared value, 0.0 Hz at the wide width, per Clear()'s own doc comment),
        // then reads it again once the whole encoded stream -- image plus the encoder's own trailing
        // footer -- has been pushed, which drives the decoder through its own NATURAL end-of-image
        // teardown internally. Deliberately does not call EndOfImageForTests directly: a single
        // PushSamples call already exercises the real teardown path, so this proves EndOfImage()
        // itself resets the value, not just that the call compiles in isolation.
        var mode = SstvModeRegistry.MartinM1;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgb24((byte)(i % 256), (byte)((i / 7) % 256), (byte)((i / 13) % 256));
        }

        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025, demodType: DemodType.ZeroCrossing);
        double? midDecodeFrequencyHz = null;
        decoder.LineDecoded += _ => midDecodeFrequencyHz ??= decoder.ZeroCrossingDemodulatorCurrentFrequencyHzForTests;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(midDecodeFrequencyHz);
        Assert.NotEqual(0.0, midDecodeFrequencyHz!.Value);
        Assert.Equal(0.0, decoder.ZeroCrossingDemodulatorCurrentFrequencyHzForTests);
    }

    [Fact]
    public async Task SlantPpmAndSyncFrequencyCorrectionHz_AreNullDuringThePendingAnchorCorrectionWindow()
    {
        // Round-1 D0-audit finding: a non-AVT mid-reception restart (ForceMode here drives the exact
        // same Commit()/_pendingAnchorCorrectionMode pipeline TryVisLockStateMachine's own restart
        // path uses) reassigns _mode to the NEW mode immediately inside Commit(), but _slantTracker/
        // _afcTracker are not rebuilt until FinalizeAnchorAndStartDecoding resolves
        // _pendingAnchorCorrectionMode a few transmission lines later. Before this round's fix, a
        // poll landing in that window would read the ABANDONED image's tracker values attributed to
        // the new mode instead of null. The restart must land MID-RECEPTION (mode already locked,
        // image not yet complete) for this to be a real regression test of AbandonInProgressImage's
        // own deliberate non-null-ing of the trackers -- EndOfImage's OWN teardown already nulls both
        // trackers directly, so forcing a restart only after a full image completes naturally would
        // prove nothing. Only the first third of the encoded stream is pushed for exactly this
        // reason: enough to lock the mode and decode several real lines (accumulating a genuinely
        // non-zero drift/correction via the same deliberate encoder/decoder rate-mismatch technique
        // RestartableSstvDecoderTests' own AutoSlant positive control uses), nowhere near enough for
        // the image or its footer to complete on its own.
        var mode = SstvModeRegistry.MartinM1;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 11025;
        const double trueSampleRate = declaredSampleRate * 1.0005;
        var encoder = new AnalogFmSstvEncoder((int)trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, autoSlantEnabled: true);
        var linesDecoded = 0;
        decoder.LineDecoded += _ => linesDecoded++;

        decoder.PushSamples(samples.Take(samples.Count / 3).ToArray());

        // Sanity: the first lock genuinely produced non-null telemetry, still mid-reception (not yet
        // idle again) -- otherwise the assertions below could pass vacuously.
        Assert.True(linesDecoded > 0, "Test setup problem: expected real lines decoded before the forced restart.");
        Assert.NotNull(decoder.SlantPpm);

        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[10]); // any push drains the pending ForceMode() request

        // Still inside the pending-anchor window: 10 silent samples is nowhere near the several
        // full transmission lines TryResolveSyncAnchorCorrection needs before it can resolve.
        Assert.Null(decoder.SlantPpm);
        Assert.Null(decoder.SyncFrequencyCorrectionHz);
    }
}
