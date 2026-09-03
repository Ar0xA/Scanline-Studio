using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>fsk_cwid.md §8.2/§10: the post-image CW-ID capture window's own arm/capture/hand-off
/// state machine, isolated from the real <see cref="ClassicalCwDecoder"/> (covered by its own
/// <c>Core.Cw.Tests</c> suite) via <see cref="FakeCwIdDecoder"/>. Uses <see cref="SstvModeRegistry.Robot36"/>
/// (real mode constants, not a synthetic 1x1 mode) at a tiny sample rate (100 Hz) so the real
/// arm-window-formula produces small, fast-to-push sample counts without special-casing the formula
/// itself -- same "tiny sample rate, real mode" technique <c>SstvSessionServiceAudioAutoSaveTests</c>
/// already uses for its own close-threshold tests.</summary>
public sealed class SstvSessionServiceCwIdTests
{
    private const int TinySampleRate = 100;
    private const int PreArmSamples = 10;
    private static readonly SstvModeDefinition Mode = SstvModeRegistry.Robot36;

    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder, FakeCwIdDecoder CwIdDecoder, FakeSettingsStore SettingsStore) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [TinySampleRate])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [TinySampleRate])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = TinySampleRate },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder { SampleRate = TinySampleRate };
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();
        var cwIdDecoder = new FakeCwIdDecoder();

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder,
            new MacroTextResolver(), waterfall, receivedImage, radioSession, cwIdDecoder, NullLogger<SstvSessionService>.Instance);

        return (service, audioEngine, decoder, cwIdDecoder, settingsStore);
    }

    private static void WithCwIdRxEnabled(FakeSettingsStore store, int windowSeconds = StationIdSettings.MinCwIdRxWindowSeconds)
    {
        store.Settings = store.Settings.WithSection(
            StationIdSettings.SectionKey,
            new StationIdSettings { CwIdRxEnabled = true, CwIdRxWindowSeconds = windowSeconds },
            StationIdSettingsJsonContext.Default.StationIdSettings);
    }

    /// <summary>Derives the same imageDurationSamples the production arm formula computes, from the
    /// mode's own public properties -- not a hardcoded magic number, so this test fails loudly (not
    /// silently) if <see cref="Mode"/> is ever changed.</summary>
    private static long ImageDurationSamples(int sampleRate)
    {
        var rowsPerTransmissionLine = Mode.ColorEncoding is ColorEncoding.YCbCrLinePaired or ColorEncoding.MonoAveragedPaired ? 2 : 1;
        var transmissionLineCount = Mode.ImageHeight / rowsPerTransmissionLine;
        return (long)Math.Round(transmissionLineCount * Mode.LineDurationMs / 1000.0 * sampleRate);
    }

    /// <summary>Arms with AnchorLagSamples set to exactly match how many samples have been pushed so
    /// far (via <paramref name="preArmSamples"/>), so imageStart lands at exactly 0 -- makes windowOpen
    /// == ImageDurationSamples() exactly, a fully predictable value the test can assert against
    /// without duplicating the arm formula's own clamping/rounding behavior.</summary>
    private static void ArmAtImageStartZero(FakeAudioEngine audioEngine, FakeSstvDecoder decoder, int preArmSamples = PreArmSamples)
    {
        decoder.AnchorLagSamples = preArmSamples;
        decoder.OnPushSamples = _ => decoder.RaiseModeDetected(Mode);
        audioEngine.PushCapturedSamples(new float[preArmSamples]);
        decoder.OnPushSamples = null;
    }

    [Fact]
    public async Task Disabled_NeverArmsOrDecodes()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        // CwIdRxEnabled defaults to false -- never explicitly enabled in this test.
        await service.StartReceivingAsync();

        ArmAtImageStartZero(audioEngine, decoder);
        var windowClose = ImageDurationSamples(TinySampleRate) + StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;
        audioEngine.PushCapturedSamples(new float[windowClose]);

        await Task.Delay(50); // grace window, not a wait-for-positive-signal
        Assert.Equal(0, cwIdDecoder.DecodeCallCount);
    }

    [Fact]
    public async Task Enabled_CapturesExactlyTheWindowAndHandsItToTheDecoder()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        cwIdDecoder.Result = new CwDecodeResult("DE W1AW", 0.9, 1000, 28, []);
        await service.StartReceivingAsync();

        var decodedTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CwIdDecoded += info => decodedTcs.TrySetResult(info);

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);

        var windowOpen = ImageDurationSamples(TinySampleRate);
        var windowSamples = StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;

        // Filler, all zeros -- must NOT be captured (before windowOpen). windowOpen is an ABSOLUTE
        // pushed-sample-count target; the arm chunk itself already consumed PreArmSamples of that
        // budget, so only (windowOpen - PreArmSamples) more filler samples are needed to land exactly
        // on the boundary.
        audioEngine.PushCapturedSamples(new float[windowOpen - PreArmSamples]);

        // The real "signal" -- a distinct, recognizable value so a test bug that captures the wrong
        // range (e.g. off-by-one, or including filler) is caught by content, not just length.
        var signal = new float[windowSamples];
        Array.Fill(signal, 0.5f);
        audioEngine.PushCapturedSamples(signal);

        var info = await decodedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(windowSamples, cwIdDecoder.LastSamples.Length);
        Assert.All(cwIdDecoder.LastSamples.ToArray(), s => Assert.Equal(0.5f, s));
        Assert.Equal(TinySampleRate, cwIdDecoder.LastSampleRate);
        Assert.Equal(1L, info.ReceptionSequence);
        Assert.Equal("DE W1AW", info.Text);
        Assert.Equal("W1AW", info.Callsign);
    }

    [Fact]
    public async Task Enabled_ChunkStraddlingWindowOpen_SlicesCorrectly()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        // A non-empty result -- DecodeAndRaiseCwIdAsync only raises CwIdDecoded for a non-empty
        // Text (§8.4 step 1's "no tone" case is deliberately silent, see its own doc comment); this
        // test only cares about what was captured, but still needs the event to actually fire.
        cwIdDecoder.Result = new CwDecodeResult("DE TEST", 1.0, null, null, []);
        await service.StartReceivingAsync();

        var decodedTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CwIdDecoded += info => decodedTcs.TrySetResult(info);

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);

        var windowOpen = ImageDurationSamples(TinySampleRate);
        var windowSamples = StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;

        // One chunk straddling windowOpen: filler (0.0f) up to the boundary, then real signal (0.5f)
        // past it, in the SAME PushCapturedSamples call -- exercises the sliceStart/sliceEnd math
        // within a single chunk, not just the between-chunks case the previous test already covers.
        // Absolute pushed count before this chunk is PreArmSamples (from the arm chunk itself), so the
        // filler portion here is (windowOpen - PreArmSamples) samples, matching the previous test's
        // own accounting.
        const int straddleSlack = 5;
        var fillerLength = (int)(windowOpen - PreArmSamples);
        var straddling = new float[fillerLength + straddleSlack];
        Array.Fill(straddling, 0.5f, fillerLength, straddleSlack);
        audioEngine.PushCapturedSamples(straddling);

        // Remaining signal to fill the rest of the window.
        var rest = new float[windowSamples - straddleSlack];
        Array.Fill(rest, 0.5f);
        audioEngine.PushCapturedSamples(rest);

        var info = await decodedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(windowSamples, cwIdDecoder.LastSamples.Length);
        Assert.All(cwIdDecoder.LastSamples.ToArray(), s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public async Task LaterEpochDecodeRestarted_DropsTheArm_NeverDecodes()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        await service.StartReceivingAsync();

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);

        // An intervening push bumps the push-epoch to a LATER value than the one captured at arm
        // time -- fsk_cwid.md §8.2's own discriminator for "this restart refers to a genuinely
        // different reception than the one just armed," not the same-push minority ordering.
        audioEngine.PushCapturedSamples(new float[1]);
        decoder.RaiseDecodeRestarted(Mode);

        // Push the rest of a would-be full window -- if the arm were NOT dropped, this would close
        // it and decode.
        var windowClose = ImageDurationSamples(TinySampleRate) + StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;
        audioEngine.PushCapturedSamples(new float[windowClose]);

        await Task.Delay(50); // grace window, not a wait-for-positive-signal
        Assert.Equal(0, cwIdDecoder.DecodeCallCount);
    }

    [Fact]
    public async Task SameEpochDecodeRestarted_DoesNotDropTheArm()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        cwIdDecoder.Result = new CwDecodeResult("DE TEST", 1.0, null, null, []);
        await service.StartReceivingAsync();

        var decodedTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CwIdDecoded += info => decodedTcs.TrySetResult(info);

        // ModeDetected (arms fresh, epoch E) THEN DecodeRestarted, BOTH from WITHIN the SAME push --
        // the real AVT/ForceMode minority-ordering shape (ModeDetected for the new reception fires
        // FIRST, then DecodeRestarted closes out the OLD one, both in one PushSamples call,
        // ISstvDecoder.ReceptionSequence's own doc comment). The restart's epoch is still E (epoch
        // only bumps once per push, not per event) -- OnCwIdDecodeRestarted's own same-epoch
        // suppression must treat this as "the restart my own arm already accounted for," not drop
        // the arm THIS push just created. Order matters: raising the restart BEFORE the arm exists
        // (an earlier draft of this test did that) can't actually exercise the suppression at all --
        // dropping a nonexistent arm is a no-op regardless of whether suppression works, which a
        // mutation test caught: disabling the suppression check entirely still passed against that
        // draft.
        decoder.AnchorLagSamples = PreArmSamples;
        decoder.OnPushSamples = _ =>
        {
            decoder.RaiseModeDetected(Mode);
            decoder.RaiseDecodeRestarted(Mode);
        };
        audioEngine.PushCapturedSamples(new float[PreArmSamples]);
        decoder.OnPushSamples = null;

        var windowClose = ImageDurationSamples(TinySampleRate) - PreArmSamples + StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;
        audioEngine.PushCapturedSamples(new float[windowClose]);

        var info = await decodedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, cwIdDecoder.DecodeCallCount);
        Assert.Equal(1L, info.ReceptionSequence);
    }

    [Fact]
    public async Task DecodeFromFileAsync_FileEndsAfterWindowOpenButBeforeWindowClose_FlushesPartialCapture_AndDetachesFromLiveCapture()
    {
        // A different (larger) sample rate than the live-capture tests above -- windowOpen/windowClose
        // need to span multiple of DecodeFromFileAsync's own fixed 4096-sample chunks so a fixture can
        // land precisely between them.
        const int fileSampleRate = 1000;
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        decoder.SampleRate = fileSampleRate;
        WithCwIdRxEnabled(settingsStore);
        cwIdDecoder.Result = new CwDecodeResult("DE TEST", 1.0, null, null, []);
        await service.StartReceivingAsync(); // wasReceiving=true, so handlers re-subscribe after the file decode

        var windowOpen = ImageDurationSamples(fileSampleRate);
        var windowSamples = StationIdSettings.MinCwIdRxWindowSeconds * fileSampleRate;

        // First 4096-sample chunk of the file arms (AnchorLagSamples = that chunk's own length, so
        // imageStart lands at 0, same technique as the live-capture tests).
        const int firstChunkLength = 4096;
        decoder.AnchorLagSamples = firstChunkLength;
        decoder.OnPushSamples = _ =>
        {
            decoder.RaiseModeDetected(Mode);
            decoder.OnPushSamples = null;
        };

        var partialIntoWindow = 2000;
        var fileLength = windowOpen + partialIntoWindow;
        Assert.True(fileLength < windowOpen + windowSamples, "fixture must end before windowClose for this to be the PARTIAL case");
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, new float[fileLength], fileSampleRate);

            var decodedTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CwIdDecoded += info => decodedTcs.TrySetResult(info);

            await service.DecodeFromFileAsync(path);

            var info = await decodedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(partialIntoWindow, cwIdDecoder.LastSamples.Length);

            // A live chunk pushed AFTER DecodeFromFileAsync returns must not retroactively appear in
            // the already-flushed capture -- proves the arm was detached from SamplesCaptured inside
            // the file decode's own finally, not left dangling into live capture.
            var beforeCount = cwIdDecoder.DecodeCallCount;
            audioEngine.PushCapturedSamples(new float[1000]);
            await Task.Delay(50);
            Assert.Equal(beforeCount, cwIdDecoder.DecodeCallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecoderException_IsSwallowed_DoesNotPropagateOrRaiseCwIdDecoded()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        cwIdDecoder.ThrowOnDecode = new InvalidOperationException("simulated decoder failure");
        await service.StartReceivingAsync();

        var decoded = false;
        service.CwIdDecoded += _ => decoded = true;

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);
        var windowClose = ImageDurationSamples(TinySampleRate) + StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;
        audioEngine.PushCapturedSamples(new float[windowClose - PreArmSamples]);

        // DecodeAndRaiseCwIdAsync's own try/catch must isolate this -- the throw happens on a
        // background Task.Run, well after PushCapturedSamples above already returned, so there is no
        // synchronous exception to catch here. Awaits FakeCwIdDecoder's own deterministic completion
        // signal (auditor round-2 nit: a fixed Task.Delay here was a real, if small, flake window --
        // there is no CwIdDecoded event to wait on instead, since a throw never raises one) rather
        // than a fixed delay.
        await cwIdDecoder.FirstCallCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, cwIdDecoder.DecodeCallCount); // the decode WAS attempted...
        Assert.False(decoded); // ...but never raised, since it threw.
    }

    [Fact]
    public async Task StopReceiving_DropsAnOpenArm_ViaAudioCaptureReset()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        await service.StartReceivingAsync();

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);

        // StopReceivingLockedAsync's own HandleAudioCaptureReset call must drop the open arm -- the
        // same "discard, don't close-and-emit" rule the audio-auto-save arm already has at this seam.
        await service.StopReceivingAsync();
        await service.StartReceivingAsync();

        // A full window's worth pushed AFTER the reset/restart must not complete the OLD (dropped)
        // arm -- there is no re-arm without a fresh ModeDetected, which nothing here raises.
        var windowClose = ImageDurationSamples(TinySampleRate) + StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;
        audioEngine.PushCapturedSamples(new float[windowClose]);

        await Task.Delay(50); // grace window, not a wait-for-positive-signal
        Assert.Equal(0, cwIdDecoder.DecodeCallCount);
    }

    [Fact]
    public async Task AutoDetectPaused_DropsAnOpenArm_NotJustGatedByThePauseCheck()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        await service.StartReceivingAsync();

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);

        // §8.2's own explicitly-flagged easy-to-miss case: SetAutoDetectPaused(true) raises neither
        // AudioCaptureReset nor DecodeRestarted, so the arm-drop has to happen at this call directly.
        //
        // Mutation-caught while authoring this test: pushing a full window's worth WHILE STILL PAUSED
        // and asserting no decode does NOT actually prove the arm was dropped -- OnCwIdSamplesCaptured's
        // own _autoDetectPaused early-return independently blocks those samples regardless of whether
        // the arm still exists, so disabling the DropCwArmForCaptureReset() call in SetAutoDetectPaused
        // still passed that version of this test. The real discriminator is RESUMING first: if the arm
        // survived the pause, _pushedSampleCount resumes incrementing from wherever it was frozen (a
        // small value, since almost nothing was pushed before pausing) and the SAME still-open arm
        // would naturally complete once enough post-resume samples arrive at its own (small,
        // unchanged) windowClose target -- only an ACTUAL drop prevents that.
        service.SetAutoDetectPaused(true);
        service.SetAutoDetectPaused(false);

        var windowClose = ImageDurationSamples(TinySampleRate) + StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;
        audioEngine.PushCapturedSamples(new float[windowClose]);

        await Task.Delay(50); // grace window, not a wait-for-positive-signal
        Assert.Equal(0, cwIdDecoder.DecodeCallCount);
    }

    [Fact]
    public async Task BackToBackReceptions_DoNotCrossAttach()
    {
        var (service, audioEngine, decoder, cwIdDecoder, settingsStore) = CreateService();
        WithCwIdRxEnabled(settingsStore);
        await service.StartReceivingAsync();

        var windowOpen = ImageDurationSamples(TinySampleRate);
        var windowSamples = StationIdSettings.MinCwIdRxWindowSeconds * TinySampleRate;

        // First reception: arms at imageStart=0 (ArmAtImageStartZero's own contract), fills with a
        // distinct signal value, decodes as reception 1.
        cwIdDecoder.Result = new CwDecodeResult("DE FIRST", 1.0, null, null, []);
        var firstTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CwIdDecoded += info => firstTcs.TrySetResult(info);

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);
        audioEngine.PushCapturedSamples(new float[windowOpen - PreArmSamples]);
        var firstSignal = new float[windowSamples];
        Array.Fill(firstSignal, 0.5f);
        audioEngine.PushCapturedSamples(firstSignal);

        var firstInfo = await firstTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1L, firstInfo.ReceptionSequence);
        Assert.Equal(windowSamples, cwIdDecoder.LastSamples.Length);

        // Second reception: ArmAtImageStartZero's OWN contract (imageStart = the running
        // pushedSampleCount total BEFORE its own pre-arm push, not zero on a second call -- see its
        // doc comment) means imageStart2 == reception 1's own windowClose, which is exactly right:
        // reception 2's image starts where reception 1's capture window ended.
        var runningTotalAfterFirst = (long)windowOpen + windowSamples;
        var windowOpen2 = runningTotalAfterFirst + windowOpen;

        cwIdDecoder.Result = new CwDecodeResult("DE SECOND", 1.0, null, null, []);
        var secondTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CwIdDecoded += info => secondTcs.TrySetResult(info);

        ArmAtImageStartZero(audioEngine, decoder, PreArmSamples);
        audioEngine.PushCapturedSamples(new float[windowOpen2 - (runningTotalAfterFirst + PreArmSamples)]);
        var secondSignal = new float[windowSamples];
        Array.Fill(secondSignal, 0.25f);
        audioEngine.PushCapturedSamples(secondSignal);

        var secondInfo = await secondTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2L, secondInfo.ReceptionSequence);
        Assert.Equal(windowSamples, cwIdDecoder.LastSamples.Length);
        // No reception-1 audio (0.5f) leaked into reception 2's own capture -- proves the two
        // windows are genuinely independent, not sharing state across the reception boundary.
        Assert.All(cwIdDecoder.LastSamples.ToArray(), s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public async Task DecodeFromFileAsync_FileFillsTheWholeWindow_DecodesTheFullCapture()
    {
        const int fileSampleRate = 1000;
        var (service, _, decoder, cwIdDecoder, settingsStore) = CreateService();
        decoder.SampleRate = fileSampleRate;
        WithCwIdRxEnabled(settingsStore);
        cwIdDecoder.Result = new CwDecodeResult("DE TEST", 1.0, null, null, []);
        await service.StartReceivingAsync();

        var windowOpen = ImageDurationSamples(fileSampleRate);
        var windowSamples = StationIdSettings.MinCwIdRxWindowSeconds * fileSampleRate;

        const int firstChunkLength = 4096;
        decoder.AnchorLagSamples = firstChunkLength;
        decoder.OnPushSamples = _ =>
        {
            decoder.RaiseModeDetected(Mode);
            decoder.OnPushSamples = null;
        };

        // §10's file case (1): a fixture long enough to fill the WHOLE window, not just part of it.
        var fileLength = windowOpen + windowSamples + 4096; // margin past windowClose
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, new float[fileLength], fileSampleRate);

            var decodedTcs = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CwIdDecoded += info => decodedTcs.TrySetResult(info);

            await service.DecodeFromFileAsync(path);

            var info = await decodedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1L, info.ReceptionSequence);
            Assert.Equal(windowSamples, cwIdDecoder.LastSamples.Length);
            // Pins that the window closed exactly once (via the natural windowClose crossing, not
            // ALSO via the end-of-file flush) -- auditor round-2 nit: only the length was previously
            // asserted, which a hypothetical double-decode would not have caught.
            Assert.Equal(1, cwIdDecoder.DecodeCallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_FileEndsInsideImage_DropsWithoutDecoding()
    {
        const int fileSampleRate = 1000;
        var (service, _, decoder, cwIdDecoder, settingsStore) = CreateService();
        decoder.SampleRate = fileSampleRate;
        WithCwIdRxEnabled(settingsStore);
        // Auditor-caught: without this, _cwIdRxEnabled (only ever written inside
        // StartReceivingLockedAsync) stays at its field default (false), OnCwIdModeDetected
        // early-returns without arming at all, and the assertion below passes VACUOUSLY --
        // indistinguishable from Disabled_NeverArmsOrDecodes, and a mutation that made
        // FlushOrDropCwArmForEndOfFile always flush would have passed this test undetected.
        await service.StartReceivingAsync();

        var windowOpen = ImageDurationSamples(fileSampleRate);
        const int firstChunkLength = 4096;
        decoder.AnchorLagSamples = firstChunkLength;
        decoder.OnPushSamples = _ =>
        {
            decoder.RaiseModeDetected(Mode);
            decoder.OnPushSamples = null;
        };

        // Well short of windowOpen -- the file ends while still inside the image itself.
        var fileLength = windowOpen / 2;
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, new float[fileLength], fileSampleRate);

            await service.DecodeFromFileAsync(path);

            await Task.Delay(50); // grace window, not a wait-for-positive-signal
            Assert.Equal(0, cwIdDecoder.DecodeCallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
