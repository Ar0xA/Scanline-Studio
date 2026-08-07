using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX force-mode decode override — the port of legacy's real RX quick-mode-button click
/// (<c>TMmsstv::SBMClick</c>, <c>Main.cpp:6096-6122</c>, calling <c>CSSTVDEM::Start(mode, TRUE)</c>,
/// <c>sstv.cpp:1749-1767</c>). Confirmed via <c>UpdateModeBtn</c> (<c>Main.cpp:5988</c>) to be a
/// one-shot "start decoding as mode X right now" kick, not a persistent lock -- see
/// <c>AnalogFmSstvDecoder.PerformForceMode</c>'s own doc comment for the full design and the two
/// rounds of plan-readiness review this went through.
/// </summary>
public class ForceModeTests
{
    [Fact]
    public void ForceMode_Idle_NonAvtMode_NoDecodeRestarted_ModeDetectedDeferredUntilAnchorResolves()
    {
        var mode = SstvModeRegistry.Robot36;
        var decoder = new AnalogFmSstvDecoder(11025);

        var modeDetectedCount = 0;
        var decodeRestartedCount = 0;
        decoder.ModeDetected += _ => modeDetectedCount++;
        decoder.DecodeRestarted += _ => decodeRestartedCount++;

        decoder.ForceMode(mode);
        decoder.PushSamples(new float[64]); // far short of the 3-4 lines TryResolveSyncAnchorCorrection needs

        Assert.Equal(0, decodeRestartedCount); // idle -> no old mode to report
        Assert.Equal(0, modeDetectedCount); // deferred: anchor not resolved yet
        Assert.Equal(mode.Id, decoder.ModeForTests?.Id); // but already locked internally
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);

        // Feed enough silence/noise to satisfy TryResolveSyncAnchorCorrection's own line-count gate.
        var lineWidthSamples = (int)(mode.LineDurationMs / 1000.0 * 11025);
        var neededSamples = 4 * lineWidthSamples; // Robot36's LineDurationMs < 1000ms -> lineCount=4
        decoder.PushSamples(new float[neededSamples]);

        Assert.Equal(1, modeDetectedCount);
        Assert.Equal(0, decodeRestartedCount);
        Assert.Null(decoder.PendingAnchorCorrectionModeForTests);
    }

    [Fact]
    public void ForceMode_Idle_Avt_ModeDetectedFiresImmediately_NoAnchorWait()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        var modeDetectedCount = 0;
        decoder.ModeDetected += _ => modeDetectedCount++;

        decoder.ForceMode(SstvModeRegistry.Avt);
        decoder.PushSamples(new float[64]); // AVT early-outs -- no anchor-correction wait

        Assert.Equal(1, modeDetectedCount);
        Assert.Null(decoder.PendingAnchorCorrectionModeForTests);
    }

    [Fact]
    public void ForceMode_MidReception_FiresDecodeRestartedWithTheOldMode()
    {
        var firstMode = SstvModeRegistry.Avt; // locks and announces immediately, simplest setup
        var decoder = new AnalogFmSstvDecoder(11025);
        decoder.ForceMode(firstMode);
        decoder.PushSamples(new float[64]);
        Assert.Equal(firstMode.Id, decoder.ModeForTests?.Id);

        SstvModeDefinition? restartedFrom = null;
        var restartCount = 0;
        decoder.DecodeRestarted += m => { restartedFrom = m; restartCount++; };

        var secondMode = SstvModeRegistry.Robot36;
        decoder.ForceMode(secondMode);
        decoder.PushSamples(new float[64]);

        Assert.Equal(1, restartCount);
        Assert.Equal(firstMode.Id, restartedFrom?.Id);
    }

    [Fact]
    public void ForceMode_WhileAPriorPendingAnchorIsUnresolved_DoesNotFireDecodeRestarted_ForTheNeverAnnouncedMode()
    {
        var firstMode = SstvModeRegistry.Robot36; // deferred: does NOT reach ModeDetected on its own here
        var decoder = new AnalogFmSstvDecoder(11025);

        var modeDetectedCount = 0;
        var restartCount = 0;
        decoder.ModeDetected += _ => modeDetectedCount++;
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.ForceMode(firstMode);
        decoder.PushSamples(new float[64]); // locked internally, but ModeDetected has NOT fired
        Assert.Equal(0, modeDetectedCount);
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);

        var secondMode = SstvModeRegistry.MartinM1;
        decoder.ForceMode(secondMode);
        decoder.PushSamples(new float[64]);

        // The interface's own contract: DecodeRestarted must never report a mode that never reached
        // ModeDetected in the first place.
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public void ForceMode_ForceAvtWhileADifferentModesPendingAnchorIsUnresolved_ClearsTheStaleEntry_ExactlyOneModeDetected()
    {
        var staleMode = SstvModeRegistry.Robot36;
        var decoder = new AnalogFmSstvDecoder(11025);

        var detectedModes = new List<string>();
        decoder.ModeDetected += m => detectedModes.Add(m.Id);

        decoder.ForceMode(staleMode);
        decoder.PushSamples(new float[64]);
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);
        Assert.Empty(detectedModes);

        decoder.ForceMode(SstvModeRegistry.Avt);
        decoder.PushSamples(new float[64]);

        Assert.Null(decoder.PendingAnchorCorrectionModeForTests); // stale entry cleared, not left behind
        Assert.Equal(new[] { SstvModeRegistry.Avt.Id }, detectedModes); // exactly one, and it's AVT

        // AVT decoding must not be stalled waiting on the stale mode's own line-count window: feed a
        // short chunk and confirm no second/spurious ModeDetected for the stale mode ever arrives.
        decoder.PushSamples(new float[4096]);
        Assert.Equal(new[] { SstvModeRegistry.Avt.Id }, detectedModes);
    }

    [Fact]
    public async Task ForceMode_WhileAvtTrainingIsPending_TearsDownTrainingState()
    {
        var avtMode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025);

        // Feed small chunks of REAL AVT audio until training is in flight but not yet resolved
        // (mirrors AvtTrainingLockDecoderTests' own real-audio approach -- training state can't be
        // reached any other way).
        const int chunkSize = 128;
        var offset = 0;
        var reachedTrainingPending = false;
        for (; offset < samples.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Count - offset);
            decoder.PushSamples(samples.GetRange(offset, length).ToArray());

            if (decoder.AvtTrainingPendingForTests && decoder.ModeForTests is null)
            {
                reachedTrainingPending = true;
                break;
            }
        }

        Assert.True(reachedTrainingPending, "Never observed AVT training in flight -- test setup problem.");

        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[64]);

        Assert.False(decoder.AvtTrainingPendingForTests);
        Assert.Equal(SstvModeRegistry.Robot36.Id, decoder.ModeForTests?.Id);
    }

    [Fact]
    public void ForceMode_SecondRequestBeforeFirstResolves_LastRequestWins()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        var detectedModes = new List<string>();
        decoder.ModeDetected += m => detectedModes.Add(m.Id);

        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.ForceMode(SstvModeRegistry.Avt); // supersedes before either is consumed
        decoder.PushSamples(new float[64]);

        Assert.Equal(SstvModeRegistry.Avt.Id, decoder.ModeForTests?.Id);
        Assert.Equal(new[] { SstvModeRegistry.Avt.Id }, detectedModes);
    }

    [Fact]
    public void ForceMode_AfterALongIdlePeriod_AnchorsAtTotalSamplesReceived_DoesNotThrow()
    {
        // Round-1 plan-readiness regression: anchoring at _consumedSamples (frozen pre-lock once
        // _fixedWindowExhausted is set) instead of TotalSamplesReceived would read already-trimmed
        // buffer and throw on this call's own thread -- the ordinary "idle app, click a mode button"
        // flow. Feed several seconds of pure silence first (comfortably past every fixed-window
        // header-search ceiling, so pre-lock trimming has genuinely started advancing _bufferBase
        // past 0, the whole time _consumedSamples never moves because nothing ever locks).
        var decoder = new AnalogFmSstvDecoder(11025);
        const int chunkSize = 2048;
        for (var i = 0; i < 30; i++) // ~5.6s of silence @11025Hz
        {
            decoder.PushSamples(new float[chunkSize]);
        }

        var totalBeforeForce = decoder.TotalSamplesReceived;

        var exception = Record.Exception(() =>
        {
            decoder.ForceMode(SstvModeRegistry.Avt);
            decoder.PushSamples(new float[64]);
        });

        Assert.Null(exception);
        Assert.Equal(totalBeforeForce, decoder.ConsumedSamplesForTests);
    }

    [Fact]
    public void ForceMode_LongLineMode_SuspendsTrimmingUntilAnchorResolves_ThenRecovers()
    {
        var mode = SstvModeRegistry.Pd290; // one of the longest-line modes in the registry
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.ForceMode(mode);
        decoder.PushSamples(new float[64]);
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);

        var lineWidthSamples = (int)(mode.LineDurationMs / 1000.0 * 11025);
        var lineCount = mode.LineDurationMs >= 1000.0 ? 3 : 4;
        var neededSamples = lineCount * lineWidthSamples;

        // Push most, but not all, of the needed window -- trimming must still be fully suspended.
        var bufferedBeforeResolve = decoder.TotalSamplesReceived;
        decoder.PushSamples(new float[neededSamples - 256]);
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);

        // Push the rest -- the anchor resolves, and trimming can resume on subsequent calls.
        decoder.PushSamples(new float[512]);
        Assert.Null(decoder.PendingAnchorCorrectionModeForTests);

        Assert.True(decoder.TotalSamplesReceived > bufferedBeforeResolve);
    }
}
