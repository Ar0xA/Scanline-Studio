using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application.Tests;

/// <summary>fsk_cwid.md §5 A2 -- <see cref="RxStationIdAttacher"/>'s per-reception accumulator join.
/// Uses <see cref="FakeSstvSessionServiceForCorrelation"/> (no real arm/close state machine needed --
/// unlike <c>RxAudioAutoSaverTests</c>, <see cref="ISstvSessionService.StationIdDecoded"/> is a plain,
/// directly-raisable event with no window-timing behavior behind it).</summary>
public sealed class RxStationIdAttacherTests
{
    private static (RxStationIdAttacher Attacher, FakeSstvSessionServiceForCorrelation SessionService, FakeReceiveHistoryStoreForStationIdCorrelation HistoryStore) CreateAttacher(string? ownCallsign = null)
    {
        var sessionService = new FakeSstvSessionServiceForCorrelation { OperatorCallsignToReturn = ownCallsign };
        var historyStore = new FakeReceiveHistoryStoreForStationIdCorrelation();
        var attacher = new RxStationIdAttacher(sessionService, historyStore, NullLogger<RxStationIdAttacher>.Instance);
        return (attacher, sessionService, historyStore);
    }

    private static Task<(string EntryId, string? Callsign, string? NrRst)> StartWaitingForStationIdAttached(RxStationIdAttacher attacher, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<(string, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string entryId, string? callsign, string? nrRst)
        {
            attacher.StationIdAttached -= Handler;
            tcs.TrySetResult((entryId, callsign, nrRst));
        }

        attacher.StationIdAttached += Handler;
        var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    [Fact]
    public async Task RecordedArrivesBeforeStationIdDecoded_PairsCorrectly_AttachesTheCallsign()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher();
        var attachedTask = StartWaitingForStationIdAttached(attacher);

        var entry = new ReceiveHistoryEntry("entry-1", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-1.png", null, ReceiveDecodeState.Completed) { ReceptionId = 1 };
        historyStore.RaiseRecorded(entry);

        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW", ReceptionSequence: 1));

        var (attachedEntryId, callsign, nrRst) = await attachedTask;
        Assert.Equal("entry-1", attachedEntryId);
        Assert.Equal("W1AW", callsign);
        Assert.Null(nrRst);
        Assert.Equal(("entry-1", "W1AW", (string?)null), historyStore.LastSetDecodedStationId);
    }

    [Fact]
    public async Task StationIdDecodedArrivesBeforeRecorded_PairsCorrectly_AttachesTheCallsign()
    {
        // OnStationIdDecoded dispatches its self-filter read to a background Task.Run, so a bare
        // back-to-back raise does not prove this order -- the synchronous Recorded raise below could
        // easily complete first. Use a deterministic gate (this project's own "deterministic gates,
        // not shared race" convention, not a Task.Delay sleep): hold GetOperatorCallsignAsync open
        // until the background dispatch has genuinely started, raise Recorded while it's still
        // pending, THEN release it -- proving StationIdDecoded's processing began strictly before
        // Recorded arrived.
        var (attacher, sessionService, historyStore) = CreateAttacher();
        var attachedTask = StartWaitingForStationIdAttached(attacher);
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.GetOperatorCallsignAsyncGate = gate;

        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW", ReceptionSequence: 2));
        await sessionService.GetOperatorCallsignAsyncEntered.Task;

        var entry = new ReceiveHistoryEntry("entry-2", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-2.png", null, ReceiveDecodeState.Completed) { ReceptionId = 2 };
        historyStore.RaiseRecorded(entry);

        gate.SetResult(null);

        var (attachedEntryId, callsign, _) = await attachedTask;
        Assert.Equal("entry-2", attachedEntryId);
        Assert.Equal("W1AW", callsign);
    }

    [Fact]
    public async Task CallsignThenNrRst_ForTheSameReception_MergesIntoOneWriteWithBoth()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher();

        var entry = new ReceiveHistoryEntry("entry-3", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-3.png", null, ReceiveDecodeState.Completed) { ReceptionId = 3 };
        historyStore.RaiseRecorded(entry);

        var firstAttachedTask = StartWaitingForStationIdAttached(attacher);
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW", ReceptionSequence: 3));
        var (_, firstCallsign, firstNrRst) = await firstAttachedTask;
        Assert.Equal("W1AW", firstCallsign);
        Assert.Null(firstNrRst); // NR/RST hasn't decoded yet -- this write must not have invented one.

        // The NR/RST sub-packet decodes separately (FskStationIdEncoder.Generate's own two-sub-packet
        // shape) -- the resulting SECOND write must carry the callsign FORWARD, not drop it just
        // because this event didn't carry one.
        var secondAttachedTask = StartWaitingForStationIdAttached(attacher);
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 1, ReceptionSequence: 3));
        var (_, secondCallsign, secondNrRst) = await secondAttachedTask;
        Assert.Equal("W1AW", secondCallsign);
        Assert.Equal("595001", secondNrRst);
    }

    [Fact]
    public async Task CompactNr_FormatsWithThe595PrefixAndD3ZeroPad()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher();
        var entry = new ReceiveHistoryEntry("entry-4", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-4.png", null, ReceiveDecodeState.Completed) { ReceptionId = 4 };
        historyStore.RaiseRecorded(entry);

        var attachedTask = StartWaitingForStationIdAttached(attacher);
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 7, ReceptionSequence: 4));

        var (_, _, nrRst) = await attachedTask;
        Assert.Equal("595007", nrRst);
    }

    [Fact]
    public async Task NrText_FormatsWithThe595Prefix()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher();
        var entry = new ReceiveHistoryEntry("entry-5", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-5.png", null, ReceiveDecodeState.Completed) { ReceptionId = 5 };
        historyStore.RaiseRecorded(entry);

        var attachedTask = StartWaitingForStationIdAttached(attacher);
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(NrText: "UT4", ReceptionSequence: 5));

        var (_, _, nrRst) = await attachedTask;
        Assert.Equal("595UT4", nrRst);
    }

    [Fact]
    public async Task OwnCallsign_IsSelfFiltered_NeverAttaches()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher(ownCallsign: "K2ABC");
        var entry = new ReceiveHistoryEntry("entry-6", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-6.png", null, ReceiveDecodeState.Completed) { ReceptionId = 6 };
        historyStore.RaiseRecorded(entry);

        var attached = false;
        attacher.StationIdAttached += (_, _, _) => attached = true;
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K2ABC", ReceptionSequence: 6));

        await Task.Delay(100); // grace window, not a wait-for-positive-signal
        Assert.False(attached);
        Assert.Null(historyStore.LastSetDecodedStationId);
    }

    [Fact]
    public async Task AnotherStationsCallsign_MatchingTheOperatorsNameCoincidentally_StillAttaches()
    {
        // Sanity-check the self-filter is an EXACT ordinal match against the operator's OWN callsign,
        // not some looser heuristic -- a different reception with a DIFFERENT decoded callsign must
        // never be affected by the operator's own configured value.
        var (attacher, sessionService, historyStore) = CreateAttacher(ownCallsign: "K2ABC");
        var entry = new ReceiveHistoryEntry("entry-7", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-7.png", null, ReceiveDecodeState.Completed) { ReceptionId = 7 };
        historyStore.RaiseRecorded(entry);

        var attachedTask = StartWaitingForStationIdAttached(attacher);
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW", ReceptionSequence: 7));

        var (_, callsign, _) = await attachedTask;
        Assert.Equal("W1AW", callsign);
    }

    [Fact]
    public async Task Recorded_WithReceptionIdZero_NeverAttaches()
    {
        var (attacher, _, historyStore) = CreateAttacher();

        var attached = false;
        attacher.StationIdAttached += (_, _, _) => attached = true;

        // ReceptionId defaults to 0 -- a disk-reconciled/backfilled entry, per ISstvDecoder.
        // ReceptionSequence's own contract ("0 means unset"), same as RxAudioAutoSaver's own guard.
        var entry = new ReceiveHistoryEntry("backfilled-entry", DateTimeOffset.UtcNow, "robot36", "/tmp/backfilled.png", null, ReceiveDecodeState.Completed);
        Assert.Equal(0L, entry.ReceptionId);
        historyStore.RaiseRecorded(entry);

        await Task.Delay(100);
        Assert.False(attached);
        Assert.Null(historyStore.LastSetDecodedStationId);
    }

    [Fact]
    public async Task StationIdDecoded_WithReceptionSequenceZero_NeverAttaches()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher();
        var entry = new ReceiveHistoryEntry("entry-8", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-8.png", null, ReceiveDecodeState.Completed) { ReceptionId = 0 };
        historyStore.RaiseRecorded(entry);

        var attached = false;
        attacher.StationIdAttached += (_, _, _) => attached = true;
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW", ReceptionSequence: 0));

        await Task.Delay(100);
        Assert.False(attached);
    }

    [Fact]
    public async Task NineUnconsumedStationIdDecodedEvents_EvictsTheOldestParkedOne_NewestStillPairs()
    {
        var (attacher, sessionService, historyStore) = CreateAttacher();

        // Park 9 StationIdDecoded events (receptionIds 1..9) with no matching entry yet -- one more
        // than the join's own 8-entry cap, so reception 1 (the oldest) must be evicted. Eviction
        // order depends on PARK order, not raise order -- since OnStationIdDecoded dispatches to a
        // background Task.Run, raising all 9 back-to-back leaves park order to unenforced thread-pool
        // scheduling. Serialize instead (deterministic gate, not a shared race): await each call's
        // own GetOperatorCallsignAsyncCalled signal before raising the next.
        for (long receptionId = 1; receptionId <= 9; receptionId++)
        {
            var entered = new SemaphoreSlim(0, 1);
            void OnCalled() => entered.Release();
            sessionService.GetOperatorCallsignAsyncCalled += OnCalled;
            sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: $"CALL{receptionId}", ReceptionSequence: receptionId));
            await entered.WaitAsync(TimeSpan.FromSeconds(2));
            sessionService.GetOperatorCallsignAsyncCalled -= OnCalled;
        }

        // The oldest (reception 1) must have been evicted -- its entry arriving now must NOT attach.
        var neverAttachedForOldest = false;
        attacher.StationIdAttached += (entryId, _, _) => neverAttachedForOldest |= entryId == "entry-1";
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry-1", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-1.png", null, ReceiveDecodeState.Completed) { ReceptionId = 1 });
        await Task.Delay(50);
        Assert.False(neverAttachedForOldest);

        // The newest (reception 9) must still be retained -- its entry arriving must attach successfully.
        var attachedTask = StartWaitingForStationIdAttached(attacher);
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry-9", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-9.png", null, ReceiveDecodeState.Completed) { ReceptionId = 9 });
        var (attachedEntryId, callsign, _) = await attachedTask;
        Assert.Equal("entry-9", attachedEntryId);
        Assert.Equal("CALL9", callsign);
    }

    [Fact]
    public async Task ConcurrentUpdatesForTheSameReception_CoalesceIntoOneFollowUpWrite_NeverARacingSecondWrite()
    {
        // A-P3b: folds in the concurrent-write-reordering fix the A-P3a auditor round explicitly
        // deferred here. An earlier version spawned one independent Task.Run per ApplyUpdate call --
        // under a bulk-decode interleaving, the thread pool could run a LATER call's write before an
        // EARLIER call's, leaving the DB showing a stale snapshot. Proves the fix's actual mechanism
        // (not just eventual correctness): while a write for a reception is genuinely in flight (held
        // open here via GateFirstCall), a second update for the SAME reception must NOT start an
        // independent second write -- it must wait for the first to finish, then write once more
        // with the fully-merged snapshot.
        var (attacher, sessionService, historyStore) = CreateAttacher();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        historyStore.GateFirstCall = gate;

        var entry = new ReceiveHistoryEntry("entry-cw", DateTimeOffset.UtcNow, "robot36", "/tmp/entry-cw.png", null, ReceiveDecodeState.Completed) { ReceptionId = 42 };
        historyStore.RaiseRecorded(entry);

        // First update (callsign only) starts the write loop; its own call parks on the gate.
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW", ReceptionSequence: 42));
        await historyStore.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Second update (NR/RST) for the SAME reception arrives while the first write is still
        // parked. Its own background dispatch has no gate of its own -- if the old, unfixed code
        // were still in place, this would complete an independent second write almost immediately,
        // landing BEFORE the first call's gate is ever released. Give it a bounded window to prove
        // that does NOT happen, rather than asserting instantly (the merge itself runs on a
        // background Task.Run with no flush primitive to await deterministically).
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 1, ReceptionSequence: 42));
        await Task.Delay(150);
        Assert.Single(historyStore.SetDecodedStationIdCalls);

        gate.SetResult(true);

        // Exactly one MORE write lands after the gate releases, carrying the fully-merged snapshot.
        await Task.Delay(150);
        Assert.Equal(2, historyStore.SetDecodedStationIdCalls.Count);
        Assert.Equal(("entry-cw", "W1AW", (string?)null), historyStore.SetDecodedStationIdCalls[0]);
        Assert.Equal(("entry-cw", "W1AW", "595001"), historyStore.SetDecodedStationIdCalls[1]);

        // Auditor code-review nit: nothing above pins that WriteLoopInFlight is actually CLEARED once
        // the loop exits -- a mutation that forgets to clear it would leave every FUTURE write for
        // this reception permanently blocked, the coalescing fix's own worst failure mode, and every
        // assertion above would still pass unchanged. A third, independent update for the same
        // reception must still produce a third write.
        sessionService.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 2, ReceptionSequence: 42));
        await Task.Delay(150);
        Assert.Equal(3, historyStore.SetDecodedStationIdCalls.Count);
        Assert.Equal(("entry-cw", "W1AW", "595002"), historyStore.SetDecodedStationIdCalls[2]);
    }

    /// <summary>Minimal <see cref="IReceiveHistoryStore"/> fake -- only the members
    /// <see cref="RxStationIdAttacher"/> actually uses are functional; everything else throws,
    /// matching the sibling fakes' "not exercised by this test suite" convention (e.g.
    /// <c>RxAudioAutoSaverTests</c>'s own nested <c>FakeReceiveHistoryStoreForCorrelation</c>).</summary>
    private sealed class FakeReceiveHistoryStoreForStationIdCorrelation : IReceiveHistoryStore
    {
        public event Action<ReceiveHistoryEntry>? Recorded;
        public event Action<ReceiveHistoryEntry>? Deleted;

        public (string EntryId, string? Callsign, string? NrRst)? LastSetDecodedStationId { get; private set; }

        /// <summary>A-P3b coalescing-writer test support: every call, in the order actually made --
        /// unlike <see cref="LastSetDecodedStationId"/> (only the most recent), this lets a test
        /// assert the exact WRITE COUNT and per-call content, both load-bearing for proving writes
        /// are serialized rather than racing.</summary>
        public List<(string EntryId, string? Callsign, string? NrRst)> SetDecodedStationIdCalls { get; } = [];

        /// <summary>When set, the FIRST call to <see cref="SetDecodedStationIdAsync"/> parks on this
        /// (after being recorded/counted) until the test releases it -- lets a test prove a SECOND,
        /// concurrent update for the same reception does not start an independent second write while
        /// the first is still in flight (see <see cref="RxStationIdAttacher.ApplyUpdate"/>'s own
        /// coalescing-writer doc comment).</summary>
        public TaskCompletionSource<bool>? GateFirstCall { get; set; }

        /// <summary>Completes once the first call has been recorded (and is now parked on
        /// <see cref="GateFirstCall"/>, if set) -- the real "the write has genuinely started" signal
        /// a test awaits before doing anything that depends on that.</summary>
        public TaskCompletionSource<bool> FirstCallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public void RaiseRecorded(ReceiveHistoryEntry entry) => Recorded?.Invoke(entry);

        /// <summary>Never called by any test here -- exists only to satisfy the interface without
        /// leaving <see cref="Deleted"/> entirely dead (CS0067), same convention as the sibling
        /// fakes.</summary>
        public void RaiseDeleted(ReceiveHistoryEntry entry) => Deleted?.Invoke(entry);

        public async Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? nrRst, CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _callCount);
            SetDecodedStationIdCalls.Add((entryId, callsign, nrRst));
            LastSetDecodedStationId = (entryId, callsign, nrRst);

            if (callNumber == 1)
            {
                FirstCallEntered.TrySetResult(true);
                if (GateFirstCall is { } gate)
                {
                    await gate.Task.ConfigureAwait(false);
                }
            }

            return true;
        }

        public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");

        public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by RxStationIdAttacherTests.");
    }
}
