# 19 — Road to 1.1

## Related

[[18-path-to-1.0]] (previous milestone — see its own status banner) · [[15-template-designer]] (the
TX Template Editor redesign — currently the sole confirmed 1.1 target; full design content lives
there, not duplicated here)

## Status: 1.0 complete; 1.1 scoping in progress, one target confirmed

`spec/18-path-to-1.0.md` is done — 🔴 Critical, 🟠 High, and 🟡 Medium tiers all closed
(2026-08-16). This document tracks 1.1 the same way `spec/18` tracked 1.0: a thin roadmap layer,
pointing at the detailed spec for each target rather than duplicating it.

## Provenance

2026-08-16: a design conversation with the user, a 6-branch ADHD divergent-ideation pass (5
cognitive frames + an independent Opus branch), and a separate open-ended Opus research pass (grounded
in real source reads) scoped the TX Template Editor redesign. Full detail — confirmed requirements,
functional scope, candidate architecture, friction risks, non-goals — lives in
[[15-template-designer]], not here.

## 1.1 targets

### TX Template Editor redesign — see [[15-template-designer]]

**Status: scoped, needs plan-review before implementation.**

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

- Plan-review [[15-template-designer]]'s candidate architecture (auditor pass, per this project's
  established process for UI/UX design decisions) — not yet done, the next concrete step.
- Whether 1.1 stays a single-item (template editor) milestone or picks up additional targets is not
  yet decided.
