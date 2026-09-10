# Improving on legacy — the evidence bar

`CLAUDE.md` §0/§0a establishes that a **proven** improvement outranks legacy's engineering choice.
This file defines what counts as proven, so "proven" does not quietly degrade into "argued".

Adopted 2026-09-10, by user decision, replacing the previous blanket "legacy is always right".

## Why the rule changed

The old rule was a reaction to a real incident: an earlier Scottie port *inferred* behaviour instead
of reading legacy, got it wrong, and round-trip tests could not see it. The fix — treat legacy as
ground truth — was correct for that failure.

But it over-generalised. It also blocked changes that were **measurably better**, on the sole ground
that legacy did something else. That is a wrong answer, and this project has already shipped a
counter-example: the H3/HBPFN narrow-mode filter was reverted for lack of measured benefit, then
reinstated when a noise sweep found a 7-13 dB improvement to the noise floor on 6 of 6 modes with no
side effects.

The distinction the old rule missed: **legacy is authoritative about what the protocol IS. It is not
authoritative about what is BEST.**

## The line that does not move

**Anything wire-observable stays legacy-bound, regardless of measurement.**

That means anything another station transmits or expects:

- TX channel order and sync placement
- Per-mode timings and line durations
- VIS codes, extended-VIS and narrow-mode codes
- Frequency plans, including the narrow 2044-2300 Hz plan
- Image dimensions per mode

These are not choices legacy made. They are facts about what is already on the air. A "better"
channel order makes us unreadable to everyone, and **our own tests will report success**, because we
would decode our own transmissions with the same wrong assumption. That is exactly the Scottie
incident, and it is why `LegacyTxChannelOrderTests` and `LegacyRxChannelMappingTests` exist.

If you believe a wire-observable change is warranted, that is a product decision about breaking
interoperability, not an engineering improvement. It goes to the user, not into a commit.

## What is open

Everything else. In rough order of how often it comes up:

- **RX interpretation.** How we read someone else's signal. Decoding more accurately than legacy is
  pure gain — we are not changing what goes on the air, only how well we understand it.
- **DSP internals** that do not alter transmitted output: filters, demodulators, sync tracking,
  buffering, slant correction.
- **Anything off-air entirely**: UI, storage, logging, error handling, image editing, settings.
  These were never really in scope for the old rule anyway.

**TX-side numerics that are not wire-observable are open too, and this is subtler.** The green cast's
Fault A (`docs/known-decode-defects.md` §1) is legacy's truncation in the colour maths, currently
matched deliberately. It shifts a level by about 2 parts in 255. No receiver keys off it and no
station can tell. Under this rule it becomes a legitimate candidate — a measurement showing rounding
is closer to the source picture, with no regression, would now win.

That makes Fault A legitimate. It does not make it scheduled. The decode-quality improvement
workstream is closed by user decision (`BACKLOG.md` §8), and this rule does not reopen it. Nothing
here is authority to start work — it is the bar that work must clear once someone decides to start.

## The bar

All five, or it is not proven:

1. **A stated baseline.** What the number is before the change, measured the same way. "It looks
   better" is not a baseline.
2. **A reproducible method.** Someone else can run it and get the same answer. Committed harness or
   documented steps, not a one-off local script that no longer exists.
3. **Full affected scope.** If it touches decoding, measure every affected mode, not a chosen few.
   Sampling the best case is how a null result gets reported as a win.
4. **No regression elsewhere.** A gain in one place paid for by a loss in another is a trade to put
   to the user, not an improvement to ship.
5. **Recorded durably.** The measurement goes in `docs/`, with enough detail to be re-run and
   re-argued later. A number in a commit message is not retrievable.

For DSP and filter changes, the existing rule still applies on top: ship it behind a visible toggle,
and default-on additionally requires clear gain **and** zero degradation across all 43 modes.

## What is not proof

Stated plainly, because each of these has been offered before:

- "This is cleaner / more idiomatic / what modern code does."
- "The standard says X." Legacy **is** the de-facto standard here; a document that disagrees with
  what stations actually transmit does not outrank them.
- "The round trip still passes." It would pass with a symmetric error too. That is the whole point.
- A measurement whose method was not written down, or whose baseline was measured differently.
- A measurement on one mode, one source image, or one SNR, presented as general.

## Two failure modes this project has actually hit

Both are on record and both are cheap to repeat, so check for them explicitly:

**Measuring the wrong thing.** The AFC retune extension first measured as a null result. The
methodology was wrong — a sample-rate mismatch — and a corrected measurement found a real, narrow
improvement. A null result deserves the same scrutiny as a positive one before it closes an item.

**Believing a stale claim.** Multiple items in this repo were carried as open for weeks after being
fixed, and others were closed on premises that had become false. Re-verify the baseline against
current source before measuring against it.
