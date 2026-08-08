using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Auto Stop -- the erratic/weak-signal detector that stops reception and re-arms auto-detection
/// (legacy's <c>sys.m_AutoStop</c>, <c>TMmsstv::AutoStopJob</c>/<c>RxAutoPush</c>,
/// <c>Main.cpp:3884-4035</c>/<c>:6042-6060</c>). Sibling to <see cref="AutoSyncTests"/> -- both features
/// share <see cref="AnalogFmSstvDecoder"/>'s own <c>TryAutoSync</c> method and its <c>m_AutoStopCnt</c>
/// counter. See that method's own doc comment for the two rounds of plan-readiness review this went
/// through -- round 1's three blockers (a guaranteed NRE from calling <c>EndOfImage()</c> synchronously
/// inside <c>TryAutoSync</c>, an unconditional 0.5s dead-time skip that's wrong for this path, and a
/// same-pass ordering race against <c>TryVisLockStateMachine</c>) are why the trigger is deferred to
/// <c>TryProcessBuffer</c>'s own per-line loop instead of applied inline.
/// </summary>
public class AutoStopTests
{
    [Fact]
    public void CleanSignal_EnvelopeSpreadExceeds8192Threshold()
    {
        // Round-1/round-2 plan-review finding 6: the 8192 (and Auto Sync's own 5000) threshold compares
        // against THIS PORT's own SyncEnvelopeDetector output, not legacy's raw m_SyncMax-m_SyncMin on
        // shorts -- an unverified scale assumption. HARD GATE, not a soft assertion: if a clean, strong
        // signal doesn't clear 8192 here, the Auto Stop increment condition (`n<2 || spread<8192`)
        // degenerates to "always true whenever n<4," and Auto Stop would fire on cluster-scatter alone
        // with no real weak-signal requirement -- no duration/round-trip test would ever surface that
        // silently. If this test fails, STOP and re-derive the threshold/scale conversion rather than
        // proceeding to tune the trigger test below around a wrong assumption.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025);

        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;
        const int chunkSize = 256;
        var offset = 0;
        for (; offset < samples.Length && lineCount < 12; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(lineCount >= 12, "Never decoded enough lines -- test setup problem.");
        Assert.NotNull(decoder.LastAutoStopEnvelopeSpreadForTests);
        Assert.True(decoder.LastAutoStopEnvelopeSpreadForTests!.Value > 8192,
            $"Clean-signal envelope spread {decoder.LastAutoStopEnvelopeSpreadForTests} does not clear the 8192 threshold -- " +
            "the port's SyncEnvelopeDetector scale may not match legacy's raw m_SyncMax-m_SyncMin assumption. " +
            "Stop and re-derive the threshold/scale conversion before trusting any Auto Stop trigger test.");
    }

    [Fact]
    public void SustainedNoise_EventuallyTriggersAutoStop()
    {
        // Needs a SUSTAINED erratic stretch, not a brief splice like Auto Sync's own test -- Auto
        // Stop's counter only increments (at most +1/line, and only while n<2, i.e. this line's
        // position doesn't cluster with recent history) and can be pulled back down by Auto Sync's
        // own triggers or the n>=4 stable-cluster branch (-1/-2), so it needs several consecutive
        // qualifying lines to reach its own >=8 threshold.
        //
        // Empirically confirmed (during this feature's own development) that pure SILENCE does NOT
        // work here, despite trivially satisfying `envelopeSpread < 8192`: silence is perfectly
        // reproducible from line to line, so ComputeAutoSyncPosition returns the same degenerate
        // value every time, which reads as a maximally STABLE cluster (n=16, the n>=4 branch), not a
        // scattered one -- _autoStopCnt gets decremented every line and pins at 0 forever. Genuine
        // random noise, by contrast, jitters the computed position line to line, reliably producing
        // n<2 and triggering the increment regardless of envelope amplitude.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var noisy = ReplaceTailWithNoise(samples);

        var decoder = new AnalogFmSstvDecoder(11025, autoStopEnabled: true);
        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;
        decoder.PushSamples(noisy);

        Assert.True(decoder.AutoStopTriggerCountForTests > 0,
            $"Auto Stop never fired against a sustained noisy tail -- final _autoStopCnt={decoder.AutoStopCntForTests}, " +
            $"last envelope spread={decoder.LastAutoStopEnvelopeSpreadForTests}. Test setup problem, or the trigger genuinely never fires.");

        // Code-level review finding: the trigger-count assertion above doesn't, by itself, prove the
        // trigger landed IN the noisy tail rather than some other cause -- tie it to a concrete,
        // independently-checkable effect: EndOfImage() aborts the image early, so it must never have
        // reached its own full ImageHeight (a naturally-completing image reaches ImageHeight regardless
        // of Auto Stop, so seeing fewer lines than that only happens via the abort path).
        Assert.True(lineCount < mode.ImageHeight,
            $"Expected Auto Stop's own EndOfImage() to abort the image before natural completion -- decoded {lineCount}/{mode.ImageHeight} lines.");
    }

    [Fact]
    public void AutoStopEnabledFalse_NeverTriggers_ButBookkeepingStillRuns()
    {
        // Same scenario the test above proves DOES trigger when enabled -- a real end-to-end A/B,
        // matching AutoSyncTests.cs's own AutoSyncEnabledFalse_... convention. _autoStopCnt must still
        // advance (Main.cpp:3886's own outer gate is constant-true in this port, see TryAutoSync's own
        // doc comment) -- only the trigger condition itself reads _autoStopEnabled.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var noisy = ReplaceTailWithNoise(samples);

        var decoder = new AnalogFmSstvDecoder(11025, autoStopEnabled: false);
        decoder.PushSamples(noisy);

        Assert.Equal(0, decoder.AutoStopTriggerCountForTests);
        Assert.True(decoder.AutoStopCntForTests > 0,
            "Bookkeeping (_autoStopCnt) must still advance even while AutoStopEnabled=false.");
    }

    [Fact]
    public void Triggering_FiresDecodeRestartedWithAbandonedMode_AndDecoderRecoversForNextTransmission()
    {
        // Proves EndOfImage(applyDeadTime:false)'s reset genuinely re-arms auto-detection (not a
        // wedged state) and that DecodeRestarted fires with the OLD/abandoned mode, matching that
        // event's existing contract (ISstvDecoder.cs's own doc comment).
        var abandonedMode = SstvModeRegistry.Robot36;
        var abandonedSamples = EncodeRealTransmission(abandonedMode, out _);
        var noisy = ReplaceTailWithNoise(abandonedSamples);

        var decoder = new AnalogFmSstvDecoder(11025, autoStopEnabled: true);

        // First-wins (code-level review finding: an earlier version used plain assignment, last-wins,
        // leaving the assertion below dependent on exactly one restart firing during this push) --
        // pins the specific Auto Stop restart this test exists to observe, not whichever fires last.
        SstvModeDefinition? restartedFor = null;
        decoder.DecodeRestarted += m => restartedFor ??= m;
        decoder.PushSamples(noisy);

        Assert.True(decoder.AutoStopTriggerCountForTests > 0, "Test setup problem -- Auto Stop never fired.");
        Assert.NotNull(restartedFor);
        Assert.Equal(abandonedMode.Id, restartedFor!.Id);

        // Fresh clean transmission of a DIFFERENT mode, to prove the decoder isn't somehow still
        // partially locked onto the abandoned one.
        var recoveryMode = SstvModeRegistry.ScottieS1;
        var recoverySamples = EncodeRealTransmission(recoveryMode, out _);

        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected ??= m;
        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;

        decoder.PushSamples(recoverySamples);

        Assert.NotNull(detected);
        Assert.Equal(recoveryMode.Id, detected!.Id);
        Assert.True(lineCount > 0, "Decoder failed to decode any lines of the recovery transmission -- suggests a wedged state after Auto Stop.");
    }

    [Fact]
    public async Task Avt_NeverRunsAutoStopBookkeepingAtAll()
    {
        // AVT exclusion is free via InitializeSlant nulling _slantTracker for AVT (ApplySlantTracking
        // returns immediately, before ever reaching TryAutoSync) -- same mechanism AutoSyncTests.cs's
        // own Avt_NeverRunsAutoSyncBookkeepingAtAll test already confirms for Auto Sync's own fields;
        // confirmed independently here for Auto Stop's own fields since they're new.
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var decoder = new AnalogFmSstvDecoder(11025, autoStopEnabled: true);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        decoder.PushSamples(samples.ToArray());

        Assert.Equal(0, decoder.AutoStopCntForTests);
        Assert.Equal(0, decoder.AutoStopTriggerCountForTests);
    }

    [Fact]
    public void ManualReSync_ResetsAutoStopCnt()
    {
        // Mirrors AutoSyncTests.cs's own ManualReSync_ResetsAutoSyncObservationCount_... test --
        // PerformReSync's own new addition (KRFSClick's real m_AutoStopCnt=0, Main.cpp:14014).
        //
        // Code-level review finding: a clean, smoothly-drifting signal (the original version of this
        // test) never actually drives _autoStopCnt above 0 in the first place (smooth drift reads as a
        // stable n>=4 cluster, which DECREMENTS the counter, not increments it -- same reasoning as
        // SustainedNoise_EventuallyTriggersAutoStop's own doc comment) -- so asserting `== 0` after
        // ReSync proved nothing; it would still pass with PerformReSync's own reset deleted entirely.
        // Fixed by pushing the same noisy-tail signal used by the trigger tests, but stopping as soon
        // as _autoStopCnt first goes positive (well before its own >=8 trigger threshold), so the
        // reset actually has non-zero state to prove it clears.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var noisy = ReplaceTailWithNoise(samples);
        var decoder = new AnalogFmSstvDecoder(11025, autoStopEnabled: true);

        const int chunkSize = 256;
        var offset = 0;
        for (; offset < noisy.Length && decoder.AutoStopCntForTests == 0; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, noisy.Length - offset);
            decoder.PushSamples(noisy.AsMemory(offset, length));
        }

        Assert.True(decoder.AutoStopCntForTests is > 0 and < 8,
            $"Test setup problem -- expected _autoStopCnt to go positive but stay below the trigger threshold; got {decoder.AutoStopCntForTests} (trigger count {decoder.AutoStopTriggerCountForTests}).");

        decoder.RequestReSync();
        decoder.PushSamples(noisy.AsMemory(offset, Math.Min(64, noisy.Length - offset)));

        Assert.Equal(0, decoder.AutoStopCntForTests);
    }

    // Replaces the back half of a real transmission with deterministic random noise -- see
    // SustainedNoise_EventuallyTriggersAutoStop's own doc comment for why noise (not silence) is
    // needed to make ComputeAutoSyncPosition genuinely scatter line to line.
    private static float[] ReplaceTailWithNoise(float[] samples)
    {
        var noiseStart = samples.Length / 2;
        var noisy = (float[])samples.Clone();
        var rng = new Random(12345);
        for (var i = noiseStart; i < noisy.Length; i++)
        {
            noisy[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.3f;
        }

        return noisy;
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode, out ArrayImageSource sourceImage)
        => EncodeRealTransmissionAtRate(mode, 11025, out sourceImage);

    private static float[] EncodeRealTransmissionAtRate(SstvModeDefinition mode, int encodeSampleRate, out ArrayImageSource sourceImage)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        sourceImage = image;

        var encoder = new AnalogFmSstvEncoder(encodeSampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, image).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
