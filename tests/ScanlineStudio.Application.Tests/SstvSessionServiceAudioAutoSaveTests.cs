using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio) -- covers the identity-keyed arm/
/// close state machine, the push-epoch same-restart suppression, the pre-roll ring, and scratch-file
/// retention, isolated from the not-yet-built <c>RxAudioAutoSaver</c> correlator (per this project's
/// own "chop into pieces, test each piece before wiring" convention). Uses the same fake-based
/// construction pattern as <c>SstvSessionServiceRecordPlaybackTests</c>.</summary>
public sealed class SstvSessionServiceAudioAutoSaveTests
{
    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [11025])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings
                {
                    CaptureDeviceId = "capture-1",
                    PlaybackDeviceId = "playback-1",
                    SampleRate = 11025,
                },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder();
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder,
            new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);

        // Auditor-caught (round 2 code-review): without this, every test's scratch files land in the
        // real DefaultAudioDirectory (~/Music/ScanlineStudio/History on the running machine/CI agent)
        // -- point them at an ephemeral per-test temp directory instead. Not cleaned up afterward
        // (matches this suite's own Path.GetTempFileName() convention elsewhere for the FINAL saved
        // path); the OS temp directory is not a concern for a fast unit-test suite.
        service.SetAudioDirectory(Path.Combine(Path.GetTempPath(), "scanlinestudio-audioautosave-tests", Guid.NewGuid().ToString("N")));

        return (service, audioEngine, decoder);
    }

    /// <summary>Waits for the background Task.Run encode+write to finish and raise
    /// AudioSliceReady, instead of a fixed sleep-then-assert.</summary>
    private static async Task<(long ReceptionId, int SampleRate)> WaitForAudioSliceReadyAsync(SstvSessionService service, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<(long, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(long receptionId, int sampleRate) => tcs.TrySetResult((receptionId, sampleRate));
        service.AudioSliceReady += Handler;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            service.AudioSliceReady -= Handler;
        }
    }

    /// <summary>A tiny sample rate makes the real close-threshold formula (transmission-line-count ×
    /// LineDurationMs × sample rate) small enough to cross with a handful of pushed samples in a
    /// fast-running test, without special-casing the formula itself -- the fakes don't cross-validate
    /// this against the device's own declared supported rates.</summary>
    private const int TinySampleRate = 50;

    [Fact]
    public async Task Disabled_NeverArmsOrRaisesAudioSliceReady()
    {
        var (service, audioEngine, decoder) = CreateService();
        await service.StartReceivingAsync();
        // AutoSaveAudioEnabled defaults to false -- never explicitly enabled in this test.

        var sliceReady = false;
        service.AudioSliceReady += (_, _) => sliceReady = true;

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        audioEngine.PushCapturedSamples(new float[1000]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);

        await Task.Delay(50); // nothing should happen; a short grace window, not a wait-for-positive-signal
        Assert.False(sliceReady);
        // ui_transition_plan.md step 12, Step 4: disabled means arming never happens at all, so this
        // must also be false throughout -- ISstvSessionService.IsAudioAutoSaveActive's own contract.
        Assert.False(service.IsAudioAutoSaveActive);
    }

    /// <summary>ui_transition_plan.md step 12, Step 4: real, varying backing for the main-window
    /// status bar's auto-save-audio chip -- true ONLY between arm and close, never a static
    /// reflection of the enable flag alone (see ISstvSessionService.IsAudioAutoSaveActive's own doc
    /// comment for why that distinction matters).</summary>
    [Fact]
    public async Task Enabled_IsAudioAutoSaveActive_TrueOnlyBetweenArmAndClose()
    {
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        Assert.False(service.IsAudioAutoSaveActive);

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36); // arms
        Assert.True(service.IsAudioAutoSaveActive);

        audioEngine.PushCapturedSamples(new float[10]);
        audioEngine.PushCapturedSamples(new float[10]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36); // closes, nothing re-armed
        await WaitForAudioSliceReadyAsync(service);

        Assert.False(service.IsAudioAutoSaveActive);
    }

    [Fact]
    public async Task Enabled_DominantOrderingRestart_ClosesUnderTheOldReceptionId_ArmsTheNewOneCleanly()
    {
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36); // reception 1 (ReceptionSequence bumped by the fake to 1)
        audioEngine.PushCapturedSamples(new float[10]); // one push -> one push-epoch

        // Dominant ordering: DecodeRestarted for the OLD image arrives in a LATER push-epoch than
        // the arm -- a genuine restart, not the minority-ordering same-epoch case.
        audioEngine.PushCapturedSamples(new float[10]); // epoch advances again before the restart
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);
        var firstClosed = await WaitForAudioSliceReadyAsync(service);
        Assert.Equal(1L, firstClosed.ReceptionId);

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36); // reception 2
        audioEngine.PushCapturedSamples(new float[10]);
        decoder.RaiseModeDetected(SstvModeRegistry.Robot36); // closes reception 2 by arming reception 3
        var secondClosed = await WaitForAudioSliceReadyAsync(service);
        Assert.Equal(2L, secondClosed.ReceptionId);
    }

    [Fact]
    public async Task Enabled_MinorityOrderingRestart_SameEpoch_DoesNotAlsoCloseTheNewlyArmedReception()
    {
        // AVT / ForceMode-into-AVT shape: ModeDetected for the NEW reception, then DecodeRestarted for
        // the OLD one, BOTH within the same PushSamples call (same push-epoch). Driven via
        // OnPushSamples so both raises genuinely share one PushSamplesToDecoder call, matching the
        // real minority-ordering shape.
        //
        // Reception 1 (the OLD image) is expected to close HERE, via OnAudioAutoSaveModeDetected's own
        // unconditional "close any still-open previous slice first" step when reception 2 arms -- see
        // the plan doc's Correlation/Identity section. That is correct, not the bug this test guards
        // against. What must NOT happen is the immediately-following SAME-EPOCH DecodeRestarted also
        // closing reception 2, the one that was just armed -- if the suppression were missing (or
        // wrongly keyed by mode identity instead of push-epoch), reception 2 would close here too,
        // truncated to near-zero samples.
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36); // reception 1, armed

        var minorityFired = false;
        decoder.OnPushSamples = _ =>
        {
            if (minorityFired)
            {
                return;
            }

            minorityFired = true;
            decoder.RaiseModeDetected(SstvModeRegistry.MartinM1); // reception 2, same push-epoch -- closes reception 1 as a side effect
            decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36); // same-epoch as reception 2's arm -- must be suppressed, not close reception 2
        };

        var sliceReadyReceptionIds = new List<long>();
        service.AudioSliceReady += (id, _) => sliceReadyReceptionIds.Add(id);

        audioEngine.PushCapturedSamples(new float[10]); // triggers the hook above

        var firstClosed = await WaitForAudioSliceReadyAsync(service);
        Assert.Equal(1L, firstClosed.ReceptionId); // reception 1 closing here is correct, expected behavior

        // Give any (wrongly) fired second close a moment to complete, then confirm reception 2 was
        // NOT also closed by the suppressed same-epoch restart -- it should still be open.
        await Task.Delay(100);
        Assert.DoesNotContain(2L, sliceReadyReceptionIds);

        // Reception 2 still closes normally later, via its own genuine (different-epoch) trigger --
        // proves it was merely still open, not silently lost/stuck.
        audioEngine.PushCapturedSamples(new float[10]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.MartinM1);
        var secondClosed = await WaitForAudioSliceReadyAsync(service);
        Assert.Equal(2L, secondClosed.ReceptionId);
    }

    [Fact]
    public async Task Enabled_SameModeBackToBackRestart_DoesNotFalseSuppressOnModeIdentityAlone()
    {
        // The auditor-caught bug this design specifically fixes: SstvModeDefinition instances are
        // shared singletons, so a restart back into the SAME mode must be distinguished by push-epoch,
        // never by comparing mode identity/reference equality against what's currently armed.
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36); // reception 1
        audioEngine.PushCapturedSamples(new float[10]);
        audioEngine.PushCapturedSamples(new float[10]); // later push-epoch than the arm

        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36); // dominant-ordering restart, SAME mode instance
        var closed = await WaitForAudioSliceReadyAsync(service);
        Assert.Equal(1L, closed.ReceptionId);
    }

    [Fact]
    public async Task Enabled_ArmedBuffer_IncludesPreRollRingSamplesFromBeforeModeDetected()
    {
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        // Pre-roll samples, pushed BEFORE any ModeDetected -- must still end up in the eventually-
        // saved slice (this is the whole reason the ring exists: ModeDetected fires after the header/
        // VIS tone has already passed).
        float[] preRoll = [0.25f, -0.25f, 0.5f];
        audioEngine.PushCapturedSamples(preRoll);

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        audioEngine.PushCapturedSamples(new float[5]);
        audioEngine.PushCapturedSamples(new float[5]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);

        var (receptionId, _) = await WaitForAudioSliceReadyAsync(service);

        var savedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Assert.True(await service.TrySaveReceptionAudioAsync(receptionId, savedPath));
            var (samples, _) = WavFile.Read(savedPath);
            Assert.True(samples.Length >= preRoll.Length, "Saved slice is shorter than the pre-roll alone -- pre-roll seeding did not happen.");
            // WavFile round-trips through 16-bit PCM, so compare with a tolerance, not exact equality.
            for (var i = 0; i < preRoll.Length; i++)
            {
                Assert.True(Math.Abs(samples[i] - preRoll[i]) < 0.01f, $"Pre-roll sample {i} not found at the expected leading position.");
            }
        }
        finally
        {
            File.Delete(savedPath);
        }
    }

    [Fact]
    public async Task AudioCaptureReset_ViaSampleRateChange_DiscardsTheOpenArm_NeverRaisesAudioSliceReadyForIt()
    {
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        var resetFired = false;
        service.AudioCaptureReset += () => resetFired = true;
        var sliceReadyFired = false;
        service.AudioSliceReady += (_, _) => sliceReadyFired = true;

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        audioEngine.PushCapturedSamples(new float[5]); // a small, still-open arm -- nowhere near its close threshold

        await service.RequestSampleRateAsync(11025); // one of the 4 documented AudioCaptureReset seams

        Assert.True(resetFired);
        await Task.Delay(50);
        Assert.False(sliceReadyFired, "The open arm at the reset seam must be discarded, never encoded/emitted.");
    }

    [Fact]
    public async Task TrySaveReceptionAudioAsync_UnknownReceptionId_ReturnsFalse()
    {
        var (service, _, _) = CreateService();
        Assert.False(await service.TrySaveReceptionAudioAsync(999L, Path.GetTempFileName()));
    }

    [Fact]
    public async Task TrySaveReceptionAudioAsync_CalledTwiceForTheSameReception_SecondCallReturnsFalse()
    {
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        audioEngine.PushCapturedSamples(new float[5]);
        audioEngine.PushCapturedSamples(new float[5]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);
        var (receptionId, _) = await WaitForAudioSliceReadyAsync(service);

        var path1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        var path2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Assert.True(await service.TrySaveReceptionAudioAsync(receptionId, path1));
            Assert.False(await service.TrySaveReceptionAudioAsync(receptionId, path2), "Already consumed -- must not silently move/re-move the same scratch file twice.");
        }
        finally
        {
            File.Delete(path1);
            File.Delete(path2);
        }
    }

    [Fact]
    public async Task ScratchRetention_MoreThanEightUnconsumedSlices_EvictsOldestFirst_NewestExempt()
    {
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        var closedIds = new List<long>();
        var tcs = new List<TaskCompletionSource>();
        service.AudioSliceReady += (id, _) =>
        {
            closedIds.Add(id);
        };

        // Produce 9 closed-and-retained slices (never consumed via TrySaveReceptionAudioAsync) --
        // one more than the count cap of 8 -- via 9 dominant-ordering restarts.
        for (var i = 0; i < 9; i++)
        {
            decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
            audioEngine.PushCapturedSamples(new float[5]);
            audioEngine.PushCapturedSamples(new float[5]);
            decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);
            await WaitForAudioSliceReadyAsync(service);
        }

        Assert.Equal(9, closedIds.Count);
        Assert.Equal(Enumerable.Range(1, 9).Select(i => (long)i), closedIds);

        // The oldest (reception 1) must have been evicted -- both from the in-memory retained list
        // (TrySaveReceptionAudioAsync now returns false for it) and from disk.
        Assert.False(await service.TrySaveReceptionAudioAsync(1L, Path.GetTempFileName()));

        // The newest (reception 9) must still be retrievable -- never evicted, per the "newest exempt"
        // rule, regardless of arrival order.
        var newestPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Assert.True(await service.TrySaveReceptionAudioAsync(9L, newestPath));
        }
        finally
        {
            File.Delete(newestPath);
        }
    }

    [Fact]
    public async Task SetAutoSaveAudioEnabled_ToggledMidReception_DoesNotTruncateOrEmitTheInProgressSliceEarly()
    {
        // Auditor-caught (round 2 code-review): the ORIGINAL version of this test only asserted "no
        // immediate emit" and "closes later under the same id" -- both of those also pass under a
        // BROKEN implementation that silently drops the samples pushed during the disabled window
        // (a truncated-then-jump-cut-spliced WAV), because neither assertion looks at the ACTUAL
        // sample content. This version pushes distinguishable, non-zero values in all three windows
        // (enabled / disabled / re-enabled) and reads the saved WAV back to prove every one of them
        // is present, in order, with nothing missing from the middle.
        var (service, audioEngine, decoder) = CreateService();
        decoder.SampleRate = TinySampleRate;
        service.SetAutoSaveAudioEnabled(true);
        await service.StartReceivingAsync();

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        float[] beforeDisable = [0.1f, 0.2f];
        audioEngine.PushCapturedSamples(beforeDisable);

        var sliceReadyFired = false;
        service.AudioSliceReady += (_, _) => sliceReadyFired = true;

        // Toggling off mid-reception must NOT truncate-and-emit the in-progress arm early.
        service.SetAutoSaveAudioEnabled(false);
        await Task.Delay(50);
        Assert.False(sliceReadyFired);

        // While disabled, the already-open arm must keep being fed -- these samples must NOT go
        // missing from the eventually-saved slice.
        float[] whileDisabled = [0.3f, 0.4f, 0.5f];
        audioEngine.PushCapturedSamples(whileDisabled);

        // Re-enabling still doesn't retroactively affect the already-armed slice -- it just closes
        // normally on its own next trigger, same as if the setting had never changed.
        service.SetAutoSaveAudioEnabled(true);
        float[] afterReenable = [0.6f];
        audioEngine.PushCapturedSamples(afterReenable);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);
        var closed = await WaitForAudioSliceReadyAsync(service);
        Assert.Equal(1L, closed.ReceptionId);

        var savedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Assert.True(await service.TrySaveReceptionAudioAsync(closed.ReceptionId, savedPath));
            var (samples, _) = WavFile.Read(savedPath);
            float[] expected = [.. beforeDisable, .. whileDisabled, .. afterReenable];
            Assert.True(samples.Length >= expected.Length, $"Saved slice has {samples.Length} samples, expected at least {expected.Length} -- samples were dropped while the setting was toggled off.");
            // The pre-roll ring may prepend earlier (zero) samples ahead of these -- check the TAIL,
            // not the head, and with a tolerance since WavFile round-trips through 16-bit PCM.
            var tailStart = samples.Length - expected.Length;
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(Math.Abs(samples[tailStart + i] - expected[i]) < 0.01f, $"Sample {i} (expected {expected[i]}) missing or out of order -- the disabled window dropped/spliced data.");
            }
        }
        finally
        {
            File.Delete(savedPath);
        }
    }
}
