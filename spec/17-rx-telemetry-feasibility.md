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

**Batch 2 SHIPPED (2026-08-09)**: Status bar's "line N / total" readout (from
`IReceivedImageBuffer.Progress`, an already-computed `[0.0,1.0]` fraction — real, zero new backend),
Frame-metadata card's "Started" timestamp (new client-side `DateTimeOffset.UtcNow` capture at
`ModeDetected`, not a decoder property). Also opportunistically wired the status bar's Buffer readout
(`RxImagePaneViewModel.BufferedSampleCountDisplay` already existed from batch 1, just wasn't
connected to this second display site) — deliberately drops mock2's own "· N XRUN" half rather than
pairing a real number with a still-fake one. Two rounds of auditor review, no blockers; one real risk
fixed (a missing property-change notification that could show a stale/wrong line total for one frame
after a fresh mode detection).

**Batch 3 SHIPPED (2026-08-09)**: RX capture device name (new `ISstvSessionService.GetConfiguredCaptureDeviceNameAsync`,
exact mirror of the existing TX-side `GetConfiguredPlaybackDeviceNameAsync` pattern), and the
Signal-quality card's "Clip lo/hi" readout (new `LuminanceClipStatistics` utility — deliberately
non-legacy image-domain pixel-luminance arithmetic, not audio DSP, using standard ITU-R BT.601 luma
weights). Two rounds of auditor review; round 1 caught a real bug in the new clip-stat math: it
computed over the WHOLE mode-sized canvas including not-yet-decoded rows (which are zeroed `Rgb24`
— pure black), so a live decode showed a wildly wrong "mostly clipped black" reading for the entire
time a user watched it, only becoming accurate on the final line. Fixed by row-limiting the
computation to `Progress * Height` rows, with the readout showing "—" while idle rather than a
misleading number.

**Batch 4 SHIPPED (2026-08-09)**: RadioHeaderView's UTC clock (new `RadioStatusViewModel.UtcClockDisplay`,
a 1s `DispatcherTimer` tick, matching `RxImagePaneViewModel`'s own telemetry-poll pattern), Input-chain's
AGC gain (`RxImagePaneViewModel.AgcGainDisplay`, pure client-side derivation from the already-real
`SignalPeakLevel` per `LevelAgc.cs:103`'s exact formula — no new backend property, splitting the
formerly-combined "Notch·AGC" row into two separate rows so AGC could go real without implying Notch
was real too), status bar's "frames today"/"log size" (new independent counts on
`RxHistoryPaneViewModel`/`LogbookPaneViewModel` respectively — deliberately decoupled from each pane's
own current search/filter state, both loaded once at construction, best-effort), and Frame-metadata's
"Size on disk" (new `IReceivedImageBuffer.Saved` event, raised by the concrete `ReceivedImageBuffer.SaveAsync`
after its write completes — the original REAL-EASY citation above assumed `IReceivedImageBuffer` itself
already had a save-completion hook a live pane could subscribe to; on inspection it didn't, since the
sole production writer, `ReceiveHistoryRecorder`, is a wholly separate class in a different layer with
no reference back to whatever pane is displaying `Current` — so this needed one new interface member,
not just a wiring pass, closer in scope to batch 3's capture-device work than a one-line add). Two
rounds of auditor review; fixed two pre-existing `RxHistoryPaneViewModel` tests broken by the new
frames-today query (an extra `QueryAsync` call at construction the tests' filter-count assertions
hadn't accounted for).

**Real bugs caught by auditor review, both fixed before shipping**: (1) round 1, a genuine
UTC-vs-local blocker — `LoadFramesTodayCountAsync` anchored its query to UTC midnight on the false
assumption that `ReceivedAt` is always stored UTC, but `ReceiveHistoryRecorder` actually writes
`DateTimeOffset.Now` (local offset), and `SqliteReceiveHistoryStore`'s date-range filter is a
lexicographic TEXT compare on `ToString("O")` that only stays correct when the query's own offset
matches the stored rows' — the UTC-anchored query silently missed/double-counted several hours of
frames around every day boundary on any non-UTC machine. Fixed to match `ShowTodayOnly`'s own local
`DateTime.Today` convention (the two filters are now genuinely consistent, collapsing what an earlier
version of this doc/code wrongly called "a separate, pre-existing inconsistency"). New store-level
test (`SqliteReceiveHistoryStoreTests.QueryAsync_DateRangeCompareIsLexicographicOnStoredOffset_NotInstantBased`)
proves the lexicographic-compare hazard directly. (2) round 1→2, a save/restart ordering race in
`RxImagePaneViewModel`'s new "Size on disk" readout — a round-1 fix (a same-class counter captured at
the `Saved` event's own callback-entry time) only narrowed the race window instead of closing it: the
part that mattered was the encode+write+recorder's-own-directory-resolve window *before* `Saved` ever
fires, and a `ModeDetected` landing there (the LIKELY interleaving for back-to-back bulk-WAV-decode
restarts, not an edge case) would already have bumped a same-class counter before the round-1 handler
ever ran. Round 2 fix: `IReceivedImageBuffer` itself now owns a `Generation` counter (bumped on
`ModeDetected`/`DecodeRestarted`, the two events that change `Current`'s identity), captured at
`SaveAsync`'s true invocation time and threaded through `Saved(path, generation)` — the pane compares
that captured value against the buffer's own then-current `Generation` instead of a second,
independently-drifting counter. Also fixed, same round: a `Saved` subscriber's own exception could
fault the `Task` that `ReceiveHistoryRecorder.RecordCompletedImageAsync` awaits, silently losing the
history row for an image that had, in fact, saved successfully — isolated with a try/catch inside
`ReceivedImageBuffer.SaveAsync` (new `ILogger<ReceivedImageBuffer>` dependency added for this). Round
3 (the auditor's own verification of round 2's fix) confirmed the generation-token race is genuinely
closed — captured under the same lock as the buffer's own snapshot, provably before any encode/write
work runs — with one accepted residual sliver (a `ModeDetected` landing in the recorder's own
pre-`SaveAsync` setup, no image work, is still invisible to the guard; documented in
`RxImagePaneViewModel.OnSaved`'s own doc comment, not worth the extra plumbing to close for a cosmetic
readout) and one comment-accuracy fix (the doc comment had wrongly implied the recorder's own
directory-resolve step was covered by the guard; corrected).

**Batch 5 SHIPPED (2026-08-09)**: Buffer·XRUN. Promoted `MiniAudioEngine.CaptureOverrunCount` onto
`IAudioEngine` itself (was deliberately concrete-class-only), threaded a pass-through through
`ISstvSessionService`/`SstvSessionService`, and recombined the status bar's/Input-chain's Buffer
readout back into mock2's original single "buffer 512 samples · 0 XRUN" format now that both halves
are real (batches 1/2 had split them apart specifically because XRUN was still hardcoded — that
reason no longer applies). Two rounds of auditor review caught 3 real risks, all fixed: (1) the new
`SstvSessionService.CaptureOverrunCount` pass-through was reachable from `RxImagePaneViewModel`'s
250ms polling timer with no guard against a documented `MiniAudioEngine` race — a concurrent Stop RX
disposing the capture session mid-read can throw `ObjectDisposedException`, which a `DispatcherTimer`
tick has nowhere safe to land (this port's global unhandled-exception handler only logs, doesn't
recover) — fixed by absorbing the exception at the `SstvSessionService` layer and returning `0` (the
already-documented "not running" contract value, not a masked failure). (2) The interface doc
comments claimed "0 when not running"/"safe to read from any thread" while the one real
implementation could throw — fixed by making the `ISstvSessionService`-layer claim genuinely true
(since (1) now absorbs the race there) and correcting the lower `IAudioEngine`-layer claim to
honestly document the exception risk instead. (3) `FakeAudioEngine.CaptureOverrunCount` was a bare
settable property that didn't enforce the same contract the real engine does (0 when not capturing,
reset on a fresh `StartCaptureAsync`) — fixed to match, closing a gap where a test could pass against
a state the real engine can never produce.

**Batch 6 SHIPPED (2026-08-09)**: Auto-correct's "on/off" half. Full plan + auditor plan-review process
(not the lighter batches 1-5 review), since this genuinely touches `AnalogFmSstvDecoder`'s decode path,
not just UI wiring. New `SstvDecoderSettings.AutoSlantEnabled` (mirrors the 4 already-shipped sibling
toggles' exact pattern — `AfcEnabled`/`SyncRestartEnabled`/`AutoSyncEnabled`/`AutoStopEnabled` —
restart-only, no live-reconfigure), threaded through `AnalogFmSstvDecoder`/`RestartableSstvDecoder`/
`ISstvDecoder`/`ISstvSessionService`, gating `ApplySlantTracking`'s commit branch (routes to
`ProcessLineHistoryOnly` instead of `ProcessLine` when off, exactly mirroring the sibling
`_slantCorrectionsDisabledForRestOfImage` gate already there). `RxImagePaneViewModel.AutoCorrectDisplay`
rewritten from a 2-way (locked/not-locked) to a 4-way state (AVT literal `"—"` / Off / Locked / on-not-
-locked), fixing a real pre-existing readout gap where "off" had no distinct state at all.

**Plan-review round found a real, un-scoped legacy-parity bug**: `KRSA->Checked` (legacy's AutoSlant
checkbox) is ALSO read at a completely separate call site — `Main.cpp:3910`/`:3917`, inside Auto
Sync's own branch-1 threshold (`(KRSA->Checked ? 5 : 2) * m_Mult`), not just at the slant-commit
block this batch originally set out to gate. This port had hardcoded the `5` side unconditionally,
correct only because no Auto Slant toggle existed yet to ever make the `2` side reachable — fixed as
part of this batch (not deferred), with a dedicated regression test verifying the exact 5:2 ratio
directly rather than trying to empirically tune a real-audio splice to distinguish the two threshold
values (both move branch 1's trigger window in opposite directions at once).

**A second real design misconception surfaced mid-implementation, caught by a test actually failing**:
both the plan and the plan-review auditor assumed "with the toggle off, `SlantPpm` stays null." Wrong
— `SlantTracker.DriftPpm` defaults to `0.0` (a real, non-null value) from construction, regardless of
the toggle, since `_currentSampleRate` starts equal to `_sampleRate` and is only ever reassigned by an
actual commit. The correct invariant is "`SlantPpm` never MOVES away from `0.0`," not "stays null" —
fixed in the test and every doc comment that repeated the wrong claim (took **two** attempts: the
first correction narrowed the claim to "only reachable in a brief pane-construction startup window,"
which the post-implementation code-review round caught as ALSO wrong — `SlantPpm` is null for a
decoder's entire IDLE period, not just a startup window, since `EndOfImage`/`AbandonInProgressImage`
both null the decoder's mode between every reception). Two rounds of post-implementation auditor
review also caught: a `RestartableSstvDecoderTests` forwarding test that could pass vacuously (fixed
with a positive control proving the same scenario DOES commit with the toggle on, at the wrapper's own
pinned 11025Hz rate — none of `SlantTests.cs`'s own commit scenarios exercise that specific rate); an
untested `OnPropertyChanged` re-raise call; and an AVT-vs-toggle test that never actually exercised the
states its name claimed to cover.

Remaining, only with explicit product decisions made first: "Source" (detection-method labels), Advanced
timing (relabel as static reference vs. drop the card section), "Reset" button semantics, and
squelch (a real, narrow, repeater-scoped legacy feature exists — decide whether/how to generalize
it before building, per the corrected verdict above; note `CLMS::Sig`/`m_repsig` may also be a
cheaper legacy-grounded path toward the broader Signal-quality card than the TIER-B SNR framing).

Everything else on this page (true SNR/noise-floor, notch state, true stereo, dropped lines,
black/white tone, RX gain) is either a real new DSP/feature build or not possible as described — do
not schedule these as "quick wiring" work.
