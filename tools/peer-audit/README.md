# Local peer-auditor

A cheap, fast, local-only *first pass* for functional/port-equivalence review,
complementary to the real `auditor` subagent (Opus). It is not a replacement:
every output is watermarked `UNVERIFIED` and must be human-verified against
real source before being trusted or acted on.

## Why this exists

A generic security-vulnerability scanner (see the sibling `Local-LLM-bugbounty`
experiment) run against this codebase's DSP layer produced ~100% false
positives — small local models pattern-match on OWASP/CWE vocabulary against
isolated snippets with no reference to compare against. Swapping to
functional/port-equivalence framing (the same checklist and reasoning style
the real `auditor` subagent uses), paired with real legacy-vs-ported grounding
and real read-only file access, produced meaningfully better results in
manual testing: correct verdicts and well-calibrated findings on two known-good
ports, including one finding that reproduced the exact nuance the human/Opus
audit trail had already independently reached.

## Usage

```
python3 peer_audit.py \
  --legacy-file fir.cpp --legacy-lines 40-74 \
  --candidate-file ScanlineStudio.Core.Sstv/TankFilter.cs --candidate-lines 8-41 \
  [--note "extra grounding, e.g. a constructor line elsewhere that sets a flag"] \
  [--model qwen2.5-coder:14b]
```

- `--legacy-file` is relative to `yoniq-old/YONIQ-main/` (or an absolute path
  under it).
- `--candidate-file` is relative to `src/` or `tests/` (or an absolute path
  under either).
- Requires `ollama serve` running locally with the model already pulled
  (`ollama pull qwen2.5-coder:14b`). No other dependencies — standard library
  only.

Output goes to stdout and to `out/<timestamp>.md` (gitignored — verdicts never
enter git history through this tool). The output header records: model +
digest, sampling params, repo git SHA, every tool call the model made (so a
human can see exactly what it looked up, not just trust its claims), and a
`done_reason` truncation flag.

## What it does that a blind scan can't

- Gives the model real read-only access to the legacy tree and the candidate
  tree (`read_file`, `grep`) instead of a single isolated snippet — it can
  chase a macro definition, a caller, or the matching TX/RX counterpart.
- A deterministic (non-LLM) preprocessor pre-scanner flags whether the exact
  legacy line range sits inside an `#if 0`/`#ifdef`/`#else` block *before* the
  model ever sees it — this was empirically necessary: the model missed a
  live `#if 0`/`#else` pair even with the guard fully inside its context
  window in initial testing, so this is not a redundant safety net.
- CP932 (Windows-31J) decoding for legacy files, never assumed UTF-8.

## Known limitations (do not build false confidence)

- **Ollama's native `tool_calls` field does not work reliably with this
  model** — verified empirically (3/3 at temperature 0): the model emits
  correctly-shaped JSON but skips the `<tool_call>` wrapper its own chat
  template requires, so Ollama never populates the structured field. This
  tool works around it by parsing the model's plain-text JSON directly. If
  you change models, re-verify this before trusting tool calls silently.
- Tool budget is capped (`MAX_TOOL_ROUNDS` in the script) to bound cost and
  guard against a small model looping without converging. A run that hits the
  cap is marked `INCOMPLETE` and exits non-zero — never silently returned as
  a normal verdict.
- The "enclosing function" echo and brace-balance check are textual
  heuristics, not a real parser. They catch obviously wrong ranges, not all
  of them.
- Only tested so far on two already-known-correct ports (see below) —
  measures that the tool doesn't scream at good code (specificity), **not**
  that it reliably catches real defects (recall).

## Before trusting this for regular use: run the negative-control gate

Both validation cases so far were deliberately known-correct ports. That
proves the tool isn't noisy on good code — it says nothing about whether it
would actually catch a real bug. Before relying on this tool's "no issues
found" as meaningful, run it against several **deliberately mutated**
candidates and confirm it flags them:

- Flip a sign in a filter coefficient
- Swap two elements' order (e.g. a TX channel sequence)
- Change a `short` to `int` (or vice versa) somewhere that matters
- Truncate a loop bound by one
- Drop a denormal/edge-case clamp (e.g. a `fabs(d) < epsilon` guard)

Detection rate on a small negative-control set like this is the real
acceptance gate for trusting this tool's silence, not just its noise level on
correct code.
