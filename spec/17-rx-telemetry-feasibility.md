# 17 — RX Telemetry Feasibility

**Date:** 2026-08-09
**Scope:** every FAKE-LIVE/STUB field on the Receive tab's Sync&Slant/Input-chain/Signal-quality/
Frame-metadata cards, the status bar, and RadioHeaderView (`spec/16-gui-wiring-survey.md`) — is the
underlying data technically obtainable, even if not currently exposed? This is the answer to that
question, verified against both this port's actual DSP/audio source and cloned legacy source
(`yoniq-old/YONIQ-main/`, `grep -a` throughout — plain `grep` silently treats these CP932 files as
binary and returns false-negative zero matches), not inferred or guessed.

**Why this exists:** the GUI wiring survey found that several of the densest placeholder cards
aren't UI-wiring gaps at all — there's no underlying measurement computed anywhere in
`Core.Audio`/`Core.Sstv` today. Before wiring any of these cards, this document sorts every field
into what's actually buildable and how big each piece really is, so building doesn't start on
something that turns out to be architecturally impossible three hours in.

**Classifications:**
- **REAL-EASY** — the data is already computed (just not exposed as a public property/wired to the
  UI), or trivially derivable from what already exists. Cheap, low-risk, no new signal processing.
- **REAL-BUT-NEW-WORK** — technically derivable, but needs genuinely new instrumentation/DSP work,
  or a product decision on exact semantics before it can be built.
- **TIER-B-STYLE** — needs an explicit product decision (what should this even mean for this port?),
  because legacy has no equivalent concept to port from.
- **NOT-POSSIBLE** — architecturally blocked given this port's (and often legacy's own) design, not
  just "not built yet."

**Auditor-verified (2026-08-09)**: spot-checked every REAL-EASY citation plus the NOT-POSSIBLE/
TIER-B negatives. One real error found and fixed: the original squelch verdict ("zero legacy
grounding at all") was wrong — a wrong `grep -a` search term (legacy spells it `SQ`, not "Squelch")
missed a real, narrower legacy squelch feature; corrected below. Four smaller fixes also folded in:
the XRUN counters need a real (small) interface extension, not a pure existing-property read; the
AGC-gain item turned out cheaper than first claimed (zero backend change, not "promote to public");
two citation line-number slips; and a missed detail that legacy has two selectable RX level-meter
types, of which only one is currently ported.

---

## Already shipped (Tier A, `~/.claude/plans/steady-humming-osprey.md`, commit `cf76a08`, plus the
earlier Slant/Sync work) — genuinely REAL today, just needs UI wiring

These are NOT FAKE-LIVE because of missing DSP — they're FAKE-LIVE purely because nobody connected
the binding yet:

| Field (survey card) | Backing property | Note |
|---|---|---|
| Slant ppm (Sync&Slant) | `ISstvDecoder.SlantPpm` | Legacy's `DrawSlantInfo` formula. |
| Offset px (Sync&Slant) | `ISstvDecoder.SyncOffsetSamples` | Legacy's `m_AutoStopPos`; samples not px, needs a trivial per-mode px conversion. |
| Level meters / "Clipping" (Input-chain) | `ISstvDecoder.SignalPeakLevel`/`IsLevelOverdriven` | Boolean threshold, NOT a percentage — legacy never computed a clip %, only this one-shot red-meter-bar threshold. UI needs to show a boolean/LED, not reuse mock2's "0.0%" framing. |
| Buffer (Input-chain, status bar) | `ISstvDecoder.BufferedSampleCount` | Decoder's internal sample-history buffer — NOT the same thing as the status bar's separate audio-engine "XRUN" buffer (see below). |
| Sync tone (Signal-quality) | `ISstvDecoder.SyncFrequencyCorrectionHz` | Already the exact number needed — display as `1200 ± correction Hz`, not raw correction. |
| Status bar Slant | `ISstvDecoder.SlantPpm` | Same property as above, second display site. |

**Auditor-caught gap in this table**: legacy actually has TWO selectable RX level-meter types —
`m_LevelType` (`sstv.h:607`) switches between `m_lvl.m_CurMax`/24578 (what `SignalPeakLevel`/
`IsLevelOverdriven` already port) and a second variant, `m_SyncLvl.m_Lvl`/16384 (`Main.cpp:6162-
6169`), plus a separate peak-hold bar (`:6185-6198`, already documented as omitted,
`LevelAgc.cs:14-20`). Tier A only ported the first variant — a level meter built off today's
properties covers ONE of legacy's two selectable meter modes, not the full feature; note this if
building the Input-chain level meter, don't silently claim full parity.

---

## New findings — REAL-EASY (data exists or is trivially derivable, zero new signal processing)

| Field (card) | Verdict | Source |
|---|---|---|
| Auto-correct "On · locked" (Sync&Slant) | REAL-EASY as a readout; the "On/Off" HALF isn't free | The "locked" half is fully derivable client-side from `SlantPpm != null`. But (auditor-caught) legacy has a real user `AutoSlant` on/off setting (`Mmsstv.ini AutoSlant=1`, cited at `SlantTracker.cs:7`) that this port hardcodes always-on — an actual On/**Off** toggle needs exposing that setting, not just reading a null. Also: for AVT (where `SlantPpm` is always null by design, not because auto-correct is "off"), a naive `SlantPpm != null` readout would misleadingly show "off" — needs an explicit AVT case, not just the null check. |
| Resync button (Sync&Slant) | REAL-EASY | `ISstvDecoder.RequestReSync()` (`ISstvDecoder.cs:60`) is already a full legacy-verified port of `TMmsstv::KRFSClick` (`Main.cpp:14004-14020`), already wired through `ISstvSessionService.RequestReSync()` (`SstvSessionService.cs:216-220`). Button just needs a `RelayCommand`. |
| RX "Device" (Input-chain) | REAL-EASY | `SstvSessionService.TryResolveDeviceAsync(forCapture: bool, ...)` (`:595-607`) already supports capture-side lookup internally — only the public capture-side wrapper (mirroring the existing TX `GetConfiguredPlaybackDeviceNameAsync`) is missing. |
| AGC gain value (Input-chain "Notch·AGC") | REAL-EASY, and cheaper than first thought | Auditor correction: no "promote to public" needed at all — `LevelAgc.cs:103` makes `_agc` a pure function of the ALREADY-public `SignalPeakLevel` (`16384/curMax`, else `512`), so it's derivable purely client-side, zero backend change. Ports legacy's `m_agc` (`sstv.h:233`). Note it lives in legacy's int16 domain (max ~512) — raw display needs a unit-conversion decision regardless of how cheap the wiring is. |
| Luminance histogram / Clip Lo-Hi (Signal-quality) | REAL-EASY | **Not audio DSP at all** — `IImageSource.GetScanline(int y)` (`IImageSource.cs:9`) gives full pixel access on the already-decoded image; a histogram/clip-count is a pure new imaging-utility function over existing data. |
| "Started" timestamp (Frame-metadata) | REAL-EASY | Not currently tracked mid-decode, but trivial to add: `DateTime.UtcNow` captured at lock time. No DSP. |
| File size (Frame-metadata) | REAL-EASY | `ReceiveHistoryEntry.FilePath` is already real (confirmed in the wiring survey) — `new FileInfo(path).Length`. |
| Line progress "168/256" (status bar) | REAL-EASY, cheapest item on this list | `DecodedImageUpdate.Line` (`ISstvDecoder.cs:5`) already carries the current line index on every `LineDecoded` event — combined with the already-known `mode.ImageHeight`, this is missing UI wiring only, zero backend work. |
| Buffer · XRUN (status bar) | REAL-EASY, but not quite zero-design | `MiniAudioEngine.CaptureOverrunCount`/`PlaybackUnderrunCount` (`MiniAudioEngine.cs:127`/`:143`, capture-side logged at `:292-294`) already exist as concrete implementation counters — but (auditor-caught) they're deliberately NOT on the `IAudioEngine` interface (`IAudioEngine.cs:20-23`'s own comment: "implementation-specific... not part of this interface itself"). Surfacing them through `ISstvSessionService` needs a real interface extension plus a `FakeAudioEngine` implementation for tests, not a pure "read an existing public property" job — still cheap, just not zero-design. This is the AUDIO-ENGINE buffer/underrun count, a genuinely different quantity from `BufferedSampleCount` above despite the similar name — don't conflate the two when wiring. |
| Frames today / Log size (status bar) | REAL-EASY | Trivial aggregate count queries against `IReceiveHistoryStore`/`ILogbookRepository` — no DSP involved at all (query-method-surface details not independently re-verified in the audit round). |
| UTC clock (RadioHeaderView) | REAL-EASY | Trivial `DateTime.UtcNow`, zero backend dependency, doesn't even need a service call. |
| Sync tone display formatting (Signal-quality) | REAL-EASY | Already covered above under "Already shipped" — listed again here because the survey's `SyncToneValue` field specifically needs `SyncFrequencyCorrectionHz` reformatted as `1200 ± X Hz`, not a new property. |

---

## New findings — needs a small decision first, then cheap (advanced timing readouts)

**Advanced timing (Sample clock/Sync window/VIS threshold/Drop-line)** — REAL-EASY but **STATIC,
not live per-decode telemetry**. `VisLockStateMachine.cs:78-80` has legacy-cited fixed constants
(`ConfirmLockDurationMs=15`, `BitDurationMs=30`, `VerifyDurationMs=30`; legacy citations
`sstv.cpp:1948/1965/1986`). "Sample clock" is just `SampleRate`. These would display as fixed
reference values that never change between decodes, not per-decode-varying numbers the way mock2's
card layout implies — **flag this UX mismatch before building**: either relabel the card section as
"reference constants" or drop it, rather than shipping something that looks live but is actually
frozen. "Drop-line" threshold specifically: unverified, no hits found in either source tree — don't
build this one without further digging.

---

## Needs a product decision (TIER-B-STYLE) — no legacy concept to port from

| Field (card) | Verdict | Reasoning |
|---|---|---|
| "Source" — detection method (Sync&Slant) | TIER-B-STYLE, cheap once decided | No legacy display concept found (`m_SyncMode` is a lock-state toggle driving a UI button, `Main.cpp:5988-6081`, not a "how was this detected" readout). This port DOES internally know which of 3 paths matched a header (`VisLockStateMachine`, sync-bypass interval detection, `AvtTrainingLockStateMachine`), but `ModeDetected` (`AnalogFmSstvDecoder.cs:905`) carries no tag distinguishing them. The state already exists internally — this is cheap to add ONCE there's a decision on what to call/show ("VIS lock" vs "sync bypass" vs "AVT training" as user-facing labels), since it's inventing a new non-legacy readout, not porting one. |
| True SNR / SNR histogram / noise floor / Min-Max (Signal-quality) | TIER-B-STYLE (established, `steady-humming-osprey.md`) | Legacy's `CNoise` is a noise *generator* for test/sim, not a *measurement* — confirmed via `grep -a`. Needs a product decision on what "SNR" even means for an FM-demodulated SSTV signal with no clean reference, not a legacy-verification pass. |
| Squelch (Input-chain) | TIER-B-STYLE, **corrected verdict** — a real, narrower legacy feature exists, decide whether to generalize it | **Auditor correction, real error in the original pass**: this doc originally claimed "zero legacy grounding at all," based on a `grep -a` for "Sql"/"Squelch"/"Sense" — legacy spells it `SQ`, and that wrong search term produced a false negative. A real squelch exists: `m_RepSQ` (`sstv.h:726`, default 6000, `sstv.cpp:1502`), measured via `m_repsig = m_lmsrep.Sig(m_ad)` (`sstv.cpp:1866`, `CLMS::Sig`, an LMS-predictor-based signal-level measurement, `fir.cpp:217-236` — read as "signal level," not proven to be an SNR-equivalent), thresholded at `sstv.cpp:2688/2734` + `Main.cpp:13553`, with a real user-editable level + live readout (`RepSet.cpp:73/103/155`) persisted as `Repeater/SQLVL` (`Main.cpp:2159/2623`). **Caveat that keeps this TIER-B-ish, not a straight port**: it's repeater-scoped, gated `m_Repeater && !m_Sync` (`sstv.cpp:1860`) — it's not a general RX-chain squelch, so building the general concept mock2 implies still needs a product decision on whether/how to generalize a narrow repeater-only legacy feature, not "invent from nothing." Worth a closer look before ruling it out: `m_repsig`/`CLMS::Sig` may be a cheaper legacy-grounded path toward the broader "Signal quality" card than the TIER-B SNR framing above, since it's a real measured legacy quantity, just not literally named SNR. |

---

## Real new work needed, or architecturally blocked

| Field (card) | Verdict | Reasoning |
|---|---|---|
| Notch-filter state (Input-chain) | TIER-C (established) — real DSP port needed first | `CNotch` (`fir.h:123`) is entirely unported in this codebase — there is no filter yet to report the state of. This is a DSP-port task, not a readout task. |
| True L/R stereo levels (Input-chain) | NOT-POSSIBLE without new architecture | Legacy AND this port are both mono-only in the demod path; this port's `AudioChannelSource` is a channel *selector*, not simultaneous dual-channel capture. |
| Dropped lines (Frame-metadata) | UNVERIFIED, likely NOT-POSSIBLE as a clean concept | No hits for any drop/skip counter in either source tree. This port's per-pixel block-averaging decode approach doesn't obviously distinguish a "dropped" line from a normally-decoded-but-noisy one — would need real design work to even define the concept, not just instrumentation. Don't build without deciding what "dropped" would mean first. |
| Black tone / White tone (Signal-quality) | NOT-POSSIBLE as meaningful per-decode measurements | Confirmed no legacy equivalent — `SyncFreq` (`sstv.cpp:2339`) is legacy's *only* tone-tracking function; no `BlackFreq`/`WhiteFreq` exists. Conceptually different from sync tone too: sync is a fixed reference tone with a defined nominal frequency to compare against, while black/white pixel luma varies continuously with real image content — there is no single "the" black/white tone reading even in principle, the same reasoning this port already applied when Tier A explicitly declined to fake a black/white AFC equivalent. |
| RX level/gain slider (Input-chain, RadioHeaderView) | NOT-POSSIBLE without new work, and no legacy grounding either | Zero hits for any RX input gain concept anywhere in this port (`RxGain`/`RxLevel`/`InputGain`/`CaptureGain` all absent) — capture is mono straight-through with no software gain stage. Would need a genuinely new pre-gain multiply stage in the audio pipeline, and legacy's own RX chain is hardware-gain-only too, so there's nothing to port even if this were built. |

---

## Unverified — flagged, not resolved either way

- **"Reset" button** (Sync&Slant, alongside Resync) — legacy has no separate `Reset`-labeled
  handler distinct from `KRFSClick` (the Resync equivalent). Likely maps to the existing
  restart/abandon-image machinery (`m_ReqSave`/`m_SyncRestart`, already ported), but exact intended
  semantics need confirmation before wiring — don't guess and wire it to the wrong command.
- **"Drop-line" threshold** (Advanced timing) — no hits found either way.
- **Dropped-line count itself** — see above, flagged as likely-not-possible but not conclusively
  ruled out; would need a real design pass, not more grepping.

---

## Recommended build order, if this becomes the next work item

**Batch 1 SHIPPED (2026-08-09)**: Slant ppm, Sync offset (relabeled from an invented "px" unit to
real samples), Auto-correct "locked" readout, Re-sync button, Buffer, Clipping, and Sync tone (with
the narrow-family 1900Hz nominal AND the legacy calibration-offset correctly subtracted out — both
real bugs an auditor round caught before shipping, see `RxImagePaneViewModel.SyncToneDisplay`'s own
doc comment for the full math). Status bar's Slant readout wired too, reusing the same property. Two
new `ISstvSessionService` pass-through properties added for the underlying decoder telemetry. The
dead "Reset" button (no real semantics, see below) was explicitly disabled rather than left silently
inert next to the now-live Re-sync button.

Remaining, cheapest/highest-value first (all REAL-EASY, mostly pure wiring, no new DSP):
1. Line progress, Frames today/Log size, File size, UTC clock, "Started" timestamp, AGC gain
   (client-side derivation, zero backend) — trivial wiring or one-line additions, no risk.
2. RX device name — same shape as work already shipped this session (mirror an existing pattern).
3. Buffer·XRUN — needs a small `IAudioEngine` interface extension + `FakeAudioEngine` update first
   (not a pure existing-property read, per the audit correction above), still cheap but budget for
   that extra step.
4. Luminance histogram / Clip Lo-Hi — new but small imaging-utility function, no DSP risk.
5. Auto-correct's "on/off" HALF still not wired (the "locked" half shipped in batch 1) — needs
   exposing legacy's real `AutoSlant` setting (currently hardcoded on) plus an explicit AVT case, not
   just a null check — slightly bigger than it looks, see the table entry above.

Then, only with explicit product decisions made first: "Source" (detection-method labels), Advanced
timing (relabel as static reference vs. drop the card section), "Reset" button semantics, and
squelch (a real, narrow, repeater-scoped legacy feature exists — decide whether/how to generalize
it before building, per the corrected verdict above; note `CLMS::Sig`/`m_repsig` may also be a
cheaper legacy-grounded path toward the broader Signal-quality card than the TIER-B SNR framing).

Everything else on this page (true SNR/noise-floor, notch state, true stereo, dropped lines,
black/white tone, RX gain) is either a real new DSP/feature build or not possible as described — do
not schedule these as "quick wiring" work.
