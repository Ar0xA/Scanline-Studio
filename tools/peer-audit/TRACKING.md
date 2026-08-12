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
| 3 | 2026-08-12 | CW-ID/FSK Phase 3: `NarrowFskHeaderDecoder.cs` (RX continuation decoder, 469 lines) vs legacy `sstv.cpp:2445-2551` | First attempt (full 469-line file): `done_reason=context_exhausted` — the file has grown large enough (mode-announce + station-ID + extensive doc comments) to exceed the 8192-token budget alongside the legacy snippet, caught and aborted correctly rather than silently truncating. Retry scoped to just the new `FskDecodeResult` type (38 lines): produced a real, if extremely terse, verdict ("nit: none found") but AGAIN echoed the unfilled verdict template verbatim — same degenerate failure as row 2, this time with zero tool calls involved, so not purely a tool-call-derailment artifact. | 2 rounds, EQUIVALENT-WITH-RISKS, no blockers either round. Round 1 found real things peer-audit had zero chance to surface given it never even got a usable read of the state-machine code: 2 legacy-fidelity field-reset mismatches, a test whose comment overclaimed what it isolated, a `TryDecodeNarrowModeHeader` control-flow nit, and — the most significant — the trickiest discrimination logic (station-ID vs. mode-announce-lock in `TryNarrowFskScan`) was correct by code review but had ZERO test coverage (`StationIdDecodeEnabled` never set true anywhere). Fixed all 5, including adding a genuine end-to-end test using this port's own Phase 1/2 building blocks. Round 2 independently re-verified every fix against source (hand-recomputed bit-boundary arithmetic for the guard-tone test, traced the property-initialization-order safety) — closed clean, "stop reviewing and move to Phase 4." | N/A (peer-audit non-functional this row, no comparison possible) | Same conclusion as row 2's note, now with a second data point: peer-audit contributed nothing to Phase 3 (context-exhausted on the real file, template-echo on the scoped-down retry) while the real auditor caught 5 real issues including the phase's single most important gap. Confirms row 2's suspicion — the template-echo issue is a recurring small-model reliability ceiling, not a fluke. Given the pattern is now 2-for-2 (rows 2 and 3), worth deciding at the review checkpoint (2 more rows) whether `NUM_CTX` needs raising for larger candidate files, or whether peer-audit's realistic ceiling is "small, single-file candidates only." |
| 4 | 2026-08-12 | CW-ID/FSK Phase 4: `AnalogFmSstvEncoder.cs`'s post-image segment append + footer selection + settings-boundary normalization (lines 252-374) vs legacy `SendSSTV` (`Main.cpp:6988-7027`) + `Option.cpp:445-448` | Template-echo failure again (unfilled `EQUIVALENT \| NOT EQUIVALENT \| EQUIVALENT-WITH-RISKS` verdict line) — third time on this feature, now the clearest single-model-ceiling case yet: zero tool calls, real prose findings underneath the broken verdict line. One real finding worth relaying: "risk <numeric fidelity>: legacy uses double, candidate uses float" — turned out to be a FALSE POSITIVE (independently confirmed by both my own read and the real auditor: nothing in the cited slice is float; float narrowing happens elsewhere, in a different method, already reviewed in an earlier phase). One accurate self-assessed non-finding (binary-is-bytes nit correctly judged not applicable). | Round 1: EQUIVALENT-WITH-RISKS, 2 real [risk] findings (NR/RST cap-before-filter instead of after — could flip compact-vs-string wire form for real exchange text containing separators; `FskIdRxEnabled` was a fully dead setting, never wired to any decoder) + 5 nits (3 fixed: stale VOX comment, missing 78-char CW text cap, missing SoundFile/ordering test coverage; 2 accepted as-is: CP932-byte-vs-char divergence matching Phase 1-2 precedent, footer-carrier-source nit already "the evidently-intended" behavior). Explicitly refuted the float-vs-double claim I relayed from peer-audit, confirming my own independent read. Round 2 (verifying all fixes): EQUIVALENT-WITH-RISKS, ready to commit -- independently re-derived the NR/RST fix by hand from source (confirmed correct), traced the footer-carrier `m_TW` provenance further than round 1 had (a genuine legacy RX-vs-TX-mode quirk, port's TX-mode choice confirmed still the right call), and caught one real gap of its own: the two new swap-survival tests asserted only the wrapper's own stored field, which would pass even if the fix were reverted -- fixed with a new `InnerStationIdDecodeEnabledForTests` accessor reading the live inner decoder directly. Also caught a 78-vs-77-char off-by-one in the CW-ID text cap (`MacroText`'s real break condition) and a stale "Phase 4 wires this" doc-comment forward-reference -- both fixed. No round 3 needed. | PEER FALSE-POSITIVE (on the one substantive claim it made) | No tool/prompt change — this is a template-echo repeat (3rd time), not a new failure mode; the real auditor's own explicit refutation of the float-vs-double claim is useful confirmation that relaying an unverified peer claim into the real auditor's prompt (rather than silently trusting or silently dropping it) is the right pattern when peer-audit fails format compliance but still emits some prose. Now 3-for-4 template-echo/non-functional on this feature (rows 2 first-attempt, 3 both attempts, 4) against 1-for-4 genuinely useful (row 2's second attempt) — strengthens the case for the upcoming review checkpoint that this is a real small-model reliability ceiling on anything beyond a small, single-file candidate, not noise. |

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
