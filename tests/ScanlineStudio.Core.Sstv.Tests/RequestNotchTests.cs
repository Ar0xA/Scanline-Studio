using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// The RX Notch filter's group-delay compensation wiring -- port of legacy's
/// <c>TMmsstv::PBoxFFTMouseDown</c>/<c>PBoxFFTMouseMove</c> (<c>Main.cpp:14360-14390</c>) and
/// <c>CSSTVDEM::Do</c>'s <c>m_Skip</c> consumption (<c>sstv.cpp:2271-2283</c>). See
/// <see cref="AnalogFmSstvDecoder"/>'s own <c>ApplyPendingNotchRequest</c>/
/// <c>ApplyNotchDisableShift</c> doc comments for the full design -- these tests exercise the
/// WIRING (cursor deltas, idle no-ops, the two asymmetric compensation directions), not the
/// filter's own DSP math (that's <see cref="NotchFilterTests"/>'s job).
/// </summary>
public class RequestNotchTests
{
    [Fact]
    public void RequestNotch_BeforeAnyLockEverHappens_IsNoOpAndDoesNotThrow()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestNotch(true, 2400.0);
        decoder.PushSamples(new float[64]); // never matches any header -- stays idle

        Assert.True(decoder.NotchEnabledForTests); // the filter itself still turns on...
        Assert.Equal(0, decoder.PendingSkipSamplesForTests); // ...but no compensation, nothing locked yet
        Assert.Equal(0, decoder.ConsumedSamplesForTests);

        decoder.RequestNotch(false, null);
        decoder.PushSamples(new float[64]);

        Assert.False(decoder.NotchEnabledForTests);
        Assert.Equal(0, decoder.PendingSkipSamplesForTests);
        Assert.Equal(0, decoder.ConsumedSamplesForTests);
    }

    [Fact]
    public void RequestNotch_EnableWhileLocked_AppliesForwardSkipEqualToHalfTap()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);

        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;

        var offset = DecodeUntilLinesComplete(decoder, samples, 3, out lineCount);
        Assert.True(lineCount >= 3, "Never decoded enough lines -- test setup problem.");

        var expectedHalfTap = new NotchFilter(11025).Tap / 2;
        var consumedBefore = decoder.ConsumedSamplesForTests;

        decoder.RequestNotch(true, 2400.0);
        decoder.PushSamples(samples.AsMemory(offset, 1));
        offset++;

        // Applied via the EXISTING forward-drain (_pendingSkipSamples/DrainPendingSkip), same
        // mechanism RequestReSync's own positive-direction correction uses -- may take more than
        // one push to fully drain, so assert the CUMULATIVE delta once drained, not an immediate one.
        var drainIterations = 0;
        while (decoder.PendingSkipSamplesForTests is not 0)
        {
            Assert.True(offset < samples.Length && drainIterations < 5000, "Drain never reached 0 -- test setup problem.");
            decoder.PushSamples(samples.AsMemory(offset, 1));
            offset++;
            drainIterations++;
        }

        Assert.Equal(expectedHalfTap, decoder.ConsumedSamplesForTests - consumedBefore);
    }

    [Fact]
    public void RequestNotch_DisableWhileLocked_RewindsConsumedSamplesInstantaneously_WithoutMovingSlantProcessedUpTo()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);

        // Enable BEFORE any lock -- no compensation needed for this transition (see the idle test
        // above), just gets the filter running so there's something real to disable once locked.
        decoder.RequestNotch(true, 4000.0); // well away from any real SSTV tone content
        decoder.PushSamples(new float[1]);
        Assert.True(decoder.NotchEnabledForTests);

        var offset = DecodeUntilLinesComplete(decoder, samples, 3, out var lineCount);
        Assert.True(lineCount >= 3, "Never decoded enough lines -- test setup problem.");

        var expectedHalfTap = new NotchFilter(11025).Tap / 2;
        var consumedBefore = decoder.ConsumedSamplesForTests;
        var idealLineStartBefore = decoder.IdealLineStartSampleForTests;
        var slantProcessedUpToBefore = decoder.SlantProcessedUpToForTests;

        decoder.RequestNotch(false, null);
        decoder.PushSamples(samples.AsMemory(offset, 1)); // single push -- the rewind is instantaneous, no drain loop needed
        offset++;

        Assert.False(decoder.NotchEnabledForTests);
        Assert.Equal(consumedBefore - expectedHalfTap, decoder.ConsumedSamplesForTests);
        Assert.Equal(idealLineStartBefore - expectedHalfTap, decoder.IdealLineStartSampleForTests, precision: 6);
        // Load-bearing: rewinding this would re-feed the sync-envelope detector / RxLineStagingBuffer
        // a second time over the same span -- see ApplyNotchDisableShift's own doc comment for why.
        Assert.Equal(slantProcessedUpToBefore, decoder.SlantProcessedUpToForTests);
        Assert.Equal(0, decoder.PendingSkipSamplesForTests); // instantaneous -- nothing left to drain
    }

    [Fact]
    public void RequestNotch_SameStateRerequest_AppliesNoCompensation()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestNotch(true, 2400.0);
        decoder.PushSamples(new float[1]);

        var offset = DecodeUntilLinesComplete(decoder, samples, 3, out var lineCount);
        Assert.True(lineCount >= 3, "Never decoded enough lines -- test setup problem.");

        var expectedHalfTap = new NotchFilter(11025).Tap / 2;
        var consumedBefore = decoder.ConsumedSamplesForTests;

        // Already enabled -- retuning (not toggling) must not trigger group-delay compensation.
        decoder.RequestNotch(true, 1700.0);
        decoder.PushSamples(samples.AsMemory(offset, 1));

        Assert.Equal(0, decoder.PendingSkipSamplesForTests);
        // A real compensation would jump ConsumedSamplesForTests by expectedHalfTap (48 samples at
        // 11025Hz) -- one pushed sample advances it by at most 1, nowhere close, confirming no
        // correction was queued (an exact +1 isn't guaranteed: _consumedSamples only advances once a
        // full line's worth of backlog lets TryProcessBuffer actually decode, not 1:1 per raw sample).
        Assert.True(decoder.ConsumedSamplesForTests - consumedBefore < expectedHalfTap);
        Assert.Equal(1700.0, decoder.NotchFrequencyForTests);
    }

    [Fact]
    public void RequestNotch_DisableThenReenableWithNoFrequency_ResumesAtLastTunedFrequency()
    {
        // Code-review regression: legacy's own CNotch::m_freq (fir.h:131) is a persistent member
        // that survives a disable -- a re-enable with no explicit frequency must resume at the
        // last-tuned value, not silently fall back to the 2400 Hz default.
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestNotch(true, 1750.0);
        decoder.PushSamples(new float[1]);
        Assert.Equal(1750.0, decoder.NotchFrequencyForTests);

        decoder.RequestNotch(false, null);
        decoder.PushSamples(new float[1]);
        Assert.Null(decoder.NotchFrequencyForTests);

        decoder.RequestNotch(true, null);
        decoder.PushSamples(new float[1]);
        Assert.Equal(1750.0, decoder.NotchFrequencyForTests);
    }

    [Fact]
    public void RequestNotch_DisableWhileNotYetLocked_TurnsOffWithNoRewindAttempt()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestNotch(true, 2400.0);
        decoder.PushSamples(new float[1]);
        Assert.True(decoder.NotchEnabledForTests);

        decoder.RequestNotch(false, null);
        decoder.PushSamples(new float[1]); // no mode/_slantTracker yet -- ApplyNotchDisableShift's own gate no-ops

        Assert.False(decoder.NotchEnabledForTests);
        // _consumedSamples never advances while idle regardless of how many raw samples are pushed
        // (same invariant RequestReSyncTests' own idle no-op test pins) -- confirms no rewind was
        // attempted, since a rewind would have driven it negative (throwing, per Rel()'s own guard).
        Assert.Equal(0, decoder.ConsumedSamplesForTests);
    }

    [Fact]
    public void EnabledThroughoutAFullTransmission_StillDecodesTheImage()
    {
        // Sanity check that the pipeline tolerates the notch being on for an entire reception --
        // tuned well away from any real SSTV tone content so it shouldn't meaningfully corrupt this
        // gradient test image, just prove nothing about the wiring itself breaks the decode.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestNotch(true, 4000.0);

        var lineCount = 0;
        var modeDetected = false;
        decoder.LineDecoded += _ => lineCount++;
        decoder.ModeDetected += _ => modeDetected = true;

        const int chunkSize = 256;
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(modeDetected);
        Assert.Equal(mode.ImageHeight, lineCount);
    }

    private static int DecodeUntilLinesComplete(AnalogFmSstvDecoder decoder, float[] samples, int targetLineCount, out int lineCount)
    {
        var count = 0;
        decoder.LineDecoded += _ => count++;

        const int chunkSize = 32;
        var offset = 0;
        for (; offset < samples.Length && count < targetLineCount; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        lineCount = count;
        return offset;
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(11025);
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
