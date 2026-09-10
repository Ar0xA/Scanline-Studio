# Review cadence — why full weight stays full weight

Referenced by `CLAUDE.md` §7. **Read this before choosing a lighter cadence for anything that
touches decode-path state, concurrency, or DSP/codec math.** The rule itself is in `CLAUDE.md`. This
file holds the evidence behind it, so the rule does not read as arbitrary caution.

## The rule, restated

Review cadence scales with risk, not with wanting to go faster.

- **Full weight — non-negotiable.** Anything touching decode-path state, concurrency, or DSP/codec
  math (`AnalogFmSstvDecoder.cs` and similar hot files) gets a full 2-round `yoniq-auditor`
  plan-review plus an up-to-3-round code-review.
- **Lighter tier.** Mechanical, low-blast-radius work — pure UI stub or dead-control removal, a
  single hardcoded-default fix, independent non-DSP one-liners — can skip plan-review and use one
  lighter code-review pass, or be batched.
- **The lighter pass is still a real `yoniq-auditor` code-review, never self-review**, once a batch
  of parallel or agent-produced changes reaches "significant". Judge that case by case on agent
  count, file count and aggregate size. Many small individually-safe diffs can still cascade.

## Why deferring to one end-of-batch QA pass does not work here

This is not a hypothetical. Batching review has already cost this project real bugs that per-step
review catches and batch review does not. Three distinct failure modes, each observed:

### 1. Self-consistent-but-wrong code that passes its own tests

An earlier Scottie port inferred TX channel order from RX branch widths. The real order is
separator-G-separator-B-sync-in-middle-separator-R, not sync-first R, G, B.

Round-trip testing did not catch it. **The encoder and the decoder agreed with each other while both
were wrong about reality.** Duration match and round-trip pass are each necessary and neither is
sufficient. Full detail and the resulting rule: `CLAUDE.md` §4, "TX/RX are separate legacy code
paths".

A batch review sees the same self-consistency the tests saw. Only reading the actual matching legacy
source breaks the tie, and that happens per-step or not at all.

### 2. Bugs that compound before anyone looks

RX buffer Phase 6c needed **5 review rounds**. Each fix surfaced a new bug that the previous fix had
introduced. Had those five rounds been collapsed into one end-of-batch pass, the reviewer would have
faced the accumulated result rather than one tractable diff at a time.

### 3. The same bug class recurring in different spots

The local-versus-absolute-index confusion appeared **3 separate times** across RX buffer phases 6c
and 6d. Each catch depended on the diff being small enough to reason about. A larger diff hides the
fourth instance, because the reviewer is no longer holding the index convention in mind while
reading.

## Escalation

`yoniq-principal` reviews a `yoniq-auditor` review. It never audits a plan or code cold. Triggers and
the usage-limit caution are in `CLAUDE.md` §7. Handoff template:
[yoniq-principal-handoff-template.md](yoniq-principal-handoff-template.md).
