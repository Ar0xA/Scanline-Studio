# 19 — Road to 1.1

## Related

[[18-path-to-1.0]] (previous milestone — see its own status banner) · [[15-template-designer]] (the
TX Template Editor redesign — currently the sole confirmed 1.1 target; full design content lives
there, not duplicated here)

## Status: 1.0 complete; the confirmed 1.1 target (TX Template Editor redesign) is implemented

`spec/18-path-to-1.0.md` is done — 🔴 Critical, 🟠 High, and 🟡 Medium tiers all closed
(2026-08-16). This document tracks 1.1 the same way `spec/18` tracked 1.0: a thin roadmap layer,
pointing at the detailed spec for each target rather than duplicating it.

**Update 2026-08-18**: the TX Template Editor redesign (below) is built — 8 implementation phases, a
design-fidelity pass against `mockups/Editwindow`, and an 18-item usability-gap batch from an
`auditor` review. See [[15-template-designer]]'s own Status section for the current gap list
(box-element corner-radius, `.mtm` import still deferred, multi-handle element resize). The
"scoped, needs plan-review" framing below describes the 2026-08-16 state, before implementation —
left as written for the historical record, not the current state.

## Provenance

2026-08-16: a design conversation with the user, a 6-branch ADHD divergent-ideation pass (5
cognitive frames + an independent Opus branch), and a separate open-ended Opus research pass (grounded
in real source reads) scoped the TX Template Editor redesign. Full detail — confirmed requirements,
functional scope, candidate architecture, friction risks, non-goals — lives in
[[15-template-designer]], not here.

## 1.1 targets

### TX Template Editor redesign — see [[15-template-designer]]

**Status: implemented (2026-08-18)** — was "scoped, needs plan-review before implementation" as of
2026-08-16; see the Update note above.

A single, always-interactive templating layer on top of the existing TX image editor (crop/rotate/
adjustments/undo-redo/basic overlay-text, all already shipped and unchanged) — drag/drop, resizable
elements with auto-fit text, color/font control, layer priority, and a fast mid-QSO fill/reuse
workflow. End-result capability matches what legacy YONIQ's template designer could produce; the
implementation is a fresh, modern design, not a port. Legacy `.mtm` file import is a confirmed real
goal, explicitly sequenced after this ships, not part of the initial build.

*(No other 1.1 targets are confirmed yet — this is currently a single-item milestone. Add further
targets here as they're scoped, following the same pattern: a one-paragraph summary + status here,
full design detail in its own spec doc.)*

## Open questions / next steps

- ~~Plan-review [[15-template-designer]]'s candidate architecture~~ — done; implementation shipped
  (see Status above).
- Whether 1.1 stays a single-item (template editor) milestone or picks up additional targets is not
  yet decided.
