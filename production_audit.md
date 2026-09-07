# Production-readiness audit plan (2026-08-30)

Senior-engineer review of the full `src/` tree (~75,600 lines, 18 projects), run as 7 parallel
`auditor` passes (read-only): 6 scoped to one subsystem each, 1 scoped to the seams between them.
Scope, method, and prompts: efficiency, optimization, dedup, correct API usage, logging/exception
handling, plus general senior-review judgment (nullable/SOLID/DI, concurrency, disposal, security,
test coverage).

> ## Status, re-verified 2026-09-07
>
> The original header said "nothing listed here has been fixed yet". That is no longer true, and it
> stayed wrong for a week. Current state:
>
> - **Tier 0 — all 13 done.** Each item now carries its own commit reference below.
> - **Tier 1 — 17 of 18 done.** Only **T1-14** is open, and only in part. Its own commit says
>   "partial" and no follow-up exists.
> - **Tier 2 — open.** Four items were re-verified as still open, listed in that section.
> - **Tier 3 — open.** Four items were re-verified as still open, listed in that section.
> - **Test-suite Tier 0 — all 7 done.** TT0-1 and TT0-2 were both re-verified closed on 2026-09-07;
>   an earlier draft of this line still called TT0-2 open, which contradicted its own entry below.
> - **Test-suite Tier 1 — 5 done, 1 deferred, the rest open.** Five were re-verified as open and are
>   marked in the table. **TT1-19** looks closed but is not fully confirmed.
> - **Test-suite Tier 2/3 — untouched.**
>
> - **Triage, 2026-09-07:** an `auditor` pass over every remaining open item in THIS document returned
>   **no MUST FIX and no `principal` round needed**. See "Triage of the remaining open items" below.
> - **External audit folded in, 2026-09-07:** all 37 P1/P2 findings from `astra-audit.md` were
>   verified against current source — **zero false positives**. That pass DID find must-fix work,
>   including two transmitter-safety defects. **Two items filed P2 were raised to P1.** See "External
>   audit folded in" below, and items 00a to 00c at the top of the pick-up list.
>
> **Shipping status: not a go.** Three verified defects block a release — a cancelled action that can
> key the transmitter, an exit path that can leave it keyed, and a CAT desync that reports a false
> transmit state and never self-heals.
>
> **This file is now the ONLY backlog in the repo.** Every other document records measurements,
> decisions and history. If it is not in "What to pick up next" below, nobody is meant to be working
> on it. Decode-path items were folded in on 2026-09-07 — see "Decode-path items folded in from other
> documents".
>
> **Start here:** "What to pick up next — the short list", immediately below this banner.
>
> **How each verdict was reached.** Tier 0 and Tier 1 rest on commit evidence: every item has a
> commit that names it. The other tiers rest on reading current source. Items with no marker below
> were not individually re-checked, so treat them as unknown, not as done.

## What to pick up next — the short list

Everything not on this list is either done or deliberately dropped. Do not re-derive the backlog
from the tiers below, and do not re-derive it from any other document — this is the only one that
carries work. Reasons and dropped-item rationale are in "Triage of the remaining open items"
further down.

**Transmitter safety and wrong-data defects — these outrank everything else on this list.**
All three come from the external `astra-audit.md` pass, all three are verified, and **two of them were
filed as P2 when they are not.** My correction and the reason for it are recorded per item. Full
provenance in "External audit folded in" below.

00a. **A pending direct-fire can key the transmitter after Cancel** (`ASTRA-020`, filed P1, agreed).
   `TxImageEditorPaneViewModel.Dispose()` sets `_disposed` and nothing else. It does not bump the
   template-load generation, and it deliberately does not detach `DirectFireRequested`, so the guard
   at the end of `LoadTemplateAsync` passes and the invoke at `:890` fires.
   `TxControlsPaneViewModel.OnEditorDirectFire` never compares the editor it was handed to
   `_currentEditor`, so it transmits and then reopens the editor the operator just closed.
   **Why it is worse than the item claims:** with no unsaved edits, Cancel is a single click and is
   not busy-gated — the code's own comment says "Cancel/Apply have no busy gate". The `cf72594`
   direct-fire guard does not cover this path.

00b. **Application exit does not wait for PTT-test cleanup** (`ASTRA-040`, filed P2 — **raise to P1**).
   The test protocol is created locally in `RadioSessionService`, keyed, then unkeyed with bounded
   retries. `RadioSessionService` implements neither `IDisposable` nor `IAsyncDisposable`, so the
   host's bounded `DisposeAsync` owns nothing here. Closing Options only cancels the token.
   **Why I raise it:** the outcome is a transmitter left keyed on air. An auditor searched `docs/`
   and found **no recorded acceptance** anywhere, and this is a different failure class from the two
   shutdown-drain items that ARE accepted (`ASTRA-036`, `ASTRA-037`) — those lose data, this
   transmits. "Accepted in kind" does not transfer across that line.

00c. **rigctld response-stream desync reports a false transmit state, permanently**
   (`ASTRA-003`, filed P2 — **raise to P1**). `SetBandwidthAsync` throws on an empty mode line
   before reading the following passband line, leaving one unread line in an open socket.
   **Why I raise it, and why the item understates itself:** the exception is classified
   command-level, so `RadioController` keeps the connection and never rebuilds it. The auditor traced
   the next poll on a leftover `2400`: frequency reads 2400, mode reads the real frequency line as a
   mode token and yields `Unknown`, and the transmit query parses 2400 successfully — so
   **`IsTransmitting` latches true forever**, and no exception is ever raised to trigger the only
   recovery path. Silent wrong data the operator cannot diagnose.

**Decode-path work — do the probe first, it may close the biggest item for free:**

0a. **Green-cast Fault B — run the whole-image ideal-transport probe.** Extend the existing per-RGB-
   triple Fault-A model to a whole image: photographic source through the real encoder's channel
   layout, transport the channel bytes ideally (no modulation, filters or demodulation), reassemble
   through the matching scanline decoder, measure green bias. About +4.7 means Fault B is protocol or
   TX-side and closes as a parity decision like Fault A. About +2.3 means the loss is in the DSP chain
   and earns the full review cadence. Touches no shipping code. Largest audience on this list — every
   YCbCr mode, which is most real colour traffic. Detail: `docs/known-decode-defects.md` §1.

0b. **MN/MC right-edge stripe — the one confirmed visible defect.** A coloured stripe on the right
   edge of every MN and MC decode, 6 modes, deterministic, present with all DSP options off. Now
   localized to one variable: MN140 and MP140 are structurally identical apart from sync 1900 against
   1200 Hz, porch 2044 against 1500 Hz, and `LuminanceMin/MaxHz` 2044-2300 against the default
   (`SstvModeRegistry.cs:418-438` against `:489-511`). MP140 is clean. **The defect follows the narrow
   frequency plan and nothing else**, so start with that controlled pair on one source plus a clean
   noise-free decode, and look at narrow band and sync-tone handling rather than a generic
   end-of-line off-by-one. Full review cadence — this is decode-path state. Detail:
   `docs/known-decode-defects.md` §4.

0c. **Freeze the `m_sint1` sync-bypass tracker during VIS-bit decode.** `AnalogFmSstvDecoder.cs:4563`
   and the sibling threshold block at `:4679-4694` are gated only on `_syncBypass1PrimaryHeld`, which
   drops on the routine d12 dips that the 1100/1300 Hz VIS data-bit tones cause. Legacy freezes it:
   `m_sint1.SyncStart()` is case-0-only (`sstv.cpp:1900`), `SyncMax` is case-1-only (`:1960`), and
   cases 2/9/3 hold no `m_sint1` code at all. `m_sint2` and `m_sint3` were already fixed by S12
   (`:4611`, `:4642`), so this is the same two-gate shape, already shipped once and reviewed. If it
   fires, the result is a bypass mode-commit during VIS decode — a garbage picture. Probability is
   unmeasured; a noise sweep that finds nothing would not prove safety and costs about as much as the
   gate, so fix rather than probe. **One real design question for plan-review:** must
   `_syncBypass1PrimaryHeld` reset on re-entering Search, matching legacy's `m_SyncMode = 0` fallback
   at `sstv.cpp:1971`/`:1983`?

**Then the production-readiness work, in this order:**

1. **Golden-vector worst-row metric.** `tests/ScanlineStudio.Core.Sstv.Tests/GoldenVectorTests.cs:966`
   — add a per-row max beside the frame average, measure the current worst row per fixture, pin at
   about 1.5x to 2x. One corrupted row currently passes.
2. **Map all 43 modes to their legacy `Main.cpp` `Line*` function**, then capture one TX golden
   fixture per uncovered function. **Corrected 2026-09-07 — do not rely on the existing 11.** All 11
   `TxCapture/*.provenance` files read `UNKNOWN-STALE-PENDING-RECAPTURE`, `StaleFixtureTheoryAttribute`
   skips the comparison, and the `.mmv` files are this port's own TX output rather than
   legacy-generated audio. The Scottie-class guard therefore does **not currently run**. Do the
   mapping first — it still tells you whether the gap is 5 fixtures or 30 — but treat current TX
   channel-order coverage as zero, not as 11.
3. **WAL mode on `history.db`.** About 2 lines at connection open, in both
   `SqliteLogbookRepository` and `SqliteReceiveHistoryStore`. Latency, not crashes.
4. **Hoist the command out of the loop** at `src/ScanlineStudio.Core.Logbook/SqliteReceiveHistoryStore.cs:490`
   and `:851`. Only those two sites. The other 19 `CreateCommand()` calls are fine.
5. **Assert every `{loc:Translate X}` key resolves.** Extend
   `tests/ScanlineStudio.UI.Tests/NoHardcodedAxamlStringsTests.cs`. A typo'd key ships as a broken
   label today.
6. **Dispose-race test for `MiniAudioDeviceMuteQuery`.** Copy the shape of its sibling
   `MiniAudioDeviceEnumerator`'s existing test.
7. **Convert 5 silent-pass tests to `[SkipOnWindowsFact]`:** `ApplyPendingRelocationsTests.cs:219`,
   `AppLocationOverridesTests.cs:47`, `AppLocationsServiceTests.cs:99` and `:192`,
   `JsonSettingsStoreTests.cs:185`. Host.Tests needs its own copy of the attribute.
8. **QRZ HTTP status check plus one non-OK fixture.** `Core.Logbook` has no status handling at all.
   Error-message quality only — do it last, or with adjacent work.

**Decide before writing any code:**

- **Gallery filter latency.** Measure `RxHistoryPaneViewModel.UpdateFilteredEntries()` at N = 1 000 /
  5 000 / 20 000 entries. Above about 50 ms per keystroke, add a 150 to 250 ms debounce. The deeper
  fix is a row cap on `QueryAsync` (`RxHistoryPaneViewModel.cs:797`), which today has none.
- **Hamlib header anchoring.** Vendor a pinned `rig.h` into the test project with a `LICENSES.md`
  entry (Hamlib is LGPL-2.1), or accept a conditional skip. `hamlib/` is gitignored, so a test
  reading it directly would silently not run anywhere else.

**Two more, only after item 5 or 7 lands:** scoped `MainWindow.axaml.cs` tests (target T1-12's
`DataContextChanged` re-entry guard first), then fold the 35 `logger is not null` guards into a
null-object logger in that same file.

---

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

### T0-1 `DONE` (`915cacf`). Radio: un-key retry loop is inert on the Hamlib backend [safety]
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

### T0-2 `DONE` (`915cacf`). `ISettingsStore` has no atomic update; ~25 read-modify-write sites can silently clobber each other
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

### T0-3 `DONE` (`fd1496b`). Startup blocks the main thread on radio-connect/audio-open with no timeout
`Host/Program.cs:219-241` — `ConnectUsingSettingsAsync().GetAwaiter().GetResult()` and
`StartReceivingAsync().GetAwaiter().GetResult()` run *before* `SetupWithLifetime`, with `ct=default`
passed through (`Application/RadioSessionService.cs:37,44`). A powered-off rigctld host or a stalled
audio server hangs the app with **no window on screen** for 20+ seconds — reads as a full hang, not
a radio problem.
**Fix:** move both to a post-window background start (`Task.Run` with the existing `Log.*Failed`
handlers), or wrap each in `.WaitAsync(TimeSpan)`.
**Investigate first:** confirm nothing downstream assumes RX capture is live by the time
`MainViewModel` is constructed.

### T0-4 `DONE` (`915cacf`). DI container graph is never validated before first resolve
`Host/Program.cs:133-142` — `ValidateOnBuild`/`ValidateScopes` only enabled in `Development`, which a
GUI launch never is. A missing registration surfaces as an unguarded crash from `SetupWithLifetime`
→ `MainViewModel` resolution instead of the already-handled `Console.Error` + rethrow at line 140.
Factory-backed singletons make it worse: MS.DI doesn't cache a failed construction, so the crash
repeats on every resolve.
**Fix:** `ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions {
ValidateOnBuild = true, ValidateScopes = true }))` before `Build()`. Cheap, existing tests already
prove the graph resolves clean. No investigation needed.

### T0-5 `DONE` (`fd1496b`). Shutdown can deadlock the UI thread for a guaranteed 10s stall
`Host/Program.cs:313-318` (`HandleLifetimeExit`) — `Task.WhenAny(...).GetAwaiter().GetResult()` runs
on the Avalonia UI thread. Any `await` in the disposal chain missing `ConfigureAwait(false)` posts
its continuation back to that blocked thread → deadlock until the 10s `Task.Delay` wins, leaving
audio device/CAT port undisposed and triggering the incomplete-teardown restart path.
**Fix:** `var disposeTask = Task.Run(() => host.DisposeAsync().AsTask());` so continuations resolve
off the UI thread.
**Investigate first (cheap):** audit `ConfigureAwait` usage in the async-disposable singletons
actually in the graph (`MiniAudioEngine`, `SstvSessionService`, `RadioController`).

### T0-6 `DONE` (`1ba6ab5`). `RxDiskLineStagingBuffer`: 8 bare `catch {}` blocks swallow I/O failures silently
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

### T0-7 `DONE` (`fd1496b`). Decoder swap can hold a lock for up to 10s during disposal, freezing every UI property read
`Core.Sstv/RestartableSstvDecoder.cs:1566,655` — `outgoing.Dispose()` runs *inside* `Swap()`'s
`lock (_gate)`, and that disposal chains into `RxDiskLineStagingBuffer.Dispose`'s two 5s
`WaitForConsumer` calls. Every UI-facing property (`SignalPeakLevel`, `SlantPpm`, `SyncSource`, etc.
— `RestartableSstvDecoder.cs:1405-1429`) blocks on the same lock. Previously bounded by a 12-hour
maintenance interval; a recent change made swaps user-triggerable (Options Save queues one on next
idle push), so this is now click-rate-bounded, not maintenance-interval-bounded.
**Fix:** `Swap` returns the outgoing instance instead of disposing it; caller disposes it in the
existing post-lock section (`PushSamplesCore`, `:719-753`) after exiting the lock. Small, local.

### T0-8 `DONE` (`d71bf61`). QRZ account password stored plaintext with zero mitigating control
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

### T0-9 `DONE` (`915cacf`). Logbook SQLite: no indexes beyond primary keys, plus an O(files×rows) reconcile scan
`Core.Logbook/SqliteLogbookRepository.cs:239-259`, `SqliteReceiveHistoryStore.cs:555-569` — only
`Id TEXT PRIMARY KEY`. Every logbook/gallery view query (`SearchAsync`, `QueryAsync`, sorted by
`StartUtc`/`ReceivedAt` DESC) is a full scan + sort. `ReconcileWithDiskAsync:456-459` runs a
`WHERE NOT EXISTS` subquery **inside a per-file loop** — a full table scan per candidate file.
**Fix:** add to both `EnsureSchema` methods (idempotent): indexes on `StartUtc`, `Callsign COLLATE
NOCASE`, `ReceivedAt`, `FilePath`, `LinkedQsoId`. Highest value/effort ratio found in the entire
review — zero data risk, cheap. Consider a UNIQUE constraint on `FilePath` (currently worked around
with `WHERE NOT EXISTS`) — **investigate first**: confirm no legitimate duplicate-`FilePath` rows
exist in shipped databases before adding UNIQUE.

### T0-10 `DONE` (`73932d8`). RX image decode allocates ~512 Large-Object-Heap buffers per received image
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

### T0-11 `DONE` (`76975d3`). UI: no `WriteableBitmap`/`Bitmap` in the UI layer is ever disposed
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

### T0-12 `DONE` (`1d76e84`). UI: TX editor recomputes the full image pipeline synchronously on the UI thread per pointer-move
`UI/ViewModels/TxImageEditorPaneViewModel.cs:3532-3553` — a crop drag runs the full
crop→resize→adjustments→template-composite (ImageSharp text rasterization) pipeline plus a full
`WriteableBitmap` allocation, **on every mouse-move event**. Same for each of 6 adjustment sliders.
**Fix:** apply the same latest-wins coalescing this codebase already does correctly in
`WaterfallPaneViewModel.cs:215-239` (one `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)`
in flight at a time).
**Investigate first (light):** measure `ApplyTemplate` cost with 3-4 text elements to decide if
coalescing alone suffices or the composite also needs to move off-thread.

### T0-13 `DONE` (`8e33687`). `MainViewModel` has zero test coverage; its Ctrl+S dispatch has a documented prior bug
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
| T1-1 `DONE` (`09c79f0`) | Core.Sstv | Both hot-path FIR filters do a full per-sample array shift instead of a circular buffer — roughly doubles cost of the 2 hottest DSP routines. Golden-vector-gated; **do not** vectorize the dot product as part of this fix (breaks bit-parity) | `SearchBandpassFilter.cs:188`, `HilbertFmDemodulator.cs:275` |
| T1-2 `DONE` (`0482045`) | Core.Sstv | `WaterfallSource.Frames` is a bare `Subject<T>` called synchronously from the audio drain thread — no stated scheduler/slow-subscriber policy, exactly the regression CLAUDE.md's concurrency rule targets | `WaterfallSource.cs:23,55,133` |
| T1-3 `DONE` (`94d57c6`) | Core.Sstv | `IAsyncEnumerable<float>` yields one sample at a time in the TX encoder — ~12.8M awaits for a PD290 transmission, no I/O involved | `AnalogFmSstvEncoder.cs:190,317,347` |
| T1-4 `DONE` (`0c267d9`) | Core.Sstv | One `[LoggerMessage]` in 20,100 lines — a dozen user commands (ForceMode, RequestReSync, SetModeLock, etc.) and decoder-restart/reconfiguration-rejected events are unlogged | `AnalogFmSstvDecoder.cs` command handlers, `RestartableSstvDecoder.Swap:1464` |
| T1-5 `DONE` (`746ff03`) | Application | `SstvSessionService.PlayWithPttAsync` is an 844-line method with 12 fields tracking PTT physical state; two residual races are self-documented as narrowed-not-closed. Extract a `PttSafetyCoordinator`; needs its own plan-review round, driven by the existing `SstvSessionServicePttSafetyTests` suite | `SstvSessionService.cs:3254-4098` |
| T1-6 `DONE` (`9411768`) | Application | Unverified sync-over-async on the audio drain thread — comment retracts its own prior safety claim, self-flagged as an open unknown on a safety path | `SstvSessionService.cs:1919` |
| T1-7 `DONE` (`0810dce`) | Application | Raw English exception text is the UI's error surface in several places (violates no-hardcoded-strings rule); `LogQsoAsync` discards the post-persist failure reason; an unguarded event-raise after a successful settings write can report "save failed" on a save that succeeded | `RadioSessionService.cs:81,104,179,209,292`; `LogbookSessionService.cs:99` |
| T1-8 `DONE` (`e0ccf0d`) | Radio/CAT | Lifecycle calls (`Connect`/`Disconnect`/`Dispose`) are documented as caller-serialized but no caller actually serializes across each other (preset switch vs. Options Save). Needs a paper decision on inline-subscriber re-entrancy before adding a lock | `RadioController.cs:130-267`; callers in `ConfigurationPresetService.cs:378-382`, `OptionsWindowViewModel.cs:992,1032` |
| T1-9 `DONE` (`e0ccf0d`) | Radio/CAT | Give-up threshold (5 attempts, ~7.5s) permanently drops the session on a cable pull with no auto-recovery — product decision, not a constant tweak | `RadioController.cs:36,367-407` |
| T1-10 `DONE` (`e0ccf0d`) | Radio/CAT | "Rig unplugged" is a permanent give-up on Hamlib/rigctld but an infinite 250ms-cadence poll forever on flrig/OmniRig (different exception classification) — decide intended semantics, make uniform | `FlrigClientProtocol.cs:74`, `OmniRigRadioProtocol.cs:65` vs. `RadioController.cs:322-407` |
| T1-11 `DONE` (`0c267d9`) | Cross-subsystem | "Test Connection" UI calls omit a cancellation token/timeout on 4 sites, unlike the sibling PTT-test calls — combined with T1-8/Hamlib timeout, can leave "Testing…" stuck indefinitely | `OptionsWindowViewModel.cs:1241,1326,1391,1539` |
| T1-12 `DONE` (`0c267d9`) | UI | `MainWindow`'s cross-VM event wiring lives entirely inside `DataContextChanged` using `+=` with no re-entry guard — a second firing double-subscribes (2 Options windows, 2 viewer windows). Latent today (DataContext only assigned once), one-line guard | `Views/MainWindow.axaml.cs:147-780` |
| T1-13 `DONE` (`0810dce`) | UI | Frequency/SWR/ALC/PWR/duration values composed via raw string interpolation in ViewModels/converters, bypassing `ILocalizationService` — decimal separator and unit literals break on non-en-US culture | `RadioStatusViewModel.cs:535,536,562,578,583,588,1331`; `RxImagePaneViewModel.cs:469,470,596,598`; 3 more sites |
| T1-14 `PARTIAL` (`69cdc91`) — **still open**, no follow-up commit | Audio/Imaging/Logbook | Every imaging operation (crop/resize/adjust/overlay/template/rotate) pays a full convert-in/convert-out; `RecomputePreview` fires per pointer-move — ~8 full-image conversions per preview frame, compounds with T0-12. **Corrected 2026-08-31:** the hot preview path (`TxImageEditorPaneViewModel.RecomputePreviewPipeline`) is fixed — see the "Tier D progress" note. The other 6 call sites named below are unchanged, deliberately (cold paths, not the "fires on every pointer-move" cost this item is about) | `TransmitImagePreparer.cs:1085-1121` (`ToImageSharp`/`FromImageSharp`) and 6 remaining call sites |
| T1-15 `DONE` (`0810dce`) | Audio/Imaging/Logbook | Same pixel-conversion loop duplicated in 7 places; already caused a real bug (`StockImageLibrary` missing an `AutoOrient` call `ImageFileLoader` had) | `ImageFileLoader.cs:35-54`, `StockImageLibrary.cs:94-113`, 5 more sites |
| T1-16 `DONE` (`10e06b7`) | Audio/Imaging/Logbook | Timestamps stored as local-offset strings, compared lexicographically — diverges from chronological order across DST/timezone changes. Needs a migration decision before coding | `ReceiveHistoryRecorder.cs:373,428`; `SqliteReceiveHistoryStore.cs:419-422,142,462` |
| T1-17 `DONE` (`0810dce`) | Audio/Imaging/Logbook | `AdifImporter` assumes UTF-8 regardless of source file encoding; unguarded date-slice parsing throws the wrong exception type and aborts mid-import, discarding already-mapped records | `AdifImporter.cs:40,298-307` |
| T1-18 `DONE` (`0c267d9`) | Infra | `CodePagesEncodingProvider` (mandatory per CLAUDE.md §4 for CP932 legacy files) is registered nowhere in the repo — latent until legacy `.ini`/`.mtm` import is attempted | repo-wide, none found |

**Corrections (2026-08-31, Tier-1 roadmap planning — see `PROJECT_BRIEF.md` for the ranked plan):**
- **T1-2:** the "no stated scheduler/slow-subscriber policy" premise is stale.
  `IWaterfallSource.cs`'s own doc comment already states the policy explicitly (added in a commit
  predating this audit), and the one real production subscriber (`WaterfallPaneViewModel.OnFrame`)
  already coalesces safely with O(1) drain-thread work. Remaining gap is narrower: no regression
  test exists gating a slow/throwing future subscriber (`WaterfallSourceTests.cs` has none, unlike
  `DecoderSubscriberFailureTests.cs`'s equivalent coverage for the decoder).
- **T1-6, closed (2026-08-31):** `SstvSessionService.cs` used to contain two comments that
  disagreed — a Round-15 retraction at the call site flagged the sync-over-async safety claim as
  unverified, while a later Round-17 comment elsewhere in the same file appeared to independently
  trace and confirm the exact `MiniAudioEngine` mechanism that would resolve it, never used to
  update the Round-15 text. Independently re-traced `MiniAudioEngine.ClaimCaptureSessionAsync`/
  `DisposeCaptureSessionAsync`/`MiniAudioCaptureSession.Dispose()` end to end: Round-17's own claim
  was correct as far as it went (a drain-thread-originated synchronous re-entrant stop cannot
  self-join-deadlock through `MiniAudioEngine`'s own machinery). But neither comment addressed a
  real, separate issue one level out: `StopReceivingAsync()`'s own `_rxTransitionGate.WaitAsync(...)`
  — the very FIRST line of that method — is a genuine async wait, and the gate is also held by
  `PlayWithPttAsync`'s own routine RX-pause-for-TX step, not just `DisposeAsync` as the old comment
  claimed. If contended at the exact moment `OnDecoderRestartCriticallyOverdue` fires (synchronously,
  on the drain thread), the drain thread blocks for up to `_cleanupTimeout` (~5s) — a bounded stall,
  not a deadlock (both sides are independently bounded), but a real cost (dropped RX audio, likely an
  abandoned native capture session on the other side) reachable on an ordinary transmission, not just
  at shutdown. Fixed: a zero-wait, non-blocking gate try-acquire first; on contention, defer the stop
  to a background task instead of blocking the drain thread — breaks the dependency, since deferring
  lets `DrainLoop` exit normally, letting the other gate holder finish and release it. One auditor
  code-review round, go. New regression test uses two separate controllable gates (this project's own
  established rule), confirmed via a temporary stash-and-rerun to fail against the pre-fix code
  (blocks ~5s, matching the corrected bounded-stall framing exactly, not a hang).
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

**Update (2026-08-31):** Tier C is now closed (see below) and T1-2/T1-6/T1-16/T1-3/T1-1/T1-5/T1-14
are all done (T1-14 partially — see its own "Tier D progress" note below for scope) -- see the
"Tier D progress" notes further below for all of these.

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

**Tier D progress — T1-6 closed (2026-08-31):** full detail in this item's own corrected entry in
the "Corrections" block above. Summary: independently re-traced `MiniAudioEngine`'s
capture-session-dispose machinery end to end and confirmed Round-17's own claim was correct as far
as it went, but found a real, separate, previously-undiscovered issue one level out —
`StopReceivingAsync()`'s own gate wait, contended against `PlayWithPttAsync`'s routine RX-pause (not
just `DisposeAsync`), can freeze the audio drain thread for a bounded ~5s (not a deadlock — both
sides are independently bounded) when `OnDecoderRestartCriticallyOverdue` fires synchronously on
that thread. User approved fixing it. Fixed with a zero-wait try-acquire + defer-on-contention
pattern; 2 rounds of plan-review (round 1 corrected my own initial "deadlock" framing to the accurate
"bounded stall" one) + 1 code-review round, go. New regression test (two separate controllable
gates) confirmed via stash-and-rerun to fail against the pre-fix code exactly as predicted (~5s
block). Full `Application.Tests` suite (437 tests) green.

**Tier D progress — T1-16 closed (2026-08-31):** `SqliteReceiveHistoryStore`'s `ReceivedAt` column
stores a genuinely correct instant (local offset preserved), but `QueryAsync`'s own `From`/`To`/
`ORDER BY` used to compare that TEXT column directly — a lexicographic compare, not an instant-based
one, so two rows/queries with different offsets could sort/filter wrong even though every individual
value was itself correct. User chose the fix shape (of 2 presented): a new, separate `ReceivedAtUtc`
column (not changing `ReceivedAt`'s own meaning), matching this file's own established
`ALTER TABLE ADD COLUMN` + backfill schema-evolution pattern (already used 6 times). One auditor
code-review round found a real no-go issue in the first pass: gating the backfill on "only when the
column was newly added this pass" (copying `DecodeState`'s own one-time-heuristic-backfill
convention) would let a `NULL ReceivedAtUtc` introduced LATER (e.g. an older build's own write
against an already-migrated DB) stay silently invisible from every filtered view forever, with
nothing left to ever repair it — data-invisibility, not a nit. Fixed: the backfill now runs
unconditionally every startup, scoped to `WHERE ReceivedAtUtc IS NULL` (idempotent, non-destructive,
so no gate is needed), plus a per-row try/catch so one unparseable value degrades to "that one row
stays NULL" instead of crashing app startup on every subsequent launch. New tests: the renamed
pinned-bug test flipped correct, a real DST-transition case, a migration/backfill test, a
self-healing-after-a-later-NULL test, and an unparseable-row-doesn't-crash-startup test — all
confirmed via stash-and-rerun to fail against the pre-fix code exactly as predicted. Full
`Core.Logbook.Tests` suite (189 tests) and full `UI.Tests` suite (1208 tests) green.

**Tier D progress — T1-3 closed (2026-08-31):** confirmed `AnalogFmSstvEncoder.EncodeAsyncCore` never
actually awaits real work (its one `await` was a no-op `Task.CompletedTask` trick), so the real cost
was CLR per-call `MoveNextAsync` dispatch overhead, not thread-hop/suspension — corrected framing
before proceeding. Blast radius was much larger than this item's own one-liner implied: ~60 test
files call `EncodeAsync` directly. User chose an ADDITIVE new `EncodeBatchedAsync` interface member
(not changing `EncodeAsync`'s own return type), so all ~60 test files stay untouched. Getting the
real win also required tracing into `PlayWithPttAsync` (the exact method T1-5 is scoped to eventually
extract) and `GenerateTone` (the Tune feature's own tone producer) — confirmed `PlayWithPttAsync`'s
own `samples` parameter has exactly one use in its 844-line body (a pure pass-through), so this was a
type-only change, not a PTT-safety logic change. User approved the full scope. `EncodeAsyncCore`
renamed to `EncodeBatchedAsyncCore` (the sole DSP synthesis implementation, now batched);
`EncodeAsync` derives its own per-float behavior from it via a `FlattenBatches` wrapper, specifically
to avoid a second copy of the DSP math (the Scottie-class risk CLAUDE.md itself calls out). 2 rounds
of plan-review (round 1 found and fixed: a `[EnumeratorCancellation]` gap that would have silently
dropped a caller's `.WithCancellation(token)`, an incorrect "yield return is a cooperative yield
point" premise in the `GenerateTone` batching, and an absolute-vs-relative sample-index risk) + 1
code-review round (go, only doc/comment nits — folded in). New tests: `EncodeBatchedAsync` sync-throw
tests mirroring `EncodeAsync`'s own, a flatten-equality test, a never-empty/uniform-batch-size test,
and matching `RestartableSstvEncoder` delegation tests. Full solution build clean; full
`Application.Tests` (437), `Core.Sstv.Tests` (1532, includes every pre-existing golden-vector/
round-trip test proving the flatten-derived path is bit-identical), and `UI.Tests` (1208) all green.

**Tier D progress — T1-1 closed (2026-08-31):** replaced the O(tap) `Array.Copy`-per-sample delay
lines in `SearchBandpassFilter.ProcessSample`/`HilbertFmDemodulator.DoFir` with a new `FirDelayLine`
sealed class — an O(1)-write circular buffer, walking-pointer convolution (no per-sample modulo:
plan-review round 1 found capacity isn't a compile-time constant here, so a naive `%` in the hot loop
is a real `idiv` that could cost more than the `Array.Copy` it replaces at high tap counts). 2 rounds
of plan-review: round 1 found a genuine blocker — `HilbertFmDemodulator.ProcessSample`'s own
`_z[_htap]` raw physical-index read, OUTSIDE `DoFir`, would have silently read the wrong tap once the
head moved under a naive circular conversion (full demodulator corruption, not a small drift — the
existing test suite's own short zero-history tests would NOT have caught it, confirmed during
review). Fixed by routing every delay-line access through `FirDelayLine`'s own logical indexer, so
that exact call site needed no textual change at all — correct by construction, not by caller
discipline. Round 2 confirmed the fix and the gate-test plan, ready to build. 1 code-review round: go
for production as specified; folded in a hardening nit (the indexer now throws
`ArgumentOutOfRangeException` instead of a Release-inert `Debug.Assert`, with a new regression test)
plus a doc-comment misattribution fix (a convention had been attributed to "legacy's own `Array.Copy`
shift," but legacy's real `CFIR2::Do`/`DoFIR` don't shift at all — the shift was this port's own
pre-change C# code). New bit-exact gate tests compare the full `ProcessSample` method (not just the
FIR core in isolation — a `DoFir`-only comparison would have missed the blocker) against a test-local
reference reproducing the pre-change linear implementation exactly, at every reachable tap including
an odd one (8000Hz/Wide → tap 17, plan-review round 2's own correction to an earlier "2 reachable
sample rates" framing that undersold the real 5000-48500Hz domain), ≥10x buffer capacity per case,
mid-stream coefficient/mode switches landing at non-capacity-multiple offsets. Both gate tests
confirmed via a deliberate mutation (flipped push direction) to fail hard against a broken
implementation, then restored and re-verified. Full `Core.Sstv.Tests` suite (1556 tests) and full
solution build green.

**Tier D progress — T1-5 closed (2026-08-31):** extracted a `PttSafetyCoordinator` class owning the 8
PTT keying/un-keying state fields out of `SstvSessionService`'s `PlayWithPttAsync`/`SetPttLockAsync`/
`DisposeAsync`; `_disposed`/`_transmitInFlight`/`_pttLockGate` deliberately stay on the service.
Corrected framing: this does NOT shrink `PlayWithPttAsync`'s own line count — the real value is
state-machine testability (the two documented lost-update races are now directly testable via
ordinary sequential calls, no thread races needed), not size reduction; "biggest lift" undersold how
much design work the shape itself needed, not the amount of code moved. A dedicated investigation
found the regression suite's own `RiskB_UnlockRacingCleanup_DoesNotStrandRxStopped` test does not
actually force the race it claims to (its two calls run sequentially, not concurrently) — user
approved fixing this in the same pass, not deferring it. 3 rounds of plan-review: round 1 set the
direction (pure-synchronous coordinator, no I/O/callbacks — avoids a self-deadlock risk a
callback-shaped design would have introduced); round 2 found 5 real blockers in the first concrete
design (a flag that would permanently latch a false alarm on the no-radio path, a count that could go
negative, a missing `RunContinuationsAsynchronously` risking inline-continuation self-deadlock, an
unreturnable log-choice, a stress-test invariant satisfied by the exact bug it was meant to catch);
round 3 re-derived the design from a fresh line-by-line re-read of the actual source (not the prior
round's own summary) and found 2 of those blocker classes recurring plus 3 more field-ownership gaps,
before finally verifying "ready to build." 1 code-review round: go for production, only cosmetic/
test-only nits (dangling XML doc `<see cref>`s to now-moved fields, a stress-test thread-id assertion
that could flake under thread-pool scheduling) — both fixed. Mutation check (removed
`ReleaseKeyedSlot`'s null-guard) confirmed the pre-existing 66-test regression suite catches a real
defect, not just passes vacuously. New `PttSafetyCoordinatorTests.cs` (10 tests) directly proves the
extraction's own argued benefit: Race 1 and Risk B's deterministic half now testable via ordinary
sequential calls. Full `Application.Tests` (447) and `UI.Tests` (1208) green.

**Tier D progress — T1-14 partially closed (2026-08-31):** fused the TX image editor's hot preview
pipeline (`TxImageEditorPaneViewModel.RecomputePreviewPipeline`, the one both the coalesced
pointer-move path and the ~20 discrete one-shot triggers funnel through) from 4 separate
Crop/Resize/ApplyAdjustments/ApplyTemplate calls (up to 8 ImageSharp round-trips per frame) into one
new `ITransmitImagePreparer.ComposePreview` call (exactly 2, regardless of how many stages are
non-trivial). Explicitly a **partial** closure, not full: the audit's own "7 call sites" count for
this item names 6 more (`TxImageEditorPaneViewModel.BuildFinalOutput`, the method that actually
produces the transmitted image; a second full-chain copy in `TxControlsPaneViewModel.cs`'s own
mode-change reflow; and 4 individual-method call sites) — all deliberately left untouched, since they
are cold paths, not the "fires on every pointer-move" cost this item's own one-liner is about.
`ComposePreview` was added as a new interface member with a DEFAULT implementation (the literal
un-fused 4-call chain) — the contract every implementer, including this project's own 2 test fakes,
must stay pixel-identical to; `TransmitImagePreparer` overrides it with the real fused body, built by
extracting shared `CropInto`/`ResizeInto`/`ApplyAdjustmentsInto`/`ApplyTemplateInto` helpers that BOTH
the existing per-stage public methods and the new override call — one copy of each stage's own logic,
not two that could drift apart. One plan-review round found 3 gaps in the first draft (the 2 fake
implementers would have broken ~60 existing test assertions without the default-implementation shape;
`BuildFinalOutput` needed an explicit "stays on the old chain, pixel-parity pinned by tests" statement;
the test matrix needed a genuinely offset, non-full-frame crop case, since a full-frame-only matrix
can't distinguish a `source.Width`-vs-`image.Width` mixup) — all folded in before implementing. One
code-review round: go for production; folded in two more tests (a `TemplateBoxElement` case, and a
reflection check pinning that `TransmitImagePreparer`'s own `ComposePreview` genuinely binds as the
interface's implementation rather than silently falling through to the slower default on a future
signature drift — pixel-exact tests alone can't catch that, since both paths produce identical output
by design) plus a doc-comment nit fix. New `TransmitImagePreparerComposePreviewTests.cs` (9 tests,
pixel-exact against the old 4-call chain, not tolerance-bounded — `ToImageSharp`/`FromImageSharp` are
lossless 8-bit copies, so exact equality is the correct gate) — mutation check (swapped the crop
region's own width/height source) confirmed all 9 actually fail against a broken implementation, not
just pass vacuously. Full `Core.Imaging.Tests` (114), `Application.Tests` (447), and `UI.Tests` (1208)
green.

---

## Tier 2 — medium priority, batch with adjacent work

> **Re-verified open 2026-09-07** (the rest of this tier was not individually re-checked):
> `SqliteCommand` is still never disposed — 21 `var command = connection.CreateCommand()` sites in
> `Core.Logbook`, zero `using`. `history.db` still has no WAL mode and no busy timeout.
> `TxControlsPaneViewModel.Dispose()` cancels its CTS but never disposes it, and unsubscribes no
> editor handler. `IHost` is still built and never started — no `IHostedService`, and no
> `Run`/`StartAsync` in `Program.cs`.

- **Core.Sstv:** per-line array/delegate allocations in scanline decoders (`YCbCrSequentialScanlineDecoder.cs:16-18`, `YCbCrLinePairedScanlineDecoder.cs:18-21`); `PixelSampleReader` delegate indirection on the hottest read path; `WaterfallSource.BuildFrame` allocates 2 scratch arrays/frame; event fan-out allocates twice per raise (`AnalogFmSstvDecoder.cs:2211`, `RestartableSstvDecoder.cs:763,1730`); `SstvModeRegistry` uses 43-branch if-chains instead of tables (`:958-1004`); 4 near-identical scanline decoders share copy-pasted index-walk math (extract *only* the walker, not the channel logic); channel dispatch by magic string instead of enum; `AnalogFmSstvDecoder.cs` is 7,945 lines at ~62% review-history comments — extract the narrative to `docs/`, keep only the invariant statements inline (safe, additive, do this one); `WaterfallSource` has an unsynchronized `_accumulatedCount` cross-thread read/write and no `_disposed` guard on the audio thread.
- **Application:** `ConfigurationPresetService` has 5 near-identical try/catch push blocks (collapses via Tier-0 `UpdateAsync` work); `OptionsSettingsService._loadedSettings` is now dead state; `OptionsSnapshot` mapping triplicated across `Defaults`/`LoadAsync`/`SaveAsync`; `TemplateStore.SaveAsync`/`ExportAdifFileAsync` write non-atomically (temp-file+rename fix, cheap); `ImportAdifFileAsync` does N individual transactions with no partial-failure reporting; fire-and-forget `Task.Run` work (audio auto-save encode, RxAudioAutoSaver completion) isn't tracked or drained on `DisposeAsync`.
- **Radio/CAT:** 4 near-identical `AcquireAsync`/timeout-wrapper/lazy-connect implementations across the 4 backend projects — extract a shared base, but preserve 2 real asymmetries (unbounded vs. bounded wait, Rigctld's deliberate no-lock dispose); Hamlib does one `Task.Run` per native call (up to 6 pool hops per 250ms poll) — wrap the whole method body once instead; OmniRig has zero logging anywhere (the one backend that can't be tested locally, so diagnosability matters most); `RigctldClientProtocol.ReadLineAsync` has no max line length (unbounded growth against a mis-pointed host); Hamlib connect-cleanup can leak a handle if `RigCleanup` throws before `_rig` is cleared (2-line swap).
- **UI:** `TxControlsPaneViewModel.Dispose()` doesn't dispose its CTS or unsubscribe editor event handlers; `UpdateFilteredEntries` does `Clear()`+N`Add()` per keystroke with no debounce; `TranslateBindingSource` only prunes dead handlers on a culture change (O(n²) growth over a long session with no language switch) — investigate actual growth before fixing; `TxImageEditorPaneViewModel` is a 4,140-line god object (extract `EditorUndoStack`/`TemplateVariableScanner` as separate testable collaborators, sequence after T0-11/T0-12).
- **Audio/Imaging/Logbook:** `SqliteCommand` never disposed (~17 sites, mechanical fix); no WAL mode/busy-timeout on the shared `history.db` (2 stores, documented as anticipating cross-process contention) — verify Microsoft.Data.Sqlite's actual default busy-retry before deciding this is needed; unbounded logbook/gallery result sets (add paging); QRZ HTTP calls don't check status code before XML-parsing (bad error message on a 5xx); font loading in `TransmitImagePreparer`'s DI-singleton constructor does 8 blocking file reads with no logger/fallback for optional fonts.
- **Cross-subsystem:** "exactly one factory match" reimplemented 3 times with drifted error-message wording (`RadioController.cs:287-301`, `RadioSessionService.cs:55-63,125-133`) — extract one resolver; `RxImagePaneViewModel`'s 250ms UI-thread poll reaches into the audio engine for overrun count, which the interface itself says can block on a concurrent capture-session dispose — snapshot on the drain thread instead of polling across the boundary.
- **Infra:** `IHost` is built but never started (no `IHostedService` registered today — harmless now, a trap for later); `ConfigurationPresetStore` isn't hardened against corrupt/locked files unlike its sibling `JsonSettingsStore`; atomic writes use a fixed `.tmp` name (two concurrent processes interleave) and skip an explicit flush-to-disk before rename; `FileLoggerProvider` does a `BaseStream.Length` syscall per log line under a global lock at the default `Debug` level, and a single transient write failure permanently and silently disables file logging for the rest of the session.

---

## Tier 3 — low priority / nits (batch opportunistically, no urgency)

> **Re-verified open 2026-09-07** (the rest of this tier was not individually re-checked):
> `ScanlineStudio.Core.Radio.Cat` is still an empty project, with no `.cs` file outside `obj/`.
> `ILogFileRelocator` is still registered twice (`Program.cs:148` and `:904`).
> `ISstvDecoderReconfiguration` still lives in `Core.Sstv`, not `Abstractions`.
> `MainWindow.axaml.cs` now has 35 `logger is not null` guards, up from the ~30 this audit recorded.

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


## Triage of the remaining open items (auditor pass, 2026-09-07)

**Two auditor passes ran.** The first covered this document's own Tier 2, Tier 3 and test-suite
items, below. The second consolidated every open bug and research item from the rest of the repo's
markdown into this file — that pass is summarised in "Decode-path items folded in from other
documents", immediately after this section.

Every remaining Tier 2, Tier 3 and test-suite item was re-verified against current source, then
triaged by the `auditor` subagent. **No item is a MUST FIX. No item needs a `principal` round.**
Tier 0 and Tier 1 took the real defects.

**Do these, best first:**

1. **Golden-vector worst-row metric** (TT1-2). `MeasureAveragePerChannelDelta`
   (`GoldenVectorTests.cs:966-988`) sums over the whole frame, and tolerances run 1.99 to 13.76. One
   fully corrupted row out of 256 moves the average by at most 1.0, so it passes. Add a per-row max
   in the same loop, measure the current worst row per fixture, pin at about 1.5x to 2x. Cheapest
   change with the widest blast radius.
2. **TX channel-order coverage** (TT1-5), starting with the mapping, not with captures.
   **CORRECTED 2026-09-07 — the sentence below is wrong and is kept only so the correction is
   traceable. The 11 captures are stale and their comparison is skipped; treat current coverage as
   zero. See the pick-up list item 2 and the RP-1 section.** Original text: 11 real
   legacy TX captures already exist in `Fixtures/GoldenVectors/TxCapture/`. The first action is to
   map all 43 modes to their legacy `Main.cpp` `Line*` function and capture one fixture per
   uncovered distinct function. CLAUDE.md §3 forbids assuming a sibling mode shares a covered mode's
   order, and only that mapping says whether the real gap is 5 fixtures or 30.
3. **WAL mode on `history.db`** (Tier 2). Two stores write one file. Without WAL a write blocks
   readers, and the Gallery query has no row limit, so a slow SELECT can stall a
   `ReceiveHistoryRecorder` insert. About 2 lines, and it degrades gracefully. Note: this is a
   latency fix, not a crash fix — Microsoft.Data.Sqlite already retries `SQLITE_BUSY` until the 30 s
   default `CommandTimeout`, so "no busy timeout" overstated the exposure.
4. **`SqliteCommand` disposal at the two in-loop sites only** (Tier 2):
   `SqliteReceiveHistoryStore.cs:490` and `:851`. Each iteration creates a command and re-prepares
   identical SQL, so a large reconcile accumulates N live native statements. The other 19 sites are
   harmless — the command is created on an `await using` connection that closes in the same method.
5. **Locale-key existence assertion** (TT1-7). `NoHardcodedAxamlStringsTests.cs:19` is one regex over
   `Content|Text|Header` in `.axaml` only. Enumerate every `{loc:Translate X}` key and assert it
   resolves. A typo'd key ships today as a visibly broken label that nothing catches.
6. **`MiniAudioDeviceMuteQuery` dispose-race test** (TT1-15). Its sibling `MiniAudioDeviceEnumerator`
   already has one, so the harness exists. The failure class is an access violation at shutdown.
7. **Scoped `MainWindow.axaml.cs` tests** (TT1-6). Target T1-12's `DataContextChanged` re-entry guard
   plus the two or three highest-traffic cross-pane wirings. Do not chase 1268 lines. Budget it as a
   coverage task, not a fix.
8. **Five silent-pass tests → `[SkipOnWindowsFact]`** (TT1-18). The count of 9 overstates it: two are
   required CA1416 analyzer guards and one computes an expected value. The genuine sites are
   `ApplyPendingRelocationsTests.cs:219`, `AppLocationOverridesTests.cs:47`,
   `AppLocationsServiceTests.cs:99` and `:192`, `JsonSettingsStoreTests.cs:185`.
9. **QRZ status check plus one non-OK fixture** (TT1-16). The source half is confirmed still true: no
   `EnsureSuccessStatusCode`, `IsSuccessStatusCode` or `StatusCode` anywhere in `Core.Logbook`.
   Impact is bounded — a 503 HTML page surfaces as "Data at the root level is invalid" to the
   operator. Error-message quality only.
10. **Shared factory-match helper** (Tier 2). Only observable effect is three different error strings
    for one condition. About 15 lines. Do it when already in those files.
11. **The 35 `logger is not null` guards** (Tier 3). Only worth bundling after item 7 lands tests.

**Investigate, do not build yet:**

- **Gallery filter latency** (Tier 2). The real variable is N: `QueryAsync` is called with no row
  limit (`RxHistoryPaneViewModel.cs:797`), so clearing "Show today only" loads the entire history.
  Measure `UpdateFilteredEntries()` at N = 1 000 / 5 000 / 20 000. Above about 50 ms per keystroke,
  add a 150 to 250 ms debounce — and consider a query row cap, which is the more fundamental fix.
- **Hamlib header anchoring** (TT1-12). `hamlib/` is gitignored (`.gitignore:11`), so a
  parse-and-compare test would silently not run on CI or any other machine — the same false-PASS
  pattern TT1-18 complains about. The decision that settles it: vendor a pinned `rig.h` into the test
  project, which needs a `LICENSES.md` entry per CLAUDE.md §5 since Hamlib is LGPL-2.1, or accept a
  conditional skip. Worth deciding, because a wrong `RIG_LEVEL_*` bit-flag silently mis-reads SWR,
  and the SWR auto-cutoff is a safety feature.

**Dropped, with the reason (do not re-open without new evidence):**

- ~~**T1-14 imaging convert-in/convert-out.**~~ **DROP WITHDRAWN 2026-09-07.** The reason given —
  "every remaining call site is a one-shot user action on an image of at most 640x496" — used the
  working-copy bound (`WorkingCopyScaleFactor`, `TxImageEditorPaneViewModel.cs:202`, applied at
  `:6724`). The full original is retained and is what gets cropped for final output and rotated, so
  that bound does not apply to those paths. Correct status: **unmeasured**. Measure a representative
  large source image before closing it again. The hot preview path really was fused, and that half
  stands.
- **`TxControlsPaneViewModel.Dispose()`.** No leak. `_transmitCts` is created and disposed inside the
  transmit method's own `finally` (`:1737`/`:1776`), and the VM is a DI singleton (`Program.cs:916`),
  so its subscriptions live exactly as long as the publishers.
- **`IHost` built but never started.** Zero `IHostedService` implementations exist, and the host is
  disposed via `HandleLifetimeExit` (`Program.cs:469`). Avalonia owns the lifetime. The Generic Host
  is deliberately a DI container here.
- **Per-line `double[]` in the scanline decoders.** `DecodeLine` runs a few times per second, so this
  is a few KB/s of gen0. Touching decode-path state triggers the mandatory 2-round plan plus 3-round
  code review (CLAUDE.md §7) — review cost is orders of magnitude above the benefit.
- **Four near-identical backend `AcquireAsync` implementations.** The audit itself requires two real
  asymmetries to survive any shared base. Unifying PTT-critical acquire/timeout logic across four
  backends is exactly the refactor that reintroduces a stuck transmitter.
- **Empty `Core.Radio.Cat` project.** The "or document" half is already satisfied
  (`spec/03-cat-layer.md:32`, `spec/01-architecture.md:61`). Deleting it costs edits to the `.sln`,
  2 csprojs, `coverage-thresholds.json` and 3 spec docs, to remove a slot you intend to fill.
- **`ILogFileRelocator` registered twice — FALSE POSITIVE.** The order is deliberate and correct.
  `RegisterServices` (holding the `NoneLogFileRelocator` default at `:904`) runs at `:139`, and the
  real `FileLoggerProvider` registers after it at `:148`. Last-wins therefore picks the real one.
  Both sites carry comments explaining the ordering requirement.
- **`ISstvDecoderReconfiguration` location — RATIONALE IS WRONG.** The `is` test is inherent to the
  optional-side-channel design, and its sibling `ISstvEncoderReconfiguration` lives in Abstractions
  and is pattern-matched the same way. `ScanlineStudio.Application.csproj:12` already references
  Core.Sstv, so moving the file breaks no boundary that is not already crossed.
- **`ConfigurationPresetStore` concurrent-writer test (TT1-19).** One preset is one whole file, so
  concurrent saves are correctly last-writer-wins, unlike `JsonSettingsStore`'s
  multiple-sections-in-one-file lost-update mode. One thing worth knowing: the temp path is a shared
  `path + ".tmp"` (`:317`), safe only because of the in-process semaphore plus the single-instance
  mutex. A per-save unique temp name would harden it more than any test here.

**Four claims in this document were stale.** TT0-2, TT1-9, TT1-10 and TT1-11 are all already tested,
at the layer that owns the guarantee. `ILogFileRelocator` and `ConfigurationPresetStore` hardening
were also already fixed. Assume the same of any remaining unverified Tier 2 or Tier 3 line before
spending on it.

---

## Decode-path items folded in from other documents (2026-09-07)

`production_audit.md` is now the ONLY backlog. Every other document records measurements, decisions
and history — none of them carries a work list. 14 candidates were verified present in current source
and triaged. Three survived and are in the pick-up list at the top as items 0a, 0b and 0c.

**Dropped, with the reason — do not re-open without new evidence:**

- **rm8 colour tilt — RETRACTED, measurement artifact.** RM8 is `CreateMonoAveragedMode`
  (`SstvModeRegistry.cs:602`), monochrome, one `"Y"` segment, so a decode is R = G = B by construction
  and cannot carry a channel-dependent tilt. Comparing a grey decode against a colour source produces
  exactly the shape that was recorded, invariant across demodulators. Retraction written into
  `docs/known-decode-defects.md` §2 with its falsifier.
- **PLL collapse at wide output cutoffs.** Unreachable in shipped configuration. The one thing that
  had to survive is a constraint, not a task: a future output-cutoff control must range-limit per
  demodulator. That is recorded in `docs/known-decode-defects.md` §3.
- **VIS header fixed origin, 0-185 ms band** (`decoder_quality_improvement.md` §15 item 4). Reachable,
  but the surviving residue after the anchor fold is an integer vertical shift of 1 to 3 lines out of
  128 to 256. No operator notices that. The only visually real part is the unfolded AVT sub-line case,
  and AVT is extinct.
- **Mid-image narrow-restart stale filter cache** (§15 item 7). Needs an MN/MC transmission
  interrupting an in-progress wide-mode image, affects about one line, and the fix is a
  checkpoint/rewind mechanism for two stateful filter classes. One rider: if item 0b's cause turns out
  to live in the narrow filter or mapping path, revisit this as part of that work, not on its own.
- **AVT training runs H2 instead of H1** (§15 item 1). Still present
  (`AnalogFmSstvDecoder.cs:1205-1211`). AVT is extinct on the air.
- **Second `m_SyncAccuracyN` refresh trigger** (§15 item 2's residue). Unreachable by construction —
  legacy gates it on `m_SyncAccuracy == 2`, an option this port never ported. Its practical effect is
  largely covered: this port replays on every committed correction (`:604-606`). The missing
  `docs/removed-features.md` entry that CLAUDE.md §2 requires has now been written.
- **§15 items 6, 8, 13, 14, 15.** No decode effect, no reception win, or bounded with no constructible
  failure and already deliberately deprioritized.
- **CW-ID window dropped during a paused file decode.** Premise unreachable: `DecodeFromFileAsync`
  throws on a paused start (`SstvSessionService.cs:2429-2432`), and `SetAutoDetectPaused(true)` drops
  the arm anyway (`:500`). The once-proposed `_fileDecodeInFlight` gate would be safe but would buy
  nothing.
- **CW decoder robustness at high WPM.** `MinDotMs = 22.0` is derived, not guessed
  (`ClassicalCwDecoder.cs:42-47`), and a clean 50 WPM decode is already pinned
  (`ClassicalCwDecoderTests.cs:42-63`). The only hole is 50 WPM under noise, where the failure is
  "no CW ID reported" on a rare fast ID. If anyone edits that file, add one noisy 50 WPM
  `[InlineData]` row. Not a backlog item.
- **Options "Currently using" field bug.** Carried for weeks with no symptom and no repro, and the
  code it pointed at was rewritten by the 2026-08-27 live-apply change. Deleted. A real user report
  would be a better starting point than this note ever was.
- **`spec/14-roadmap.md`'s two deferral tables.** Not worth re-verifying row by row: 7 of 8
  spot-checked rows were stale or already tracked here. Both tables now carry a
  "historical record, not an open backlog" banner.

**Deliberately NOT folded in.** `spec/06`, `spec/07`, `spec/15`, `spec/17`, `spec/19` and `spec/03`
carry feature-scope deferrals, not defects — manual clock calibration, the drag-the-slant tool,
VOX-mode ID variants, unwired telemetry fields, `TemplateCatProtocol` fallback. Those belong in the
roadmap. `docs/functional-audit-playbook.md` holds scattered deferred nits across 8000 lines, each
already reasoned harmless where it sits; extracting them is a large job with a low hit rate.

---

## External audit folded in — `astra-audit.md`, verified 2026-09-07

**Provenance.** `astra-audit.md` (repo root, gitignored, produced by a third party against commit
`7b14b15`) lists 42 findings: 1 at P1, 36 at P2, 4 at P3, 1 withdrawn by its own author. All 37 at
P1/P2 were checked against current source — 13 by me directly, 24 across three `auditor` passes split
by subsystem.

**Headline result: zero false positives in 37.** Every premise held. For an external audit of a
75,000-line codebase that is unusually clean, and it is the reason the items below are folded in
rather than re-litigated. Where I disagree with the audit, I disagree about **priority and scope**,
never about whether the defect exists.

**What "zero false positives" means, per their RP-1 correction, accepted:** source corroboration of
the listed mechanisms — not 37 runtime-confirmed active bugs. Two of the 37 are accepted limitations,
so their own active count is 35 at P1/P2 plus 4 untriaged at P3. Nothing here was reproduced against
running hardware; every verdict on both sides is a source trace.

### RP-1 — the rest of their reconciliation, recorded in full

The three reversals and six disputes below are the sharp end. Their reconciliation also carried
material that is not a dispute at all but should not be lost. All of it is theirs unless marked.

**Corrections to claims WE made that are stale or wrong:**

- **ADIF partial-failure reporting — our claim is stale.** The whole file is parsed before any insert
  (`LogbookSessionService:146-164`), so a later malformed record cannot leave earlier records
  committed, and the UI does warn through `ImportPartial` (`LogbookPaneViewModel:891-905`,
  `en.json:976`). Transaction batching remains a separate choice.
- **Logbook offset sorting — our reasoning was wrong even though the item is right.** This document
  inferred normalization from the column NAME `StartUtc`. The column stores offset-bearing
  `ToString("O")` text (`SqliteLogbookRepository:123,129,132,226-227`). The receive-history migration
  did not fix this separate store.
- **`TT1-15` — a missing test is not a proven crash.** We wrote that it "guards a native crash". The
  Linux mute path uses the same context mutex as teardown (`native/scanline_audio.c:1502-1542`,
  `:134-142`), Windows uses per-call COM resources, and macOS does not touch that shared context. The
  test is still worth adding; the asserted access violation is not established.
- **Hamlib cleanup leak — unproven.** A throwing fake does not show the production C path leaks; the
  real adapter calls a C function returning an int. Defensive handling is still reasonable.
- **`TT1-4` "zero concurrency tests" — false.** `WaterfallSourceTests:248-303` already covers a
  throwing first subscriber and stale-frame prevention. Only broader reconfiguration/swap coverage is
  missing.
- **`TT1-2` — our framing overreaches slightly.** A single maximally wrong row in an otherwise perfect
  256-row frame moves the mean by at most `255/256`, which is under the tolerances. That does not
  prove corrupting any row of every already-imperfect fixture always passes. Add spatial and
  worst-row metrics anyway.
- **CI coverage gate — it IS wired** (`.github/workflows/ci.yml:53-121`). Absent per-backend
  thresholds are a scope question, not proof the gate is unrun.
- **`TT1-13` is not closed by the phase-1 plan.** It still relies on a 150 ms fake delay and two 20 ms
  waits (`HamlibRadioProtocolTests:393-427`). Deterministic gates would prove the ordering.

**They confirm several of our Tier 2/Tier 3 drops as correct and deliberate:** Generic Host as a DI
container with no hosted services, the documented empty CAT project, the duplicate log-relocator
registration that deliberately leaves the real provider last, the TX CTS disposed in its `finally`,
and singleton subscriptions that are not leaks. Backend acquire/timeout asymmetries must not be
erased merely to deduplicate.

**Hardening they raise that this document never listed** — none is a demonstrated failure, all are
worth knowing:

- **No byte-length ceiling on rigctld line reads** (`RigctldClientProtocol:538-551`). A mispointed
  server is relevant because the endpoint is user-configurable, though request and transport timeouts
  bound the time. Distinct from `ASTRA-003`.
- **Cross-process fixed temp names**, particularly when the single-instance guard is bypassed
  (`Program:586-601`). Unique temp names do not solve shared-document lost updates, and durable
  flushing is a separate power-loss guarantee.
- **QRZ upload logs the full response body on failure** (`QrzLogbookUploader:58,126-127`) and rotation
  happens after writing. Bounded diagnostics are worthwhile.
- **Eight synchronous bundled-font loads** at `TransmitImagePreparer:34-58`, a startup-cost and
  fallback-policy question.
- **Translation handler retention** (`TranslateExtension:67-95`) keeps small weak closures until a
  culture change. Some growth is real; a visible problem was not established.
- **Per-line logger cost**, `HamlibProtocolFactoryTests:146-192` raising the process-wide minimum
  thread count without restoring it, and `ApplyPendingRelocationsTests:172`'s 1200 ms delay.
- **Unpromoted:** `PttSafetyCoordinator:297-305` checks an epoch before separately clearing flags,
  which can interleave. Their own note, not promoted to a finding.

**Their four P3 items, which nobody has triaged** — listing them so "not triaged" is checkable:
`ASTRA-013` native stopped-flag read/clear can lose a notification; `ASTRA-033` a transient log-file
error permanently disables the provider silently; `ASTRA-034` QRZ HTTP failures are reported as
response-format errors; `ASTRA-035` preset-name validation accepts Windows device names.

**Their process critique of this document, which I accept.** Their omissions table argues that six
active findings plus two accepted limitations were missing here because files were marked "reviewed"
while concrete failure paths went unrecorded — coverage of source was complete, coverage of defects
was not. Their proposed method, worth adopting: keep a claim ledger with an evidence-backed
disposition per claim; apply one failure-scenario matrix to every boundary (partial writes, malformed
input, external failure status, cancellation, overlapping requests, disposal, shutdown); verify
comments and historical decisions against current callers; judge test evidence quality (real
production path, independent oracle, fixture provenance, skipped execution, metrics that mask
localized corruption); and report source coverage separately from defect coverage.

### RP-1 exchange — where they said "this is a defect" and we had said otherwise

**This is the part that mattered.** The severity arguments changed nothing we do. These three did:
each is a place where we **closed or dismissed something**, they said it is still a real problem, and
**I checked and they are right.** All three are now reopened.

**1. PLL collapse at a wide output cutoff — we closed it on a FALSE premise. Reopened.**
On 2026-09-07 we wrote into `docs/known-decode-defects.md` §3 "Not reachable in shipped configuration
— `pllOutputCutoffHz` defaults to 900 Hz and no UI exposes it", and added "Triaged 2026-09-07: no
work item." **That claim is wrong.** Verified: `OptionsWindowView.axaml:986` is a
`NumericUpDown` bound to `PllOutputCutoffHz` with `Minimum="1" Maximum="3900"`, and
`OptionsWindowViewModel.cs:3413` applies it live through `RequestPllTuning`. The demodulator's own
clamp is `Math.Clamp(cutoffHz, 1.0, _sampleRate * 0.45)`, which at 11025 Hz permits 4961 Hz — so 3600
passes. **A user can reach, from the Options UI, a setting that made the picture break into a
herringbone pattern on a clean signal.** Their qualification is fair and adopted: the historical
sweep's own experiment code was not located, so which demodulator cutoff it changed is unconfirmed,
and advanced tuning may legitimately permit poor combinations. That argues for a range or a warning,
not for closing it.

**2. The 11 TX golden captures do not provide the coverage we claimed. Pick-up item 2 corrected.**
We wrote that "11 real legacy TX captures already exist" and used that to say channel-order coverage
is better than `TT1-5` stated. **Verified false.** All 11 `TxCapture/*.provenance` files contain
exactly `UNKNOWN-STALE-PENDING-RECAPTURE` — 11 of 11 — and `StaleFixtureTheoryAttribute` **skips**
the comparison entirely. That attribute's own skip message is the warning we should have read: the
round-1 version hashed the current `.mmv` against itself, "producing a permanently-green,
permanently-lying result". The `.mmv` files are this port's own TX output, not legacy-generated
audio. **So the Scottie-class guard we believed we had does not currently run.** This makes `TT1-5`
more urgent than we filed it, not less, and it does not change the recommended first step — map the
43 modes to distinct legacy TX paths — it only removes the false comfort attached to it.

**3. T1-14 (cold imaging conversions) — dismissed on a wrong size premise. Reopened as unmeasured.**
We dropped it because "every remaining call site is a one-shot user action on an image of at most
640x496, sub-millisecond". That bound is `WorkingCopyScaleFactor`, which caps the **working copy**
only (`TxImageEditorPaneViewModel.cs:202`, applied at `:6724`). They point out the full original is
retained and is what gets cropped for final output and rotated. So the size premise behind our
dismissal does not hold. Correct disposition: **unmeasured, not dismissed** — measure a
representative large source image before closing it again.

**One wording correction I accept for three items we dropped.** We dropped the AVT H1/H2 gap, the VIS
fixed-origin 0-185 ms band, and the mid-image narrow-restart cache. Their objection is that "AVT is
extinct" and "no operator notices" are **prioritization, not correctness evidence**, and our text
should not read as though the defects were disproved. Agreed. All three remain source-proven parity
defects that we are choosing not to fix. That is a different statement from "not a bug", and the
drop entries below should be read that way.

**One internal inconsistency they caught in this document, now fixed.** The status banner said
"Test-suite Tier 0 — 6 of 7 done, only TT0-2 is open" while the TT0-2 entry itself had already been
corrected to DONE in the same pass. Their broader point stands: treat this document as evidence to
verify, not as an authoritative current count.

### RP-1 exchange, 2026-09-07 — what we agreed after pushback

The Astra author responded to the section below with a rebuttal pass, recorded in `astra-audit.md`
under **"External-feedback review pass RP-1"**. Everything in this subsection comes from that
exchange. It is here so a reader can see which of our positions survived contact and which did not.

**The useful result first: the exchange changed almost nothing about what to fix.** Of six
disputes, five are arguments about wording or severity on items BOTH sides agree are real and belong
on the fix list. Only one — `ASTRA-003` — could change what we do, because it decides whether that
item blocks a release.

**We were wrong, corrected here:**

- **`ASTRA-026` — we withdraw "overstated".** Our argument was that both apply paths are modal
  dialogs, so a transmission cannot start while one is open. That misread the trigger. Their order is
  the reverse: footer preparation starts FIRST, then Options is opened during it (`MainViewModel`
  permits this, and Save is not TX-gated). Modality never applied. **Reachability stands as they
  filed it.**
- **`ASTRA-027` — we withdraw "no visible design lost".** Our conclusion assumed the whole quad is
  sub-pixel. Only ONE edge has to fail the minimum-edge check, so a quad tapering from about 0.288 px
  to 5.44 px across 128 px is rejected by the preview while being plainly visible. Their
  counterexample defeats our framing. Our terminology correction (working-copy width is about 640 px,
  not 1920 px) was adopted by them and stands.
- **`ASTRA-041` — we withdraw "Windows-only".** Our own auditor hedged that word ("essentially
  Windows-only"), which is not a finding. The honest trigger is **failed image deletion with a
  readable directory and a writable database**, whatever the platform.
- **`ASTRA-040` — we withdraw one sentence, not the priority.** We wrote that its original P2 rating
  came from mentally grouping it with the accepted limitations. We could not know that, and they say
  it reflected conditional shutdown evidence. The claim was unsupported and is removed. **The raise to
  P1 stands on consequence alone** — a transmitter left keyed on air — which is how it was argued
  anyway, and they accepted that raise.
- **`ASTRA-042`, the composition claim — half wrong.** We wrote "fix either one and the chain
  breaks". They are right that a deletion-tombstone fix on `ASTRA-041` would NOT stop `ASTRA-042`'s
  orphan, because that orphan was never deleted and carries no deletion intent to record. The correct
  statement is narrower: **fix `ASTRA-042` and the chain breaks.** Fixing 041 alone does not.

**Where we were talking past each other, now settled by wording:**

- **`ASTRA-029` — adjudicated: each side right about a different proposition.** We are right that the
  consequence is genuinely reachable, not merely a failed `File.Create`: writer A releases its
  exclusive handle at the end of its `await using` block **before** its `File.Move`, so writer B's
  `File.Create` landing in that gap gets the pathname and POSIX `rename()` lets A publish B's
  incomplete file. `LoadForBootstrap` then swallows every exception type and returns `Empty`, and
  because every caller does load-modify-save over the whole record, one `Empty` read plus any later
  Apply permanently drops the other two overrides. They are right that it is **conditional, not
  entailed** — it needs POSIX rename semantics (on Windows A's `File.Move` fails on B's open handle),
  plus B's create landing in a narrow same-continuation window, plus B's write failing, or else B's
  completed JSON lands at the real path and the file self-heals. Our "both follow from its own
  window" asserted entailment and was overstated. Wording sharpened below.
  **New, from the adjudication, described in neither document:** in that same interleaving, writer B
  throws `FileNotFoundException` from its own `File.Move` because its temp was renamed away, and
  **rolls back its relocation while its setting is actually persisted** — leaving the app and the
  file disagreeing. Worth folding into whatever fix `ASTRA-029` gets.
- **`ASTRA-042`, the acceptance question.** They say the recorded scope cut covers audio only, so
  image and history loss needs its own explicit decision. That is what this document already said —
  nothing on paper covers it. Our "covered in spirit" line was a recommendation to the user, not a
  claim about the record. **Reframed: this is a project decision to make, not an acceptance to
  inherit.**

**Their corrections to our framing, accepted:**

- "Zero false positives in 37" means **source corroboration of the listed mechanisms**, not 37 active
  runtime-confirmed bugs. Their own count is 35 active P1/P2 plus 4 untriaged P3, because two of the
  37 are accepted limitations.
- **`ASTRA-030` was independent verification, not independent discovery.** It reached their report
  through an earlier reconciliation with this document. Agreement between two source reads is weaker
  evidence than we implied, and neither substitutes for a legacy-captured interference waveform.
- **`ASTRA-020`, `-021` and `-025` already described** cancellation-when-clean, multiple text styles
  with stale Redo, and stale gesture state respectively. Our added detail is useful, but should not
  be credited as mechanisms they missed.

**The one dispute we win — `ASTRA-003` stays P1. Adjudicated against all three rebuttal points.**

Their rebuttal was that recovery is possible: transport failures can rebuild the protocol, timeouts
remain possible, and different responses or capabilities change the sequence. **Each fails on source.**

1. **"Different responses or capabilities change the sequence" — no.** Every read in the poll path is
   paired with its own write: `GetSingleLineOrThrowAsync` is 1 write and 1 read
   (`RigctldClientProtocol.cs:507-513`), `TryGetMeterAsync` 1 and 1 (`:438-441`), `GetModeAsync` 1
   write and 2 reads matching rigctld's 2-line `m` response (`:492-497`). Lines read per poll always
   equals lines produced per poll, for **every** capability combination, so the one-line offset is
   invariant. It does not drift back into alignment.
2. **"Timeouts remain possible" — not from this defect.** Over-reading is only possible at
   `GetModeAsync`'s second read, and `ThrowIfErrorLine` (`:496`) aborts before it whenever a one-line
   `RPRT` lands in the mode slot. Reads never block, so `WithRequestTimeoutAsync`'s `TimeoutException`
   (`:334-347`) — the only transport-class exception that reaches the rebuild — is never produced by
   the desync itself. A timeout requires an unrelated stall.
3. **"Transport failures can rebuild the protocol" — they can, but this defect never raises one.**
   When the desync does throw (the meters-enabled variant, where a meter float lands in the frequency
   slot and `long.TryParse` fails, `:120-123`), it is a `RadioProtocolException`.
   `RadioController.cs:431-458` resets backoff, publishes `CommandFailed`, and continues — it never
   disposes the protocol. Disposal and reassignment happen only in the generic transport branch
   (`:459-475`), and `EnsureConnectedAsync` short-circuits on `_transport.IsOpen` (`:302-307`), so the
   socket is never rebuilt. On a `CommandFailed` poll no state is published at all, so the last
   published `IsTransmitting = true` simply persists.

**It is also worse than a stuck indicator.** `state.IsTransmitting` gates the SWR cutoff evaluation
(`TxControlsPaneViewModel.cs:705,768`), and the desynced frequency reads as `0`, which is what gets
persisted into history and QSO rows (`ReceiveHistoryRecorder.cs:450`). So the defect corrupts logged
data and feeds a safety cutoff, not just a UI light. The trigger is user-reachable from the bandwidth
control (`RadioStatusViewModel.cs:938-945`).

**Escalation judged unnecessary** — the verdict rests on read source, not inference. If you want
belt-and-braces before shipping the P1, the scripted multi-poll test **they themselves proposed**
settles it empirically in about an hour, and I would take that offer.

**Both sides already agree on the fix regardless of rating:** consume the passband before validating
the mode token, and invalidate the connection when a transaction cannot be fully consumed.

### Priority corrections, with reasons

The audit filed one P1. **Two more belong there**, and they are now items 00b and 00c in the pick-up
list above.

- **`ASTRA-040` P2 → P1 — agreed by both sides in RP-1.** It leaves a transmitter keyed on air at
  exit. No acceptance for it exists in `docs/` — an auditor searched. (An earlier version of this
  line asserted why the original P2 rating was chosen. That assertion was unsupported and is
  withdrawn; the priority rests on consequence.)
- **`ASTRA-003` P2 → P1 — disputed by them, ADJUDICATED IN OUR FAVOUR 2026-09-07.** All three of
  their rebuttal points fail on source: the read/write pairing makes the one-line offset invariant
  across every capability combination, no starvation means no timeout, and the exception the desync
  does raise is command-level, which never disposes the protocol. `IsTransmitting` therefore latches
  true and stays published. It also gates the SWR cutoff and corrupts the frequency written into
  history rows. Full proof in "RP-1 exchange" above. **The fix is agreed regardless of rating.**

### Seven items are UNDERSTATED — worse than their own text says

- **`ASTRA-003`** — permanent and silent, not one bad response. See 00c above.
- **`ASTRA-029` (storage-location Apply race)** — the item disclaims lost-update and corruption, but
  both are reachable through its own window. The temp filename is fixed and shared, so one
  operation's `File.Move` **can** publish another's half-written temp. If that happens, the loader
  swallows the JSON error and returns empty, discarding all three overrides at once — which defeats
  the atomicity guarantee `SaveAsync`'s own doc comment claims. **Conditional on losing the race, not
  inevitable** (wording tightened in RP-1 at their request; the mechanism itself was never disputed).
  Confirmed separately: T0-2's `UpdateAsync` serialization covers `settings.json` only and does not
  reach this file.
- **`ASTRA-010` (auto-follow clobbers quick-mode slots)** — not a one-time loss. The only other
  writer is the reassign path, so **every** launch with auto-follow enabled re-clobbers the
  operator's assignments. Custom quick-mode buttons can never survive a restart while that flag is
  on.
- **`ASTRA-018` (late radio test marks an edited endpoint tested)** — six unconditional publish
  sites, not three. The two the item missed are the **Hamlib and flrig PTT tests**, which feed the
  keying half of the gate. A late result can also silently undo a Reset-to-defaults.
- **`ASTRA-015` (radial gradient preview)** — for text, preview and transmitted output resolve their
  brushes against different rectangles, so Horizontal and Vertical gradients are wrong too, with no
  non-square box needed. Second miss in the same file: bitmap-pattern fill tiles in canvas-display
  pixels against the output's output-pixels, so tile density drifts with zoom and crop.
- **`ASTRA-021` (text edits bypass undo)** — every text style except font size and solid colour, not
  just Bold: stroke, shadow, stack, gradient, bitmap fill, rotation. Boxes got their hooks wired and
  text did not. The redo half is the damaging one — a post-Undo text edit leaves a stale redo branch
  live, and one Redo click silently discards work.
- **`ASTRA-025` (cancelled placement drag undoes the preceding edit)** — a second trigger needing no
  drag movement at all, because the gesture flag is never cleared on pointer release. The in-code
  comment asserting the flag "is always false for this mode" is simply wrong.

### Items we called OVERSTATED — three withdrawn in RP-1, one survives

We originally narrowed four items. After their rebuttal, **only one narrowing stands.**

- **`ASTRA-011` (export overwrites a different file) — narrowing SURVIVES, partly.** The GTK case is
  demonstrated and the file's own doc records that Win32 rewrites the path itself. Their
  qualification, accepted: treat non-GTK platforms as **unverified**, not as excluded. macOS was
  never checked in either direction.
- **`ASTRA-027` — narrowing WITHDRAWN.** Only one edge must fail the check, so a visibly tapering
  quad is rejected. Our "sub-pixel hairline" reasoning does not hold. Our terminology correction
  (working-copy width about 640 px, not 1920 px) was adopted by them and stands.
- **`ASTRA-026` — narrowing WITHDRAWN.** We misread the trigger order. Preparation starts before
  Options is opened, so modality never applied.
- **`ASTRA-041` — narrowing WITHDRAWN.** "Windows-class delete failure" was a hedge, not a finding.
  The trigger is a failed image deletion, on any platform.

The reasoning for each withdrawal is in "RP-1 exchange" above.

### Corroboration worth recording

**`ASTRA-030` is the same `m_sint1` defect this project's own auditor reached today from different
evidence**, and it is already pick-up item 0c. Two independent passes converging on one narrow
decode-path gap is the strongest signal on either list.

### One composition nobody filed — corrected in RP-1

An interrupted save under **`ASTRA-042`** leaves a truncated orphan PNG that **`ASTRA-041`**'s disk
reconciliation then imports as a new Gallery entry. Each item is individually narrow. Chained, they
produce a corrupt entry the operator never created.

**Correction:** our original "fix either one and the chain breaks" was wrong. A deletion-tombstone
fix on `ASTRA-041` would NOT stop this, because the truncated orphan was never deleted and carries no
deletion intent to record. **Fix `ASTRA-042` and the chain breaks.** They adopted the composition and
rejected the "either" claim, correctly. Their suggested belt-and-braces addition: publish through a
temporary filename and validate image completeness at reconciliation, so a truncated file is
quarantined rather than imported.

### Disposition of all 37

**Fold into the work list (see the pick-up list above for the top three):** `ASTRA-020`, `-040`,
`-003` as P1. Then, roughly by value: `-021`, `-022`, `-023`, `-024`, `-025` (TX editor, UI-tier
review, several near-one-liners), `-016`, `-018`, `-029`, `-010`, `-009`, `-012`, `-017`, `-019`,
`-039`, `-031`, `-008`, `-006`, `-001`, `-002`, `-032`, `-005`, `-015` (radial half only).

**Already tracked here:** `ASTRA-030` = pick-up item 0c.

**Accepted limitations, not new bugs — the audit says so itself:** `ASTRA-036` (a UI-blocking
tradeoff documented in `MiniAudioEngine.cs`, not a shutdown-drain item) and `ASTRA-037` (user-approved
v1 scope cut in `docs/plans/step12-auto-save-rx-audio-plan.md`).

**`ASTRA-042` needs your decision, and I withdraw the recommendation I attached to it.** The recorded
scope cut is audio-only, so nothing on paper covers image and history loss. I previously suggested
treating it as covered in spirit. They rejected that, and they are right that it is not an acceptance
anyone can inherit — it is a separate call to make. Two things to weigh: the window is roughly the
same as the audio one you already accepted, but this item also feeds the truncated-orphan composition
above, which the audio one does not.

**Low priority, real:** `ASTRA-011`, `-038`, `-041`, `-014`, `-027`, `-026`, `-028`, `-004`.

**Two open questions from the verification itself, both cheap to settle:**

1. `ASTRA-038` rests on System.Text.Json assigning an explicit JSON `null` over a `= new()` property
   initializer under source generation. A five-line deserialize test settles it definitively.
2. `ASTRA-028`'s severity turns on whether `_levelAgcProcessedUpTo` can sit persistently ahead of
   `_consumedSamples`. If it can, channel 0 finishes up to a line-time early and the missed capture
   becomes routine rather than rare — which would move that item to UNDERSTATED.

**Not triaged:** the 4 P3 items (`ASTRA-013`, `-033`, `-034`, `-035`) and `ASTRA-007`, which its own
author withdrew after checking the view. Nobody has checked the P3 four.

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

> **Superseded 2026-09-07 — historical.** Every item in this order shipped. The live order is "What
> to pick up next" near the top of this file.

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
and committed. Each closed item is marked `DONE` below. **Re-verified 2026-09-07: TT0-1 is closed
too** — `FakeSettingsStore` now enforces real mutual exclusion and carries `SaveGate` and
`LockAcquiredSignal` hooks, both tagged `T0-2` in its own doc comments. **TT0-2 is also closed** — the hang IS
tested, at the layer that owns the guarantee: `RadioSessionServiceTests.cs:341`
(`TestPttAsync_UnkeyAttempt1Hangs_Attempts2And3StillRunWithinABound`) drives a real hang via
`FakeRadioProtocol.HangOnCallNumber` and an injected `unkeyAttemptWaitTimeoutForTests`. The retry
loop lives in `RadioSessionService`, not in the Hamlib backend, so `FakeHamlibNative.CallDelay` was
never the right instrument. **Still open**: TT1-13 (item
9, deliberately deferred to its own future plan — concurrency work with unresolved design questions,
see `docs/plans/test-suite-fixes-phase1-plan.md`'s own item 9 section for why). Everything else in
Tier 2/3 below is still open, untouched.

## Test-suite Tier 0 — blockers (ship gates, either standalone or as a fix's regression test)

### TT0-1 `DONE` (closed alongside T0-2, verified 2026-09-07). `FakeSettingsStore` cannot express the settings read-modify-write race (T0-2's blind spot)
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

### TT0-2 `DONE` — **premise stale, corrected 2026-09-07**. The Hamlib PTT un-key retry is tested only against throws, never a hang (T0-1's blind spot)
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
| TT1-2 — **OPEN**, re-verified 2026-09-07 — no worst-row or outlier metric exists in `GoldenVectorTests.cs` | Core.Sstv | Golden-vector image comparison is average-delta-only across the whole frame — a single fully-corrupted scanline (960 samples at max delta) still passes every fixture's tolerance. Add worst-row and outlier-fraction metrics alongside the existing average |
| TT1-3 `DONE` | Core.Sstv | `NoiseRobustnessTests.cs:62-123` computes a noise-floor metric across a 10-level SNR sweep and **never asserts on it** — a DSP regression that halves usable SNR range is green. One-line fix: pin the measured current value |
| TT1-4 **PARTLY STALE (2026-09-07)** — `DecoderSubscriberFailureTests.cs` exists and 5 Core.Sstv test files use real threading. The gap is narrower than "zero" | Core.Sstv | Zero concurrency tests in 26,300 lines — the source audit's own concurrency findings (T0-7 decoder-swap lock, T1-2 waterfall `Subject`) will ship with no regression gate. Add throwing/slow-subscriber tests to `WaterfallSourceTests` (mirroring `DecoderSubscriberFailureTests`'s existing pattern) and a concurrent-reader-during-swap test to `RestartableSstvDecoderTests` |
| TT1-5 **OPEN, smaller than stated (2026-09-07)** — 11 real legacy TX captures exist in `Fixtures/GoldenVectors/TxCapture/` (avt, martin-m1, mn110, mr73, pd90, r24, rm8, robot-36, robot-72, scottie-dx, scottie-s1), and TX captures are what pin channel order | Core.Sstv | Channel *order* (the literal Scottie-incident failure mode) is pinned by golden vectors for only 11 of 43 modes; the other 32 rest on duration-sum + self-round-trip, which CLAUDE.md itself says is insufficient. Add a per-mode segment-order table transcribed from legacy `Main.cpp` `Line*` functions — mechanical, ~43 entries, zero design risk, biggest single structural gap found |
| TT1-6 | UI | `MainWindow.axaml.cs`'s ~780 lines of cross-VM wiring (where T1-12's `DataContextChanged` re-entry guard lives) has zero behavioral coverage — 6 separate test files disclaim the same seam explicitly. Extract to a testable `WireOnce(...)` before testing; don't try to test it through a real headless `Window` |
| TT1-7 | UI | The no-hardcoded-strings guard (`NoHardcodedAxamlStringsTests.cs`) covers `.axaml` only, 3 attributes, and doesn't verify `{loc:Translate}`/`GetString` keys actually resolve against `en.json` — confirmed live `.cs` violations (T1-13) would not be caught. Add a `.cs`-literal rule and a key-existence check |
| TT1-8 `DONE` | UI | Two tests claiming to prove off-UI-thread event marshaling (`PaneViewModelTests.cs:109-129,1681-1703`) raise the event from the same headless UI thread the assertion runs on — **cannot fail** if the marshaling were deleted. Raise from `Task.Run` instead and assert `Dispatcher.UIThread.CheckAccess()` inside the handler |
| TT1-9 `DONE` — **premise stale, 2026-09-07**: not untested, only untested in `Application.Tests`. `OptionsWindowViewModelTests.cs` builds the real `OptionsSettingsService` at 194 sites against a real store | Application | `OptionsSettingsService` (8 of the 16 Application-layer settings RMW sites) has zero tests in this project. Add a full round-trip test and a `Defaults()` vs. `LoadAsync(empty)` equivalence test — the latter catches the whole `OptionsSnapshot` triplication-drift class (Tier-2 source finding) in one assertion |
| TT1-10 `DONE` (2026-09-07) — `LogQsoAsync_PostPersistStepThrows_...` asserts `PostPersistError == "network unreachable"`; the reason is surfaced | Application | `LogQsoAsync`'s post-persist-failure test (`LogbookSessionServiceTests.cs:72-91`) asserts the record survives but nothing about the failure being *reported* — currently ratifies the lossy behavior T1-7 flags as a bug |
| TT1-11 `DONE` — **premise stale, 2026-09-07**: `RadioControllerTests.cs:102` drives the Connect-versus-Dispose race deterministically against the real `_lifecycleLock`, where T1-8's guarantee lives | Radio/CAT | Zero test coverage for lifecycle-call serialization (T1-8) in either direction — neither the undefined-behavior half (`Connect`/`Disconnect` racing) nor the claimed-safe half (`SetPttAsync` racing `Disconnect`). Add a characterization test (pin current behavior, not desired) so T1-8's eventual lock has a red/green signal instead of a paper argument |
| TT1-12 | Radio/CAT | No test is anchored to the real `hamlib/include/hamlib/rig.h` header — every P/Invoke constant is hand-typed, comment-verified only. Add a `HamlibNativeLayoutTests` with `Marshal.SizeOf`/offset assertions on the `value_t` union (catches a silently-reinterpreted-meter-reading class of bug with no compiler-catchable signal today) plus a table-driven constant-vs-citation test |
| TT1-13 `DEFERRED` — own future plan | Radio/CAT | `HamlibRadioProtocolTests.cs:393-427` sequences a 3-way dispose/PTT/poll race using `CallDelay` + two bare `Task.Delay(20)` calls — assumed ordering, not enforced; this is the regression gate for a physically-keyed-transmitter-on-disposed-handle bug and is a real CI flake risk. Replace with explicit gates per this project's own "deterministic gates, not shared race" rule |
| TT1-14 `DONE` | Audio/Imaging/Logbook | Closed alongside T1-16 (2026-08-31). `SqliteReceiveHistoryStore`'s own mixed-offset ordering bug is fixed; the pinned-bug test is renamed (`QueryAsync_DateRangeCompareIsInstantBased_NotLexicographicOnStoredOffset`) and flipped to assert the correct result, plus a new dedicated real-DST-transition test and 3 migration/backfill tests (including a self-healing-after-a-NULL-row test, added during code-review). `SqliteLogbookRepository` was checked and does NOT share this bug class — its own `StartUtc` column is already UTC, not local-offset (`ORDER BY StartUtc DESC`) |
| TT1-15 — **OPEN**, re-verified 2026-09-07 — no dispose-race test for the mute query | Audio/Imaging/Logbook | `MiniAudioDeviceMuteQuery` has one happy-path test and no dispose-race test, while its sibling `MiniAudioDeviceEnumerator` has exactly the missing test (`DisposeAsync_RacingConcurrentRefreshAsync`). Copy it verbatim — cheap, closes a native-context-use-after-release class of bug (a crash, not a wrong value) |
| TT1-16 — **OPEN**, re-verified 2026-09-07 — no non-OK `HttpStatusCode` fixture in `Core.Logbook.Tests` | Audio/Imaging/Logbook | Every QRZ HTTP test fixture is `HttpStatusCode.OK` — zero `EnsureSuccessStatusCode`/status-code handling anywhere in source or tests. A 5xx currently surfaces as a nonsense XML-parse error instead of "QRZ is down." Cheap `[Theory]` addition, the `FakeHttpMessageHandler.ResponseFactory` seam already supports it |
| TT1-17 `DONE` | Infra | Fixed shared paths in `JsonSettingsStoreTests.cs:102,121,156` (`/tmp/relocated`, `/tmp/fresh-install-target`, `/tmp/conflict`) with inline cleanup that's **skipped on any assertion failure** — one failed run permanently poisons every subsequent run on that machine. Two-line fix: derive from the per-test temp subdirectory |
| TT1-18 — **OPEN**, re-verified 2026-09-07 — 9 `OperatingSystem.IsWindows()` early-returns remain | Infra | 4 tests silently `return` (report **passed**, not skipped) on Windows instead of asserting anything, relying on `File.SetUnixFileMode` denial — which root ignores, so if the Linux CI leg runs as root these are vacuous everywhere, not just on Windows. Investigate CI's user first, then gate with a real skip attribute |
| TT1-19 `DROP` (2026-09-07) — `JsonSettingsStore` is covered. `ConfigurationPresetStore` needs no equivalent: every method runs under its own `SemaphoreSlim`, and one preset is one whole file, so concurrent saves are correctly last-writer-wins | Infra | No concurrent-writer test for `JsonSettingsStore`/`ConfigurationPresetStore`'s atomic-write claim beyond "no `.tmp` left behind on success" — doesn't prove crash-mid-write recovery. Add: garbage `.tmp` present + `LoadAsync` still returns good content; garbage `.tmp` present + `SaveAsync` new content + assert result is the new content, not a merge |

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

> **Superseded 2026-09-07 — historical.** See "What to pick up next" near the top of this file.

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
