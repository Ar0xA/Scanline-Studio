# Peer-audit performance tracking

Tracks real-workflow uses of `peer_audit.py` (as opposed to ad-hoc testing) so we can evaluate,
with evidence, whether it's earning its place in the default workflow — see `CLAUDE.md` §7b for
when/how it's invoked, and `README.md` for what it does and its known limitations.

**Review checkpoint: after 5 logged uses below**, stop and do an explicit go/no-go review (append
it to "Review log" at the bottom) before continuing to rely on this by default.

## Usage log

| # | Date | Port / task | Peer verdict | Real auditor / human verdict | Agreement? | Notes |
|---|------|-------------|--------------|-------------------------------|------------|-------|
| 1 | 2026-08-12 | CW-ID/FSK Phase 1: `CwMorseGenerator.cs` vs legacy `WriteCWID` (`sstv.cpp:2951-3003`) | EQUIVALENT. One nit (correctly identified the documented WPM deviation as intentional, not a bug); honestly flagged the new `MillisecondsPerDotFromWpm` formula as unverified since it's not a port of anything. | EQUIVALENT-WITH-RISKS. Found a real correctness bug peer didn't surface at all: legacy iterates CP932 *bytes*, the port iterated UTF-16 *chars*, so a naive `&0x7f` mask aliased out-of-range Unicode characters into valid Morse letters instead of producing silence (e.g. U+3042 → 'B'). Also caught a mistitled test that didn't test what it claimed, and a test-rigor gap (silence tests were zero-in/zero-out, which passes trivially whether the output filter runs or is skipped — didn't actually prove the filter keeps running across a gap). All fixed same round. | PEER MISSED | No tool/prompt change made yet — one data point isn't enough to diagnose why peer missed the CP932/UTF-16 encoding issue (plausibly: it's a cross-cutting project convention from CLAUDE.md §4, not something visible from the two snippets alone, and the peer-audit prompt doesn't currently inject project-wide encoding rules). Revisit if this class of miss repeats. |

**Column guide:**
- **Peer verdict**: the local tool's own watermarked VERDICT line, plus a 1-line summary of its
  top finding if any.
- **Real auditor / human verdict**: what the actual `auditor` subagent (or a human read)
  concluded on the *same* candidate afterward. This is what makes a row meaningful — an
  unchecked peer verdict is not data, just noise.
- **Agreement?**: one of
  - `AGREE` — same conclusion.
  - `PEER MISSED` — the real auditor found something peer didn't. The dangerous direction; a
    cluster of these is grounds to stop trusting a clean peer-audit verdict on anything.
  - `PEER FALSE-POSITIVE` — peer flagged something that wasn't real. Costs time, not safety.
  - `PEER CAUGHT FIRST` — peer found something real that the real auditor then confirmed. The
    whole point of this tool, if it happens.
- **Notes**: anything adjusted as a result (prompt wording, deterministic pre-scan logic,
  tool-call budget, model choice) and why. If nothing was adjusted, say so explicitly
  ("no change needed") rather than leaving it blank.

## Adjustments log

Every time `auditor_prompt.md` or `peer_audit.py`'s logic changes because of something a tracked
usage-log row revealed, record it here with the row # that triggered it.

- (none yet)

## Review log

Filled in at the review checkpoint (after 5 usage-log rows) and again at any later re-review.
Answer directly: is this still earning its place in the default workflow, unchanged, adjusted, or
dropped back to ad-hoc/opt-in use?

### Review 1 (after use #5) — pending
