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
| 2 | 2026-08-12 | CW-ID/FSK Phase 2: `FskStationIdEncoder.cs`/`FskStationIdWireFormat.cs` vs legacy `OutputFSKID` (`Main.cpp:6903-6965`) | First run: tool call failed (see Adjustments below), model's own verdict line literally echoed the unfilled template. Second run (after fixing peer_audit.py): EQUIVALENT, one real tool-use success (correctly fetched and read the second candidate file via `read_file`), one nit ("none"), reasonable off-scope note on concurrency. | EQUIVALENT-WITH-RISKS, no blockers. Independently re-derived the exact same golden-vector byte sequences by hand (`2a,21,01,21,02,01,3B,38` and `2a,21,01,21,21,22,11,01,12`) — strong cross-confirmation the port is byte-correct. All 4 findings were nits: an overstated doc-comment citation, a CP932-bytes-vs-UTF16-chars filter divergence (low-severity, garbage-either-way), an sscanf-overflow edge case (port's behavior is the safer one), and a culture-explicitness style nit. All fixed same round. | AGREE (on substance) | **Real tool bugs found and fixed in `peer_audit.py` itself this row** — see Adjustments log. This was the first row where the model actually attempted a tool call; both bugs were latent since row 1 never exercised that code path. Once fixed, the tool correctly resolved the model's self-chosen path and produced a real, on-topic (if format-noncompliant) verdict. |

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

- **Row 2**: two real bugs in `peer_audit.py` found via actual tool-call usage (the first real case
  where the model attempted a tool call at all, since row 1 never needed one):
  1. `resolve_and_contain`'s relative-path resolution only tried each allowed root directly, not
     the repo root itself -- the model guessed a repo-root-relative path
     (`src/ScanlineStudio.Core.Sstv/Foo.cs`) for its `read_file` call, which silently failed to
     resolve (returned a 119-char ERROR string). Fixed: also try `REPO_ROOT / path_str` as a base.
  2. `find_enclosing_signature`'s search started exactly at the slice's own first line and only
     scanned backward -- missed the common case where a `//---` divider comment is the slice's
     first line and the real function signature is 1-2 lines further in. Reported the wrong
     enclosing function (`OutputMMV` instead of `OutputFSKID`). Fixed: extend the initial window a
     few lines forward before searching backward.
  3. Also added a guard for a new failure mode observed this row: the model can echo the prompt's
     own verdict-line template verbatim (`EQUIVALENT | NOT EQUIVALENT | EQUIVALENT-WITH-RISKS`)
     instead of picking one option, most likely after a confusing tool-call result derails it --
     now flagged explicitly as INCOMPLETE rather than silently accepted.

## Review log

Filled in at the review checkpoint (after 5 usage-log rows) and again at any later re-review.
Answer directly: is this still earning its place in the default workflow, unchanged, adjusted, or
dropped back to ad-hoc/opt-in use?

### Review 1 (after use #5) — pending
