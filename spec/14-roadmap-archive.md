# Roadmap Archive — Closed/DONE History

Archived closed/DONE roadmap history, split out of spec/14-roadmap.md on 2026-08-19 to reduce file size. Nothing here was altered — this is a verbatim copy of closed sections.

### Tier 0 — must fix before calling anything "0.9 beta" — **DONE, 2026-08-14**

All items below closed. DSP item investigated across 2 rounds of auditor plan-review plus direct
empirical measurement, closed as not-a-bug (see its own entry). UI-honesty items landed via 4
parallel worktree-isolated sweeps (one per file), each individually diff-reviewed, then the
combined batch went through 2 rounds of real auditor code-review (not just self-review) per
CLAUDE.md §7's own rule — round 1 found 3 real blockers (a broken test, non-functional
`ToolTip.ShowOnDisabled` app-wide, 6 dead controls left next to fixed ones) plus 2 cards the
original task list had wrongly left out under the user's own "no fake-live items" bar (TX Queue/
Recently-sent/Saved-templates fake item lists); round 2 found one item (TX Log's 2 fake stat
values) that got named in round 1 but missed in the fix pass. Both rounds' findings fixed, full
solution build + 242/242 `ScanlineStudio.UI.Tests` green throughout. Not yet pushed to
`origin/master`.

The core loop was verified end-to-end (audio in → decode → save/gallery is real; image → encode →
PTT-keyed TX is real; crop + draggable macro-resolved text overlay + Apply → Transmit is real). The
only things wrong with it are controls that lie about it — the suspected DSP bug below turned out
not to be one:

> **"Robot 36/72 replay chroma-bleed" — investigated 2026-08-14, closed as NOT a bug, dropped from
> Tier 0.** 2 rounds of auditor plan-review plus a direct empirical measurement (per-row deltas,
> with AND without replay) found the suspected corruption was an artifact of the test image itself
> (deliberately wild adjacent-row colors, an invalid fidelity target for Robot 36's own by-design
> vertical chroma subsampling) — a no-replay control reproduced the identical per-row deltas with
> zero replay activity. The one real, replay-attributable artifact (a single stale seed row per
> redraw pass) is legacy-equivalent (legacy's own `UpdateSampFreq` never resets its `m_D36`
> cross-line state either) and smaller than the mode's own inherent per-row error on a real image.
> Also corrected in the same pass: only Robot 36 uses the stateful decoder — Robot 72 was never
> affected (`ColorEncoding.YCbCrSequential`, stateless), an earlier "Robot36/Robot72" framing here
> and in `PROJECT_BRIEF.md` was wrong. Full finding in `AnalogFmSstvDecoder.PerformReplay`'s own doc
> comment (`AnalogFmSstvDecoder.cs`, ~line 5117 onward). The one genuinely real, separate,
> mode-independent divergence from legacy (the sacrificed row on every replay pass, every mode) was
> already known/documented/accepted before this investigation, not new scope from it.

- **7 fake-live/dead controls inside the core loop** (`TxImageEditorPaneView.axaml:46-72,119-126,
  139-166,252-267` — tool strip, dead Transmit/Tune/Preview/Halt row + progress bar, 6 unbound
  adjustment sliders, decorative callsign/report-plate overlay; `MainWindow.axaml:178-179,477-480` —
  Mode card "Locked" toggle, 4 dead Incoming-frame buttons):
  TX editor's dead Transmit/Tune/Preview/Halt button row + progress bar (real Transmit lives in the
  left column); the 9-icon tool strip (Move/Crop/Scale/Rotate/Text/Box/Line/Mask/Pick — "Crop"/
  "Text" actively mislead since the real controls are the always-on canvas drag + separate "Add
  text" button); 6 unbound Brightness/Contrast/Saturation/Gamma/Sharpen/Denoise sliders; the
  decorative safe-area/callsign/report-plate canvas overlay (not load-bearing — a real overlay path
  already exists via "Add text," so this is a delete/rebind, not a build); Receive's 4 remaining dead
  Incoming-frame buttons (Save Frame/Abort/Re-decode/Copy to TX — "Save Frame" is worst, since
  auto-save already happened silently and a click-with-no-effect reads as data loss; Log QSO shipped
  2026-08-15, no longer in this list); Receive Mode card's "Locked" toggle (no mode-lock feature
  exists at all).
- **Auto-start shown hardcoded-On** — `OptionsWindowView.axaml:377` (the feature doesn't exist at
  all). Unlike the RX-buffer default below, this one is NOT superseded by anything — its Tier-2 item
  is a scoping pass for a feature that may not land before 0.9b, while the dialog keeps lying in the
  meantime. Genuinely dependency-free one-line fix, unconditional Tier 0.
- **RX buffer shown hardcoded-Off** — `OptionsWindowView.axaml:365` (real runtime default is **On**,
  `SstvDecoderSettings.cs:112`, confirmed materially active since Phase 6d). Superseded by Tier 2's
  RX-buffer Phase 9 (replaces the whole stub) — only worth a standalone one-line fix here if Phase 9
  slips past 0.9 beta.


### Tier 1 — cheap trust fix before a *public* beta (not a Tier-0 blocker) — **DONE, 2026-08-14**

Absorbed into the Tier-0 UI-honesty sweep rather than done as its own separate pass — the actual
bar applied ("no fake-live items, mock/disabled is fine") was universal, not scoped to "inside the
core loop only," so the B1-B4 sweep + combined-review fixes already covered every item named below.
Verified directly against current `en.json` before writing this note (not assumed): status-bar SNR,
Signal-quality Min/Max, Input-chain Squelch, Frame-metadata Frequency, and Gallery's per-entry SNR
suffix are all now genuine placeholders (`"—"`), not fake literals. `spec/16-gui-wiring-survey.md`'s
own staleness precondition (below) was independently resolved by its own 2026-08-14 refresh, done
earlier the same day as the Tier-0 sweep.

~40 more FAKE-LIVE controls outside the core loop per `spec/16-gui-wiring-survey.md` (status-bar
SNR, Signal-quality SNR-plot/histogram/tone readouts, Input-chain Squelch/BPF/Notch/Noise-floor/
Level meters, Frame-metadata Freq/Mode-VIS/SNR-Slant/OCR/Dropped-lines, Gallery per-entry SNR/freq/
grid-dist). Action = grey out or remove, **not build** — these are diagnostics about the loop, not
the loop itself.

**Precondition, don't skip**: `spec/16-gui-wiring-survey.md` is itself stale as of this writing —
it predates CW-ID/FSK, demod-type, RX BPF, and the RX-buffer subsystem, and its own line ~187
justifies the Frame-metadata Callsign row staying FAKE-LIVE with "no FSK-decoded-callsign source
exists yet," which is no longer true (`FskStationIdDecodedInfo` → `RxImagePaneViewModel` shipped
2026-08-12). Its summary counts (`~132 REAL` inline vs. `PROJECT_BRIEF.md`'s `~149 REAL`) also
disagree with each other. Refresh that survey first, then grey out whatever's still genuinely
unbacked — don't grey out something now cheaply wireable.

Overlap note: Receive tab's Input-chain/Signal-quality rows appear here (grey out short-term) AND
in Tier 2 (build real measurement long-term) — deliberate two-step, not a duplicate/contradiction.


## Piece 10 — `GetPictureLevel` peak-picking

Started as a one-line item ("Robot 36 luma bare read") in the "Secondary, smaller,
independently-source-verified divergences" list below piece 8's own entry. Investigation (reading
`Main.cpp`'s real per-pixel RX decode switch directly, not inferring from the original framing) found
the real scope was far bigger: legacy's `GetPictureLevel` (`Main.cpp:4057-4071`) peak-picks — compares
two raw demodulated samples `m_KSB` apart, keeps whichever is larger — and this applies to nearly every
mode's every channel (all 3 RGB channels for Scottie SCT1/SCT2/Martin/SC2/MC/Pasokon; luma only for the
Robot/R24/MR/ML/PD/MP/MN YCbCr-paired families, chroma always stays bare; RM8/RM12's single channel).
Scottie DX is the ONE mode that never peak-picks anything. This port previously did zero peak-picking
anywhere across all 5 scanline decoders — confirmed by reading every one of them.

User chose "full fix, plan-reviewed first" over a narrower Robot-36-only slice. **4 rounds of auditor
plan review** (a new "ask the auditor directly: is this plan good enough to build now, given a
code-level review will follow?" policy was established mid-piece — see `~/.claude/agents/auditor.md`'s
"Plan reviews" section and the `feedback_plan_review_cadence` memory — soft 3-round backstop, then check
with the user, which is what happened: round 3 gave a clean GO, round 4 was dispatched anyway per this
policy and confirmed it independently):

- **Round 1**: 2 blockers (PD50/PD90 wrongly placed in group A instead of E; missing the universal
  `if(!m_KSB) m_KSB++;` floor) + several risks (Robot's 3 read sites not 2, PD/MP/MN's two luma segments
  both peak-pick, `effectiveSampleRate` not the raw constructor rate, a design footgun in the originally
  proposed two-same-typed-delegate-parameters shape).
- **Round 2**: found the round-1 fix for the line-end boundary was ITSELF wrong twice — the "bare always
  wins past line end" premise was traced to the wrong buffer's clamp (it's actually a floor at each
  mode's own `LuminanceMinHz`, since `-16384` equals `center-BWH` exactly), and the reachability claim
  ("AVT-only") was backwards — the guard is unreachable at EVERY currently-registered mode (max ratio
  ~0.625 at PD290, not AVT), meaning test coverage needed to be a direct synthetic-line unit test, not
  an integration test. Also recommended moving the group-table/Scottie-DX-exception data off
  `SstvModeDefinition` (its own precedent for a compile-error-on-missing-mode guarantee didn't actually
  hold, since the cited nullable fields all have defaults) onto `SstvModeRegistry` as internal functions,
  matching `GetSyncPeakOffsetMs`/`IsFastAfcGroup` precedent — **adopted**.
- **Round 3 (verdict GO)**: independently re-verified rounds 1-2's corrections, confirmed the sign
  direction (legacy picks the HIGHER raw value = higher frequency = brighter, verified via
  `m_Buf[n]=-d`'s sign convention) and the corrected line-end floor. Flagged that the golden-vector
  acceptance criterion needed to be a FIRM "no regression" requirement, not just "deferred" — this repo
  has a real precedent (`8f87146`) of loosening a tolerance instead of investigating a regression.
- **Round 4 (verdict GO, explicit)**: asked directly "is this good enough to build now, given a
  code-level review follows?" — yes, with 2 cheap spec-text clarifications (group C's "no trim" needed
  an explicit sentinel/factor-of-1.0, not a divisor-of-1 that would silently zero `m_KSB`; the
  int-truncation needed to live directly in the formula, not just in prose) and one adopted
  implementation-ordering split (3a: wire the reader in bare-only, provably zero-behavior-change; 3b:
  flip the actual peak-pick sites — a clean bisection point).

**Implementation (all isolate-tested, matching the chop-into-pieces methodology)**:
1. `SstvModeRegistry.GetPeakPickParameters`/`NeverPeakPicks`/`GetKsbSamples` — pure functions, the 5-group
   `m_KSS`/`m_KSB` table transcribed from `sstv.cpp:1110-1179`. Pinned by `PeakPickParametersTests.cs`
   (48 tests: one assertion per mode's group, `AllModesCovered`, hand-computed AVT/PD290 spot-checks
   matching the auditor's own worked examples, universal-floor check). Zero behavior change.
2. `PixelSampleReader.cs` — the new `ReadBare`/`ReadPeakPicked` reader object replacing the old single
   `Func<int,int,double>` delegate `IScanlineDecoder.DecodeLine` used to take (distinctly-named methods
   so a call-site mixup fails to compile, not silently reads the wrong channel — the round-1-flagged
   design fix). Takes a `Func<int,double> rawSampleAt` delegate rather than owning a `List<double>`
   directly, so it stays unit-testable with an arbitrary synthetic source. Pinned by
   `PixelSampleReaderTests.cs` (7 tests). Zero behavior change (nothing called it yet).
3a. Wired the reader into all 5 decoders + `AnalogFmSstvDecoder`'s call site, every call site to
    `ReadBare` only. Provably zero-behavior-change: 317/317 tests bit-identical to the pre-piece-10
    baseline.
3b. Flipped the real peak-pick call sites per the corrected mode/channel table, folded into each
    decoder's existing exhaustive `ChannelName` switch (not a parallel one). Fixed a stale doc comment
    (`IScanlineDecoder`/`AnalogFmSstvDecoder`) that had claimed "GetPictureLevel/GetPixelLevel both
    simply dereference `*ip`" — false; only `GetPixelLevel` is the bare read. Full suite: **317/317
    passing** with peak-picking actually active.

**Re-verification: found a real, small, root-caused side effect — not a bug in this piece.** Both
`GoldenVectorTests` fixtures moved slightly the wrong direction (robot-36 16.995→17.086, martin-m1
1.216→1.284 — both tiny, both comfortably within their 25.0/15.0 tolerances) and most round-trip deltas
increased slightly (+0.1 to +0.9 across ~35 of 43 modes). Investigated rather than accepted at face
value: empirically measured `PllFmDemodulator`'s real transient response to a small tone step (the size
of a typical gradient's per-pixel frequency delta) — it undershoots BELOW the starting tone within ~5
samples, then recovers over 30+ samples; for narrow-pitch modes (e.g. RM12, ~12.7 samples/pixel at
44100Hz) that settling time exceeds a whole pixel's dwell time, so both the bare and peek-ahead samples
land inside the same still-ringing transient. **Root cause traced directly to source**: `CSSTVDEM`'s
constructor sets `m_Type = 2` (`sstv.cpp:1492`), and the demodulator dispatch (`sstv.cpp:2256`, `case 0:
PLL / case 1: Zero-crossing / default: Hilbert`) confirms legacy's REAL shipped default is Hilbert, not
PLL — this port only implements PLL, and its settling dynamics are slower/rougher than whatever
Hilbert's were when legacy's peak-pick heuristic was designed. The peak-pick LOGIC itself is
independently verified correct (4 rounds of source-verified review + 55 dedicated unit tests) — this is
a genuine, small, now-explained interaction with an already-known, already-scoped-out demodulator gap
(see the open-items list's item 5, priority bumped by this finding), not a defect introduced by piece
10. Discussed directly with the user (not silently accepted): decision was to keep the small increase
(documented, root-caused, within tolerance), and NOT implement Hilbert as part of this piece — assessed
as a large, uncertain-payoff undertaking for a currently-small measured gap, better scoped/researched as
its own piece than decided on the strength of one finding. Concrete next step logged: read `CHILL`'s
real implementation and get an actual settling-time comparison before deciding whether to implement it,
rather than leaving "worth it?" as a guess in either direction.

**Piece 10 status: implemented, 317/317 passing, committed (`ee74bfd`), pushed.**


## Piece 11 — `m_KSS`/`m_KS2S` horizontal pixel-pitch fix

Open-items list's item 3 (logged during piece 10 as "connected to piece 10"). Legacy's real per-pixel
x-mapping (`Main.cpp`'s decode switch, e.g. `Main.cpp:4223/4300`) is `x = ps * Width / m_KSS` for
luma/RGB channels, `x = ps * Width / m_KS2S` for chroma (R-Y/B-Y, and Robot's tone-selected combined
"C" channel) — both TRIMMED widths, not the raw scan duration `m_KS`/`m_KS2` this port's decoders were
using (`perPixelDurationMs = scan.DurationMs / mode.ImageWidth`, no trim). A ~0.1%-0.4%-of-scan
horizontal scale error, per group.

**TX/RX verified separately, per CLAUDE.md's rule** (never infer one from the other): read
`TMmsstv::LineR36` (`Main.cpp:6558`) directly — TX writes each pixel at a flat `DurationMs/Width` with
no trim at all. This fix is RX-decode-only.

**Caught a wrong assumption before implementing**: an earlier working note in this project claimed
`m_KS2S` always uses the identical trim divisor as `m_KSS`. Re-verified directly against
`sstv.cpp:1110-1160`'s grouping switch and found this is FALSE for group D (MR73 only) —
`m_KSS = m_KS - m_KS/640` but `m_KS2S = m_KS2 - m_KS2/1024` (`sstv.cpp:1152-1154`), different
divisors. Groups A/B/C/E use the same divisor for both. Caught by re-reading source instead of trusting
the earlier note — exactly the kind of error CLAUDE.md's "no assumptions" rule exists to catch.

**Fix**: extended `PeakPickParameters` (`SstvModeRegistry.cs`) with a second field, `Ks2sTrimFactor`
(equal to `KssTrimFactor` in groups A/B/C/E; `1023/1024` vs `639/640` in group D). Added
`IsChromaChannel(channelName)` (`"RY"`/`"BY"`/`"C"`) and `GetPixelPitchTrimFactor(mode, channelName)`,
reusing the same 5-group switch `GetPeakPickParameters` already implements. All 5 scanline decoders'
`perPixelDurationMs` now multiply by `GetPixelPitchTrimFactor(mode, scan.ChannelName)` before computing
each pixel's sample window.

**User approved implementing directly** (no auditor plan-review round) given this reuses piece 10's
already-tested `GetPeakPickParameters` machinery rather than introducing new architecture.

**Tests**: `PeakPickParametersTests.cs` extended — `Ks2sTrimFactor` column added to the existing
43-mode theory (MR73 is the only row where it differs from `KssTrimFactor`), plus dedicated
`IsChromaChannel`/`GetPixelPitchTrimFactor` tests using MR73's real divergence as the discriminating
case (a test that only exercised matching-groups modes could pass even with the chroma branch wired to
the wrong field). 327/327 passing (was 317).

**Golden-vector re-measurement** (both fixtures are group E — 239/240 trim on both axes): martin-m1
1.284 → 1.438 (small increase, still well inside the 15.0 tolerance), robot-36 17.086 → 14.809
(improved). No regression; small opposite-signed movement is the expected shape for a scale fix (unlike
piece 10's settling-time-driven regression, this doesn't touch demodulator dynamics at all).

**Piece 11 status: implemented, 327/327 passing, committed (`be938d4`), pushed.**


## Piece 12 — RM8/RM12 gain correction

Open-items list's `MonoAveragedPairedScanlineDecoder` RM8/RM12 gain item. Looked small ("multiply by a
constant"), same as piece 11 turned out to be — overturned a previously-documented design decision
instead.

**The bug**: legacy's RX applies an RM-specific gain (`d *= 256.0/(256.0-32.0)`, `Main.cpp:4438`) on
top of `GetPictureLevel`/`GetPixelLevel`'s calibration pipeline, RM8/RM12 only. A prior piece had left
a "NOT ported" comment in `SstvModeRegistry.cs` reasoning this port's simpler linear
frequency-to-pixel formula couldn't faithfully carry the correction, since it doesn't replicate
legacy's actual calibration internals.

**Re-derivation (round 1 of 2 auditor plan-review rounds)**: that premise was wrong. Traced
`GetPixelLevel`'s real chain: `m_Buf[n] = -d` where `d` comes from the discriminator
(`sstv.cpp:2278/2289`); `GetPixelLevel` applies global constants `m_DemOff=0`,
`m_DemWhite=m_DemBlack=128/16384` (`Main.cpp:875-877`). Independently re-derived via TWO different
demodulator paths (`CPLL::Do`'s VCO gain chain, and `CFQC`'s zero-crossing path, `sstv.cpp:280-345` and
`376-488`) that for the demodulator's 1500-2300Hz FM band this reduces to
`GetPixelLevel(freq) = (freq-1900)*128/400`, confirming a prior piece's own derivation
(`RobotScanlineDecoder.cs`'s `AmbiguityHalfWidthHz` constant) rather than re-trusting it blind.
Algebraically, `GetPixelLevel(freq)+128 = (freq-1500)*256/800` — exactly this port's own
`(freq-LuminanceMinHz)*256/(LuminanceMaxHz-LuminanceMinHz)` formula for the standard band RM8/RM12 both
use (confirmed: unmodified `LuminanceMinHz`/`MaxHz` defaults, 1500/2300, in the registry). The two
pipelines were never mismatched, just expressed differently.

**Round-1 finding (real blocker, not just documentation)**: legacy's RM8/RM12 RX branch
(`Main.cpp:4437-4449`) writes gray straight into R=G=B with no `YCtoRGB` matrix call at all — but this
port's existing decoder routed luma through `YCbCr.ToRgb(y[x], 128, 128)`, which applies its own
different studio-to-full-swing expansion (`1.164457*(y-16)`). Naively applying the RM correction to
`y[x]` and still routing through `ToRgb` stacks two different corrections and produces worse output
(worked example: mid-gray 128→130.4 instead of 128; TX-white 235→272.8, clipped to 255, instead of
~250). Fix: bypass `YCbCr.ToRgb` entirely for this decoder, write gray directly, matching legacy's real
branch field-for-field. This also retires a second, separately-wrong previous design decision — an
earlier version of `MonoAveragedPairedScanlineDecoder`'s own doc comment justified routing through
`ToRgb` as "the same path every other Y-bearing family already uses, rather than inventing a third,
RM-specific reconstruction convention" — but legacy already has a second, genuinely different
convention here (direct gray write, no matrix); porting it isn't inventing a third one.

**Round-2 auditor verdict**: "Yes — build it." Confirmed the corrected plan matches
`Main.cpp:4437-4449` step-for-step (no other clamp/offset/matrix step missed), confirmed two remaining
judgment calls both hold:
- **No truncation replication**: legacy truncates to `int` twice (once inside `GetPixelLevel`, once at
  `d *= gain`); this port keeps doubles throughout, matching every other decoder in this codebase.
  Documented as an accepted, asymmetric-across-mid-gray divergence, originally estimated as "a couple
  of levels" — **S21 (Band 4) later measured this exactly**: an exhaustive sweep over every achievable
  input in this port's own AGC'd ±16384 domain finds a max divergence of precisely 2 levels, never
  more, asymmetric as predicted (legacy-minus-port ranges -1..+2, not a symmetric ±2) — see this file's
  own "S21" entry further down. Well inside existing 10.0-25.0 tolerances, now confirmed rather than
  assumed.
- **No compensating TX-side gain**: legacy's TX (`LineRM`, `Main.cpp:6785-6801`) has no matching
  correction — confirmed RX-only. Round-trip is genuinely non-identity by design (predicted delta
  ~2.4 avg/~4.7 max: the offset terms cancel exactly, `112*8/7=128`, leaving pure gain error), but this
  is legacy's real asymmetry, not a port bug to "fix" by inventing symmetry legacy doesn't have (the
  Scottie-incident failure mode CLAUDE.md warns about). Predicted to stay under the existing flat 10.0
  round-trip tolerance without needing a Robot36-style override — and measurement confirmed this: no
  override needed.

**Fix**: `MonoAveragedPairedScanlineDecoder.cs` — `y` now a `byte[]` storing the final clamped value
directly; `(rawValue-128)*256/224+128` clamped to `[0,255]`, written straight into
`new Rgb24(gray,gray,gray)`, no `YCbCr.ToRgb` call. Rewrote both stale comments (the decoder's own, and
`SstvModeRegistry.cs`'s "NOT ported" note).

**Tests**: isolate-tested `EncodeThenDecode_RoundTripsWithinTolerance_MonoFamily` (RM8/RM12) first, per
the auditor's explicit instruction not to pre-add a tolerance override — passed at the existing flat
10.0, as predicted. Full suite: 327/327 passing (no new test files needed — existing coverage already
exercises this decoder).

**Piece 12 status: implemented, 327/327 passing.**


## Piece 13 — `TryDecodeNarrowModeHeader` FSK bit decode

**The bug**: `AnalogFmSstvDecoder.TryDecodeNarrowModeHeader` decoded each of the 24 MN/MC mode-ID bits
by averaging the shared PLL's demodulated-frequency stream over a fixed 22ms window and comparing to a
fixed midpoint threshold — a proxy that only worked by coincidence, reading off the general PLL stream
rather than replicating a dedicated detector. Same bug shape Piece 9 already fixed for VIS-bit decode.

**Legacy ground truth**: `CSSTVDEM::DecodeFSK(int m, int s)` (`sstv.cpp:2378-2606`), called every
sample with `m=int(d19)` (1900Hz mark envelope) and `s=int(dsp)` (2100Hz/`FSKSPACE` space envelope,
`m_iirfsk`, 100Hz bandwidth — same as `m_iir19`, not the 80Hz VIS-bit detectors). A 5-phase
`m_fskmode` state machine, not a simple threshold race.

**Two rounds of auditor plan-review, both substantive**: round 1 caught 3 real state-transition
errors in the first plan draft (mode 1's actual 50ms hold conflated with mode 2's 100ms *timeout
window*, which is a different thing; mode 3 mischaracterized as a debounce when it's really a single
recheck at a fixed 11ms-later instant; the `|m-s|>=2048` amplitude gate treated as checked every
sample when it's actually checked once per 22ms bit-sampling instant only) — each independently
re-verified against `sstv.cpp:2378-2444`/`sstv.h:710-717` before accepting, not taken on the auditor's
word alone. Also flagged: missing search-ceiling bound (this decoder is by construction a
retry-on-reject scanner — same unbounded-scan trap `TryDecodeVisDataBits` already hit once, `:1207`),
the `int`/`double` field-type contract (`m_fsktime`/`m_fsknexti` int, `m_fsknextd` double —
drift-corrects the 24-bit stream; naive repeated integer addition drifts ~13 samples by bit 24 at
11025Hz), collapsing to two caller-visible outcomes only (Locked/still-pending, matching the VIS-bit
race's own already-fixed precedent — legacy never permanently aborts, every failure path resumes
scanning from mode 0), and a `docs/removed-features.md` entry for the FSK callsign-ID packet (`0x2a`
STX, modes 5-10) this piece deliberately doesn't port. Round 2 re-derived the corrected state machine
fresh from source (not trusting round 1's own summary) and confirmed it clean, plus pinned down 3 final
one-line decisions: commit at `headerStart + NarrowHeaderTotalDurationMs` (the fixed nominal duration,
matching TX's real placement and the existing passing round-trip test) rather than wherever the state
machine locks; `0x2a` at the STX dispatch is treated identically to any other unrecognized byte (reset,
resume); and each `ProcessSample` call must read the mode once and run exactly one case's logic
(mirroring legacy's `switch`+`break`-per-`Do()`-call structure) — a re-dispatch-within-one-call
implementation would silently break the mode-2 zero-sample edge case. Verdict: "ready to build."

**Fix**: new `NarrowFskHeaderDecoder` (`src/ScanlineStudio.Core.Sstv/`) — a literal, sample-driven port of the
state machine (modes 0/1/2/3/4/16/17/18 only; 5-10 out of scope, see `docs/removed-features.md`).
`TryDecodeNarrowModeHeader` rewired to drive it with two dedicated `SyncEnvelopeDetector` instances
(1900Hz mark / `VisHeader.NarrowSpaceFrequencyHz` space) over `AgcSampleAt`, bounded by an explicit
local search ceiling mirroring `TryDecodeVisDataBits`'.

**Tests**: isolated first, per this project's chop-into-pieces methodology —
`NarrowFskHeaderDecoderTests` (13 tests) drives the state machine directly with synthetic int m/s
pairs, no audio/AGC/filter pipeline involved: all 6 registered mode codes lock correctly, an
unregistered-but-checksum-valid code returns its raw byte (mapping to "no mode" is
`SstvModeRegistry.FindByNarrowCode`'s job, one layer up, not this class's), wrong STX/bad checksum
reject-then-resume-scanning, a `0x2a` callsign-ID preamble followed immediately by a valid `0x2d`
packet still locks (confirms the carve-out is truly independent), and the `|m-s|=2048` vs `2047`
amplitude boundary and the 49ms-vs-50ms guard-hold boundary are both pinned exactly (the latter
surfaced a real, legacy-faithful off-by-one: mode 0's trigger sample is consumed before mode 1's
countdown starts, so completing the hold needs 551+1 samples, not exactly `MsToSamples(50)` — real TX's
100ms guard swallows this invisibly, but the boundary test needed the extra sample to pass). Full
suite: 340/340 passing (327 + 13 new).

**Piece 13 status: implemented, 340/340 passing.**


## Hilbert demodulator (`CHILL`) scoping pass — investigation only, no code changed

Follow-up to piece 10's flagged next step ("read `CHILL`'s real implementation and get an actual
settling-time comparison before deciding whether to implement it"). Read `sstv.cpp:3005-3087`/
`sstv.h:374-395` directly; no code written, no decision made on whether to implement — logged per
CLAUDE.md §8 so this doesn't need re-deriving cold next time.

**What `CHILL` is**: a Hilbert-transform-based instantaneous-phase (quadrature/arctan) FM
discriminator — structurally unrelated to `PllFmDemodulator`. Feedforward, not closed-loop: a Hilbert
FIR (`MakeHilbert`, Hamming-windowed sinc-difference -- corrected from an earlier misidentification as
Hann; the real formula is `0.54 - 0.46*cos(2*pi*n/N)`, `fir.cpp:458`, not Hann's `0.5 - 0.5*cos(...)` --
`fir.cpp:432`) produces a quadrature component,
paired with the delayed real signal (delay = half the tap count) to form an analytic signal;
`atan2(quadrature, delayedReal)` gives instantaneous phase; consecutive-sample phase difference
(unwrapped to ±π, lag depends on a sample-rate-tiered decimation factor `m_df`) gives frequency; a
final order-3/1800Hz Butterworth IIR smooths the result. No VCO, no loop filter, no lock-acquisition
transient in the PLL sense.

**Confirmed directly** (re-verified, not re-trusted from the earlier flag): `sstv.cpp:1492`'s
`CSSTVDEM` constructor sets `m_Type = 2` — Hilbert really is legacy's compiled-in default (PLL=0,
zero-crossing=1 are the non-default alternatives, `sstv.cpp:2256-2268`'s dispatch switch). It's a real
user-facing setting (`Option.cpp`'s `RGDemType` radio group, `.ini` `DemType` key, `Main.cpp:1937`) —
this port has no settings/UI layer yet to expose an equivalent toggle.

**Settling-time comparison, partly measured directly (not guessed)**:
- CHILL's FIR stage is a fixed-length window: legacy tiers tap count by sample rate (`SetWidth`,
  `sstv.cpp:3022-3051`) — 12 taps below 16kHz `SampBase`, 24 up to 40kHz, 48 above. That's a
  deterministic (no asymptotic tail) 13-sample transient at 11025Hz, 49 samples at 44100Hz.
- CHILL's final smoothing IIR is the exact same filter design this port already has and trusts
  (`IirFilter.Design` — already order-agnostic, confirmed by reading it, no new filter-design code
  needed). Instantiated it with CHILL's real params (order 3, 1800Hz cutoff) in a throwaway test
  (written, measured, then deleted — not committed) and measured its unit-step response directly:
  **3 samples to 95-99% at 11025Hz, 14 samples at 44100Hz.**
- Combined worst-case CHILL transient ≈ ~16 samples at 11025Hz, ~62 samples at 44100Hz — bounded and
  monotonic, no overshoot below the target.
- Against that: piece 10's own already-measured PLL number — undershoots BELOW the starting value
  within ~5 samples, then recovers over 30+ samples. Open-ended feedback-loop dynamics, and actively
  wrong-direction during recovery, not just slower.
- **Honest caveat, not smoothed over**: RM12 at 44100Hz has only ~12.7 samples/pixel dwell time
  (piece 10's own cited number) — shorter than CHILL's own 49-sample FIR window at that rate. CHILL
  would not fully settle within one RM12 pixel either. It would very likely still beat PLL there
  (bounded/monotonic vs. an active wrong-direction dip), but "Hilbert fixes RM12 outright" is not a
  safe claim without actually simulating per-pixel error — window-length comparison alone doesn't
  prove it, and this pass did not go that far.

**Integration cost, now concrete rather than vague**:
- AFC's `SyncFreq` call sites are already demodulator-agnostic in legacy (same call shape for all
  three `m_Type` branches, `sstv.cpp:2256-2268`) — this port's existing `AfcTracker`/
  `ApplyAfcCorrections` should accept a swap with no rework, just a different `d` source.
- A previously-just-flagged gap is now pinned down exactly: `Main.cpp:3794`/`5528` apply an extra
  `n -= dp->m_hill.m_htap/4` sync-anchor correction specific to Hilbert's own group delay, on top of
  the anchor correction piece 8's `SyncAnchorCorrector` already ports for the PLL case. Real, small,
  additive follow-on work if this gets implemented — not blocking, but not free either.
- The final smoothing-IIR stage needs zero new filter-design code (existing `IirFilter` already
  covers it, order 3 included). The Hilbert coefficient generator (`MakeHilbert`) and a tap-delay
  line (`DoFIR` — no equivalent utility exists anywhere in this port yet, checked) would be genuinely
  new, but small and mechanical (~40-50 lines combined).
- This replaces the demodulator for ALL modes, not just narrow ones — a full test-suite regression
  risk (golden-vector + round-trip, all 43 modes), not a scoped one like pieces 10-12 were.

**Sizing**: medium, self-contained piece, comparable to piece 8 or 9 — new demodulator class, a small
`SyncAnchorCorrector` addition, one construction-site swap in `AnalogFmSstvDecoder`, and a full-suite
re-run. Real but not slam-dunk case for closing the narrow-pitch gap (see the RM12 caveat above).

**Status: scoped, not decided.** User's call (2026-08-01): log findings, no decision yet on whether to
implement — revisit later, alongside or after Windows CI per the existing sequencing note.

**QSSTV cross-check (secondary reference, not authoritative per CLAUDE.md precedence) — worth noting
for whenever this is picked back up.** Checked `QSSTV-main/src/dsp/filter.cpp:184-229`
(`filter::processFIRDemod`) and `QSSTV-main/src/sstv/` for how QSSTV's own SSTV video demodulator
works: **no PLL anywhere in its SSTV/DSP code** (confirmed by grep across `src/dsp/` and `src/sstv/`,
excluding the unrelated DRM module). QSSTV mixes the input to baseband I/Q via an NCO, FIR-filters
both channels (**181 taps**, `VIDEOFIRNUMTAPS`, centered 1900Hz — far longer/sharper than `CHILL`'s
12-48 tap Hilbert filter), then does a delay-and-conjugate-multiply + `atan2` between consecutive I/Q
samples for the phase-difference/frequency reading, plus a sanity clamp (500-2600Hz, holds previous
value on violation) and a final FIR smoothing stage. Architecturally the same family as `CHILL` —
instantaneous/feedforward phase-difference discrimination, no closed loop — just via NCO down-mixing
instead of a wideband Hilbert transform on the passband signal directly.

Two independently-developed SSTV decoders (legacy YONIQ's real default, and QSSTV) both landed on
non-PLL discrimination for video decode — real corroborating evidence that the approach works in
practice, tempering the "PLL is probably safer for noisy real-world reception" caution above somewhat.
**But not without a caveat that matters if Hilbert ever gets implemented here**: QSSTV's 181-tap
front-end FIR is doing substantial noise-rejection work before its memoryless discriminator runs —
plausibly compensating for giving up PLL's loop-based noise integration with heavy front-end
filtering instead, not because instantaneous discrimination is inherently noise-robust on its own.
`CHILL` has no equivalent front-end weight. This connects directly to an already-flagged, still-unported
gap in this codebase: legacy's real pre-AGC/pre-demodulator bandpass filter chain (`sstv.cpp:1824-1833`,
logged elsewhere in this file). If Hilbert is implemented without also addressing that gap, real-world
noise robustness may not match either legacy's own Hilbert path or QSSTV's — worth treating that filter
chain as more load-bearing than it looked in isolation, not assumed away.


## Piece 14 — Hilbert demodulator (`CHILL`) port, implemented

Follow-up to the scoping pass above. Two rounds of auditor plan-review (soft-3-round backstop; round 2
verdict said round 3 wasn't needed), then implementation, then a real before/after measurement across
all 43 modes plus both golden-vector real-audio fixtures.

**Plan-review round 1 found 4 real blockers** in the first draft (all independently re-verified against
source before accepting): missing `m_OFF`/`m_OUT` per-tier multipliers (`sstv.cpp:3032-3047` — a 4x
error at 44100Hz, existing specifically to cancel the `2^df` phase-lag scaling); no output-domain spec
at all (legacy's scaled value is `(1900-f)*32768/800`, confirmed three ways, and the final smoothing
IIR filters THIS scaled value directly, not Hz — filtering in Hz first would give the filter a false
0Hz cold-start instead of the correct 1900Hz one, reproducing exactly the settling problem this piece
exists to fix); AFC re-sourcing (legacy's PLL branch feeds AFC from a *separate* zero-crossing counter,
but Hilbert's branch feeds AFC from its own output — this port's existing AFC modeled the PLL branch
specifically, would have kept modeling the wrong branch after the swap); `DoFIR`'s impulse response is
the REVERSED coefficient array (newest sample at the last buffer index), which a "natural" forward
convolution would silently get backwards, compounding with `MakeHilbert`'s own antisymmetry into a
sign error that could cancel invisibly in a self-consistency test while still being wrong.

**User decided to bundle the AFC re-sourcing into this piece's scope** (not defer it).

**Round 2 re-verified every round-1 fix from source independently** (not from round 1's own summary)
and confirmed all four correct, with MORE supporting evidence than round 1 found for two of them (the
output-domain derivation, and the fixed-width-simplification precedent). It also found ONE genuinely
new blocker: **legacy's AVT training-lock state machine always calls `m_pll.Do(ad)` directly**
(`sstv.cpp:2129/2159/2169/2187/2222`), completely outside the `m_Type`-dispatched picture-demodulation
switch — legacy always uses PLL for AVT lock detection regardless of which demodulator handles the
picture stream. This port's `AvtTrainingLockStateMachine` previously reused the main decode path's
stream on the explicit (soon-to-be-false) premise that "both legacy's `m_pll` here and this port's main
decode path are the exact same demodulator" — exactly the "inferred one code path from a neighboring
one" failure shape CLAUDE.md §4's Scottie incident warns about, caught before any code was wrong.
**User decided: keep a dedicated `PllFmDemodulator` instance feeding AVT**, matching legacy's real
dual-demodulator structure, rather than accepting the divergence. Two smaller paper fixes also applied:
both `MakeHilbert` buffers are `tap+1` elements, not `tap` (`sstv.h:380/381`, inclusive loop bounds);
the `m_df` warm-up assertions can't hold on the whole assembled class (FIR fill + IIR settling sit on
top of the phase-diff stage), so they're tested against the phase-diff logic in isolation instead.

**Implementation**: new `HilbertFmDemodulator` (`src/ScanlineStudio.Core.Sstv/`) — a literal port of `CHILL`,
with `MakeHilbert`/`DoFir`/`ComputePhaseDifference` each extracted as independently-testable
`internal static` methods (mirroring legacy's own free-function shapes) specifically so the riskiest
details (reversed-kernel indexing, `2^df` lag/warm-up counts) could be tested in isolation before
wiring. `AnalogFmSstvDecoder`'s main picture-decode demodulator swapped from `PllFmDemodulator` to
this class; `ApplyAfcCorrections`/`InitializeAfc` re-sourced from `_demodulatedFrequencies` directly
(no more `ZeroCrossingFrequencyCounter` in the mainline path); `AvtTrainingLockStateMachine` now fed by
a dedicated `PllFmDemodulator` instance (warmed up on real preceding audio, mirroring
`TryResolveSyncAnchorCorrection`'s own established technique); `SyncAnchorCorrector`'s caller adds the
`+htap/4`-samples term (sign independently confirmed two ways during review: algebraic substitution
into this port's already-established `-n` sign-flip convention, and a physical cross-check that the
Hilbert path delays the picture stream relative to the sync envelope this fold tracks). Both
`PllFmDemodulator` and `ZeroCrossingFrequencyCounter` stay in the codebase, genuinely used (AVT and
their own dedicated test suites respectively) — no `docs/removed-features.md` entries needed.

**A real bug caught by "test early, test often," not by review**: the AVT warm-up loop originally ran
eagerly in `TryStartAvtTraining`, unconditionally reading raw samples up to `_avtTrainingOriginSample`
-- `ArgumentOutOfRangeException` on the very first full-suite run, because a chunked/streaming
`PushSamples` caller can invoke `TryStartAvtTraining` before the buffer has actually grown that far.
Fixed by deferring the warm-up into `TryResolveAvtTraining`, gated on the origin point being fully
buffered, running exactly once whenever that becomes true.

**Full before/after measurement, all 43 modes + both golden-vector real-audio fixtures (measured
directly via a temporary diagnostic added to each test file, then removed -- not assumed, not
estimated): 43 of 45 measurements improved, 2 worsened.** The 2 that worsened are RM8 (1.766→2.065)
and RM12 (1.439→1.670) — exactly the narrow-pitch modes the original Piece 10 finding and the scoping
pass's own caveat both flagged as marginal (RM12's ~12.7 samples/pixel dwell time at 44100Hz is shorter
than `CHILL`'s own 49-sample FIR window there, so it can't fully settle within one pixel either). Both
worsenings are small and stay comfortably inside the existing flat 10.0 tolerance. Every other mode
improved, typically by 0.6-1.9 points (avg/max-per-channel delta). Golden-vector real-audio fixtures
also improved: martin-m1 1.438→1.312, robot-36 14.809→14.307 (`GoldenVectorTests.cs`'s own tolerance
comment updated with these numbers). No tolerance values needed to change anywhere in the suite.

**Tests**: `HilbertFmDemodulatorTests` (23 tests) — `MakeHilbert` coefficients checked against
independently-computed (Python, not derived from or captured against the C# implementation) fixture
values plus a structural antisymmetry check; `DoFir`'s reversed-kernel impulse response;
`ComputePhaseDifference`'s `2^df` lag and warm-up counts in isolation; settled-tone output at 1500/
1900/2300Hz (not just the center frequency, which can't catch a sign inversion); an exact-zero-real-
component case (confirmed to correctly read 0Hz, not the center frequency -- a genuinely non-oscillating
signal has no instantaneous frequency to report, a distinction an earlier draft of this test itself
got wrong before the code). Full suite: 363/363 passing.

**Status: implemented, committed.** Bandpass-filter-chain follow-up (the QSSTV-cross-check item above)
remains logged as a separate, deliberately deferred piece, not bundled here per user instruction.


## Pre-AGC bandpass filter chain — scoping, second opinion, and split into Piece A / harness / Piece B

Picked up the deferred bandpass-filter-chain item next. Investigation (`sstv.cpp:1819-1839`'s
`CSSTVDEM::Do`) found it bigger and more architecturally invasive than expected: `MakeFilter`
(`fir.cpp:332-427`) is a full Kaiser-windowed arbitrary FIR designer (not a one-off formula like
`MakeHilbert`); three filter variants (H1/H2/H3) across 3 width presets; the convolution engine used at
the call site (`CFIR2::Do`) uses a DIFFERENT addressing convention than `HilbertFmDemodulator`'s own
`DoFir` (`H[0]` pairs with the newest sample, not the oldest); and filter *selection* depends on
legacy's real-time per-sample lock state (`m_Sync`/`m_SyncMode`), which this port's upfront-buffer-
demodulation architecture doesn't have available at the point it would need it.

**Requested and got an independent second opinion (the `auditor` agent, framed explicitly as a scope/
value judgment, not a code-fidelity check) before committing effort.** It verified every technical
claim from source directly and found real corrections to both directions:
- The scope was overstated in one way: `MakeFilter` skips the Kaiser/Bessel branch entirely below 21dB
  attenuation, and every "Wide"-preset call (the default) uses `att=20` — so a Wide/H2-only slice never
  touches the Bessel `I0` function at all, comparable in size to `MakeHilbert`, not bigger.
- It was understated in another, more important way: legacy's real demodulator input (`m_Cur`,
  `sstv.h:256-257`) IS the post-filter, pre-AGC value — independently confirmed by reading `CLVL::Do`
  directly. This port's demodulator has always been fed raw, unfiltered samples. This isn't an optional
  noise-robustness bonus sitting off to the side; it's a real, structural input-pipeline gap, and it
  means the filter chain's effect IS measurable (it reshapes the demodulator's own input), contradicting
  an earlier "can't measure this" framing.
- It also caught a real risk this session had missed (group-delay skew between the picture-demod path
  and the sync/envelope path if the filter were applied to only one) and downgraded an overweighted one
  (the `CFIR2`/`DoFir` addressing-convention mismatch doesn't matter here — `MakeFilter`'s output is
  symmetric by construction, unlike `MakeHilbert`'s antisymmetric kernel, so reversal is a mathematical
  no-op for this filter specifically).
- Its recommendation, adopted: **split into Piece A** (the always-on, unconditional 2-tap
  moving-average pre-filter, `d=(s+m_ad)*0.5` — small, no filter design, no lock-state dependency, do
  immediately) **and defer the Kaiser bandpass filter itself (Piece B) behind a noise-fixture harness**
  that doesn't exist yet, converting "we think this helps" into an actual measurement rather than a
  judgment call. Explicitly recommended over a deferred-second-pass correction (AFC's own pattern):
  since this filter sits upstream of AGC, a deferred correction would mean re-running AGC, the
  demodulator, and every sync/envelope detector for the whole post-lock buffer — not a correction, a
  second full decode.
- On Piece B's expected value specifically, the auditor was MORE skeptical than this session's own
  framing, not just hedging alongside it: the *safe* scope (H2, run continuously, no lock-state
  switching — the only version without correctness risk) is also, by construction, the WEAKEST filter
  legacy ever runs (widest band, lowest attenuation, fewest taps) — so even a faithful port of the safe
  slice may not deliver the QSSTV-comparison noise-robustness benefit that motivated wanting this at
  all. Its verdict on deferring Piece B: not "can't measure the win" but "the only measurable outcome
  available today is regression detection... weak return" — defer specifically because the noise
  harness would make it a decidable question instead.
- User, after this discussion, raised a stronger alternative to the synthetic-noise harness: an actual
  TX'd picture recaptured over a real WebSDR (real atmospheric/propagation noise and receiver
  characteristics, not a synthetic AWGN approximation) — agreed this is categorically better evidence
  than the synthetic harness, and if also run through legacy's own decoder, would double as a genuine
  new golden-vector fixture (this project currently has exactly two, both already documented as
  unusually clean captures). Bigger practical lift (needs real TX+WebSDR access, not just code) but
  strictly better evidence for the exact question at hand. Not yet done — next-step decision point.


## Piece 15 — legacy's always-on 2-tap moving-average pre-filter, implemented

`CSSTVDEM::Do`, `sstv.cpp:1824-1825`: `d=(s+m_ad)*0.5; m_ad=s;` — unconditional (not gated by `m_bpf`),
applied before AGC and before the demodulator. `m_ad` zeroed once at construction (`sstv.cpp:1417`),
confirmed never reset in either `Start()` overload or `Stop()` — matches this port's existing
continuously-running-filter precedent. Traced all three post-filter signal domains directly from
source (independently re-verified during plan-review, not just accepted): `m_Cur` (post-filter,
pre-AGC) feeds the picture demodulator; `ad` (post-filter, post-AGC, unscaled) feeds AVT's dedicated
PLL call; the final scaled+clipped `d` (`ad*32`, clip ±16384) feeds the sync/tone-envelope detectors.

New `FilteredRawSampleAt(int index)` -- a pure function of `_rawSamples` (no adaptive state, so safe to
call independently from multiple sites with bit-identical results, no shared cache needed). Applied at
all four sites that previously fed raw samples directly: `AgcSampleAt` (upstream of the existing AGC
logic, which was already otherwise correct), `PushSamples`'s main demodulator feed, and both of
`AvtTrainingLockStateMachine`'s dedicated-`PllFmDemodulator` call sites (closes part of that class's
already-flagged, still-only-partially-resolved input-domain gap from Piece 14 -- the deeper
unscaled-AGC-domain mismatch stays exactly as previously deferred, not expanded into here). One
single-round auditor plan-review before implementation (matching the piece's small size) found the
helper should return `double`, not `float` (legacy computes entirely in double; an earlier draft's
`float` return introduced an avoidable rounding step) -- fixed before coding.

**A real, pre-existing bug found by the new chunk-boundary test, unrelated to this piece.** Following
the auditor's recommended test (decode the same signal as one `PushSamples` call vs. many small
chunks, assert identical results), a first version asserting EXACT pixel identity failed -- confirmed
via `git stash` to fail IDENTICALLY on pre-Piece-A code too, so not a regression from this piece.
Measured severity: ~1.75 average per-channel delta between whole-push and chunked-push decodes of the
same signal, comfortably inside every tolerance already in this suite. Root cause not chased (off-scope
for this piece): this port's deferred/incremental correction passes (AFC, Auto Slant) process "whatever
is available so far" as data streams in, so chunk timing can shift their exact correction values by a
small amount -- a real, small, pre-existing characteristic of the port's architecture that no existing
test had caught (every other chunked test in this suite only checks mode-detection equality, not full
pixel identity). Test adjusted to a 5.0-tolerance comparison instead of exact identity -- still catches
a genuine Piece-A-specific regression (a wrong previous-sample reference at a chunk boundary would
produce a structural misalignment, not a small ambient delta like this), without being blocked by the
unrelated pre-existing gap.

**Measured before/after, all 43 modes + both golden-vector fixtures (same methodology as Piece 14): 18
improved, 26 worsened, 1 unchanged -- but every change is tiny** (mostly <0.1, largest is robot-36's
real-audio fixture at +0.169). This is the expected signature of a smoothing filter applied to fixtures
with little noise to remove: the synthetic round-trip fixtures carry zero noise, and both real
golden-vector captures are already-documented as unusually clean -- a mixed, small-magnitude result is
consistent with "correctly implemented, real value not provable with what we currently have to test
against," not a regression. Everything stays comfortably inside existing tolerances; none needed to
change. `GoldenVectorTests.cs`'s own tolerance comment updated with the new numbers.

**Tests**: the new chunk-boundary consistency test (`SstvRoundTripTests.cs`) described above. Full
suite: 364/364 passing.

**Status: implemented, not yet committed as of this entry.** Piece B (the Kaiser bandpass filter
itself) remains deferred behind either a synthetic noise-injection harness or, if arranged, a real
TX/WebSDR capture -- decision point, not yet started.


## Noise-robustness harness — built, baseline measured

User's call: build the synthetic noise-injection harness now as an interim check, while separately
weighing a real TX/WebSDR recapture (categorically better evidence -- real propagation/receiver
characteristics, not synthetic AWGN -- and would double as a new golden-vector fixture if also run
through legacy's own decoder) as a longer-lead-time follow-up. Not a legacy port (legacy has no
synthetic-noise-injection concept of its own) -- new test infrastructure, built directly rather than
through the usual plan+auditor-review cycle since there's no legacy source to verify fidelity against;
the judgment calls (noise model, SNR calibration, the "usable decode" quality bar) are documented
inline in `NoiseRobustnessTests.cs` instead.

**Design**: additive Gaussian noise injected into the ENCODED AUDIO SAMPLES (post-encode, pre-decode --
the domain a real receiver's front-end noise actually occupies), calibrated to a target SNR by
measuring the real encoded signal's own RMS power first (`SNR_dB = 20*log10(signalRms/noiseRms)`), not
an assumed/fixed noise amplitude. Deterministic (fixed seed) for reproducible, genuinely comparable
results run to run. Sweeps a fixed set of SNR levels (40 down to 0dB) per mode, measures average
per-channel delta at each, and reports the "noise floor" -- the lowest SNR still meeting a documented
"usable decode" bar (30.0 average delta, a judgment call noted as such, not derived from source: looser
than `SstvRoundTripTests`' own noiseless-signal tolerances, which measure DSP self-consistency, not
real noise tolerance). One regression-guard assertion (the noiseless/infinite-SNR case must still
decode correctly) catches the harness itself being broken; the SNR sweep itself is informational,
logged via `ITestOutputHelper`, not strictly asserted per level -- there's no known-correct threshold
to assert against yet, that's what this harness exists to discover.

**Baseline measured (this port's CURRENT state: `HilbertFmDemodulator` + piece 15's 2-tap pre-filter,
no Kaiser bandpass filter yet)**:
- **martin-m1**: noise floor **9.0dB** SNR. Degrades roughly monotonically from 2.72 (40dB, clean) to
  25.24 (9dB, still usable) to 33.48 (6dB, fails the bar).
- **robot-36**: noise floor **16.0dB** SNR -- meaningfully worse tolerance than martin-m1, consistent
  with this mode's already-documented fragility to small timing perturbations (piece 8's tone-selector-
  ambiguity finding). Degrades roughly monotonically down to 16dB (22.18, still usable), then becomes
  NON-monotonic at more extreme noise (12dB=45.90, 9dB=122.18, 6dB=58.19, 3dB=73.50) -- expected, not a
  harness bug: at very low SNR, header/VIS detection can fail outright in qualitatively different,
  effectively chaotic ways (wrong-mode misdetection, garbage decode) rather than smoothly degrading,
  which the harness's own doc comment already flagged as an unproven assumption, not asserted.

**These two numbers are the actual comparison target for Piece B**, not a pass/fail gate on their own --
re-run this same harness (`NoiseRobustnessTests.cs`) once the Kaiser bandpass filter exists and compare
the new noise floors against 9.0dB/16.0dB. A meaningful improvement (materially lower noise floor, i.e.
usable decode at a WORSE SNR than today) is the actual evidence this piece is worth its cost; no
meaningful change would be real, measured evidence for the auditor's own skepticism (the safe/H2-only
scope being "the weakest filter legacy ever runs") turning out correct.

**Tests**: `NoiseRobustnessTests.cs`, 2 new tests (martin-m1, robot-36). Full suite: 366/366 passing.

**Status: implemented, not yet committed as of this entry.** Piece B itself still not started --
baseline now exists to measure it against, decision on WHEN to build Piece B (now vs. after arranging a
real TX/WebSDR capture too) not yet made.


## Piece B — the Kaiser/search bandpass filter (`H2`), implemented and measured against baseline

User's call: build Piece B now rather than waiting on a real TX/WebSDR capture. `SearchBandpassFilter`
-- a literal port of `CSSTVDEM::Do`'s pre-AGC bandpass stage (`sstv.cpp:1826-1833`), scoped to ONLY
legacy's `H2`/"search" width variant (`sstv.cpp:1522-1551`'s `CalcBPF`) per the earlier scoping
discussion's adopted recommendation: the widest, most permissive of the three lock-state-selected
variants, run continuously rather than gated by `m_Sync`/`m_SyncMode` (this port's upfront-buffer
architecture has no real-time lock state available at the point legacy would switch filters). `H1`/`H3`
and the lock-state switch itself deliberately not ported.

**Scope confirmed narrower than `MakeFilter`'s full generality once traced**: `H2`'s parameters
(400-2500Hz, attenuation 20) are identical across all three legacy width presets -- only tap count
differs (24/64/96, scaled by sample rate) -- and this port's only reachable preset is the shipped
default (Wide, confirmed `sstv.cpp:1416` and the `.ini` `DEMBPF` fallback, `Main.cpp:1855`). The
Kaiser/Bessel branch of `MakeFilter` (`fir.cpp:346-427`) only activates at attenuation >=21dB; `H2` is
always 20 -- provably unreachable for this filter, not an approximation, so not ported (matches piece
14's `MakeHilbert` precedent of omitting a provably-unreachable branch).

Chains onto piece 15's `FilteredRawSampleAt` output (`sstv.cpp:1824-1834`'s real order: 2-tap
pre-filter -> `if(m_bpf) m_BPF.Do` -> AGC), applied at the same four consumption sites piece 15 already
touches (`AgcSampleAt`, the main demod feed in `PushSamples`, both of AVT's dedicated-PLL call sites) via
a new forward-fill cache (`BandpassFilteredSampleAt`, mirroring `AgcSampleAt`'s own established pattern).

**Auditor plan-review, one round, found one load-bearing issue and one latent correctness gap, both
independently re-verified against source before fixing**:
- **Causal-vs-centered window (the important one).** `CFIR2::Do`'s real convolution (`fir.cpp:1131-1144`)
  pairs `H[0]` with the NEWEST sample and walks backward -- a genuine, constant `tap/2`-sample group
  delay (~1.09ms at every reachable rate, since tap scales with rate), not a centered window. `MakeFilter`'s
  output kernel is symmetric by construction for this port's only reachable (even) tap counts, which rules
  out a coefficient-REVERSAL sign risk (unlike `HilbertFmDemodulator`'s antisymmetric kernel) but does
  NOT make causal-vs-centered alignment irrelevant: a centered window would pass a symmetry check, a
  coefficient-fixture check, and a frequency-response check identically while silently shifting every
  downstream sync/slant/line anchor by `tap/2` samples -- the exact "restructured-but-provably-equivalent"
  failure shape CLAUDE.md §4's Scottie incident warns about. Reasoned explicitly, not assumed, why this
  needs NO new sync-anchor correction term (unlike piece 14's Hilbert `+htap/4`): the filter sits upstream
  of ALL four consumption sites uniformly, so the sync-envelope path and the picture-demod path see the
  same new delay together -- no differential delay for `SyncAnchorCorrector`'s argmax search to be wrong
  about. Defended primarily by an impulse-response test (`ProcessSample_ImpulseResponse_IsCausal_NotCentered`),
  the only test shape that can distinguish causal from centered.
- **Odd-tap symmetry claim was over-broad.** The mirroring loop in `MakeFilter` only writes `2*(tap/2)+1`
  entries (integer division) -- for odd tap, the trailing coefficient is never written and stays
  zero-init, genuinely asymmetric. This port's only reachable tap counts (24@11025Hz, 96@44100Hz) are
  both even, so latent, not currently wrong -- ported to mirror legacy's EXACT loop bounds rather than
  assume full-array coverage, and the symmetry test scoped explicitly to even tap only.

**Performance regression found and fixed, not predicted (though flagged by the auditor as a possible
follow-up if it happened): full suite went from ~3min baseline to 12min2s (387/387 still passing) after
first wiring Piece B in.** Root cause, two compounding issues: (1) the original design was
`ProcessSample(Func<int,double> filteredSampleAt, int index)`, recomputing the O(tap) convolution from
scratch on every call with no cache, and multiple call sites frequently requesting the same index; (2)
`Func<int,double>` delegate-call overhead, multiplied by up to 97 taps per convolution at 44100Hz. Fixed
in two steps: a forward-fill cache alone brought a `SstvRoundTripTests` subset from timeout territory to
6min57s for 41 tests -- still not enough -- so `SearchBandpassFilter` itself was redesigned from the
stateless `Func`-based window lookup to a genuine streaming delay line (`ProcessSample(double input)`,
`Array.Copy`-shift + dot-product), explicitly modeled on `HilbertFmDemodulator.DoFir`'s already-proven
pattern. Full suite after the redesign: **387/387 passing, 4min44s** -- close to the pre-Piece-B baseline,
the remaining difference being genuine new per-sample work, not overhead.

**Noise-floor comparison against the established baseline (the actual acceptance criterion, per
`NoiseRobustnessTests.cs`'s own stated test-plan item) -- meaningful improvement in both modes**:

| Mode | Baseline (piece 15, no Piece B) | With Piece B | Improvement |
|---|---|---|---|
| martin-m1 | 9.0dB | 3.0dB | 6dB lower noise floor |
| robot-36 | 16.0dB | 9.0dB | 7dB lower noise floor |

Both modes now decode usably (average per-channel delta <= 30.0) at meaningfully worse SNR than before
-- real, measured evidence the filter is worth its cost, not just legacy-parity-for-its-own-sake. This
directly answers the auditor's own stated skepticism (the safe/H2-only scope being "the weakest filter
legacy ever runs, may not deliver the benefit") with a measurement rather than more argument either way.

**Tests**: `SearchBandpassFilterTests.cs`, 21 new tests (coefficient fixtures at two tap/rate
combinations independently computed in Python, not derived from the C# implementation; the causal
impulse-response test; an 8-point frequency-response sweep against independently-computed magnitudes).
Full suite: 387/387 passing, 4min44s. `NoiseRobustnessTests.cs` re-run: 2/2 passing, new noise floors
recorded above.

**Status: implemented, measured against baseline, not yet committed as of this entry.**

**Demo:** a console/test harness encodes a test image to a `.wav`, decodes it back, and the round-trip image matches within tolerance — provable before any UI exists.


## Windows CI fix — `dotnet restore`/`build`/`test` failing since Engine 0-6, root cause found and fixed

Not a DSP/port item — CI infrastructure, tracked here because it blocked seeing green Windows results
for everything above. `windows-latest` had been failing since Engine 0-6 (2026-07-30); Linux/macOS legs
were unaffected.

**Root cause:** `.github/workflows/ci.yml`'s Windows-only `ilammy/msvc-dev-cmd@v1` step (added for
`ScanlineStudio.Core.Audio.MiniAudio`'s `BuildNativeShimWindows`/`cl.exe` target) sets `Platform=x64` as a
job-level env var. MSBuild/`dotnet restore` implicitly reads ambient `Platform`/`Configuration` env vars
as default property values, and `ScanlineStudio.sln` only defines "Any CPU" solution configurations (no
"Debug|x64") — so restore failed on every Windows run: `error MSB4126: The specified solution
configuration "Debug|x64" is invalid.` Build/Test steps never ran. Confirmed via `gh run view <id>
--log-failed` across several failing runs, all showing the identical Restore-step error.

**Fix:** pass `/p:Platform="Any CPU"` explicitly on the `restore`/`build`/`test` steps, overriding the
ambient env var. `cl.exe` itself is invoked via an `Exec` command in `BuildNativeShimWindows`, not
through MSBuild's `$(Platform)` property, so it's unaffected by the override and still gets the
x64 toolchain msvc-dev-cmd set up.

**Verified against real CI runs, not just eyeballed:** two consecutive pushes both green on all three
legs (`gh run watch <id>`) — Windows 5m25s / macOS 2m31s / Linux 3m51s on the first, Windows/macOS/Linux
all passing again on the second. Commits `8be35b7` (fix), `761ef1a` (doc update).

**Status: fixed and committed. Windows/Linux/macOS all green on `master`.**


### Band-1 item 1 (S4) — exception-swallowing catch in `MiniAudioCaptureSession`, DONE

Started with this one first since it's the smallest and most standalone (no dependency on the
"streaming contract" architecture question Pattern 1 flagged).

**Draft plan**: keep the existing catch (deliberately added by an earlier opus-review pass -- a
raw background `Thread`, unlike a thread-pool work item, dies the whole process on an unhandled
exception, so some catch is required), but stop it being silent -- add a `LastCallbackException`
property mirroring the file's existing `TimedOutDuringClose` pattern.

**Auditor plan-review verdict: buildable, but 3 things needed fixing on paper first (no second round
needed):**
1. `TimedOutDuringClose`'s plain-auto-property pattern doesn't transfer -- that one is written once
   under a write lock during `Dispose`, with `_drainThread.Join()` supplying the happens-before for
   its single read site. The new field is written repeatedly on a live thread with readers on
   arbitrary other threads -- needs `volatile` (reference field) / `Interlocked`+`Volatile.Read`
   (counter), not a plain auto-property.
2. Needed a counter alongside the exception, not just the last value (`OverrunCount`'s own "raw
   counter for the caller to interpret" shape is the in-file precedent) -- otherwise "threw once" and
   "threw on every single chunk of a 2-minute transmission" are indistinguishable.
3. The class is `internal` with `InternalsVisibleTo` scoped to tests only -- as drafted, the property
   would be unreachable from any real production caller. Needed a `MiniAudioEngine` pass-through,
   mirroring the existing `CaptureOverrunCount` pattern exactly.
4. (Judgment call, not a blocker) auditor also caught a real second bug on its own initiative:
   `SamplesAvailable?.Invoke(samples)` is a single try/catch around the WHOLE invocation list -- one
   throwing subscriber silently starves every other subscriber, and every later chunk, from being
   delivered. Real designed-for scenario per `spec/05-audio-engine.md:89` (a VU-meter subscriber
   running alongside the DSP decode pipeline on the same stream) -- fixed in the same commit.
5. (Nit) renamed away from "Callback" (already means the native real-time callback everywhere in
   this file's vocabulary) -- `LastSubscriberException`/`SubscriberExceptionCount` instead.

**Implemented**: `volatile Exception? _lastSubscriberException` + `Interlocked`-incremented
`_subscriberExceptionCount`, exposed as `LastSubscriberException`/`SubscriberExceptionCount`
(deliberately NOT gated on `_disposed`, unlike `OverrunCount` -- this is exactly the state a caller
wants to inspect right after a session dies). `DrainLoop` now iterates
`SamplesAvailable.GetInvocationList()` with a per-handler try/catch instead of one catch around the
whole multicast call. `MiniAudioEngine.CaptureLastSubscriberException`/`CaptureSubscriberExceptionCount`
pass-through added, mirroring `CaptureOverrunCount`.

**Tested**: new `SamplesAvailable_SubscriberThrows_IsRecordedAndDoesNotStarveOtherSubscribers` test
(real virtual-sink audio, not mocked) — one handler throws every chunk, a second handler counts its
own invocations; asserts the second handler keeps firing (proves per-handler isolation) AND
`LastSubscriberException`/`SubscriberExceptionCount` are correctly recorded. Full suite: 387/387
`ScanlineStudio.Core.Sstv.Tests`, 51/51 `ScanlineStudio.Core.Audio.MiniAudio.Tests` (49 previous + this new one +
one other pre-existing), solution-wide build clean. **Status: DONE, committed `288d5d0`.**


### Band-1 items 2+3 (S2 memory growth, S3 chunk-timing sensitivity) — per user instruction, following
the auditor's Pattern-1 recommendation: treated as one combined piece rather than two independent
patches, since both were flagged as likely tracing to the same "upfront buffer vs. real-time stream"
architectural gap.

**Investigation phase first** (not a blind fix — matches this project's established methodology):
dispatched a dedicated investigative pass (read-only, no code changes) to find S3's actual root cause
before drafting any plan, since the original inventory's attribution ("AFC/Auto Slant process
'whatever's available so far'") was itself unverified.

**Root cause found, high-confidence, source-cited (not yet empirically confirmed by the investigator
itself — no Bash access in that pass; confirmation is the next step, done in this session directly
since Bash is available here):**

**AFC and Slant are NOT the cause — both confirmed already fully chunk-invariant** (`ApplyAfcCorrections`'s
bound can never be limited by `_demodulatedFrequencies.Count` since the enclosing per-line guard at
`AnalogFmSstvDecoder.cs:388` already guarantees enough data exists; `ApplySlantTracking` is bounded
only by `_consumedSamples`, never by `Count`). The original test comment attributing this to AFC/Slant
is wrong and needs correcting once the real fix lands.

**The real mechanism: two header-detection paths race, and priority is decided by call-boundary
timing, not absolute sample position.** `TryDecodeVisHeader` (the fixed-window, analytically-precise
path) refuses to commit until the full 910ms header is buffered (`:1443`,
`_demodulatedFrequencies.Count - headerStart < totalHeaderSampleCount` → `return false`) — when that
happens, `TryDecodeHeader` falls through (`:621`) to `TryInterleavedHeaderScan`, whose loop bound is
explicitly "whatever has arrived so far" (`:871`, `_syncBypassProcessedUpTo < _rawSamples.Count`) and
which commits at a DIFFERENT anchor (`VisLockStateMachine`'s own empirically-triggered lock, with a
self-documented, uncorrected group-delay lag — measured ~80 samples @11025Hz elsewhere in this file,
scaling to ~320 samples/~7.3ms @44100Hz). In a one-shot push, the fixed-window path is satisfied on
the very first `TryProcessBuffer` call and the fallback never runs at all. In a chunked push, the
fallback runs on every chunk from sample 0 onward and gets a real chance to win — a race window
several hundred samples wide (Martin M1 @44100: fixed-window needs `Count>=40131`; fallback can
commit as early as `trigger+12569`), and if it wins, the committed anchor is off by roughly the
group-delay lag (~7.3ms ≈ ~16 pixels of a Martin M1 scan) before `TryResolveSyncAnchorCorrection`'s
fold absorbs most of it — which is exactly why the symptom is a small ~1.75 ambient delta rather than
a visibly torn image, not evidence it's a small/unimportant bug.

**Structural, not a one-liner**, per the investigator: `TryDecodeHeader`'s own doc comment states the
intent "header wins, every time" but the implementation only delivers that when the fixed-window
path's availability gate happens to be satisfied on the same call it's checked — a call-scoped,
not sample-scoped, priority decision. Proposed principled fix (not yet plan-reviewed): scope the
fallback's own bound to lag the fixed-window path's own commit latency
(`_rawSamples.Count - maxFixedWindowHeaderLatency`), so the fixed-window path always gets first
refusal at the same absolute sample index regardless of how push calls are chunked.

**S2 (buffer trimming) is separable from S3 — confirmed, does not need to wait.** Hard rule for
correctness: the trim watermark must be `min(every processed-up-to cursor) - lookback` (never a
single cursor — some cursors run far ahead of others, e.g. AGC/bandpass-filter cursors vs.
AFC/Slant/VIS-lock cursors), AND trimming must never happen while `_mode is null` or
`_pendingAnchorCorrectionMode is not null` (exactly the region S3's bug lives in, and where every
long backward-read — AVT PLL warm-up, sync-anchor fold, header retry rescans — also lives). ~25
call sites need offset-translation (every buffer is indexed by absolute sample index today). The one
coupling: if S3's fix adds a new cursor (the fallback's lagged bound), it just joins the trim
watermark's `min(...)` — a one-line addition, not a redesign.

**Status: investigation done. Next: empirically confirm the race hypothesis (chunk-size sweep +
targeted temporary instrumentation at the 4 commit call sites, reverted before any real fix), THEN
get an auditor plan-review of the actual fix (both S3's priority-scoping fix and S2's trim-watermark
design) before writing production code. Test comment at `SstvRoundTripTests.cs:198-205` needs
correcting once the real fix lands (currently misattributes this to AFC/Slant).**

**Empirically confirmed (2026-08-02), done directly in this session (the investigative pass had no
Bash access) -- clean, unambiguous result, exactly the non-monotonic pattern predicted, not a smooth
AFC-drift pattern.** Added temporary `[CallerLineNumber]`-based instrumentation to `Commit()`
(reverted immediately after, working tree confirmed clean via `git status`/`git diff`), swept chunk
sizes for a real Martin M1 encode:

| Push shape | Commit call site | Anchor sample |
|---|---|---|
| Whole (one-shot) | line 1512 (fixed-window path) | 40131 |
| chunk=499/500/512/1000/1024/20000 | line 896 (interleaved VisLock fallback) | **40571** |
| chunk=4096/40131/45000 | line 1512 (fixed-window path) | 40131 |

Confirms the race exactly as hypothesized: small/misaligned chunk sizes let the fallback commit
first at a DIFFERENT anchor (440 samples off, ~10ms @44100Hz); larger/aligned chunk sizes let the
fixed-window path get satisfied first, matching the one-shot result. Not a diffuse drift — a discrete
either/or race outcome depending purely on push chunking, confirming the root-cause diagnosis, not
just corroborating it.

**Fix plan (drafted, not yet auditor-reviewed):**
- **S3**: give the fixed-window path a guaranteed first-refusal window in ABSOLUTE sample terms, not
  call-scoped terms. Concretely: `TryInterleavedHeaderScan`'s own scan bound becomes
  `Math.Min(_rawSamples.Count, _rawSamples.Count - maxFixedWindowHeaderLatency)` -- i.e. the fallback
  never examines/commits on samples the fixed-window path could still claim first. `maxFixedWindowHeaderLatency`
  needs deriving as the maximum `totalHeaderSampleCount`-equivalent across every mode this port
  detects via the fixed-window path (the fallback can't know in advance which mode is arriving, so it
  must wait out the worst case, not a specific mode's own value) -- needs a new
  `SstvModeRegistry`/`VisHeader` helper, not a hardcoded guess. Open question for the auditor: does
  this introduce unacceptable header-detection latency for genuinely headerless/degraded signals that
  currently rely on the fallback firing early (the whole POINT of `TryInterleavedHeaderScan`/`m_sint2`-
  equivalent detection)? Needs explicit discussion, not just implemented and hoped.
- **S2**: bound/trim the 5 growing buffers using watermark = `min(_afcProcessedUpTo, _slantProcessedUpTo,
  _visLockProcessedUpTo, _syncBypassProcessedUpTo, _avtTrainingProcessedUpTo, _consumedSamples) - lookback`
  (`lookback = max(2000, SearchBandpassFilter tap+1, Hilbert tap+1, ksbSamples, 1)`), with a hard rule:
  never trim while `_mode is null` or `_pendingAnchorCorrectionMode is not null`. ~25 call sites need
  offset-translation (every buffer indexed by absolute sample index today). If S3's fix adds a new
  cursor, it joins the `min(...)` -- confirmed compatible, not blocking.

**Status: both items' fixes now going to the auditor for a combined plan-review (per user instruction
to follow the auditor's own Pattern-1 recommendation -- one coherent piece, not two unrelated
patches) before any production code is written.**

**Auditor plan-review verdict: NOT ready to build. 7 real, paper-level defects found, each cheap to
fix now and expensive to retrofit after ~25 call sites are rewritten.**

1. **[blocker] Rolling cap is the wrong mechanism, not just a latency cost.** `bound = Count - L`
   permanently drops the tail of a finite stream (a headerless transmission in the last ~1.3s of a
   bulk/file decode becomes undetectable -- a NEW bug) and imposes a needless per-sample latency
   penalty forever (the fixed-window path's search is one-shot, not rolling -- once exhausted it's
   provably dead for the epoch). **Fix: one-shot `_fixedWindowExhausted` gate instead** (`scanBound =
   (Count >= _consumedSamples + L) ? Count : _consumedSamples`, cleared in `EndOfImage`). Cost:
   ~395ms one-time delay for the VisLock path per epoch; typically ZERO added delay for the
   genuinely-headerless m_sint1/2/3 path (already needs ≥0.6-1.34s of consecutive-interval matching).
2. **[blocker] `L` must be the max SEARCH CEILING (1305ms, extended-VIS's own retry margin), not max
   commit-gate duration (1150ms)** -- using the smaller number reopens the race, just narrower.
   Derive in `VisHeader` (not `SstvModeRegistry` -- not per-mode), and have
   `TryDecodeVisDataBits`/`TryDecodeNarrowModeHeader` compute their own `searchCeiling` from the SAME
   helper so the two can't silently desync.
3. **[blocker] Trim watermark formula had 3 real bugs**, independently re-derived by enumerating
   every backward-read site: missing `_levelAgcProcessedUpTo`/`_bandpassFilteredProcessedUpTo` from
   the `min()` (reachable today, not hypothetical -- AGC lags `_consumedSamples` right after
   `Commit`); `_syncBypassProcessedUpTo` is frozen while locked, so including it pins the watermark
   for a WHOLE IMAGE (up to ~380MB at PD290/44100 before it can advance again); lookback derivation
   was wrong (`SearchBandpassFilter`/`HilbertFmDemodulator` are streaming, tap counts don't belong;
   the real raw lookback is 1; the load-bearing 2000 constant is for the sync-anchor-correction
   warm-up specifically, not a generic safety margin -- needs sharing with those warm-up sites, not
   re-derived independently).
4. **[risk] `EndOfImage`'s 0.5s dead-time skip means AGC never gets fed through it** (deliberate,
   matches legacy's own continuous-feed behavior, `:204-210`'s existing doc comment) -- once
   `_levelAgcProcessedUpTo` joins the watermark (per #3), this PINS the watermark forever after image
   1 unless addressed. Recommended: force-feed `AgcSampleAt` through the dead zone in `EndOfImage`
   (0.5s of extra filter work per image, preserves the documented legacy-fidelity property).
5. **[blocker] The biggest one -- "never trim while `_mode is null`" is EXACTLY BACKWARDS.** The
   5.7GB/hr memory-growth scenario this whole fix exists for IS the never-locks case (an idle
   receiver on open squelch) -- excluding it from trimming means the actual motivating case is never
   fixed at all; the locked case is already naturally bounded by one image's duration. **Real fix:
   pre-lock trimming at a ~5s retention window** (`max(L=1.3s, SyncIntervalTracker's own max interval
   ×3 ≈4.17s, + anchor warm-up)` ≈8MB @44100 instead of unbounded) -- keep the "never trim while
   `_pendingAnchorCorrectionMode is not null`" half of the original rule, drop the `_mode is null`
   half entirely.
6. **[structural recommendation, not a blocker]** implement via a single `_bufferBase` + accessor
   methods (`RawAt(i)`, `DemodAt(i)`, etc.) rather than rewriting all ~25 absolute-index call sites
   individually -- contains the change, preserves existing anchor arithmetic verbatim. `List<T>.RemoveRange`
   is O(remaining); amortize trims (only trim once accumulated slack passes ~1s worth) to avoid O(n²).
7. **New test needed**: assert anchor EQUALITY across chunk sizes {1, 500, 4096, one-shot}, not a
   pixel-delta tolerance -- the existing tolerance-based test is exactly why the wrong AFC/Slant
   attribution survived undetected this long.

Verdict explicitly: "the underlying diagnosis is correct, the two problems genuinely do share one
root... resolve these five [now seven, folding in 6/7], and the piece is well-scoped and ready" --
no second plan-review round required, apply corrections directly (same pattern as Band-1 item 1).

**Status: revising plan per the 7 points above, then implementing in sub-pieces (chop into
independently-tested parts, per this project's established methodology): (A) VisHeader search-ceiling
helper, zero behavior change; (B) one-shot gate + the actual race fix, tested via anchor-equality
across chunk sizes; (C) `_bufferBase` abstraction, zero behavior change; (D) pre-lock trim watermark +
EndOfImage AGC force-feed, tested via a long-non-locking-stream memory-bound test; (E) full-suite
regression + update the stale AFC/Slant test comment.**

**Sub-piece A DONE (commit `365d57b`)**: `VisHeader.NormalSearchCeilingMs`/`ExtendedSearchCeilingMs`/
`NarrowSearchCeilingMs`/`MaxSearchCeilingMs` (1035/1305/950/1305ms), pinned by a dedicated test against
independently hand-derived values rather than wired directly into the two existing decoder methods'
own inline arithmetic (deliberately -- combining several separately-`MsToSamples()`-rounded terms into
one would risk a ±1-sample rounding-order behavior change, which the "zero behavior change" scope for
this sub-piece explicitly ruled out). 388/388.

**Sub-piece B DONE, the actual S3 race fix.** Added `_fixedWindowExhausted` (reset in `EndOfImage`
alongside every other pre-lock cursor/flag) and gated `TryInterleavedHeaderScan`'s scan bound: stays
pinned at `_consumedSamples` (0 new samples scanned) until `_rawSamples.Count >= _consumedSamples +
MsToSamples(VisHeader.MaxSearchCeilingMs)`, at which point it flips permanently open for the rest of
the epoch -- a one-shot gate, not the rejected rolling cap. Correctness relies on the method's own
already-established entry invariant (`_syncBypassProcessedUpTo == _visLockProcessedUpTo ==
_consumedSamples`), so any fallback match is provably >= the point the fixed-window paths are already
known to be exhausted (pure functions of accumulated `Count`, not call count -- so if either could
have succeeded, it already would have, on an earlier call, deterministically).

**Verified, not just implemented**: tightened
`DecodedImage_MatchesWithinTolerance_WhetherSamplesArriveInOneChunkOrMany` (renamed
`DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany`, now a `[Theory]` over chunk
sizes {1, 500, 4096}) from a loose 5.0-tolerance match to EXACT pixel identity -- passes at all three
sizes including the pathological `chunkSize=1`, confirming decode is now provably deterministic
regardless of chunking, not just "close enough." Corrected the stale comment that misattributed the
old failure to AFC/Slant. Full suite: 390/390. Golden-vector tests re-run specifically (real captured
legacy audio, the highest-value check): 8/8 unaffected.

**Status: S3 (Band-1 item 3) fully done. Moving to S2 (Band-1 item 2, buffer trimming) -- sub-pieces
C/D/E next.**


### Band-1 item 2 (S2) — unbounded sample-buffer memory growth, DONE

**Implementation**: single `_bufferBase` field + `Rel(int absoluteIndex)` translator (throws
`InvalidOperationException`, loudly not silently, if asked to read behind the trim watermark) --
avoided rewriting ~25 individual call sites by routing every read through `Rel()`, either directly or
via the 4 existing forward-fill accessors (`AgcSampleAt`/`FilteredRawSampleAt`/
`BandpassFilteredSampleAt`/`AgcCurMaxAt`). `TotalSamplesReceived = _bufferBase + _rawSamples.Count`
replaces every `.Count` read used as an absolute total. `TrimBuffers()` (called once per `PushSamples`)
computes a watermark two ways (locked: `min(_afcProcessedUpTo, _slantProcessedUpTo,
_visLockProcessedUpTo, _levelAgcProcessedUpTo, _bandpassFilteredProcessedUpTo, _consumedSamples) -
AnchorWarmupSamples`, deliberately excluding `_syncBypassProcessedUpTo` since it's frozen while
locked; pre-lock: a fixed trailing retention window sized to `max(VisHeader.MaxSearchCeilingMs,
SyncIntervalTracker.MaxIntervalSamples) + AnchorWarmupSamples`, further bounded by the same live
cursors), amortized behind a `MinTrimSamples` (44100) threshold before actually calling
`List<T>.RemoveRange` on all 5 buffers. `AdvanceAgcThroughDeadZone`/`_agcDeadZoneCatchUpTarget`:
defers `EndOfImage`'s AGC dead-zone force-feed (preserving the documented "AGC advances monotonically
regardless of dead-time skip" legacy-fidelity property) until the dead-zone's own samples have
actually arrived, rather than assuming they're already available synchronously inside `EndOfImage`.

**A real bug found and fixed DURING implementation, not anticipated by the plan-review**: the first
working version's pre-lock watermark included `_consumedSamples` in its `min()` unconditionally.
`_consumedSamples` is NEVER advanced pre-lock except by `Commit()`/`EndOfImage()` -- for a stream
that never locks (an idle receiver on open squelch, the EXACT scenario this fix exists for), it stays
0 forever, permanently blocking all trimming. Caught by actually running the intended test
(`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream`, 30s of real noise) rather than assuming
the implementation matched the reviewed design.

**Two designs tried for the fix, second one kept**: (1) skip `TryDecodeVisHeader`/
`TryDecodeNarrowModeHeader` entirely once `_fixedWindowExhausted` (they're pure functions of
`(headerStart, buffered data)`, provably dead for the epoch), excluding `_consumedSamples` from the
watermark only then. (2) periodically RE-ANCHOR `_consumedSamples` forward to track each trim,
re-arming `_fixedWindowExhausted` for a "fresh" shot each time. (2) was tried first and reverted: a
second real test (`DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming`,
a real Martin M1 transmission after 20s of silence) showed the re-anchor point is arbitrary relative
to any real header's actual start -- the odds of landing exactly there are negligible, so the extra
complexity (re-arming, re-closing `TryInterleavedHeaderScan`'s gate, real risk of subtly reopening the
S3 race) bought back no actual precision. Kept (1): simpler, no race risk, and the practical outcome
is identical either way -- a header arriving well after the epoch's one fixed-window opportunity is
exhausted is found via `TryInterleavedHeaderScan`'s fallback, at that path's own already-documented,
already-accepted anchor precision (29.0 tolerance, matching `SyncBypassDetectionTests`' own precedent
for the same mode/mechanism) -- a pre-existing architectural property, not a regression this fix
causes. The test asserts detection succeeds + structural correctness within that established
tolerance, not exact pixel identity.

**Final code-level auditor review (after implementation, before commit): "Ready to commit," no
blockers.** Independently re-verified every `Rel()` call site (confirmed complete, no bypasses),
re-derived both watermark branches' safety from scratch (found one additional real coupling the
implementation relies on but hadn't stated explicitly: `TryResolveAvtTraining`'s own warm-up reads
aren't directly in the pre-lock `min()`, safe only because `_fixedWindowExhausted` is provably false
for the whole AVT-pending window), confirmed the never-locking-stream fix is correct and doesn't break
AVT detection, confirmed `AdvanceAgcThroughDeadZone` preserves the monotonic-AGC property with no
out-of-order reads or lost catch-up targets across multiple images. 6 lower-severity findings, all
addressed with documentation (not code changes, since none were actual bugs): an `int`-overflow
session-length limit (~13.5h @44100Hz, flagged explicitly rather than silently accepted, widening to
`long` deliberately out of scope for this fix); the AVT-warmup coupling above; two warm-up clamps
(`TryResolveSyncAnchorCorrection`/`TryResolveAvtTraining`) that assume `_bufferBase==0`, safe only via
the watermark's own `AnchorWarmupSamples` margin, now stated explicitly rather than left implicit; a
1-3 sample extended-VIS ceiling rounding mismatch, now a one-way gate instead of a harmless per-call
retry (practically unreachable, flagged not fixed); `FilteredRawSampleAt`'s 1-sample-deeper read
relying on tail margin rather than being directly covered by the `min()`.

**Tests**: `BufferedSampleCount_StaysBounded_ForLongNeverLockingStream` (30s of real noise, asserts
buffered count stays well below what unbounded growth would produce) and
`DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming` (real Martin M1
transmission after 20s of silence that's guaranteed to trigger multiple trims first, asserts correct
mode detection + full line count + structural correctness within the established fallback tolerance).
Full suite: 392/392, solution-wide build clean. Golden-vector tests re-run: 8/8 unaffected.

**Status: S2 (Band-1 item 2) DONE, not yet committed as of this entry.**


### Band-1 item 4 (S1) — lock-dependent bandpass filter switch (H2 search vs. H1 locked), PLANNED

This port has never once run legacy's real locked-state filter (`HBPF`/`H1`) — only the weaker
pre-lock/search one (`HBPFS`/`H2`, Piece B). Investigated by reading `sstv.cpp`/`fir.cpp` directly
rather than working from the earlier speculative scope notes; findings below correct/simplify those
earlier notes materially.

**No filter-state warm-up problem, contrary to the earlier speculation.** Legacy's real convolution
engine, `CFIR2::Do(d, hp)` (`fir.cpp:1131-1144`), maintains ONE shared delay line (`m_pZ`) and simply
chooses which coefficient table (`H1` vs `H2`) to dot-product against, per call —
`m_BPF.Do(d, m_Sync||m_SyncMode>=3 ? (m_fNarrow?HBPFN:HBPF) : HBPFS)` (`sstv.cpp:1826-1832`). It is
NOT two independent filter instances. This port can mirror that exactly: one shared delay line inside
`SearchBandpassFilter`, a second coefficient array for H1, and a `bool useLocked` parameter on
`ProcessSample` — no second warmed-up filter object needed.

**H1 params (Wide preset, `CalcBPF` case 1, `sstv.cpp:1522-1531`)**: passband 1100-2600Hz (`lfq=1100`
since `m_SyncRestart` is hardwired on, `sstv.cpp:1486`; `g_dblToneOffset` confirmed 0 absent CQ100),
attenuation 20 — same as H2, same tap-count formula (`24*SampFreq/11025.0`). Attenuation 20 reconfirms
the already-resolved S28 pre-check (Kaiser/Bessel branch needs att>=21, unreached here either way).

**Scoping decision — an early-switch window is NOT being ported, deliberately. Auditor plan-review
corrected the window size: it's not a flat 30ms.** Legacy's real switch condition is
`m_Sync || m_SyncMode>=3`, not just "locked". `SyncMode==3` is the VIS **stop-bit** confirmation window
(a fixed 30ms) for a normal (single-byte) VIS code — so for most modes legacy starts using H1 about
**one VIS bit-period (30ms) before** `m_Sync` itself goes to 1 / before `Start()` fires. But
`sstv.cpp:2066-2070` sets `m_SyncMode = 9` (not straight to 3) for the extended-VIS escape byte `0x23`,
and case 9 decodes 8 MORE 30ms bits (the real extended mode code) before falling through to
`m_SyncMode = 3` (`sstv.cpp:2077`) — and case 9 is `>= 3` too. So for every extended-VIS mode (the
MR/MP/ML families) legacy actually runs H1 for **~270ms** of *decision-critical bit-decode*, not a 30ms
tail. Also unlisted originally: `Stop()` sets `m_SyncMode = 512` (`sstv.cpp:1786`), so legacy keeps H1
through the entire 0.5s post-image dead window too (cases 512/513) — this port's `EndOfImage()` reverts
to H2 immediately; harmless since the port doesn't do detection during that analytically-skipped window
anyway, but noted for completeness.

This port's natural hook point is `Commit()` (the single choke point every match path — fixed-window
VIS, narrow, AVT-post-training, sync-bypass fallback — already funnels through), which switches exactly
AT the lock anchor, i.e. LATER than legacy for that window (30ms for normal VIS, ~270ms for extended).
Traced whether this actually matters before deciding to skip it: nothing in this port reads AGC/bandpass
content from that specific window with any precision-sensitive purpose — VIS-bit decode (data + parity)
finishes before the stop-bit position starts, and picture-line decode starts at Commit's anchor, not
before it. The one thing that DOES read across that boundary, `TryResolveSyncAnchorCorrection`'s
2000-sample pre-`origin` warm-up loop, explicitly discards its output (pure resonator/smoother settling,
never accumulated into the fold-bin search) — auditor also found `TryStartAvtTraining`'s own AVT-PLL
warm-up does the same backward read; neither accumulates into a real result. A differently-filtered tail
out of a 2000-sample discarded warm-up read is not expected to matter. Chose NOT to build a
retroactive-patch-and-re-run-demodulator mechanism to close a boundary condition nothing
correctness-critical actually consumes — documented here explicitly (not silently absorbed) per this
project's established pattern for similar small timing simplifications (extended-VIS 7-bit escape byte,
sint2/sint3 freeze gating, etc.). **If a future finding ever shows something DOES read that window with
real precision sensitivity, re-open this.**

**Also explicitly out of scope, both already tracked separately, not new gaps introduced by this
item**: narrow mode's `H3`/`HBPFN` (this port's narrow modes keep using H2/search always — part of the
already-tracked MN/MC narrow-mode gap family, Band 3); AVT's *during-training* H1 usage (legacy uses H1
for the entire AVT training sequence via the same `SyncMode>=3` condition staying true throughout,
`sstv.cpp:2139-2144` — a materially bigger gap than the 30ms window above, tied to the already-tracked,
separately-deferred AVT items). AVT's eventual real image-decode phase, once its own training-lock
Commit() fires, correctly gets H1 for free via the same design (no special-casing needed there). A third
divergence found and documented once 4b actually implemented the switch (round-4 auditor code-level
review nit): legacy's `Stop()` keeps `m_SyncMode` at 512 through the whole 0.5s post-image dead zone
(`sstv.cpp:1786`, cases 512-513, `sstv.cpp:2243-2252`), so real legacy stays on H1 there too — this port
drops to H2 the instant `EndOfImage` clears `_mode`, even though that dead zone's own samples still get
demodulated (`AdvanceAgcThroughDeadZone`) and feed AGC state into the next transmission's search. Small,
matches the shape of the other two, documented in `SearchBandpassFilter`'s own doc comment.

**Chunk-invariance reasoning — WRONG, auditor found a real blocker (round 1 plan-review verdict: NOT
READY).** The original claim was that `PushSamples`'s per-sample loop always bandpass-filters every new
raw sample BEFORE `TryProcessBuffer()`/`Commit()` can run for that same push, so filter assignment per
index is deterministic regardless of chunking. **True, but the conclusion drawn from it was backwards.**
`PushSamples` (`AnalogFmSstvDecoder.cs:390-411`) runs its ENTIRE per-sample loop — which forward-fills
`BandpassFilteredSampleAt` (and `_demodulatedFrequencies`) for *every* sample in the chunk — before
`TryProcessBuffer()` (where `Commit()` actually happens) runs even once. A `_mode`-evaluated-at-first-
computation gate reduces to "was `_mode` non-null at the START of the push containing this sample" —
with two consequences, neither acceptable:
- **Bulk push (whole file/WAV in one `PushSamples` call — almost certainly how the round-trip/e2e tests
  drive the decoder)**: `_mode` is null for the ENTIRE per-sample loop, since `Commit()` can't fire until
  AFTER that loop finishes. H1 is **never used at all** — a silent no-op the existing test suite would
  not catch (it would just look like "no regression").
- **Chunked push**: H1 only engages at the NEXT chunk boundary after the lock, not at the true anchor —
  decode output becomes a function of chunk size again. **This is exactly the Band-1 item 3 race class,
  reopened** — `BandpassFilteredSampleAt` never recomputes a cached index, so the wrong filter choice is
  permanently frozen in, not just delayed.

Root cause: this port's upfront-buffer architecture processes a whole pushed chunk in one bulk pass
before header-detection/`Commit()` ever runs for that chunk's own content — a chunk can be, and in the
bulk-push case IS, the entire remaining file. Gating logic inside `BandpassFilteredSampleAt`'s existing
fill loop cannot fix this: the *placement* of the evaluation (before Commit can possibly have fired) is
what's broken, not the predicate itself (`_mode is not null && _mode.NarrowModeCode is null` correctly
encodes legacy's `(m_Sync||m_SyncMode>=3) && !m_fNarrow`, collapsed to this port's lock model — that part
survives review unmodified).

Auditor's three options, verdict pending user/next-round decision, **none implemented yet**:
- **(a) Don't do item 4.** Keep H2 continuous everywhere (today's actual behavior), document the
  divergence precisely (now including the corrected ~270ms extended-VIS window, not 30ms). Zero
  implementation risk; Band-1 item 4 stays a known, formally-accepted gap rather than fixed.
- **(b) Make the demod feed lazy.** Restructure so `_demodulatedFrequencies` (and by extension whichever
  cursor ultimately drives `BandpassFilteredSampleAt`) only computes forward as far as an ACTUAL consumer
  needs, mirroring `AgcSampleAt`'s own established lazy-forward-fill pattern, instead of PushSamples
  eagerly draining the whole chunk up front. Caveat found while reasoning through this after the
  auditor's report (not yet auditor-reviewed): `AgcSampleAt` itself is legitimately eager for
  header-detection's own sake (sync-bypass/tone-race detectors must scan continuously to ever find a
  header), and `AgcSampleAt`'s own forward-fill loop is what ultimately drives
  `_bandpassFilteredProcessedUpTo` forward — so making ONLY `_demodulatedFrequencies` lazy may not be
  sufficient on its own; needs re-verification before treating (b) as viable as stated.
- **(c) Retroactive re-filter.** Keep the current eager architecture, but at `Commit()` time, retroactively
  recompute (using already-cached `FilteredRawSampleAt` inputs — a pure function given the coefficient
  table, no demodulator-state replay needed for the bandpass stage itself) and overwrite
  `_bandpassFilteredSamples` for whatever suffix `[lineStartSample, _bandpassFilteredProcessedUpTo)` was
  already filled with H2 by the time lock happened, THEN also re-run `_demodulator.ProcessSample` over
  that same range to regenerate `_demodulatedFrequencies` (since that's a stateful streaming transform,
  not a pure function of index). Bounded to "whatever's been buffered since lock," which for a bulk push
  could mean re-processing most of the file — not free, but scoped/local rather than a full streaming-
  contract redesign.

**Status: round-1 plan-review found a real blocker (not a nitpick) — paused for a decision on (a)/(b)/(c)
before continuing. Do not implement against the original plan text above; it would produce H1-never-
engages (bulk push) or chunk-dependent decode (chunked push).**

**Real measurement (spike, per the `/adhd` skill's top-scored idea) + round-2 auditor verdict:**

Added a temporary diagnostic (`AnalogFmSstvDecoder.DiagCommitFired`, fires the provisional lock anchor
immediately from `Commit()`) and a throwaway test
(`tests/ScanlineStudio.Core.Sstv.Tests/Diag_BandpassFilterSwitchSpike.cs`) measuring, for a real Martin M1
transmission pushed at several chunk sizes, the gap between the true lock anchor and how far
`BandpassFilteredSampleAt`'s eager cache had already raced ahead by the time that push returned:

| chunk size (samples) | lock anchor (sample @44100Hz) | gap (samples) | gap (ms) |
|---|---|---|---|
| 1 | 40131 | 0 | 0.0 |
| 500 | 40131 | 369 | 8.4 |
| 4096 | 40131 | 829 | 18.8 |
| bulk (whole file, one push) | 40131 | 5,077,525 | ~115,137 (entire rest of the transmission) |

Confirms the lock anchor itself is chunk-invariant (root cause is isolated to filter-selection caching,
not upstream) and that the gap scales linearly with chunk size — small/bounded for realistic streaming
chunk sizes, catastrophic (full no-op) for bulk push.

Fed these numbers plus a `/adhd` divergent-ideation pass (5 frames, scored/clustered, top 3 deepened —
full session not reproduced here) back to the same auditor thread for a second opinion. Verdict:

- **Q1 (does the measured magnitude change the call): no.** Bulk push is a first-class supported caller
  in this codebase, not a test artifact — `TryProcessBuffer`/`ApplyAfcCorrections`/`ApplySlantTracking`
  all have existing shipped fixes specifically for bulk-push correctness. A feature that's a total no-op
  under bulk push is untested-by-construction, worse than not shipping it. But the chunk-invariant anchor
  DOES de-risk a proper fix as a bounded change, not a pipeline redesign.
- **Q2 (is "decouple only the demod feed," found by the `/adhd` deepening pass, sufficient): partially,
  and one premise in it was wrong.** Confirmed a real pre-lock reader of `_demodulatedFrequencies`
  (`AnalogFmSstvDecoder.cs:790-801`, the narrow-vs-normal-VIS discriminator) — not fatal, since that
  window sits inside the VIS header where H2 is legacy-correct anyway. But `AgcSampleAt` is NOT safe to
  "leave untouched" as assumed — it's itself a driver of the same shared bandpass cache, and
  `TryInterleavedHeaderScan` can race it all the way to `TotalSamplesReceived` once
  `_fixedWindowExhausted` fires. The residual gap this leaves is bounded by detection latency, not chunk
  size (chunk-invariant) — unmeasured, flagged as needing verification before trusting the fix, but this
  is the property that actually matters, so the design is directionally sound with a corrected
  justification.
- **Q3 (buildable now within item 4's scope): no — split it.** The lazy-demod change is its own
  behavior-preserving refactor with a clean acceptance criterion (entire existing suite stays
  bit-identical) — `Rel()`'s shared-growth assumption between `_rawSamples`/`_demodulatedFrequencies`
  (`:81`), `TrimBuffers`' `_demodulatedFrequencies.RemoveRange` needing its own watermark (`:536`), and
  `ApplyAfcCorrections`' in-place mutation ordering (`:1996`) all need to survive it. Recommended split:
  **4a** = lazy demod feed (no behavior change, ship when suite is unchanged bit-for-bit; keep the spike
  test but turn it into a permanent regression asserting the anchor-to-cache-head gap is chunk-invariant,
  not just report the raw numbers). **4b** = the actual H1/H2 switch, which becomes the originally-small
  change once 4a lands and is genuinely chunk-invariant. If 4a isn't worth its cost right now, fall back
  to **option (a)**: document the gap (~30ms normal VIS / ~270ms extended VIS) and defer both, rather than
  ship the originally-planned `_mode`-gated version, which the auditor called explicitly indefensible —
  "it would read as done while being inert for the caller shape your own test suite uses."

Off-scope note from this round (not chased): `AverageFrequencyInWindow`'s doc comment (`:2092-2094`)
still says "PLL loop's transient response," stale since the Hilbert-demodulator switch (Piece 14).

**User's call: follow the auditor, split it. 4a DONE.**

`_demodulatedFrequencies` is now its own lazy forward-fill cache (`DemodulatedFrequencyAt`, mirroring
`AgcSampleAt`/`BandpassFilteredSampleAt`'s own established pattern), no longer filled eagerly inside
`PushSamples`' per-sample loop. All three real readers updated (`PixelSampleReader`'s lambda,
`ApplyAfcCorrections`, `AverageFrequencyInWindow`'s pre-lock discriminator).

**Real bug found during implementation (not caught by plan-review), same failure class as the original
S2 bug, different cursor.** First attempt included `_demodulatedFrequenciesProcessedUpTo` as a watermark
term in `TrimBuffers`, matching `_bandpassFilteredProcessedUpTo`'s own existing pattern (matching the
auditor's stated recommendation literally). Two things went wrong depending on how: included
unconditionally → permanently pinned near 0 for a long-idle never-locking stream (its only pre-lock
reader is gated behind `!_fixedWindowExhausted`, so it stops advancing the moment that flips) —
`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream` caught this immediately. Included
conditionally (matching `_consumedSamples`' own `if (!_fixedWindowExhausted)` pattern) → let the
watermark advance PAST this cursor's actual fill position, which **crashed**:
`System.ArgumentException` from `List<T>.RemoveRange` — unlike `_consumedSamples` (a logical cursor,
safe to go stale), this cursor IS the list's own physical length, and `RemoveRange` can never remove
more elements than a list actually holds. Root-caused and fixed properly (not patched around): removed
`_demodulatedFrequenciesProcessedUpTo` from the pre-lock watermark computation entirely (kept it
unconditionally in the locked branch, where real per-line decode consumption already keeps it in step —
safe, matches `_bandpassFilteredProcessedUpTo`'s pattern there); added an explicit catch-up step right
before the `RemoveRange` block (`if (watermark > _demodulatedFrequenciesProcessedUpTo) {
DemodulatedFrequencyAt(watermark - 1); }`) that force-fills the small remaining gap before trimming —
mirrors the existing `AdvanceAgcThroughDeadZone` pattern for the same class of problem, a no-op in the
locked branch, and correct in the pre-lock branch since it only ever fills the SAME bounded range the
watermark computation already proved safe to trim for every other buffer.

**Verified**: full suite 393/393 (392 pre-existing baseline, unchanged bit-for-bit, plus one new
permanent test), golden-vector tests 12/12 unaffected. The throwaway spike (`Diag_BandpassFilterSwitchSpike.cs`)
was converted into a permanent regression test per the auditor's own recommendation
(`BandpassCacheChunkInvarianceTests.cs`) — its first version measured the WRONG quantity (raw pushed-
sample count, which is trivially the whole push size regardless of the fix) rather than the actual
bandpass-cache cursor position; fixed before converting to a permanent assertion. Real numbers, now
chunk-invariant by construction: lock anchor sample 40131 and bandpass-cache cursor 37264 (gap **-2867
samples**, i.e. the cache trails slightly BEHIND the lock point, not ahead) — identical across chunk
sizes {1, 500, 4096, bulk-whole-file}, replacing the pre-fix bulk-push gap of ~5.08 million samples.
The two permanent diagnostics (`AnalogFmSstvDecoder.LockAnchorCommitted`, `.BandpassFilteredProcessedUpTo`)
were kept (not reverted) as the test's own infrastructure, per the auditor's recommendation.

**Final code-level auditor review: EQUIVALENT-WITH-RISKS, no bug found, cleared to start 4b.** Verified
the catch-up-before-trim design directly against source: correct, and load-bearing on an invariant not
originally stated — `watermark <= _bandpassFilteredProcessedUpTo` in BOTH branches (each already
includes it in their own `Min()` chain), which is what stops `DemodulatedFrequencyAt(watermark - 1)`
inside the catch-up from ever re-advancing the bandpass cache itself (its inner
`BandpassFilteredSampleAt` calls become pure cache reads, not new fills) — i.e. what stops the catch-up
from silently reintroducing 4a's own bug from inside `TrimBuffers`. Documented that invariant explicitly
at the catch-up site per the auditor's flag, with an explicit warning not to drop
`_bandpassFilteredProcessedUpTo` from either watermark chain in 4b. Also confirmed: `EndOfImage` does
NOT reset `_demodulatedFrequenciesProcessedUpTo` (correctly — resetting it would desync it from the
list's own physical length); `Rel()` bounds and the length invariant both hold at the degenerate
all-removed edge; existing tests (`EndOfImageResetTests`, `BufferTrimTests`' silence-then-real-transmission
case) already exercise the exact multi-image/repeated-trim scenarios that would have caught a real bug
here, so "bit-identical baseline + the new chunk-invariance test" was judged genuine coverage, not luck.

**One design correction for 4b, the single most valuable thing this measurement bought**: the real
measured gap is NEGATIVE (cache cursor 37264 trails lock anchor 40131 by 2867 samples, ~65ms@44100Hz) —
meaning under the ORIGINAL point-6 design (gate H1/H2 selection on live `_mode` at first-computation
time), those 2867 PRE-anchor samples would get computed AFTER `Commit()` already fired and would be
wrongly assigned H1, when legacy actually used H2 for nearly all of that span (only the final ~30ms
stop-bit window is `m_SyncMode>=3`, already decided out of scope). Fix, same cost: gate 4b on a captured
`index >= lockAnchorSample` field (set in `Commit()`, cleared in `EndOfImage()`), not on live `_mode` —
exactly correct and still chunk-invariant.

Minor nits, both addressed or noted: laziness-vs-ordering distinction now documented at the catch-up
site (4a's real win for a never-locking stream is ordering relative to `Commit()`, not CPU savings — the
demodulator still eventually runs over ~all pre-lock audio either way); `FilteredRawSampleAt`'s
`index > 0` (absolute) vs. `index > _bufferBase` guard is pre-existing (S2, not 4a), currently
unreachable given today's margins, flagged only because 4b will touch these same accessors — watch for
it, not a blocker. Off-scope, not chased: `AverageFrequencyInWindow`'s doc comment still says "PLL
loop's transient response," stale since the Hilbert-demodulator switch (flagged twice now).

**Status: 4a done and committed. Starting 4b (the actual H1/H2 filter switch) — corrected design: gate
on a captured lock-anchor sample index, not live `_mode`.**


### Band-1 item 4b — the actual H1/H2 filter switch, DONE

`SearchBandpassFilter` now carries both coefficient tables (`_h1` 1100-2600Hz, `_h2` 400-2500Hz, both via
the existing `MakeFilter` helper, same tap count) over ONE shared delay line — `ProcessSample(double
input, bool useLocked)` shifts the line unconditionally, dot-products against whichever table
`useLocked` selects, mirroring `CFIR2::Do(d, hp)`'s own single-delay-line-plus-coefficient-choice shape
exactly. No separate H1 warm-up needed, verified by a new unit test
(`ProcessSample_SwitchingToLocked_ReusesExistingDelayLineHistory_NoSeparateWarmUp`) that feeds several
H2-selected samples then switches to H1 for one sample and checks the result against H1's coefficients
convolved against that SAME accumulated history, computed independently by hand from the raw inputs.

`AnalogFmSstvDecoder` gates `BandpassFilteredSampleAt`'s selection on a NEW field,
`_bandpassLockedFromSample` (captured in `Commit()`, reset to `int.MaxValue` in `EndOfImage()`) — NOT
live `_mode` state, per the corrected design from item 4a's own auditor review: `useLocked = _mode is
not null && _mode.NarrowModeCode is null && index >= _bandpassLockedFromSample`, evaluated once, at each
index's own first-computation time. Narrow mode's H3/HBPFN stays out of scope (already-tracked MN/MC
gap family) via the `NarrowModeCode is null` check; the legacy ~30ms(normal)/~270ms(extended) early-
switch window stays out of scope too (already documented in item 4's own entry above).

**Tests**: `SearchBandpassFilterTests.cs` — added an independently-computed (Python, not derived from
the C# implementation, same discipline as the existing H2 fixtures) H1 coefficient fixture (full array
at tap=24@11025Hz, selected values at tap=96@44100Hz), an H1 frequency-response sweep (independently
computed magnitudes at H1's own passband edges 1100/2600Hz plus the same real VIS-bit/sync/leader tones
1200/1900Hz), parametrized the existing causal-impulse-response and zero-padding tests over both H1/H2,
and the shared-delay-line/no-warm-up proof described above. 21 new tests, 42/42 in this file.

**Verified**: full suite 414/414 (413 baseline + 21 new filter tests unchanged, no regressions anywhere),
golden-vector tests unaffected, noise-robustness tests unaffected (both within existing established
tolerance), and — the property this whole item existed to fix — the existing
`DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany` round-trip test (chunk sizes
{1, 500, 4096}) still passes at EXACT pixel identity with H1/H2 switching now live, confirming item 4b
did not reopen the Band-1 item 3 chunk-timing race.

**Final code-level auditor review (round 4): EQUIVALENT, ready to commit.** Verified the gating
condition against `sstv.cpp:1826-1832` directly (correctly collapses `(m_Sync||m_SyncMode>=3) &&
!m_fNarrow`), confirmed no sub-`_bufferBase` evaluation risk, confirmed `Commit()`-within-the-same-push
ordering is handled (the round-3 correction's whole point), confirmed mid-reception restart
(`TryVisLockStateMachine`) and AVT post-training lock both correctly get H1, confirmed `EndOfImage`'s
`_bandpassLockedFromSample` reset is genuinely redundant-but-correct (the `_mode is not null` conjunct
already closes that gate) rather than overclaimed, and confirmed nothing from item 4a's own review
reopened (`_bandpassFilteredProcessedUpTo` still in both `TrimBuffers` watermark chains, the catch-up
still can't advance the bandpass cursor). One recommended (non-blocking) gap: the round-3 correction's
own most subtle property — samples strictly before the anchor computed after `Commit()` fires must stay
H2 — was protected only by a doc comment, not a test. Closed before committing: added
`AnalogFmSstvDecoder.FirstLockedBandpassIndex` (diagnostic-only, mirrors `LockAnchorCommitted`'s own
pattern) and `BandpassCacheChunkInvarianceTests.FirstLockedFilterSample_EqualsTheLockAnchor`, which pins
the two equal directly — would fail with a clear, specific mismatch if this ever regressed back to
gating on live `_mode` alone. Two nits also addressed: the third legacy divergence (0.5s post-image dead
zone, `sstv.cpp:1786`/`2243-2252` — legacy stays on H1 there, this port drops to H2 at `EndOfImage`) now
explicitly documented in `SearchBandpassFilter`'s own doc comment and here (see "Also explicitly out of
scope" above); the `BandpassFilteredSampleAt` comment/code phrasing mismatch (loop's "this index" vs the
method's own `index` parameter) fixed with a named local.

**Verified (final)**: full suite 415/415 (414 baseline + 1 new gate-pinning test, no regressions
anywhere), golden-vector and noise-robustness tests unaffected, chunk-invariance confirmed both
end-to-end (`DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany`) and directly at the
gate itself (the new pinning test).

**Status: Band-1 item 4 (S1) DONE, committed. With it, all 4 Band-1 (must-fix-before-Phase-2) items are
complete: S4 (`288d5d0`), S2 (`86e3af6`), S3 (`365d57b`/`765ba3c`), S1 4a+4b (this entry). Next:
task #7 (capture new golden-vector fixtures) → task #8 (Phase 3 chain/integration audit).**


## Band 2 — scoping, "Pattern 1" recommendation revisited and withdrawn

Before starting Band 2 (S5, S16, S15, S14, S6 — "should fix during Phase 2 bring-up"), asked the
auditor whether its own original Pattern-1 recommendation ("decide the streaming contract explicitly
before writing individual fixes") still holds now that Band 1 has real outcomes to check it against.

**Verdict: withdrawn. Start S5 directly, no design pass.** The recommendation was written before this
port had a proven streaming pattern; it now has both halves it asked for, already in-tree and
review-hardened: persistent-detector-plus-monotonic-cursor (`_syncBypass1200Detector`/`_visLockStateMachine`
as instance fields, driven by `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`) and lazy forward-fill
sample caching with a bounded ring buffer (`AgcSampleAt`/`BandpassFilteredSampleAt`/`DemodulatedFrequencyAt`
+ `TrimBuffers`). A design pass now would document code that already exists, not decide anything new.
Empirical case: all 3 Pattern-1 Band-1 items (S1, S2, S3) landed as scoped incremental fixes and are
stable — S1, the worst-shaped one, cost one session and added permanent regression infrastructure, not
throwaway patches. Honest counterweight kept: a unified pass would catch cross-item invariants (like
4a/4b's `watermark <= _bandpassFilteredProcessedUpTo` coupling) by construction instead of via review —
real, but small, and already mitigated by documenting invariants at the site plus a per-item review gate
that's caught one genuine issue each of the last three rounds.

**Important correction to the original audit's own framing**: S5 is NOT the same shape as 4a. 4a was an
*eagerness/ordering* defect (chunk-dependence). S5 is a *cold-start fidelity* defect — `TryDecodeVisDataBits`
already builds fresh detectors as a "pure function of (headerStart, buffered data)" (already chunk-
invariant, confirmed in-code), just missing legacy's continuously-running filter history
(`m_iir11/12/13/19`+`m_lpf*`). Different failure mode, same destination pattern (the persistent-instance
shape at `AnalogFmSstvDecoder.cs:327-333`, not 4a's lazy-cache shape).

**Shape classification for all 5 Band-2 items** (which existing pattern each should reuse):

| Item | Shape | Reuses |
|---|---|---|
| S5 | Cold-start detector rebuild (`TryDecodeVisDataBits`, `:1754-1757`) vs legacy's continuously-running detectors | The persistent-instance pattern (`:327-333`), NOT 4a's |
| S16 | Same as S5 — fresh `PllFmDemodulator` per call (`:1960`) + a clamped 2000-sample warm-up hack | Same conversion as S5 |
| S14 | Half S5 (fresh mark/space detectors, `:1626-1627`), half anchor-precision — split it | Detector half rides with S5 |
| S6 | 4b's shape, not 4a's — a lock-dependent parameter switch on a continuously-running filter | `_bandpassLockedFromSample` directly |
| S15 | ~~Materially different... a semantics change~~ **Correction (see S15's own closing section below): "nothing existing to reuse" was right, but "genuinely new design work" was wrong — it's *previously-attempted-and-reverted* design work (the S2-era re-anchoring draft), a different and more useful status. Closed via documentation, no code needed.** | Nothing existing (the obvious implementation was already tried and reverted during Band-1 S2) |

**Two traps flagged for when Band 2 actually starts** (not yet acted on):
- **S6**: `HilbertFmDemodulator.SetWidth` changes tap count AND phase-diff lag (`HilbertFmDemodulator.cs:46`)
  — unlike H1/H2's constant-tap coefficient swap, legacy explicitly compensates a tap change
  (`SetBPF`'s `m_Skip = (newtap-oldtap)/2`, `sstv.cpp:1602-1613`). 4b's "one shared delay line, no
  warm-up, no delay compensation needed" finding does NOT transfer to S6 — read `CSSTVDEM::SetWidth`
  (`sstv.cpp:1707-1715`) and `Start()` fresh before reusing any 4b reasoning. Exactly the "don't infer
  from a similarly-shaped case" trap CLAUDE.md §3 warns about generally.
- **S15 is coupled to Band 1 in a way the original audit predates**: `_fixedWindowExhausted` is
  load-bearing for `TrimBuffers`' pre-lock watermark (`:523-526` — what makes trimming past a stale
  `_consumedSamples` safe). Changing when the search window closes changes trimming safety. Do S15
  LAST, and re-verify the pre-lock watermark as part of it, not as an afterthought.

**Recommended order: S5 → S16 → S14 → S6 → S15.** S5/S16 are the same mechanical conversion (do them
back to back while the pattern's loaded); S14's detector half rides along; S6 needs its own fresh
`SetWidth` legacy read before anything is written; S15 goes last since it perturbs Band-1's own trimming
invariant.

**Status: Band 2 scoped, no design pass needed. Starting S5.**


### Band-2 item S5 — persistent VIS-bit-decode detectors, implemented

`TryDecodeVisDataBits` used to construct 4 fresh `SyncEnvelopeDetector`s (d11/d12/d13/d19) on every
call, cold-started at `headerStart` on every retry. Verified against legacy directly (auditor
plan-review): `CIIRTANK` (`m_iir11/12/19`) has no `Clear()` method at all and `SetFreq` only ever
writes coefficients, never the resonator's internal state (`z1`/`z2`) — these detectors run with
continuous, never-reset state for the CSSTVDEM object's whole life, even across AFC-triggered retunes.
`m_iir12`/`m_iir19` (d12/d19) are fed unconditionally every sample; `m_iir11` (d11) effectively every
sample too (`m_SyncRestart` hardwired on).

**Real design correction found by plan-review before any code was written**: `m_iir13` (d13) is
NOT the same shape — legacy only feeds it during case 2/9 (`sstv.cpp:1976`), so its value at a given
sample depends on trigger history, not just that sample's own index. An index-keyed forward-fill cache
is structurally wrong for it. **d13 stays method-local, exactly as before** — its cold start costs
nothing measurable anyway (first read is 30ms after it starts being fed; an 80Hz-bandwidth resonator
settles in ~4ms). d11/d12/d19 converted to persistent instance fields behind lazy forward-fill caches
(`D11At`/`D12At`/`D19At`), mirroring `AgcSampleAt`'s own established pattern exactly.

Deliberately NOT unified with the existing `_syncBypass1200Detector`/`_syncBypass1900Detector` (which
already faithfully port legacy's literal SHARING of one d12/d19 pair for the continuous
`TryInterleavedHeaderScan` fallback path) — `_syncBypassProcessedUpTo` being caught up to whatever
`TryDecodeVisDataBits` needs at call time is unverified (the fixed-window path is tried first). Not a
permanent scope cut: converting d12/d19 to index-keyed caches here is exactly the prerequisite that
makes that future unification mechanical instead of a redesign, when/if it's ever done (same deferred-
unification family as `_syncBypass1PrimaryHeld`'s own doc comment already describes).

**`TrimBuffers` trap, correctly anticipated this time (not rediscovered the hard way like item 4a)**:
these 3 new cursors are read ONLY pre-lock, by `TryDecodeVisDataBits` alone — so they're excluded from
BOTH branches' watermark `Min()` chain (not just pre-lock like `_demodulatedFrequenciesProcessedUpTo`,
since post-lock they'd otherwise freeze the watermark at whatever value they held at the moment of
lock, blocking trimming for the entire image). Safety guaranteed purely by an extended catch-up-before-
trim step, same load-bearing invariant as item 4a's own catch-up (`watermark <= _levelAgcProcessedUpTo`
in both branches, so the catch-up's `AgcSampleAt` calls are always pure cache reads).

**Real test gap closed before committing (auditor plan-review flagged it)**: `BufferedSampleCount` only
tracks `_rawSamples`, so these 3 new `List<double>`s silently failing to trim (same bug class Band-1
item 2/4a each hit once already, different cursors) would have passed
`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream` without any warning. Added
`VisDataDetectorBufferedSampleCount` (combined physical length of the 3 new caches) and asserted it in
both existing `BufferTrimTests` tests, not a new test file.

**Verified**: full suite 415/415, no regressions, no new test count (extended existing assertions,
didn't add new test methods). Golden-vector and noise-robustness tests unaffected.

**Final code-level auditor review: EQUIVALENT, ready to commit.** Confirmed d13 genuinely untouched
(same construction/feed/read-timing as before); confirmed the exclude-from-both-branches `TrimBuffers`
reasoning is correct (traced the actual call chain: `TryDecodeVisDataBits` is only ever reachable while
`_mode is null`); confirmed the catch-up safety invariant holds with 3 more consumers (all pure reads
off `AgcSampleAt`, fully order-independent, and trim-timing itself provably can't affect decode output
since the caches are index-keyed with a strictly monotonic feed); confirmed nothing from 4a/4b reopened.
One stale comment found and fixed (the `sample++`-after-reject comment cited "these are stateful
streaming filters, not a cache" as its reason — no longer true for d11/d12/d19, restated against the
real legacy citation, `sstv.cpp:1983`, instead). One useful non-blocking note: the locked-branch
catch-up now runs all three detectors over every sample of every image (previously idle there) — not a
bug, actually a fidelity GAIN (legacy's real `m_iir11/12/19` run every sample too, so the next
transmission's header decode now sees real carried-over state exactly like legacy, not just a
memory-bounded cache) — worth knowing so the added per-sample cost isn't a surprise later. One
recommended (non-blocking) test gap: nothing directly proves the persistence behavior itself (a
regression back to cold-start-per-call would still pass all 415 tests) — deferred, since the 3 new
fields are `private readonly` (structurally can't be silently reassigned to a fresh instance without an
obviously-visible code change), and the auditor itself called this "recommended, not a commit blocker."
If picked up later, pairs naturally with S16 (same detector-persistence shape).

**Status: S5 DONE, committed.**


### Band-2 item S16 — AVT PLL warm-up, corrected scope and implemented

**Plan-review round 1 caught its own earlier misclassification before any code was written**: S16 is
NOT the same shape as S5. Legacy's real `m_pll` (AVT's dedicated PLL) is fed only during `SyncMode`
cases 3-7 (`sstv.cpp:2129/2159/2169/2187/2222`) — intermittent, not a pure function of absolute sample
index, the SAME shape as d13's own case-2/9-only feed that already ruled out an index-keyed cache for
IT in S5. A PLL also has no equivalent of a resonator's fast, data-independent re-settling (its phase
state carries indefinitely) — making "just leave it running forever" (the original plan) an actual
regression, not a neutral simplification: it would make AVT training entry a function of the ENTIRE
preceding stream, which legacy's real per-attempt `m_pll` usage never is.

**The real defect, and the actual fix**: `_avtPllDemodulator` stays fresh-per-training-attempt exactly
as before (no persistence, no new cache, no new cursor, no `TrimBuffers` changes) — but its old
clamped-2000-sample warm-up was far too short relative to legacy's real contiguous feed window. This
port's own `_avtTrainingOriginSample` deliberately skips past all 3 VIS repeats before ever
constructing a training-lock instance at all — but legacy spends that entire skipped span (cases 4-7,
~1820ms) continuously feeding `m_pll` real failed-marker-search audio, plus case 3's own 30ms VIS-
stop-bit window right before that (`sstv.cpp:2127-2129`, verified directly, not assumed — the
plan-review flagged this specific 30ms as a judgment call worth checking rather than guessing).
Widened the warm-up from the arbitrary `AnchorWarmupSamples` (2000) constant to a legacy-derived start
point (`_avtPllWarmupStartSample = headerStart + totalHeaderSampleCount - one VIS bit period`), computed
once in `TryStartAvtTraining` and consumed by `TryResolveAvtTraining`'s existing warm-up loop (now
looping from that point instead of a clamped window). Updated the `TrimBuffers` comment that used to
cite the old `AnchorWarmupSamples`-based coverage to cite the new, correct span instead.

**Verified**: full suite 415/415, no regressions (including AVT's own round-trip tests).

**Final code-level auditor review: EQUIVALENT, ready to commit.** Confirmed the case-3 boundary
(`sstv.cpp:2127-2130`: `if(!m_Sync){ m_pll.Do(ad); }`, unconditional through the whole 30ms countdown)
and the 30ms figure itself (`m_SyncTime = 30*SampFreq/1000`, `sstv.cpp:1986`) match exactly. Flagged one
derivation detail to settle, not guess: whether `totalHeaderSampleCount` includes the VIS stop bit
(if not, the warm-up start would be 30ms early). **Settled directly from `VisHeader.cs`**:
`NormalTailDurationMs = BitDurationMs (parity) + BitDurationMs (stop)`, and `totalHeaderSampleCount` is
computed from `PrefixDurationMs + NormalTailDurationMs` — the stop bit IS included, so the derivation is
exactly right, not off by one bit period. Confirmed numerical safety at the new, much longer warm-up
length (~80k samples through `PllFmDemodulator` before first real read): AGC resets every zero-crossing
(no accumulation), loop drive is hard-clamped, VCO phase accumulates but with `double`-precision error
around 1e-12 rad even at this length — no new failure mode versus the old 2000-sample window, just
smaller in degree. Confirmed the `TrimBuffers` transitive-safety margin actually GREW (from ~45ms to
~580ms) rather than shrank.

**Systemic finding, addressed before committing (not per-item deferred debt)**: the auditor noted this
is the THIRD consecutive item (4b, S5, S16) whose real behavior change was structurally invisible to
the existing suite — decode outcomes stayed correctly identical in all three, but nothing pinned the
underlying MECHANISM, so each would have silently passed if reverted to its old (wrong) behavior. 4b's
own gate is already covered (`BandpassCacheChunkInvarianceTests.FirstLockedFilterSample_EqualsTheLockAnchor`,
added during that item's own final review — the auditor's context was stale on this one, flagged as
still-missing when it wasn't). Closed the other two in one sitting rather than deferring further:
new `tests/ScanlineStudio.Core.Sstv.Tests/LegacyDerivedSpansTests.cs` — `AvtPllWarmupSpan_MatchesLegacyDerivedDuration`
(pins S16's warm-up span as a pure, headerStart-independent constant derived the same way the source
computes it) and `VisDataD11Cursor_NeverResets_AcrossBackToBackTransmissions` (pins S5's persistence
across an image boundary, the property a revert-to-cold-start would actually violate). Two small new
diagnostics added to support them (`AvtPllWarmupStartSample`/`AvtTrainingOriginSample`,
`VisDataD11ProcessedUpTo`), same pattern as every other diagnostic already in this file.

**Verified (final)**: full suite 417/417 (415 baseline + 2 new tests), no regressions.

**Status: S16 DONE, committed.**


### Band-2 item S14 — DONE (commit `7a153a0`)

Implemented per the verified plan below, no changes to the plan itself needed. `FskSpaceAt` added
mirroring `D11At`/`D12At`/`D19At` exactly; `D19At` reuse for mark confirmed correct by two rounds of
auditor code-level review this session (one against current source, one against the actual diff — both
EQUIVALENT). Key legacy confirmation from the code-level review: `InitTone` (`sstv.cpp:1695-1705`)
retunes `m_iir19` and `m_iirfsk` together, inside the same `if( m_AFCFQ != dfq )` block with the same
`dfq` — mark and space always move together in legacy, so sharing `D19At` with the VIS path introduces
no relative divergence for narrow mode specifically (neither retune is modeled by this port at all yet —
pre-existing, S6-adjacent, unchanged by this item).

Non-blocking gap the first review round found (closed before commit, not deferred): the space-cursor
persistence test alone didn't pin the *D19-reuse decision* itself — reverting mark to its own fresh
detector would still pass it. Added `VisDataD19ProcessedUpTo` diagnostic + a narrow-mode-only decode test
(`NarrowHeaderDecode_AdvancesTheSharedD19Cursor`) asserting it advances past 0 — MN/MC never runs
`TryDecodeVisDataBits` at all, so this can only be explained by the narrow-header path itself reading
`D19At`. 419/419 passing (417 before this item, +2 new tests:
`FskSpaceCursor_NeverResets_AcrossBackToBackNarrowTransmissions` and the D19-reuse test above).

<details>
<summary>Original plan-review writeup (pre-implementation)</summary>

#### Band-2 item S14 — plan verified, NOT YET IMPLEMENTED (session paused at 92% budget)

`TryDecodeNarrowModeHeader`'s own `markDetector`/`spaceDetector` (1900Hz/2100Hz `SyncEnvelopeDetector`s,
~line 1774) are constructed fresh every call — same cold-start shape as S5's d11/d12/d19. Legacy's real
equivalents: `m` is literally `d19` (`m_iir19`+`m_lpf19`, unconditional every sample, `sstv.cpp:1851-1853`
— the SAME detector S5 already made persistent as `D19At`), `s` is `dsp` from `m_iirfsk`+`m_lpffsk`
(2100Hz `FSKSPACE`, also unconditional every sample, `sstv.cpp:1855-1857`, not yet covered anywhere).

**Plan-review verdict, confirmed against source, ready to implement:**
1. **Mark detector: REUSE `D19At`, don't add a third 1900Hz instance.** Initial instinct was to keep
   narrow-mode's own separate persistent 1900Hz detector (matching S5's own precedent of NOT reusing
   `_syncBypass1200Detector`/`_syncBypass1900Detector`) — auditor correction: that precedent doesn't
   apply here. S5's non-unification was specifically about `_syncBypass1900Detector`, which is driven by
   a *separate scan cursor* whose caught-up-ness at call time is unverifiable. `D19At` is a plain
   index-keyed cache (ask for index i, it forward-fills and returns) — no cursor-lag question exists.
   Legacy's mark literally *is* d19, same object, same value — reusing `D19At` is MORE faithful, cheaper,
   and stops 3x duplication (`_syncBypass1900Detector`, `_visDataD19Detector`, a hypothetical third)
   becoming 4x. **Required follow-through**: `D19At`'s existing `TrimBuffers` exclusion comment
   (currently justified by "read ONLY by `TryDecodeVisDataBits`") becomes false once
   `TryDecodeNarrowModeHeader` also reads it — update to name both readers (exclusion logic itself stays
   correct, both are pre-lock-only readers).
2. **Space detector: genuinely new.** New persistent field + `FskSpaceAt(int index)` lazy forward-fill
   cache + cursor, exactly mirroring `D11At`/`D12At`/`D19At`'s own shape — plus its own `TrimBuffers`
   catch-up-before-trim call, `RemoveRange`, and a diagnostic-count entry (matching S5's own
   `VisDataDetectorBufferedSampleCount` pattern — fold the new list into that combined count, or add a
   sibling; decide at implementation time).
3. **`NarrowFskHeaderDecoder`'s own bit-accumulation state machine stays fresh-per-call, NOT converted.**
   Real finding: legacy's `DecodeFSK`/`m_fskmode` (`sstv.cpp:2378-2606`) is ALSO called unconditionally
   every sample with no `headerStart` concept at all — a genuinely bigger architectural gap than d13/S16
   ever were. Confirmed correctly OUT of scope for S14 specifically: this port's bounded-fixed-window-
   plus-`_syncBypassNarrowTracker`-fallback architecture is the same, already-accepted shape as the VIS
   path's own `TryDecodeVisHeader`/`TryInterleavedHeaderScan` split — `NarrowFskHeaderDecoder`'s bit
   accumulation is the direct analogue of `TryDecodeVisDataBits`'s own trigger-search logic, which S5
   correctly left fresh-per-call too. Safe specifically BECAUSE once both tone detectors are index-keyed
   caches, the state machine becomes a pure function of `(headerStart, cached values)` — same reasoning
   that already applies to S5's own untouched search loop.
4. **Anchor-precision half: ALREADY CLOSED, no work needed.** The original S14 audit row's own concern
   ("supporting evidence is [only] a round-trip test... drift/jitter unmeasured") is stale — an existing
   doc comment on this method (predating this session's Band-2 audit) already independently verified the
   fixed-nominal-duration commit point against `Main.cpp:7422-7424`: TX writes the mode byte then the
   checksum byte, and the guard-tone write immediately after is commented out in legacy's own TX code —
   image data follows at a fixed offset determined entirely by TX, not by wherever RX's state machine
   happens to lock. `headerStart + VisHeader.NarrowHeaderTotalDurationMs` is confirmed correct, TX-source-
   verified rather than round-trip-self-consistent. Nothing to change here.

**Sizing (auditor's own assessment): small — smaller than S5.** One new detector+cache+cursor (space),
one catch-up, one `RemoveRange`, one diagnostic extension, two comment updates (the `TrimBuffers`
exclusion citation above, plus swapping two local constructions for cache reads in
`TryDecodeNarrowModeHeader` itself). Comfortably a single session once resumed.

</details>

**Status: DONE (commit `7a153a0`).** Implemented per the plan above, no changes needed. 419/419 tests
passing before this item (417 base + 2 new S14 tests).


### Band-2 item S6 — DONE (commit `6da0a65`)

`HilbertFmDemodulator.ProcessSample` gained an `isNarrow` parameter selecting between two precomputed
`(off, out)` tuning pairs — normal (1900Hz/800Hz) and narrow (`NARROW_CENTER`=2172Hz, `NARROW_BW`=256Hz,
`sstv.h:441-444`) — mirroring item 4b's per-call `useLocked` selection shape rather than a stateful
mutator. `AnalogFmSstvDecoder.DemodulatedFrequencyAt` reuses item 4b's own `_bandpassLockedFromSample`
anchor to gate the selection (`_mode.NarrowModeCode is not null && index >= _bandpassLockedFromSample`)
— both fire off the same `Commit()` event, and an auditor plan-review round confirmed the same
negative-gap property item 4a discovered for the bandpass cache recurs here (this cursor also trails
the anchor at `Commit()` time, for a different reason — its only pre-lock driver,
`AverageFrequencyInWindow`, ends at `headerStart+380ms`, well before a narrow anchor at
`headerStart+NarrowHeaderTotalDurationMs` — but the same direction, so the same `index >= anchor` form
absorbs it).

**Roadmap correction, found via a fresh legacy re-read before writing any code**: the original S6 trap
note (below, in the collapsed plan-review section) claimed `CHILL::SetWidth` changes tap count and
cited `CSSTVDEM::SetBPF`'s `m_Skip = (newtap-oldtap)/2` compensation (`sstv.cpp:1602-1613`) as evidence
this couldn't reuse 4b's "no warm-up needed" finding. Verified wrong: `SetBPF` is a different, unrelated
feature entirely — the user-configurable Wide/Narrow/VeryNarrow bandpass QUALITY setting (`m_bpf`
1/2/3), operating on `m_BPF` (`SearchBandpassFilter`, item 4b's own class), not on `m_hill`/CHILL at
all. `CHILL::SetWidth` itself (`sstv.cpp:3022-3051`) only changes two scalars (`m_OFF`/`m_OUT`); tap
count and `m_df` tier on sample rate ONLY, never on `fNarrow`. 4b's finding — a live scalar/coefficient
switch on continuously-running state needs no warm-up — DOES transfer here, confirmed by an auditor
plan-review round working from source independently. Also ruled out during the same fresh read: `m_fqc`
(this port's `ZeroCrossingFrequencyCounter`, which already has a working but never-called `SetWidth`) is
dead code in the live path — AFC now reads directly from `HilbertFmDemodulator`'s own output, post the
Hilbert-demod architecture switch — so no separate CFQC wiring was needed; `HBPFN` (locked-narrow
bandpass) is a separate, already-logged, deliberately-out-of-scope gap (`SearchBandpassFilter.cs`'s own
doc comment), not S6's concern.

**The real finding, confirmed algebraically by two independent derivations (this session and an
auditor code-level review) and worth recording plainly**: at steady state, the `off`/`out` encode and
`ProcessSample`'s final `centerHz - scaled*bandwidthHz/32768` descale are exact algebraic inverses for
ANY consistent `(centerHz, bandwidthHz)` pair — both cancel completely, dynamically too (the smoothing
filter is linear). So `isNarrow` has **no effect on the settled Hz readout** in this port's
representation — unlike legacy, where `m_OFF`/`m_OUT` genuinely matter, because `CHILL::Do` returns the
raw SCALED value directly (`sstv.cpp:3086`) and never converts to Hz at all; this port's Hz conversion
is its own representational choice, and that choice is exactly what makes the selection cancel here.
Sanity-checked against the ALREADY-EXISTING pre-fix test at 2300Hz, which was passing before any S6
code existed. The ONLY observable effect of this item is a brief, bounded output-IIR transient right at
a mid-stream width switch (the smoothing filter's stored state is in the OLD scale for one switch) —
faithfully reproducing a transient legacy has too and does nothing to compensate (no `Clear()`/reset
anywhere in `SetWidth` or its callers). **This is a legacy-fidelity port, not a decode-accuracy fix** —
the original inventory row's "unmeasured, now the live picture-demod path" framing turned out to be a
non-issue for this port's own representation, valuable to know rather than to have assumed.

An earlier draft's regression test (`isNarrow=true` reads back 2044/2172/2300Hz correctly) was
tautological — it would have passed with `isNarrow` silently ignored, for the exact reason above.
Replaced per the auditor's own suggestion with two tests that pin real, executable facts:
`ProcessSample_IsNarrowSelection_IsRepresentationallyInert_AtSteadyState` (wide vs. narrow settle to
the identical value) and `ProcessSample_IsNarrowFlipMidStream_CausesBoundedTransient_ThenResettlesToSameValue`
(a real deviation happens right at the flip, bounded/fast, resettles to the same value). 423/423 tests
passing (419 before this item).


### Band-2 item S15 — CLOSED via documentation, no code needed. Band 2 fully done.

Before drafting a plan, re-read `AnalogFmSstvDecoder.cs`'s own doc comments from Band-1 item 2 (S2,
pre-Band-2) and found this item's core concern had already been investigated once: `TryDecodeHeader`'s
own doc comment describes a "more ambitious earlier draft" that tried periodically re-anchoring
`_consumedSamples` forward to give the fixed-window paths (`TryDecodeVisHeader`/`TryDecodeNarrowModeHeader`)
another shot after each trim, instead of the current one-shot `_fixedWindowExhausted` gate — reverted,
confirmed empirically that it "produces the identical practical outcome" either way, since any header
found after exhaustion is found via `TryInterleavedHeaderScan`'s fallback (which is ALREADY continuous,
never one-shot) at that path's own already-accepted anchor precision. Sent this finding to an auditor
plan-review to settle whether that narrower (trimming-safety-only) result actually closes S15's broader
concern (legacy's real per-sample-forever `m_SyncMode` search vs. this port's one-shot-then-fallback
architecture) or whether a real gap remains.

**Auditor verdict: close S15, no code tonight.** Three points settle the main concern:
1. **The fallback has the same mode-identification power for normal + extended VIS.** `VisLockStateMachine`
   is a full VIS decoder including the extended path (`LockState.DecodeExtendedVis`,
   `EscapeVisByte = 0x23`) — MR/MP/ML stay covered post-exhaustion, not orphaned.
2. **Initial anchor precision is largely washed out downstream.** `TryResolveSyncAnchorCorrection` runs
   after every non-AVT `Commit()`, re-deriving the anchor from a multi-line sync-envelope fold
   regardless of which path committed. The S3 measurement put the two paths ~440 samples (~10ms) apart
   for the affected modes — comfortably inside a line period, so the fold recovers it. This is the
   *mechanism* behind the S2-era empirical result, not just a restatement of it.
3. **Already observed working end-to-end** via `BufferTrimTests.DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming`.

**One real, narrower gap the S2 investigation didn't cover — NOT a new item, it's already tracked as
S8** ("mid-image narrow re-lock", line 136/1137 above: "mid-image narrow-mode FSK-announce re-lock
(`sstv.cpp:2592`, needs a sample-by-sample FSK decoder this port doesn't have — `TryDecodeNarrowModeHeader`
is a fixed-window analytic shortcut with no real-time legacy counterpart)"). Legacy's `DecodeFSK` runs
every sample, forever (`sstv.cpp:1858`, confirmed during S14); this port's only FSK packet decoder lives
inside the fixed-window `TryDecodeNarrowModeHeader` — the same missing capability whether framed as
"initial detection post-exhaustion" (what this S15 investigation found) or "mid-image re-lock" (S8's
original framing). Re-verified S8's own load-bearing assumption while here, since an auditor flagged it
unverified (`SstvModeRegistry.cs:998-999`/`1023-1026`): `GetSyncIntervalCandidates` returns ALL non-AVT
modes including the whole MN/MC family, and `GetSyncIntervalMatchDepth` has an explicit MN73/110/140/
MC110/140/180 row (`isNarrow ? 8-5 : null`) — so `_syncBypassNarrowTracker` genuinely covers MN/MC, and
since its candidate list carries each mode's own distinctive line-duration interval, it can plausibly
identify the SPECIFIC MN/MC submode by timing alone, not just detect "some narrow signal" generically —
a different (not equivalent, but real) mechanism than legacy's FSK-payload-based identification, likely
narrowing S8's gap further than "no coverage at all." Still gated on the MN/MC golden-vector fixture
task #7 already plans to capture, per the roadmap's own "fix when measured, not reasoned" rule for this
item family — not rescheduled here, stays Band 3.

**Why building S15 as originally scoped would be the wrong call for an unattended session, even though
closing it isn't**: its own obvious implementation IS the already-tried-and-reverted re-anchoring draft
— a continuously-retriggering high-precision path structurally needs a rolling anchor, and a bounded
pre-lock buffer (Band-1 S2's own concern) needs one too. **These are the same underlying problem, not
two coincidentally-similar ones** — confirmed by the fact that the one existing attempt at solving
either one attempted to solve both at once, and was reverted because an arbitrary re-anchor point almost
never lands on a real header start — a property of the problem itself, not of that one attempt. Building
this unattended (nothing existing to reuse, unmeasured benefit, touches the load-bearing
`_fixedWindowExhausted`/`TrimBuffers` coupling) would mean relitigating an already-evidenced decision,
not executing a verified plan — exactly what this session's autonomous-continuation authorization
excludes.

**Worth recording for whoever revisits this**: exhaustion is the NORMAL case, not an edge case — pre-lock,
`_consumedSamples` never advances, so `_fixedWindowExhausted` fires ~`MaxSearchCeilingMs` (~1s) into
every epoch regardless of real signal content. In Phase 2's real target deployment (live capture, not a
test fixture starting at sample 0), any transmission not beginning within ~1s of decoder start or ~1s
after the previous `EndOfImage` is found ONLY by the fallback — the fixed-window path is close to
vestigial there, not "the main path with an occasional fallback." Doesn't change the verdict above, but
is the single most useful sentence to leave here.

**Band 2 is now fully done**: S5, S16, S14, S6 shipped as code; S15 closed via this documentation entry
(no code needed — its one real remaining gap turned out to be the already-tracked Band-3 item S8, whose
own load-bearing assumption got independently re-verified along the way). 423/423 tests unchanged (no
code this item).


## Task #7 — six new golden-vector fixtures captured (scottie-s1, robot72, pd90, rm8, mn110, avt)

Source images generated (gradient formula matching the existing `martin-m1`/`robot36` fixtures, sized
to each mode's exact `SstvModeRegistry` canvas), captured by the user against the real legacy Windows
binary, then wired into `GoldenVectorTests.cs`/`GoldenVectorFixtureReaderTests.cs` the same way as the
original two. Full capture/trim/measurement detail lives in
`tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/README.md`'s own new "Task #7" section — this entry
covers only the two real findings worth tracking here.

**Five of six modes decoded cleanly** (scottie-s1, robot-72, pd90, rm8, mn110 — restarts=0, correct mode
detected first-try, deltas in the same healthy range as `martin-m1`/`robot-36`'s own numbers).

**New tracked item, S31 — AVT's real capture never decodes at all (add to Band 3, most urgent of that
band). Root-caused and fixed — see the dedicated "S31" entry further down this file for the real
mechanism (not the working hypothesis below, which turned out to be wrong) and the fix.** Zero
`ModeDetected` events across the entire ~100s real `avt.mmv` capture — not a quality gap
like robot-36's, a total detection failure. Investigated before concluding this is a decoder bug, not a
bad capture: hand-traced the raw audio's frequency content (short-window FFT spot checks) and confirmed
it matches legacy's exact expected header sequence for the first ~2.7s (`OutHEAD`'s 800ms leader
pattern, then a proper VIS leader/break/data-bit sequence), with later content consistent with real
image-body transmission — so the capture is legitimate, the gap is on this port's decode side.
**Working hypothesis, not yet confirmed**: AVT's header is by far the longest of any mode (~8s: 3 VIS
repeats + a ~5.3s training sequence, vs. ~910ms for a normal header) — this port's fixed-window header
detection may not tolerate real-world timing jitter accumulated over that much longer a span, something
no synthetic (self-generated) round-trip test ever exercises. Needs an isolated repro (e.g. feed just
the real header audio, or progressively longer prefixes of it, to the decoder in isolation) before a fix
hypothesis is worth forming — not attempted in this pass, per the user's explicit choice to wire in the
five working modes now and track this separately rather than block on it.

**Smaller finding, folded into S9 (MN/MC narrow retune) rather than a new item**: `mn110`'s real capture
shows no measurable footer trailing-carrier at all (sharp cutoff to noise floor, confirmed via raw
envelope inspection) where the other five new fixtures' footers are all consistent with the existing
428ms-RX-default hypothesis — genuinely unexplained (the real capture session's RX-side state is
unrecoverable after the fact), modeled as an honest zero-footer special case in
`GoldenVectorTests.cs`'s duration check rather than forced to fit the shared formula. Not itself a DSP
bug (this test only checks envelope timing, not decode correctness) — noted here in case it turns out
relevant when S9 is eventually worked.

Test count: 452/452 `ScanlineStudio.Core.Sstv.Tests`, confirmed via a full solution-wide run (not just the
filtered subset used while iterating), 0 failed, 7m9s (423 prior + 29 new: 6 new `LegacyOwnDecode`/
`Fixtures` rows, 5 new `Decoder_DecodesRealLegacyAudio` rows, 5 new `EncoderOutput_DecodesSimilarlyTo`
rows, 6 new `MmvFixture_TxRegionDuration` rows, 6 new `MmvFile_ReadsNewTask7Fixture` rows, 1 new
`BmpFile_ReadsAvtRx...` row — AVT deliberately excluded from the two decoder-dependent theory lists, see
above), solution-wide build clean.


## S31 — AVT's real capture never decoded: root-caused and fixed

The working hypothesis logged above when this item was first tracked ("AVT's header is by far the
longest of any mode... may not tolerate real-world timing jitter") was **wrong** — investigated
properly in a follow-up session, per the user's request to confirm the actual mechanism empirically
before designing a fix, rather than build against the guessed hypothesis.

**Root cause, confirmed empirically** (temporary instrumentation added to both
`AnalogFmSstvDecoder`/`VisLockStateMachine`, run against the real `avt.mmv` fixture, then fully
reverted before any fix code was written — `git status`/`git diff` confirmed clean before proceeding):

- `VisLockStateMachine` — the only mechanism in this port with real-world noise tolerance (finds a VIS
  header anywhere in a stream, not just at a fixed offset relative to `_consumedSamples`) — deliberately
  discarded every AVT match it found, by design (an early scope decision documented on the class itself:
  AVT needed a hand-off to `AvtTrainingLockStateMachine` that only the fixed-window path provided, so
  extending this class to support it was flagged as "a possible future refinement, not attempted").
  The diagnostic hook proved this class already decodes AVT's real VIS byte (`0x44`) correctly from the
  real capture — **three separate times**, once per real VIS repeat, at samples 29495/39526/49559
  (≈2.675s/3.585s/4.495s, spaced exactly `VisHeader.AvtVisBlockDurationMs` apart) — and discarded every
  one.
- The only path allowed to act on an AVT match, `AnalogFmSstvDecoder.TryDecodeVisHeader` (the
  fixed-window path), is a single one-shot attempt anchored at the very start of the current epoch's
  buffer. On a real capture that window lands in `OutHEAD`'s 800ms pre-header leader tones plus ~1s of
  pre-TX room audio (the SAME mechanism already root-caused for martin-m1/robot-36's own anchor-precision
  gap, `VisHeader.MaxSearchCeilingMs` ≈1.3s is exhausted before the real header even starts at ≈1.8s) —
  so it always fails, and (being a one-shot gate per epoch, only reset by `EndOfImage()`, which requires
  a successful decode to ever run) never gets a second chance for the rest of the file.

Net effect: the only mechanism that could find AVT threw the match away; the only mechanism allowed to
act on it never saw real content. Zero `ModeDetected` events, fully explained — not a subtle
timing/jitter issue in the training sequence itself, which the decoder never even got far enough to
reach on real audio.

**Fix** (plan-reviewed by the auditor before implementation, which caught a real blocker before any code
shipped — see below):

1. `VisLockStateMachine.ProcessSample`: removed the premature `mode == SstvModeRegistry.Avt` bail-out in
   `DecodeVis`, letting AVT flow through the same `Verify` state every other mode already uses. Legacy
   justification, confirmed directly against `sstv.cpp:2127-2153`: case 3 runs the identical 1200Hz-hold
   verification for every mode uniformly — the AVT-specific diversion into cases 4-8 (setting the long
   training countdown and `m_SyncAVT`) only happens *after* that shared verification succeeds, never
   instead of it. AVT's own VIS byte (`0x44`) is a normal, non-extended code and AVT is not in the
   Scottie family, so `Verify`'s existing anchor arithmetic already computed exactly
   `headerStart + totalHeaderSampleCount` for it — proven algebraically (and independently re-verified by
   the auditor) via the same identity this class's own doc comment already used to justify its
   non-AVT anchor precision. No new math needed.
2. `AnalogFmSstvDecoder.TryStartAvtTraining`: refactored from `(int headerStart, int totalHeaderSampleCount)`
   to a single `(int visHeaderEndSample)` — every internal use (`_avtTrainingOriginSample`,
   `_avtTrainingFallbackDeadlineSample`, `_avtPllWarmupStartSample`) was already purely a function of
   their sum. Pure refactor, zero behavior change for the existing (fixed-window) caller.
3. `AnalogFmSstvDecoder.TryInterleavedHeaderScan` (the pre-lock noise-tolerant fallback): on an AVT match
   from `VisLockStateMachine`, hands off to `TryStartAvtTraining` instead of `Commit`-ing it as a normal
   line-0 anchor.
4. `AnalogFmSstvDecoder.TryVisLockStateMachine` (piece 6c's mid-reception re-verification, running while
   some OTHER mode is already locked and mid-decode): deliberately still discards an AVT match found
   there, via an explicit guard. Safely restarting into pending AVT training mid-decode would need the
   same in-progress-image teardown `Commit()` does for every other restart, which `TryStartAvtTraining`
   doesn't provide (it's only ever been called while `_mode is null`) — named as a deliberate, narrow,
   deferred non-goal (S31's own reported failure is a first-transmission scenario, not this rarer one),
   not a silent gap.
5. **Auditor plan-review blocker, caught before any code shipped**: item 3 sets
   `_fixedWindowExhausted = true` inside `TryInterleavedHeaderScan`, *before* its own scan loop can find
   an AVT match — invalidating an existing `TrimBuffers` comment's claim that `_avtPllWarmupStartSample`
   was transitively protected by the `_consumedSamples`-based watermark term (that protection only ever
   applied to the fixed-window entry path). Without a fix, a long-running chunked/streaming push could in
   principle trim the buffer past `_avtPllWarmupStartSample` during the `_avtTrainingPending` window and
   crash `Rel()`'s own bounds check — a real, previously undiscovered defect this project's usual bulk
   single-`PushSamples` test shape would never have caught. Fixed with one explicit watermark term
   (`if (_avtTrainingPending) watermark = Math.Min(watermark, _avtPllWarmupStartSample);`), the stale
   comment rewritten to state the new, narrower (~166-sample/~15ms) margin a code-level auditor review
   measured directly, rather than the wide one `_consumedSamples` used to provide.

**Known, accepted residual risks** (both flagged by review rounds, neither chased further): (a) which of
AVT's 3 VIS repeats gets matched is now genuinely signal-dependent (`TryStartAvtTraining` assumes repeat
1) — a match on repeat 2/3 still resolves correctly via `AvtTrainingLockStateMachine`'s own
signal-derived completion point, only the (already-approximate) fallback-deadline path would commit
910ms/1820ms late; (b) `VisLockStateMachine`'s own already-documented false-positive risk (a
sync-heavy image assembling a byte that happens to match a real mode's VIS code) is now, for AVT
specifically, *acted on* rather than silently discarded pre-lock — entering an up-to-~7.1s
`_avtTrainingPending` window during which no other header can be found. Legacy-faithful (`sstv.cpp`'s
own case 3→4-8 behaves identically on a spurious match, timing out via `m_SyncTime`), not a new defect,
but a genuine widening of an already-known risk — noted directly on `VisLockStateMachine`'s own class
doc comment.

**Real fixture, now decoding**: `avt.mmv` — `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource`
delta 5.80 (restarts=0, correct mode detected first-try, tolerance 15.0), `EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode`
delta 9.92 (tolerance 18.0) — both measured directly, comfortably in the same healthy range as the other
five Task #7 fixtures and well under the ~42.67 corruption floor. `avt` is now included in
`GoldenVectorTests.DecoderFixtures` (previously excluded).

**New tests**: `AvtNoiseTolerantDetectionTests.cs` (4 tests) — end-to-end decode via
`VisLockStateMachine` after leading silence a fixed-window scan would miss; a handoff-equivalence check
proving the sample passed to `TryStartAvtTraining` from the new path matches what the fixed-window path
would have computed (not just coincidentally close); a mid-reception guard regression test (item 4);
a chunked/streaming-push regression test for item 5's `TrimBuffers` fix (documented honestly: a
deliberate sweep across leading-silence lengths and chunk sizes, with that fix temporarily disabled,
never actually reproduced a crash in practice — real coverage for the code path, not a proven repro of
the exact crash; the fix is kept regardless, since the invariant violation itself is real and
structural, independent of how hard it is to trigger).

Test count: 458/458 `ScanlineStudio.Core.Sstv.Tests` (452 prior + 6: 2 new `avt` rows in the existing
`DecoderFixtures`-driven theories, 4 new `AvtNoiseTolerantDetectionTests`), solution-wide build clean.
Two plan-review rounds (auditor) plus one code-level review after implementation — code-level verdict:
EQUIVALENT-WITH-RISKS, no blockers, ready to commit as-is; a handful of stale-comment nits it found were
fixed directly rather than deferred.


## S9, S13 — closed while working the remaining Band-3 items (S9 already done, S13 doc-only)

After S31, the user asked to fix the rest of Band 3. Investigating each item before writing code (per
this project's own "fix when measured, not reasoned" rule) found two of the eight didn't need new code:

- **S9 (MN/MC narrow retune) — already closed.** Band-2 item S6 (`6da0a65`) already ported exactly
  this: `HilbertFmDemodulator.ProcessSample`'s `isNarrow`-selected `(off, out)` pairs
  (`NARROW_CENTER`=2172Hz/`NARROW_BW`=256Hz vs. normal 1900Hz/800Hz, matching `CHILL::SetWidth`'s
  narrow branch exactly), gated in `AnalogFmSstvDecoder.DemodulatedFrequencyAt` on
  `_mode.NarrowModeCode is not null && thisIndex >= _bandpassLockedFromSample`. The Band-3 inventory
  entry was simply never updated/merged when S6 shipped — same gap, two tracking numbers. No code.
- **S13 (m_Type demodulator selector) — doc entry only.** The code toggle itself (PLL/zero-crossing/
  Hilbert RX picture demodulator) is correctly blocked on the not-yet-built Phase-3 settings UI. Added
  the missing `docs/removed-features.md` entry (CLAUDE.md §2 process debt, independent of the code
  blocker) — "Picture-demodulator selector (`m_Type`...)" section.


## S10 — extended-VIS escape byte (and every normal VIS byte) decided from 7 bits, not legacy's real 8

**Widened in scope from the original narrow framing** ("extended-VIS escape byte decided from 7 bits,
not 8") to the real underlying gap, found while investigating it: legacy's real `m_VisData` accumulator
is always 8 bits wide (`m_VisCnt` starts at 8, `sstv.cpp:1966-1967`) — 7 data bits PLUS the parity bit
— before its mode-lookup `switch(m_VisData)` (`sstv.cpp:1993-2074`) ever runs, for EVERY arm: the escape
check (`case 0x23`) and every normal single-byte mode case are arms of the exact same switch, not two
separately-timed decisions. `VisLockStateMachine` (the noise-tolerant fallback path) already did this
correctly — it accumulates 8 bits and matches via `SstvModeRegistry.FindByFullVisByte` (full byte,
parity included). Only `AnalogFmSstvDecoder.TryDecodeVisHeader` (the fixed-window path, tried first on
every header) was wrong: it read only 7 bits, decoded via a parity-stripped helper, and looked up via a
parity-stripped registry lookup — for BOTH the escape check and normal-mode matching.

**Practical effect** (why no existing test caught this): on a REAL, noisy capture, if a VIS byte's 7
data bits happen to match a real mode's low-7-bits but the 8th (parity) bit gets corrupted by noise,
legacy rejects the whole byte outright (`default: m_SyncMode=0`) — this port's fixed-window path would
have accepted it anyway, parity never checked. Invisible to any self-round-trip test since this port's
own encoder (`VisHeader.GenerateSegments`) always transmits a correctly-computed parity bit.

**Fix**: `TryDecodeVisHeader` now reads `VisHeader.FirstByteBitCount` (8, new named constant) bits
before deciding normal-vs-extended, and uses `SstvModeRegistry.FindByFullVisByte` (already existing,
already tested, already used by `VisLockStateMachine`) for both the escape check and normal-mode
lookup — reusing proven-correct code rather than inventing new escape-specific logic. The extended-path
bit-slicing was re-partitioned accordingly (still 16 total bits, `VisHeader.ExtendedDataBitCount`
unchanged) but slices cleanly at the new 8-bit boundary instead of skipping a bit. `NormalSearchCeilingMs`
bumped by one bit-slot (1035→1065ms) to stay in sync with the new 8-bit first decision;
`MaxSearchCeilingMs` itself is unaffected (still dominated by the 1305ms extended ceiling). Removed the
now-dead parity-stripped helpers (`SstvModeRegistry.FindByVisCode`, `VisHeader.DecodeVisCode`) rather
than leaving an unused "match ignoring parity" API around — auditor's own framing: "leaving a public
helper around is how this bug re-enters." Deduped `VisLockStateMachine`'s own local `0x23` escape
constant to reference `VisHeader.ExtendedVisEscapeCode` directly instead of an independently-drifting
copy.

**Auditor plan-review** (round 1, EQUIVALENT-WITH-RISKS, ready to build): independently re-verified
against `sstv.cpp` that this is genuinely one gap, not two (escape and normal-mode ARE the same switch);
recomputed all 24 non-extended legacy VIS bytes from the registry and confirmed all reproduce correctly
post-fix, including RM12's own forced-parity quirk (`Rm12ForcedParityBit`); confirmed the ceiling
analysis (`MaxSearchCeilingMs` genuinely unaffected). Flagged one real risk to watch: the 8th
bit-decision point lands close to the 1200Hz stop-bit boundary on real audio (~14.5ms measured
trigger-settling lag vs. a similarly-sized margin) — if golden vectors regressed, the instruction was to
investigate the decision-point timing, not widen tolerances. **All 7 real golden-vector fixtures passed
unchanged, no tolerance touched** — risk did not materialize.

**Code-level review** (after implementation, EQUIVALENT-WITH-RISKS, no blockers, ready to commit):
independently sanity-checked the stop-bit-boundary risk wasn't just "tests happen to pass" — confirmed
algebraically that even if a decision did drift into the stop-bit region, `VisBitDecision.TryDecide`
can only REJECT there (d11/d13 both non-responsive to 1200Hz), never produce a wrong bit, so the worst
case is degraded anchor precision via fallback, never a wrong mode. Found and fixed two doc-comment
nits (`ExtendedDataBitCount`'s definition reworded for clarity, its stale "already used at" claim
fixed). One pre-existing (not introduced by this fix), narrow, non-blocking risk noted but not chased:
a mid-word bit-rejection during the extended-code's second-byte decode could in principle re-trigger at
a different alignment while the caller still assumes the escape byte was proven — requires a specific
rare failure sequence, degrades to a wrong extended-mode-lookup attempt at worst (not a silent wrong
answer, `FindByExtendedCode` would just fail to match), not fixed here.

Test count: 460/460 (458 prior + 2 new: `WrongParityBit_NormalVisCode_NeverLocksViaFixedWindowPath`,
`WrongParityBit_EscapeByte_NeverLocksAsExtendedViaFixedWindowPath` in `VisToneRaceHeaderTests.cs`),
solution-wide build clean.


## S8 — mid-image narrow-mode (MN/MC) FSK re-lock

**Scope turned out much smaller than the roadmap implied.** Investigating before designing anything
found `NarrowFskHeaderDecoder` — a per-sample port of legacy's real `CSSTVDEM::DecodeFSK`
(`sstv.cpp:2378-2606`) — already existed, already faithful, already tested (two prior rounds of
line-by-line auditor review per its own doc comment). It was only ever used in a one-shot, fixed-window
way (`AnalogFmSstvDecoder.TryDecodeNarrowModeHeader`, fresh instance per call at `_consumedSamples`) —
the same "cold-started every call" architectural gap S5/S16 already fixed elsewhere. S8 is "wire the
already-correct decoder up as a persistent, continuously-fed scanner," not "build a new FSK decoder."

Confirmed directly against source: legacy's real `DecodeFSK(int(d19), int(dsp))` call (`sstv.cpp:1858`)
is unconditional every sample — outside and before the `if(!m_Sync||m_SyncRestart||m_SyncAVT)` gate
(`sstv.cpp:1889`) that restricts `m_sint1`/`m_sint2`/`m_sint3` and the VIS-decode switch. Also confirmed:
`CSSTVDEM::Stop()` (`sstv.cpp:1769-1791`) never touches any `m_fsk*` field — legacy's real narrow-FSK
state is genuinely never externally reset, only self-resets internally on its own failure/success
paths, exactly matching `NarrowFskHeaderDecoder`'s already-existing design.

**Fix**: added a persistent `_narrowFskDecoder`/`_narrowFskProcessedUpTo` pair (constructed once,
decoder-lifetime, never `Reset()`), wired into a new shared `AnalogFmSstvDecoder.TryNarrowFskScan`
method, called from both `TryInterleavedHeaderScan` (pre-lock) and `TryVisLockStateMachine`
(mid-reception, piece 6c) — unlike S31's AVT restriction, a narrow-FSK match needs no special-casing at
the mid-reception call site, since it's immediately actionable via the same `Commit()` every other
restart already uses (confirmed: `Commit()`'s body has no AVT-specific or VIS-specific step).

`NarrowFskHeaderDecoder.ProcessSample` now returns `(int ModeCode, int SamplesSinceBitClockOrigin)?`
instead of bare `int?` — the caller needs the packet's own timing to compute a real anchor. New
`VisHeader.NarrowPostBitClockOriginDurationMs` (539ms) constant for that arithmetic.

**Auditor plan-review** (found 2 real blockers before any code shipped, both resolved per the
auditor's own concrete suggested fixes, adopted directly):
1. The persistent scan cursor cannot share `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`'s own
   500ms `EndOfImage` jump — doing so would starve the narrow-FSK scanner of exactly the post-image
   window a real mode-change announcement is most likely to arrive in, defeating the whole point of
   this fix. Fixed by giving `TryNarrowFskScan` its own independent inner loop/cursor, outside the
   existing lockstep for-statement and entry-invariant check.
2. The original design assumed `NarrowFskHeaderDecoder`'s own internal sample counter could be treated
   as an absolute index (since the instance is never reset). Auditor: unsafe premise, nothing enforces
   it, a future caller-side skip would silently produce a wrong anchor, not a crash. Fixed: the class
   returns a RELATIVE offset (`SamplesSinceBitClockOrigin`) instead, and the reference point itself was
   corrected from the mode-0 guard-tone trigger (real, unbounded-in-practice jitter — envelope-settling
   lag plus mode 1's own 50ms hold tolerating a trigger up to ~50ms late) to the mode-3→4 transition
   (tightly pinned by construction — a single pass/fail recheck, not a hold).

**Two further regressions found empirically** (full-suite run, not anticipated by either plan-review
round — found the way this project's own methodology requires, by actually running everything before
calling a piece done):
1. An early implementation bound `TryNarrowFskScan`'s call inside `TryInterleavedHeaderScan` by
   `TotalSamplesReceived` (reasoning: "no fixed-window sibling to race against"). Wrong risk addressed
   — `scanBound` isn't only about protecting a fixed-window path, it's what stops ANY pre-lock detector
   reading ahead into a second, not-yet-legitimately-reached transmission on a bulk single-`PushSamples`
   call (the same "bulk vs. streaming ordering" class Band-1 items 2+3 already fixed elsewhere). Caught
   by `LegacyDerivedSpansTests.FskSpaceCursor_NeverResets_AcrossBackToBackNarrowTransmissions`
   (`ModeDetected` fired 4 times instead of 2) and `BandpassCacheChunkInvarianceTests` (unrelated
   fixture, same root cause). Fixed: bound by `scanBound`, matching the other two detectors.
2. Even after that fix, the same test still failed. Root cause: `Commit()` already fast-forwards
   `_visLockProcessedUpTo` to `_consumedSamples` on every commit (regardless of which path found the
   match) specifically so `VisLockStateMachine`'s own mid-reception scan never re-examines a header
   that just committed — `_narrowFskProcessedUpTo` needed the identical treatment and didn't have it.
   Since image 1's real header was found via the FIXED-WINDOW path (which uses its own separate, local,
   fresh decoder instance, not the new persistent one), the persistent decoder had never actually been
   fed samples 0.._consumedSamples — the first mid-reception scan fed it image 1's own real header for
   the first time, correctly decoded it (real, valid content), and fired a spurious second restart.
   Fixed: added the same `Math.Max` fast-forward in `Commit()` and in the sync-anchor-correction delta
   step, right alongside the existing `_visLockProcessedUpTo` ones. Explicitly NOT the same as the
   `EndOfImage` jump item 1 of the plan-review blockers avoided reintroducing — this is the smaller,
   always-necessary "don't re-discover what was just committed" correction, not an artificial lookahead.

**Code-level review** (after implementation, EQUIVALENT-WITH-RISKS, no behavioral bug found, ready to
commit): independently re-derived the anchor arithmetic against `sstv.cpp` and confirmed it exact at
11025Hz (±1 sample at 44100Hz); confirmed both empirical regressions are fully fixed with no third path
needing the same treatment (every `_consumedSamples` mutation site checked); confirmed no interaction
with S6/S9's narrow-mode retune (different signal domain); confirmed the unregistered-mode-code handling
matches legacy's own resume-on-failure behavior. Found a handful of doc/test-wording nits (a misleading
"first/last sample" claim in the new isolated unit test's comment, a watermark-safety argument that
should have cited the existing catch-up mechanism rather than a cursor-ordering claim Commit()'s
fast-forward can violate, a backwards inequality in a comment, a too-loose buffer-bound test threshold,
an O(n²) test helper) — all fixed directly.

Test count: 465/465 (460 prior + 5 new: `SamplesSinceBitClockOrigin_MatchesExactDataBitPhaseLength` in
`NarrowFskHeaderDecoderTests.cs`; `HeaderAfterLeadingSilence_IsRecognizedAndDecodedViaPersistentScan`,
`HeaderAfterLeadingSilence_AnchorMatchesExpectedWithinMeasuredTolerance`,
`MidReception_RealNarrowTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly`,
`BufferStaysBounded_AcrossMultipleImageCycles_WithPersistentNarrowFskCursor` in new
`NarrowFskNoiseTolerantDetectionTests.cs`), solution-wide build clean.


## AVT package: S7 (mid-image re-lock), S11 (PLL signal domain), S17 (training-entry restructure)

Bundled as one plan-review (matching this project's own pattern-3 finding: AVT items are one work
package, not independent). Auditor verdicts per item: S7 ready to build after one on-paper decision
(made below); S11 not ready as originally proposed (a real flaw in the first draft, caught before any
code shipped); S17 close via documentation, no code needed, confirmed empirically rather than assumed.


### S7 — mid-image AVT re-lock, DONE

S31 left `AnalogFmSstvDecoder.TryVisLockStateMachine` (mid-reception re-verification, piece 6c)
deliberately discarding an AVT match found there, since `TryStartAvtTraining` didn't perform the same
in-progress-image teardown `Commit()` already does for every other restart. Fix: extracted
`Commit()`'s own 5-field header (`_mode`/`_lineDecoder`/`_pixels`/`_nextLine`/`_bandpassLockedFromSample`)
into a shared `AbandonInProgressImage()` helper — `Commit()` calls it first then sets its own new-lock
state on top (pure refactor, verified behavior-preserving for every existing caller); the new
mid-reception AVT branch calls it then `TryStartAvtTraining(...)`, leaving `_mode` null (training isn't
resolved yet) and returning `true` unconditionally — the caller (`TryProcessBuffer`'s per-line loop)
already fires `DecodeRestarted` with its own pre-captured mode and correctly re-enters the outer loop's
`_mode is null` routing either way (training resolved same-call or still pending).

Auditor plan-review independently verified all three load-bearing claims (the extraction is
behavior-preserving; the caller machinery is correct; S31's own `_avtPllWarmupStartSample` `TrimBuffers`
protection already covers this new entry path with no changes needed) and flagged one real,
previously-unconsidered cost, accepted rather than engineered around: once `_mode` goes null mid-image,
`_syncBypassProcessedUpTo` (frozen at wherever the FIRST transmission locked, only re-anchored by
`EndOfImage`, which this path deliberately doesn't call) pins the pre-lock watermark there for the whole
up-to-~7.1s pending window — retaining the abandoned image's audio rather than trimming it. Bounded (by
the same AVT-pending window this port already accepts elsewhere), not a repeat of the crash class
`TrimBuffers`' own `_avtTrainingPending` term prevents — the on-paper decision was to accept and assert
the bound in a new test rather than re-anchor the sync-bypass cursor in the AVT branch (which would
touch load-bearing state outside `AbandonInProgressImage()`'s own clean scope for a rare-case
optimization). Also flagged and accepted: false-positive-lock cost is now asymmetric (destroys a good
image instead of being free), matching legacy's own equally-uncorrectable case-3-through-8 shape and
the same accepted-risk category `VisLockStateMachine`'s own class doc comment and S8 already carry.

New tests: `MidReception_RealAvtTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly` (repurposed
from the old S31-era test that pinned the opposite, now-superseded behavior — asserts `DecodeRestarted`
fires with the OLD mode and `ModeDetected` with `Avt`, per the auditor's own requested pin) and
`MidReception_RealAvtTransmissionAfterAnotherMode_ChunkedPush_StaysBounded` (chunked, not bulk — the
only shape that exercises the `_avtPllWarmupStartSample` protection and the accepted retention
tradeoff; measures rather than assumes the "settles well below peak" property instead of predicting an
exact bound).


### S11 — AVT training PLL signal-domain mismatch, DONE

Legacy's real `m_pll.Do(ad)` (`sstv.cpp` cases 3-7) reads `ad` — the AGC output BEFORE the separate
`*32` scale-up and ±16384 clip every OTHER envelope-detector consumer in this file needs (`d`, what
`AgcSampleAt` already returns). The port's AVT PLL feed used `BandpassFilteredSampleAt(w)*32768.0` —
pre-AGC entirely, not even the same family as `ad`.

**First draft rejected by the auditor's own plan-review round, caught before any code shipped**: the
proposed fix (`AgcSampleAt(w)/32.0`, dividing the already-clipped value back down) was based on an
inverted premise — `|ad|` peaks at ~16384 by construction (`m_agc = 16384.0/m_CurMax`), so the clip
triggers whenever `|ad| > 512`, meaning `AgcSampleAt`'s own output is a hard-limited square wave for
roughly 98% of every cycle at normal amplitude, not "rarely." Dividing that back down would have fed
the PLL a ±512 square wave, not a scaled copy of the real waveform.

**Fix actually shipped**: `_agcSamples` now stores the value UNCLIPPED (the `Math.Clamp` moved to
`AgcSampleAt`'s own return statement — zero behavior change for every existing reader of that return
value), and a new `AvtPllSampleAt(index)` reads the same cache directly, dividing back out only the
`*32` term, never the clip. Exact in every case, not an approximation; reuses the existing cache instead
of adding a new one (zero new cursor/list/`TrimBuffers` entries) — the auditor's own recommended
smaller variant of its "new dedicated cache" option, once the clamp-move insight made a full parallel
cache unnecessary.

**Acceptance criterion, corrected before measuring**: auditor traced `PllFmDemodulator`'s own internal
per-half-cycle AGC (normalizes peak-to-peak to a fixed target) against `CPLL::Do`, confirming the class
is scale-invariant to any consistent input multiplier far above its own ~1.0 floor — both the old and
new feeds are the SAME underlying filtered signal, differing only by a slowly-varying scalar the PLL's
own AGC already divides back out. Expected (and correct) result: **no measurable change** in `avt.mmv`'s
own decode delta — this is a fidelity fix (matching legacy's real signal domain exactly), not an
accuracy fix, stated honestly so it isn't later "corrected" back on a false assumption. Measured directly
(not assumed): `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource` 5.80→5.79,
`EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode` 9.92→9.88 — both unchanged within measurement
noise, exactly as predicted.


### S17 — AVT training-entry restructure, CLOSED via documentation, no code needed

The port's `TryStartAvtTraining` analytically skips all 3 VIS repeats before constructing
`AvtTrainingLockStateMachine`; legacy's real case 3 hands off right after the FIRST repeat, then spends
repeats 2-3 as failed marker-search noise inside cases 4-7's own search loop — a materially different,
search-based entry mechanism the port simplifies away from.

Per the roadmap's own "decide based on measured effect" instruction for this item (not "build the
restructure unconditionally"): auditor plan-review found a source-derived argument that the entry
mechanism can't matter — `AvtTrainingLockStateMachine`'s own case-6-equivalent recalculates the overall
completion timeout ABSOLUTELY from each decoded block's own position in the 32-block sequence (its `h`
byte), not cumulatively from entry, so any error in HOW the state machine entered training cannot
propagate past the first successfully-decoded block. Recommended one cheap empirical check (~20 lines,
temporary instrumentation, added and fully reverted, same methodology as S31's own investigation) before
closing: record `(h, sample)` for every checksum-passing block in `AvtTrainingLockStateMachine` while
decoding the real `avt.mmv` fixture.

**Measured, not assumed**: the port's analytic skip does NOT land exactly on the training's real first
block — the first successfully-decoded block is `h=0x5e` (block 2 of 32), not `h=0x5f` (block 1), a real
~166ms landing imprecision the "ideal" case didn't predict. 31 of 32 blocks decode successfully
(`h=0x5e` down to `h=0x40`). This is a STRONGER confirmation than the auditor's own "ideal-landing"
argument, not a weaker one: it shows the real landing genuinely isn't perfect, yet — because completion
timing recalculates absolutely from whichever block locks first, not cumulatively — this measured
imprecision has zero effect on the training's own final completion accuracy. The restructure would only
recover that one missed block, which recovery doesn't change the answer at all. No code change; closed
via documentation, matching S9/S15's own precedent for this project's "fix when measured, not reasoned"
rule working in the OTHER direction (measurement showing a fix isn't needed, not confirming one is).


### Code-level review, all three items combined

Verdict: **EQUIVALENT-WITH-RISKS, no blockers.** S11's signal domain confirmed bit-exact against
`sstv.cpp:1834-1839`/every AVT case's own `m_pll.Do(ad)` call site; every existing `_agcSamples` reader
confirmed to still go through the clamped return, not the raw cache. S7's `AbandonInProgressImage()`
extraction confirmed behavior-preserving field-for-field; the deliberately-NOT-reset
`_visLockStateMachine`/`_visLockOriginSample` pair confirmed correct (resetting either alone would have
been the actual bug); trim safety traced explicitly through both the locked-branch and pending-window
watermark paths, no cursor can outrun retained data. S17 confirmed zero residual diagnostic
instrumentation in either touched file. Three documentation-only findings closed directly (no behavior
change, so no re-test needed): `AvtPllSampleAt`'s doc comment now states its fix is AGC-stage-only, not
full-chain (the upstream bandpass stage still runs H2/search instead of legacy's real H1 throughout AVT
training — a real, separate, ALREADY-tracked gap per `SearchBandpassFilter.cs`'s own doc comment, not
opened or closed by S11); `_avtPllWarmupStartSample`'s computation now documents the one real behavioral
divergence S7 makes newly reachable (legacy's case-3 PLL feed is gated `!m_Sync`, so it skips its own
30ms warm-up span when a mode is already locked — this port always includes it; immaterial to the
~1850ms warm-up window, confirmed by S7's own passing mid-reception test, but was previously unflagged);
two stale comments at the `TryVisLockStateMachine` call site corrected (claimed `Commit()` always runs
before `DecodeRestarted` fires — true for non-AVT restarts, not for the AVT branch, where training is
still pending). Two remaining nits accepted as genuinely cosmetic, not fixed: a single duplicate-fed
sample on the AVT restart path (state machine can't re-match on it) and a per-call vs. per-fill
`Math.Clamp` move in `AgcSampleAt` (free in practice). One off-scope finding logged in one line per this
project's own ADHD-scope rule, not chased: legacy sets `m_ReqSave` to preserve a substantially-complete
partial image on ANY mid-reception restart (`sstv.cpp:2135-2137`); this port drops the in-progress image
unconditionally on every restart path, not just the new AVT one — pre-existing, no
`docs/removed-features.md` entry yet.

Test count: 466/466 (465 prior + 1 net new: `MidReception_RealAvtTransmissionAfterAnotherMode_
ChunkedPush_StaysBounded` in `AvtNoiseTolerantDetectionTests.cs`; the existing
`MidReception_RealAvtTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly` was rewritten in place to
pin the new restart behavior rather than added as a new test; no new tests for S11 (verified via the
existing golden-vector re-measurement) or S17 (closed via documentation, temporary instrumentation
fully reverted)), solution-wide build clean.


## S12 — m_sint2/m_sint3 freeze-while-decoding-VIS gating, DONE

`TrySyncIntervalDetectionStep`'s m_sint2/m_sint3 blocks (`AnalogFmSstvDecoder.cs`) evaluated
unconditionally every sample, with no equivalent of legacy's real `switch(m_SyncMode)` gating —
`m_sint1` had already been fixed (an earlier holistic-review pass), but `m_sint2`/`m_sint3` had not.

**Legacy source read directly** (`sstv.cpp:1889-1973`), not inferred: case 0 (`if(!m_Sync && m_MSync)`)
runs `m_sint1.SyncStart()`, then (if that didn't match) `m_sint2`'s full SyncMax-or-SyncStart if/else,
then the entire `m_sint3` phase-latch block. Case 1 keeps calling `m_sint2.SyncMax` (condition-true only,
no else-branch, no SyncStart) but has **zero** `m_sint3` references at all. Cases 2/9/3 (real VIS-bit
decode/verify) have **zero** references to any of the three trackers, confirmed by grepping every
`m_sint1`/`m_sint2`/`m_sint3` occurrence in the file.

**First plan-review round caught a real design gap before any code was written**: my original proposed
fix reused the pre-existing `_syncBypass1PrimaryHeld` field (already gating `m_sint1`) to gate
`m_sint2`/`m_sint3` too — auditor traced the actual case boundaries and found `_syncBypass1PrimaryHeld`
only tracks legacy's case-0↔1 boundary (m_SyncMode 0 vs 1), not the case-2/9/3 freeze the item is
literally named for: it goes false again the instant d12 dips below SLvl, which VIS data-bit tones
(1100/1300Hz, close to d12's 1200Hz passband) can readily cause mid-decode — exactly the failure mode
`m_sint1`'s own fix comment already named as the reason it needed gating in the first place. The narrow
fix would have left the real freeze unimplemented while closing the checkbox on a false claim.

**Fix actually shipped**: exposed `VisLockStateMachine`'s own internal state (already modeling
Search/ConfirmLock/DecodeVis/DecodeExtendedVis/Verify = legacy's case 0/1/2/9/3 exactly) via two new
properties, `IsSearching` (case 0 only — gates `m_sint3`'s whole block and `m_sint2`'s SyncStart) and
`IsAtOrBeforeConfirmLock` (cases 0-1 — gates `m_sint2`'s SyncMax continuation). Read at the top of
`TrySyncIntervalDetectionStep`, which already runs BEFORE `_visLockStateMachine.ProcessSample` for the
same sample index (`TryInterleavedHeaderScan`'s own loop order) — giving the state as of the END of the
previous sample, exactly matching legacy's `switch(m_SyncMode)` using its pre-transition value. No new
cursor/list/`TrimBuffers` entry needed; reuses the state machine this file already runs every sample.
A stale comment claiming "`m_sint2` already has an equivalent effect for free" (never true — leftover
justification from when this gap was first identified and deliberately not fixed) was corrected in the
same commit.

**Code-level review, verdict EQUIVALENT, no blockers.** Verified all three legacy case boundaries
against source directly (case 0's SyncMax-or-SyncStart split, case 1's SyncMax-only/no-else, cases
2/9/3's total absence), the ordering claim (checked all 3 real transition edges: trigger sample,
ConfirmLock-fail sample, per-bit-reject sample — no off-by-one), the `EndOfImage`/`Reset()` interaction
(clean — matches legacy's own `Stop()` clearing `m_SyncPhase` in the same place), and the pairing with
`m_sint1`'s unchanged gate (no new double-fire risk). Two doc-only findings closed directly: the existing
`_syncBypass1PrimaryHeld`/`VisLockStateMachine` "two copies can disagree during resettle" comment (already
documenting a pre-existing divergence) now also notes S12's own new consequence — a transient false
Search→ConfirmLock→DecodeVis in the state-machine copy during that same resettle window can freeze
m_sint2/m_sint3 for up to ~270-540ms where legacy would not (bounded, low-probability, not fixed); a
second comment records that legacy freezes `m_sint2`/`m_sint3` permanently after a successful lock
(`m_SyncMode=256` until `Stop()`) while `VisLockStateMachine` resets straight back to `Search`, argued
(and confirmed unreachable) since every lock-returning call exits the scan loop immediately either way.
One test-coverage nit closed: a load-bearing comment was added at the `TryInterleavedHeaderScan` call
site itself, flagging that swapping `TrySyncIntervalDetectionStep`/`ProcessSample`'s call order would
silently invert this gate with no existing test catching it.

Two new isolated unit tests in `VisLockStateMachineTests.cs` (matching this project's own "verify each
sub-piece before wiring" methodology, no `AnalogFmSstvDecoder` involved): `IsSearching_
FalseOnceTooShortBlipEntersConfirmLock_TrueAgainAfterItResets` (reuses the existing
`TooShortBlip_NeverAdvancesPastConfirmLock` blip shape to pin the case-0↔1 round trip) and
`IsAtOrBeforeConfirmLock_FalseWhileRealVisHeaderIsDecodingVisBits` (a full real header, asserting both
phases are observed and in the right order). Full suite green, no existing tolerance needed widening —
consistent with the auditor's own prediction that a real, clean fixture's own tone content rarely
produces the momentary dip this gate specifically guards against.

Test count: 468/468 (466 prior + 2 new, both in `VisLockStateMachineTests.cs`), solution-wide build
clean.


## Band 4 — documentation/test-only items (S27, S29, S30, S21), DONE

All four Band-4 items are pure documentation/test additions — no DSP behavior change anywhere, verified
by an unchanged full-suite result before and after. (S13's doc half was already closed earlier, alongside
S9/S10.) User asked for "a good deep documentation and comment update and audit," so each item below was
re-verified against legacy source directly rather than trusted from the original inventory table's own
summary, and the whole batch got a dedicated code-level auditor review before commit.


### S27 — CQ100 mode: missing `removed-features.md` entry + a stale code comment

Investigated fresh rather than trusting the inventory table's narrower "FIR tap-tripling" framing: an
exhaustive grep of every `g_dblToneOffset` reference in `sstv.cpp` found 44 distinct referencing lines
(46 occurrences) across the whole DSP core (VIS-decode envelope detectors, sync-interval/AFC
frequencies, bandpass filter cutoffs, the AVT training PLL center, `CHILL`'s own `m_OFF`) — not just
`HilbertFmDemodulator`'s tap-tripling, which is a second, independent CQ100-gated effect
(`sstv.cpp:3048-3050`, `m_tap *= 3`). **Code-level audit caught the first draft's own doc entry was
still incomplete** despite that grep: two more real `sys.m_bCQ100`-gated DSP effects existed
independent of `g_dblToneOffset` itself and were missing from `removed-features.md` — an `m_OFP`
sync-timing shift (`sstv.cpp:1181-1184`, a division by `g_dblToneOffset`, not the additive shift every
other site uses, roughly -1.1ms) and a narrowed AFC capture window (`sstv.cpp:1678-1681`/`1688-1691`,
sync±50Hz replacing the wider default range in both narrow and normal branches). Both added to the
entry; the "~30 call sites" estimate corrected to the exact 44/46 figure in all three places it
appeared (`removed-features.md`, this entry, `HilbertFmDemodulator.cs`'s own comment). All four effects
are set from exactly one place, a `-i` command-line flag at startup (`Main.cpp:1065-1077`) — never
reachable via the GUI, an `.ini` key, or any other path (confirmed by a whole-tree grep finding no other
assignment site for either `g_dblToneOffset` or `sys.m_bCQ100`), and this port has no command-line-flag
entry point that could set an equivalent. Fixed the stale comment in `HilbertFmDemodulator.cs` (previously
claimed `g_dblToneOffset` is "confirmed always 0.0," which overstated it — corrected to "confirmed 0.0
on every path this port's architecture can reach," matching `SearchBandpassFilter.cs`'s own already-
correct wording for the same fact) and cross-referenced the new `removed-features.md` entry.


### S29 — `MakeFilter` odd-tap trailing-zero divergence: add an executable guard

The existing `MakeFilter_IsSymmetric_ForEvenTap` test already deliberately excludes odd tap (both of
this port's currently-reachable tap counts, 24@11025Hz and 96@44100Hz, are even), and the class's own
doc comment already documented WHY (legacy's real mirroring loops, `fir.cpp:421-426`, write exactly
`2*(tap/2)+1` entries — for an odd tap that's one short of the full `tap+1` array, leaving the last
slot at its zero-init default) — but nothing pinned this with an executable assertion. Added
`MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric` (`SearchBandpassFilterTests.cs`), parametrized
over two odd tap counts (23, 25) at the port's real filter parameters: asserts the trailing slot is
exactly 0.0, the slot just before it is a real nonzero coefficient, and the array is provably not
symmetric the way the even-tap case is. Guards against a future refactor that "helpfully" fully
populates the trailing slot (looking like an off-by-one fix) silently diverging from legacy's real
(latent, currently unreachable) behavior.


### S30 — `CHILL` middle decimation tier (16-40kHz): add instance-level coverage

The raw filter-coefficient generator (`MakeHilbert`) was already exercised at the middle tier's tap
count via existing `[InlineData(24, 22050.0)]` cases, but no test ever constructed an actual
`HilbertFmDemodulator` INSTANCE at a sample rate in the 16-40kHz range — meaning the tier-selection
logic itself (`sampleRate >= 16000` branch) and its `tierMultiplier=2.0` wiring
(`_offWide`/`_outWide`/`_offNarrow`/`_outNarrow`) had no end-to-end proof, only the isolated kernel.
Added `Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly`
(`HilbertFmDemodulatorTests.cs`) at 22050Hz: asserts `HalfTap == 12` (directly pins tier selection, not
just its downstream effect) and that settled tone readback at 1500/1900/2300Hz is correct, mirroring
the existing `SettledOutput_SteadyTone_ReadsBackCorrectFrequency` pattern already used for the other
two tiers. Updated the class's own doc comment to note this tier is no longer untested, just unused by
any currently-supported sample rate.


### S21 — RM8/RM12 int-truncation divergence: record the exact measured bound

The existing doc comment (`MonoAveragedPairedScanlineDecoder.cs`) already correctly identified the
divergence (legacy truncates to `int` twice — once inside `GetPixelLevel`'s own `d *=
m_DemWhite`/`m_DemBlack`, once at the RM8/RM12 branch's own `d *= gain`; `GetPictureLevel`, the function
the RM8/RM12 branch actually calls, itself calls `GetPixelLevel` exactly once, so this collapses one
hop without changing the truncation count) but only estimated its size as "a couple of levels." Read
`Main.cpp:4038-4073`/`4437-4449` and `ComLib.h:242-243` directly to pin the exact chain
(`m_DemWhite`/`m_DemBlack` both default to `128.0/16384.0`, confirmed via `Main.cpp:875-877`, and
`m_DemCalibration` defaults to 0/`Main.cpp:878`, so `GetPixelLevel`'s calibration branch is never live
on the default path this port models — the single default pair covers this port's whole reachable
domain, not an incomplete scope), then wrote an exhaustive test
(`MonoAveragedPairedScanlineDecoderTests.IntTruncationDivergence_MatchesLegacysExactTwoTruncationChain_WithinMeasuredBound`)
that independently replicates legacy's real two-truncation chain and sweeps every achievable input in
this port's own AGC'd ±16384 sample domain against the port's actual formula. **Exact measured result**:
maximum divergence of precisely 2 levels, never more, and asymmetric as the original comment predicted
but never quantified — legacy-minus-port ranges `-1..+2`, not a symmetric `±2`. `RmGainFactor` (was
`private`) made `internal` so the test references the SUT's real constant rather than a
separately-drifting literal copy. Both the code comment and the roadmap's own "Piece 12" note (search
"No truncation replication") updated with the exact figure. Well inside the existing
10.0 round-trip / 15.0-25.0 golden-vector tolerances, now confirmed by measurement rather than estimate.

**Code-level audit** independently re-derived the exact bound in closed form (not just re-running the
test) and confirmed `-1..+2`/max-abs-2 correct, confirmed the sweep domain is robust beyond its own
stated ±16384 bound (both chains saturate identically outside roughly `[-14336,+14224]`, so the
"every achievable" phrasing is harmless rather than an under-sweep), and confirmed no overflow/sign
risk anywhere in the chain (both sides stay in `double` throughout, matching legacy's own `double`
multipliers on `int` accumulators). Two comment-accuracy nits found and fixed: the test's own doc
comment named `GetPixelLevel` where legacy's real call site is `GetPictureLevel` (which itself calls
`GetPixelLevel` once — the numeric claim was never affected, just the citation); and a self-contradictory
sentence about `Math.Truncate` vs `Math.Floor` was corrected to describe what the code actually needs
(C#'s `(int)` cast's own toward-zero semantics).

Test count: 472/472 (468 prior + 4 new: `MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric` in
`SearchBandpassFilterTests.cs` (2 theory cases), `Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly`
in `HilbertFmDemodulatorTests.cs`, `IntTruncationDivergence_MatchesLegacysExactTwoTruncationChain_WithinMeasuredBound`
in new `MonoAveragedPairedScanlineDecoderTests.cs`), solution-wide build clean, no DSP behavior change.


## Milestone audit, Phase 1+2 (docs/audit-playbook.md) — 2026-08-04

After Band 1-4 closed and Task #7's 8 golden-vector fixtures landed, ran the milestone-audit playbook,
scoped down from its generic template to this repo's actual current state (radio/CAT, DI wiring,
localization, and UI aren't built yet — skipped as units; RX had heavy per-item auditor coverage this
session already, so Phase 2 effort was weighted toward TX/encode and the cross-cutting cursor mechanism,
both comparatively under-scrutinized).

**Status: Phase 1 (unit map, done by the orchestrating session directly, no auditor spend) and Phase 2
(4 batches, fully fanned out) are done. Phase 3 (chain/integration audit) has NOT yet run.** This is
not a complete audit — Phase 3 is explicitly the part per-function/per-unit checks can't cover, and
batch D itself found real golden-vector coverage gaps (Scottie DX, MR73, R24 each exercise a code path
no other fixture does); batch A confirmed TX has zero legacy-decode-verified golden vectors at all
(round-trip only), so TX chain-verification is fundamentally data-limited without a legacy-binary
automation path this project doesn't have.


### Phase 1 unit map

| Unit | Files | Legacy ref | Golden vector |
|---|---|---|---|
| AGC/level detection | `LevelAgc.cs`, `SyncEnvelopeDetector.cs`, `AfcTracker.cs` | `CLVL`, `sstv.cpp` | shared, all 8 fixtures |
| Sync/VIS header detection | `VisLockStateMachine.cs`, `SyncIntervalTracker.cs`, `VisHeader.cs` | `sstv.cpp:1889-1973`+ | all 8 |
| AVT training | `AvtTrainingLockStateMachine.cs`, `PllFmDemodulator.cs` | `sstv.cpp` cases 3-8 | `avt.mmv` |
| Narrow FSK header | `NarrowFskHeaderDecoder.cs` | `DecodeFSK` | `mn110.mmv` |
| Picture demodulator | `HilbertFmDemodulator.cs`, `SearchBandpassFilter.cs` | `CHILL`, bandpass | all 8 |
| RX: RgbSequential (Martin/Scottie/SC2) | `RgbSequentialScanlineDecoder.cs` | `Main.cpp` decode switch | `martin-m1`, `scottie-s1` |
| RX: YCbCrRobot (Robot 36) | `RobotScanlineDecoder.cs` | same | `robot36` |
| RX: YCbCrSequential (Robot72/R24) | `YCbCrSequentialScanlineDecoder.cs` | same | `robot72` |
| RX: YCbCrLinePaired (PD/MP) | `YCbCrLinePairedScanlineDecoder.cs` | same | `pd90` |
| RX: MonoAveragedPaired (RM8/RM12) | `MonoAveragedPairedScanlineDecoder.cs` | same | `rm8` |
| TX: all 5 encoder families + orchestrator | `AnalogFmSstvEncoder.cs` + 5 `*ScanlineEncoder.cs` | `Main.cpp`'s `Line*`, `CSSTVMOD` | **none** — round-trip only |
| Cross-cutting buffer/cursor/restart | `AnalogFmSstvDecoder.cs` (`TrimBuffers`/`Commit`/`EndOfImage`/`AbandonInProgressImage`) | `sstv.cpp` state machine | indirectly, via the above |


### Phase 2 batches (all 4 run, `auditor` subagent, isolated context each)

**Batch A — TX encoders vs legacy.** Verdict: EQUIVALENT-WITH-RISKS. Per-line channel order,
sync/separator placement, per-channel durations, and line counts CONFIRMED correct for all 6 families /
all 43 modes against the real `Line*` functions — the historical Scottie failure class (right duration,
wrong order/sync placement) does not recur anywhere. VIS/header/AVT-preamble/narrow-FSK-packet TX all
CONFIRMED byte/bit-exact.

**Batch B — buffer/cursor/restart mechanism.** Verdict: EQUIVALENT-WITH-RISKS. `Stop()`/`EndOfImage()`
parity with legacy CONFIRMED exact (resets precisely the same state legacy's own `Stop()` does, no more
no less). Cursor enumeration CONFIRMED complete (found several the task's own framing didn't name, all
verified clean). Every `RemoveRange` physical-length invariant CONFIRMED to hold.

**Batch C — AVT + narrow-FSK interaction.** Verdict: EQUIVALENT-WITH-RISKS. S17's own closing claim
(training-entry imprecision doesn't affect final accuracy) independently RE-DERIVED from source and
CONFIRMED, not just re-trusted. Narrow-FSK's lack of a PLL-style warm-up gap CONFIRMED (no filter/phase
state to warm up, unlike AVT's PLL).

**Batch D — RX decoder family cross-check.** Verdict: **NOT EQUIVALENT**. Mode→`ColorEncoding` mapping
CONFIRMED exact 1:1 against legacy's real RX switch partition for every one of legacy's six branches.
Peak-pick-vs-bare read mode CONFIRMED correct per family, not by analogy, at every legacy call site.
Channel order for the two default-branch (untested) mode families (Pasokon, MC) CONFIRMED from their
real TX generators, not inferred.


### Findings, prioritized (MUST/SHOULD/COULD/NICE-TO-HAVE)

**MUST — confirmed, live-reachable bugs, fix soonest:**

1. **[D] Pixel-pitch trim accumulator drift.** Independently confirmed by the orchestrating session
   directly against `Main.cpp:4454-4503`, not just trusted from the auditor. Legacy's segment
   *transitions* (`m_CG`/`m_CB` etc.) use the full, untrimmed nominal channel span; only the
   pixel-index-*within*-a-segment mapping (`x = ps*Width/m_KSS`) uses the trimmed divisor, and any
   leftover time at a segment's tail is simply discarded in legacy, never folded forward. This port's
   decoders instead accumulate `idealSamplesSoFar` by the *trimmed* total across each scan segment
   (`RgbSequentialScanlineDecoder.cs:25-30` and the identical pattern in the other 4 decoders), so every
   scan segment after the first starts early — compounding across channels. Measured magnitude (per
   batch D): PD90 Y2 shifts by up to 4.0px, Martin M1/Scottie S1 B/R channels by 1.33/2.67px, Pasokon
   ~1.33px/segment. Unaffected: RM8/RM12 (single scan segment) and "group C" modes (trim factor exactly
   1.0). Fixtures pass today because their 15.0-25.0 average-delta tolerance absorbs a few pixels of
   channel misregistration — the exact "round-trip + golden vector both agree while wrong" shape
   CLAUDE.md §4 warns about, since the trim was validated on the RX side alone (Piece 11) with nothing
   pinning absolute segment-start positions against legacy's real untrimmed boundaries.
2. **[C] `TryNarrowFskScan` is a whole-buffer pre-pass, not a per-sample interleave.** Independently
   confirmed by the orchestrating session directly (`AnalogFmSstvDecoder.cs:1352-1365`: a complete
   internal `for` loop from `_narrowFskProcessedUpTo` to `bound`, called and fully resolved BEFORE the
   sync-bypass/VIS-lock lockstep loop takes even one step, `:1804-1809`). On a bulk `PushSamples`
   containing an earlier other-mode transmission followed later by a narrow (MN/MC) one, the narrow scan
   sweeps the whole buffer and can commit the LATER transmission before the sync-bypass/VIS-lock loop
   ever examines the EARLIER one's samples — re-introducing, for the narrow-FSK path specifically, the
   exact bug class the `m_sint1` decoder-ordering fix (piece 7d) was written to eliminate. The mid-image
   caller (bounded to ~one line via `TryVisLockStateMachine`) has the same shape but is low-impact there;
   the pre-lock bulk-push caller is the real exposure.
3. **[B] AVT locked images never trim at all.** `_afcProcessedUpTo`/`_slantProcessedUpTo` are the
   locked-branch watermark's two terms (`AnalogFmSstvDecoder.cs:883`), but AVT is the one mode whose AFC
   and Slant trackers are permanently null (`InitializeAfc`/`InitializeSlant` return early for AVT,
   `:2698-2704`/`:2796-2807`), so neither cursor ever advances once an AVT image locks. AVT is ~90s/image
   (240 lines × 375ms) — `_bufferBase` stays pinned at the lock anchor for the whole image, retaining
   ~56MB@11025Hz/~225MB@44100Hz per AVT image. Same failure class Band-1 item S2 fixed pre-lock,
   re-opened here on the locked side for AVT specifically. Not caught by any existing test — the only
   AVT buffer-bound test measures peak-vs-final AFTER the fixture's own final `EndOfImage` already
   trimmed everything back down.

**SHOULD — real, worth doing soon, bounded or conditional impact:**

4. **[A] TX frequency-mapping drops legacy's two integer truncations — DONE.** Legacy's `ColorToFreq`/`GetRY`
   chain truncates twice (int arithmetic); every port TX encoder maps in unrounded doubles. Net: a
   systematic (not random) ~0-4Hz, mean ~2Hz, one-sided-high bias on every transmitted pixel — visually
   nil, but it means true bit-exact TX parity against a real legacy decode is unattainable until this is
   modeled (two `Math.Floor`s), and it's the reason TX golden-vector capture (if ever done) wouldn't
   match byte-for-byte even with a perfectly-timed encoder.
5. **[A] `OutHEAD` pre-VIS tone burst never emitted — DONE** (800ms normal / 400ms narrow, `Main.cpp:7270-7292`,
   called unconditionally before VIS at legacy's shipped `m_VOX=0` default). Not a decode blocker (a real
   legacy RX still locks on the VIS leader), but a real unported TX segment with no
   `docs/removed-features.md` entry and no code comment — a CLAUDE.md §2 process-rule gap, same class as
   S27's CQ100 gap before it was fixed.
6. **[C] Mid-image narrow restart leaves ~1 line with a stale demod-cache config — ASSESSED, DEFERRED**
   (see "Items 6, 8, 10" below). When a non-narrow
   locked image is abandoned mid-image for a narrow-mode commit (S8), `_bandpassFilteredSamples`
   (`useLocked`) and `_demodulatedFrequencies` (`isNarrow`) are both per-index-frozen caches already
   driven ahead by the abandoned line's own decode — so roughly the first line of the NEW narrow image
   is stuck with the OLD mode's `useLocked=true`/`isNarrow=false` config, meaning S9's own narrow Hilbert
   retune doesn't apply to it. Bounded (~1 line), but confirmed, not just risked — neither S8's nor S9's
   own individual review was positioned to see this, since it only exists at their intersection.
7. **[C] Narrow-FSK detection is fully suspended for the whole ~7.1s AVT-training window — DONE.** Legacy calls
   `DecodeFSK` unconditionally throughout AVT training and would actually abort training on a valid
   MN/MC packet found during it; this port's `TryDecodeHeader` short-circuits to AVT resolution while
   `_avtTrainingPending`, so a narrow packet overlapping an AVT header is silently missed. Not documented
   anywhere in the S7/S8 comments.
8. **[B] Pre-lock watermark's `<=` should be strict `<` — DONE** (see "Pre-lock watermark strict
   inequality" below). `FilteredRawSampleAt` reads
   `_rawSamples[Rel(index-1)]`; if a trim ever left `_bufferBase == _bandpassFilteredProcessedUpTo`, the
   next fill would throw. Currently unreachable — protected only by an undocumented numeric coupling
   (`preLockRetentionSamples` ≈1.3s exceeds the 380ms narrow-discriminator window that's the actual
   driver) that nothing enforces or comments on.
9. **[B] `TryInterleavedHeaderScan`'s entry invariant holds by reachability, not construction — DONE**
   (documented, no behavior change — see "TryInterleavedHeaderScan's entry invariant" below). S7's
   `AbandonInProgressImage()` created a second way for `_mode` to become null without going through
   `EndOfImage()` (which is what normally re-syncs `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`).
   Currently safe only because that call site always leaves `_avtTrainingPending` true, short-circuiting
   the scan entirely until a guaranteed `Commit()`. A future "abandon without committing" path would
   throw.
10. **[B] `PixelSampleReader`'s `Math.Clamp` silently substitutes the boundary sample — ASSESSED,
    DEFERRED** (see "Items 6, 8, 10" below) for an
    already-trimmed index, at the one call site that actually writes pixels — converting what `Rel()`'s
    throw elsewhere in the file treats as a loud bug into a silent one, at the highest-consequence reader.
    Safe today (locked watermark stays 2000 samples of margin back), but the inconsistency itself is a
    risk.
11. **[D] Luma `Limit256` clamp missing in 3 of 5 RX decoders — DONE** (Robot36, YCbCrSequential,
    YCbCrLinePaired don't clamp pre-`YCtoRGB`; RgbSequential and MonoAveragedPaired do, matching their
    own legacy sites) — a real cross-family inconsistency, not a uniform policy choice. Only bites on
    out-of-band/overdriven input, which no existing fixture exercises. See "Working the SHOULD backlog"
    below.
12. **[D] Robot 36's tone-selector reads ~1ms later than legacy's own decisive window — DONE**, inside the
    demodulator's settling region toward the following porch — correct on the clean synthetic/fixture
    signal, fragile (biased toward the ambiguous-band toggle fallback) on a real noisy one. **MUST
    fix 3's code-level review found this got MORE relevant, not less**: correcting the luma segment's
    own boundary (MUST fix 3) moved the tone-selector's read point 0.37ms later than before, so it now
    lands on the exact LAST sample of the selector segment — right at the boundary with the 1900Hz
    porch that follows (1900Hz being exactly the ambiguity midpoint, worst case for contamination).
    Correct today (robot-36 still decodes with restarts=0), safe if the demodulator's own group delay
    is non-negative (not traced), but this is now the one read MUST fix 3 pushed right up against a
    segment boundary. Cheap hardening suggested but not applied: read at `endSample - 1` minus a small
    margin, or bound it by legacy's own `m_CG` decisive-window end rather than the segment's full end.
13. **[D] Golden-vector coverage gaps: Scottie DX, MR73, R24 — SUBSTANTIALLY ADDRESSED.** Each is the
    ONLY mode exercising a specific code path no other fixture reaches (Scottie DX: the sole
    `NeverPeakPicks` mode; MR73: the sole mode where luma/chroma trim by different divisors; R24: the
    sole mode using legacy's row-doubling substitution). The TX-side golden-vector work (this file's
    own "TX-side golden-vector tests wired in" entry) added real-legacy-decode coverage for all three
    via `LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` — a real legacy install decoding
    this port's own TX output for each mode, exercising each one's distinct code path end-to-end.
    Not FULLY closed: that only covers the TX-encode direction; there's still no real legacy-CAPTURED
    (RX-direction) fixture for any of these three, so a bug specific to how this port's RX chain
    handles one of these three paths against REAL analog-captured audio (as opposed to this port's own
    clean encoder output) would still pass every existing test. Downgraded from a live gap to a smaller,
    honestly-scoped residual one.

**COULD — worth doing, low urgency:**

14. **[D] Line-paired chroma trim is generalized from the sequential family's own rule** (`IsChromaChannel`
    routes ALL families' RY/BY through `Ks2sTrimFactor`) rather than read from `LinePaired`'s own legacy
    branch, which actually uses `m_KSS`. Numerically harmless today (the two factors are equal in every
    currently-reachable group), but would silently break if a line-paired mode ever landed in the one
    group where they differ. Likely gets touched incidentally while fixing MUST item 1 above.
15. **[C] Three doc comments disagree about whether `_narrowFskProcessedUpTo` is ever re-anchored** —
    `AnalogFmSstvDecoder.cs:333-334` and `:1346` both still claim it's never jumped; `:1984`/`:1999`/
    `:2179` correctly show it being fast-forwarded. Doc-only fix, zero behavior implication.
16. **[B] `InitializeSlant` uses a bare assignment where `InitializeAfc` deliberately uses `Math.Max`** —
    an undocumented asymmetry. Harmless today (fresh tracker + fresh detector on every re-init, no
    in-place buffer mutation the way AFC's correction has), but worth a comment or a matching guard.

**NICE-TO-HAVE — cosmetic / near-zero impact:**

17. [A] MR/ML "hold" segments emit 1900Hz instead of holding the last pixel's real frequency — ~1
    sample, duration preserved, already documented as an approximation.
18. [A] AVT's trailing blip is a literal tone (holds phase) where legacy writes 3 samples of true zero.
19. [A] TX segment-boundary rounding uses `Math.Round`; legacy's real running accumulator truncates —
    both are drift-free running accumulators, differing by ≤1 sample at any single boundary.
20. [A] TX output isn't filtered/scaled the way legacy's real BPF+`m_outgain` pipeline is — only matters
    if TX golden vectors are ever captured from real legacy audio.
21. [A] RM8/RM12's TX luma average is kept as `double`; legacy averages already-truncated `int`s — same
    class as MUST-adjacent finding 4, same fix if ever addressed.
22. [A] TX footer trailing-carrier length uses the port's own (arguably saner) TX-mode duration where
    legacy quirkily uses the RX-mode's; worth one documentation line, not a behavior change.
23. [B] `FirstLockedBandpassIndex` never resets across images on a multi-image stream — diagnostic-only
    field, no functional consumer.
24. [B] `_afcBoundSample` caps the locked watermark at the image's nominal extent, pinning ~0.1% of a
    slow-clock image's tail — bounded, near-zero.
25. [C] AVT's never-locks fallback timeout fires ~9ms early relative to `AvtTrainingLockStateMachine`'s
    own internal timeout, dropping a small margin term legacy's own formula includes.
26. [C] `AvtTrainingLockStateMachine.ProcessSample` returns on the exact sample its counter hits zero;
    legacy's own `Start()` call happens one sample later. ~1 sample.

**Verified, no action needed** (recorded so nobody re-investigates): batch A's `YCbCr.FromRgb` missing
`LimitRGB`-equivalent clamp (proven unreachable — Y/RY/BY extremes for any real 8-bit RGB input never
exceed [0,255]); batch C's confirmation that narrow-FSK has no AVT-PLL-style warm-up-gap concern of its
own (no filter/phase state); batch C's independent re-derivation confirming S17's original closing
argument holds exactly (budget arithmetic matches legacy's real case-3-through-8 timing to sub-ms
precision).


### MUST fix 1 — AVT buffer-trim, DONE

`TrimBuffers()`'s locked branch included `_afcProcessedUpTo`/`_slantProcessedUpTo` unconditionally in
its watermark `Math.Min` chain. AVT is the one mode where `InitializeAfc`/`InitializeSlant` both leave
`_afcTracker`/`_slantTracker` null — two SEPARATE legacy guards, not one (AFC: `sstv.cpp:2258/2263/2267`'s
`m_afc && m_CurMax>16 && mode!=smAVT`, inside all three `m_Type` branches; Slant/AutoStop:
`Main.cpp:3886`'s `(m_AutoStop||m_AutoSync||KRSA->Checked) && mode!=smAVT` — a code-level review
correction caught an earlier draft of this fix's own comment mis-citing both to the same guard). With
both trackers null, `ApplyAfcCorrections`/`ApplySlantTracking` both return immediately every call
without ever advancing their own cursor again once frozen at commit time — permanently pinning the
whole AVT image's buffer for its entire ~90s duration (240 lines × 375ms), retaining tens to hundreds of
MB depending on sample rate. Same failure class Band-1 item S2 already fixed pre-lock, silently reopened
here on the locked side for AVT specifically.

**Fix**: mirrors the pre-lock branch's own already-established pattern for these exact two cursors (its
own comment: "AFC/Slant don't exist yet ... excluded here ... because there's nothing to include") —
`_afcProcessedUpTo`/`_slantProcessedUpTo` are now only folded into the locked watermark `Math.Min` chain
when their tracker is actually non-null. For every other mode (where both trackers are real), the
existing defensive stall-protection property (documented in the same comment block: "an idle-forever
previous lock, e.g. AFC/Slant stalled, shouldn't be able to grow unboundedly either") is preserved
exactly unchanged.

**New regression test** (`AvtNoiseTolerantDetectionTests.LockedAvtImage_BuffersActuallyTrimMidDecode_NotJustAtTheVeryEnd`):
pushes a full AVT image in 20000-sample chunks, samples `BufferedSampleCount` after every chunk once
locked, and checks the buffered count plateaus (not just "ends small," since the existing
`ChunkedPush_StaysBounded` test only samples at the very end, after the image's own final `EndOfImage`
already resets everything — this test samples DURING the locked decode itself). Two checks: a relative
plateau ratio (second-half peak < 1.5× first-half peak) AND an absolute ceiling (a generous multiple of
one line's own sample count), the second added per code-level review — a relative ratio alone would also
pass a slower-but-still-unbounded leak. Also asserts the decoded image still matches the source within
the existing 20.0 AVT tolerance, since `PixelSampleReader`'s index lambda clamps rather than throwing on
an out-of-range read — an over-aggressive watermark would otherwise silently corrupt pixels rather than
crash, and a buffer-only check wouldn't catch that. **Confirmed to actually discriminate the bug**, not
just pass coincidentally: temporarily reverted the fix and re-ran — pre-fix measured 493334 samples
(first-half peak) growing to 993334 (second-half peak, exactly 2.01×, both failing assertions); restored
the fix, re-confirmed passing.

**Code-level review**: EQUIVALENT-WITH-RISKS, ready to commit. Verified every reader that could read
"behind" the new AVT watermark stays safely bounded by the remaining chain terms (pixel decode's own
small margin needs, `ApplyAfcCorrections`/`ApplySlantTracking`'s own early-returns, the sync-anchor-
correction warm-up being unreachable for AVT, `_avtPllWarmupStartSample`'s own pre-lock-only term, the
unconditional D11/D12/D19/FskSpace catch-ups) — no path found where the fix releases data a live reader
still indexes. Verified the stall-protection property can only ever be skipped for AVT specifically (the
one non-AVT window where trackers are momentarily uninitialized, `_pendingAnchorCorrectionMode is not
null`, is already excluded earlier in the same method). Two citation nits fixed (the AFC/Slant guard
mix-up above); two test-strengthening suggestions folded in before commit (the pixel-correctness
assertion and the absolute-ceiling check, both described above).

Test count: 473/473 (472 prior + 1 new: `LockedAvtImage_BuffersActuallyTrimMidDecode_NotJustAtTheVeryEnd`
in `AvtNoiseTolerantDetectionTests.cs`, strengthened with a pixel-correctness assertion and an absolute
buffer ceiling per code-level review, both folded into the same test rather than split out separately),
solution-wide build clean.


### MUST fix 2 — `TryNarrowFskScan` whole-buffer ordering bug, DONE

`TryInterleavedHeaderScan` (pre-lock) used to call `TryNarrowFskScan(scanBound)` ONCE, letting it run
to completion (match or exhaust `scanBound`) before the interleaved sync-bypass/VIS-lock loop below it
ever took a single step. Legacy's real `DecodeFSK` call is unconditional and runs BEFORE the sync/VIS
switch, but for the SAME sample every time (`sstv.cpp:1858` vs `:1889`, confirmed directly, not
inferred) — never a whole-buffer sweep ahead of the other detectors. On a bulk push spanning an earlier
non-narrow transmission followed by a later narrow one, once `_fixedWindowExhausted` (quick, ~1.1s)
made `scanBound` span the whole buffer, the old code let the narrow scan find and `Commit()` the LATER
transmission before the loop ever examined the EARLIER one's own header — silently skipping it. Same
bug class the `m_sint1` decoder-ordering fix (piece 7d) eliminated, reopened here for narrow-FSK.

**Fix**: `TryNarrowFskScan` itself is unchanged — only how it's called from `TryInterleavedHeaderScan`
changed. One catch-up call before the loop, bounded by `Math.Min(scanBound, _syncBypassProcessedUpTo)`
(never further than wherever sync-bypass already sits, since `_narrowFskProcessedUpTo` can legitimately
lag behind across image boundaries — it's never reset/jumped by `EndOfImage`, unlike
`_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`), then one call per loop iteration bounded by
`_syncBypassProcessedUpTo + 1` — in the steady state this processes exactly one narrow-FSK sample per
interleaved-loop sample, reproducing legacy's real per-sample order for every sample in range, not just
the first one.

**New regression test** (`NarrowFskNoiseTolerantDetectionTests.BulkPush_EarlierNonNarrowTransmissionBeforeLaterNarrowOne_DetectsEarlierFirst`):
3s leading silence (forces detection through the noise-tolerant scan, not the one-shot fixed-window
path), a full Martin M1 transmission, then a full MN110 transmission, all pushed in ONE bulk call so
`scanBound` spans everything from the first call — exactly the shape that exercised the bug. Asserts
`ModeDetected` fires twice, Martin M1 first then MN110, both images decode within tolerance, and (per
code-level review) zero `DecodeRestarted` events. **Confirmed to discriminate the bug**: reverting the
fix made `ModeDetected` fire once (MN110 only — Martin M1 silently skipped entirely); restored, fires
twice in the right order.

**Code-level review**: EQUIVALENT-WITH-RISKS, ready to commit. Verified the ordering guarantee holds for
every case, not just the tested scenario (reversed order, back-to-back narrows, multiple images in one
push — all sound, since the per-sample interleave always evaluates narrow-FSK before sync-bypass/VIS-
lock at the SAME index, and the catch-up call only ever covers `_narrowFskProcessedUpTo`'s own past
relative to sync-bypass, never its future). Confirmed the `Math.Min` in the catch-up call is load-
bearing (removing it reintroduces the exact original bug). Two stale-comment nits fixed (claims that the
scan "runs once per call" and "is called with scanBound" — now literally called more often, with tighter
bounds, though the underlying conclusions both still hold, now stated accurately).

**Real risk found and deliberately deferred, not silently absorbed**: the fix increases exposure at a
SECOND call site (`TryVisLockStateMachine`, mid-reception, NOT touched by this fix — its own bound is
`_consumedSamples`, growing by one decoded line per call, so its own theoretical inversion window was
already up to ~146-428ms, not one sample, even before this fix). Before this fix, on a bulk push, the
pre-lock whole-buffer pass had already exhausted `_narrowFskProcessedUpTo` to `TotalSamplesReceived` by
the time locked decode began, making the mid-reception call site's own scan an accidental no-op. After
this fix, `_narrowFskProcessedUpTo` sits much further behind once locked, so that scan now genuinely
runs over the locked image's own picture content — a legacy-faithful exposure (real `DecodeFSK` is
unconditional while locked too, so legacy carries the identical theoretical risk), but newly reachable
in this port where it was previously accidentally muted. The original milestone-audit finding's own
"minor there" assessment undersold this by roughly 4 orders of magnitude in sample count once this fix
landed. **Deliberately deferred, not fixed in this same pass** — same fix shape could be applied to
`TryVisLockStateMachine`'s own call site later if this ever proves reachable in practice; the new test's
own `Assert.Equal(0, restartCount)` stands as a live guard against it regressing silently in the
meantime.

Test count: 474/474 (473 prior + 1 new), solution-wide build clean.


### MUST fix 3 — pixel-pitch trim accumulator drift, DONE

The biggest and most invasive of the three MUST fixes, touching 4 decoder files identically. Legacy's
real per-channel scan-segment BOUNDARIES (`Main.cpp:4454-4503`'s `ps < m_KS`/`ps < m_CG`/`ps < m_CB`
checks) are defined using the mode's full, UNTRIMMED nominal channel span. Only the pixel-index-
WITHIN-a-segment mapping (`x = ps*Width/m_KSS`, `ps` already relative to the segment's own start)
uses the TRIMMED divisor. Any leftover time at a segment's tail (once `x` would reach `Width`) is
simply never assigned a pixel in legacy — discarded, not folded into where the next segment starts.
`RgbSequentialScanlineDecoder`/`RobotScanlineDecoder`/`YCbCrSequentialScanlineDecoder`/
`YCbCrLinePairedScanlineDecoder` instead accumulated their running position by the TRIMMED total
across each whole scan segment, so every segment after the first started early — compounding across
channels.

**Fix**: in each of the 4 files, capture `segmentStartSample = idealSamplesSoFar` BEFORE the per-pixel
loop; walk pixels using a separate `pixelWalk` accumulator (the trimmed per-pixel duration) relative
to that start; after the loop, set `idealSamplesSoFar = segmentStartSample + scan.DurationMs / 1000.0
* sampleRate` (the segment's FULL untrimmed duration), discarding the trimmed pixel walk's own
leftover exactly as legacy's own `x >= Width` boundary does. `RobotScanlineDecoder.cs`'s fix lives in
its shared `DecodePixels` helper (`ref double idealSamplesSoFar`), used for both the luma and chroma
scan segments. `MonoAveragedPairedScanlineDecoder.cs` (RM8/RM12) deliberately NOT touched — single
scan segment per line, structurally immune (confirmed by measurement: rm8's own golden-vector delta
was numerically unchanged) — a one-line comment added there instead, flagging that the bug would
return if a trailing segment were ever added to that family.

**Golden-vector re-measurement** (temporary zero-tolerance technique, together with MUST fix 2's own
narrow-anchor-timing change): martin-m1 1.263→0.44 (big improvement), robot-36 14.476→16.19 (worsened
slightly, same accepted-tradeoff category as an earlier Piece A finding — a real, honestly recorded
consequence of a genuine correctness fix, not chased to zero), scottie-s1 2.74→1.92 (improved),
robot-72 13.46→14.57 (worsened slightly, same category), pd90 1.99→0.96 (improved), rm8 13.76→13.76
(UNCHANGED, confirming `MonoAveragedPaired`'s immunity directly), mn110 12.79→2.39 (improved — NOT
attributed to this fix, since MN110 is a "group C" mode with trim factor exactly 1.0, a mathematical
no-op for it; attributed to MUST fix 2 instead), avt 5.78 (was 5.79, unchanged). No tolerances needed
changing.

**New dedicated test file** `PixelPitchSegmentBoundaryTests.cs`: two isolated tests using synthetic
hard step-edge images (unlike the real `.mmv` fixtures' smooth gradients, a sharp edge makes pixel-
column drift directly measurable, isolated from AGC/noise). Martin M1's R channel (third of three scan
segments, compounding drift from both G and B before it) and PD90's Y2 channel (fourth of four
segments, the single worst-case drift magnitude among all affected decoders). **Confirmed to
discriminate the bug directly**: reverting the fix moved the detected edge column from the true 160 to
165 for both (a measured 5px systematic shift); restored, Martin M1 lands at 162 (2px off) and PD90 at
161 (1px off), both within the ordinary-noise tolerance.

**Attempted, not committed**: dedicated step-edge tests for `RobotScanlineDecoder`/
`YCbCrSequentialScanlineDecoder` too, to close a code-level-review-flagged coverage gap. Investigation
finding worth recording: Robot 72's chroma segments are only 69ms wide across the full 320-pixel width
(~2.4 samples/pixel at 11025Hz) vs. Martin M1's/PD90's own ~5-6 samples/pixel — a sharp step edge on a
channel this narrow is dominated by ordinary envelope-detector settling smear (the same real-time delay
spans far more pixels when each pixel represents so little time), not by this fix's own segment-start
placement. A correct test for these two families needs a differential (pre-fix-vs-post-fix column
shift) design, not an absolute-position check — out of scope for this pass, tracked as a SHOULD-level
follow-up rather than silently dropped.

**Code-level review**: EQUIVALENT, ready to commit. Independently cross-checked the fix's whole premise
— that `scan.DurationMs` really is legacy's real untrimmed nominal span — against `CSSTVSET::SetSampFreq`
formulas for 7 different modes (MRT1, SCT1, R36, R72, PD90, AVT, MR73), all exact matches. Verified the
Robot `ref`-threading composes correctly end-to-end (chroma now starts at exactly legacy's `m_SB`, was
2.7px early). Verified TX independently confirms the trimmed-pitch/untrimmed-boundary asymmetry is
real and RX-only (TX places segments at flat untrimmed pitch, no trim at all). Two real risks found and
addressed: (1) the Robot 36 tone-selector read-timing risk, now folded into the existing SHOULD item 12
above with the fix's own specific consequence documented; (2) the test coverage gap for Robot 36/72,
investigated (see "attempted, not committed" above) and tracked rather than silently left. Several nits
fixed directly: `MonoAveragedPairedScanlineDecoder.cs`'s own immunity now has a guarding comment; the
two committed tests' tolerances tightened and their real measured values recorded in-comment instead of
left implicit.

Test count: 476/476 (474 prior + 2 new, both in `PixelPitchSegmentBoundaryTests.cs`), solution-wide
build clean.


### TX-side capture prep — DONE (waiting on the user's own real-legacy-install time)

User is setting up the real legacy binary under Wine locally and asked whether this session could
prepare the TX-side `.mmv` files itself. Investigated the legacy capture mechanism directly
(`Sound.cpp`) before assuming anything: no CLI/headless RX mode exists (only `-r`/`-i` flags, unrelated),
but the SAME `.mmv` format already used for the existing RX fixtures can be fed into legacy via its own
`File → Play` menu action and replayed through its real-time RX pipeline — no live audio hardware/
routing needed. This session has no screenshot/visual-feedback tooling to drive that GUI reliably, so
the actual `File → Play` / save-image steps stay the user's own — but everything else (generating
correctly-formatted TX audio, and later processing the results into real fixtures/tests) is fully
automatable and was done now.

**New file-format writers, verified before any fixture generation (user's own explicit request: "be
sure to have opus verify the correctness of these two tools before assuming they work")**:
`MmvFile.Write` and `BmpFile.Write` (inverses of the existing `Read` methods, `tests/ScanlineStudio.Core.Sstv.Tests/`).
Opus code-level review (read-only, against `Sound.cpp`/the standard BMP/DIB spec) found `BmpFile.Write`
clean but caught a REAL blocker in `MmvFile.Write`'s first draft: `(short)Math.Round(clamped *
32768.0f)` could wrap a full-scale +1.0 sample to -32768 instead of saturating at +32767 (`Math.Round`
has no `float` overload, so the multiply widens to `double`; .NET's saturating float→int32 conversion
catches the intermediate 32768.0 fine, but the SUBSEQUENT int32→short narrowing is plain truncation,
not saturation — 32768 = `0x8000` truncates to a `short` as -32768). Genuinely reachable, not
hypothetical: `AnalogFmSstvEncoder` yields raw full-scale `Math.Sin(phase)`, so ~1.3% of every cycle's
samples land in the wrap window — every generated file would have been silently corrupted with a
~2x-full-scale impulse roughly every 75 samples throughout the whole transmission. Fixed by clamping in
the integer domain (`Math.Clamp(Math.Round(...), short.MinValue, short.MaxValue)`) instead of relying
on the cast to saturate. New round-trip tests (`FixtureFileFormatTests.cs`, 14 tests, including a
10,000-sample full-cycle sine sweep) confirmed to discriminate the bug directly (reverting the fix
reproduces the exact `+1.0 → -1.0` wrap); `BmpFile.Write`'s own review found zero defects, confirmed by
a hand-traced 2x2/3x3 round-trip plus 5 parametrized round-trip tests across odd/even widths.

**11 TX `.mmv` files generated** (`tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/TxCapture/`,
11025Hz, matching every other fixture): the 8 modes with existing RX fixtures (reusing their exact
source `.bmp`), plus 3 modes the milestone audit flagged as having zero coverage anywhere (Scottie DX,
MR73, R24 — each the sole mode exercising a specific code path), with newly-generated source `.bmp`s
using the same established gradient formula. Generated via a temporary test-project generator, run
once, then fully reverted (git diff clean) per this project's own established methodology.

**Self-decode verification, not just file-format correctness** — user's own explicit follow-up
question caught a real gap: the file-format round-trip tests alone don't prove the ACTUAL generated
files are valid, decodable transmissions, only that arbitrary float values survive the byte format.
New `TxCaptureFixturesTests.cs` (11 tests, kept permanently, not reverted) reads each real generated
file back exactly as a human would hand it to legacy, decodes it with this port's own decoder, and
confirms zero restarts, the correct mode detected, and a correct decode within the same tolerances the
existing RX-direction golden-vector tests use. All 11 pass.

**Next**: `TxCapture/README.md` documents the exact steps and file-naming convention for the user's own
side (set legacy's sample rate to 11025Hz first — mismatched rates trigger a silent lowpass-resample
prompt on `File → Play`, defeating the whole point). No rush — fixtures can be wired in individually as
results come back, without waiting for all 11.


### TX-side golden-vector tests wired in — DONE (all 11 modes) — 2026-08-04

All 11 `<mode-id>_TX_RX.bmp` results came back from the user's real legacy install (Wine). New theory
`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` in `GoldenVectorTests.cs` (11 cases) compares
each source `.bmp` against legacy's own real decode of this port's own encoder output — this is the
actual test spec/14-roadmap.md's own "Explicit prerequisite before Phase 3" note required: TX output
verified against a REAL legacy decode, not just this port's own decoder agreeing with itself.

R24 needed special handling, not a plain top-crop: its source `.bmp` is 120 rows (its real transmitted
row count, `SstvModeRegistry.R24`'s own doc comment) but legacy's saved `_TX_RX.bmp` is the usual 256-row
canvas with each real row duplicated into 2 consecutive display rows (`Main.cpp:4160-4168`, `R=y*2`). A
plain `CropToTop` would compare doubled rows against undoubled source rows and misalign 2x — added
`CropToTopEvenRows` (takes rows 0, 2, 4, ..., 238 of the top 240) to undo the doubling before comparing.

Deltas measured directly via the same temporary-zero-tolerance technique used throughout this session:
robot-36 3.30, martin-m1 1.53, scottie-s1 1.05, robot-72 3.21, pd90 2.40, rm8 5.15, mn110 2.60, avt 0.86,
scottie-dx 1.03, mr73 3.22, r24 3.70. All 11 restart-free, correct mode detected first-try. Every one of
these is BELOW the RX-direction `LegacyOwnDecode_MatchesSourceImage_EstablishesBaselineDelta` numbers for
the same modes (robot-36 6.99, martin-m1 1.57) despite going through this port's own encoder first — real
evidence this port's TX output is a valid, accurately decodable transmission to a real legacy receiver.
Tolerances set to ~2x each measured value (same margin style as the RX baseline's 15.0), still
comfortably under the ~42.67 corruption floor this gradient-image metric measures elsewhere in this file.

**The "TX-side verification tests, built and confirmed" prerequisite is now fully satisfied.** Phase 3
(chain/integration audit) can run.

Test count: 512/512 (501 prior + 11 in `GoldenVectorTests.cs`), solution-wide build clean.


### Phase 3 — chain/integration audit (docs/audit-playbook.md) — 2026-08-04

Two `auditor` calls, isolated context each, per the playbook's Phase 3 instructions: audit the SEAMS
between already-reviewed units (Phase 2), not re-litigate per-unit correctness. RX chain (AGC → sync/VIS
→ AVT/narrow-FSK → demodulator → 5 decoder families → buffer/cursor) and TX chain (encoder orchestrator →
5 encoder families → VIS header handoff) run separately, since TX/RX don't share runtime state.

**RX chain verdict: NOT EQUIVALENT.** One confirmed MUST bug, two risks, two nits.

**TX chain verdict: EQUIVALENT-WITH-RISKS.** No analog of the MUST-bug-class found — structurally
impossible (see below). One new SHOULD-level finding, three already-tracked nits re-confirmed at the seam.


#### MUST 4 — RX per-line cursor rounds every line; legacy never rounds within a transmission

Independently re-verified against both sides directly (not just trusted from the auditor):
`AnalogFmSstvDecoder.cs:1094/1125` computes `lineSampleCount = (int)Math.Round(_effectiveSamplesPerLine)`
and advances `_consumedSamples += lineSampleCount` — i.e. line *k* starts at `anchor + k*round(E)`.
Legacy (`Main.cpp:4133-4148`, `DrawSSTVNormal`) uses ONE continuous integer sample counter `n` for the
whole transmission and derives each line's boundary as `y = int(double(n)/SSTVSET.m_TW)` — a `double`
division against the running counter, never rounded per line. Line *k* starts at exactly `anchor + k*E`
in legacy; this port's line starts drift from that by up to ~0.5 samples/line, compounding across the
image (same bug shape as MUST fix 3, promoted one level up: MUST fix 3 fixed rounded-vs-unrounded
*within* a line/segment; this is the same mismatch *between* lines).

**Structurally invisible to Auto Slant**, which could otherwise mask/correct it: `ApplySlantTracking`
(`:2898-2954`) tracks its own separate, exact fractional grid (`_slantIdealSamplesSoFarInLine`, rolled
over with carried remainder, never reset to 0 — `:2951`), so the measured sync-peak position stays
constant regardless of the line-cursor's own rounding error, and `SlantTracker` never sees a drift to
correct.

Predicted per-mode magnitude (arithmetic from each mode's own registry timing at 11025Hz, not yet
measured against a dedicated test): Robot 72 worst at ~0.50 samples/line × 240 lines ≈ 120 samples drift
by the last line (~25-50px depending on channel); RM8 ~0.47/line × 120 ≈ 56 samples; Robot 36 ~0.25/line
× 120 ≈ 30 samples; Scottie S1/Martin M1/PD90 much smaller (their line pitch lands closer to an integer
at 11025Hz). Correlates with — not proof of, stated as a hypothesis — the existing decoder-vs-source
delta ranking in this file (~line 3153): robot-36/robot-72/rm8 are the three worst-scoring fixtures,
pd90/martin-m1 the two best, matching the `|round(E)-E|` magnitude ranking above.

Not caught by any existing test: golden-vector tolerances (15.0-25.0) absorb a few pixels of horizontal
shear; TX is not making the equivalent mistake (Batch A/Phase-3-TX both confirm TX's accumulator is
drift-free), so no test compares this port's own RX decode against a bit-exact-timed reference precise
enough to expose sub-pixel-per-line drift.

**Fix shape (not yet applied):** keep a `double` line-start accumulator alongside `_consumedSamples`
(mirrors MUST fix 3's own `segmentStartSample`/`pixelWalk` split), advance it by the unrounded
`_effectiveSamplesPerLine` every line, and pass its rounded value as `lineStartSample` — `_consumedSamples`
itself is load-bearing for at least six other cursors/watermarks and should keep tracking the rounded
accumulator's value, not be replaced by a double.


#### TX chain: why the MUST-bug-class is structurally impossible here

Legacy's TX accumulator (`CSSTVMOD::Write`, `sstv.cpp:2842-2846`) is `m_dPos += tim*m_TxSampFreq/1000`,
reset only once per transmission (`InitTXBuf`) — never per line or segment. `AnalogFmSstvEncoder.cs:36-46`
mirrors this exactly (one accumulator spanning header + every pixel + footer), and all five
`*ScanlineEncoder.cs` files feed it the same per-pixel duration they yield (`scan.DurationMs /
mode.ImageWidth`, matching legacy's own `tw /= width`) — there is no second, trimmed derivation of a
segment's length anywhere on the TX side for a MUST-fix-3/MUST-4-shaped mismatch to hide in. Cross-line
counts (all 43 modes, including R24's internal double-increment) and phase continuity (`CVCO::Do`, never
reset per segment) were independently re-derived from source and confirmed to match.

**New SHOULD-level finding: no image/mode dimension contract validation in the TX orchestrator — DONE
(see "Working the SHOULD backlog" below).**
`AnalogFmSstvEncoder.EncodeAsync` (`:23-39`) never checks `IImageSource.Width`/`.Height` against
`mode.ImageWidth`/`.ImageHeight` before encoding; every scanline encoder then indexes the image blindly
(e.g. `RgbSequentialScanlineEncoder.cs:26`, `image.GetScanline(lineIndex)[x]` for `x` up to
`mode.ImageWidth-1`) — independently confirmed directly. A too-small image throws `IndexOutOfRangeException`
mid-stream (after header + partial line already yielded, no clean error state); a too-large one silently
crops with no signal. Legacy is structurally immune (its `Line*` functions read width from the bitmap
itself). Not reachable today (every caller — tests and the TX fixture generator — already passes a
correctly-sized image), but a real landmine once real image input (crop/resize UI, arbitrary file load)
lands in a later phase. Fix is one guard at `EncodeAsync`'s entry, not a math change.

Already-tracked nits re-confirmed at the seam, no new severity: AVT's 3-sample DC-hold vs legacy's true
zero on its training tail (NICE-TO-HAVE 18 — one untested hypothesis noted: this sits at exactly the
preamble→first-body-line boundary, the same region S31, already fixed, used to fail at; not investigated
further, S31 itself is closed); no TX BPF/`m_outgain` stage (NICE-TO-HAVE 20); footer trailing-carrier
unit mix assuming `m_TxSampOff==0` (NICE-TO-HAVE 22).


#### Phase 3 summary

**Findings, prioritized:**
- **MUST**: MUST 4 (RX line-cursor rounding) — the only chain-level bug found; everything else Phase 1-2
  already covers is fixed.
- **SHOULD** (new, added to the existing Phase 1-2 SHOULD list): TX dimension-contract guard; RX
  event-scheduler contract (`LineDecoded`/`DecodeRestarted`/`ModeDetected`); RX `LineDecoded` live-alias
  hazard.
- **NICE-TO-HAVE** (new): the two AFC-boundary nits above.

Not yet fixed — reported for prioritization, per the user's own standing "document everything so nothing
is lost" preference. 512/512 tests still pass (nothing here is caught by any existing test, per each
finding's own "why the tests don't catch it" note above).


### MUST 4 — RX per-line cursor rounding, DONE

Same discipline as MUST fixes 1-3: dedicated regression test written first, confirmed to FAIL on the
pre-fix code with the predicted magnitude, then the fix implemented, re-confirmed, code-reviewed.

**Test** (`LineCursorRoundingTests.cs`, new file): encodes a Robot 72 image (largest predicted per-line
rounding error among the fixture modes, ~0.50 samples/line at 11025Hz) with a hard vertical step edge on
the Y channel at the same column in every row, decodes it, and compares the detected step column at an
early line (2) against the last line (239). Robot 72's Y channel was chosen specifically because it's
the FIRST scan segment in its line shape, isolating this bug from MUST fix 3's already-fixed within-line
effect. Pre-fix (confirmed by actually reverting the fix and re-running): early=161, last=136 — a 25px
drift, matching the ~25px prediction (0.50 samples/line × 239 lines / 4.756 samples/px) almost exactly.
Post-fix: early=161, last=161 — drift eliminated, both within the same ~1px settling noise as
`PixelPitchSegmentBoundaryTests`' own established floor.

**Fix**: new `_idealLineStartSample` double field in `AnalogFmSstvDecoder.cs`, advanced by the unrounded
`_effectiveSamplesPerLine` every line (mirrors MUST fix 3's own `segmentStartSample`/`pixelWalk` split,
one level up). Each line's `nextLineStartSample = round(_idealLineStartSample + _effectiveSamplesPerLine)`
is computed fresh from the running double total — never by re-rounding and accumulating a fixed per-line
step — and `_consumedSamples` (still an `int`, still load-bearing for every other cursor/watermark in the
file) is set to that rounded value rather than incremented by a fixed `lineSampleCount`.
`_idealLineStartSample` is resynced to `_consumedSamples` at every other site that assigns it directly
(`Commit`, `TryResolveSyncAnchorCorrection`, `EndOfImage`'s dead-time skip) so it never drifts across an
image boundary — only during the per-line loop's own fractional accumulation.

**Golden-vector re-measurement** (`GoldenVectorTests.cs`, zero-tolerance technique): RX decode-vs-source
deltas improved most on exactly the modes predicted to have the largest per-line rounding error — robot-36
16.19→5.04, robot-72 14.57→4.41, rm8 13.76→4.17 (rm8's improvement is itself confirmatory: MUST fix 3
couldn't touch it, since `MonoAveragedPairedScanlineDecoder` has only one scan segment per line, but MUST
4 lives in the shared per-line loop, so it improves rm8 just as much — proving these are two genuinely
different bugs, not one being re-measured). Smaller mixed changes on modes with tiny predicted error, same
accepted-tradeoff category as prior fixes: martin-m1 0.44→0.77 (worsened slightly), scottie-s1 1.92→0.45,
pd90 0.96→0.93, mn110 2.39→1.97 (all improved), avt 5.78→6.74 (worsened slightly, code-review confirmed
AVT uses the identical legacy boundary rule so this is expected variance, not a missed case). TX-direction
deltas (`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage`) are UNCHANGED, exactly as predicted —
this fix only touches the RX decoder's cursor, not the encoder.

**Code-level review**: verdict EQUIVALENT. Confirmed against the real current file (not just the
snippet): all 5 `_consumedSamples`-writing sites covered (the narrow-mode-header site resyncs
transitively via its own immediately-following `Commit()` call); same-line `_effectiveSamplesPerLine`
correctly used for both the boundary computation and the accumulator advance (Auto Slant's own mutation
runs strictly after); `lineSampleCount`'s new varying-by-±1 definition has no consumer that assumed a
fixed per-line constant; double-accumulation drift over a full image is ~1e-7 samples, six orders of
magnitude below the bug being fixed. Two cheap nits fixed in this pass (both doc-only): the field's own
comment now notes the ROUNDED cursor is `Math.Round` (round-half-to-even) vs legacy's effective ceiling
— a small, uniform, non-compounding ~0.1px bias absorbed by `SyncAnchorCorrector`, unlike the compounding
drift this fix removes — and now explicitly notes the narrow-mode-header site's transitive coverage via
Commit(). Not fixed (both truly zero-impact, left as documented, not code changes): `Math.Round`'s
banker's-rounding vs legacy's consistent ceiling (bounded to 1 sample, non-compounding either way,
consistent with `Math.Round` usage elsewhere in the file).

Test count: 513/513 (512 prior + 1 in `LineCursorRoundingTests.cs`), solution-wide build clean.

**All 4 confirmed MUST bugs from the milestone audit (3 from Phase 1-2, 1 from Phase 3) are now fixed.**
Remaining open: the 3 new SHOULD-level landmines from Phase 3 (TX dimension-contract guard, RX
event-scheduler contract, RX `LineDecoded` live-alias) plus the pre-existing SHOULD/COULD/NICE-TO-HAVE
backlog from Phase 1-2 — none reachable without a live caller/UI, none urgent.


## Working the SHOULD backlog — 2026-08-04

User: "take the shoulds." Working through all 13 open SHOULD items (10 from Phase 1-2, 3 from Phase 3),
triaged by effort. Doc-only batch (event-scheduler contract, `LineDecoded` live-alias, finding 13 status)
already done above. This section covers the real code fixes.


### TX image/mode dimension-contract guard (Phase 3 SHOULD) — DONE

`AnalogFmSstvEncoder.EncodeAsync` never validated an image's dimensions against the mode before
encoding. Split into a public non-iterator `EncodeAsync` (validates `image.Width/Height` against
`mode.ImageWidth/Height`, throws `ArgumentException` if mismatched) delegating to a private
`EncodeAsyncCore` iterator (the original body, unchanged) — a plain iterator method's body doesn't run
until the first `MoveNextAsync`, so the guard needed to move outside the iterator to throw synchronously
at the `EncodeAsync()` call site, not merely on first enumeration. `ISstvEncoder`'s own XML doc now states
the throw-on-mismatch contract for any future implementer.

New `AnalogFmSstvEncoderInputValidationTests.cs` (4 tests): too-small image throws immediately (checked
via `Assert.Throws` on the un-enumerated call, not `ThrowsAsync`), too-large image throws immediately,
a width-only mismatch throws (code-review finding: the first two tests varied both axes together),
correctly-sized image doesn't throw and produces samples.

Code-level review: verdict PASS-WITH-RISKS. Confirmed no interface break (only implementer), exact-match
is the right rule (no `*ScanlineEncoder.cs` ever legitimately reads outside the mode canvas), exception
type/param convention matches the codebase's own sibling usage. One real risk flagged and independently
resolved: whether any existing golden-vector fixture `.bmp` might not exactly match its mode's canvas
size (which would make this guard newly throw where the old code silently cropped) — checked directly
(`file` on all 8 fixture bmps): every one is byte-for-byte exactly its mode's `ImageWidth x ImageHeight`,
confirmed safe. Two cheap nits fixed (doc comment on the interface, the width-only test case); one nit
left undone (paired-encoder height-not-divisible-by-2 still unguarded — a future-mode-only gap, not
reachable by any mode this port currently defines).


### Robot 36 tone-selector read-point hardening (SHOULD item 12) — DONE

**Round-1 finding caught a real mistake before it shipped**: the first version of this fix backed the
tone-selector's read point off from the segment's exact last sample using an INVENTED
`SettlingMarginSamples = 6` constant, on the assumption that legacy has no narrower "decisive window" to
port instead. Independently re-verified directly against source and found this assumption wrong: legacy
(`sstv.cpp:664-665`, case smR36) sets real `m_SG`/`m_CG` constants — `m_CG` sits exactly 1.0ms before the
tone segment's own nominal end — and `Main.cpp:4286-4297`'s RX switch only re-decides `m_DSEL` for
`ps ∈ [m_SG, m_CG)`, freezing at whatever it was once `ps` reaches `m_CG`. This port's pre-fix read
(`endSample - 1`) was reading a full ~1.0ms (~11 samples at 11025Hz) LATER than legacy's own real last
decision — a genuine fidelity gap, not a theoretical contamination worry, and squarely inside the
following 1900Hz porch's own settling zone (1900Hz being exactly the ambiguity midpoint).

Rewrote using the real legacy constant: new `DecisiveWindowTailMarginMs = 1.0` (traced directly to
`sstv.cpp:664-665`'s derivation, replacing the invented margin), read point now
`endSample - 1 - round(1.0ms in samples)`. Removed the old version's lower clamp against the segment
start (round-1 review's own nit: unreachable, and its fallback would have been wrong if ever reached) —
confirmed unreachable at every sample rate this port supports (the margin would need to be within ~1
sample of the segment's full 4.5ms duration).

Round-2 code-level review: verdict PASS, both round-1 risks confirmed resolved. Two cheap nits fixed:
a doc-comment precision correction (the read lands ~0.1 sample before `m_CG`'s exact boundary, not
exactly on it — deliberate, trades a hair of precision for robustness against `Math.Round`'s worst-case
direction, not an off-by-one) and a note on a latent legacy inconsistency (`sstv.cpp:665` computes `m_CG`
from the bare/nominal `SampFreq`, not the slant-corrected `m_SampFreq` every neighboring line in the same
switch uses — looks like a legacy typo, deliberately not reproduced, flagged so a future reader doesn't
"fix" this port toward it).

Golden-vector re-measurement (zero-tolerance technique): robot-36 RX decode-vs-source and
self-encode-vs-real-audio-decode deltas both UNCHANGED (5.04, 4.79) — expected, not a null result: on
this clean synthetic fixture the tone is fully decisive across its whole span, so old and new read points
land on the same side of the ±200Hz threshold either way. This fix only changes behavior when the OLD
read point was contaminated toward the porch — noisy/real signals or active slant correction, which this
particular fixture doesn't exercise. Confirmed via code review, not assumed.

Test count: 517/517 (513 prior + 4 in `AnalogFmSstvEncoderInputValidationTests.cs`), solution-wide build
clean.


### Luma `Limit256` clamp added to 3 RX decoders (SHOULD item 11) — DONE

Traced legacy's real per-CHANNEL clamp pattern directly (`Main.cpp:4275-4430`) rather than assuming a
uniform "clamp every luma channel" rule -- the actual rule is per-channel, not per-family, and includes
one genuinely surprising asymmetry:

- Robot36 (`Main.cpp:4275-4297`): Y clamped (`Limit256`, line 4282); chroma (R-Y/B-Y via tone-select)
  NOT clamped (line 4304, raw `short(d)`).
- Robot72/MR/ML family (`Main.cpp:4316-4366`): Y clamped (line 4332); R-Y/B-Y NOT clamped
  (lines 4342/4351).
- PD/MP/MN family (`Main.cpp:4381-4430`): Y1 clamped (line 4387); R-Y/B-Y NOT clamped
  (lines 4396-4397/4405-4406); **Y2 -- a SECOND luma read via the identical `GetPictureLevel` peak-pick
  path Y1 uses -- is NOT clamped** (`Main.cpp:4420-4422` feeds straight into `YCtoRGB` with no
  `Limit256` call at all). Confirmed by code-level review as a real legacy asymmetry, not a misread --
  worth noting since it's the single easiest part of this fix to get wrong (the intuitive assumption is
  "both luma segments alike," which peak-picking IS but clamping is NOT).

Threaded a `clamp: bool` through each decoder's existing per-channel dispatch (the channel switch in
`YCbCrSequentialScanlineDecoder.cs`/`YCbCrLinePairedScanlineDecoder.cs`, a new parameter on
`RobotScanlineDecoder.cs`'s shared `DecodePixels` helper), applying `Math.Clamp(value, 0, 255)` only
where legacy does.

New `Limit256ClampTests.cs` (3 tests, one per decoder): drives each decoder directly with a constant
out-of-band frequency (2700Hz, 400Hz past `LuminanceMaxHz`, raw pre-clamp value 384) via a scripted
`PixelSampleReader` stub, and asserts the clamped channel(s) land at 255 while unclamped channel(s) keep
the raw 384 -- including a dedicated Y1-vs-Y2 assertion. Confirmed to discriminate the bug: temporarily
reverted the clamp in all three source files, re-ran, all 3 failed with the exact predicted unclamped
values (74 vs 255, 120 vs 0, etc.), restored, re-confirmed all 3 pass.

Code-level review: verdict PASS. Y1-vs-Y2 asymmetry independently re-confirmed against source. One real
caution flagged, addressed directly rather than dismissed: the port's clamp boundary (`value` reaches
256 exactly at `LuminanceMaxHz`) is not strictly a no-op for all in-band input -- `ReadPeakPicked`'s
own "larger of two samples" bias could in principle read slightly over the nominal band on near-white
content and clip a couple of levels. This is legacy-faithful (legacy's own `Limit256` clips the exact
same peak-picked overshoot), not a port-introduced divergence, and the full suite (520/520, including
the robot-36/robot-72/pd90/mn110 real-legacy-capture golden vectors, all unchanged tolerances) confirms
no real fixture actually triggers it.

Test count: 520/520 (517 prior + 3 in `Limit256ClampTests.cs`), solution-wide build clean.


### Narrow-FSK suspended during AVT training window (SHOULD item 7) — DONE

Confirmed directly against source: legacy's `DecodeFSK` (`sstv.cpp:1858`) runs UNCONDITIONALLY every
sample, including throughout AVT training -- called before the `if(!m_Sync||...)` block AVT's own
case-3-8 state machine lives inside (`:1889`). Its narrow-packet-completion handler (`:2589-2593`)
commits to the narrow mode whenever `(m_SyncRestart || !m_Sync) && m_NextMode && (m_SyncMode >= 0)`.
Independently traced every real `m_SyncMode` assignment during AVT training (round-2-review correction
of an earlier, imprecise citation): the real values are 4/5/6/7/8 (`:2161/2173/2180/2202/2208/2212/
2217/2224/2230/2235`) plus the 256 timeout sentinel (`:2157/2167/2185`) -- all `>= 0`. `m_Sync` stays
false throughout training (the only `m_Sync = 1` assignment anywhere in legacy is `Start()`, `:1743`).
So a valid narrow packet found DURING AVT training genuinely aborts it in legacy. This port's
`TryDecodeHeader` used to short-circuit straight into `TryResolveAvtTraining` while
`_avtTrainingPending`, silently missing any such packet.

**Real design correction found during development, not just a code-review nit**: the first working
version checked `TryNarrowFskScan` once in `TryDecodeHeader` itself (bounded to
`_avtTrainingFallbackDeadlineSample`) before ever calling `TryResolveAvtTraining`. This failed the new
end-to-end test: a bulk single `PushSamples` call lets `TryResolveAvtTraining`'s own while loop consume
the ENTIRE buffer in one shot on the FIRST call (`TotalSamplesReceived` is already the whole buffer), so
`TryDecodeHeader` never got a "second chance" call while `_avtTrainingPending` was already true to check
data that arrived in the SAME push -- the exact "bulk vs. streaming ordering" bug class MUST fix 2 fixed
elsewhere. Fixed by moving the check INSIDE `TryResolveAvtTraining`'s own per-sample loop, interleaved
exactly like `TryInterleavedHeaderScan`'s own already-established lockstep pattern: an initial catch-up
call (`TryNarrowFskScan(Math.Min(TotalSamplesReceived, _avtTrainingProcessedUpTo))`) before the loop,
then `TryNarrowFskScan(_avtTrainingProcessedUpTo + 1)` at the top of every iteration, checked BEFORE
that iteration's own AVT training step (matching legacy's real per-sample order, DecodeFSK before the
sync-mode switch). On a match, explicitly clears `_avtTrainingPending`/`_avtTrainingLock`/
`_avtPllDemodulator` (matching `EndOfImage`'s own reset list for these same 3 fields), since `Commit()`'s
own teardown (`AbandonInProgressImage`) doesn't touch AVT-training-specific state.

New `NarrowFskDuringAvtTrainingTests.cs`: encodes a real AVT image, takes a 4.5s prefix (inside AVT's
own ~2.73s-8.04s training window), appends a FULL real MN110 transmission immediately after, pushes the
combined buffer, asserts the decoder locks onto MN110 (not AVT) with zero restarts (a first-ever lock,
not a restart -- AVT training never committed to `_mode`). Confirmed to discriminate: reverted just this
fix (`git stash` on the one changed file), re-ran, decoder locked onto "avt" instead of "mn110" as
predicted, restored, re-confirmed passing. A header-only splice was tried first and found insufficient
-- `Commit()` defers `ModeDetected` for non-AVT modes until `TryResolveSyncAnchorCorrection` succeeds,
which needs several lines' worth of buffered samples PAST the anchor; the full image provides that.

Code-level review: verdict PASS. Independently re-verified the entire `m_SyncMode`/`m_Sync` legacy
premise line-by-line against source (confirmed correct) and every interleaving/field-reset/trim-safety
detail (all clean). Two cheap doc nits fixed: the `m_SyncMode` value list corrected (6/7 were missing,
512 was wrongly included -- it's `Stop()`'s value, not a training one) in both the fix's own comment and
the test's XML doc; a stale comment claiming `_narrowFskProcessedUpTo` pauses for the whole training
window was corrected (no longer true -- it now advances throughout training via the interleaving this
fix adds). One low risk noted and accepted: the persistent `_narrowFskDecoder` now sees AVT audio it
never did before (more legacy-faithful, since `DecodeFSK` is unconditional in legacy too) -- covered by
the existing `AvtTrainingLockDecoderTests`/`AvtNoiseTolerantDetectionTests` (both bulk-push full AVT
transmissions and assert `detectedMode.Id == "avt"`, which a false narrow-positive during training would
flip outright), all still passing.

Test count: 521/521 (520 prior + 1 in `NarrowFskDuringAvtTrainingTests.cs`), solution-wide build clean.


### TryInterleavedHeaderScan's entry invariant (SHOULD item 9) — documented, no behavior change

`AbandonInProgressImage()` is a second way `_mode` becomes null without going through `EndOfImage()`
(which normally re-syncs `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo` before
`TryInterleavedHeaderScan` -- the pre-lock scanner gated on `_mode is null` -- would run again). This
method doesn't do that resync. Currently safe by REACHABILITY, not by construction: both existing call
sites (`Commit()`, which immediately re-assigns `_mode`; and S7's AVT-training-abandon path, which
leaves `_avtTrainingPending` true, short-circuiting `TryDecodeHeader` past `TryInterleavedHeaderScan`
until a guaranteed later `Commit()`) never actually let `TryInterleavedHeaderScan` observe the unsynced
cursors this method leaves behind. Added a precise doc comment on `AbandonInProgressImage()` itself
explaining this coupling explicitly, so a FUTURE call site that abandons an image without either
committing a new one or entering AVT training doesn't silently break it. No code/behavior change --
both existing call sites are exhaustively safe today, and this method doesn't need a resync neither of
its current callers requires.


### Pre-lock watermark strict inequality (SHOULD item 8) — DONE

**Self-correction, worth recording**: this item was first assessed (like items 6/10) as needing "an
explicit floor clamp threaded through consistently, not a one-line change" and deferred. Re-examined the
same day after the user asked "are 6/8/10 not fixes, or just need more research?" -- reconsidering
`FilteredRawSampleAt`'s own ternary (`index > 0 ? (...reads index-1...) : _rawSamples[Rel(index)] * 0.5`)
showed the earlier conclusion was too conservative: the problematic `index-1` read only happens for
`index >= 1`, so wrapping the subtraction in `Math.Max(0, ...)` rather than using a bare `- 1` gives
EXACTLY today's own value (0) at the one boundary case that worried the original assessment
(`_bandpassFilteredProcessedUpTo == 0`), and exactly one less everywhere else -- a genuinely safe,
minimal, one-line-per-site change after all.

Both `TrimBuffers` sites (pre-lock and locked branches) changed from
`Math.Min(watermark, _bandpassFilteredProcessedUpTo)` to
`Math.Min(watermark, Math.Max(0, _bandpassFilteredProcessedUpTo - 1))`. Updated the adjacent
load-bearing-invariant comment (the one `DemodulatedFrequencyAt(watermark - 1)`'s own catch-up call
relies on, "watermark is ALWAYS <= _bandpassFilteredProcessedUpTo") to note the tightened bound only
strengthens that invariant, never weakens it.

No dedicated test added: the condition is currently unreachable (other retention margins already keep
this term from ever being the chain's own minimum), so there's no observable behavior to write a
discriminating test against -- relied on the mathematical proof (documented in the fix's own comment)
plus the full suite staying green (521/521, unchanged count and unchanged pass) as the correctness
signal instead.

Code-level review: verdict PASS. Independently re-derived the arithmetic identity (`Math.Max(0, X-1)`
equals today's `X` at `X==0`, equals `X-1` for `X>=1`), confirmed the crash-prevention claim holds at
both the `X==0` boundary and every `X>=1` case, confirmed the load-bearing invariant is strengthened not
weakened (tightening one term in a `Math.Min` chain can only make the chain's own result smaller-or-equal,
never larger), confirmed no other reader of `_bandpassFilteredProcessedUpTo` assumed the old
non-strict relationship, and confirmed strict monotonicity (`_bufferBase` can only ever retain
equal-or-more data than before, never less -- the safe direction). One doc-accuracy nit fixed: the
fix's own comment overstated which specific downstream call would have broken under a bare `-1` at the
`X==0` boundary (an earlier clamp+early-return already absorbs it) -- `Math.Max(0, ...)` is still the
right choice for being locally self-evident, just the originally-stated failure mode wasn't the real one.

Test count: unchanged at 521/521 (no new tests -- see "No dedicated test added" above), solution-wide
build clean.


## TX-side SHOULD cluster (items 4, 5) — DONE, in an isolated fork worktree

User approved running two pipelines in parallel: a `fork` (isolated git worktree) handling the
TX-side SHOULD items (4: frequency-mapping truncation, 5: OutHEAD leader-tone port), while the main
session continued the RX-orchestrator cluster (6, 7, 9, boundary-hardening 8/10) directly in the main
working tree. This section covers the fork's own work; the RX-side cluster's own entry lives
separately (main branch). Same test+review discipline as every other fix this session, run
independently in this worktree.


### SHOULD item 4 — TX frequency-mapping integer truncation — DONE

Legacy's real pixel-to-frequency TX chain truncates TWICE via integer arithmetic: `GetRY`
(`ComLib.cpp:3653-3668`) assigns a `double` RHS into `int&` out-parameters (truncates toward zero,
confirmed always non-negative for real 8-bit RGB input so `Math.Floor` is the correct C# equivalent),
then `ColorToFreq`/`ColorToFreqNarrow` (`ComLib.cpp:3491-3501`) does `d*(max-min)/256` using INTEGER
division. A THIRD, family-specific truncation was found by reading `TMmsstv::LineRM` directly
(`Main.cpp:6796-6799`): RM8/RM12's luma averaging (`YY = (YY + Y[x]) / 2`) is also integer division.
This port mapped in unrounded doubles end to end -- a systematic ~0-4Hz one-sided bias on every
transmitted pixel.

Added `YCbCr.FromRgb`'s own `Math.Floor`+`Math.Clamp` (matching `GetRY`+`LimitRGB`'s exact order) and
a new shared `YCbCr.ColorToFreq(colorValue, luminanceMinHz, luminanceMaxHz)` (`Math.Floor` after the
multiply-divide, proven bit-exact to C++ integer division: the multiply is an exact integer product
under 2^53, the divide is by a power of two). Wired into all 5 `*ScanlineEncoder.cs` files, replacing
each one's own inline unrounded formula; `MonoAveragedPairedScanlineEncoder.cs` also got the RM8/RM12-
specific integer-division averaging fix.

New `YCbCrColorToFreqTruncationTests.cs`: exhaustive sweep (every integer 0-255, both bands this port
defines) confirming bit-exact match against an independent from-scratch reimplementation of legacy's
real integer division; confirmed to discriminate (temporarily reverted to floating-point division,
re-ran, confirmed failure at 1503 vs 1503.125, restored). `YCbCrTests.cs`'s existing round-trip
tolerance widened from +/-1 to +/-4 -- measured via a 2,000,000-sample random sweep, not guessed
(expected and legacy-faithful: legacy's own real TX/RX round-trip is lossy by this same chain).

Code-level review (round 1): EQUIVALENT-WITH-RISKS. Two real risks, both fixed: (1) `YCbCr.FromRgb`'s
C# operation grouping (`16 + a*r + b*g + c*b`, left-to-right) differed from legacy's exact grouping
(`16.0 + (a*R + b*G + c*B)`, weighted terms summed first) -- floating-point addition isn't
associative, and every R=G=B gray level's exact chroma value is precisely 128 (the weight
coefficients sum to exactly zero), i.e. exactly on the truncation boundary, so the two groupings could
land different gray levels on different sides of it. Reparenthesized to match legacy exactly. (2) A
misleading golden-vector comment claiming the TX-direction real-legacy-decode test was re-measured and
found unchanged "because a sub-3Hz shift is below one quantization level" -- WRONG: that test reads
checked-in files from disk and never invokes the encoder at all, so "unchanged" was a tautology, not
evidence. Corrected to disclose the real gap: TX-vs-real-legacy validation of this fix doesn't exist
yet, needs a fresh `TxCapture/` re-capture (out of scope, needs the user's own real legacy install).
Two doc-comment nits also fixed (multiply-exactness under-specified; "+/-4 gives margin" corrected to
"+/-4 equals the exact measured max, not a margin").

Golden-vector re-measurement (the one test in `GoldenVectorTests.cs` that live-encodes): all 8 modes
moved (martin-m1 1.29->1.34, robot-36 4.79->4.16, scottie-s1 0.48->0.37, robot-72 4.64->4.13, pd90
1.58->0.18, rm8 3.43->3.80, mn110 1.10->0.14, avt 9.65->9.87), mostly improved, a few worsened
slightly (accepted-tradeoff category, same as every other fix this session), all comfortably inside
existing tolerances.


### SHOULD item 5 — OutHEAD pre-VIS leader-tone burst — DONE

Legacy (`Main.cpp:7270-7292`, `TMmsstv::OutHEAD`) emits a leader-tone burst UNCONDITIONALLY at the
very start of every real transmission (`Main.cpp:7393`, called before the VIS/narrow-FSK header block)
at the shipped `sys.m_VOX==0` default -- narrow: 1900,2300,1900,2300 (400ms); normal:
1900,1500,1900,1500,2300,1500,2300,1500 (800ms), all 100ms/tone. This port's TX encoder never emitted
it at all -- a real missing TX segment, no `docs/removed-features.md` entry, same class of gap S27's
CQ100 omission was before it got fixed. AVT gets the SAME 800ms non-narrow burst as every other
non-narrow mode, not a special AVT-only header -- confirmed directly against source (AVT's own
3x-VIS-repeat logic, `Main.cpp:7429`, lives INSIDE the later non-narrow branch OutHEAD precedes).

Added `VisHeader.GenerateOutHeadSegments(bool narrow)` plus 3 new named constants
(`OutHeadToneDurationMs`/`OutHeadNarrowDurationMs`/`OutHeadNormalDurationMs`), wired into
`AnalogFmSstvEncoder.GenerateFrequencySegments` as the very first segments emitted, before the
existing AVT/narrow/extended/normal branch.

New tests (`VisHeaderTests.cs`): two pure unit tests pin the exact tone sequences; a third
(theory, 3 cases: robot-36/avt/mn110) drives the REAL encoder end to end and measures the actual
generated audio's frequency at t=150ms via a zero-crossing-rate estimator -- this should land on
OutHEAD's own second tone (1500Hz normal, 2300Hz narrow), which is NOT what a no-OutHEAD encode would
produce at that timestamp (VIS's own leader is an unbroken 300ms of 1900Hz). Confirmed to discriminate
(temporarily removed the segment-emission wiring, all 3 cases failed measuring ~1894Hz, matching the
no-OutHEAD prediction almost exactly, restored).

Full-suite run surfaced 2 real consequences of the new 400-800ms of leading audio, both fixed:
`NarrowFskNoiseTolerantDetectionTests`' anchor-position test needed its `expectedAnchor` formula
updated to add the new leading burst; `SstvRoundTripTests`' AVT-specific tolerance needed raising
(10.0 -> 16.0, measured 11.21) since AVT's own already-documented-fragile training lock absorbs a
modest quality cost from the longer preamble (mode detection unaffected).

**Code-level review (round 1): EQUIVALENT-WITH-RISKS, one real finding fixed properly (not just
patched around) rather than dismissed.** 5 test files (`SyncBypassDetectionTests.cs`,
`SyncBypass1DetectionTests.cs`, `SyncScanInterleaveTests.cs`, `PiecesSixCReachabilityTests.cs`,
`SyncBypassNarrowDetectionTests.cs`) strip a fixed header-duration offset from live-encoded audio to
reach a "headerless" body, specifically to exercise the sync-interval-bypass path (`m_sint1`/
`m_sint2`/`m_sint3`). None of their skip formulas included the new OutHEAD term -- post-fix, every one
was 400-800ms short, meaning they were silently locking via the REAL VIS header path instead of the
bypass path they exist to test, while every assertion still passed (a VIS lock is more accurate than a
bypass lock, so nothing looked wrong from outside -- the exact "round-trip passes while both halves
agree on something wrong" shape CLAUDE.md's own Scottie incident warns about, just for a test fixture
instead of production code). Fixed all 5 by adding the missing `OutHeadNormalDurationMs`/
`OutHeadNarrowDurationMs` term. Re-measuring afterward surfaced something genuinely interesting: the
OLD documented deltas for these files (SyncBypassDetectionTests: 19.01/12.65/25.13/13.85;
SyncBypass1DetectionTests: 39.03; SyncBypassNarrowDetectionTests: 24.27/21.17/19.62/16.38/14.29/11.42)
were themselves measuring VIS-lock accuracy, not real sync-bypass accuracy -- with the strip offset
now correct and the bypass path genuinely engaged, freshly measured values are all SMALLER, not
larger (SyncBypassDetectionTests: 3.48/4.93/6.91/4.86, tolerance 29.0->14.0;
SyncBypass1DetectionTests: 6.63, tolerance 43.0->10.0; SyncBypassNarrowDetectionTests:
13.76/13.20/13.04/9.19/8.84/8.29, tolerance 25.0->18.0) -- a genuine sync-bypass anchor turns out to
be MORE precise than what the old (contaminated) numbers were ever actually measuring.

A second finding was a weakly-supported explanation, not a wrong assertion: an early comment
attributed AVT's own tolerance increase to "AGC/level-detection settling," which round-1 review
found implausible (AVT already carries ~10s of its own preamble before line 0, so its AGC is long
converged either way; its training PLL is documented elsewhere as amplitude-scale-invariant) and
proposed an alternative (unverified) mechanism instead. Corrected to present both as open hypotheses,
explicitly noting no instrumented measurement was taken to settle it -- round-2 review additionally
caught that the alternative "whole-VIS-repeat-block-shift" hypothesis doesn't even fully fit either,
since AVT's numbers moved in OPPOSITE directions across two different tests for this same fix
(SstvRoundTripTests' self-round-trip worsened, GoldenVectorTests' self-vs-real-legacy-decode
improved) -- flagged as genuinely unresolved rather than smoothed over with a plausible-sounding story.

Round-2 review: PASS-WITH-RISKS, both round-1 findings confirmed resolved at the code level; residual
findings were all documentation staleness introduced BY the correction itself (two of the five fixed
test files' own comments still cited their old, pre-fix-contaminated numbers) -- all fixed the same
way, by actually re-measuring rather than just updating prose.

Test count: 527/527 (520 prior + 3 in `VisHeaderTests.cs`'s new theory + 2 in its two unit tests),
solution-wide build clean.

**Merged into master.** Before merging, two independent, isolated-context Opus auditor reviews (no
visibility into either side's own prior review reasoning) re-checked the full accumulated diff on each
side end to end -- RX-side (items 4/7/8/9/10/11/MUST 4) and TX-side (items 4/5) -- specifically
re-verifying code AND every comment/legacy-citation, not just re-running the per-item checklist. Both
reviews found only comment/citation staleness (stale "shared by BOTH" caller counts, a misleading
past-tense claim, an off-by-one `Main.cpp` line citation propagated across 4 sites, a dangling doc-
comment cross-reference, imprecise citation ranges, a residual-bias figure that was rate-dependent, an
unacknowledged Auto Slant interaction) -- no functional/behavioral findings on either side. All fixed;
both suites re-confirmed green before merge (RX 521/521, TX 527/527).

**Phase 1 status (2026-08-04): closed out, moving to Phase 2.** All 4 MUST bugs from the full 3-phase
milestone audit fixed; 11 of 13 SHOULD findings fixed or closed via documentation (items 6, 10
deliberately deferred, real reasoning recorded above); remaining COULD (14-16) and NICE-TO-HAVE (17-26)
items are open but low-urgency/cosmetic by their own original triage, same tier as the already-accepted
Band 5 precedent — none block moving on. Two independent, code-level comprehensive audits (RX diff, TX
diff, fresh Opus context each) ran on the full accumulated SHOULD-fix work immediately before this
close-out and found no functional issues.

**Architecture question resolved before Phase 2 code started**: whether `IRadioController`'s scope
should stay rigctld-client-only or also build in Hamlib directly (WSJT-X style). Resolved further than
originally framed — not just "add Hamlib alongside hand-written protocols," but **no hand-written
per-rig CAT protocols at all**. Scanline Studio is a pure client of external CAT backends (Hamlib linked
in-process, `rigctld`, flrig, OmniRig-as-client), full reasoning and backend list in
[[03-cat-layer]] (renamed from "CAT Layer" to "External CAT Backends"), removal accounting in
[docs/removed-features.md](../docs/removed-features.md)'s "Native per-rig CAT protocol implementations"
entry. `spec/02-radio-layer.md` and `spec/04-rigctld.md` updated to match (no `IRigRegistry`/
`RigDefinition`, `RadioConnectionSpec` subtypes now per-backend).


## Phase 2 — Radio layer (no CAT rigs yet) — DONE

Both roadmap items landed together: `ScanlineStudio.Abstractions.Radio` interfaces, `RadioController` (reference
`IRadioController`), and `RigctldClientProtocol`/`RigctldProtocolFactory` (client mode). 639/639 tests
pass solution-wide (528 pre-existing DSP + 111 new/other, none regressed — confirmed `git status` shows
nothing SSTV-related touched). Full detail below; this entry is the summary.

**Design settled via an auditor plan-review pass before any code was written** (not a mechanical build —
this is new architecture, not a port): `IRadioProtocol` dropped its `IRadioTransport` parameter (each
protocol owns its own transport internally, the only shape that also fits future call-based backends
like linked Hamlib/OmniRig — a real `spec/02-radio-layer.md` amendment, not just a code detail);
backend resolution via `IRadioProtocolFactory` with exactly-one-match required (never silent
first-match-wins); poll-loop error taxonomy splitting rigctld protocol errors (`RPRT -n`, no backoff,
keep polling) from transport failures (backoff + dispose/recreate the protocol via the factory, with an
overflow-safe clamp on the exponential formula); `\dump_caps` parsing rejected in favor of probing
`f`/`m`/`t` directly at connect (a `spec/04-rigctld.md` amendment) since `\dump_caps`'s grammar drifts
across Hamlib versions and couldn't be verified without a real instance at plan-review time.

**Hamlib cloned locally for reference** (`hamlib/`, gitignored, same convention as
`yoniq-old/YONIQ-main/`/`QSSTV-main/`) — resolved the plan-review's flagged highest-risk unknown
(whether rigctld's `f`/`m`/`t` get-commands emit a trailing `RPRT` line in backward-compatible mode) by
reading `tests/rigctl_parse.c` directly rather than guessing: they don't (only `set` commands and
errors get an `RPRT` line; `get` commands succeed with just their raw value line(s)). Also confirmed
Hamlib ships a hardware-free "Dummy" rig backend (`RIG_MODEL_DUMMY`, `port_type = RIG_PORT_NONE`) and
the exact real-Hamlib mode-token vocabulary (`src/misc.c`'s `mode_str[]`) used for `RadioMode` mapping.

**Real interop, not just fixtures** — the user's own suggestion mid-session: since a real `rigctld` +
Hamlib's Dummy rig backend exists, spin one up as a subprocess and drive this port's own
`TcpTransport`/`RigctldClientProtocol` against it over a real loopback socket, rather than trusting
fixture-replay tests alone (which only prove the parser matches bytes someone wrote down). Landed as
`RigctldDummyRigIntegrationTests` (4 tests) — best-effort, skips cleanly if `rigctld` isn't on PATH,
confirmed manually first (`rigctld -m 1` by hand) before writing the C# test. Real output matched the
source-derived prediction exactly on the first try, including the Dummy backend's genuinely-unsupported
`get_ptt` (`RPRT -11`), exercising the same capability-absence path the fixture tests cover separately.

**Two real implementation bugs caught by the test suite itself, not review**: (1) `ConnectAsync`'s
`Task.Run` lambda read the `_pollLoopCts` field at execution time instead of capturing its token before
scheduling — a fast concurrent `DisconnectAsync` (exactly what
`ConnectAsync_PublishesConnectingThenConnected...` does) could null the field before the lambda ran,
`NullReferenceException`. Fixed by capturing the token into a local before `Task.Run`. (2) A test
asserted `LastKnownState` *after* `DisconnectAsync`, which deliberately clears it back to null — a test
bug, not an implementation bug, fixed by asserting before disconnecting.

**Deferred, not forgotten**: `\chk_vfo`/VFO support (not needed by `RadioState`'s current domain model).
Server mode, flrig/OmniRig-as-client, `TemplateCatProtocol`, and all `ScanlineStudio.Application`/UI/settings-
persistence wiring are Phase 3/4 per the plan below, unaffected. (Linked Hamlib itself was originally
slated for Phase 4 too — done early instead, see the section immediately below.)

**Demo** (not yet built — Phase 3's job, wiring this into `ScanlineStudio.Application`/UI): `IRadioController`
connects to a real `rigctld` instance and reports live frequency/mode changes in a log/console.


## Linked Hamlib backend ("bring-your-own-libhamlib") — DONE, ahead of its original Phase 4 slot

Built directly after Phase 2 rather than waiting for Phase 4, since the packaging design question
(below) was the actual blocker on starting it, not calendar ordering.

**Packaging decision**: the user asked how to compile Hamlib into the app in-process, WSJT-X-style.
Investigated for real rather than assuming — WSJT-X's actual approach turned out to be a private Hamlib
fork, statically linked via a "superbuild" CMake step (one static binary, no runtime swap), which the
user then explicitly asked NOT to be used as the framing for the exploration. Ran a full `/adhd`
divergent-ideation pass (5 cognitive frames — regulator, logistics, remove-the-load-bearing-assumption,
3am-on-call, ant-colony — 30 raw ideas, clustered, top 3 deepened) followed by an Opus `auditor` review
of the 4 resulting candidates plus the researched WSJT-X precedent. Verdict: **"bring-your-own-
libhamlib"** — Scanline Studio never builds, forks, or vendors Hamlib at all; `ScanlineStudio.Core.Radio.Hamlib` P/Invokes
whatever `libhamlib` the user's OS/package manager already has installed, discovered at runtime,
version-gated to major-4, falling back to the already-working `rigctld` client on failure. Rejected
WSJT-X's static-fork approach specifically on cost/maintenance grounds for a solo hobby project with a
constrained CI-minutes budget and no unmerged Hamlib patches to justify carrying a fork — not on license
grounds (both static and dynamic linking satisfy LGPL here, since Scanline Studio's own source is fully public).
The auditor also found the C-struct-free surface meant no shim project was needed at all (unlike
[[05-audio-engine]]'s MiniAudio integration, which does need one).

**Two rounds of `auditor` plan-review before any code was written** (new architecture, not a port — same
discipline as Phase 2's own plan-review pass), each explicitly re-verifying the prior round's fixes
against real source rather than trusting them:
- **Round 1** found 4 real blockers: `hamlib_version2` is a `const char *` **data export**, not a
  function — P/Invoking it as a function delegate would have crashed the version probe itself (fixed:
  use `rig_version()` instead, confirmed present in the released-4.x export list); Hamlib error codes
  are negative and split into soft/hard via `RIG_IS_SOFT_ERRCODE` — a naive "nonzero = command-level"
  classification would have made a dead/unplugged rig spin `CommandFailed` forever instead of ever
  triggering `RadioController`'s reconnect (fixed: pinned to the macro's exact 11-member soft list);
  no thread was specified for the blocking native calls, which would have frozen the UI thread on a PTT
  keystroke (fixed: `SemaphoreSlim` for mutual exclusion + `Task.Run` for offload, documented as two
  separate concerns); and discovery/the version gate was being re-run on every `RadioController` backoff
  reconnect instead of cached once (fixed: `IHamlibRuntime` computes both eagerly in its constructor).
- **Round 2** re-verified all 4 fixes against source (all confirmed genuinely correct, not hand-waved)
  and found residue from the fixes themselves: a missing UTF-8 null terminator on the switched-off-
  default-marshaling string params, the semaphore-release-vs-uncancellable-native-call contract needed
  stating explicitly, the `RIG_MODE_*` table listed bit *positions* where "exact-value equality" needed
  bit *values* (`1UL << n`), and the connect sequence (`rig_init` → `rig_set_conf`×N → `rig_open`)
  needed pinning since two parts of the plan implied different orderings. Verdict: "Go," all four pinned
  on paper, no third round needed.
- **One more real design bug found later, writing tests** (not caught by either review round): the
  discovery-order locator's override-path handling contradicted its own spec text — implemented as a
  last-resort fallback (tried only after auto-detection failed), when the spec said an explicit user
  override should *win* over auto-detection. Fixed in both the spec and the locator before any test was
  written against the wrong behavior.

**Built**: `ScanlineStudio.Core.Radio.Hamlib` — `INativeLibraryLoader`/`NativeLibraryLoader`,
`HamlibLibraryLocator` (3-tier discovery, override tried exclusively when set),
`IHamlibNative`/`HamlibNative` (the frozen P/Invoke surface, `CLong` for `pbwidth_t`/`hamlib_token_t`,
explicit-UTF-8-plus-null-terminator string marshaling, `Cdecl` throughout), `HamlibVersionGate`,
`IHamlibRuntime`/`HamlibRuntime`/`IHamlibNativeFactory`, `HamlibRadioProtocol`, `HamlibProtocolFactory`.
`HamlibConnectionSpec` added to `ScanlineStudio.Abstractions`. Full design: `spec/03-cat-layer.md`'s "Linked
Hamlib: bring-your-own-libhamlib" section. Full plan with both review rounds' findings:
`/home/artien/.claude/plans/temporal-launching-valiant.md`.

**Tested**: 40 new tests in `ScanlineStudio.Core.Radio.Tests` — fixture/fake-driven unit tests covering
connect-sequence ordering, soft-vs-hard error classification (both branches), capability probing,
dispose safety, and a concurrency test proving the semaphore actually serializes overlapping native
calls, plus **4 real-interop tests against this dev machine's actual installed `libhamlib.so.4` (4.5.5)**
driving Hamlib's own hardware-free Dummy rig backend — confirmed genuinely executing (not skip-via-early-
return) via real ~120-165ms durations, verifying the whole discovery→version-gate→P/Invoke→marshaling
pipeline against genuine native code, not just fakes. Full solution: 678/678 passing (`ScanlineStudio.Core.Sstv.Tests`
528/528 unchanged, confirming no DSP regression). One pre-existing flake noted, not chased (ADHD-scope
one-liner): `RigctldDummyRigIntegrationTests.Capabilities_PttUnsupportedOnTheDummyRig_IsProbedCorrectly`
intermittently fails only under the full parallel test run — a subprocess-connection-wait timing race,
confirmed by two clean 100%-green runs with `xunit.parallelizeTestCollections=false`; pre-existing test
infrastructure fragility exposed by adding more concurrent real-process/real-native tests, not a defect
in the new Hamlib code.

**Explicitly deferred, not built this pass**: cross-backend auto-demotion to `rigctld` (no home for that
policy yet — `IRadioController` has no "try the next backend" concept, needs the
`ScanlineStudio.Application`/settings layer, which doesn't exist) and the Settings UI for the manual
library-override path (hard-coded as a constructor parameter for now).


## Phase 3 — Minimal UI, first end-to-end path — DONE

- [[09-ui]]: `MainWindow` walking skeleton — waterfall, RX image panel, basic TX button — wired to Phase 1/2 services through `ScanlineStudio.Application`. Shipped as 3 real Dock panes (Waterfall/RX Image/TX Controls) plus a fixed radio/frequency status strip in `MainWindow`'s own chrome (a deliberate scope trim, not a 4th dockable pane — nothing in this roadmap or [[09-ui]]'s dialog inventory calls for that).
- [[10-localization]]: `ILocalizationService` + `Translate` extension in place from the start (retrofitting localization onto an already-built UI is far more expensive than building it in from the first window). Done; `ja.json` and `.dfm`-mining are still open (no legacy dialog counterpart exists for these 3 new panes to mine strings from).
- [[07-image-pipeline]]: minimal `IReceivedImageBuffer`/basic TX image selection (crop/resize can follow in Phase 4) — shipped as `IImageFileLoader` (load + fit-to-mode resize only, no user-facing crop/resize tooling).

**Demo — actually run, not just claimed:** real `rigctld` + Hamlib Dummy rig for the radio half (the running app genuinely showed a live frequency in the status strip). For RX/TX audio, no GUI-automation tool was available in the sandbox this ran in, so the round trip was proven via two independent `ISstvSessionService` instances (mirroring two real app processes) over a real PipeWire virtual audio cable — real `MiniAudioEngine`, real `AnalogFmSstvEncoder`/`Decoder`: mode auto-detected, all lines received, PTT keyed correctly, received image pixel-identical to the source. Exercises the same `TransmitAsync` call path the real TX button invokes.

**Aesthetic note**: partway through, the visual direction was corrected to "raw, functional instrumentation" (cuSDR64/Perseus/SDR++ — no rounded corners, no gradients/shadows, 2-4px padding max, monospace numeric readouts, bordered module groups) — recorded as the durable directive in [[09-ui]]'s "Visual design direction" section, applied to everything already built. Waterfall visual richness was explicitly deprioritized relative to RX/TX image handling and templating — kept deliberately simple, revisit later.

Full build log, real bugs caught along the way (a settings-schema layering bug, an RX-image row-copy bug, an e2e-demo design contradiction), and file-level detail: see the session's own `PROJECT_BRIEF.md` history and `/home/artien/.claude/plans/hidden-conjuring-curry.md`.


