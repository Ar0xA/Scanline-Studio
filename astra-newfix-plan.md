# Fix plan v3 — the five defects `astra-fix` introduced (00d–00h)

> **BUILT AND MERGED — no open work here (marked 2026-09-10).** This build spec was implemented,
> code-reviewed to GO FOR PRODUCTION over two rounds, and merged to `master` on 2026-09-09
> (`07d1a54`). What actually shipped, including the two places implementation diverged from this
> plan, is in `production_audit.md`, section "Fixing 00d-00h". Do not treat the steps below as a
> to-do list.

Findings and proof: `production_audit.md`, section "`astra-fix` branch verification — 2026-09-08".
Branch: `astra-fix`, now merged.

Review history: plan-review round 1 → NOT READY (1 blocker, 4 majors). v2 → NOT READY (4 majors, all
paper fixes, "a v3 with those four pinned is a go"). A principal review of the F3 design decision
confirmed option (a) and corrected its trigger. **v3 pins all four and is the build spec.**

## Changes from v2

1. `IPttTestDrain` is **public**, not internal — `ScanlineStudio.Application` grants internals only
   to its own test project, so an internal interface would not compile at the `Program.cs` cast.
   Public also satisfies the original goal: nothing fakes it, so no test double changes.
2. The `_noteWrites` cleanup **keeps its `ReferenceEquals` guard** inside the `finally`. An
   unconditional remove would delete a *newer* in-flight write, so the next edit would chain onto
   `Task.CompletedTask` and run concurrently with it — the exact ordering bug the chain prevents.
3. The tombstone prune runs **unconditionally, before both of `ReconcileWithDiskAsync`'s early
   returns**. Placed after them it would almost never run, because the common case is nothing to
   import.
4. A test pins the round-1 blocker invariant: after `DrainPttTestsAsync`, `TestPttAsync` must be
   refused and must not key.
5. **Principal correction:** write the tombstone from the **caught exception**, not from a post-hoc
   `File.Exists`. `File.Exists` is a worse proxy in both directions — a Windows delete against a
   `FILE_SHARE_DELETE` handle returns without throwing while the file stays visible, and an
   `UnauthorizedAccessException` on an inaccessible directory can leave `File.Exists` false.

## F1 (00d + 00e) — `RadioSessionService` shutdown ownership

**Files:** `src/ScanlineStudio.Application/RadioSessionService.cs`, a new `IPttTestDrain.cs` in the
same project, `src/ScanlineStudio.Host/Program.cs`,
`tests/ScanlineStudio.Host.Tests/HandleLifetimeExitTests.cs`.

**Invariant, stated because no existing test catches it.** `TestPttAsync`'s gate currently keys on
`_pttDisposeCompletion`, which only `DisposeAsync` sets. Once `Program.cs` calls the drain instead, a
gate keyed on that field would never close on the real exit path, and a PTT test started during
shutdown would key the rig — `ASTRA-040`'s own failure class.

1. Add `private bool _pttStopping`. `TestPttAsync`'s gate (`:138`) becomes
   `if (!_pttStopping && _pttCompletion is null)`. Remove the now-unused `_pttDisposeCompletion`
   (`:131`); CS0169 is an error here.
2. `public interface IPttTestDrain { Task<bool> DrainPttTestsAsync(TimeSpan? timeout = null); }` in
   `ScanlineStudio.Application`, implemented by `RadioSessionService`. Not added to
   `IRadioSessionService` — that is the UI-facing contract, and it would force three test doubles to
   implement a shutdown-only method.
3. `DrainPttTestsAsync` must be a **non-async** method returning the cached `Task<bool>` field.
   `RadioSessionServiceTests.cs:33` asserts `Assert.Same` on the returned task, and an `async`
   method awaiting the cached task returns a fresh instance. Plumbing follows
   `ReceiveHistoryRecorder.DrainAsync` (`:274-296`) exactly: under `_pttLifetimeGate`, return the
   cached task if set, else set `_pttStopping = true`, snapshot `active`, and start
   `Task.Run(() => DrainPttCoreAsync(...))`. `Task.Run` matters — it keeps the core's synchronous
   prefix off the lock, which `RunOwnedPttTestAsync`'s own `finally` (`:162-169`) also takes.
4. **The body deliberately departs from that precedent, which is not fault-free** (its unsubscribes
   at `ReceiveHistoryRecorder.cs:302-304`, its two catch-arm logs at `:323`/`:328` and its completion
   log at `:333` are all throw sites that would fault the task). `DrainPttCoreAsync`'s required
   shape:
   - one `try` covering the whole body, including the "shutdown started" log;
   - every log call, **catch arms included**, routed through one private helper that swallows;
   - a `bool` local set in the `try`, returned **after** the try/catch — not from a `finally`, which
     is CS0157.
5. `DisposeAsync()` becomes `new(DrainPttTestsAsync())`. Discarding the bool is safe: the container's
   second dispose observes the same cached task, and that task cannot fault.
6. `Program.cs`: `drainPttTests` becomes `Func<Task<bool>>?`, using the same
   `if (!result) { disposedCleanly = false; Log...; }` branch the image drain already has at
   `:479-495`. One new `[LoggerMessage]`: `PttDrainIncomplete`.
7. Host backstop gets **its own seam**, `TimeSpan? pttDrainTimeout = null`, default **80 seconds**.
   Reusing `disposeTimeout` would break `HandleLifetimeExitTests.cs:71`, which passes 50 ms while
   holding the PTT drain open. 80 s clears the internal 75 s budget while adding least to worst-case
   exit time.
8. Two existing-test edits: `HandleLifetimeExitTests.cs:58` becomes `Task.FromException<bool>(...)`
   — the throw-signals-failure path is **kept**, not replaced — and `:71`'s lambda gains
   `return true`.

**Accepted residuals, recorded not fixed:** (i) if a drain never completes at all, the container's
second dispose at `Program.cs:506` re-awaits the same cached task and burns the 10 s host budget —
the backstop bounds the explicit drain, not that second await; (ii) the outer gate method's own body
throws synchronously rather than returning a faulted task, so a throw there would escape
`DisposeAsync()` inline; nothing in that body can realistically throw. The additive 75+30+10 s exit
budget stays out of scope as a separate logged item.

## F2 (00f) — logbook migration must not block startup

**File:** `src/ScanlineStudio.Core.Logbook/SqliteLogbookRepository.cs`

1. `qso_utc_ticks` uses `DateTimeOffset.TryParse`, returning `0` on failure.
2. **Hoist `connection.CreateFunction(...)` out of the `if (!existingColumns.Contains(...))` block**
   (`:319-321`). The backfill now runs unconditionally, so a second launch would otherwise throw "no
   such function".
3. Backfill becomes `UPDATE Qso SET StartUtcTicks = qso_utc_ticks(StartUtc) WHERE StartUtcTicks = 0`,
   run unconditionally, and **moved below the two `CREATE INDEX` statements** (`:331-332`) so the
   equality predicate can use `IX_Qso_StartUtcTicks` from the second run onward. Ticks 0 is
   `DateTimeOffset.MinValue`, so recomputing it is a no-op and no legitimate row is rewritten.
4. Wrap the backfill `UPDATE` in its own try/catch inside the transaction, so the `ALTER TABLE` and
   both indexes still commit if the backfill fails for a reason `TryParse` cannot cover — for
   example a non-TEXT storage class in `StartUtc`. Whether Microsoft.Data.Sqlite throws or coerces
   there is **not verified**; the wrap is correct either way, and only that justification is
   speculative. A rollback-forcing class such as `SQLITE_FULL` would still make the commit throw,
   which is no worse than today.
5. Log the outcome **once per run with a row count**, plus an "aborted" branch for step 4's catch. Not
   per row: an unparseable row is written 0 and therefore stays in the predicate's set forever.

**Known residual, deliberately out of scope.** `MapRecord` (`:171`) still parses `StartUtc` on read,
so an unparseable row still throws from `SearchAsync` exactly as on `master`. The third option —
skip-and-log the unreadable row — invents nothing and is what this codebase already chose two methods
away (`ParseMode`'s doc comment at `:215-219`: a plain `Enum.Parse` "would take down the entire
logbook list over one row"). Not adopted here because it is pre-existing behaviour, not one of the
five.

## F3 (00g) — deletion tombstones must not be permanent

**Files:** `src/ScanlineStudio.Core.Logbook/SqliteReceiveHistoryStore.cs`, and `DeleteAsync`'s own
summary at `:329-330`.

**Design: option (a) — tombstone only when the image delete actually failed.** Confirmed by the
principal review on three grounds: `ASTRA-041`'s filed trigger is a failed delete, not a crash
(`astra-audit.md:161`, RP-1 at `:558`); the contract at `IReceiveHistoryStore.cs:286` already scopes
the guarantee to an image "whose disk cleanup **failed**", so the shipped implementation is broader
than its own contract; and (a)'s residual failure is recoverable while (b)'s is not. The crash gap is
the same two adjacent statements in both designs — (a) yields a re-deletable entry there, (b) yields
a silent permanent suppression.

1. The row-delete transaction **no longer writes a tombstone**. Keep transaction-first ordering, so
   the "row no longer existed → return false, no `Deleted` event" early return is unchanged.
2. Write the tombstone **from the image delete's catch arm** (`:366-372`), not from a `File.Exists`
   probe. The existing `if (File.Exists(...))` guard at `:361` means an already-absent file attempts
   no delete and therefore writes no tombstone, which is correct. Only the image delete's catch
   writes one — an audio-delete failure must not suppress the image path.
3. The tombstone SQL becomes a plain `INSERT OR IGNORE INTO ReceiveHistoryDeletion (FilePath)
   VALUES ($path)`. The current statement's `WHERE EXISTS (SELECT 1 FROM ReceiveHistory WHERE
   Id = $id)` guard (`:339-342`) is permanently false once the row is already deleted, so moving it
   verbatim would write no tombstone at all and silently return `ASTRA-041`.
4. **Re-check `File.Exists` inside the reconcile insert loop** (`:517-553`). Required by (a) and
   correct regardless: the tombstone check at `:540` is currently the only guard against inserting a
   candidate deleted between the SELECT snapshot (`:426-451`) and the insert, and a successful delete
   no longer leaves one. This narrows a TOCTOU rather than eliminating it — it is not a proof.
5. **Prune tombstones whose file has disappeared**, unconditionally and **before** the
   `toImport.Count == 0` early return (`:502-505`) and the missing-directory early return
   (`:419-424`). The prune is keyed on `File.Exists(tombstonePath)`, so it needs neither. Safe by
   construction: if the file is gone there is nothing to resurrect.

**Accepted residuals, recorded not fixed:** (i) a delete landing in the gap between the row-delete
commit (`:352`) and the unlink (`:363`), while a once-per-session reconcile is mid-insert, can leave
one row whose file is gone — it renders without a thumbnail and the normal delete command removes
it; (ii) the same applies to the second tombstone consumer, `RecordAsync` (`:158`, logging at
`:193`): under (a) a successful delete no longer suppresses a delayed recorder write, so an
import-then-delete-then-late-`RecordAsync` sequence can re-insert one row for a gone file. Both are
recoverable by deleting again, which is the whole basis for choosing (a).

## F4 (00h) — note-write chain must not poison

**File:** `src/ScanlineStudio.UI/ViewModels/RxHistoryPaneViewModel.cs`

1. Wrap the whole `PersistNoteAfterAsync` body in one try/catch, covering `await previous` and both
   `Dispatcher.Post` calls. Keep the two distinct messages (store failure, entry missing).
2. The swallowing helper must cover **the `Log.*Failed` call as well as the `Post`** — after step 1
   the catch arm is a log then a post (`:1450-1451`, `:1460-1461`), and a throwing `ILogger` there
   faults the task and reproduces 00h one level deeper.
3. Apply the same guard to `PersistFlaggedAsync`'s catch arm (`:1521-1522`) and correct its comment
   at `:1500-1502`, which asserts its returned task "can never fault" — false today for exactly this
   reason. Both reviews confirmed this is load-bearing, not scope creep: a dispatcher shutdown that
   makes `Post` throw in the `try` makes it throw in the `catch` too, at the same probability.
4. Cleanup becomes
   `try { await write; } catch { log } finally { if (ReferenceEquals(_noteWrites.GetValueOrDefault(entryId), write)) _noteWrites.Remove(entryId); }`.
   **The `ReferenceEquals` guard is load-bearing** — an unconditional remove would drop a newer
   in-flight write, so the next edit would chain onto `Task.CompletedTask` and run concurrently with
   it. The `catch` is needed because the caller is fire-and-forget from `:1408`.
5. Fix the two stale comments naming the removed `_notePersistCts` (`:60`, `:1257`).

## Verification

Build, then targeted suites: `Application` + `Host` (F1), `Core.Logbook` (F2, F3), `UI` (F4). Full
sweep at the end against current baselines (Application 510, Core.Logbook 225, Settings 99,
Core.Localization 23, Host 70, UI 1603, Core.Radio 292, Core.Imaging 138, UI.Font 35, Core.Sstv 1703
passed / 10 skipped) plus the new tests.

Each new test states how it is gated — a pre-fix failure where one is reachable, an explicit mutation
otherwise.

- **F1a — admission during shutdown** (the round-1 blocker's own invariant, currently untested).
  `await DrainPttTestsAsync()` with nothing in flight, then assert `TestPttAsync` returns
  `Success == false` **and** the fake protocol recorded no `SetPtt` call. Mutation gate: keying the
  flag on the old `_pttDisposeCompletion` must fail it. The existing assertion at
  `RadioSessionServiceTests.cs:34` does **not** cover this — it passes on `_pttCompletion is not
  null` alone.
- **F1b — double dispose after a failed drain.** Pre-invoke
  `((IPttTestDrain)service).DrainPttTestsAsync(50ms)` with an in-flight test that never completes, so
  the cached result is `false`; then a real `ServiceProvider` holding two `IAsyncDisposable`
  singletons where `RadioSessionService` disposes first — assert the **second** still disposed. The
  pre-invoke matters: the container calls the parameterless overload, which would otherwise wait out
  the 75 s default. Mutation gate: reverting to `TrySetException` must fail it.
- **F1c — throwing logger.** Assert `await DrainPttTestsAsync()` returns `false` rather than throwing,
  bounded with `WaitAsync` so the pre-fix behaviour fails instead of hanging the suite. This is the
  only test that gates F1 step 4's catch-arm guard.
- **F1d — incomplete drain is reported.** Needs a **capturing logger**; `HandleLifetimeExitTests`
  passes `NullLogger.Instance` everywhere and `disposedCleanly` has no other observable effect, so
  without one the test asserts nothing.
- **F2 — unparseable row.** Pre-migration database built with raw SQL (no `StartUtcTicks`), one valid
  row and one unparseable. Assert the constructor succeeds, the column exists, the good row is
  date-filterable, and the bad row holds ticks 0. Fails pre-fix.
- **F3 — five cases.** (i) successful delete, file restored on disk, reconcile imports it — fails
  pre-fix; (ii) both existing failed-delete tombstone tests pass unchanged, since both inject a
  throwing `DeleteImageFileForTests` and therefore still get a tombstone; (iii) delete during
  reconcile via the existing `BeforeReconcileInsertForTests` seam — **passes pre-fix**, so its gate
  is an explicit mutation: removing step 4's `File.Exists` re-check must fail it; (iv) a tombstone
  whose file disappeared is pruned; (v) a tombstone is still written when the image delete throws
  but the audio delete succeeds.
- **F4 — injectable fault.** A throwing `Dispatcher.Post` is not injectable (static, real headless
  dispatcher). Use `FakeReceiveHistoryStore.SetNoteGate` throwing to drive the catch arm, plus an
  `ILogger<RxHistoryPaneViewModel>` that throws **only** for the SetNote-failed event — a blanket
  throwing logger risks derailing `RefreshCommand` setup before the assertion. Assert a later edit on
  the same entry still persists. Requires a logger parameter on `CreateRxHistoryPaneViewModel`
  (`PaneViewModelTests.cs:7970-7994`), which hardcodes `NullLogger`.

No help-doc change: none of these five alters a user-visible field, dialog or workflow step. Add no
new "auditor round-N" citations to comments — CLAUDE.md §3 forbids them.
