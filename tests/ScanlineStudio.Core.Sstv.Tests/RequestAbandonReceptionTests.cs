using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX pause/resume's decoder-side cleanup command (elegant-wondering-hinton.md item, RX toggle) --
/// the port of legacy's <c>TMmsstv::RxAutoPush</c>'s own <c>pDem->Stop()</c> step
/// (<c>Main.cpp:6042-6060</c>). Deliberately a one-shot cleanup request, not persistent "paused"
/// state -- the actual pause lives at <c>SstvSessionService</c>'s session layer (it simply stops
/// forwarding audio), which this decoder has no concept of. Four rounds of plan-readiness review
/// went into this design; see <see cref="AnalogFmSstvDecoder.RequestAbandonReception"/>'s own doc
/// comment and <c>ISstvDecoder.RequestAbandonReception</c>'s for the full reasoning.
/// </summary>
public class RequestAbandonReceptionTests
{
    [Fact]
    public void WhileIdle_IsASafeNoOp()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        var decodeRestartedCount = 0;
        decoder.DecodeRestarted += _ => decodeRestartedCount++;

        decoder.RequestAbandonReception();
        decoder.PushSamples(new float[64]);

        Assert.Equal(0, decodeRestartedCount);
        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);

        // Decoder must still be able to search fresh afterward -- not left in some half-reset state.
        Assert.Null(decoder.ModeForTests);
    }

    [Fact]
    public async Task LockedMidReception_FiresDecodeRestarted_LeavesDecoderIdle()
    {
        var mode = SstvModeRegistry.MartinM1;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(200, 100, 50));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025);
        var linesDecoded = 0;
        SstvModeDefinition? restartedMode = null;
        decoder.LineDecoded += _ => linesDecoded++;
        decoder.DecodeRestarted += m => restartedMode = m;

        // First third only: enough to lock and decode real lines, nowhere near enough for the image
        // or its trailing footer to complete on its own -- same technique SyncSourceTests uses.
        decoder.PushSamples(samples.Take(samples.Count / 3).ToArray());
        Assert.True(linesDecoded > 0, "Test setup problem: expected real lines decoded before abandoning.");
        Assert.Equal(SstvSyncSource.Locked, decoder.SyncSource);

        decoder.RequestAbandonReception();
        decoder.PushSamples(new float[64]); // drains the deferred request

        Assert.Equal(mode.Id, restartedMode?.Id);
        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);
        Assert.Null(decoder.ModeForTests);
    }

    [Fact]
    public async Task DuringAvtTraining_FiresNoDecodeRestarted_ButAbortsTraining()
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
        var decodeRestartedCount = 0;
        decoder.DecodeRestarted += _ => decodeRestartedCount++;

        // Same small-chunk technique as ForceModeTests.ForceMode_WhileAvtTrainingIsPending_...:
        // training state can't be reached except by feeding real AVT audio incrementally.
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

        decoder.RequestAbandonReception();
        decoder.PushSamples(new float[64]); // drains the deferred request

        Assert.Equal(0, decodeRestartedCount); // matches legacy's WriteHistory being gated on m_Sync
        Assert.False(decoder.AvtTrainingPendingForTests);
        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);
    }

    [Fact]
    public void LockedWithPendingAnchorCorrection_FiresNoDecodeRestarted_LeavesDecoderIdle()
    {
        // Round-4 plan-review nit: abandon while a mode is committed internally but its anchor
        // correction hasn't resolved yet (ModeDetected never fired for it) -- the first EndOfImage
        // call site reachable in that specific state combination.
        var mode = SstvModeRegistry.Robot36;
        var decoder = new AnalogFmSstvDecoder(11025);
        var modeDetectedCount = 0;
        var decodeRestartedCount = 0;
        decoder.ModeDetected += _ => modeDetectedCount++;
        decoder.DecodeRestarted += _ => decodeRestartedCount++;

        decoder.ForceMode(mode);
        decoder.PushSamples(new float[64]); // far short of TryResolveSyncAnchorCorrection's own gate

        Assert.Equal(mode.Id, decoder.ModeForTests?.Id);
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);

        decoder.RequestAbandonReception();
        decoder.PushSamples(new float[64]); // drains the deferred request

        Assert.Equal(0, modeDetectedCount); // never reached ModeDetected before being abandoned
        Assert.Equal(0, decodeRestartedCount); // hadPendingAnchor gate -- matches PerformForceMode's own
        Assert.Null(decoder.ModeForTests);
        Assert.Null(decoder.PendingAnchorCorrectionModeForTests);
    }

    [Fact]
    public void LongIdlePeriod_ThenAbandon_DoesNotThrow()
    {
        // Round-2/3 finding this regression-tests: EndOfImage's DEFAULT _consumedSamples anchor is
        // only valid while a mode is/was locked -- for a decoder that's been idle long enough for
        // _bufferBase to overtake it, using that default would yank cursors behind already-trimmed
        // buffer and throw. RequestAbandonReception's idle branch anchors at TotalSamplesReceived
        // instead specifically to avoid this.
        var decoder = new AnalogFmSstvDecoder(11025);

        // Feed enough silence to advance TotalSamplesReceived/trigger real trimming well past
        // MinTrimSamples while never locking (silence never produces a VIS header).
        for (var i = 0; i < 50; i++)
        {
            decoder.PushSamples(new float[11025]); // 1 second per push, 50 seconds total
        }

        var exception = Record.Exception(() =>
        {
            decoder.RequestAbandonReception();
            decoder.PushSamples(new float[64]);
        });

        Assert.Null(exception);
        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);

        // Decoder must still be able to search fresh afterward.
        decoder.PushSamples(new float[64]);
    }

    [Fact]
    public void ForceModePendingInSamePushSamplesCall_AbandonDoesNotTearItDown()
    {
        // Round-3 drain-order finding this regression-tests: draining the abandon request AFTER
        // _forcedMode would let a pending abandon silently tear down a mode ForceMode just
        // committed in the same call. RequestAbandonReception must drain FIRST.
        var mode = SstvModeRegistry.Robot36;
        var decoder = new AnalogFmSstvDecoder(11025);
        var modeDetectedCount = 0;
        var decodeRestartedCount = 0;
        decoder.ModeDetected += _ => modeDetectedCount++;
        decoder.DecodeRestarted += _ => decodeRestartedCount++;

        decoder.RequestAbandonReception(); // e.g. the user just paused
        decoder.ForceMode(mode); // ...then immediately clicked a quick-mode button, same call

        decoder.PushSamples(new float[64]); // both requests drain in this one call

        // If abandon drained AFTER ForceMode, this would be null (silently torn down).
        Assert.Equal(mode.Id, decoder.ModeForTests?.Id);
        Assert.NotNull(decoder.PendingAnchorCorrectionModeForTests);
        Assert.Equal(0, decodeRestartedCount); // idle before either request -- no old mode to report
        Assert.Equal(0, modeDetectedCount); // deferred: anchor not resolved yet
    }

    [Fact]
    public async Task AfterAbandon_FreshTransmission_LocksAndDecodesCleanly()
    {
        // Code-review finding (round: elegant-wondering-hinton.md code-review): every existing test
        // stopped at confirming the decoder went back to Idle -- none proved it can actually LOCK and
        // DECODE a real, subsequent transmission afterward, i.e. that the resume half of pause/resume
        // genuinely works and not just that abandon doesn't leave the decoder stuck.
        var mode = SstvModeRegistry.MartinM1;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(200, 100, 50));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            firstSamples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025);
        var linesDecoded = 0;
        decoder.LineDecoded += _ => linesDecoded++;

        // Lock onto and partially decode the first transmission, then abandon it mid-reception --
        // same setup as LockedMidReception_FiresDecodeRestarted_LeavesDecoderIdle above.
        decoder.PushSamples(firstSamples.Take(firstSamples.Count / 3).ToArray());
        Assert.True(linesDecoded > 0, "Test setup problem: expected real lines decoded before abandoning.");

        decoder.RequestAbandonReception();
        decoder.PushSamples(new float[64]); // drains the deferred request
        Assert.Equal(SstvSyncSource.Idle, decoder.SyncSource);

        linesDecoded = 0;
        var modeDetected = false;
        decoder.ModeDetected += m => modeDetected = m.Id == mode.Id;

        // A second, independent, FULL transmission fed after the abandon -- proves the decoder can
        // search fresh and lock again, not just that it went idle.
        var secondSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            secondSamples.Add(sample);
        }

        decoder.PushSamples(secondSamples.ToArray());

        Assert.True(modeDetected);
        Assert.Equal(mode.ImageHeight, linesDecoded);
    }
}
