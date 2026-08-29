# Step 12 plan: Auto-save RX audio (ui_transition_plan.md lines 296-364)

Post-round-3 patch. Round 1 found 4 blockers (wrong project for the new class, a broken
`Saved`+`Recorded` correlation design, an unreachable/undersized pre-roll constant, unhandled live
rate/device changes). Round 2 fixed those but introduced/found 3 more (capacity arithmetic wrong by
~3.7×, inverting the redesign's own memory-saving goal; the `Recorded`-only correlation's "single
thread" claim was false; a `ModeId`-only match with no consumption reintroduced a cross-attach bug)
plus an AVT close-threshold gap. Round 3 confirmed the capacity/threading fixes but found round 2's
correlation fix STILL cross-attached (a `Saved`-before-slice-closes ordering case round 2 didn't
account for), found the AVT premise itself was backwards (`ModeDetected` fires AFTER the training
gap, not before — pre-roll was ~3.8 s short, not the close threshold ~7 s too short), and found the
scratch-file encode was specified to run on the audio drain thread. All three are fixed below via a
symmetric rendezvous-pairing redesign (`Correlation`), a corrected `AudioPreRollMs` derivation, and
moving the encode to a background `Task.Run`.

**Round 4 happened anyway.** The round-3 rendezvous-FIFO went to a second design opinion and then
the auditor, and both converged on rejecting it: an orphaned entry on either FIFO (e.g. an
abandoned reception whose `Recorded` fires with no `Saved` to rendezvous against) permanently
desyncs every LATER pairing until the next `AudioCaptureReset` — fragile by construction, not by a
fixable bug. Root cause: reception identity was being *reconstructed downstream from arrival
order* instead of *assigned upstream at the moment of arming*. Several sub-rounds between the two
reviewers then found and closed: a same-epoch `DecodeRestarted` ambiguity (both AVT and an ordinary
same-mode-back-to-back reception hit this, not just AVT), an abandoned-path id-selection gap, a
scratch-file-lifetime contradiction with a keyed join, an unnecessary and harmful reuse of the
pre-existing `Generation` counter, a missed third `PushSamples` call site, and a cross-platform bug
in an interim lock-file proposal. The `Correlation` section below is fully rewritten around an
identity-based design (`ISstvDecoder.ReceptionSequence`) both reviewers signed off on as closed.
`Saved` is no longer part of the correlator at all. Flagged decode-path-adjacent — full 2-round
plan-review + up-to-3-round code-review cadence (CLAUDE.md §7) before any code is written.

**A whole-document auditor pass then checked the written-out redesign for real** (prior rounds had
only checked deltas/summaries, not the actual prose) and found 5 genuine paper-level defects plus 2
missing decisions, all now fixed in place: the close-trigger formula reintroduced the PD/MP/MN (YCbCrLinePaired) / RM8-RM12 (MonoAveragedPaired)
`ImageHeight` double-count bug the Capacity section itself had already caught and fixed for buffer
*sizing* but not for the close *trigger*; `AudioCaptureReset` was specified to wipe the join
dictionary at RX seams, which would have destroyed genuinely valid pending pairs in the most common
QSO workflow (TX pause/resume landing right after a reception closes) — fixed by removing that
component's `AudioCaptureReset` subscription entirely, since disjoint reception ids already make
cross-attach impossible without it; the recorder's completed-image path was specified to reuse the
abandoned-path discriminator, which doesn't apply to it, and was missing the synchronous-hoist
pattern the recorder already uses elsewhere for exactly this race; the same-epoch invariant was
stated as absolute when it is actually conditional on today's bounded push-chunk sizes (now stated
as an explicit precondition binding both production code and test harnesses); and the peak-memory
claim ("never two full-size buffers at once") was false in the interruption case (now stated
honestly as a bounded transient ~226 MB worst case). The two missing decisions — how
`AutoSaveAudioEnabled`/`AudioDirectory` actually propagate from Options into `SstvSessionService`,
and how the final saved WAV's path/filename is derived — are now specified. The interference/
shadowing question from the previous paragraph is RESOLVED, not open: confirmed at
`AnalogFmSstvDecoder.cs` source level to be an ordinary instance of the dominant/minority orderings
this design already handles, with two edge cases verified safe by construction (see
Recorder-side id selection and Accepted v1 limitations under Correlation below). Per the auditor's
own framing, none of this warranted another full design round — it was a diff-level fix to the
written prose. **One more diff-check-only auditor pass over exactly these edits is expected before
implementation starts**, not a further design round.

## Scope

Add an always-on, bounded, per-reception audio slice that auto-saves alongside each RX history
image, gated by a new Options setting. Builds on the existing manual record path
(`ISstvSessionService.StartRecordingAsync`/`StopRecordingAsync`,
`SstvSessionService.cs:2231-2342`, unbounded `_recordingChunks`) as a **separate, independent** 5th
`IAudioEngine.SamplesCaptured` fan-out target — the manual path must keep working unchanged.

## Files touched

- `src/ScanlineStudio.Core.Logbook/ReceiveHistorySettings.cs` — add `bool? AutoSaveAudioEnabled`
  (kept nullable for consistency with the sibling settings on this type, though the STJ
  init-only-default trap doesn't actually bite here since the desired default, off, equals the CLR
  default), `string? AudioDirectory`.
- `src/ScanlineStudio.Abstractions/Imaging/IReceiveHistoryStore.cs` — new
  `GetAudioSettingsAsync`/`SetAudioSettingsAsync`-shaped methods, mirroring
  `GetImagesDirectoryAsync`/`SetImagesDirectoryAsync` (IReceiveHistoryStore.cs:92-114). UI must
  never reference `Core.Logbook.ReceiveHistorySettings` directly.
- `src/ScanlineStudio.Core.Logbook/SqliteReceiveHistoryStore.cs` — implement the above; new
  `AudioFilePath TEXT NULL` column, appended as a new `ALTER TABLE ADD COLUMN` check block (the 6th —
  `DecodeState`/`Note`/`IsFlagged`/`FrequencyHz`/`RigMode` already exist, `:517-544`) AFTER the
  existing `FrequencyHz`/`RigMode` pair — step 6's migration already shipped, this is a new block,
  not a combined pass. `CREATE TABLE`'s own column order gains `AudioFilePath` last, matching. New
  `SetAudioFilePathAsync(string entryId, string path)` (`ReceiveHistoryEntry.Id` is `string`,
  `IReceiveHistoryStore.cs:45` — matches every sibling setter's parameter type) — a plain `UPDATE`,
  no event re-raise (see "Attach notification" below for why).
- `src/ScanlineStudio.Abstractions/Imaging/ReceiveHistoryEntry.cs` (declared in
  `IReceiveHistoryStore.cs:44-54`) — new trailing optional `string? AudioFilePath = null`, plus a
  transient `long ReceptionId = 0` (NOT a DB column — see Correlation below; `Recorded` subscribers
  receive the caller's own instance so the transient value survives to them, but nothing persists or
  reloads it; a `ReconcileWithDiskAsync`-backfilled entry correctly carries `0` ("unset"), which the
  correlator ignores by contract). Note: as a positional `record` member this joins the type's value
  equality, so a DB-loaded copy (`ReceptionId == 0`) never equals the originally-recorded instance
  (`ReceptionId == n`) — harmless today since every consumer keys by `Entry.Id`
  (`RxHistoryPaneViewModel.UpdateEntryInPlace`), not by record equality; don't rely on record equality
  for this type going forward.
- `src/ScanlineStudio.Abstractions/Sstv/ISstvDecoder.cs` — new `long ReceptionSequence { get; }`.
  Contract (state explicitly in the XML doc comment): monotonic for this instance's lifetime, never
  reused; first real value is `1` (an `Interlocked.Increment` from a 0-initialized field, NOT
  post-increment — `0` stays reserved for "no reception yet"/"unset"); safe to read from any thread.
  **Precondition the same-epoch invariant (see Correlation below) depends on, state it here too:**
  a single `PushSamples` call must never itself contain two independent restart-triggering events —
  true today only because live capture pushes are chunked at
  `MiniAudioCaptureSession.DrainBufferFrames = 4096` and file-decode pushes are similarly bounded
  (and file-decode pushes never reach an OPEN ARM to false-suppress, since the arm/close state
  machine itself — not the epoch counter — is gated off during a file decode via
  `_fileDecodeInFlight`; the epoch counter is still bumped for every push regardless) — any test
  harness driving this path (including `FakeAudioEngine.PushCapturedSamples`, which accepts
  arbitrary-length buffers) MUST chunk pushes at ≤4096 samples per call, or a single oversized test
  buffer containing two real restarts will collapse them into one epoch and silently swallow the
  second one (slice never closes). Implemented by `RestartableSstvDecoder` (the only instance DI
  ever hands out) and `AnalogFmSstvDecoder` (the inner decoder `RestartableSstvDecoder` wraps and
  periodically swaps), plus the 3 test fakes under
  `tests/ScanlineStudio.{Core.Logbook,Core.Imaging,Application}.Tests/FakeSstvDecoder.cs`.
- `src/ScanlineStudio.Core.Sstv/RestartableSstvDecoder.cs` — owns the real counter (a fresh inner
  `AnalogFmSstvDecoder` after a swap must NOT reset it — the wrapper counter is the only one that
  matters in production, since DI always resolves the wrapper). Increment happens in `OnModeDetected`
  (currently ~RestartableSstvDecoder.cs:1692-1694) via `Interlocked.Increment`, BEFORE
  `RaiseForwardedSubscribers` fans the event out — this is what makes every independent subscriber
  (the audio side, `ReceivedImageBuffer`, `ReceiveHistoryRecorder`, UI panes) read the identical value
  for the same reception regardless of subscription order, without needing any subscriber to
  coordinate with any other. `OnDecodeRestarted` never increments it (see Correlation below for why:
  `DecodeRestarted` does not always mean "new reception").
- `src/ScanlineStudio.Application/ISstvSessionService.cs` / `SstvSessionService.cs`:
  - New two-tier capture buffer (see Capacity below), a new `SamplesCaptured` subscriber.
  - `SstvSessionService` subscribes internally to `_decoder.ModeDetected`/`DecodeRestarted` for this
    feature's own arm/close logic — today it only RE-EXPOSES these as add/remove pass-throughs
    (`SstvSessionService.cs:1138-1145`); this is a genuinely new internal subscription, not a reuse
    of the pass-through wiring.
  - New internal push-epoch counter, bumped around EVERY call into `_decoder.PushSamples` — route
    all such calls through one private helper (don't enumerate call sites: besides the live drain
    handler and `DecodeFromFileAsync`, a 3rd site at ~SstvSessionService.cs:384 pushes an empty
    buffer specifically to flush a deferred `RequestAbandonReception` on the auto-detect-pause edge,
    and a 4th could be added later — the self-test loop at ~:2628-2648 pushes into its own separate
    local decoder instance and correctly stays outside this mechanism).
  - Still reuses the EXISTING `_fileDecodeInFlight` field (SstvSessionService.cs:99, set at 2350,
    cleared at 2473) to gate WHETHER the audio arm/close state machine captures at all during a file
    decode — orthogonal to reception identity, unaffected by the Correlation rewrite below.
  - Audio arm/close state machine, keyed by `ISstvDecoder.ReceptionSequence` read at the SAME
    `ModeDetected` callback that arms the slice (never re-read later): arm on `ModeDetected` (capture
    the reception id + the current push epoch; close any still-open previous slice under its OWN old
    id first). Close a slice on: the next `ModeDetected`; the mode's sample-count threshold (see
    Capacity below — this threshold must be derived from the mode's actual TRANSMISSION-line count,
    not `ImageHeight`, for the PD/MP/MN (YCbCrLinePaired) / RM8-RM12 (MonoAveragedPaired) families); or `DecodeRestarted` — but ONLY when
    `DecodeRestarted`'s push epoch differs from the armed slice's own arm epoch. A same-epoch
    `DecodeRestarted` necessarily refers to an OLDER reception (see Correlation below for why this is
    an invariant, not a per-case heuristic, and for the chunking precondition it depends on) and must
    never close the current arm.
  - New `AudioSliceReady` event, `(long ReceptionId, int SampleRate)` payload — raised only AFTER the
    closed slice's scratch-file write completes (see B3 fix below), not synchronously at close.
    `ReceptionId` is the exact `ISstvDecoder.ReceptionSequence` value captured at that slice's arm.
  - New `AudioCaptureReset` event (no payload) — raised on every RX stop/start seam: file-decode
    ENTRY (same moment `_fileDecodeInFlight` is set) and EXIT, `RequestSampleRateAsync`,
    `RequestCaptureDeviceAsync`, and TX-pause/resume (wherever `PlayWithPttAsync` stops/restarts live
    capture) — same seams `_recordingSampleRate` (SstvSessionService.cs:50-58) already tracks for the
    manual path. **Scope, narrowed from an earlier draft of this doc**: clears the capture ring inside
    `SstvSessionService`, AND discards any currently-open arm — drop the tier-2 buffer without
    encoding or writing it, emit no `AudioSliceReady` for it. It is NOT observed by `RxAudioAutoSaver`
    and does not touch the join dictionary or any already-CLOSED, already-written scratch file.
    Reasoning: disjoint reception ids already make cross-attach structurally impossible regardless of
    this event's timing, and a seam-triggered wipe of IN-FLIGHT-but-not-yet-completed PAIRS (e.g. a
    reception whose slice just closed and whose `Recorded` is still landing on a fire-and-forget
    `Task.Run`) would destroy a genuinely valid pending pair the instant a TX pause/resume happens to
    land — a real bug an earlier draft of this redesign had. Discarding an OPEN ARM is different and
    necessary: without it, a mid-arm `RequestSampleRateAsync`/`RequestCaptureDeviceAsync` would splice
    pre- and post-change samples into one WAV that `AudioSliceReady` then declares with a single
    (now-wrong-for-half-the-buffer) `SampleRate` — a real corruption risk, not just a lost recording.
    This closes the same gap as the existing accepted limitation, below, for a live reception
    interrupted by a file decode: an open arm at any of these seams is dropped, never partially
    emitted. Orphan bounding for genuinely closed-but-unpaired slices is handled entirely by the
    cap-based eviction below, which runs on its own schedule (triggered by new arrivals, not by RX
    seams).
  - New `Task<bool> TrySaveReceptionAudioAsync(long receptionId, string path)` — moves the retained
    scratch file matching `receptionId` to `path` (a cheap rename, no re-encode); returns `false` if
    no such file is currently retained (already evicted or already consumed). Renamed from the
    original `TrySaveLastReceptionAudioAsync` — "last" no longer applies once multiple slices can be
    retained concurrently (see Capacity/Correlation below).
  - On close: hand the closed append buffer to a background `Task.Run` that encodes and writes it to
    a scratch WAV file (see Correlation below for naming/retention), THEN raises `AudioSliceReady` —
    never encodes on the audio drain thread (round 3 finding: up to a ~112 MB synchronous
    encode+write on the hot path). **Peak-memory honesty**: a new arm's tier-2 buffer can be allocated
    while the PREVIOUS slice's background encode+write is still in flight (that write is not
    guaranteed to finish before the next `ModeDetected`) — so up to two full mode-sized buffers CAN
    be resident transiently (~224 MB, ~226 MB counting the pre-roll ring — see Capacity below), not
    "never," though this is bounded and short-lived versus round 2's design retaining two full
    buffers indefinitely.
  - **Settings propagation, previously unspecified:** `ISstvSessionService` gains
    `SetAutoSaveAudioEnabled(bool)` / `SetAudioDirectory(string)`, called directly by
    `OptionsWindowViewModel`'s existing Apply/Save flow (same pattern as this project's other
    live-apply Options settings) — this IS the concrete mechanism behind `AutoSaveAudioEnabled`'s
    "refreshed by the settings setter" note just below; there is no separate store-change-notification
    event for `SstvSessionService`'s own copy. `RxAudioAutoSaver` does NOT share this cached value —
    see its own bullet below for where IT reads `AudioDirectory` from. `AutoSaveAudioEnabled` cached
    as a `volatile bool` field, not read from `ISettingsStore`/`IReceiveHistoryStore` on the decode
    path.
    Toggling it mid-reception takes effect only at the next arm (on `ModeDetected`) — an in-progress
    slice is never truncated-and-emitted early by a live toggle. `AudioDirectory` changes take effect
    for NEW scratch writes and new final-save paths only — already-retained scratch files under the
    old directory are left in place (consumed or evicted normally, not migrated); this is not a live
    hot-migration of in-flight state, same v1-limitation shape as the `AutoSaveAudioEnabled` toggle.
- `src/ScanlineStudio.Core.Logbook/ReceiveHistoryRecorder.cs`:
  - New `_currentReceptionId` field, set every `OnModeDetected` from `decoder.ReceptionSequence`
    (same callback, same value the audio side arms with).
  - New `_pendingAbandonReceptionId`, stashed alongside the EXISTING `_pendingAbandon*` fields
    (~ReceiveHistoryRecorder.cs:97-104) at the same point those are populated — never a live counter
    read at abandon time, since by the time an abandon actually records, `ModeDetected` for the NEXT
    reception may already have advanced the live counter past the abandoned one's id.
  - `RecordAbandonedImageAsync` selects between `_currentReceptionId` and `_pendingAbandonReceptionId`
    via the SAME dominant/minority discriminator the recorder already applies in `OnDecodeRestarted`
    at ~ReceiveHistoryRecorder.cs:153-190. **The normal completed-image path (`OnLineDecoded` →
    `RecordCompletedImageAsync`) is DIFFERENT and does NOT use that discriminator** — it always uses
    `_currentReceptionId`, because a completed image is by definition still the CURRENT reception, not
    a candidate for the abandon logic. Critically, `_currentReceptionId` must be HOISTED INTO A LOCAL
    VARIABLE synchronously, on the same thread and at the same point, as the recorder already hoists
    its snapshot/generation/radio-state values before dispatching into the completion's own
    `Task.Run` (~ReceiveHistoryRecorder.cs:290/300/307) — a live field read from inside that closure
    could observe a LATER reception's id if a new `ModeDetected` arrives before the closure runs, and
    would stamp the wrong id onto a completed image. Both paths thread their chosen id into the
    entry's new `ReceptionId`. The existing `LineDecoded`-triggered stash-clear (~:237-245) is
    unchanged and still load-bearing for the minority-ordering case.
  - **Interference/shadowing resolved (was an open item; confirmed against `AnalogFmSstvDecoder.cs`
    directly, not assumed):** a co-channel interferer that produces a decodable header DOES raise
    `DecodeRestarted` before the interrupted reception's natural end
    (`AnalogFmSstvDecoder.cs:3751-3768`'s per-line re-verification, or the Auto Stop erratic-sync
    path at `:3692-3699`) — this is an ordinary instance of the dominant/minority orderings already
    handled above, not a distinct case. Two edge cases confirmed safe by construction, not by this
    discriminator: (a) with the "Lock" toggle engaged AND Auto Stop also off (Auto Stop's own
    erratic-sync restart at `:3692-3699` is independent of the Lock toggle and is NOT gated off by
    it alone), VIS-lock re-verification is gated off, so an interferer that never reaches `Commit()`
    produces NO restart at all — the reception decodes garbage through to its natural end with no new
    `ModeDetected` ever arming a second slice, so
    there is nothing to cross-attach (one row, one slice, containing both signals — an accepted
    quality issue, not a correctness bug); (b) `RequestAbandonReception` with a pending unresolved
    anchor (`AnalogFmSstvDecoder.cs:2001-2021`) suppresses `DecodeRestarted` entirely — this degrades
    to the ordinary "abandoned reception whose slice never gets claimed" orphan class below, not a
    cross-attach.
- NEW `src/ScanlineStudio.Application/RxAudioAutoSaver.cs` — DI singleton (corrected project: this
  needs `ISstvSessionService`, which `Core.Logbook` cannot reference —
  `ScanlineStudio.Application.csproj:14` references `Core.Logbook`, not the reverse). Subscribes
  `ISstvSessionService.AudioSliceReady` and `IReceiveHistoryStore.Recorded` ONLY — see Correlation
  below for why `IReceivedImageBuffer.Saved` is deliberately NOT subscribed (round 3 used it; this
  redesign drops it), and see the `AudioCaptureReset` bullet above for why this component does NOT
  subscribe that event either (narrowed from an earlier draft). Applies its OWN cap-based eviction to
  its join dictionary — `count > 8` parked entries, newest exempt (COUNT-only: a parked entry is
  metadata — reception id, sample rate, target entry id/path — not audio bytes, since
  `AudioSliceReady` fires only after the scratch file is already written and the buffer already
  released; a byte cap here would be checking a number that's always ~0). This is independent of
  `SstvSessionService`'s separate scratch-file-ON-DISK eviction, which DOES use the `count > 8` OR
  `bytes > 256 MB` byte-aware rule from Capacity, since that structure holds the actual audio data.
  **`AudioDirectory` source for this component**: reads `IReceiveHistoryStore.GetAudioSettingsAsync`
  at the moment each pairing completes (not cached) — this runs once per completed reception, not on
  any hot path, so a live read costs nothing and needs no separate propagation/notification mechanism
  the way `SstvSessionService`'s decode-path copy does. Derives the final saved-WAV path as
  `{AudioDirectory}/{entry.Id}.wav` — deliberately NOT the same shape as the existing image-file
  naming convention (`ReceiveHistoryRecorder.cs:341`/`:394` timestamps and prefixes the filename with
  the mode id), chosen instead for simplicity and guaranteed uniqueness via `entry.Id` alone; accept
  that this means the audio directory sorts as opaque ids rather than chronologically, unlike the
  image directory. No `Dispatcher.UIThread` anywhere (this is not UI code and could not reference
  Avalonia if it tried).
- `src/ScanlineStudio.UI/ViewModels/OptionsWindowViewModel.cs` — new enable toggle + directory row,
  same Browse/Apply pattern as the existing RX-images-folder row
  (OptionsWindowViewModel.cs:2449-2472: Browse/Apply for `ImagesDirectory`).
- `src/ScanlineStudio.UI/ViewModels/RxHistoryPaneViewModel.cs` / `RxImagePaneViewModel.cs` —
  delete-linked-WAV (tolerate missing file); storage-bytes figure; Gallery/RX-details "Open audio
  file location" + "Re-decode this frame" actions (distinct from the existing "Decode WAV file…"
  arbitrary-file entry point, RxImagePaneViewModel.cs:2073). "Re-decode this frame" surfaces the
  REAL failure reason (rate mismatch, `_autoDetectPaused`, TX in flight —
  SstvSessionService.cs:2359/2368/2375-2379 all throw for `DecodeFromFileAsync` today) rather than
  a generic error.
- `assets/locale/en.json` — new keys for the above (no hardcoded strings).
- New unit test files under `tests/ScanlineStudio.Application.Tests/` (capture buffer, correlation,
  gating) and `tests/ScanlineStudio.Core.Logbook.Tests/` (migration, store methods) per
  Verification below.

## Steps (dependency order)

1. Settings + DB schema: `ReceiveHistorySettings` fields, `IReceiveHistoryStore` get/set methods +
   `SqliteReceiveHistoryStore` implementation + migration column + `ReceiveHistoryEntry.AudioFilePath`.
2. Capture buffer in `SstvSessionService`: two-tier buffer (pre-roll ring + mode-sized append
   buffer), arm/close-to-scratch-file state machine keyed by `ISstvDecoder.ReceptionSequence` and
   the push-epoch counter (see Files touched and Correlation), gated by `_fileDecodeInFlight`,
   `AudioSliceReady` event, `TrySaveReceptionAudioAsync`.
3. Correlation: `RxAudioAutoSaver` in `ScanlineStudio.Application`, wired into DI.
4. UI: Options row, Gallery/RX-details actions, delete-linked-WAV, storage-bytes figure, and a main-
   window status indicator showing whether auto-save is currently active (added 2026-08-29, direct
   user request). **Design constraint, researched before building**: this app's established home for
   a passive/background live-state indicator is the bottom status bar's `IndustryStatus` LED+lozenge
   chip (`MainWindow.axaml`'s status-bar `StackPanel`, same pattern as the existing `Receiving`
   chip bound to `RadioStatus.IsReceiving`) -- NOT the tab-strip row. **A near-identical "AUTOSAVE
   ON" chip existed there before and was deliberately removed** (commit `2e009a9`,
   `MainWindow.axaml:1564-1590`'s own comment) specifically because it was a static label with NO
   backing property at all (RX images are unconditionally auto-saved, so the chip claimed a
   permanent truth, not live state) -- judged "fake-live" and cut on direct user request. The new
   chip must bind to a REAL, varying boolean or it risks the identical fate: the LED should light
   only while a slice is actively armed/being captured (mirroring `IsReceiving`'s own "on only while
   actually happening" shape), not a static reflection of the Options toggle alone. This needs a new
   VM-exposed property sourced from `SstvSessionService`'s arm/close state machine (step 2, not yet
   built) -- cannot be implemented before that exists. Locale keys follow the existing
   `MainWindow.StatusBar.*`/`.Help` convention (e.g. `MainWindow.StatusBar.AutoSaveAudio`).

## Pre-roll and capacity

**Pre-roll**: `VisHeader.MaxSearchCeilingMs` is `internal` to `Core.Sstv`
(`InternalsVisibleTo` only grants `Core.Sstv.Tests`/`Host.Tests` — not `Application`), so it cannot
be referenced from this feature's code even if it were the right basis. It also isn't the right
basis: it bounds only the fixed-window `TryDecodeVisHeader`/`TryDecodeNarrowModeHeader` paths, which
a closed prior decision (project memory, 2026-08-19) documents as dead for any realistic
transmission — `AnalogFmSstvEncoder`'s unconditional `OutHEAD` leader burst (400-800 ms) means
`headerStart` never aligns for those paths. Real detection goes through
`VisLockStateMachine`/`TryNarrowFskScan`/`TryInterleavedHeaderScan`, and `AnalogFmSstvDecoder.cs:
4583-4592` gates the interleaved scan to commit no earlier than `_consumedSamples +
MaxSearchCeilingMs` — 1305 ms is a *lower* bound on that path's latency, not an upper bound.
Independent of detection latency, the tone content alone needs `OutHEAD` (up to 800 ms) + extended
VIS (`PrefixDurationMs` 850 + `ExtendedTailDurationMs` 300 = 1150 ms, `VisHeader.cs:49-67`) ≈ 1.95 s
before `ModeDetected` can exist at all for a normal (non-AVT) reception.

**AVT is the real worst case, and round 3 corrected a wrong premise here**: AVT does NOT raise
`ModeDetected` from the first VIS repeat. `AnalogFmSstvDecoder.cs:379-384` documents
`_avtTrainingPending` as "a CONFIRMED detection that hasn't reached `Commit()` yet" — `Commit` is
what raises `ModeDetected` (`:4806`, `:4949`), and `TryStartAvtTraining` (`:5681-5688`) sets the
fallback deadline at `visHeaderEnd + AvtExtraHeaderDurationMs` (≈7133 ms, `VisHeader.cs:454`). So
AVT's `ModeDetected` fires AFTER the training gap, at line-data start — not before it. AVT's
pre-`ModeDetected` window is therefore `OutHEAD` (≤800 ms) + first VIS block (~910 ms) + up to
`AvtExtraHeaderDurationMs` (~7133 ms) ≈ **8.8 s**, not "no larger than the extended-VIS case" as an
earlier draft of this doc wrongly asserted.

**Decision**: define a generous constant directly in `ScanlineStudio.Application` —
`AudioPreRollMs = 10_000` (10 s) — covering the ~8.8 s AVT worst case with a ~13% margin (not a large
margin — confirm or tighten it via the measured test below before treating it as settled), without
needing a separate AVT-specific value. Ring cost at this size is still small (~1.94 MB at the max
supported 48500 Hz). Derivation written in the constant's doc comment; do not derive it from
`VisHeader`. Confirm or tune via the measured test in Verification below (record
`TotalSamplesReceived` at `ModeDetected` minus the true header start, using a REAL AVT capture and a
real extended-VIS capture through the actual detection paths, not synthetic fixed-window ones)
before shipping; 10 s is the working number to build against, not a placeholder pending user input.

**Capacity — round 2's arithmetic was wrong by ~3.7×** (used `scanDurationMs` where the formula
needs the true `LineDurationMs`, the sum of ALL of a mode's `LineSegments`, not just the scan
segment). Corrected: ML320's real `LineDurationMs` is 10.3 + 2×317.5 = 645.3 ms → 496 × 645.3 ≈
320.1 s. The actual longest mode by this formula is PD290 (616 × 937.28 ms ≈ 577.4 s ≈ 112 MB at
48500 Hz float) — but PD/MP/MN (YCbCrLinePaired) / RM8-RM12 (MonoAveragedPaired) declare `ImageHeight` as `transmissionUnits * 2` against a
per-transmission-line duration (`SstvModeRegistry.cs:435`, confirmed), so this formula double-counts
those specifically; the longest mode by actual transmission time is Pasokon P7 (496 × 818.75 ms ≈
406 s ≈ 78.8 MB). Retaining two full-size buffers (a live one plus a retained closed one, round 2's
design) would cost up to ~226 MB resident worst case — worse than the flat design it was meant to
replace. Root cause: holding the finished slice in memory at all, waiting for a caller to supply a
save path. **This same double-count bug must NOT be reintroduced by the close-trigger formula below**
— round 4's auditor pass caught it doing exactly that in an earlier draft of this doc.

**Corrected design — write to a scratch file immediately on close, don't hold it in memory:**
- Tier 1, always allocated: a small rolling ring holding exactly `AudioPreRollMs` of samples
  (~1.94 MB at `SstvSampleRate.Maximum = 48500`, `SstvSampleRate.cs:12` — matches the figure used
  elsewhere in this doc; an earlier draft said "~1 MB" here, which was wrong).
- Tier 2, allocated on `ModeDetected`, **sized to the ARMED mode**, not the global max (avoids a
  25-112 MB allocation on every arm regardless of what's actually receiving) — worst case ~78.8 MB
  for the longest real single-pass mode (Pasokon P7), ~112 MB for the longest by the raw formula
  (PD290, accepted as a safety margin since the true PD290 duration is roughly half that).
- On arm: seed the append buffer with a copy of the current pre-roll ring contents (race-free: both
  the ring writes and `ModeDetected` fire synchronously on the same audio drain thread, confirmed
  against `MiniAudioCaptureSession.DrainLoop`), then keep appending live samples until close.
- **On close: hand the closed append buffer to a background `Task.Run`** that encodes and writes it
  to a scratch WAV file named `scratch/{sessionGuid}/{receptionId}.wav` (in the configured audio
  directory) — `SstvSessionService` OWNS this file: writing it, and applying the retention/eviction
  rule below to it; `RxAudioAutoSaver` never touches scratch files directly, only calling
  `TrySaveReceptionAudioAsync` (see Correlation below for why ownership is split this way and why
  `AudioCaptureReset` no longer plays a role in either side's eviction). THEN releases the in-memory
  buffer and raises `AudioSliceReady` — never on the audio drain thread itself (round 3 finding: up
  to a ~112 MB synchronous encode+write would land on the hot path otherwise). Unlike round 3's plan,
  this v2 retains MULTIPLE scratch files at once (see Correlation below for why a single "last" file
  contradicts a keyed join): after each write, evict oldest-first while `count > 8` OR
  `totalBytes > 256 MB` (named constants, derivation — at least 2 worst-case slices — in their doc
  comment; the byte math must follow whatever the actual scratch WAV format emits, float32 or 16-bit
  PCM, not assume one), always EXCLUDING the newest entry from eviction so a single worst-case slice
  can never evict itself the moment it's written. On process startup, purge scratch subdirectories
  belonging to dead prior sessions (see Correlation below for the liveness check) — this purge is
  DISK HYGIENE ONLY, not a correctness requirement: the `scratch/{sessionGuid}/` naming already makes
  a cross-session id collision structurally impossible on its own, so an unpurged directory from a
  crashed prior session costs disk space, never a wrong attach.
  `TrySaveReceptionAudioAsync(receptionId, path)` becomes a file move/rename from the matching
  retained scratch path to the caller-supplied final path (cheap, no re-encoding, no second
  in-memory copy), returning `false` if no file for `receptionId` is currently retained (already
  evicted or already consumed). **Peak resident memory, stated honestly (an earlier draft of this doc
  overclaimed "never two full-size buffers at once"):** ~1.94 MB (ring) + up to TWO full mode-sized
  append buffers (~224 MB worst case) can be resident TRANSIENTLY, if a new arm's tier-2 buffer is
  allocated before the previous slice's background encode+write finishes releasing its own buffer —
  bounded and short-lived (one buffer releases as soon as its own background write completes), but
  not literally "never two at once." Scratch DISK usage is separately bounded by the byte cap above.
- Close trigger: sample-count based, not a wall-clock timer (see "Inactivity timeout" below) — a
  slice closes at the armed mode's TOTAL TRANSMISSION-LINE COUNT × its per-transmission-line
  `LineDurationMs` worth of samples (+ a small stated margin for line-sync drift), counted from the
  arm point. **Do NOT use `armedMode.ImageHeight * armedMode.LineDurationMs` for this** — for the
  PD/MP/MN (YCbCrLinePaired) / RM8-RM12 (MonoAveragedPaired) families `ImageHeight` is `transmissionUnits * 2` (see the double-count note above),
  so that formula would run PD290's slice at ~577 s instead of its true ~289 s transmission time,
  roughly doubling the WAV size and delaying `AudioSliceReady` by several minutes for that whole mode
  family. **Implementation note, corrected during coding (2026-08-29): derive the rows-per-
  transmission-line count from `mode.ColorEncoding` directly** (`YCbCrLinePaired`/`MonoAveragedPaired`
  → 2, everything else → 1) **rather than a named-family list** — this doc's own earlier prose
  wrongly grouped MC into the "PD/MP/RM/MN/MC" doubling family; `SstvModeRegistry.CreateMcFamilyMode`
  actually declares `ColorEncoding.RgbSequential` with a literal `ImageHeight: 256`, not
  `transmissionUnits * 2` — MC does NOT double, despite the superficial family-name similarity to
  MN. Keying off `ColorEncoding` instead of a name list is both correct today and safe against this
  exact class of error recurring for a future mode. Use the same transmission-line-count basis the
  Capacity section above already derives correctly, not `ImageHeight` again. AVT already fires
  `ModeDetected` AFTER the ~7.1 s training gap
  — see above, so **no additional AVT term is needed here**; round 3's first pass at this fix added
  `AvtExtraHeaderDurationMs` to the close threshold based on the wrong premise that `ModeDetected`
  fires BEFORE that gap, which would have both over-extended every AVT slice by ~7 s and delayed
  `AudioSliceReady` by the same. Close also fires immediately on the next `ModeDetected`, or on a
  same-arm-epoch `DecodeRestarted` (see Correlation below for the epoch rule that replaces "whichever
  comes first" with an exact discriminator).

## Correlation (redesigned a 4th time — round 4 rejected round 3's rendezvous-FIFO outright)

Round 1: `Saved`+`Recorded` on a false same-thread-ordering claim. Round 2: `Recorded`-alone matched
by `ModeId` against a single mutable "last payload" field — broken by concurrent `Recorded` calls and
by a never-invalidated stale match. Round 3's rendezvous-FIFO (two queues, paired by arrival order,
flushed at RX seams) fixed round 2's specific bugs but was itself fragile by construction: an
orphaned entry on either queue — e.g. an abandoned reception whose `Recorded` fires with no `Saved`
to rendezvous against — permanently desyncs every LATER pairing until the next `AudioCaptureReset`.

**Root cause, identified in round 4: reception identity was being reconstructed downstream from
arrival order instead of assigned upstream at the moment of arming.** The redesign below assigns one
shared identity ONCE, at the single point both the audio side and the image side already react to
the same underlying signal (`ModeDetected`), and keys everything off that identity instead of off
which event happened to arrive first.

### Identity

- `ISstvDecoder.ReceptionSequence` (see Files touched) is incremented exactly once per reception, in
  `RestartableSstvDecoder.OnModeDetected`, BEFORE the event fans out to subscribers. Every
  subscriber — the audio arm/close state machine, `ReceiveHistoryRecorder`, UI panes — reads the
  identical value for the same reception, with no ordering dependency between them.
- `DecodeRestarted` never bumps this counter. **Why this matters, and why mode identity alone is NOT
  a safe substitute discriminator:** `ISstvDecoder.cs:95-107` documents two real orderings —
  dominant (`DecodeRestarted` fires first, for the OLD image, then a NEW mode's `ModeDetected`
  arrives later, possibly in a much later `PushSamples` call) and minority (AVT, `ForceMode`-into-AVT:
  `ModeDetected` for the NEW reception fires first, then `DecodeRestarted` closes out the OLD image,
  both within the SAME `PushSamples` call — confirmed at source level,
  `AnalogFmSstvDecoder.cs:3754-3768` / `:2824-2832`: `Commit()` raises `ModeDetected` inline before
  the same call's `DecodeRestarted` raise). **The exact same minority shape also occurs for an
  ordinary same-mode-to-same-mode restart** (e.g. Scottie 1 → Scottie 1) — `SstvModeDefinition`
  instances are shared `public static readonly` singletons (`SstvModeRegistry.cs`), so "does this
  `DecodeRestarted`'s mode match the mode I just armed" can't distinguish "the restart my own arm
  already accounted for" from "a genuinely new restart of the reception I just armed." **The
  invariant that actually holds, independent of mode identity:** a `DecodeRestarted` that fires
  within the SAME `_decoder.PushSamples` call as the `ModeDetected` that armed the current slice
  necessarily refers to an OLDER reception, and must never close the current arm; a `DecodeRestarted`
  in a LATER call is real and must close it. This is what the push-epoch counter encodes (see Files
  touched: bumped around every `PushSamples` call via one private helper, so no call site can miss
  it) — same-epoch `DecodeRestarted` is suppressed, different-epoch closes normally. **This invariant
  is empirical, not structural — it holds only under the chunking precondition stated on
  `ISstvDecoder.ReceptionSequence` in Files touched** (today's live/file-decode push sizes never let
  two independent restart-triggering events land in one `PushSamples` call); it is not true for an
  arbitrary caller that pushes unbounded buffers, which is why that precondition also binds test
  harnesses, not just production code paths.

### Recorder-side id selection (abandoned images)

`ReceiveHistoryRecorder` already distinguishes the two orderings for its own stash/clear logic at
`ReceiveHistoryRecorder.cs:153-190` (stash on `OnModeDetected`/`OnDecodeRestarted` depending on
which fires first, clear the stash on the new image's first `LineDecoded` at `:237-245` — that clear
is the proof that the minority-ordering restart has already arrived, and stays load-bearing
unchanged). The abandoned path reuses this SAME discriminator to pick between `_currentReceptionId`
(the live counter, correct when the abandon is recorded in the dominant ordering, where the counter
hasn't advanced yet) and `_pendingAbandonReceptionId` (the value stashed at abandon time, correct in
the minority ordering, where the live counter has already advanced past the abandoned reception) —
never a live property read regardless of which branch runs.

### The join

- `RxAudioAutoSaver` holds one `Dictionary<long, object>` under one `lock (_gate)`, keyed by
  `ReceptionId`. It subscribes `AudioSliceReady` and `Recorded` ONLY — **`Saved` is deliberately NOT
  subscribed, and `AudioCaptureReset` is deliberately NOT subscribed either** (see below for why the
  latter was removed from an earlier draft of this design).
- On `AudioSliceReady(receptionId, sampleRate)`: under the lock, if an entry for `receptionId` is
  already parked (a `Recorded` arrived first), complete the pair immediately; otherwise park the
  slice payload under `receptionId`.
- On `Recorded(entry)`: under the lock, if `entry.ReceptionId != 0` and an entry for it is already
  parked (an `AudioSliceReady` arrived first), complete the pair immediately; otherwise park
  `entry.FilePath`/`entry.Id` under `entry.ReceptionId`. **`entry.ReceptionId == 0` means no audio
  identity was ever assigned for this row (e.g. a disk-reconciled backfill) — skip it entirely, do
  not park, do not error.**
- Completing a pair: derive `wavPath = {AudioDirectory}/{entry.Id}.wav` (see Files touched), call
  `TrySaveReceptionAudioAsync(receptionId, wavPath)`, then on success
  `SetAudioFilePathAsync(entry.Id, wavPath)`, then remove the dictionary entry (one-shot).
- **Why the FIFO's fragility is actually gone, not relocated:** because the key is the reception's
  own identity rather than queue position, an orphan (e.g. an abandoned reception whose `Recorded`
  never gets a matching slice, or vice versa) strands only ITS OWN dictionary entry. It can never
  cause a later, unrelated pairing to steal or miss its partner — there is no shared queue for it to
  desync.
- **Manual "Save frame as…" is structurally excluded, not merely mitigated:** since the join never
  subscribes `Saved`, and `RxImagePaneViewModel.SaveFrameAsAsync`'s manual path never raises
  `Recorded` (`ReconcileWithDiskAsync` deliberately doesn't, `IReceiveHistoryStore.cs:162-166`), a
  manual save can never enter the correlator at all. Round 3's accepted v1 limitation (a manual save
  landing mid-reception could steal that reception's pairing) is fully closed, not just accepted.
- **`AudioCaptureReset` is NOT subscribed by `RxAudioAutoSaver` (narrowed from an earlier draft of
  this design, which had it clear the join dictionary at every RX seam):** doing so would destroy a
  genuinely valid pending pair the instant a seam happens to land — e.g. a reception that just closed
  and whose `Recorded` is still landing on a fire-and-forget `Task.Run` when an operator-initiated TX
  pause/resume fires the reset. Correctness does not depend on this event firing at the right instant
  at all: file-decoded receptions draw fresh, never-colliding ids from the same counter as live
  receptions (`DecodeFromFileAsync` pushes into the same `_decoder`, serialized by
  `_rxTransitionGate`), so there is no hazard for a reset to prevent here, unlike round 3's design
  where it was the only thing preventing a file decode's `Saved` from pairing with a stale live
  slice. `AudioCaptureReset` still exists and still matters, but ONLY for its original job — clearing
  `SstvSessionService`'s own capture ring (see Files touched) — not for anything in this component.
- **Orphan classes and their bound:** (1) an abandoned reception whose slice is still open when RX
  stops entirely — never gets an `AudioSliceReady`; (2) a `Recorded` that never arrives because
  `RecordAsync` throws; (3) a mid-reception CRITICAL decoder swap
  (`RestartableSstvDecoder.cs:676-679` — the mandatory swap that can fire regardless of `IsIdle`; NOT
  the separate `IsIdle`-gated maintenance swap at `:681-688`, which cannot fire mid-reception) that
  raises only the payload-less `Restarted` event, never `DecodeRestarted` — its slice closes later on
  sample count with no reception ever claiming it; (4) `RequestAbandonReception` landing while a
  detection anchor is still unresolved (`AnalogFmSstvDecoder.cs:2001-2021`) suppresses
  `DecodeRestarted` entirely, reachable from the interference/interruption scenario discussed under
  Recorder-side id selection above. All four degrade to one bounded leaked dictionary entry and/or one
  bounded leaked scratch file, never a cross-attach. Bound: `RxAudioAutoSaver` applies its OWN
  cap-based eviction to its join dictionary — oldest-first while `count > 8` parked entries, newest
  exempt. **COUNT-only, not byte-capped**: a parked join entry is metadata (reception id, sample
  rate, target entry id/path), not audio bytes — `AudioSliceReady` fires only after the scratch file
  is already written and its buffer already released, so there is no meaningful byte total to cap
  here. This is independent of, and uses a different rule than, `SstvSessionService`'s separate
  scratch-file-ON-DISK eviction (`count > 8` OR `bytes > 256 MB` — see Capacity above, where the
  bytes actually live); two independent bounded structures, no shared trigger, don't conflate them.
  No wall-clock TTL on either: eviction pressure exists only when new receptions/writes arrive, which
  is exactly when eviction runs, so a timer would close no additional gap.
- **Scratch-directory session scoping and stale-session purge** (owned by `SstvSessionService`, which
  is what writes these files — see Capacity above): each process writes its scratch files under
  `scratch/{sessionGuid}/` inside the CURRENT `AudioDirectory`, and on startup purges sibling
  subdirectories belonging to DEAD prior sessions only, as disk hygiene (the GUID scoping alone
  already makes a cross-session id collision impossible — see Capacity above). This purge only scans
  the current `AudioDirectory` — if the setting changes between sessions, a prior session's scratch
  subdirectory under the OLD directory is never purged; accepted as a disk-hygiene gap only (not a
  correctness issue, since GUID scoping already prevents any collision). Liveness check: each session
  writes a marker
  (`{pid, startTimeTicks}`) into its own subdirectory at creation; a sibling is purged only if
  `Process.GetProcessById(pid)` throws (the process is gone) or the live process's actual `StartTime`
  doesn't match the recorded one (the PID was reused by an unrelated process). Any access error while
  checking (e.g. permission denied reading another user's process) is treated as "assume live, skip"
  — the safe direction, since an un-purged leak only costs disk. Do NOT use an OS file-lock/lock-file
  mechanism for this check — file locking is not reliably enforced cross-process on Unix for this
  purpose (`FileStream.Lock` throws `PlatformNotSupportedException` on macOS; `flock`-based
  enforcement is disable-able via `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` and unreliable on network
  filesystems), and a mechanism that can silently degrade to "looks locked, isn't" is worse than no
  lock at all — it would let the purge delete a genuinely live sibling session's in-flight scratch
  files.
- This design covers abandoned images in v1 (reversing round 1's exclusion, on round 1's own
  reasoning: the audio is exactly what an operator wants for a botched capture, and `DecodeRestarted`
  already closes the slice with no extra work).

### Accepted v1 limitations (state explicitly, not implied)

- A live reception interrupted by a file decode (`DecodeFromFileAsync` → `RequestAbandonReception`
  while the live arm/close machine is gated by `_fileDecodeInFlight`) loses its audio: the live
  reception's `Recorded` arrives with no matching slice. This is a MISS, never a wrong attach — same
  safe-direction trade-off as round 3's design, just restated under the new mechanism.
- Orphaned join entries and scratch files are bounded by the cap-based eviction above, not
  individually reclaimed the instant they're known to be orphaned.
- **No shutdown flush.** A normal app close (or crash) while a slice has already closed but is still
  encoding, or is sitting in the join dictionary waiting for its matching `Recorded`, loses that one
  reception's audio outright -- the in-flight background work is simply abandoned along with the
  process. Deliberate, user-decided scope for v1: the actual vulnerable window per reception is short
  (the encode+write of already-in-memory samples, plus the correlator's pairing wait -- on the order
  of tens of milliseconds to roughly a second, against a reception cycle that runs minutes), so this
  is rare in practice, and a bounded shutdown-wait mechanism was considered and explicitly declined
  as not worth the added complexity for that exposure. If this needs revisiting later, the cheap
  version is a short bounded wait for in-flight `RxAudioAutoSaver` work on app shutdown -- not a full
  guarantee, just closing most of the real-world exposure.
- Any reception whose arm is still OPEN when `AudioCaptureReset` fires (sample-rate change, capture-
  device change, TX pause/resume, file-decode entry/exit) loses its audio entirely — no partial
  slice is encoded or emitted for it. Same MISS-not-wrong-attach direction as the file-decode case
  above, now stated as the general rule an earlier draft of this doc left implicit.
- With the "Lock" toggle engaged and Auto Stop also off, a co-channel interferer that never resolves
  to its own `Commit()` produces NO restart at all — the interrupted reception decodes garbage
  through to its natural end with no second `ModeDetected` ever arming a second slice. Result: one
  row, one slice, containing audio from both signals. This is a quality issue (a corrupted image with
  its own genuinely-matching audio), not a correctness bug — no cross-attach is possible when no
  second reception is ever armed.

### Interference/shadowing (resolved — was an open item pending decoder-source verification)

A stronger co-channel signal interrupting an in-progress reception IS confirmed, at the decoder
source level, to be an ordinary instance of the dominant/minority orderings this design already
handles — see the "Interference/shadowing resolved" note under Recorder-side id selection above for
the two edge cases (Lock-toggle-off no-restart, and unresolved-anchor suppression) verified safe by
construction rather than by the discriminator itself. No design change was needed once this was
checked against `AnalogFmSstvDecoder.cs` directly.
**Attach notification**: `SetAudioFilePathAsync` is a plain `UPDATE`, not a `Recorded` re-raise —
re-raising would make every existing `Recorded` subscriber (`RxHistoryPaneViewModel`,
`RxImagePaneViewModel.PreviousFrames`) handle a synthetic second event for the same row, a bigger
behavior change than this feature needs. Instead, `RxHistoryPaneViewModel`/`RxImagePaneViewModel`
patch their already-held in-memory `ReceiveHistoryEntry` for that row directly when
`RxAudioAutoSaver` reports success (new lightweight event or return value the UI layer subscribes
to — implementation detail for code-review, not a design fork). Accept that a full app restart
before that patch fires re-reads the real `AudioFilePath` from disk regardless.

## Concurrency/threading notes

- `SamplesCaptured` fires on the audio drain thread (same contract as `_decoderHandler`/
  `_waterfallHandler`/`_levelMeterHandler`/`_recordingHandler`, subscribe-inside-lock pattern at
  SstvSessionService.cs:2263-2272 is the template: subscribe/unsubscribe under one lock, do disk I/O
  outside it via `Task.Run`).
- `ModeDetected`/`DecodeRestarted` fire synchronously on the audio drain thread FOR LIVE capture, but
  `DecodeFromFileAsync` pushes into the same shared `_decoder` from its own `Task.Run`
  (SstvSessionService.cs:2429) — so across a session there are two producer CONTEXTS, mutually
  excluded by `_rxTransitionGate`, not literally one thread. The conclusion still holds (each
  individual raise is a synchronous fan-out reaching every subscriber before the raising call
  returns), which is what makes same-value-for-all-subscribers safe — but `ReceptionSequence` must
  be `Interlocked.Increment`/`Volatile.Read`, not a plain field, and the epoch counter needs the same
  treatment, since `ISstvDecoder.ReceptionSequence` is documented as safe to read from any thread for
  other implementers (`AnalogFmSstvDecoder`, the 3 test fakes) to honor.
- File-decode gating: `DecodeFromFileAsync` calls `StopReceivingLockedAsync()` (SstvSessionService.cs
  :2413), which unsubscribes all three live handlers AND stops live capture — so no LIVE audio
  arrives during a file decode. `_fileDecodeInFlight` still gates whether the audio arm/close state
  machine captures at all during a file decode (unchanged from round 3) — this is now an
  independent, orthogonal concern from reception identity: even without this gate, a file-decoded
  reception would get its own non-colliding id from the same counter as live receptions, so a stale
  live slice could no longer be wrongly attached to it even if the gate were removed. The gate is
  kept because capturing audio during a file decode serves no purpose (the file itself is the
  audio), not because correctness depends on it.
- Rate/device change: `RequestSampleRateAsync`/`RequestCaptureDeviceAsync` (SstvSessionService.cs:
  1420-1438, 1581-1594) both currently defer when `_recordingChunks is not null` — the new ring must
  NOT be added to that check (doing so would permanently block both settings once auto-save is
  enabled). Instead, both handlers clear/reallocate the ring as part of their existing
  stop/reconfigure/restart sequence, same seam `_recordingSampleRate` already tracks for the manual
  path.
- `RxAudioAutoSaver` correlation: no `Dispatcher.UIThread` (not UI code), but real cross-thread
  access — `AudioSliceReady` fires from a background `Task.Run` (after the scratch-file write),
  `Recorded` fires on arbitrary, potentially-concurrent threadpool threads (confirmed: it fires
  inline in `SqliteReceiveHistoryStore.RecordAsync`, `SqliteReceiveHistoryStore.cs:166`, on whatever
  threadpool thread a fire-and-forget `Task.Run` happens to run on, `ReceiveHistoryRecorder.cs:
  312-326`; two completions from a bulk WAV decode can call it concurrently). `RxAudioAutoSaver` does
  NOT subscribe `AudioCaptureReset` at all (see Correlation above) — its own join dictionary's every
  access goes through one `lock (_gate)`, and its own cap-based eviction runs inline on whichever
  thread completes a write, not on any RX-seam thread.
- `_fileDecodeInFlight` is used ONLY by the arm/close state machine's own gate (same field, same
  read/write pattern as before — no new consumer). It is NOT read by `RxAudioAutoSaver` — identity
  correctness no longer depends on any point-in-time flag, live or otherwise (see Correlation above).

## Verification

- Ring buffer doesn't grow unbounded over a long synthetic capture (assert resident buffer count/
  size stays at the two-tier ceiling, not proportional to elapsed time).
- Pre-roll actually includes pre-`ModeDetected` samples for BOTH a real extended-VIS capture and a
  real AVT capture through the actual detection paths (not synthetic fixed-window ones) — use this
  run to confirm or tune `AudioPreRollMs` (currently 10 s, ~13% margin over the AVT worst case)
  before shipping; AVT is the binding case.
- No slice taken during a file decode; stale pre-file-decode ring contents are discarded.
- **`ReceptionSequence` assigns one id per ARM, verified against both real orderings**: for a normal
  dominant-ordering restart (`DecodeRestarted` then a LATER `ModeDetected`), the two receptions get
  distinct ids and distinct audio; for a minority-ordering restart (AVT, `ForceMode`-into-AVT:
  `ModeDetected` then a SAME-`PushSamples`-call `DecodeRestarted`), the NEW reception's slice is NOT
  truncated by the immediately-following restart, and the recorder's abandoned-image id selection
  matches the audio side's id for the SAME abandoned reception. Include an ordinary same-mode
  back-to-back restart (e.g. Scottie 1 → Scottie 1) as its own case — mode identity must NOT be able
  to cause a false-suppress here, only the push-epoch check may. Also assert the chunking
  precondition directly: a test that deliberately pushes an oversized single buffer containing two
  real restarts through `FakeAudioEngine.PushCapturedSamples` demonstrates the false-suppress failure
  mode this precondition exists to prevent, so a regression in test-harness chunking is itself
  caught, not silently passing.
- The push-epoch counter is bumped on EVERY `_decoder.PushSamples` call site, including the
  auto-detect-pause flush at ~SstvSessionService.cs:384 — not just the two obvious sites — and NOT
  bumped for the self-test loop's separate local decoder instance.
- **Close-threshold correctness for the PD/MP/MN (YCbCrLinePaired) / RM8-RM12 (MonoAveragedPaired) families specifically**: PD290's slice closes
  at its true ~289 s transmission time, not ~577 s — assert the close-trigger sample count is derived
  from transmission-line count, not `ImageHeight`, for at least one mode from this family. A
  regression here (reintroducing the `ImageHeight`-based formula) would double the WAV size and delay
  `AudioSliceReady` by minutes for this whole mode family without any test failing elsewhere.
- **Recorder completed-image path uses a synchronously-hoisted id, not a live read**: construct a
  race where a new `ModeDetected` (and its counter bump) arrives between the completed image's
  `OnLineDecoded` trigger and its `Task.Run` closure actually running, and assert the completed
  entry's `ReceptionId` is the OLD (correct) reception's id, not the new one.
- Join correctness: `AudioSliceReady` arriving before `Recorded` for the same `ReceptionId`, AND
  `Recorded` arriving before `AudioSliceReady` for the same `ReceptionId` — both orders attach the
  same reception's audio to the same reception's row. Two rapid, genuinely concurrent completions
  from one bulk WAV decode (must not cross-attach — exercise with real concurrent `Task.Run`
  completions, not a sequential simulation). An abandoned image, matched by exact `ReceptionId` (not
  a `ModeId` heuristic). A manual "Save frame as…" during an armed reception causes NO
  attach-related side effect whatsoever (verify it never even reaches `RxAudioAutoSaver`, since
  `Saved` isn't subscribed) — this closes round 3's accepted limitation rather than merely bounding
  it. A live reception's slice followed immediately by a file decode does not cross-attach — assert
  this holds with NO `AudioCaptureReset` firing at all during the test (that event is not subscribed
  by `RxAudioAutoSaver` in this design; correctness must not depend on it).
- **Join dictionary applies its own COUNT-only cap-based eviction, independent of scratch-file
  eviction**: drive more than 8 receptions without any of them completing their pairing, and assert
  oldest-first eviction with the newest entry exempt — this is a count-only rule (parked entries are
  metadata, not audio bytes, so there is no byte cap to test here), deliberately DIFFERENT from the
  scratch files' count-OR-bytes rule, verified as a SEPARATE structure (a bug in one must not be
  masked by the other happening to also be correct).
- `entry.ReceptionId == 0` (a disk-reconciled/backfilled entry) is skipped by the correlator without
  parking or erroring.
- `TrySaveReceptionAudioAsync` returns `false` (does not overwrite, does not re-encode) when called
  with a `receptionId` that isn't currently retained (already evicted or already consumed).
- Scratch-file retention: after N+1 writes (N = the configured file cap), the oldest is evicted
  UNLESS it is also the newest (single-slice edge case); disk usage stays under the configured byte
  cap under sustained reception; a single worst-case slice larger than the byte cap is retained, not
  evicted, since the newest entry is always exempt.
- Startup purge deletes only scratch subdirectories for sessions confirmed dead (PID no longer
  running, or PID reused by a different process per a start-time mismatch) — a live sibling
  session's subdirectory (simulate two instances running concurrently) is never purged.
- The scratch-file encode+write never runs on the audio drain thread (assert via a timing/thread-id
  check, not just "the test passed") — `AudioSliceReady` only fires after that background write
  completes. A back-to-back arm arriving before the previous slice's background write completes does
  not corrupt either buffer (the stated transient up-to-two-full-buffers peak, not "never," see
  Capacity above) and both slices still attach correctly.
- AVT `ModeDetected` fires AFTER the ~7.1 s training gap (confirm this against the real decoder, not
  assumed) — the close trigger must NOT add any extra AVT-specific term on top of that, and a
  re-decode of an AVT auto-saved WAV must not be missing its header or its tail lines.
- Migration adds the column in the right position and is idempotent across two startups.
- Delete removes PNG+WAV+row, tolerating a missing WAV; a WAV that predates this feature's install
  (no row references it) is left alone.
- **Settings propagation**: `OptionsWindowViewModel`'s Apply/Save flow calling
  `SetAutoSaveAudioEnabled`/`SetAudioDirectory` actually changes `SstvSessionService`'s cached
  `volatile bool`/directory value; toggling `AutoSaveAudioEnabled` mid-reception does not truncate or
  early-emit the in-progress slice (takes effect at the next arm only); changing `AudioDirectory`
  mid-session does not move or lose already-retained scratch files under the old directory, and new
  slices/final-save paths use the new directory.
- **Final WAV path derivation**: a successfully paired reception's WAV lands at
  `{AudioDirectory}/{entry.Id}.wav`; two receptions never produce colliding final paths (guaranteed
  by `entry.Id` uniqueness, verify it holds under a rapid back-to-back sequence too).
- End-to-end: enable the option, decode a known WAV through the real detection path, confirm the
  auto-saved WAV re-decodes within a stated pixel-difference tolerance (not "visually identical").
- "Re-decode this frame" surfaces the real failure reason (rate mismatch / auto-detect paused / TX
  in flight), not a generic error.
- Manual: disk-usage figure updates; option off writes nothing; toggling the option mid-reception
  does not emit a truncated slice for that reception.
- **Stronger-signal interruption (resolved, not an open item — verify the resolution, not the
  question)**: a co-channel interferer that produces a decodable header raises `DecodeRestarted`
  before the interrupted reception's natural end; verify the interrupted image is recorded as
  abandoned under its own correct `ReceptionId`, its audio slice closes and attaches to that
  abandoned entry (not the interrupting reception's entry), and the interrupting reception's own
  slice starts clean from its own arm (backfilled by the pre-roll ring, which will legitimately
  overlap the tail of the interrupted reception's own audio — assert each row's slice contains its
  own reception's header and line data, NOT "no audio shared or duplicated between the two rows",
  which is unsatisfiable by construction given the pre-roll ring's design). Separately verify the two
  accepted no-restart edge cases produce no cross-attach: Lock-toggle-engaged-AND-Auto-Stop-off
  interference (one row, one slice, both signals present — a quality issue, not a wrong attach; test
  with Auto Stop ON too, and confirm it DOES restart in that configuration, per the condition fix
  above) and unresolved-anchor
  suppression (degrades to the ordinary abandoned-reception orphan class, bounded by the cap).
