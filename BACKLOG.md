# BACKLOG — the one list of work that still needs doing

**This is the only backlog in the repo.** If a task is not here, nobody is meant to be working on it.
Every other document records measurements, decisions, design rationale or history.

Built 2026-09-10 by sweeping `production_audit.md`, `astra-audit.md`, `docs/known-decode-defects.md`,
`docs/functional-audit-playbook.md`, `spec/14-roadmap.md`, `spec/18-path-to-1.0.md`,
`spec/19-path-to-1.1.md`, `spec/15-template-designer.md`, `fsk_cwid.md`,
`decoder_quality_improvement.md`, `~/.claude/plans/`, and a `TODO`/`FIXME` grep over `src/` and
`tests/` (which returned **zero** markers).

**Every item below was re-verified against current source on 2026-09-10**, not copied forward on
trust. Where the source disagreed with the old entry, the entry is corrected here and the correction
is stated. Items that turned out to be already done are listed at the bottom so nobody re-derives
them.

**No known defect gates a release.** The transmitter-safety and wrong-data defects are fixed and
merged (`master`, 2026-09-09).

## How to read this

| Column | Meaning |
|---|---|
| ID | Stable handle. `PA-n` = from `production_audit.md`. `TT1-n` keeps its original test-suite ID. |
| Review tier | Per `CLAUDE.md` §7. "Full" = 2-round plan-review plus up-to-3-round code-review. |

---

## 1. Decode path — do the probe first

### D1. CLOSED 2026-09-10 — Fault B is LEGACY BEHAVIOUR, not a port defect

**Measured against real legacy audio**, `LegacyGreenBiasProbe.cs`. Legacy's own decode and ours both
run over the same legacy-captured `.mmv`, scored against the image legacy actually transmitted, using
§1's own metric `G_err - (R_err + B_err)/2`:

| mode | legacy | ours | ours - legacy |
|---|---|---|---|
| robot-36 | +12.8 | +13.0 | **+0.2** |
| robot-72 | +13.3 | +13.3 | **+0.0** |
| pd90 | +4.8 | +4.9 | **+0.1** |
| mn110 | +3.8 | +3.8 | **+0.0** |
| martin-m1 (RGB control) | +0.1 | -0.1 | -0.2 |
| scottie-s1 (RGB control) | -0.6 | -0.5 | +0.1 |

**Legacy carries the green cast in equal measure. Our deviation is 0.0 to 0.2 — noise.** The two
RGB-sequential controls read near zero, matching §1's documented +0.1, which validates the metric
rather than the result.

So the DSP-chain investigation this item called for would have been chasing legacy's own behaviour.
**It is a §0a improvement candidate, not a defect**, and it needs the full evidence bar in
`docs/improving-on-legacy.md` if anyone ever wants it. Note the bias is larger than §1's +4.7 on these
gradient fixtures (robot-36 reads +12.8), so it is content-dependent — any improvement work must state
its source image.

**How this closed in twenty minutes what was filed as a full-review-tier investigation:** by asking
"does legacy do this too?" FIRST. The D2 edge-stripe arc spent a whole session and four rejected fix
designs before asking the same question, and got the same answer. See
[[feedback_legacy_is_good_enough_ship_the_app]].

### (superseded) D1 original filing

**The probe ran 2026-09-10 and the answer was the expensive branch.** Ideal transport reads **+2.4**
where the real chain reads +4.7, so Fault B is **not** protocol-side or TX-side and does **not** close
as a parity decision. The two RGB-sequential controls came out at +0.0 and +0.1 against a documented
+0.1, which is what validates the measurement.

Harness: `tests/ScanlineStudio.Core.Sstv.Tests/IdealTransportGreenBiasProbe.cs`, gated behind
`SCANLINE_SOURCE_BMP`. Full table and reasoning: `docs/known-decode-defects.md` §1, Fault B.

**What is left is a real defect in the DSP chain**, worth about +2.3 of green bias on every YCbCr
mode — most real colour traffic. The probe removed modulation, filtering and demodulation and the
fault vanished, so it lives in one of those three.

**First place to look, not the last:** the probe samples an exact frequency at each point with no
windowed averaging, so the demodulator's own settling, group delay and amplitude response are still
unexamined — and the chroma segments carry the shortest dwell times in the mode. §1 already rules out
the output smoother (flat against cutoff) and any single demodulator (Hilbert +4.7 against
zero-crossing +5.6), so a shared upstream stage is more likely than either.

Review tier: **full** — this is DSP/codec math. Sizing note: this is no longer a cheap item, and it
now competes with D2 rather than preceding it. D2 is the one a user can actually see.

### D2. MN/MC right-edge stripe — SOLVED 2026-09-10, now a decision, not an investigation

**The mechanism is settled and measured.** On the real chain with a FLAT grey source, mn140 and mn73
render their last pixel column exactly black on every odd row — the rows carrying Y2, the last scan
segment before the next line's sync. The demodulated frequency there is **2029.5 Hz** against grey's
2172.0, a **-142.5 Hz** pull, below the 2060.0 Hz point where `YCbCr.cs:49` clamps to black. mp140 is
pulled only 22.5 Hz and stays 327 Hz clear of its own threshold.

**The discriminator is the SIZE of the pull (6.3x), not band sensitivity.** MN's 1900 Hz sync sits
mid-passband in the narrow RX bandpass at full amplitude; MP's 1200 Hz sits at the wide filter's
lower cutoff and is attenuated. §4's "3.1x more level-sensitive per Hz" reasoning is refuted — the
sensitivity is cancelled by MN's smaller step to its own sync.

**Two things this overturned.** The stripe does NOT need image content, and it does NOT follow the
narrow plan alone — `martin-m1` is wide-band and has its own last-column defect (54.6 against 128,
no clamping, no parity split). That second defect is filed as **D4** below.

**It is legacy-faithful**, so this is no longer a bug report. 2029.5 Hz sits inside legacy's
representable [1916, 2428] Hz window, so legacy computes the same near-black. Under the old rule that
closed the item. **Under `CLAUDE.md` §0a it does not** — nothing here is wire-observable, so a
measured improvement wins. **This needs a user decision, not more investigation.**

**Do NOT reintroduce legacy's 16-bit demod buffer.** `yoniq-auditor` rated the `short`-versus-`double`
width a blocker; `yoniq-principal` overturned it. Out-of-range `double`-to-`short` is undefined
behaviour, so "legacy renders it white" is compiler-dependent, not a fact about legacy. The
divergence is documented, not actioned.

Full evidence: `docs/known-decode-defects.md` §4. Harness:
`tests/ScanlineStudio.Core.Sstv.Tests/NarrowModeEdgeRealChainProbe.cs`.

### D2-FIX. OPEN, but DOWNGRADED in urgency 2026-09-10 — user still wants a fix if one is viable

**User's assessment, after seeing 8x crops of the real artefact:** "noticable but not horrible,
considering legacy does the same... not super noticable when a full picture is send unless you zoom
in on it." Followed by: **"i still want to fix it if we can."**

So this is a priority downgrade, NOT a closure. The stripe is 1 to 4 pixels — 1.25% of width on a
320-wide mode — and it matches legacy on every mode measured against real legacy audio, so nobody
receives a worse picture than the reference implementation gives them. That removes the urgency. It
does not remove the goal.

**The blocker is measurement basis, not willingness.** A per-mode correction needs a non-circular
attribution of registration to the receive path, and the only such basis today is real legacy audio,
which covers 8 of 43 modes. A round trip through our own encoder cancels shared errors — `pd90` reads
0.91 px that way against 13.95 px on real audio.

**THE MEASUREMENT BLOCKER IS GONE.** `IdealAudioRegistrationProbe.cs` now reports all 43 modes with
**0.00 px spread** across three lead-ins. It synthesises the stimulus from each mode's own segment
table (so our encoder contributes nothing), pushes it through the FULL production decoder, and uses a
hard-edged source so the estimator is not degenerate. Six instruments were needed; the first five each
failed for a different structural reason, all recorded in this file.

Ten modes sit beyond one pixel: `mc110` -5.23, `avt` +4.91, `mc140` -4.50, `rm8` -3.72, `mc180` -3.45,
`mn73` -3.25, `rm12` -3.19, `mn110` -2.33, `pd180` and `pd290` -2.07.

**So the fix is now buildable, and the remaining question is only whether it is worth building.** The
artefact is 1-4 px, matches legacy, and is not visible on a full picture without zooming in. See
[[feedback_legacy_is_good_enough_ship_the_app]] before reopening this.

**If it is ever built, the shape is settled** by three plan-review rounds and two principal rounds:
1. Separate the ANCHOR error (varies per lock) from the READ-SCHEDULE offset (fixed per mode). Only
   the second is correctable by a constant.
2. Shift the read grid by that deterministic part, in ONE shared helper across the five decoders,
   indexed per (mode, scan segment) — never per output channel.
3. Verify WITHOUT measuring registration: uniform white, count deviating leading and trailing columns
   per mode. That is the artefact itself, and it diagnoses cause for free — trailing-only means late
   reads, leading-only means early, both means a filter transient.
4. Per-mode opt-out at zero, because correcting the grid must expose the leading edge and that trade
   has to be measured mode by mode.
5. Re-measure at **11025 Hz**, the production and legacy default. Everything so far is at 44100, and
   column contamination scales with pixel pitch in samples.

**Still open as genuine PORT DEFECTS**, because they diverge from legacy rather than match it — see
D5 below. Those are separate from this parked item and need no §0a argument.

Everything below is the investigation record. It stopped four wrong fixes, each of which would have
shipped a regression: a coloured fringe on Martin, a cosmetic patch over a registration error, a
replay double-correction across ~20 modes, and a correction table built on a circular measurement.

### (record) D2-FIX investigation — plan-review round 1

**User approved fixing this 2026-09-10** under `CLAUDE.md` §0a: it is RX interpretation only, nothing
wire-observable, so a measured improvement outranks legacy's behaviour.

**Scope is far larger than D2 described. 37 of 43 modes are affected** on a flat grey source at
44100, losing 1 to 4 trailing columns. Only `avt`, `mp140`, `mp175`, `p7`, `pd90` and `scottie-s1`
read clean, and flat grey is the WEAKEST case, so some of those six are probably not clean either.
All 43 modes decoded; no decode failures. Worst rows: `martin-m2` 4 columns at 104.2, `robot-36` 2 at
78.1, `r24` 3 at 72.6, `martin-m1` 1 at 73.4, `p3` 2 at 69.5.

**`yoniq-auditor` plan-review round 1: NOT READY. Two design blockers.**

1. **Repair width must be per (mode, SCAN SEGMENT), not per mode.** The sweep's metric is a
   3-channel mean, so one fully-clamped channel caps at 42.7. `martin-m2`'s 104.2 and `martin-m1`'s
   73.4 are arithmetically impossible from one channel — Martin and Pasokon put a separator after
   EVERY channel, so all three tails are pulled. Repairing only the final segment would leave G and B
   about 90 levels dark and turn a grey bar into a **saturated coloured fringe**.
2. **The width depends on `RxBpfPreset`, a live user setting**, whose group delays are 1.09 / 2.90 /
   4.35 ms — a 4x span. A table fitted to the default under-reaches at VeryNarrow, and an
   under-reaching replication is **worse than no fix**: it copies a still-contaminated pixel across
   the whole tail.

**Mechanism adopted instead of a static table:** compute the width at decode time from the live
filter chain's reach divided by the pixel pitch, minus the trim headroom already in
`GetPixelPitchTrimFactor`. That self-tracks preset, demodulator, sample rate and new modes. The
measured table demotes to a test fixture pinning the formula's output.

**Refuted along the way.** Width is essentially rate-invariant at standard rates (both FIRs scale tap
with rate), so it must NOT be keyed on sample rate. And `scottie-s1` is not structurally clean — its
R tail IS followed by a separator, it only looks clean on grey.

**Round 1's required measurements are DONE (flat WHITE, per channel, both edges, all 43 modes).**
Both blockers confirmed, one auditor prediction refuted:

- **38 of 43 modes affected. 23 have MORE THAN ONE channel corrupted at the tail.** Blocker 1 is
  measured, not argued: `martin-m1` reads tail R=1, G=1, B=1 and `martin-m2` reads 3, 4, 4.
- **9 modes have LEADING-edge corruption too** — `robot-36`, `robot-72`, `rm8`, `mr73`, `mr90`,
  `ml180`, `pd120`, `pd50`, `mp140`. The symmetric-reach prediction holds. A trailing-only repair
  would leave the picture asymmetric.
- **White is far worse than grey, as predicted.** MC modes lose their B channel completely (error
  255.0 against 42.7 on grey). `martin-m2` reads 253.8.
- **The corrupted channel set names the corrupted SEGMENT.** MC and MR/ML show B only (0,0,4) — one
  segment, the one abutting the sync. Martin and Pasokon show all three, because they put a separator
  after every channel. For YCbCr modes one bad segment spreads across all three output channels, so
  the repair must be indexed by scan segment, never by output channel.
- **`avt`'s 0 is VACUOUS, not clean.** AVT has no sync, porch or separator — three back-to-back scan
  segments — so on a flat source the whole line is one constant tone and the probe is structurally
  incapable of measuring it. On a real image AVT's R tail abuts the same line's G head, so its right
  edge carries G's left-edge content. Open, not safe to skip.
- **REFUTED: the auditor predicted `scottie-s1` would stop being clean on a white edge. It did not** —
  0 on every channel, both edges. Its separator apparently shields it. `mp175`, `p7` and `pd90` are
  also clean on white.

**Round 2 + `yoniq-principal`: STILL NOT READY, and the fix direction is now in question.** Two
blockers, both arithmetic, both with premises the principal verified independently:

1. **The reach-divided-by-pitch formula is refuted by the data it would be fitted to.** MC110/140/180
   share a decoder, a filter, a band and trim group C, so a fixed sample depth must fit all three.
   The required intervals are [77.2, 96.5), [99.2, 124.0) and [127.9, 159.8) samples — **disjoint**.
   As a fraction of scan duration all three agree at 1.25-1.56%. A fixed-tap filter's reach cannot
   scale with duration. (The principal narrowed this: three column-quantised points rule out a fixed
   count but do NOT prove proportionality.)
2. **The clean leading edge is physically impossible under smear.** Pixel 0 reads at the segment
   boundary with ZERO guard, against a chain window of about ±72 samples, so half of pixel 0's window
   is foreign tone in every mode — yet 34 of 43 read exactly 0. The principal confirmed the metric is
   not hiding it: on white every preceding tone is lower, so lead smear would darken and register.

**The explaining hypothesis: the read indices are systematically LATE by roughly the chain's group
delay**, which would clean every lead and dirty every tail — exactly the measured asymmetry.

**Critically, that is legacy-identical, not a port bug.** The principal verified the port's anchor
(`SyncAnchorCorrector.cs:131` plus `AnalogFmSstvDecoder.cs:5262-5265`) reproduces legacy's
`argmax - OFP + htap/4` (`Main.cpp:3777-3795`) exactly, sign included. Sync and picture DO run
through different group delays — sync through a 100 Hz resonator and 50 Hz Butterworth, picture
through the Hilbert and an 1800 Hz smoother — and legacy absorbs the differential with an EMPIRICAL
per-mode offset, not a computed one. Any residual is that tuning's leftover. **So correcting it is a
§0a improvement decision needing the full proof bar, not a bug fix.**

**Do NOT ship the replication patch first** — it would bake the bias into its width table and hide
the registration error underneath.

### MEASURED 2026-09-10 — it is REGISTRATION, not smear. Every mode reads LATE.

`HorizontalRegistrationProbe.cs`, half-black/half-white source, sub-pixel edge detection, all 43
modes. Three results:

1. **Drift is RULED OUT.** Max first-8-rows against last-8-rows difference is 0.35 px (`rm8`); every
   other mode is within 0.06 px. The offset is constant down the picture.
2. **Every mode reads LATE**, from 0.03 ms (`robot-72`, essentially perfect) to 3.67 ms (`avt`). In
   pixels that is 0.05 to 9.39. `robot-36` and `robot-72` are the only modes registered correctly.
3. **The offset predicts the contamination.** `ceil(|offset in pixels|)` matches the measured
   contaminated tail count within ±1 for **34 of 43 modes**.

**This explains both round-2 blockers at once.** The offset is roughly constant in MILLISECONDS
within a family, and pixel pitch differs between family members, so the contaminated PIXEL count
varies while the sample depth does not — which is exactly why no fixed sample count fitted MC110/140/
180 ([77.2, 96.5) against [127.9, 159.8)). Their offsets are -2.248, -2.521 and -2.534 ms: the same
time, three different pixel counts. And reads being LATE is precisely why the leading edge is clean
in 34 of 43 modes despite having zero guard.

**So the reads run past the end of each scan segment into the following tone.** That is the stripe.
It is not the filter smearing backwards; it is the sampling grid sitting in the wrong place.

**Nine modes are not explained by registration alone**, and they matter:
`ml180`/`ml240`/`ml280`, `r24`, `mr73` predict 1 and measure 3-4 — all YCbCr-sequential modes whose
final segment is CHROMA, which has its own pitch. `robot-36` predicts 0 and measures 2, and its
offset is essentially zero, so it is the one genuine smear case. `martin-m1` and `mc110` are
over-predicted. `avt`'s row is vacuous (flat source).

**What this changes about the fix.** Replicating pixels would paper over a sampling-grid error that
also shifts EVERY decoded picture horizontally by 0.3 to 5 pixels. Correcting the registration fixes
the stripe and the alignment together. The principal called this before the measurement existed.

**It stays a §0a decision, not a bug fix** — the anchor is verified legacy-identical, so this is
improving on legacy's empirical tuning and needs the full proof bar.

**Superseded plan:** `HorizontalRegistrationProbe.cs`, a half-black/half-white
vertical edge per mode, reporting the decoded edge offset in samples with sub-pixel interpolation,
split first 8 rows against last 8. Constant across rows and modes means an anchor residual and one
decode-side constant fixes it. Growing with row means drift, and neither the patch nor an anchor
shift helps. Similar in milliseconds but not samples means a per-mode offset residual.

**The probe's metric is also under suspicion** and must not be the acceptance gate: it counts only a
contiguous run inward from the edge, its threshold scales with the mode's own baseline, and it
averages over all rows, which dilutes any per-line-alternating contamination about 2:1. `robot-36`'s
tail is therefore an UNDERESTIMATE — on white, its 2300 Hz selector is identical to the picture, so
only half its lines can contaminate at all.

Harness: `tests/ScanlineStudio.Core.Sstv.Tests/EdgeContaminationSizingProbe.cs`.
Ships **default off** behind a visible toggle. Default-on needs clear gain and zero degradation across
all 43 modes. Review tier: **full**. **The user wants to verify the result visually before it lands.**

### D5. RETRACTED 2026-09-10 — the divergence figures came from a broken instrument

**Do not act on this item. Its numbers are wrong.** It claimed `avt` (6.40 px), `rm8` (0.90) and
`mn110` (0.30) diverge from legacy's own decode of the same audio. `yoniq-principal` then found two
defects in the instrument that produced them: the shift search radius clamped `pd90`'s fit at the last
admitted candidate, making its "matches legacy" verdict vacuous, and squared-error fitting on the
gradient fixtures confuses a luma GAIN difference for a displacement — a 4% gain error reads as about
6 px on a 320-px ramp, which is `avt`'s entire reported divergence.

Replacing the fit with normalised correlation did not rescue it: correlation on a near-linear ramp is
nearly flat across shifts, so the peak is noise. All 8 fixtures are smooth gradients, so **no shift
estimator works on them.** Measuring our RX against legacy's RX needs a fixture with sharp features,
which would mean new legacy captures.

**What survives from that probe** is its edge-stripe measurement, which involves no fitting: legacy's
own decode carries the stripe on all 8 captured modes, ratios 1.3x to 12.1x. That result stands and is
what proved the artefact is legacy-faithful.

### (retracted) D5 original filing

Found 2026-09-10 while investigating D2. Distinct from that parked item: these three do **not** match
legacy, so they need no §0a argument. They are bugs.

Measured by `LegacyAudioRegistrationProbe.cs` against real legacy `.mmv` captures, comparing our
decode of legacy's own audio with legacy's own decode of the same audio. Five of eight captured modes
match within 0.10 px, which is what makes these three stand out:

| mode | ours - legacy | worth fixing |
|---|---|---|
| **avt** | **6.40 px** | yes — the whole picture sits in the wrong place |
| rm8 | 0.90 px | marginal |
| mn110 | 0.30 px | no, unless it shares a cause with one of the others |

**`avt` is the one that matters and it has a contained cause to look at.** AVT has no sync segment at
all — legacy's `SyncSSTV` early-returns for it (`Main.cpp:3754-3758`) with `m_OFP = 0`, and
`AdjustSyncPos` leaves it in `default:`. Its registration comes entirely from the header timer chain
(`sstv.cpp:2140`, then `:2155-2209`). So a 6.40 px error is a picture-start error in that chain, not
a per-line sync residual, and it shifts the whole image rather than dirtying an edge.

Pass criterion is unambiguous and already automated: our decode of `avt.mmv` must land where
`avt_RX.bmp` lands. Review tier: **full** — decode-path state.

### D6. CLOSED 2026-09-10 by user decision — clamp kept, no behaviour change

`AnalogFmSstvDecoder.cs`'s `Math.Max(0, origin + delta)` deviates from legacy. That much is settled:
`yoniq-auditor` traced both sign conventions and returned NOT EQUIVALENT. Legacy's
`if (n<0) continue` (`Main.cpp:4146`) fires on `m_rBase < 0`, which is a POSITIVE correction in this
port and never reaches the guard. This branch matches legacy's `m_rBase > 0`, where legacy keeps every
pixel in its correct column and row. **Clamping discards registration where legacy preserves it.**

**Closed anyway, and the reason is the point.** The fix was written — wrap by `+(int)lineWidthSamples`,
which is legacy's own AutoSync idiom (`Main.cpp:3919`) and already ported in `TriggerAutoSync`. It
built, and five modes passed. Then the mutation gate failed: **restoring the clamp changed nothing any
test could detect.** Not at mid-picture, not at the top rows, where auto-sync has not yet corrected.
The branch demonstrably executes (2 of 5 modes in a provoking harness), and the predicted failure — a
vertical seam up to ~65% of image width on Scottie — did not reproduce at any magnitude.

A behaviour change to decode-path state that no test can distinguish from the status quo is not worth
shipping. The user's call, and the right one.

**Reachability, which is narrower than first assumed.** The original theory — a user tuning in
mid-transmission — is **REFUTED**. VIS lock anchors at header end (≥1 s of samples), narrow FSK
anchors past the window, and the sync-bypass anchor is peak-derived so its correction is ~0 by
construction. The only production path is `ForceMode` pressed within ~0.28 s of a fresh decoder's
first sample. The user reports many recordings with Scanline and has never seen it.

**What DID land:** the comment above that line asserted the clamp was legacy-equivalent. It is not,
and a false parity claim in decode-path code outlives whoever reads it next. The comment now records
what the clamp actually does, that it deviates, why it was kept, and that the wrap is a ~3-line change
if anyone revisits.

### (superseded) D6 original filing

Found 2026-09-10 by `yoniq-principal` while diagnosing a measurement instrument, then confirmed
against source. **Latent in production, not just in tests.**

`AnalogFmSstvDecoder.cs:5274` computes `_consumedSamples = Math.Max(0, origin + delta)`. When the
resolved anchor delta is negative, that **clamps to zero** rather than wrapping by one line duration.
The picture then locks at an arbitrary phase within the line instead of at the line start.

**Reachability:** it needs a lock within roughly `preSyncSegmentOffsetMs + OFP` of decoder
construction. Worst case is the Scottie family at about 0.3 s, because their tracked sync sits around
two thirds into the line, so the sum wraps most easily. A user who starts receiving immediately after
opening the app, on a signal already in progress, is the real-world shape.

**How it was found, which is also how to reproduce it:** `IdealAudioRegistrationProbe` called
`ForceMode` before pushing any samples, so the decoder committed origin zero and every subsequent
anchor delta was negative. 21 of 43 modes then returned lead-in-dependent garbage — `scottie-s1`
read 31.70 px at one lead-in and 124.95 at another. The arithmetic predicts each failure exactly:
Scottie DX at a 300 ms lead-in gives 300 + 694 = 994 ms, under its 1050 ms line, so no wrap and a
correct answer; at 620 ms it gives 1314 mod 1050 = 264, a delta near -430 ms, clamped, garbage.

**Fix:** wrap by `+TW` rather than clamping at zero. Review tier: **full** — decode-path state.

### D4. martin-m1's last column reads low — a second, separate edge defect

Found while solving D2, 2026-09-10. On flat grey 128, `martin-m1`'s last column decodes to **54.6**
uniformly, on both row parities, with no clamping. Mid-image error is 0.2, so the mode is otherwise
near-perfect on that source. Back-computes to roughly a 230 Hz downward pull.

Consistent with the same filter pre-echo as D2, acting on martin's 1500 Hz separator rather than a
sync. Not confirmed. **This is wide-band**, so it refutes the standing claim that the edge defect is
narrow-specific.

Not yet measured across other wide modes. Review tier: **full** — same DSP chain as D2.

### (superseded) D2's earlier framing — kept because its measurements still stand

**Probe run 2026-09-10.** The stripe does **not** survive ideal transport: every mode sits at
1.2-1.3x edge-to-mid, narrow and wide alike, against the real chain's 4.7x for mn73. Harness:
`tests/ScanlineStudio.Core.Sstv.Tests/NarrowModeEdgeColumnProbe.cs`.

**Ruled OUT, not merely deprioritised:** an end-of-line window running past the last pixel centre,
the sync search consuming samples the last pixels need, and any pixel-geometry error in the scanline
codecs. All three were the standing candidates and none of them is it.

**Still live:** the frequency plan, but acting through the DSP chain rather than through the codec.
MN's sync sits 144 Hz below its picture band where MP's sits 300 Hz below its own, so filter
transition and group delay can carry the approaching next-line sync transient into the picture band
early — and the right edge is where early lands.

**Next step is a real-chain experiment, not another ideal-transport run:** decode an MN line whose
following sync is replaced by silence or by MP's 1200 Hz, and see whether the edge error follows the
sync. Full table and reasoning: `docs/known-decode-defects.md` §4.

Everything below is the original entry, kept because its measurements still stand.



A coloured stripe on the right edge of every MN and MC decode. Six modes, deterministic, present with
all DSP options off.

Localized to one variable. MN140 and MP140 are structurally identical apart from sync 1900 against
1200 Hz, porch 2044 against 1500 Hz, and `LuminanceMin/MaxHz` 2044-2300 against the default
(`SstvModeRegistry.cs:418-438` against `:489-511`). MP140 is clean. **The defect follows the narrow
frequency plan and nothing else.** Start with that controlled pair on one source plus a clean
noise-free decode. Look at narrow-band and sync-tone handling, not a generic end-of-line off-by-one.

Detail: `docs/known-decode-defects.md` §4. Review tier: **full** — this is decode-path state.

### D3. Range-limit the output-cutoff control per demodulator

`OptionsWindowView.axaml:986` is a `NumericUpDown` bound to `PllOutputCutoffHz` with
`Minimum="1" Maximum="3900"`, and `OptionsWindowViewModel.cs:3413` applies the value live through
`RequestPllTuning`. `PllFmDemodulator`'s own clamp is `Math.Clamp(cutoffHz, 1.0, _sampleRate * 0.45)`,
which permits 4961 Hz at 11025 Hz. So 3600 Hz passes, and a user can reach it from the Options UI
today. At 3600 Hz on a clean signal the PLL demodulator reads R +26, G +43, B +21 and the picture
visibly breaks into a herringbone pattern. Hilbert and zero-crossing merely get grainy there.

**The safe range is not the same for all three demodulators, so one shared range is wrong.** Either
range-limit per demodulator or warn.

**Do not overclaim.** The original sweep's experiment code was never located, so which demodulator
cutoff that sweep actually changed is unconfirmed. Do not assert guaranteed PLL collapse, and do not
prescribe a new tuning range from that entry alone. Advanced tuning may legitimately permit poor
combinations. That argues for a range or a warning, not for closing the item.

**Contradiction to be aware of:** `production_audit.md:1337` still lists this as dropped and
unreachable. That entry rests on the false "no UI exposes it" premise, and `:1477` reopens it with
the verification above. The reopened version is correct.
Detail: `docs/known-decode-defects.md` §3. Review tier: **full** if the fix touches the demodulator.

---

## 2. Test-integrity work — the golden-vector guard does not currently run

### PA-1 / TT1-2. Golden-vector worst-row metric

`MeasureAveragePerChannelDelta` (`GoldenVectorTests.cs:966`) sums over the whole frame, and
tolerances run 1.99 to 13.76. One fully corrupted row out of 256 moves the average by at most 1.0, so
it passes today. Add a per-row max beside the frame average, measure the current worst row per
fixture, then pin at about 1.5x to 2x. **Needs its own measurement pass first** — do not bundle it
into another change.

### PA-2 / TT1-5. Mapping DONE 2026-09-10 — recapture gap is 4 functions, not 30

**The mapping question is answered.** 43 modes dispatch to **14 distinct `Line*` functions**
(`Main.cpp`'s TX switch, around `:7060`). The channel order for all 14 is transcribed into
`tests/ScanlineStudio.Core.Sstv.Tests/LegacyTxChannelOrderTests.cs`, which asserts the port's
registry against it — 44 tests, mutation-gated by renaming a scan segment.

**The port agrees with legacy on all 43.** The only two disagreements were my transcription, not the
port: `LineRM`'s two `// Y` loops are one transmitted scan, because the first loop only reads row N
and the second averages it with row N+1 before writing. Counting loop comments instead of `Write`
calls gives the wrong answer, and the table now records that.

**The recapture gap:** the 11 stale fixtures cover 10 of the 14 functions. **Four functions have
never been covered by any fixture** — `LineSC2180`, `LineP`, `LineMP`, `LineMC`. So the answer to
"5 fixtures or 30" is **4 new, plus revalidating 11 stale**, not 32.

**One fixture per function is enough for ORDER**, because the parameters those functions take
(`tw`, `S`, `P`, `C`, `ts`) are durations and porch widths — none selects or reorders a channel.
Timing still needs per-mode coverage; that is a different guarantee.

**Both halves of the Scottie incident are covered.** The original bug was wrong in channel order AND
sync placement, and comparing scan names alone catches only the first half. Each row also carries how
many scans precede the line's primary sync — 2 for `LineSCT`, 0 elsewhere, null for the syncless
`LineAVT`. Mutation-gated by actually moving Scottie's sync to the head of the line.

**A per-function golden capture would not retire this file.** A capture pins one function's internal
order; this table pins the mode→function **dispatch**. `LineMRT` and `LineSCT` have identical channel
order and different sync placement, which is exactly where inferring one family from another fails.

**What the table does NOT replace.** It compares the port against legacy *source*, not against
legacy's emitted *audio*. A shared misreading of the source would pass. The golden captures remain
the only thing that closes that, so this makes their absence survivable rather than acceptable. It
also pins no durations, only order and sync position.

**Audited.** `yoniq-auditor` verified all 43 rows against the legacy bodies independently, confirmed
the 14-function dispatch against `Main.cpp:7059-7189` and `sstv.h:450-495`, and confirmed that no
parameter of any of the 14 functions is read in a conditional — so "one fixture per function pins
order" holds. Verdict: go. The sync-placement gap was its one substantive finding and is now closed.

Original entry follows.



Then capture one TX golden fixture per uncovered function.

**Treat current TX channel-order coverage as zero, not as 11.** All 11 `TxCapture/*.provenance` files
read `UNKNOWN-STALE-PENDING-RECAPTURE`, `StaleFixtureTheoryAttribute` skips the comparison, and the
`.mmv` files are this port's own TX output rather than legacy-generated audio. The Scottie-class guard
therefore **does not currently run**.

Do the mapping first. It is mechanical, about 43 entries, zero design risk, and it is the only thing
that tells you whether the real gap is 5 fixtures or 30. `CLAUDE.md` §3 forbids assuming a sibling
mode shares a covered mode's channel order. Biggest single structural gap in the test suite.

### TT1-18 / PA-7. Convert the 4 remaining silent-pass tests to `[SkipOnWindowsFact]`

**Partly done — re-verified 2026-09-10, and the paths in the old entry were wrong.**
`JsonSettingsStoreTests.cs` is converted (`:230` and `:281`), and `ScanlineStudio.Settings.Tests`
already carries its own copy of the attribute.

Still a bare `if (OperatingSystem.IsWindows()) return;`:

1. `tests/ScanlineStudio.Settings.Tests/AppLocationOverridesTests.cs:47`
2. `tests/ScanlineStudio.Settings.Tests/AppLocationsServiceTests.cs:148`
3. `tests/ScanlineStudio.Settings.Tests/AppLocationsServiceTests.cs:241`
4. `tests/ScanlineStudio.Host.Tests/ApplyPendingRelocationsTests.cs:219`

Only **Host.Tests** still needs its own copy of the attribute. These tests report **passed**, not
skipped, so they are vacuous rather than honest. They rely on `File.SetUnixFileMode` denial, which
root ignores — **check what user the Linux CI leg runs as first**, because if it runs as root these
are vacuous everywhere, not only on Windows.

### PA-5. Assert every `{loc:Translate X}` key resolves

Extend `tests/ScanlineStudio.UI.Tests/NoHardcodedAxamlStringsTests.cs`. It holds one test today, and
that test only checks the reverse direction (no hardcoded literals). A typo'd key ships as a broken
label with nothing to catch it.

### TT1-13. Replace the 3-way dispose/PTT/poll race with deterministic gates

`HamlibRadioProtocolTests.cs:393-427` sequences a dispose/PTT/poll race using `CallDelay` plus two
bare `Task.Delay(20)` calls. That is assumed ordering, not enforced ordering. This test is the
regression gate for a physically-keyed-transmitter-on-disposed-handle bug, and it is a real CI flake
risk. Apply this project's own "deterministic gates, not shared race" rule.

### W1-W8. Windows-only test coverage — nothing here has ever been executed by a test

**Filed 2026-09-10**, after the user confirmed Windows is a supported platform and macOS is not.
Everything below is a code path that exists only on Windows, so the Linux suite cannot reach it and
`dotnet test` passing on a Windows machine does not reach it either — the audio tests skip by design
and the rest have no Windows-specific test at all.

**Do W1's classification step first.** It is cheap and it tells you how much of W2 closes for free.

#### W1. Classify the 43 `[RequiresPipeWireFact]` tests — DONE 2026-09-10

`RequiresPipeWireFactAttribute` returns false unconditionally on non-Linux, so **all 43 skip on
Windows, permanently, by design.** They are written against `pactl`/`paplay`/`ffmpeg`.

**Result: roughly 16 of the 43 genuinely need a cable or loopback. The other ~27 need only a
device.** So most of the Windows audio gap does not wait on W2's native work.

| Needs | Count | Examples |
|---|---|---|
| **Cable or loopback** — asserts on captured content | ~16 | `Write_PlaysRealAudibleTone_CapturedBackViaMonitor` (x2), `EncodeThenDecode_ThroughRealMiniAudioEngine_...`, `Constructor_ChannelSourceLeftVsRight_CapturesDistinctChannelContent`, `Write_WithStereoTxEnabled_...`, `SamplesAvailable_FiresWithRealNonSilentAudio_...` |
| **Any real device** — lifecycle, concurrency, disposal | ~27 | every `DisposeAsync_*` and `ConcurrentUseAndDispose_*`, `StartCaptureAsync_ConcurrentCalls_OnlyOneSucceeds`, `TwoEnumeratorInstances_CanBeUsedConcurrently` |

**Three cases do not fit either bucket and need their own decision:**

1. `RefreshAsync_FindsRealVirtualCable_AsDistinctPlaybackAndCaptureDevices` wants one device
   presenting as **both** a sink and a source. **Loopback cannot do this** — it is the one test that
   genuinely argues for VB-CABLE.
2. Both `HotplugDisposeTests` cases need a device that can **disappear mid-session**. On Linux that
   is `pactl unload-module`. Windows has no equivalent one-liner, so this needs a real answer before
   it can port.
3. `IsDeviceMutedAsync_PlaybackSink_ReflectsTheRealPulseAudioMuteState` needs mute **control**, not
   loopback. That is W4's job, not W2's.

**One over-gated test, free to fix now:**
`Constructor_GivenOutOfRangeDrainThreadPriority_ThrowsBeforeTouchingAnyDevice` is gated behind a
running audio server while its own name says it throws before touching a device. If that holds, it
should run everywhere — including CI, today, on all three runners. Confirm and ungate.

**Honesty about this classification:** it was made from test names, doc comments and which files
shell out to `pactl`, not by reading all 43 bodies. The buckets are sound enough to plan with and
should be confirmed per test as each is ported.

Counts by file: `MiniAudioEngineTests` 16, `MiniAudioCaptureSessionTests` 8,
`MiniAudioPlaybackSessionTests` 6, `MiniAudioDeviceEnumeratorTests` 4, `HotplugDisposeTests` 2,
`MiniAudioSpikeGateTests` 2, `MiniAudioDeviceMuteQueryTests` 2, `MiniAudioEngineSstvRoundTripTests` 1.

#### W2. A Windows audio round-trip — use WASAPI loopback, not VB-CABLE

The Linux round trip is not a physical cable either: it captures a null-sink's `.monitor`. **WASAPI
loopback is the direct analogue**, so it gives equivalent coverage rather than a weaker substitute.

miniaudio already supports it — `ma_device_type_loopback`, documented "WASAPI only", with
`ma_context_is_loopback_supported()` to probe. It is in the vendored `miniaudio.h` at the pinned tag.
**The shim does not expose it**: `scanline_audio.c` only ever builds `ma_device_type_capture` and
`ma_device_type_playback`.

Work: a loopback device type through the shim's own ABI, a managed entry point to request it, and a
`RequiresWasapiLoopbackFact` gate. **Native interop on the audio path, so full review cadence.**

VB-CABLE was considered and is the fallback, not the plan. Its one real advantage is presenting a
genuine capture endpoint rather than a special mode. Against that: it needs a manual driver install
on every machine, it cannot be created and torn down per test the way `pactl load-module` is, and its
licence terms need checking before the project relies on it. Loopback needs none of that.

#### W3. WASAPI device-id conversion — Windows-only code, never executed

`scanline_audio.c:171` and `:226` convert between WASAPI's `wchar_t[64]` device id and this project's
own UTF-8 ABI (`WideCharToMultiByte`/`MultiByteToWideChar`). Every other backend passes strings
through. This is real conversion logic with buffer-size arithmetic and no test has ever run it.
A non-ASCII device name is the obvious case to cover.

#### W4. WASAPI mute query — Windows-only, never executed

`scanline_wasapi_with_endpoint_volume` and `scanline_wasapi_get_mute_cb` (`:1144`, `:1186`) back
`IsDeviceMutedAsync` on Windows through COM's `IAudioEndpointVolume`. The Linux equivalent has a real
test against `pactl set-sink-mute` as an independent oracle. Windows has none. Unlike the Linux path,
this one does **not** take `g_context_mutex` — see the TT1-15 note in "Verified done", because the
reasoning there does not transfer to this branch.

#### W5. OmniRig COM — the one backend that cannot be tested off Windows at all

`OmniRigComClient` is `[SupportedOSPlatform("windows")]` and `OmniRigProtocolFactory` refuses to
construct elsewhere. Existing tests are a fake, a reflection check on the CLSID/IID/`[DispId]`
attributes (TT0-4), and mapper unit tests. **No test has ever instantiated the real COM object.**
Compounding it, `production_audit.md`'s Tier 2 notes OmniRig has zero logging anywhere — the one
backend nobody can test locally is also the one that says least when it fails.

#### W6. `JsonSettingsStore`'s Windows branch — an untested security assumption

`:167` takes plain `File.Create` on Windows instead of the Unix owner-only `UnixCreateMode`, and
`TrySetOwnerOnlyPermissions` returns immediately (`:196`). That is deliberate and documented: Windows
per-user profile ACLs are already private. **But nobody has verified it.** `settings.json` can hold a
real QRZ.com password in plaintext, so "the directory is already private" deserves one test that
actually reads the ACL on a real Windows box, not a comment.

#### W7. Path and device-name behaviour that differs by platform

- `DirectoryPathComparer:20` uses `OrdinalIgnoreCase` on Windows and macOS, `Ordinal` on Linux. The
  case-insensitive branch has never run against a genuinely case-insensitive filesystem.
- `SerialPortEnumerator` passes through `SerialPort.GetPortNames()`, which yields `COM*` on Windows
  and `/dev/tty*` on Linux — different shapes, one code path.
- `HamlibLibraryLocator:75` has a Windows-only DLL discovery branch.
- `ConfigurationPresetStore:467-476` rejects `CON`/`PRN`/`AUX`/`NUL`. That logic is deliberately
  cross-platform so files stay portable, so it is testable on Linux — **check whether it already is**
  before counting it as a gap.

#### W8. The four tests that skip *on* Windows have no Windows counterpart

TT1-18's four sites assert Unix permission behaviour and return early on Windows. Converting them to
`[SkipOnWindowsFact]` makes the skip honest but still leaves the Windows behaviour unasserted. Decide
per site whether a Windows equivalent is worth writing or whether the skip is the whole answer.

#### Not on this list

`MainWindow.axaml.cs:417`'s Windows-only geometry branch. It was confirmed on real hardware across
four rounds and is recorded in auto-memory (`project_windows_maximize_taskbar_bug`). Real-window
geometry is not something a headless test settles — leave it to the manual checklist.

### RX1. DONE 2026-09-10 — Robot 36's ambiguity fallback is now tested

Legacy toggles the previous chroma selection when the selector tone is too weak to call
(`Main.cpp:4289-4296`). The port already implemented it. No test reached it, because both tones in
the existing test are full strength (`|d| = 128`).

Five rows added to `LegacyRxChannelMappingTests`, each running ONE decoder across two consecutive
lines so the second inherits the first's selection. **No source change was needed.**

**The rails are asymmetric, and that is now pinned.** `d` truncates toward zero, so +200.0 Hz gives
`d = 64` and IS decisive, while -200.0 Hz gives `d = -64`, which fails `d < -64` and is NOT. The low
rail only bites at -203.125 Hz. Mutation-gated twice: removing the toggle fails the 3 ambiguous rows,
and changing `d < -64` to `d <= -64` fails exactly the 1 row that pins the asymmetry.

### RX2. DONE 2026-09-10 — the second switch is live, and it diverges for exactly one mode

`DrawSSTVDiff` (`Main.cpp:4508+`) is a **live RX path**, not a variant. `DrawSSTV` (`:4113-4120`)
selects it whenever `sys.m_Differentiator` is set and the mode is not `smSCTDX`. That option defaults
off (`:827`), is a user checkbox (`Option.cpp:253`), persists to `[Define] Differentiator`, and both
replay paths re-enter the same dispatch (`:5711`, `:5747`, `:5752`).

**My first reading was wrong and the auditor refuted it.** I claimed `DrawSSTVDiff` was a
channel-mapping clone differing only in the pixel read function. It is not. **`smPD160` is absent
from `DrawSSTVDiff`'s 13-label PD/MP/MN case** (`:4732-4744`) while `DrawSSTVNormal` carries 14
(`:4370`), so with the differentiator on PD160 falls to the RGB `default:` handler: Y, R-Y and B-Y map
to R, G and B, its chroma pair IS differentiated (`:4839`, `:4851`), and `gp2` is never written though
the prologue allocated the 2-row PD layout. Every other family agrees between the two switches.

**No third switch exists.** The other `switch(SSTVSET.m_Mode)` sites are per-line scalar setup.

**What this changes.** Nothing ships differently — the differentiator is a removed feature
(`docs/removed-features.md`). Two records were corrected instead: that document's "never touches a
true chroma-difference channel, in any mode" absolute now carries the PD160 exception, and
`LegacyRxChannelMappingTests`'s class doc now says it pins one of legacy's two RX mappings, not all of
legacy RX. **If the differentiator is ever ported it needs its own mapping table** — `DrawSSTVDiff`'s
is not derivable from `DrawSSTVNormal`.

Two smaller divergences recorded for the same future port: `DrawSSTVDiff` applies a one-column
back-shift (`x = x ? x - 1 : 0`) at every differentiated site but not at chroma sites, and it skips
the `x == 0` pixel in two families.

### PA-Two-more. After PA-5 or TT1-18 lands

1. Scoped `MainWindow.axaml.cs` tests. Target T1-12's `DataContextChanged` re-entry guard plus the
   two or three highest-traffic cross-pane wirings. Budget it as a coverage task, not a fix. Do not
   chase 1268 lines.
2. Fold the 35 `logger is not null` guards in that same file into a null-object logger.

---

## 3. Small source items, verified open

### PA-Backfill-Throw. Widen the backfill's catch past `FormatException`

`SqliteReceiveHistoryStore.cs:910` catches only `FormatException`, but `DateTimeOffset.Parse` also
throws `ArgumentOutOfRangeException` on an out-of-range offset. That escapes `EnsureSchema`, which
runs from the constructor, which resolves inside DI — **the same shape as the 00f incident**, where a
constructor-time throw made the app fail to start on every launch until the row was repaired by hand.

Pre-existing, not introduced by the 2026-09-10 quick-wins batch. Found by `yoniq-auditor` during that
batch's review and filed here rather than widening that commit. The fix mirrors the existing
skip-and-log arm: catch the second exception type alongside the first and leave that one row NULL,
which the unconditional backfill self-heals on a later start. Small, but it touches a startup path,
so it warrants its own change and its own regression test.

### PA-Factory. Extract one factory-match resolver

"Exactly one factory match" is reimplemented three times with drifted error-message wording:
`RadioController.cs:287-301` and `RadioSessionService.cs:55-63` and `:125-133`. About 15 lines. The
only observable effect today is three different error strings for one condition. Do it when already
in those files.

---

## 4. Measure before building

### M1. Gallery filter latency

Measure `RxHistoryPaneViewModel.UpdateFilteredEntries()` at N = 1 000, 5 000 and 20 000 entries.
Above about 50 ms per keystroke, add a 150 to 250 ms debounce. The deeper fix is a row cap on
`QueryAsync` (`RxHistoryPaneViewModel.cs:797`), which has none today — so clearing "Show today only"
loads the entire history.

### M2. Hamlib header anchoring

Decide one of two: vendor a pinned `rig.h` into the test project with a `LICENSES.md` entry (Hamlib
is LGPL-2.1, so `CLAUDE.md` §5 requires the entry), or accept a conditional skip. `hamlib/` is
gitignored (`.gitignore:11`), so a test reading it directly would silently not run on CI or any other
machine — the same false-PASS pattern TT1-18 is about. Worth deciding, because a wrong `RIG_LEVEL_*`
bit-flag silently mis-reads SWR, and the SWR auto-cutoff is a safety feature.

### M3. T1-14 — cold imaging convert-in/convert-out

**Status: unmeasured, not dropped.** The earlier drop reason ("every remaining call site is a
one-shot user action on an image of at most 640x496") used the working-copy bound
(`WorkingCopyScaleFactor`, `TxImageEditorPaneViewModel.cs:202`, applied at `:6724`). The full
original is retained, and it is what gets cropped for final output and rotated, so that bound does
not apply to those paths. Measure a representative large source image before closing it again.

The hot preview path really was fused (`TxImageEditorPaneViewModel.RecomputePreviewPipeline`), and
that half stands. Six `ToImageSharp`/`FromImageSharp` call sites remain in
`TransmitImagePreparer.cs`.

---

## 5. Audit work still owed

### A1. Functional audit — chunk D0

The final whole-file field-lifecycle pass over `AnalogFmSstvDecoder.cs`: every mutable field's
reset-or-preserve correctness across every teardown path. It is the last chunk, and it counts as one
of the two required clean rounds. D1 through D9 are all closed. Playbook and chunk boundaries:
`docs/functional-audit-playbook.md`. **Re-verify the line ranges before reuse** — the file has
changed since they were written.

### A2. Milestone audit at the next workstream boundary

Not scheduled work, a trigger. Prompt and cost throttles: `docs/audit-playbook.md`.

---

## 6. Product and roadmap work, not defects

### R1. `TemplateCatProtocol` fallback

**Verified 2026-09-10: zero occurrences in `src/`.** Still a real planned deliverable — it is the
named example in `CLAUDE.md` §4's binary-is-bytes rule, so its byte fields must be modelled as
`byte[]`/`ReadOnlyMemory<byte>`, never string or JSON literal. Spec: `spec/03-cat-layer.md`.

### R2. Localization completion

Remaining unlocalized views, plus a community-translation workflow. Spec: `spec/10-localization.md`.
Pairs naturally with PA-5, which would catch broken keys.

### R3. Receive-tab Sync-and-Slant, Input-chain and Signal-quality cards

Real new DSP work. No live audio-chain measurement exists for most of these. This is the long-term
counterpart to the short-term grey-out already shipped.

### R4. Offline callsign and country lookup

Distinct from QRZ.com's online lookup, which already exists. **Blocked on H3 below** (the Clublog
`cty.dat` API key), but the feature itself is real planned work, not just its blocker.
Spec: `spec/08-logging.md`.

### R5. Station-ID decode defaults — turn both on

`FskIdRxEnabled` and `CwIdRxEnabled` both default to `false`
(`src/ScanlineStudio.Core.Sstv/StationIdSettings.cs:80` and `:88`). Plan:
`~/.claude/plans/default-config-changes.md`. Two things to settle **before** flipping:

1. **The FSK half is a deliberate divergence from legacy.** Legacy ships `RXFSKID=0`. Turning it on
   is a product decision, not a port correction, so that field's doc comment must stop citing the
   legacy default as its reason and say plainly that we diverge, and why. **This half is not blocked.**
2. **The CW half is gated on H1 below.** Its doc comment cites `fsk_cwid.md` §12: not default-on
   before the classical decoder has been exercised on the legacy fixture. Either do H1 first, or
   record that the gate was consciously lifted. Do **not** silently flip past it.

Verification: a fresh-install settings load yields both true, an existing `settings.json` holding an
explicit `false` still loads as false, `dotnet test tests/ScanlineStudio.Application.Tests`, and
`node docs/help/check-help.mjs`. Also update `docs/help/index.html` wherever it states these are off
by default.

### R6. Decide whether 1.1 stays a single-item milestone

`spec/19-path-to-1.1.md`'s one confirmed target (the TX Template Editor redesign) is implemented.
Whether 1.1 picks up further targets is undecided. A user call, not an agent guess.

---

## 7. Blocked on a human — cannot be finished in this environment

### H1. B-P4 — FSK-ID/CW-ID legacy golden-vector fixture

The one open item in `fsk_cwid.md`, which is otherwise fully closed. **It gates the CW half of R5.**

Needs a manual one-time capture from the real legacy YONIQ binary (Windows or a VM). Two `.wav`
captures via `File → Rec`:

1. Post-image auto-ID path. `CWID=1`, default `CWIDText` ("DE %m"), stock CW speed (10, about 28 WPM),
   a short Robot 36 image.
2. Manual CW-send path (`SendCWID` directly, not post-image). Speed 18 to 20 WPM. Uses the separate
   `CWText` field (default "%m", no leading "DE").

Once both exist: place them under `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/`, add
a `LICENSES.md` row, and write a test asserting each capture's own decoded text, WPM within ±5% of
its real speed, and — for capture 1 only — CW-ID starting after FSK-ID within the expected sample
range.

### H2. Audio hardware validation — Windows covered by the user, macOS dropped

**Updated 2026-09-10 by user report.** The user has built and tested on Windows. macOS is explicitly
**out of scope by user decision** — "too bad, no worries" — not deferred, not a gap to close.

What remains, and it is narrow: the user's report was "tested Windows, done builds on Windows", which
does not by itself say whether the `spec/13-testing.md` **audio-device round-trip** was among what
was exercised. Confirm that one item before treating the Windows audio path as validated. Everything
else on this entry is closed.

**Running the suite on Windows does not close this.** `RequiresPipeWireFactAttribute` skips
unconditionally on non-Linux, so all 43 real-audio tests report a skip there. A green `dotnet test`
on Windows is genuine evidence for the other ~2,600 tests and no evidence at all about WASAPI.
Closing H2 needs a manual transmit-and-receive through a real Windows audio device, or the automated
coverage filed as **W1-W8** in section 2.

**Do not assume the PulseAudio-specific findings transfer to WASAPI** regardless — that caution was
about the findings, not about who runs the test.

No `docs/removed-features.md` entry is owed for the macOS decision. That rule covers dropping a
**legacy** capability, and legacy MMSSTV is Windows-only, so macOS was never a ported feature.

### H3. Clublog `cty.dat` licence

No fee, but redistribution requires a human to email Clublog's helpdesk and obtain an individual API
key before bundling. **Blocks R4.**

### H4. Chilkat and FastReport licence status

Needs confirming whether either actually backs a real legacy feature, by running the legacy binary
directly. Not verifiable from source alone. Currently assumed unused and orphaned, from a source-only
search.

### H5. Pooled-SQLite behaviour on Windows

**Unblocked 2026-09-10** — the user has a Windows machine and builds there. This is now a real task
rather than an impossible one. Worth doing soon: the 2026-09-10 WAL change (`88cbc67`) is exactly the
kind of thing whose file-locking behaviour differs between platforms, and Windows holds file locks
more strictly than Linux does.

### H6. Release-gate decision — largely answered 2026-09-10

The blocker for any tagged release is the full `spec/13-testing.md` manual hardware checklist (real
rig CAT session, real audio device round-trip, real third-party `rigctld` interop).

**The user has settled the platform question:** Windows is built and tested, macOS is out of scope.
So the old "Linux-validated-only, clearly labelled?" decision is moot — the answer is Linux plus
Windows, and macOS is not a gate.

**What is left is bookkeeping, not a decision:** confirm which `spec/13-testing.md` line items the
Windows pass actually covered (see H2), then record the two-platform scope in `spec/13-testing.md`
itself, which still describes a three-platform gate.

---

## 8. Parked by explicit decision — do not start these unprompted

Listed so nobody re-derives them as open work. Each was parked or dropped by a user decision or a
measured negative, not by neglect.

- **All new DSP improvement candidates.** The decode-quality improvement workstream was **closed
  2026-09-07 by user decision**. Decode **defects** stay open — that is D1 to D3 above. This closure
  also covers `~/.claude/plans/audio-domain-click-detection.md` (draft v2, awaiting a review that the
  closure cancelled) and `~/.claude/plans/auto-notch.md` (design only, nothing built).
  **The 2026-09-10 rule change does not reopen this.** `CLAUDE.md` §0a now allows a proven
  improvement to beat legacy, and that makes candidates like the green cast's Fault A *legitimate*.
  It does not make them *scheduled*. The rule says what is allowed. This bullet says what is next,
  and it is still a user decision to change it.
- **Real-HF-noise sweep.** Stopped unfinished by the same closure. The harness is committed and green
  (33 tests) and was never run to a measured result. Plan: `~/.claude/plans/real-hf-noise-sweep.md`.
- **`decoder_quality_improvement.md` §15 and §16.** Historical record, not a backlog.
- **Legacy `.mtm`/`.mti` template import.** **Rejected outright 2026-08-29** — a final product call,
  not a deferral. See `docs/removed-features.md`.
- **`.ini` legacy settings importer.** Parked 2026-08-15 on user redirect ("not important, put it on
  the maybe one day"). Note it is still a stated `CLAUDE.md` §2 backward-compatibility commitment,
  and real legacy fixtures exist locally at `yoniq-old/YONIQ-main/Mmsstv.ini`.
- **Phase 5 plugin system** (`IPlugin`/`PluginHost`/`IImageFilter`). Verified 2026-09-10: none exist
  in `src/`. Tier 3, parked with no implied revisit date.
- **Everything else in `spec/14-roadmap.md` Tier 3.** Perspective correction and webcam capture, full
  Hamlib extended command-set coverage, plugin sandboxing, legacy `.MDT` log import, SSTV
  repeater/beacon mode, contest logging (fully out of scope), OCR (no legacy precedent), stereo L/R
  input-chain level meters (blocked on a mono-versus-stereo architecture decision), unattended RX,
  and session frames.
- **`ui_transition_plan.md` step 14.** Rejected outright, not deferred.
- **`production_audit.md` Tier 2 and Tier 3 residue.** Real but low-value. The 2026-09-07 `auditor`
  triage returned **no MUST FIX and no `principal` round needed** across every one of them. The
  items worth doing were promoted into sections 2 and 3 above. The rest stay in that document.
- **`production_audit.md`'s "Accepted residuals"** under "Fixing 00d-00h". The user accepted those
  explicitly after verification. Do not reopen them as bugs.
- **`ASTRA-036`, `ASTRA-037`** (accepted) and **`ASTRA-007`** (withdrawn). Not active bugs.

---

## 9. Verified done during this sweep — do not re-derive

Each of these was listed as open somewhere and is not.

| Was listed as | Verified state, 2026-09-10 |
|---|---|
| `production_audit.md` items 00a-00h | Fixed and merged (`07d1a54`, `d17005b`, and the branch merge). |
| `production_audit.md` item 0c — `m_sint1` VIS freeze | Done (`ASTRA-030`, `b30c760`). `_syncBypass1PrimaryHeld` no longer exists. |
| `production_audit.md` item 8 / **TT1-16** — QRZ non-OK status | **Done, both halves.** Source: `EnsureSuccessStatusCode` at `QrzCallsignLookup.cs:173` and `:188`, and `QrzLogbookUploader.cs:53`. Test: `QrzCallsignLookupTests.cs:17` uses `HttpStatusCode.ServiceUnavailable`. TT1-16's "OPEN" mark is stale. |
| Tier 2 — `TemplateStore.SaveAsync` non-atomic write | Done. GUID temp file plus `File.Move` at `TemplateStore.cs:109-114` and `:248-274`. The `ExportAdifFileAsync` half was not re-checked. |
| `spec/15-template-designer.md` — "real open items: multi-select move only" | **Stale, contradicted by its own later bullet.** Multi-select move shipped as group-ops-lite (`a3484fa`). That document has no real open items left. |
| `~/.claude/plans/tx-editor-overlay-wysiwyg.md` | Done. `FontSizePx`, `CanvasFontSize` and `SelectedTextElementFontSizePx` all bind in `TxImageEditorPaneView.axaml`. |
| All 13 Tier 0 and 17 of 18 Tier 1 items | Done, each with a naming commit. Only T1-14 remains, as M3 above. |
| All 7 test-suite Tier 0 items | Done. |
| **TT1-19** — `ConfigurationPresetStore` concurrent-writer test | **Not open at all.** `production_audit.md:1983` marks it `DROP` (2026-09-07): `JsonSettingsStore` is covered, and `ConfigurationPresetStore` needs no equivalent because every method runs under its own `SemaphoreSlim` and one preset is one whole file, so concurrent saves are correctly last-writer-wins. Only the stale Status banner called it unconfirmed. |
| **PA-3** — WAL mode on `history.db` | **Done 2026-09-10.** New shared helper `SqliteWriteAheadLogging.TryEnable`, called from both stores' `EnsureSchema`. **Busy timeout deliberately not added**: `Microsoft.Data.Sqlite` 8.0.10's own XML docs give `SqliteCommand.CommandTimeout` a 30-second default, fed by `DefaultTimeout`, so the retry budget already exists. |
| **PA-4** — hoist the command out of the loop | **Done 2026-09-10.** Both loops in `SqliteReceiveHistoryStore` now build one command with one typed parameter set above the loop and reassign only the values per iteration. |
| **TT1-15 / PA-6** — `MiniAudioDeviceMuteQuery` dispose race | **Done 2026-09-10, and the fear behind it was wrong.** This file previously predicted "likely a real defect, not just a test", because `IsDeviceMutedAsync` checks `_disposed` under `_gate` and then runs the native call OUTSIDE that lock while `Dispose` releases the context outside it too. That managed-only reading is incomplete. **The native shim closes it:** `scanline_audio_get_device_mute` holds `g_context_mutex` across its entire body and re-checks `g_context_initialized`, and `scanline_audio_context_uninit` takes that same mutex — so an orphan call either completes before teardown or returns -1, which surfaces as a null. The Windows (`scanline_wasapi_with_endpoint_volume`) and macOS (`scanline_coreaudio_get_device_mute`) paths never touch the shared context at all. Verified by reading `native/scanline_audio.c`, not inferred. The new test passes against unmodified code and is mutation-gated on the double-dispose guard. **No source change was needed or made.** |
| **PA-Dispose** — dispose `SqliteCommand` | **Done 2026-09-10.** All 20 bare sites now use `using`. (The earlier "5 already using" count in this file was wrong — it was 8, because `SqliteReceiveHistoryStore.Deletion.cs` was already fully correct and served as the pattern.) |

**Two plan files in `~/.claude/plans/` belong to other projects, not this one:**
`how-do-i-start-quirky-simon.md` (a wxPython launcher) and `iterative-tinkering-stearns.md` (an
AGCGuard history purge). Ignore both here.

---

## Known contradictions still unresolved

Two documents disagree with themselves. Neither blocks anything, but settle each before acting on it.

1. **`TxControlsPaneViewModel.Dispose()`.** `production_audit.md`'s Tier 2 banner lists it as open
   (cancels its CTS, never disposes it, unsubscribes no editor handler). The same file's triage list
   drops it as a false positive, on the grounds that `_transmitCts` is created and disposed inside
   the transmit method's own `finally` (`:1737`/`:1776`). **The source at `:2175-2182` shows only
   `Cancel()`.** Both claims can be partly right — settle which CTS each is talking about.
2. **The PLL output cutoff.** Covered in D3 above. `production_audit.md:1337` says dropped,
   `:1477` reopens it. The reopened version is later and verified.
