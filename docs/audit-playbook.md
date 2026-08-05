# Milestone Audit Playbook

Run this at **workstream boundaries** (e.g. DSP engine -> UI, CAT layer -> codec
modes) or **before a release tag** — not routinely. For single-function checks, just
say *"use the auditor agent to check this part"* instead.

**Why it's separate from per-function audits:** per-function checks prove each brick
is sound; this proves the wall stands. Each piece can pass its own round-trip while
the composed chain is still wrong about reality (see the Scottie TX/RX incident in
CLAUDE.md §4) — so this playbook verifies the **chain end-to-end against legacy golden
vectors**, not just the parts.

---

## How to run

Paste the prompt below into a fresh session (ideally after a `/clear`, with
`PROJECT_BRIEF.md` read first). The main session acts as **orchestrator** and stops
for your review at the end of each phase. Throttle cost by approving Phase 1 before
any auditing runs, and by batching Phase 2 (4–5 units at a time).

---

## The prompt

```
We've completed and per-function-verified a large portion of the port. I now want a
broad, systemic audit — not re-checking individual functions, but whether the whole
thing holds together and behaves as YONIQ does end-to-end.

Act as the orchestrator. Do NOT audit inline. Work in three phases and STOP for my
review at the end of each phase:

PHASE 1 — PLAN (no auditing yet)
- Map the ported surface into a dependency-ordered list of audit units: DSP core,
  codec/mode paths (TX and RX separately), CAT/cradio framing, encoding boundaries,
  DI/service wiring, concurrency/scheduler seams, localization, and the license/
  removed-features docs.
- For each unit list: the C# files, the matching legacy source (yoniq-old/...),
  and the golden-vector or reference it should be checked against.
- Flag units with NO golden vector as a gap — those can't be chain-verified.
- Output this as a table and wait for my go.

PHASE 2 — PER-UNIT FAN-OUT
- For each unit, delegate to the `auditor` agent in its own isolated context with
  only the minimal legacy + candidate snippets. Restate the ADHD scope rule in each
  payload. Collect each verdict; do not fix anything yet — just aggregate.
- Return a consolidated findings table: unit | verdict | blockers | risks.

PHASE 3 — CHAIN / INTEGRATION AUDIT
- This is the part per-function checks can't cover. For each full path (TX end-to-end,
  RX end-to-end, CAT round-trips): verify real input flows through the composed C#
  chain and matches LEGACY-captured output within the stated tolerance — NOT an
  internal round-trip (encoder+decoder can agree while both are wrong; see the Scottie
  incident). Audit the seams: cross-module contracts, data/encoding handoffs, scheduler
  behavior across the whole signal path, and the ScanlineStudio.Application boundary.
- Report any gap where per-unit passes but the chain diverges.

Lead each phase with the verdict. Keep off-scope findings to one line each.
```

---

## Cost throttles

- **Gate on Phase 1.** Approve the unit map before any fan-out — this is the biggest
  token lever. Kill or narrow units with no golden vector.
- **Batch Phase 2.** Name 4–5 units at a time ("start with DSP core + TX paths")
  rather than fanning out all at once.
- **Reuse the map.** Phase 1's table is reusable — after fixes, re-audit individual
  units without re-planning.

## Scope options

- Narrow to one workstream: *"Run Phases 1–3 for the DSP core and TX paths only."*
- Skip straight to integration if units are already green:
  *"Skip Phase 2 — units are verified; run Phase 3 chain audit for TX and RX end-to-end."*
