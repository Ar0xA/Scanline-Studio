# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08, four times on 2026-08-11, and again 2026-08-12 (was ~291 lines) — the "wire what you
can" Options-dialog pass that filled most of the previous version is DONE and fully captured
elsewhere: each shipped control has its own detailed commit message (`git log --oneline` shows them
in order: `13a8ca7` Open in Log, `4f14396` QRZ lookup, `e132044` Auto-stop/Auto-restart,
`a73b21e` Sense level, `5d7a4cf` Remember-window-position, `33774e6` Audio tab,
`0d90b58`/`3caee2c`/`cf20a61` Radio/CW-ID/Advanced scoping), and the durable per-control state lives
in `spec/16-gui-wiring-survey.md` (wiring inventory) and `spec/14-roadmap.md` (backlog + research).
Nothing lost — see git history for this file if older narrative is ever needed.

## Resume here (2026-08-12, ACTIVE) — CW-ID / FSK station-ID subsystem, Phases 1-4 DONE, starting Phase 5

**Status: Phases 1-4 complete and committed.** Phase 1 (`26f674f`): silence representation in
`AnalogFmSstvEncoder.cs` + `CwMorseGenerator.cs`. Phase 2: `FskStationIdEncoder.cs` +
`FskStationIdWireFormat.cs` (TX FSK-ID packet + NR/RST sub-packet) — auditor independently
re-derived the exact same golden-vector bytes by hand. Phase 3: `NarrowFskHeaderDecoder.cs` extended
with the station-ID continuation (legacy modes 5-10) + `AnalogFmSstvDecoder.cs`'s
`TryNarrowFskScan`/`TryDecodeNarrowModeHeader` consumer-contract changes (station-ID results must
NOT abort AVT training/header scanning the way a real mode-announce lock does) — 2 review rounds,
round 1 found the phase's single most important gap (the discrimination logic was correct by review
but had ZERO test coverage), closed with a real end-to-end test. Phase 4 (settings data layer + TX
pipeline wiring) — 2 auditor review rounds, both closed clean, committed:
- New `StationIdSettings.cs`/`CwIdMode` enum (Core.Sstv) + `StationIdSettingsJsonContext.cs` —
  persisted settings, nullable-with-documented-default pattern for fields whose legacy default isn't
  the CLR default (`CwWpm`=28, `CwToneFrequencyHz`=1000, `NrRstEnabled`=true).
- New `StationIdTransmitOptions.cs` (Abstractions.Sstv) — the resolved-per-transmission DTO crossing
  the Application/Core.Sstv boundary; `ISstvEncoder.EncodeAsync` gained an optional
  `stationId` param (placed before `ct`, so ~90 existing 2-arg call sites are unaffected).
- `AnalogFmSstvEncoder.cs`: footer-branch selection (`GenerateFooterSegments(mode, fskIdEnabled)`,
  the previously-deferred FSK-ID-configured branch is now implemented), post-image FSK-ID/CW-ID
  segment append, and the settings-boundary callsign normalization (`Option.cpp:445-448`, exact
  truncate-then-uppercase-then-trim order) + NR/RST raw-text length cap — all scoped to this file
  only, Phase 2/3's already-reviewed internal types untouched.
- `SstvSessionService.cs`: new `IMacroTextResolver` constructor dependency, `TransmitAsync` now
  async and resolves `StationIdSettings`+`OperatorSettings` into a `StationIdTransmitOptions` per
  call (`ResolveStationIdTransmitOptionsAsync`), including WPM/tone-frequency settings-boundary
  validation (Phase 1 code-review finding: invalid values fall back to the documented default rather
  than reaching the generator or aborting TX).
- 7 new Core.Sstv tests (footer branch + full encode-then-decode wiring, including a real finding:
  post-`EndOfImage` narrow-FSK scanning needs the SAME `MaxSearchCeilingMs` trailing-silence padding
  Phase 3's tests already required — a general pre-lock-scan-gate-re-arms-every-image property, not
  new) + 10 new Application tests (settings-resolution gates/fallbacks, via a new
  `FakeSstvEncoder.LastStationIdOptions` capture field). 731/731 Core.Sstv tests pass, 83/83
  Application tests pass, full solution builds clean.
- Local peer-audit run (row 4, `tools/peer-audit/`, logged): template-echo failure again (now
  3-for-4 non-functional on this feature) but surfaced a float-vs-double numeric-fidelity claim,
  relayed to the real auditor for independent verification — refuted (false positive), confirming my
  own read.
- **Auditor code-review round 1 (returned): EQUIVALENT-WITH-RISKS, 2 real [risk] findings, both
  fixed**: (a) `CapNrRstTextForStationId` capped the RAW NR/RST text before filtering instead of
  after — separators get removed by `FilterNrRstChars`, so raw length wasn't a safe proxy for
  filtered length and could flip compact-vs-string wire form for real exchange text; fixed by
  filtering first, then capping. (b) `StationIdSettings.FskIdRxEnabled` was a fully dead setting —
  nothing ever wired it to a decoder despite Phase 3's own doc comments promising "Phase 4 wires
  this." Fixed: added `bool StationIdDecodeEnabled { get; set; }` to `ISstvDecoder` (deliberately
  live-settable, unlike the restart-only `AutoSlantEnabled`-style toggles), `RestartableSstvDecoder`
  now stores + preserves it across its own periodic inner-decoder rebuild, and
  `SstvSessionService.StartReceivingAsync` applies it from settings on every RX start. Also fixed 3
  nits (stale VOX comment, missing 78-char CW-ID text cap per `Main.cpp:6972-6974`, missing test
  coverage for `CwIdMode.SoundFile`'s gate and the truncate-then-trim callsign ordering) and accepted
  2 as-is (CP932-byte-vs-char divergence, footer-carrier-source nit — both already the "evidently
  intended" behavior per the auditor's own round-1 read). 6 new tests added across the fix set;
  735/735 Core.Sstv tests, 87/87 Application tests, 28/28 Imaging tests, 78/78 Logbook tests (the
  latter two needed their own `FakeSstvDecoder` copies updated for the new interface member) — all
  green.
- **Auditor code-review round 2: EQUIVALENT-WITH-RISKS, ready to commit — closed clean, no round 3
  needed.** Independently re-derived the NR/RST fix by hand from source (confirmed correct); traced
  the footer-carrier `m_TW` provenance further than round 1 (a genuine legacy RX-vs-TX-mode quirk,
  confirmed the port's TX-mode choice is still the right call, added a clarifying comment); caught
  one real gap of its own — the two swap-survival tests asserted only the wrapper's own stored
  field, which would pass even if the fix were reverted — fixed with a new
  `RestartableSstvDecoder.InnerStationIdDecodeEnabledForTests` accessor reading the live inner
  decoder directly; caught a 78-vs-77-char off-by-one in the CW-ID text cap (`MacroText`'s real break
  condition, `Main.cpp:10829`) and a stale "Phase 4 wires this" doc-comment forward-reference — both
  fixed. Final state: 735/735 Core.Sstv tests, 87/87 Application tests, both full solution builds
  clean throughout. **Committed.**

Next: Phase 5 (RX consumer / auto-fill wiring — `RxImagePaneViewModel.OverrideCallsign`, two
different gates for callsign vs NR/RST writes, no automatic QRZ lookup). Note for Phase 5:
`RestartableSstvDecoder` doesn't forward the `StationIdDecoded` event at all yet (only the enable
flag was in Phase 4's scope) — that forwarding is Phase 5's job, not a gap in Phase 4.

v1 scope confirmed with user: FSK+CW+NR/RST, sound-file deferred, `.ini` import deferred.

Also note: `tools/peer-audit/` contributed real value in Phase 1-2 (caught 2 tool bugs via actual
use, found genuine issues on Phase 1's code) but has now gone 0-for-2 on Phases 2-3's larger
candidate files (context-exhausted or template-echo failures) — see
`tools/peer-audit/TRACKING.md`'s row 3 notes. At row 3 of the 5-row review checkpoint; worth
deciding at checkpoint whether `NUM_CTX` needs raising or peer-audit's real ceiling is
small-file-only.
Two product decisions also confirmed with user: CW-ID speed follows the configured WPM (fixing an
apparent legacy bug where it's effectively pinned to a default), and no automatic QRZ lookup fires on
FSK-ID decode (fill the callsign field only). Full plan (exact legacy citations, phase-by-phase file
lists, tests, both review rounds' corrections folded in) lives at
`/home/artien/.claude/plans/abundant-weaving-lark.md` — **read that file first on resume**, this
section is just status, not a duplicate of the plan content.

Both plan-review rounds found and fixed real protocol-level errors by reading legacy source directly
(round 1: wrong NR predicate, wrong CW-timing citation, missing TX-encoder silence representation,
under-specified RX-decoder return-type change; round 2, independently re-verifying round 1's fixes:
wrong NR/RST auto-fill gate — was using the callsign's gate for both, they're different predicates —
plus a missing `AnalogFmSstvDecoder.TryNarrowFskScan` consumer-contract note that would have silently
broken AVT training/header scanning, plus two missing Morse special-cases (`/` and `.`)). Wire
protocol is now independently confirmed correct in every byte-level detail checked.

Task list has all 6 phases pre-created (`Phase 1` = silence representation + CW Morse generator,
... `Phase 6` = Options/Transmit UI + e2e) — see current task list, don't recreate. Phase 1 is
`in_progress`.

**What this is**: the next item off the Must-implement backlog, picked explicitly by the user (not
autonomously) after the "wire what you can" pass finished everything wireable in the Options window.
Real, working legacy feature (TX Morse/FSK station ID + RX FSK-callsign decode) with zero
replacement built in this port.

**Status: TX+RX research phase DONE, no code written yet.** User asked to see legacy's real behavior
on both sides before any implementation — that research is complete and written up in full in
`spec/14-roadmap.md`'s "CW-ID / FSK station-ID subsystem" entry (exact line citations for
`OutputFSKID`/`OutputCWID`/`OutputMMV`/`WriteCWID`/`WriteFSK`/the RX `DecodeFSK` state machine).
**Read that entry first when resuming** — don't re-derive, it's current and detailed. Headline
findings, so this file doesn't need to repeat the full research:
- TX side is 3 independent, combinable mechanisms (FSK callsign packet + optional serial-number
  sub-packet, CW Morse, sound-file playback), triggered right after the image's last line.
- RX side is ~60% already built: `NarrowFskHeaderDecoder.cs` already implements the entire shared
  FSK bit-sync front half (it decodes a *different* packet type today — mode-announce, not station
  ID — but the low-level machinery is identical); only the station-ID payload continuation is
  missing, not a from-scratch decoder.
- This port already has reusable pieces: `VisHeader.cs`'s FSK timing constants + a TX segment-
  generator pattern, `MacroTextResolver.cs` (covers `%m`/`%D`/`%T`, not yet his-callsign/QTH/RST
  tokens), `OperatorSettings.Callsign`.
- Bundled full scope (TX Morse generator + TX/RX FSK-ID codec + NR sub-packet + Identification tab
  UI) is comparable in size to the QRZ lookup feature (`4f14396`) — needs its own dedicated
  plan → auditor-plan-review → implement → auditor-code-review arc, not a quick wiring pass.

**Interop-safety guardrail — load-bearing, repeat this in any plan/subagent prompt for this
feature**: many real users run legacy YONIQ/MMSSTV. The WIRE PROTOCOL (STX/EOT byte values, XOR
checksum, bit timing, the 6-bit character encoding/ASCII-offset scheme) must stay byte-for-byte
identical to legacy — that's what lets a legacy station decode our FSK-ID and vice versa. Everything
else is safe to extend freely: richer macro tokens, auto-filling the QSO log from a decoded FSK-ID,
UI polish — all "local text in/out," none of it touches the protocol. The one real risk is the
character set (legacy's 6-bit encoding has no bounds-checking spotted in the decode, so
out-of-range characters would desync a real legacy receiver) and inventing new packet types (only
Scanline-Studio-to-Scanline-Studio would understand them, not legacy).

**User has flagged wanting to keep an eye on possibly expanding CW-ID/FSK-ID capability later**
(e.g. richer macro tokens for CW-ID text, auto QSO-log fill from a decoded callsign) — noted as a
real interest, not a commitment yet. Fine to design room for later, as long as it stays on the safe
side of the interop guardrail above; don't let "leave room to expand" become an excuse to invent new
wire-protocol behavior now.

**Next action on resume**: no plan written yet. Next step is either (a) go straight to scoping an
implementation plan (research is complete enough to start), or (b) ask the user for scope
boundaries first (e.g. is sound-file ID in scope for v1, or just FSK+CW; is the NR/RST sub-packet
wanted). Given this is a real feature-design decision, don't default-pick — ask, matching this
project's established "big design choices get a plan-review pass" convention (`CLAUDE.md`
§7/`feedback_audit_ui_design_before_building` memory).

## Other deferred items (scoped, not started — full citations in the docs named)

Each of these was investigated this session and needs its own dedicated session (DSP/architecture
rigor, not ordinary wiring) — see `spec/16-gui-wiring-survey.md`'s per-tab sections and
`spec/14-roadmap.md`'s "Options dialogs" bullet for full citations on each:
- **RX BPF** (Decode tab) — Kaiser/Bessel filter math is reusable from `TxOutputBandpassFilter`, but
  needs per-tap-count sync-anchor-correction re-derivation with golden-vector tests.
- **Demod type** (Decode tab) — runtime dispatch between 3 already-ported demodulators; biggest/
  riskiest of the DSP items.
- **RX buffer** (Decode tab) — needs a whole buffered-line-replay subsystem built first; wiring the
  UI control alone today would be a fake no-op.
- **Auto-start** (Decode tab) — needs a design pass to find the right internal "disarmed, still
  live" gating point across multiple sync-detection branches.
- **Advanced tab** — PLL/Zero-crossing tuning is gated on Demod-type above; TX BPF/LPF toggles are a
  small, bounded change but have real over-the-air spectral consequences; Loopback/calibration
  wizards are fully unbuilt.

Resolved, not open items: RTS-on-RX (obsolete, removed feature, `docs/removed-features.md`); VOX/
Sound-file ID (bundled into CW-ID/FSK above); JPEG save quality (bundled with the Gallery's
still-stub "Export frame" button, not a standalone Options control — separate small item, not on
this list).

## Current wiring snapshot

`spec/16-gui-wiring-survey.md` (~286 controls tracked): **~147 REAL, ~94 STUB, ~46 FAKE-LIVE,
~1 PARTIAL**. Densest remaining gaps: Receive tab's Sync&Slant/Input-chain/Signal-quality cards (no
live audio-chain measurement exists for most of these — real new DSP work); the 5 deferred items
above; Transmit tab's Queue/TX-log/Recently-sent cards (100% stub, no such features exist); TX image
editor's canvas-overlay safe-area/callsign/report-plate text (FAKE-LIVE, the most deceptive
placeholder in the app). Remaining PARTIAL: RxFrameMeta's Note `TextBox` (needs a real backing field
on the frame/session model).

## Established process (proven across many prior batches, reuse it)

research → plan → auditor plan-review (2 rounds for anything touching decode-path/concurrency/
schema; skip for pure UI-plumbing with no DSP/concurrency risk) → implement → **local peer-audit**
(`tools/peer-audit/`, free/fast, log to `tools/peer-audit/TRACKING.md` — added 2026-08-12, see
CLAUDE.md §7b; review checkpoint at 5 logged uses) → auditor code-review (soft cap ~3 rounds —
round 1 finds real blockers, round 2 catches an incomplete fix, round 3 usually closes it, loop in
the user rather than a round 4) → verify (build + full relevant test suite, real-window
screenshot/DB-level check if UI-visible) → commit → push. Escalation path if stuck: ask the
auditor; if the auditor also can't resolve it, log to `spec/14-roadmap.md`'s "Verify later with
human" section rather than stalling.

Real-window testing has caught bugs build+tests never would this session (twice, both in the
window-geometry work: a startup deadlock and a cross-thread UI-property crash) — keep testing the
actual running app for anything touching lifecycle/threading, not just unit tests.
