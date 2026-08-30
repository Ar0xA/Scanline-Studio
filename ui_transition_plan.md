# UI transition plan — from ui_findings.md (second pass), source-audited 2026-08-28

Working plan. Findings references (T1-n/T2-n) map to `ui_findings.md`'s original tiers.

## Status (2026-08-30)

Steps 1-13, 15, 16 are **done**, committed, and pushed to `master`. Step 14 is **rejected outright**
(final user decision, not deferred). Detailed how-it-was-built notes for the done steps have been
pruned from this file — the code itself now carries the load-bearing comments (e.g.
`MainWindow.axaml:48` for step 1, `TemplateStore.cs`/`ITemplateStore.cs` for step 13); `git log`
has the commit-by-commit history. This file keeps only what is still actionable or still a live
design record: step 14's rejected-but-documented design, the Tier 3 scope-decision log, and the
DSP/concurrency call-outs that set review cadence precedent for similar future work.

## Sequencing

1. Workspace usable below 1920 DIP (T1-1) — **done**. `MainWindow.axaml` MinWidth relaxed to 1280.
2. SEND panel honesty + contextual Ctrl+S (T1-2, T1-3) — **done**. `ApplyAndTransmitCommand` added;
   Ctrl+S now dispatches per active tab, RX export moved to Ctrl+Shift+E.
3. Full-size RX/history viewer + live Previous-Frames thumbnails (T1-5 + T2-6) — **done**.
   `ImageViewerWindowView(+Model)`.
4. Per-item RX history delete (T1-4, reframed — no automatic retention cap, see
   `docs/removed-features.md`) — **done**. `SqliteReceiveHistoryStore.DeleteAsync`.
5. QSO handoff: RX → TX → Logbook (T1-6 + T2-8 merged) — **done**. Prefill carries frequency/mode,
   form displays MHz, `MacroTextResolver` resolves a current-contact source for TX QSO-FILL.
6. Latch per-frame metadata (T2-4) — **done**, auditor-reviewed (decode-path state per CLAUDE.md
   §7). `RxImagePaneViewModel._latchedFrequencyHz`/`_latchedRigMode`.
7. Options: Apply-without-close, Connect against form values (T2-3, corrected scope) — **done**.
8. Sound-file ID validation at selection/save time (T2-2) — **done**.
   `ISstvSessionService.ValidateStationIdSoundFileAsync`.
9. Label/help cleanup + collapse Decode Activity + right-click discoverability (T2-9, T2-10, T2-11,
   T2-7 naming) — **done**.
10. Persistent RX mode lock (T2-5) — **done**, auditor-reviewed (decode-path state).
11. Frequency entry / honest read-only header (T2-1) — **done**. Inline MHz entry on the header
    readout via `FrequencyInputMhz`/`IsEditingFrequency`.
12. Auto-save RX audio (Options flag + per-frame WAV + retention) — **done**, auditor-reviewed (4
    plan-review rounds, decode-adjacent hot-path state). Design superseded the original
    `Saved`/`Recorded`-ordering assumption; see
    [docs/plans/step12-auto-save-rx-audio-plan.md](docs/plans/step12-auto-save-rx-audio-plan.md)
    for the corrected `ReceptionSequence`-keyed correlation design (kept as reference since the
    original assumption's failure mode is a real trap for similar future correlation work).
13. Scanline template bundle export/import (T1-7, half A) — **done**. `.sstemplate` = zipped
    `TemplateStore` folder; `ITemplateStore.ExportAsync`/`ImportAsync`.
14. Legacy `.mtm` template import (T1-7, half B) — **rejected 2026-08-29**, see below and
    `docs/removed-features.md`.
15. QSO delete + duplicate detection + QSL flags (Tier 3, accepted 2026-08-29) — **done
    2026-08-29**.
16. Copy / open-externally for received images (Tier 3, accepted 2026-08-29) — **done 2026-08-29**.

OmniRig client backend (Tier 3, accepted 2026-08-29, **done 2026-08-29**) was never a numbered step
here — it was CAT-layer/backend work outside this doc's UI-findings-driven scope. Tracked at
`spec/03-cat-layer.md`'s "Definition of done" and `spec/14-roadmap.md`.

## Step 14 — Legacy `.mtm` template import (rejected)

`.mtm` is import-only in this design; there is no `.mtm` export (Scanline's element model has no
group/OLE/line equivalent and excludes perspective/vertical/gradient text — a writer would be lossy
where fidelity matters most). "Both directions" was meant to be satisfied by `.mtm` import plus the
already-shipped `.sstemplate` export/import (step 13).

**Rejected outright, 2026-08-29** — explicit, final user decision, not a technical blocker. Before
rejection, the format was confirmed fully specified (not an "unreverse-engineered" gate, contrary to
an earlier, now-corrected claim in `spec/15-template-designer.md`):
`yoniq-old/YONIQ-main/Draw.cpp`'s `CDrawGroup::SaveToStream`/`LoadFromStream` (lines 5344-5385
file-is-one-group, 4963-5004 container loop with `int32` command dispatch against `CM_*` in
Draw.h:80-88, 408-454 base record including a `0x55aa0000`-tagged optional `m_BoxStyle` block,
501-524 length-prefixed CP932 strings, 456-486 an embedded VCL `TBitmap` stream). `.mti` (single
template item) rides the identical parser, differing only in the file-dialog filter string
(`Main.cpp:10090/10134`) — its rejection is moot alongside `.mtm`'s.

Two other things ruled out committing legacy sample files (`def1-5.mtm`, `Stock/*.mtm`,
`Current.mtm`) as test fixtures even if the feature had been accepted: YONIQ's own `License.TXT`
calls the *program* freeware under the author's copyright (distinct from the LGPL source license),
and the files embed author-created bitmap artwork.

`docs/removed-features.md`'s "Legacy `.mtm`/`.mti` template import" entry and
`spec/15-template-designer.md`'s Decisions/Deferred sections carry the corresponding correction —
this section is kept here as the design record in case the decision is ever revisited.

## Tier 3 — scope decisions for the user (accept / defer / reject, one line each)

- QSO delete + duplicate detection + QSL flags: **accepted 2026-08-29, done 2026-08-29** (step 15).
- Camera/webcam TX source: **rejected 2026-08-29** — see `docs/removed-features.md`.
- Copy / open-externally for received images: **accepted 2026-08-29, done 2026-08-29** (step 16).
- Print for received images: **rejected 2026-08-29** — user can print from whatever app opens the
  externally-opened image (see above). Not a legacy capability, so no `docs/removed-features.md`
  entry.
- Legacy `.mti` import alongside `.mtm`: **moot, rejected with step 14** — see
  `docs/removed-features.md`.
- Legacy `.mtm` **export** (write, not read): moot — the whole import effort is rejected.
- Richer Gallery filters/sort/bulk actions: **deferred 2026-08-29** ("maybe later," not scheduled).
- External logger / live current-QSO integration beyond ADIF UDP: **no action needed** —
  `ScanlineStudio.Core.Logbook` already ships batch ADIF file export/import AND a generic
  ADIF-over-UDP live streamer (`AdifUdpStreamer.cs`/`AdifUdpStreamingSettings.cs`, the broadcast
  standard GridTracker/N1MM/Log4OM already understand). A bespoke native integration with one
  specific app goes further than that and conflicts with the project's minimal-footprint logbook
  decision (`feedback_logbook_minimal_footprint` memory) — reject unless a concrete need for a
  specific app's native protocol appears later.
- OmniRig client backend: **accepted 2026-08-29, done 2026-08-29** — see `spec/03-cat-layer.md`.
- Waterfall/spectrum palette customization: **re-confirmed rejected 2026-08-29** — same feature as
  the already-shipped-and-removed "7 per-element waterfall/spectrum colors"
  (`docs/removed-features.md`); a deliberate SDR-style gradient replaced legacy's per-element
  pickers.
- QSSTV digital SSTV / DRM / hybrid FTP / repeater: **rejected 2026-08-29** — see
  `docs/removed-features.md`.

## DSP/concurrency review-cadence precedent

These steps touched decode-path or decode-adjacent per-reception state and required full auditor
plan-review + code-review cadence (CLAUDE.md §7), not a lighter UI pass — kept as precedent for
judging similar future work:

- Step 6 (metadata latch) and step 10 (RX mode lock): decode-path per-reception state.
- Step 12 (auto-save audio): a 5th cross-thread `SamplesCaptured` fan-out target on the audio drain
  thread plus a new identity-keyed `Recorded`/`AudioSliceReady` correlation — a hot-path handler,
  despite the user-facing surface being one checkbox. Took 4 plan-review rounds across two
  reviewers because the original correlation assumption (`Saved` always fires before `Recorded` for
  the same frame) turned out false.
- Step 4's store `Deleted` event and step 2's cross-VM transmit event are cross-thread event paths;
  both marshal via Dispatcher like the existing `Recorded` event does (CLAUDE.md §4 concurrency
  rule).
- Steps 13-14 (template bundle/`.mtm` import) are file I/O and parsing, not decode-path — a lighter
  UI-style review was appropriate, but step 14's design still called for golden-fixture tests
  against real `.mtm` files given the format's version-gated branching (moot now it's rejected).
