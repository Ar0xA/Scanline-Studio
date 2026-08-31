# Production-readiness audit plan (2026-08-30)

Senior-engineer review of the full `src/` tree (~75,600 lines, 18 projects), run as 7 parallel
`auditor` passes (read-only): 6 scoped to one subsystem each, 1 scoped to the seams between them.
Scope, method, and prompts: efficiency, optimization, dedup, correct API usage, logging/exception
handling, plus general senior-review judgment (nullable/SOLID/DI, concurrency, disposal, security,
test coverage). This is a **plan**, not a changelog — nothing listed here has been fixed yet.

Per-subsystem go/no-go, from each audit's own closing verdict:

| Subsystem | Verdict |
|---|---|
| Infra/cross-cutting (`Host`, `Settings`, `Abstractions`, `Localization`, `Plugins`) | **No** — H1/H2/H4 below |
| `Application` | **No** — settings race (Theme 1) |
| `Core.Sstv` (DSP engine) | Conditional — B1/B2/C2 below need fixing; structure/dedup items are post-ship |
| `Core.Audio`/`Core.Imaging`/`Core.Logbook` | **No** — QRZ plaintext password, missing SQLite indexes, RX-path LOH churn |
| `ScanlineStudio.UI` | **No** — undisposed bitmaps, UI-thread-blocking recompute, 3 untested god-ViewModels |
| `Core.Radio.*`/CAT (Hamlib/flrig/rigctld/OmniRig) | **Yes**, with 2 one-line pre-ship fixes |
| Cross-subsystem seams | **No** — radio PTT-safety gap, same settings race confirmed independently |

**Two findings were discovered independently by two different audits, which is the strongest signal
in this report:**
1. **`ISettingsStore` has no atomic read-modify-write** — found by both the `Application` audit
   (10 call sites) and the cross-subsystem audit (25 call sites across `UI`+`Application`+
   `Core.Logbook`). One fix closes both.
2. Radio PTT un-key safety — the cross-subsystem audit's A1/A2 sharpen the `Core.Radio` audit's own
   B2 finding into a concrete safety gap: the retry loop protecting against a stuck-keyed
   transmitter is structurally inert on the Hamlib backend specifically.

---

## Tier 0 — blockers (fix before calling this production-ready)

### T0-1. Radio: un-key retry loop is inert on the Hamlib backend [safety]
**Files:** `Core.Radio.Hamlib/HamlibRadioProtocol.cs:572-581` (unbounded `_lock.WaitAsync(ct)`, no
timeout — the only one of 4 backends without one); `Application/RadioSessionService.cs:222-246`
(3-attempt un-key retry, `CancellationToken.None`).

If a native Hamlib call wedges holding `_lock`, the un-key retry's first attempt blocks forever and
attempts 2-3 never run. `SstvSessionService.TryUnkeyPttAsync` already has the right pattern
(`Application/SstvSessionService.cs:4262-4326` — bounded wait, unbounded command, fault-observer
continuation) but `RadioSessionService`'s copy doesn't use it.
**Fix:** give `HamlibRadioProtocol.AcquireAsync` the same `SemaphoreAcquireTimeout` flrig/OmniRig
already have (direct copy, no design work). Extract `SstvSessionService`'s bounded-un-key sequence
into a shared `Application`-layer helper; call it from `RadioSessionService` too.
**Investigate first:** whether abandoning a truly wedged `rig_*` call on a shared `RIG*` handle is
itself safe — `HamlibRadioProtocol.cs:24-32` already discusses this tradeoff.

### T0-2. `ISettingsStore` has no atomic update; ~25 read-modify-write sites can silently clobber each other
**Confirmed by 2 independent audits.** Root: `Settings/JsonSettingsStore.cs:12-15,50,90` — `_fileLock`
serializes individual `LoadAsync`/`SaveAsync` calls, not a caller's load→mutate→save *sequence*
against another caller's. `WithSection` clones the whole `Sections` dict
(`Settings/AppSettingsSectionExtensions.cs:18-25`), so a lost update drops another subsystem's
**entire settings section**, not one field.
Reachable today: `RadioStatusViewModel`'s debounced TX-volume persist vs. Options Save (an operator
can transmit at the wrong power with the UI showing the right number); `MainWindow`'s
window-geometry-on-close save vs. any in-flight Application save; `ConfigurationPresetService` vs.
`OptionsSettingsService` vs. `SstvSessionService` (5 sites).
Call sites: `Application` (16 — `SstvSessionService.cs` ×5, `RadioSessionService.cs` ×2,
`ConfigurationPresetService.cs` ×2, `OptionsSettingsService.cs` ×8), `UI` (7), `Core.Logbook` (2).
**Fix:** add `Task<AppSettings> UpdateAsync(Func<AppSettings,AppSettings> mutate, CancellationToken ct)`
to `ISettingsStore`; implement in `JsonSettingsStore` as load+mutate+save inside one `_fileLock`
acquisition. Migrate all ~25 call sites — most collapse to a one-line lambda (also the largest
single dedup win found in this whole review: 5 near-identical guard blocks in
`ConfigurationPresetService.cs:113-169` alone).
**Investigate first:** confirm no mutate lambda does I/O or awaits (`_fileLock` isn't reentrant);
a few sites read other sections off the same loaded snapshot in one breath
(`LogbookSessionService.LogQsoAsync:71-83`) and need that read kept outside the mutate lambda.

### T0-3. Startup blocks the main thread on radio-connect/audio-open with no timeout
`Host/Program.cs:219-241` — `ConnectUsingSettingsAsync().GetAwaiter().GetResult()` and
`StartReceivingAsync().GetAwaiter().GetResult()` run *before* `SetupWithLifetime`, with `ct=default`
passed through (`Application/RadioSessionService.cs:37,44`). A powered-off rigctld host or a stalled
audio server hangs the app with **no window on screen** for 20+ seconds — reads as a full hang, not
a radio problem.
**Fix:** move both to a post-window background start (`Task.Run` with the existing `Log.*Failed`
handlers), or wrap each in `.WaitAsync(TimeSpan)`.
**Investigate first:** confirm nothing downstream assumes RX capture is live by the time
`MainViewModel` is constructed.

### T0-4. DI container graph is never validated before first resolve
`Host/Program.cs:133-142` — `ValidateOnBuild`/`ValidateScopes` only enabled in `Development`, which a
GUI launch never is. A missing registration surfaces as an unguarded crash from `SetupWithLifetime`
→ `MainViewModel` resolution instead of the already-handled `Console.Error` + rethrow at line 140.
Factory-backed singletons make it worse: MS.DI doesn't cache a failed construction, so the crash
repeats on every resolve.
**Fix:** `ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions {
ValidateOnBuild = true, ValidateScopes = true }))` before `Build()`. Cheap, existing tests already
prove the graph resolves clean. No investigation needed.

### T0-5. Shutdown can deadlock the UI thread for a guaranteed 10s stall
`Host/Program.cs:313-318` (`HandleLifetimeExit`) — `Task.WhenAny(...).GetAwaiter().GetResult()` runs
on the Avalonia UI thread. Any `await` in the disposal chain missing `ConfigureAwait(false)` posts
its continuation back to that blocked thread → deadlock until the 10s `Task.Delay` wins, leaving
audio device/CAT port undisposed and triggering the incomplete-teardown restart path.
**Fix:** `var disposeTask = Task.Run(() => host.DisposeAsync().AsTask());` so continuations resolve
off the UI thread.
**Investigate first (cheap):** audit `ConfigureAwait` usage in the async-disposable singletons
actually in the graph (`MiniAudioEngine`, `SstvSessionService`, `RadioController`).

### T0-6. `RxDiskLineStagingBuffer`: 8 bare `catch {}` blocks swallow I/O failures silently
`Core.Sstv/RxDiskLineStagingBuffer.cs:191,201,212,405,524,551,566,612,680`. A full temp disk, a
read-only `TMPDIR`, or mid-write ENOSPC silently degrades RX Extended buffering to "capture stopped"
with **no log line anywhere** in this class except one dispose-path log. This does I/O
(create/write/read/delete temp files) and is exactly the code CLAUDE.md's logging rule targets.
Compounding: `AnalogFmSstvDecoder.cs:6606` discards `TryAppendLine`'s return value —
`IRxLineStagingBuffer.HasWriteFailed` exists and latches but nothing surfaces it to the operator.
**Fix:** add `[LoggerMessage]` entries for each swallowed failure path (keep the non-throwing
contract, stop the silence). Expose `HasWriteFailed`/`RxBufferDegraded` on `ISstvDecoder`, forward
through `RestartableSstvDecoder`, log once on the latching transition.
**Investigate first (light):** decide UI-status-indicator vs. log-only for the surfaced flag.

### T0-7. Decoder swap can hold a lock for up to 10s during disposal, freezing every UI property read
`Core.Sstv/RestartableSstvDecoder.cs:1566,655` — `outgoing.Dispose()` runs *inside* `Swap()`'s
`lock (_gate)`, and that disposal chains into `RxDiskLineStagingBuffer.Dispose`'s two 5s
`WaitForConsumer` calls. Every UI-facing property (`SignalPeakLevel`, `SlantPpm`, `SyncSource`, etc.
— `RestartableSstvDecoder.cs:1405-1429`) blocks on the same lock. Previously bounded by a 12-hour
maintenance interval; a recent change made swaps user-triggerable (Options Save queues one on next
idle push), so this is now click-rate-bounded, not maintenance-interval-bounded.
**Fix:** `Swap` returns the outgoing instance instead of disposing it; caller disposes it in the
existing post-lock section (`PushSamplesCore`, `:719-753`) after exiting the lock. Small, local.

### T0-8. QRZ account password stored plaintext with zero mitigating control
`Core.Logbook/QrzLookupSettings.cs:25` (password) and `QrzUploadSettings.cs:17` (API key) persist
into `settings.json` unencrypted. Confirmed: repo-wide grep for `ProtectedData`/`UnixFileMode`/
`File.SetUnixFileMode` returns zero matches — the settings file also carries default (world-readable)
permissions. Only mitigation today is a UI hint suggesting a throwaway password.
**Fix, in order:** (a) `File.SetUnixFileMode`/ACL to restrict the settings file at write time —
cheap, do first; (b) redact both fields from any settings export/preset path — **check
`Settings/ConfigurationPresetStore.cs` first**, since if presets serialize this section and users
share presets, this becomes an exfiltration path, not just a local-file gap; (c) larger: an
`ICredentialStore` abstraction (libsecret/DPAPI/Keychain) with plaintext JSON as a one-time
migration source — post-ship.

### T0-9. Logbook SQLite: no indexes beyond primary keys, plus an O(files×rows) reconcile scan
`Core.Logbook/SqliteLogbookRepository.cs:239-259`, `SqliteReceiveHistoryStore.cs:555-569` — only
`Id TEXT PRIMARY KEY`. Every logbook/gallery view query (`SearchAsync`, `QueryAsync`, sorted by
`StartUtc`/`ReceivedAt` DESC) is a full scan + sort. `ReconcileWithDiskAsync:456-459` runs a
`WHERE NOT EXISTS` subquery **inside a per-file loop** — a full table scan per candidate file.
**Fix:** add to both `EnsureSchema` methods (idempotent): indexes on `StartUtc`, `Callsign COLLATE
NOCASE`, `ReceivedAt`, `FilePath`, `LinkedQsoId`. Highest value/effort ratio found in the entire
review — zero data risk, cheap. Consider a UNIQUE constraint on `FilePath` (currently worked around
with `WHERE NOT EXISTS`) — **investigate first**: confirm no legitimate duplicate-`FilePath` rows
exist in shipped databases before adding UNIQUE.

### T0-10. RX image decode allocates ~512 Large-Object-Heap buffers per received image
`Core.Imaging/ReceivedImageBuffer.cs:179,230-239` and `Core.Logbook/ReceiveHistoryRecorder.cs:289,
474-483` — both allocate a fresh `Rgb24[width*height]` (~246 KB, over the 85 KB LOH threshold) on
**every** `LineDecoded` event, in 2 independent subscribers. A 320×256 image over ~256 line events
is on the order of 512 LOH allocations on the capture drain thread — real Gen2 pressure/fragmentation
on the RX hot path, every reception.
**Fix:** double-buffer (two preallocated arrays swapped under the existing lock) or
`ArrayPool<Rgb24>.Shared`.
**Investigate first (genuinely needed):** ownership handoff is the hard part —
`ReceivedImageBuffer.Current` hands the buffer to arbitrary readers, and
`ReceiveHistoryRecorder.cs:322` captures it into a fire-and-forget `Task.Run` closure for saving. A
naive pool return can hand a recycled buffer to a live UI reader or a mid-flight PNG encode. Design
the handoff before writing code.

### T0-11. UI: no `WriteableBitmap`/`Bitmap` in the UI layer is ever disposed
`UI/Imaging/ImageSourceBitmapConverter.cs:15` returns a fresh `WriteableBitmap` on every call, ~12
call sites, every VM-held bitmap replaced by assignment and abandoned — e.g.
`RxImagePaneViewModel.cs:1844` on **every coalesced decode update** for the whole reception;
`TxImageEditorPaneViewModel.cs` ×3 sites; `RxHistoryPaneViewModel.cs:729` (N thumbnails per refresh).
Each `WriteableBitmap` holds a native Skia buffer behind a ~100-byte managed object with no GC
pressure signal — classic Avalonia native-memory leak; a long RX session or editor slider-drag can
accumulate hundreds of MB of RSS before a Gen2 collection reclaims it.
**Fix:** for the 2 hot paths (RX live frame, TX editor preview), keep one `WriteableBitmap` per
`(width,height)` and blit via `Lock()` instead of reallocating; for cold paths (thumbnails, viewer,
rack slots) dispose the previous instance on reassignment/teardown.
**Investigate first:** Avalonia's `Image` control won't re-render on an unchanged source reference,
so in-place blitting needs a paired invalidate/double-buffer swap — verify with a real window, not
headless (per this project's own known headless-`Lock()` gotcha). Confirm no bitmap is shared
elsewhere (gallery/viewer) before disposing.

### T0-12. UI: TX editor recomputes the full image pipeline synchronously on the UI thread per pointer-move
`UI/ViewModels/TxImageEditorPaneViewModel.cs:3532-3553` — a crop drag runs the full
crop→resize→adjustments→template-composite (ImageSharp text rasterization) pipeline plus a full
`WriteableBitmap` allocation, **on every mouse-move event**. Same for each of 6 adjustment sliders.
**Fix:** apply the same latest-wins coalescing this codebase already does correctly in
`WaterfallPaneViewModel.cs:215-239` (one `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)`
in flight at a time).
**Investigate first (light):** measure `ApplyTemplate` cost with 3-4 text elements to decide if
coalescing alone suffices or the composite also needs to move off-thread.

### T0-13. `MainViewModel` has zero test coverage; its Ctrl+S dispatch has a documented prior bug
**Corrected 2026-08-30 by the test-suite audit below — the original grep methodology (`new
RxHistoryPaneViewModel(`, `new LogbookPaneViewModel(`) was stale.** Both of those ViewModels are
actually well covered — `RxHistoryPaneViewModel` (~65 tests) and `LogbookPaneViewModel` (~40 tests)
in `tests/ScanlineStudio.UI.Tests/PaneViewModelTests.cs`, constructed via private factory helpers
(`CreateRxHistoryPaneViewModel`/`CreateLogbookPaneViewModel`), which a `new(...)` grep misses. Real
gap is narrower than first stated: only `MainViewModel` (`src/ScanlineStudio.UI/ViewModels/
MainViewModel.cs`) is genuinely untested — specifically `SaveOrApply:348-370`'s Ctrl+S tab-dispatch
(whose own doc comment records a **previously shipped bug in exactly that method**),
`OnSelectedTabIndexChanged:80-86`'s reconcile trigger, and the `EditorOpened`/`EditorClosed` wiring
(`:150-151`).
**Fix:** add `MainViewModelTests.cs`, covering: Transmit tab + active editor → `editor.ApplyCommand`
runs, not `RxImage.SaveFrameCommand`; Transmit tab + no active editor → falls to save-frame (the
original bug's fallthrough); either branch with `CanExecute == false` → neither command fires;
Gallery tab selected → `RxHistory.ReconcileDiskThenRefreshAsync` runs exactly once, not twice on a
repeat selection; editor open/close → `ActiveEditor` set/cleared. All 15 constructor args are
injectable with existing fakes.
**Note:** `MainViewModel`'s constructor fires 3 fire-and-forget tasks — tests need the same
awaitable-seam treatment `RxImagePaneViewModel._loadQuickModeGridTask` already uses, or gate
`FakeSettingsStore.Gate` for deterministic construction.

---

## Tier 1 — high priority, fix soon after Tier 0

| # | Subsystem | Finding | File(s) |
|---|---|---|---|
| T1-1 | Core.Sstv | Both hot-path FIR filters do a full per-sample array shift instead of a circular buffer — roughly doubles cost of the 2 hottest DSP routines. Golden-vector-gated; **do not** vectorize the dot product as part of this fix (breaks bit-parity) | `SearchBandpassFilter.cs:188`, `HilbertFmDemodulator.cs:275` |
| T1-2 | Core.Sstv | `WaterfallSource.Frames` is a bare `Subject<T>` called synchronously from the audio drain thread — no stated scheduler/slow-subscriber policy, exactly the regression CLAUDE.md's concurrency rule targets | `WaterfallSource.cs:23,55,133` |
| T1-3 | Core.Sstv | `IAsyncEnumerable<float>` yields one sample at a time in the TX encoder — ~12.8M awaits for a PD290 transmission, no I/O involved | `AnalogFmSstvEncoder.cs:190,317,347` |
| T1-4 | Core.Sstv | One `[LoggerMessage]` in 20,100 lines — a dozen user commands (ForceMode, RequestReSync, SetModeLock, etc.) and decoder-restart/reconfiguration-rejected events are unlogged | `AnalogFmSstvDecoder.cs` command handlers, `RestartableSstvDecoder.Swap:1464` |
| T1-5 | Application | `SstvSessionService.PlayWithPttAsync` is an 844-line method with 12 fields tracking PTT physical state; two residual races are self-documented as narrowed-not-closed. Extract a `PttSafetyCoordinator`; needs its own plan-review round, driven by the existing `SstvSessionServicePttSafetyTests` suite | `SstvSessionService.cs:3254-4098` |
| T1-6 | Application | Unverified sync-over-async on the audio drain thread — comment retracts its own prior safety claim, self-flagged as an open unknown on a safety path | `SstvSessionService.cs:1919` |
| T1-7 | Application | Raw English exception text is the UI's error surface in several places (violates no-hardcoded-strings rule); `LogQsoAsync` discards the post-persist failure reason; an unguarded event-raise after a successful settings write can report "save failed" on a save that succeeded | `RadioSessionService.cs:81,104,179,209,292`; `LogbookSessionService.cs:99` |
| T1-8 | Radio/CAT | Lifecycle calls (`Connect`/`Disconnect`/`Dispose`) are documented as caller-serialized but no caller actually serializes across each other (preset switch vs. Options Save). Needs a paper decision on inline-subscriber re-entrancy before adding a lock | `RadioController.cs:130-267`; callers in `ConfigurationPresetService.cs:378-382`, `OptionsWindowViewModel.cs:992,1032` |
| T1-9 | Radio/CAT | Give-up threshold (5 attempts, ~7.5s) permanently drops the session on a cable pull with no auto-recovery — product decision, not a constant tweak | `RadioController.cs:36,367-407` |
| T1-10 | Radio/CAT | "Rig unplugged" is a permanent give-up on Hamlib/rigctld but an infinite 250ms-cadence poll forever on flrig/OmniRig (different exception classification) — decide intended semantics, make uniform | `FlrigClientProtocol.cs:74`, `OmniRigRadioProtocol.cs:65` vs. `RadioController.cs:322-407` |
| T1-11 | Cross-subsystem | "Test Connection" UI calls omit a cancellation token/timeout on 4 sites, unlike the sibling PTT-test calls — combined with T1-8/Hamlib timeout, can leave "Testing…" stuck indefinitely | `OptionsWindowViewModel.cs:1241,1326,1391,1539` |
| T1-12 | UI | `MainWindow`'s cross-VM event wiring lives entirely inside `DataContextChanged` using `+=` with no re-entry guard — a second firing double-subscribes (2 Options windows, 2 viewer windows). Latent today (DataContext only assigned once), one-line guard | `Views/MainWindow.axaml.cs:147-780` |
| T1-13 | UI | Frequency/SWR/ALC/PWR/duration values composed via raw string interpolation in ViewModels/converters, bypassing `ILocalizationService` — decimal separator and unit literals break on non-en-US culture | `RadioStatusViewModel.cs:535,536,562,578,583,588,1331`; `RxImagePaneViewModel.cs:469,470,596,598`; 3 more sites |
| T1-14 | Audio/Imaging/Logbook | Every imaging operation (crop/resize/adjust/overlay/template/rotate) pays a full convert-in/convert-out; `RecomputePreview` fires per pointer-move — ~8 full-image conversions per preview frame, compounds with T0-12 | `TransmitImagePreparer.cs:1026-1062` and 7 call sites |
| T1-15 | Audio/Imaging/Logbook | Same pixel-conversion loop duplicated in 7 places; already caused a real bug (`StockImageLibrary` missing an `AutoOrient` call `ImageFileLoader` had) | `ImageFileLoader.cs:35-54`, `StockImageLibrary.cs:94-113`, 5 more sites |
| T1-16 | Audio/Imaging/Logbook | Timestamps stored as local-offset strings, compared lexicographically — diverges from chronological order across DST/timezone changes. Needs a migration decision before coding | `ReceiveHistoryRecorder.cs:373,428`; `SqliteReceiveHistoryStore.cs:419-422,142,462` |
| T1-17 | Audio/Imaging/Logbook | `AdifImporter` assumes UTF-8 regardless of source file encoding; unguarded date-slice parsing throws the wrong exception type and aborts mid-import, discarding already-mapped records | `AdifImporter.cs:40,298-307` |
| T1-18 | Infra | `CodePagesEncodingProvider` (mandatory per CLAUDE.md §4 for CP932 legacy files) is registered nowhere in the repo — latent until legacy `.ini`/`.mtm` import is attempted | repo-wide, none found |

**Corrections (2026-08-31, Tier-1 roadmap planning — see `PROJECT_BRIEF.md` for the ranked plan):**
- **T1-2:** the "no stated scheduler/slow-subscriber policy" premise is stale.
  `IWaterfallSource.cs`'s own doc comment already states the policy explicitly (added in a commit
  predating this audit), and the one real production subscriber (`WaterfallPaneViewModel.OnFrame`)
  already coalesces safely with O(1) drain-thread work. Remaining gap is narrower: no regression
  test exists gating a slow/throwing future subscriber (`WaterfallSourceTests.cs` has none, unlike
  `DecoderSubscriberFailureTests.cs`'s equivalent coverage for the decoder).
- **T1-6:** `SstvSessionService.cs` contains two comments that disagree — a Round-15 retraction at
  the call site (`:1899-1911`) flags the sync-over-async safety claim as unverified, but a later
  Round-17 comment elsewhere in the same file (`:2172-2187`) appears to independently trace and
  confirm the exact `MiniAudioEngine` mechanism that would resolve it, and was never used to update
  the Round-15 text. Reconciling those two comments is this item's own necessary first step, not a
  separate finding.
- **T1-17:** the "unguarded date-slice parsing throws the wrong exception type" half is already
  fixed — see `TT0-7` below, marked `DONE`; `AdifImporter.cs`'s date parsing now uses guarded
  `TryParseExact` throwing `FormatException`. Only the source-file-encoding half (`LogbookSessionService.cs:135`'s
  `StreamReader` with no declared encoding) is still open.

**Tier B closed (2026-08-31):** T1-7, T1-13, T1-15, T1-17 implemented and committed. Auditor
code-review round found two real must-fix issues before commit, both fixed and re-verified:
- **T1-17 regression:** a UTF-16 (BOM'd) source file imported silently as zero records — the
  encoding threaded from `StreamReader.CurrentEncoding` into `AdifImporter.Import`'s byte-count
  slicing is only safe for UTF-8 or a single-byte code page (the class's own doc comment already
  said so); a multi-byte encoding broke the ASCII eoh/eor scan with no exception. Fixed by rejecting
  an unsafe caller-supplied encoding inside `Import` itself (falls back to UTF-8), plus a new
  UTF-16-BOM regression test.
- **T1-7(b) misattribution:** the post-persist failure reason was written into `LogQsoResult.QrzError`,
  which the UI renders through a QRZ-specific "QRZ: failed (...)" string — misattributing e.g. a
  settings.json permissions error to QRZ, even with QRZ upload disabled. Fixed with a new, distinct
  `LogQsoResult.PostPersistError` field and a neutral locale key.

T1-8/T1-9/T1-10 (Tier C) still need one user decision each before coding. T1-1/T1-2/T1-3/T1-5/T1-6/
T1-14/T1-16 (Tier D) still need their own plan-review round.

**Update (2026-08-31):** Tier C is now closed (see below) and T1-2 (Tier D) is done — see the
"Tier D progress" note further below. T1-1/T1-3/T1-5/T1-6/T1-14/T1-16 still each need their own
plan-review round.

**Tier C closed (2026-08-31):** all 3 decided and implemented.
- **T1-9:** kept manual-only recovery after give-up (user's own decision, no code change beyond a
  doc comment confirming the design is deliberate).
- **T1-10:** flrig/OmniRig's "no rig attached" check now throws `IOException` instead of
  `RadioProtocolException`, matching `HamlibRadioProtocol`'s own convention — reaches
  `RadioController`'s transport-level backoff/give-up path instead of looping on `CommandFailed`
  forever.
- **T1-8:** `RadioController.ConnectAsync`/`DisconnectAsync`/`DisposeAsync` now serialize against
  each other via an internal `SemaphoreSlim`, closing the documented-but-unenforced contract.
  CLAUDE.md §7's full 2-round plan-review + code-review cadence (concurrency work) ran in full: round
  1 of plan-review found a genuine self-deadlock hazard (`ConnectAsync`/`DisposeAsync` both call
  `DisconnectAsync` internally — fixed via a private `DisconnectLockedAsync` split); round 2 found a
  disposed-flag TOCTOU (fixed via an atomic `Interlocked.Exchange` claim) and a new uncaught-exception
  path on the Options dialog's Disconnect button (fixed with a try/catch, matching the existing
  Connect-branch pattern). The code-review round found one more real gap — a second unguarded
  `_stateChanges.OnNext(null)` publish in the poll loop's give-up path, made materially more
  reachable by T1-10's own change — fixed the same way as the first instance, plus a new regression
  test. Applying that fix's own comment update briefly dropped the actual publish call; caught by a
  now-failing test (`PollLoop_PublishesStateChanges_AndUpdatesLastKnownState`), fixed immediately.

**Tier D progress — T1-2 closed (2026-08-31):** re-scoped per the correction above (user confirmed
"tests only" as the starting scope), but verifying the "no state corruption" half of that scope
empirically (a throwaway test, deleted after confirming) found a real bug: `WaterfallSource`'s
internal accumulator never slid forward after a throwing `Frames` subscriber, since the old
`EmitFrame()` called `_frames.OnNext` BEFORE the slide — the next `PushSamples` call then
re-emitted a stale duplicate frame. User approved fixing it in the same pass. Fixed by splitting
`EmitFrame()` into a pure `BuildFrame()` (compute only) and reordering `PushSamples` to slide the
accumulator before publishing. One auditor code-review round, go — confirmed the fix is
bit-identical output (pure reordering), closes the gap including the multi-window-per-call case,
and also caught a separate pre-existing doc-comment overstatement (`IWaterfallSource.cs` claimed
`SstvSessionService` "wraps every subscriber call" in try/catch; it actually wraps its own whole
`Waterfall.PushSamples` call, no per-subscriber isolation) — corrected. Full `Core.Sstv.Tests`
suite (1524 tests) green.

---

## Tier 2 — medium priority, batch with adjacent work

- **Core.Sstv:** per-line array/delegate allocations in scanline decoders (`YCbCrSequentialScanlineDecoder.cs:16-18`, `YCbCrLinePairedScanlineDecoder.cs:18-21`); `PixelSampleReader` delegate indirection on the hottest read path; `WaterfallSource.BuildFrame` allocates 2 scratch arrays/frame; event fan-out allocates twice per raise (`AnalogFmSstvDecoder.cs:2211`, `RestartableSstvDecoder.cs:763,1730`); `SstvModeRegistry` uses 43-branch if-chains instead of tables (`:958-1004`); 4 near-identical scanline decoders share copy-pasted index-walk math (extract *only* the walker, not the channel logic); channel dispatch by magic string instead of enum; `AnalogFmSstvDecoder.cs` is 7,945 lines at ~62% review-history comments — extract the narrative to `docs/`, keep only the invariant statements inline (safe, additive, do this one); `WaterfallSource` has an unsynchronized `_accumulatedCount` cross-thread read/write and no `_disposed` guard on the audio thread.
- **Application:** `ConfigurationPresetService` has 5 near-identical try/catch push blocks (collapses via Tier-0 `UpdateAsync` work); `OptionsSettingsService._loadedSettings` is now dead state; `OptionsSnapshot` mapping triplicated across `Defaults`/`LoadAsync`/`SaveAsync`; `TemplateStore.SaveAsync`/`ExportAdifFileAsync` write non-atomically (temp-file+rename fix, cheap); `ImportAdifFileAsync` does N individual transactions with no partial-failure reporting; fire-and-forget `Task.Run` work (audio auto-save encode, RxAudioAutoSaver completion) isn't tracked or drained on `DisposeAsync`.
- **Radio/CAT:** 4 near-identical `AcquireAsync`/timeout-wrapper/lazy-connect implementations across the 4 backend projects — extract a shared base, but preserve 2 real asymmetries (unbounded vs. bounded wait, Rigctld's deliberate no-lock dispose); Hamlib does one `Task.Run` per native call (up to 6 pool hops per 250ms poll) — wrap the whole method body once instead; OmniRig has zero logging anywhere (the one backend that can't be tested locally, so diagnosability matters most); `RigctldClientProtocol.ReadLineAsync` has no max line length (unbounded growth against a mis-pointed host); Hamlib connect-cleanup can leak a handle if `RigCleanup` throws before `_rig` is cleared (2-line swap).
- **UI:** `TxControlsPaneViewModel.Dispose()` doesn't dispose its CTS or unsubscribe editor event handlers; `UpdateFilteredEntries` does `Clear()`+N`Add()` per keystroke with no debounce; `TranslateBindingSource` only prunes dead handlers on a culture change (O(n²) growth over a long session with no language switch) — investigate actual growth before fixing; `TxImageEditorPaneViewModel` is a 4,140-line god object (extract `EditorUndoStack`/`TemplateVariableScanner` as separate testable collaborators, sequence after T0-11/T0-12).
- **Audio/Imaging/Logbook:** `SqliteCommand` never disposed (~17 sites, mechanical fix); no WAL mode/busy-timeout on the shared `history.db` (2 stores, documented as anticipating cross-process contention) — verify Microsoft.Data.Sqlite's actual default busy-retry before deciding this is needed; unbounded logbook/gallery result sets (add paging); QRZ HTTP calls don't check status code before XML-parsing (bad error message on a 5xx); font loading in `TransmitImagePreparer`'s DI-singleton constructor does 8 blocking file reads with no logger/fallback for optional fonts.
- **Cross-subsystem:** "exactly one factory match" reimplemented 3 times with drifted error-message wording (`RadioController.cs:287-301`, `RadioSessionService.cs:55-63,125-133`) — extract one resolver; `RxImagePaneViewModel`'s 250ms UI-thread poll reaches into the audio engine for overrun count, which the interface itself says can block on a concurrent capture-session dispose — snapshot on the drain thread instead of polling across the boundary.
- **Infra:** `IHost` is built but never started (no `IHostedService` registered today — harmless now, a trap for later); `ConfigurationPresetStore` isn't hardened against corrupt/locked files unlike its sibling `JsonSettingsStore`; atomic writes use a fixed `.tmp` name (two concurrent processes interleave) and skip an explicit flush-to-disk before rename; `FileLoggerProvider` does a `BaseStream.Length` syscall per log line under a global lock at the default `Debug` level, and a single transient write failure permanently and silently disables file logging for the rest of the session.

---

## Tier 3 — low priority / nits (batch opportunistically, no urgency)

Grouped by subsystem; see each audit's own report section for exact file:line if reviving this
tier. Non-exhaustive here by design — these were the "safe to defer indefinitely" items each audit
flagged, not omissions.

- **Core.Sstv:** exception-as-control-flow in a test-only path; non-volatile `_disposed` fields; `SemaphoreSlim` used as an ever-growing signal instead of a real condition variable; `PixelSampleReader.ReadBare`'s ignored second parameter; two inconsistent array-pool-return code paths.
- **Application:** mixed error-reporting channel in `RequestSampleRateAsync` (result enum + exception for one case); `TemplateStore.DeleteAsync` does sync I/O behind a `Task` façade; one undisposed `Process.GetCurrentProcess()` handle; a closure-allocating `SafeLog` helper on cold paths only.
- **Radio/CAT:** empty `Core.Radio.Cat` project (delete or document); `HamlibVersionGate` int-parse without invariant culture; flrig response-encoding not explicitly stated; asymmetric per-factory `Create` logging; doc/behavior mismatch on whether a throwing `RadioConnectionEvents` subscriber is surfaced as an event (it isn't, only logged — fix the doc, not the code).
- **UI:** `MainWindow.axaml.cs` has ~30 repeated `if (logger is not null)` guards resolvable once; 5 near-identical Browse-directory command pairs in `OptionsWindowViewModel`; 130+ inline ViewModel constructions in `PaneViewModelTests.cs` (extract a builder); `RadioStatusViewModel` is `new`'d directly instead of DI-registered like every other pane VM; `ReadyRackViewModel.TryLoadThumbnail` swallows exceptions with no log line.
- **Audio/Imaging/Logbook:** `WavFile` does per-sample virtual calls in both read and write directions (bulk-buffer fix, low risk); duplicate WAV `fmt `/`data` chunks silently override with no error; a test double (`FakeAudioEngine`) ships in the production assembly instead of a test project; undisposed `HttpResponseMessage`/`FormUrlEncodedContent` in the QRZ client (not a live leak under `IHttpClientFactory`, just non-idiomatic); unbounded raw-response-body logging on a QRZ upload failure.
- **Infra:** `ILogFileRelocator` registered twice (last-wins, harmless); a stale doc comment claims `ScanlineStudio.Application` "has no real source files today"; `ScanlineStudio.Settings.csproj` has an unused `ProjectReference` to `Abstractions` contradicting its own documented layering; `ConfigurationPresetStore`'s filename validation allows Windows-reserved device names.
- **Cross-subsystem:** `ISstvDecoderReconfiguration` lives in `Core.Sstv` while its 2 siblings live in `Abstractions` (asymmetric, causes an otherwise-unnecessary downcast); an empty `Core.Radio.Cat` project is referenced by `Application` for no current reason; one unresolvable `<see cref>` in a doc comment.

---

## Explicitly do-not-touch (called out by name in the audits)

Each subsystem audit flagged specific things NOT to change, and why — preserve this guidance rather
than "finishing the job" on adjacent code:

- **Core.Sstv:** do not vectorize the FIR dot-product when fixing T1-1 (reassociates floating-point
  summation, breaks golden-vector bit-parity). Do not attempt a full decomposition of
  `AnalogFmSstvDecoder` — it's a faithful port of a legacy god-class and the state coupling is the
  point; only the sample-cache substrate (`Rel()`, the 9 forward-fill caches, `TrimBuffers`) is a
  safe, bounded extraction, and only after its own plan-review round. Do not unify the 4 scanline
  decoders' channel switches — the per-family asymmetries are real, separately-verified legacy
  behavior. Do not touch the 9-list `RemoveRange`-based trim-watermark logic (A8 in the Core.Sstv
  report) — working correctly, most heavily-reviewed code in the file, small payoff.
- **Radio/CAT:** don't dispatch another review round on the B/C/G-tier items (dedup/optimization/nits)
  — refactoring value, not risk. Don't assume a combined rigctld or flrig RPC exists without
  verifying against a local clone first (flrig has none in this tree).
- **Application:** the `PlayWithPttAsync` extraction (T1-5) must not be done opportunistically — it
  needs its own plan-review round and the existing `SstvSessionServicePttSafetyTests` suite as the
  regression gate, not a drive-by refactor alongside something else.

---

## Suggested sequencing

1. **T0-4** (DI validation) and **T0-9** (SQLite indexes) first — cheapest, zero-risk, immediate
   payoff, no design decisions needed.
2. **T0-1** (Hamlib PTT timeout) and **T0-2** (`ISettingsStore.UpdateAsync`) — the two
   cross-confirmed findings; T0-1 is a safety issue, T0-2 unblocks the largest single dedup win in
   the review.
3. **T0-3, T0-5, T0-7** together — all are "bound an unbounded wait/lock" fixes in the Host/decoder
   startup-shutdown path, same shape, can share a review pass.
4. **T0-6** (RX staging logging) — pure addition, no behavior risk.
5. **T0-8** (QRZ password) — check `ConfigurationPresetStore` export behavior first, then the
   file-permission fix same day.
6. **T0-10, T0-11, T0-12** — each needs its own short design decision (ownership handoff / bitmap
   reuse strategy / coalescing granularity) before coding; don't start any as a drive-by.
7. **T0-13** — one focused test-writing session (`MainViewModelTests.cs`), can run in parallel with
   anything above.
8. Tier 1, roughly in the table's order, batched by subsystem as capacity allows.

---

# Test-suite audit (2026-08-30)

Second pass: 6 parallel `auditor` reviews of `tests/` (~82,000 lines, matching the source-audit's
subsystem split), from the same senior-engineer lens applied specifically to the tests — does each
test check the outcome a user would actually experience, are expected values independently correct
or tautological, what's missing, what's brittle/over-mocked, what's flaky. Each pass cross-referenced
the source findings above and reported whether existing tests would actually catch each one.

Per-suite verdict, from each audit's own closing call:

| Test project | Verdict |
|---|---|
| `Core.Sstv.Tests` | No — flagship TX-validation test executes zero production code |
| `UI.Tests` | No, narrowly — but far stronger than assumed (see T0-13 correction above) |
| `Application.Tests` | No — but only 2 gaps, both attach directly to the T0-1/T0-2 source fixes |
| `Core.Radio.Tests` | Conditional — rigctld/flrig/RadioController: go. Hamlib: go only with T0-1's test landed. OmniRig: not evidenced, ship labeled experimental |
| Audio/Imaging/Logbook tests | No — but only 2 items block, both fail against real bugs today |
| Host/Settings/Localization tests | No — 6 findings ship green today, one is a live un-tested defect |

**The strongest cross-cutting signal**: in 5 of 6 passes, the audit didn't find "thin coverage" in
the abstract — it found the *specific* reason a *specific* Tier-0 source bug survived:

- The settings-race fix (T0-2)'s own test fake (`FakeSettingsStore`) **cannot express the race** —
  it parks on the wrong side of the load/save window. That's not an oversight to fix later; it's why
  T0-2 shipped unnoticed.
- The Hamlib PTT-hang fix (T0-1) is tested only against *throwing* failures, never a *hang* — which
  is T0-1's actual failure mode.
- The timezone bug (T1-16) survived because every timestamp fixture uses UTC only — and the one test
  that touches the issue **asserts the broken lexicographic ordering as correct**.
- The ADIF encoding bug (T1-17) survived because every fixture is an in-memory ASCII `StringReader` —
  the file-decode path is structurally unreachable from any existing test.
- The DI-validation gap (T0-4) and shutdown-deadlock gap (T0-5) survive because the relevant tests
  run on a bare thread-pool thread with no `SynchronizationContext` — the exact condition that makes
  T0-5 dangerous cannot occur inside the test harness as written.

---

**Implementation status (2026-08-30): `docs/plans/test-suite-fixes-phase1-plan.md` closed 10 of the
17 items below** (TT0-3 through TT0-7, TT1-1, TT1-3, TT1-8, TT1-17) — test-only in scope, 3
plan-review rounds + 1 code-review round, both GO, every touched project's suite green, uncommitted
in the working tree. Each closed item is marked `DONE` below. **Still open**: TT0-1 and TT0-2 (must
pair with the T0-2/T0-1 *source* fixes, not test-only work — see each item's own note); TT1-13 (item
9, deliberately deferred to its own future plan — concurrency work with unresolved design questions,
see `docs/plans/test-suite-fixes-phase1-plan.md`'s own item 9 section for why). Everything else in
Tier 2/3 below is still open, untouched.

## Test-suite Tier 0 — blockers (ship gates, either standalone or as a fix's regression test)

### TT0-1. `FakeSettingsStore` cannot express the settings read-modify-write race (T0-2's blind spot)
`Application.Tests/FakeSettingsStore.cs:26-59` — `Gate` parks *before* `LoadAsync` returns, on the
wrong side of the race window; `SaveAsync` has no hook at all. No test in `Application.Tests`,
`Settings.Tests`, or `UI.Tests` exercises two callers racing a load-mutate-save. Don't mistake
`ConfigurationPresetServiceTests.cs:562` for coverage — that's a single-flight guard within one
service, not protection against a concurrent writer in a different service (the actual T0-2
scenario: `RadioStatusViewModel`'s debounced TX-volume persist vs. Options Save).
**Fix:** add `SaveGate`/`OnAfterLoad` hooks to `FakeSettingsStore`; write one parameterized test —
caller A loads and parks, caller B completes a full load-mutate-save on a different section, A
resumes and saves, assert B's section survived. **Write this as part of the `ISettingsStore.UpdateAsync`
fix (T0-2), not separately** — it should fail today and pass once T0-2 lands, making it the
regression gate for all ~25 migrated call sites.

### TT0-2. The Hamlib PTT un-key retry is tested only against throws, never a hang (T0-1's blind spot)
`Application.Tests/RadioSessionServiceTests.cs:298,321,346` script failures via
`SetPttExceptionsToThrow`; `FakeRadioProtocolFactory.cs:62`'s `SetPttAsync` has no gate/hang hook
(only `PollAsync` does, via `PollGate`). T0-1's actual failure mode — attempt 1 blocks forever in
Hamlib's unbounded lock, attempts 2-3 never run — cannot be expressed by any existing test.
**Fix:** port the already-proven `HangOnCallNumber`/`Gate`/`GateOnCallNumber` hooks from
`FakeRadioSessionService.cs:106-165` onto `FakeRadioProtocol.SetPttAsync`; add a test hanging un-key
attempt 1 and asserting attempts 2/3 still run within a bound. **Land with the T0-1 Hamlib timeout
fix**, not before (it will hang if written first).

### TT0-3. `DONE`. Core.Radio: real-interop tests silently pass with zero assertions when their native dependency is absent
`Core.Radio.Tests/HamlibDummyRigIntegrationTests.cs`, `RigctldDummyRigIntegrationTests.cs`,
`HamlibNativeTests.cs`, `OmniRigProtocolFactoryTests.cs:37-40` — all use `if (!available) return;`.
xUnit reports these **passed**, not skipped; CI installs neither `libhamlib` nor `rigctld` on any
matrix leg, so **9 tests report green having asserted nothing, on every CI run.** (Acknowledged in
`coverage-thresholds.json:3`, but acknowledgement doesn't make a CI image that loses `libhamlib`
detectable.) `OmniRigProtocolFactoryTests.cs:37-40` inverts the problem — it no-ops on *Windows*, the
only OS where that code is reachable.
**Fix:** copy the existing in-repo pattern — `Core.Audio.MiniAudio.Tests/RequiresPipeWireFactAttribute.cs`
(a `FactAttribute` subclass that sets `Skip` from a process-wide availability probe). Add
`RequiresHamlibFact`/`RequiresRigctldFact`; convert the early-returns. Then add an env var
(`SCANLINE_REQUIRE_NATIVE_CAT=1`) that turns a missing dependency into a hard failure, set on one CI
leg with `libhamlib`/`rigctld` actually installed — so losing the dependency is a red build, not a
silently-absorbed coverage drop. Cheap (an attribute class + a CI step), highest leverage item in the
whole Core.Radio.Tests pass.

### TT0-4. `DONE`. `OmniRigComClient`'s CLSID/IID/`[DispId]` values have zero test coverage of any kind
`Core.Radio.Tests` has no test for `src/ScanlineStudio.Core.Radio.OmniRig/OmniRigComClient.cs`. The
audit independently re-transcribed and verified all 11 GUID/DISPID literals against
`yoniq-old/YONIQ-main/OmniRig_TLB.h`/`.cpp` — **all correct today** — but nothing guards against
future drift, and `FakeOmniRigComClient` structurally can't catch a marshaling bug by design.
**Fix:** a `OmniRigComClientAttributeTests` file using reflection (`InternalsVisibleTo` +
`BindingFlags.NonPublic`) to read `GuidAttribute`/`InterfaceTypeAttribute`/`DispIdAttribute` off the
`[ComImport]` interfaces and assert them against the 11 literals, each with a `// OmniRig_TLB.h:<line>`
citation. Pure attribute-metadata reflection — **no COM activation, runs on Linux CI**, ~40 lines,
highest value-per-line item found across all 6 test audits.

### TT0-5. `DONE`. `ConfigurationPresetStore`'s unhardened corrupt-file path has no masking test — it's a live, untested defect
`src/ScanlineStudio.Settings/ConfigurationPresetStore.cs:257-276` (`ReadPresetFileAsync`) has no
try/catch — confirmed by both the source audit (T0's Tier-2 list) and this test pass independently.
`Settings.Tests/ConfigurationPresetStoreTests.cs:328` ("HandEditedFile") writes *valid* JSON, so it
never exercises the gap. A hand-edited/corrupt preset — the exact kind of file users are most likely
to share — throws straight out of an interactive menu click today.
**Fix:** add the failing test first (corrupt preset file → `LoadPresetAsync` returns null/defaults
and logs), then mirror `JsonSettingsStore.LoadAsync`'s catch filter. Do the same for
`JsonSettingsStoreTests.cs` — its own corrupt-file/permission-denied hardening (which fixed a
documented "single bad byte bricked startup" bug) has **zero tests today**, so narrowing that catch
back down would ship silently green.

### TT0-6. `DONE`. DI-graph and shutdown-deadlock tests can't reach the conditions they claim to cover
Two related gaps in `Host.Tests`:
- **DI validation (T0-4):** `SstvCompositionRootTests.cs:56-59` resolves exactly 4 roots; nothing
  asserts container validity, and confirmed-uncovered registrations exist
  (`MacrosReferenceWindowViewModel`, `ConfigurationsManagerWindowViewModel`, `IApplicationRestarter`).
  **Fix:** add a test building with `ValidateOnBuild = true, ValidateScopes = true` (catches
  type-registered descriptors) **and** a separate enumerate-and-resolve loop over every registered
  descriptor (catches the ~10 factory-lambda registrations `ValidateOnBuild` structurally can't see —
  `JsonSettingsStore`, `ConfigurationPresetStore`, `ILocalizationService`, the Hamlib factory, etc.).
- **Shutdown deadlock (T0-5):** `HandleLifetimeExitTests.cs`'s 6 tests all call `HandleLifetimeExit`
  from a plain thread-pool thread with **no `SynchronizationContext`** — so the UI-thread-capture
  condition that makes T0-5 dangerous cannot occur in the test as written; they test the extracted
  ordering logic in isolation, not the hazard. **Fix:** install a single-threaded
  `SynchronizationContext` test double + a fake host whose `DisposeAsync` awaits a gate without
  `ConfigureAwait(false)`; assert completion rather than a timeout. Requires making the 10s dispose
  timeout an injectable parameter (small source change). **Land alongside the T0-5 fix.**

Also in scope, smaller but real: `SstvCompositionRootTests.cs` builds the *real*
`SqliteReceiveHistoryStore` against the developer's/CI runner's actual `history.db` (the `ISettingsStore`
substitution doesn't cover this constructor's fixed default path) — substitute `IReceiveHistoryStore`
with a fake/temp-path registration.

### TT0-7. `DONE`. ADIF import: no test exercises non-ASCII source encoding or a malformed date, and both fail against real bugs today
Confirms and sharpens T1-17. `Core.Logbook.Tests/AdifImporterTests.cs`: all 17 tests use an in-memory
`StringReader` over ASCII text — the production path (`LogbookSessionService.cs:135`,
`new StreamReader(filePath)`, default UTF-8) is structurally unreachable from any fixture in this
project. The importer's own headline invariant (byte-exact multi-byte field slicing) has zero tests;
the exporter has the equivalent test (`AdifExporterTests.cs:113-125`), the importer doesn't.
**Fix, both fail today, both are cheap:**
1. `Import_NonAsciiFieldValue_SlicesByUtf8ByteCountNotCharCount` — `<NAME:5>Jörg<CALL:6>N0CALL<EOR>`;
   assert `Name == "Jörg"` **and** `Callsign == "N0CALL"` (a char-count bug misaligns everything
   after the field, so the callsign assertion is the real detector).
2. `Import_Cp1252SourceFile_DecodesCorrectly` — write real CP1252 bytes to a temp file, read via the
   production path. Requires `CodePagesEncodingProvider` registration (T1-18) to even run.
3. A `[Theory]` over malformed dates (`"2026"`, `"1"`-length time, non-digit) asserting `FormatException`
   — `AdifImporter.cs:300-305`'s unguarded `AsSpan` slicing currently throws
   `ArgumentOutOfRangeException` instead, inconsistent with every other malformed-input path in the
   same class.
**Correction to the source audit's T1-17 wording:** the "discards already-mapped records" claim is
only true in-memory — `LogbookSessionService.cs:135-138` parses the whole file before persisting
anything, so a bad record aborts the import with **zero rows committed**, not a partial DB write.

---

## Test-suite Tier 1 — high priority

| # | Subsystem | Finding |
|---|---|---|
| TT1-1 `DONE` | Core.Sstv | The flagship TX-validation test (`GoldenVectorTests.cs:120-180`, `LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage`) reads two static BMPs and compares them — **invokes zero production code**, and its reference images are stale relative to the current encoder (regenerated `.mmv` set, not-regenerated `_RX.bmp` set). Rename to stop overclaiming + add a provenance-hash staleness check now (hours); the real close is refreshing the 11 reference BMPs against a real legacy install |
| TT1-2 | Core.Sstv | Golden-vector image comparison is average-delta-only across the whole frame — a single fully-corrupted scanline (960 samples at max delta) still passes every fixture's tolerance. Add worst-row and outlier-fraction metrics alongside the existing average |
| TT1-3 `DONE` | Core.Sstv | `NoiseRobustnessTests.cs:62-123` computes a noise-floor metric across a 10-level SNR sweep and **never asserts on it** — a DSP regression that halves usable SNR range is green. One-line fix: pin the measured current value |
| TT1-4 | Core.Sstv | Zero concurrency tests in 26,300 lines — the source audit's own concurrency findings (T0-7 decoder-swap lock, T1-2 waterfall `Subject`) will ship with no regression gate. Add throwing/slow-subscriber tests to `WaterfallSourceTests` (mirroring `DecoderSubscriberFailureTests`'s existing pattern) and a concurrent-reader-during-swap test to `RestartableSstvDecoderTests` |
| TT1-5 | Core.Sstv | Channel *order* (the literal Scottie-incident failure mode) is pinned by golden vectors for only 11 of 43 modes; the other 32 rest on duration-sum + self-round-trip, which CLAUDE.md itself says is insufficient. Add a per-mode segment-order table transcribed from legacy `Main.cpp` `Line*` functions — mechanical, ~43 entries, zero design risk, biggest single structural gap found |
| TT1-6 | UI | `MainWindow.axaml.cs`'s ~780 lines of cross-VM wiring (where T1-12's `DataContextChanged` re-entry guard lives) has zero behavioral coverage — 6 separate test files disclaim the same seam explicitly. Extract to a testable `WireOnce(...)` before testing; don't try to test it through a real headless `Window` |
| TT1-7 | UI | The no-hardcoded-strings guard (`NoHardcodedAxamlStringsTests.cs`) covers `.axaml` only, 3 attributes, and doesn't verify `{loc:Translate}`/`GetString` keys actually resolve against `en.json` — confirmed live `.cs` violations (T1-13) would not be caught. Add a `.cs`-literal rule and a key-existence check |
| TT1-8 `DONE` | UI | Two tests claiming to prove off-UI-thread event marshaling (`PaneViewModelTests.cs:109-129,1681-1703`) raise the event from the same headless UI thread the assertion runs on — **cannot fail** if the marshaling were deleted. Raise from `Task.Run` instead and assert `Dispatcher.UIThread.CheckAccess()` inside the handler |
| TT1-9 | Application | `OptionsSettingsService` (8 of the 16 Application-layer settings RMW sites) has zero tests in this project. Add a full round-trip test and a `Defaults()` vs. `LoadAsync(empty)` equivalence test — the latter catches the whole `OptionsSnapshot` triplication-drift class (Tier-2 source finding) in one assertion |
| TT1-10 | Application | `LogQsoAsync`'s post-persist-failure test (`LogbookSessionServiceTests.cs:72-91`) asserts the record survives but nothing about the failure being *reported* — currently ratifies the lossy behavior T1-7 flags as a bug |
| TT1-11 | Radio/CAT | Zero test coverage for lifecycle-call serialization (T1-8) in either direction — neither the undefined-behavior half (`Connect`/`Disconnect` racing) nor the claimed-safe half (`SetPttAsync` racing `Disconnect`). Add a characterization test (pin current behavior, not desired) so T1-8's eventual lock has a red/green signal instead of a paper argument |
| TT1-12 | Radio/CAT | No test is anchored to the real `hamlib/include/hamlib/rig.h` header — every P/Invoke constant is hand-typed, comment-verified only. Add a `HamlibNativeLayoutTests` with `Marshal.SizeOf`/offset assertions on the `value_t` union (catches a silently-reinterpreted-meter-reading class of bug with no compiler-catchable signal today) plus a table-driven constant-vs-citation test |
| TT1-13 `DEFERRED` — own future plan | Radio/CAT | `HamlibRadioProtocolTests.cs:393-427` sequences a 3-way dispose/PTT/poll race using `CallDelay` + two bare `Task.Delay(20)` calls — assumed ordering, not enforced; this is the regression gate for a physically-keyed-transmitter-on-disposed-handle bug and is a real CI flake risk. Replace with explicit gates per this project's own "deterministic gates, not shared race" rule |
| TT1-14 | Audio/Imaging/Logbook | Zero mixed-UTC-offset ordering test for `SqliteReceiveHistoryStore`/`SqliteLogbookRepository` (T1-16); the one test that touches the issue (`QueryAsync_DateRangeCompareIsLexicographicOnStoredOffset_NotInstantBased`) asserts the broken behavior as the contract. Blocked on the migration-shape decision T1-16 already flags — don't write until that's settled |
| TT1-15 | Audio/Imaging/Logbook | `MiniAudioDeviceMuteQuery` has one happy-path test and no dispose-race test, while its sibling `MiniAudioDeviceEnumerator` has exactly the missing test (`DisposeAsync_RacingConcurrentRefreshAsync`). Copy it verbatim — cheap, closes a native-context-use-after-release class of bug (a crash, not a wrong value) |
| TT1-16 | Audio/Imaging/Logbook | Every QRZ HTTP test fixture is `HttpStatusCode.OK` — zero `EnsureSuccessStatusCode`/status-code handling anywhere in source or tests. A 5xx currently surfaces as a nonsense XML-parse error instead of "QRZ is down." Cheap `[Theory]` addition, the `FakeHttpMessageHandler.ResponseFactory` seam already supports it |
| TT1-17 `DONE` | Infra | Fixed shared paths in `JsonSettingsStoreTests.cs:102,121,156` (`/tmp/relocated`, `/tmp/fresh-install-target`, `/tmp/conflict`) with inline cleanup that's **skipped on any assertion failure** — one failed run permanently poisons every subsequent run on that machine. Two-line fix: derive from the per-test temp subdirectory |
| TT1-18 | Infra | 4 tests silently `return` (report **passed**, not skipped) on Windows instead of asserting anything, relying on `File.SetUnixFileMode` denial — which root ignores, so if the Linux CI leg runs as root these are vacuous everywhere, not just on Windows. Investigate CI's user first, then gate with a real skip attribute |
| TT1-19 | Infra | No concurrent-writer test for `JsonSettingsStore`/`ConfigurationPresetStore`'s atomic-write claim beyond "no `.tmp` left behind on success" — doesn't prove crash-mid-write recovery. Add: garbage `.tmp` present + `LoadAsync` still returns good content; garbage `.tmp` present + `SaveAsync` new content + assert result is the new content, not a merge |

---

## Test-suite Tier 2/3 — batch opportunistically

- **Core.Sstv:** `RxDiskLineStagingBuffer`'s read-failure path has no fault-injection test (mirrors the write-side hooks that already exist); zero-length/null-input theories missing across all major entry points; two load-sensitive tests (`RxDiskLineStagingBufferTests.cs:230-273,319-341`) depend on scheduling assumptions under thread-pool saturation — plausible cause of this project's known intermittent full-suite flake; 651 `*ForTests` production members (63 in `AnalogFmSstvDecoder.cs` alone) — defensible for a legacy god-class port, but track as a ratchet, don't grow it further; move `GoldenVectorTests.cs`'s ~120-line inline re-measurement history to `docs/`, same fix as the source audit's D1(a).
- **UI:** ~26 tests gate on real wall-clock sleeps with 100-300ms slack; the negative-assertion subset (`Assert.Empty(...)` after a fixed delay) can pass for the wrong reason under CI load — inject a `TimeProvider` seam or pair with a positive control; `ImageSourceBitmapConverter` (the only `IImageSource`→screen-pixel path) has zero tests — extract the per-row packing into a plain-array-testable `PackRow` helper, sidesteps the documented headless-`Lock()` unreliability entirely; `en.json` format strings hand-copied into 3 test files with a "should be caught by updating this test" comment instead of an actual drift guard; 180+ inline 14-argument `OptionsWindowViewModel` constructions — extract a builder, mirroring the pattern `PaneViewModelTests.cs` already uses well for pane VMs.
- **Application:** `SstvSessionServicePttSafetyTests.cs:346-375`'s `RiskB_...` test's own comment claims an interleaving its current mechanism doesn't actually drive (investigate — stale comment or lost coverage after a refactor); T1-7's hardcoded-English-string violations are pinned verbatim by several tests' literal-string assertions, so fixing T1-7 will break them — tag with one shared marker now so the fix can find them in one grep; cancellation-token propagation untested outside `SstvSessionService` despite every public method taking one.
- **Radio/CAT:** the cross-backend exception-classification divergence (T1-10 — Hamlib/rigctld give up permanently, flrig/OmniRig poll forever, for the same real-world "rig unplugged" condition) is exercised per-backend but never named as a cross-backend inconsistency in one place — add a table-driven `BackendErrorTaxonomyTests` documenting it as tracked, not accidental; flrig has no `HttpRequestException`-mid-operation test and no dispose-races-in-flight-request test (Hamlib has the equivalent and it's one of the suite's better tests); `OmniRigComClient`/`Flrig` are absent from `coverage-thresholds.json` entirely — confirm first whether the coverage gate is actually running in CI (it may be silently unrun, which would be the bigger finding); `HamlibProtocolFactoryTests.cs:146-149` mutates process-global `ThreadPool.SetMinThreads` with no restore.
- **Audio/Imaging/Logbook:** `ReceiveHistoryRecorderTests.cs`'s negative assertions gated on `Task.Delay(50-200ms)` guard 3 real previously-shipped bugs (the AVT double-save family) — the tests you least want silently inert under load; two tests use literal `/tmp/does-not-exist-...` paths instead of `Guid.NewGuid()`, unlike every sibling test.
- **Infra:** `JsonLocalizationService`'s `string.Format` call is unguarded against a malformed `{0` placeholder in a community translation (same "hand-editable file bricks the app" class its other 6 tests exist to prevent); `LocaleFileIntegrityTests.cs` checks key coverage in only one direction and doesn't verify every `locales.json` manifest entry has a matching file; `ApplyPendingRelocationsTests.cs:156-183` races a 1200ms background delay against a production retry loop — same "shared race, not a deterministic gate" class this repo has already fixed once elsewhere (`HandleLifetimeExitTests.cs` shows the correct pattern right next to it).

---

## Verified genuinely strong (no action, stated so it isn't re-litigated)

- **Application.Tests**: PTT-safety testing (`SstvSessionServicePttSafetyTests.cs`) is exceptional — 31 numbered audit rounds, stateful fakes with deterministic happens-before hooks, tests that document their own prior mutation-insensitivity and how they were strengthened. No tautological assertions found anywhere in the project.
- **Core.Sstv.Tests**: golden vectors are real legacy captures with documented provenance and measured (not invented) tolerances; FIR filter expected values are independently computed in Python, not derived from the C# under test; all 43 modes' durations and VIS codes are pinned against cited legacy source lines; only 17 "didn't throw" assertions in the whole 26,300-line project, all justified; zero flaky/disabled tests.
- **UI.Tests**: `Fakes.cs` fakes are genuinely behavioral (filter/sort/mutate/raise-events like the real store), not record-and-verify theater; command tests assert user-visible outcomes (collection contents and order, not counts); headless-bitmap risk is handled correctly — no test asserts bitmap bytes, and the known headless `Lock()` unreliability is documented and worked around rather than ignored.
- **Core.Radio.Tests**: rigctld and flrig backends assert real wire bytes (`transport.WrittenText`, byte-for-byte); `RadioController`'s state machine (give-up counting, latch semantics, publish-before-dispose ordering) has 20 tests that are visibly the residue of multiple review rounds using deterministic gates, not sleeps; every hand-typed Hamlib/OmniRig P/Invoke constant was independently re-verified against the real headers in this pass and found correct.
- **Localization tests**: the model the other two infra projects should follow — real per-culture resolution, missing-key fallback chains, corrupt-file and corrupt-manifest recovery, BOM/non-ASCII handling, all with outcome-distinguishing assertions.

---

## Suggested sequencing (test-suite work)

**Steps 1, 2, 4-6 done** (`docs/plans/test-suite-fixes-phase1-plan.md`, 2026-08-30 — see the
implementation-status banner above the Test-suite Tier 0 section). Step 3 (TT0-1/TT0-2) remains
open, correctly: both are designed to land as the regression gate for the T0-2/T0-1 *source* fixes,
which haven't been scheduled yet. TT1-13 (referenced in the old step 6 wording as part of Tier 1) was
pulled into its own deferred plan during round-1 plan-review, not implemented here.

1. ~~**TT0-3** (`RequiresHamlibFact`/`RequiresRigctldFact`) and **TT0-4** (OmniRig reflection
   test)~~ — done.
2. ~~**TT0-5** (`ConfigurationPresetStore`/`JsonSettingsStore` corrupt-file tests) and **TT1-17**
   (shared-`/tmp` fix)~~ — done.
3. **TT0-1 + TT0-2** — still open. Write these *as* the regression gates for the T0-2/T0-1 source
   fixes, landed same PR as those fixes, not before (TT0-2 will hang if written first).
4. ~~**TT0-6** (DI validation both ways, `SynchronizationContext` harness)~~ — done (as its own
   testability seam + tests; the actual T0-4/T0-5 source fixes are still separately open).
5. ~~**TT0-7** (ADIF encoding + malformed-date)~~ — done.
6. ~~**TT1-1, TT1-3** (Core.Sstv test-infrastructure fixes)~~ — done. **TT1-2** (worst-row/outlier
   golden-vector metrics) remains open — needs its own measurement pass first, not bundled into
   phase 1.
7. Everything else at Tier 1 (TT1-4 through TT1-7, TT1-9 through TT1-12, TT1-14 through TT1-16,
   TT1-18, TT1-19), batched by subsystem; Tier 2/3 opportunistically.
