using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio) -- <see cref="RxAudioAutoSaver"/>'s
/// identity-keyed join. The end-to-end pairing tests drive a REAL <see cref="SstvSessionService"/>
/// (same fake-decoder/fake-audio-engine harness as <c>SstvSessionServiceAudioAutoSaveTests</c>) so
/// <see cref="ISstvSessionService.AudioSliceReady"/> fires from the actual arm/close state machine;
/// the eviction/id-0 tests use <see cref="FakeSstvSessionServiceForCorrelation"/> instead, since
/// they need precise, arbitrary control over <c>receptionId</c> values a real, sequential decoder
/// cannot give (auditor-suggested, round 1 code-review).</summary>
public sealed class RxAudioAutoSaverTests
{
    private const int TinySampleRate = 50;

    private static async Task<(RxAudioAutoSaver Saver, SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder, FakeReceiveHistoryStoreForCorrelation HistoryStore)> CreateSaverAsync()
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
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 11025 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder { SampleRate = TinySampleRate };
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder,
            new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        service.SetAutoSaveAudioEnabled(true);
        var scratchAudioDirectory = Path.Combine(Path.GetTempPath(), "scanlinestudio-rxaudioautosaver-tests", Guid.NewGuid().ToString("N"));
        service.SetAudioDirectory(scratchAudioDirectory);
        // Required so _decoderHandler subscribes to SamplesCaptured -- without it, PushCapturedSamples
        // never reaches PushSamplesToDecoder, the push-epoch counter never advances, and every
        // DecodeRestarted below would be wrongly treated as same-epoch (suppressed).
        await service.StartReceivingAsync();

        var historyStore = new FakeReceiveHistoryStoreForCorrelation(scratchAudioDirectory);
        var saver = new RxAudioAutoSaver(service, historyStore, NullLogger<RxAudioAutoSaver>.Instance);

        return (saver, service, audioEngine, decoder, historyStore);
    }

    /// <summary>Arms and drives one full dominant-ordering reception through the REAL state machine,
    /// returning once its <see cref="ISstvSessionService.AudioSliceReady"/> has actually fired (not a
    /// fixed delay) -- the same id a real <c>ReceiveHistoryRecorder</c> would have captured into a
    /// <see cref="ReceiveHistoryEntry.ReceptionId"/>.</summary>
    private static async Task<long> ArmAndCloseOneReceptionAsync(SstvSessionService service, FakeAudioEngine audioEngine, FakeSstvDecoder decoder)
    {
        var sliceReadyTask = StartWaitingForAudioSliceReady(service);

        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        var receptionId = decoder.ReceptionSequence;
        audioEngine.PushCapturedSamples(new float[5]);
        audioEngine.PushCapturedSamples(new float[5]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);

        await sliceReadyTask;
        return receptionId;
    }

    /// <summary>Subscribes NOW, returns a task to await LATER -- callers must subscribe before
    /// triggering the event, not after (a background pairing can complete before a post-hoc
    /// subscribe ever attaches, causing a spurious timeout under load; auditor-caught, round 1
    /// code-review).</summary>
    private static Task<(long ReceptionId, int SampleRate)> StartWaitingForAudioSliceReady(ISstvSessionService service, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<(long, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(long receptionId, int sampleRate)
        {
            service.AudioSliceReady -= Handler;
            tcs.TrySetResult((receptionId, sampleRate));
        }

        service.AudioSliceReady += Handler;
        var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    /// <summary>Same "subscribe now, await later" shape as <see cref="StartWaitingForAudioSliceReady"/>.</summary>
    private static Task<(string EntryId, string Path)> StartWaitingForAudioAttached(RxAudioAutoSaver saver, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string entryId, string path)
        {
            saver.AudioAttached -= Handler;
            tcs.TrySetResult((entryId, path));
        }

        saver.AudioAttached += Handler;
        var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    [Fact]
    public async Task RecordedArrivesBeforeAudioSliceReady_PairsCorrectly_AttachesThePath()
    {
        var (saver, _, audioEngine, decoder, historyStore) = await CreateSaverAsync();
        var attachedTask = StartWaitingForAudioAttached(saver);

        // Arm and hold the reception open (don't close it yet) so we control the arrival order --
        // raise Recorded FIRST, then close the slice to produce AudioSliceReady second.
        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        var receptionId = decoder.ReceptionSequence;
        audioEngine.PushCapturedSamples(new float[5]);

        var entry = new ReceiveHistoryEntry("entry-1", DateTimeOffset.UtcNow, SstvModeRegistry.Robot36.Id, "/tmp/entry-1.png", null, ReceiveDecodeState.Completed) { ReceptionId = receptionId };
        historyStore.RaiseRecorded(entry);

        audioEngine.PushCapturedSamples(new float[5]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36); // closes the slice -> AudioSliceReady

        var (attachedEntryId, attachedPath) = await attachedTask;
        Assert.Equal("entry-1", attachedEntryId);
        Assert.True(File.Exists(attachedPath));
        Assert.Equal(attachedPath, historyStore.LastSetAudioFilePath);
        File.Delete(attachedPath);
    }

    [Fact]
    public async Task AudioSliceReadyArrivesBeforeRecorded_PairsCorrectly_AttachesThePath()
    {
        var (saver, service, audioEngine, decoder, historyStore) = await CreateSaverAsync();
        var attachedTask = StartWaitingForAudioAttached(saver);

        // Waits for the REAL AudioSliceReady raise, not a fixed delay -- deterministically proves
        // "slice parked, waiting for its Recorded half" regardless of how long the background
        // encode+write actually takes.
        var receptionId = await ArmAndCloseOneReceptionAsync(service, audioEngine, decoder);

        var entry = new ReceiveHistoryEntry("entry-2", DateTimeOffset.UtcNow, SstvModeRegistry.Robot36.Id, "/tmp/entry-2.png", null, ReceiveDecodeState.Completed) { ReceptionId = receptionId };
        historyStore.RaiseRecorded(entry);

        var (attachedEntryId, attachedPath) = await attachedTask;
        Assert.Equal("entry-2", attachedEntryId);
        Assert.True(File.Exists(attachedPath));
        File.Delete(attachedPath);
    }

    [Fact]
    public async Task Recorded_WithReceptionIdZero_NeverAttaches()
    {
        var (saver, _, _, _, historyStore) = await CreateSaverAsync();

        var attached = false;
        saver.AudioAttached += (_, _) => attached = true;

        // ReceptionId defaults to 0 -- a disk-reconciled/backfilled entry, per ISstvDecoder.
        // ReceptionSequence's own contract ("0 means unset").
        var entry = new ReceiveHistoryEntry("backfilled-entry", DateTimeOffset.UtcNow, "robot36", "/tmp/backfilled.png", null, ReceiveDecodeState.Completed);
        Assert.Equal(0L, entry.ReceptionId);
        historyStore.RaiseRecorded(entry);

        await Task.Delay(100);
        Assert.False(attached);
        Assert.Null(historyStore.LastSetAudioFilePath);
    }

    [Fact]
    public async Task RecordedWithReceptionIdZero_DoesNotCountTowardTheEvictionCap()
    {
        // Auditor-suggested rigorous version (round 1 code-review) of the id-0 guard: a version that
        // only checks "id-0 never attaches" can't distinguish CORRECTLY SKIPPED from HARMLESSLY
        // PARKED FOREVER under key 0 -- both look identical from the outside, since a real slice can
        // never carry id 0. This version instead observes the guard's real side effect on the shared
        // eviction cap: park exactly 8 real slices (the cap) FIRST, THEN raise an id-0 Recorded. If
        // the guard works, id-0 is never parked, so the 8 real slices are untouched and the OLDEST
        // one still pairs when its own Recorded arrives. If the guard were missing, id-0 would push
        // the parked count to 9 and evict that oldest slice, and it would then fail to pair.
        var sessionService = new FakeSstvSessionServiceForCorrelation();
        var historyStore = new FakeReceiveHistoryStoreForCorrelation(Path.Combine(Path.GetTempPath(), "scanlinestudio-rxaudioautosaver-tests", Guid.NewGuid().ToString("N")));
        var saver = new RxAudioAutoSaver(sessionService, historyStore, NullLogger<RxAudioAutoSaver>.Instance);

        for (var receptionId = 1; receptionId <= 8; receptionId++)
        {
            sessionService.RaiseAudioSliceReady(receptionId, 11025);
        }

        var zeroEntry = new ReceiveHistoryEntry("backfilled-entry", DateTimeOffset.UtcNow, "robot36", "/tmp/backfilled.png", null, ReceiveDecodeState.Completed);
        historyStore.RaiseRecorded(zeroEntry);

        var attachedTask = StartWaitingForAudioAttached(saver);
        var oldestEntry = new ReceiveHistoryEntry("entry-oldest", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-oldest.png", null, ReceiveDecodeState.Completed) { ReceptionId = 1 };
        historyStore.RaiseRecorded(oldestEntry);

        var (attachedEntryId, _) = await attachedTask;
        Assert.Equal("entry-oldest", attachedEntryId);
    }

    [Fact]
    public async Task NineUnconsumedRecordedEntries_EvictsTheOldestParkedEntry_NewestStillPairs()
    {
        // Uses FakeSstvSessionServiceForCorrelation, not a real SstvSessionService -- this needs to
        // fire AudioSliceReady for a SPECIFIC, chosen (including "already evicted") receptionId,
        // which a real sequential decoder can't reproduce on demand. TrySaveReceptionAudioResult
        // defaults to true regardless of receptionId, so pairing success/failure here is driven
        // ENTIRELY by RxAudioAutoSaver's own join/eviction bookkeeping, not by any real file state.
        var sessionService = new FakeSstvSessionServiceForCorrelation();
        var historyStore = new FakeReceiveHistoryStoreForCorrelation(Path.Combine(Path.GetTempPath(), "scanlinestudio-rxaudioautosaver-tests", Guid.NewGuid().ToString("N")));
        var saver = new RxAudioAutoSaver(sessionService, historyStore, NullLogger<RxAudioAutoSaver>.Instance);

        // Park 9 Recorded entries (receptionIds 1..9) with no matching slice yet -- one more than
        // the join's own 8-entry cap, so reception 1 (the oldest) must be evicted.
        for (var receptionId = 1; receptionId <= 9; receptionId++)
        {
            var entry = new ReceiveHistoryEntry($"entry-{receptionId}", DateTimeOffset.UtcNow, "robot36", $"/tmp/entry-{receptionId}.png", null, ReceiveDecodeState.Completed) { ReceptionId = receptionId };
            historyStore.RaiseRecorded(entry);
        }

        // The oldest (reception 1) must have been evicted -- its slice arriving now must NOT pair.
        var neverAttachedForOldest = false;
        saver.AudioAttached += (entryId, _) => neverAttachedForOldest |= entryId == "entry-1";
        sessionService.RaiseAudioSliceReady(1, 11025);
        await Task.Delay(50);
        Assert.False(neverAttachedForOldest);

        // The newest (reception 9) must still be retained -- its slice arriving must pair successfully.
        var attachedTask = StartWaitingForAudioAttached(saver);
        sessionService.RaiseAudioSliceReady(9, 11025);
        var (attachedEntryId, _) = await attachedTask;
        Assert.Equal("entry-9", attachedEntryId);
    }

    /// <summary>Minimal <see cref="IReceiveHistoryStore"/> fake -- only the members
    /// <see cref="RxAudioAutoSaver"/> actually uses are functional; everything else throws, matching
    /// the sibling fakes' "not exercised by this test suite" convention.</summary>
    private sealed class FakeReceiveHistoryStoreForCorrelation(string audioDirectory) : IReceiveHistoryStore
    {
        public event Action<ReceiveHistoryEntry>? Recorded;
        public event Action<ReceiveHistoryEntry>? Deleted;

        public string? LastSetAudioFilePath { get; private set; }

        public void RaiseRecorded(ReceiveHistoryEntry entry) => Recorded?.Invoke(entry);

        /// <summary>Never called by any test here -- exists only to satisfy the interface without
        /// leaving <see cref="Deleted"/> entirely dead (CS0067), same convention as the sibling fakes
        /// in this codebase (e.g. Core.Logbook.Tests' own FakeReceiveHistoryStore).</summary>
        public void RaiseDeleted(ReceiveHistoryEntry entry) => Deleted?.Invoke(entry);

        public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(new AudioAutoSaveSettings(true, audioDirectory));

        public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default)
        {
            LastSetAudioFilePath = path;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");

        public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxAudioAutoSaverTests.");
    }
}
