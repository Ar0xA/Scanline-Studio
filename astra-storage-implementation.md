# Final storage implementation and verification

Status: **IMPLEMENTED AND VERIFIED**, 2026-09-08. Independent final code review
approved; no required correctness fixes remain in this bounded storage change.

Scope: fix the two storage failures and their reproduced follow-up regressions on
`astra-fix`, preserving existing working changes. The user authorized implementation
and verification, then acceptance of documented remaining risks.

## Design being implemented

Use a token-only tombstone with the existing history row as the durable retry
anchor. Do not create an unprunable Pending state or delete another row's metadata.

Serialize the entire Delete lifecycle with an instance-owned asynchronous gate,
acquired before reading the row. The production DI graph uses one store singleton;
a queued stale second delete re-reads the row and returns false before filesystem
work. This gate affects user deletions only, not Record/reconcile or hot audio work.

1. An immediate transaction looks up the actual selected row. A missing ID returns
   false without filesystem effects. If another row references the same image,
   remove only the selected row and preserve the referenced image/other metadata.
2. Otherwise upsert a fresh tombstone token while **retaining the history row**,
   then commit. Existing Record/reconcile insertion guards block late writes.
3. Attempt image cleanup outside SQLite transactions and off the UI thread.
   Confirm absence by inspecting the file's own accessible direct parent. Missing,
   inaccessible, malformed and uncertain paths retain protection.
4. A second immediate transaction verifies the token and row, removes that selected
   row, and retires the same token only after confirmed successful cleanup. Failed
   cleanup leaves suppression. If this transaction fails, the history row remains
   available for ordinary Delete retry, including after restart. No correctness
   write is required after the row-removal commit.
5. Prune only confirmed-absent paths that have no referring history row, comparing
   the captured token and repeating the no-reference check in the DELETE. A staged
   or retryable deletion therefore cannot be pruned. Dispose readers before probes.
6. Preserve independently referenced audio and existing event behavior. All new
   I/O/command diagnostics use generated logging and failure reporting is isolated.

Canonical comparison uses the existing normalization boundary through a connection-
local SQLite function; preserve existing stored paths and metadata. The only schema
addition is `Token TEXT NOT NULL DEFAULT ''` on the tombstone table.

## Verification gates

Two dedicated read-only plan rounds before core edits; independent code review of
deletion, then pruning/integration. Deterministic regressions cover late recorder
and reconcile insertion, offline parents and their return, token replacement,
existing row+tombstone successful restore, interrupted finalization/restart/retry,
shared metadata/files, cancellation, malformed paths, logger failures and migration.
Run focused storage tests first, then full Core.Logbook and affected integration
suites. Record actual results and reviewer findings here and in the audit ledgers.

## Remaining risks to retain explicitly

Filesystem observations are snapshots; an accessible empty remounted directory
cannot be distinguished from real absence by this probe. Mixed old application
versions do not honor the token protocol. A late recorder after successful cleanup
may still produce a row without an image. Existing independent entries sharing a
file remain independent: deleting one preserves the others and their metadata.
Tokens protect database retirement across instances; the instance gate does not
serialize filesystem operations by a second application process or an external
program. Concurrent external replacement of an image remains a filesystem risk.
No broad redesign, push or merge is included.

## Review and results

- Plan review round 1: NOT READY for overlapping same-instance deletion I/O;
  retained-row/token mechanics approved. Added full lifecycle async serialization,
  persisted-row re-read and a queued stale-delete/restoration regression.
- Plan review round 2, `audit_final_storage_fix`: READY within the documented
  singleton-store scope. Explicit cross-instance filesystem limits retained.
- Code review round 1: APPROVE core deletion lifecycle; no blocking findings.
- Combined code review round 2: APPROVE deletion/record/reconcile composition and
  all 16 new deterministic actual-store regressions; no required correctness fixes.
- Build: Core.Logbook Debug, zero warnings/errors.
- Full suites: Core.Logbook **249**, Application **515**, Host **73**, UI **1604**:
  **2,441 passed**, zero failures/skips. The 16 new lifecycle tests are included in
  Core.Logbook's total, not additional to it. `git diff --check` passed.
- The former test that dropped the tombstone table after row removal now uses a
  targeted SQL-abort trigger, retains the row/protection, and proves ordinary retry
  after reopening the store. This reflects the stronger durable-retry contract.
- The user explicitly accepted documented remaining risks once the concrete
  failures were corrected and verified. No broader redesign or release certification.

## Fixed findings and final reviewer verdicts

| Finding | What changed and why | Verdict |
| --- | --- | --- |
| ASTRA-041 / failed-delete late insertion | Stage token before cleanup while keeping the selected row; atomic finalization retains failed-cleanup protection. Actual late-first-record and existing reconcile-race tests pass. | Approved |
| Historical/offline parent pruning | Probe the stored path's own parent; uncertainty retains intent. Folder-return and late-record tests prove the deleted image stays suppressed. | Approved |
| Old prune / old completion clears newer intent | Match the captured token, recheck referring rows at retirement, and serialize same-store deletion I/O. Real second-store ABA and queued stale-delete tests pass. | Approved |
| 00g / successful cleanup cannot restore | Successful row removal and token retirement commit together, including old row+tombstone state; immediate restore re-imports. | Approved |
| Interrupted finalization would strand suppression | The history row stays available until finalization commits; cancellation, SQL failure, reopen and ordinary retry are tested. | Approved |
| Co-path collateral data loss | Delete only the selected ID and preserve files referenced by other rows, including canonical aliases/shared audio. Notes, flags and QSO links are asserted unchanged. | Approved |

Primary files: `SqliteReceiveHistoryStore.Deletion.cs`, token migration/logging in
`SqliteReceiveHistoryStore.cs`, `IReceiveHistoryStore.cs` contract, and
`ReceiveHistoryDeletionLifecycleTests.cs` plus the revised existing failure test.
