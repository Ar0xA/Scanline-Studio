# 18 — Path to 1.0

## Related

[[14-roadmap]] (this supersedes its Tier 0/1 "DONE" status — see "Relationship to 14-roadmap" below)

## Provenance

Produced 2026-08-15 from a full milestone audit (`docs/audit-playbook.md`) — 8 parallel `auditor`
agent passes, each independently verifying against current source (not roadmap prose, which this
session repeatedly found stale): DSP core demodulators/sync, RX per-mode decode paths, TX per-mode
encode paths, RX buffer/replay/Correct-Slant, the TX image editor pane, Receive-tab UI wiring,
Transmit-tab UI wiring, and a broader "what's missing outside those seven units" sweep (packaging,
crash resilience, first-run, licensing, CI, test-suite health). Every item below cites its
supporting evidence (file:line); nothing here is a guess. **Crash resilience: audited, no
findings** — `Program.cs:290-291` handles both `AppDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException` before any service resolves; not listed as its own item
below because there's nothing to fix.

**Plan-reviewed 2026-08-15** (GO WITH CHANGES) — 14 citations spot-checked against current source,
all correct. Changes from that review are folded into the text below (item 1's playback-device
coupling, the rigctld-verification-scope correction, items 5/11 merged, item 7's TX-pill/Locked-
toggle additions, item 3's second citation, item 8's wording).

**Explicit exclusion by user instruction**: items whose only purpose is yoniq-compatibility (not
needed for a working 1.0 SSTV app) are listed in "Parked" at the bottom, not mixed into the active
tiers, even where a legacy feature is real and well-documented.

## Relationship to `[[14-roadmap]]`

`14-roadmap.md`'s own Tier 0/Tier 1 sections say "DONE, 2026-08-14." This audit found that claim no
longer holds — Critical item 1 below (transmit broken with the default radio backend) is a
core-loop regression that postdates that DONE marking. Treat *this* document as the current
source of truth for 1.0 priority; `14-roadmap.md`'s Tier 2 backlog (UI placeholders, CAT protocol
work, localization, etc.) remains valid and unaffected — only its Tier 0/1 "done" claim is
superseded.

---

## 🔴 Critical — blocks the core loop today

1. **Transmit is impossible with the default ("none") radio backend, AND with no playback device
   configured.** `RadioConnectionSettings.BackendId` defaults to `"none"`
   (`src/ScanlineStudio.Core.Radio/RadioConnectionSettings.cs:14,49`) →
   `NoneRadioProtocol.SetPttAsync` throws (`NoneRadioProtocol.cs:36-37`). `PlayWithPttAsync` keys
   PTT *before* generating any audio, with no capability guard
   (`src/ScanlineStudio.Application/SstvSessionService.cs:552-560`) — the exception propagates,
   zero audio is ever produced, user sees only "Transmission failed. Check your radio and audio
   device settings." Breaks VOX/manual-PTT/no-CAT operation, i.e. the majority SSTV case, and
   directly contradicts `spec/02-radio-layer.md`'s "must work with zero radios connected"
   commitment. **No test catches this** — every fake radio backend in the test suite no-ops
   `SetPttAsync` (`tests/ScanlineStudio.UI.Tests/Fakes.cs:334`,
   `tests/ScanlineStudio.Application.Tests/FakeRadioSessionService.cs:32`). **Coupled to item 8**
   (plan-review finding): `ResolveDeviceAsync(forCapture: false)` runs at
   `SstvSessionService.cs:548`, *before* the PTT call at `:552`, and throws the identical way for a
   null `PlaybackDeviceId` (`:745-749`) — a PTT capability guard alone does not fix a fresh install
   with no playback device configured either. **Real acceptance criterion**: fresh install, no
   `settings.json`, backend=`"none"` → Transmit produces real audio out the default playback
   device. **Verification note** (plan-review correction): a local `rigctld -m 1` dummy session
   (see Human-action-required below) is the WRONG gate for this item — it exercises a backend that
   already works and would pass even if `NoneRadioProtocol` were untouched. Verify this item
   specifically with `BackendId="none"` and a real/default playback sink; keep the rigctld-dummy
   work as its own separate CAT-integration verification, not this item's proof.

---

## 🟠 High — real, reachable bugs in core workflows

2. **Stale-mode transmit crash** (confirmed independently by two separate audit passes). Changing
   the TX mode while the image editor is open leaves the editor holding a stale target-mode
   snapshot (`TxImageEditorPaneViewModel.cs:42,78`; `TxControlsPaneViewModel.cs:690,789-795`) —
   Apply then produces an image sized for the OLD mode, and Transmit throws a
   dimension-mismatch exception (`AnalogFmSstvEncoder.cs:44-49`) with only the generic error
   banner. Minimum fix: disable the mode selector while the editor is open, or push mode changes
   into the open editor.
3. **TX image editor: no rotate, no EXIF auto-orient.** Neither `ImageFileLoader.LoadOriginalAsync`
   (`src/ScanlineStudio.Core.Imaging/ImageFileLoader.cs:23-27`) nor the non-editor `LoadAsync`
   (`:16-21`, the stock-picker path) call `AutoOrient()`; the Rotate tool is a permanently-disabled
   stub (`TxImageEditorPaneView.axaml:61`). A phone photo with EXIF orientation loads sideways with
   no in-app remedy, via either load path.
4. **TX image editor: no aspect-locked crop.** Only a free-drag bottom-right corner handle exists
   (`TxImageEditorPaneView.axaml:200-205`) plus a binary preserve-aspect toggle — filling a mode's
   frame without distortion or arbitrary letterboxing (the single most-wanted crop behavior) isn't
   achievable by hand.
5. **Packaging doesn't exist, and `dotnet publish` would ship a broken binary — plus no version
   stamping, no About dialog, dead Help menu (merged from former item 11 per plan-review: these
   ship together, not sequentially).** No publish profile, installer, or release workflow anywhere
   (`.github/` has only `workflows/ci.yml`; `spec/01-architecture.md:21`'s packaging line is
   unimplemented). Worse: the native audio shim
   (`libyoniqaudio.so`/`yoniqaudio.dll`/`.dylib`, built via a raw `<Exec>` in
   `src/ScanlineStudio.Core.Audio.MiniAudio/ScanlineStudio.Core.Audio.MiniAudio.csproj:51-89`) is
   not a `None`/`Content` MSBuild item, so it never reaches `ResolvedFileToPublish` — a published
   app would `DllNotFoundException` on first `IAudioEngine` resolve. No RX, no TX, ever, in a
   published build. Never actually tested (nobody has run `dotnet publish` on this repo).
   `Directory.Build.props` has no `<Version>`/`<Product>`/`<InformationalVersion>` — every assembly
   ships as `1.0.0.0`, and a bug report can't identify which build it came from. The `Help`
   top-level menu has no children and no command (`MainWindow.axaml:107`) — once there's an
   installer, the LGPL notice (`COPYING`/`COPYING.LESSER`/`LICENSES.md`) has no delivery path in a
   binary distribution without an About box. Do version stamping + an About dialog as part of the
   same packaging work, not as an afterthought.
6. **RX buffer Extended (disk) mode: write failures are recorded but never checked before a
   read/replay.** `IRxLineStagingBuffer.HasWriteFailed` has zero consumers
   (`RxDiskLineStagingBuffer.cs`; grepped across `src/`). A disk write failure mid-reception, or a
   `Clear()` drain-timeout (`:339-348`), leaves stale/short data on disk; the next
   `PerformReplay`/read either zero-fills over an already-decoded valid prefix
   (`RxDiskLineStagingBuffer.cs:591`, `AnalogFmSstvDecoder.cs:5393-5400`) or redraws at a
   post-truncation row count against pre-truncation samples. Silent image corruption, no user
   signal. Fix is a one-line guard at each of the two call sites (`PerformReplay`,
   `TryCorrectSlant`). RAM mode (the default) is unaffected.
7. **Both quick-mode-button grids (Receive and Transmit tabs) are dead, unlabeled pills** sitting
   directly beside real controls — no `Command`, no `IsEnabled="False"`, no tooltip, visually
   indistinguishable at rest from genuinely-interactive chips nearby
   (`MainWindow.axaml:198-215` RX, `TxControlsPaneView.axaml:49-66` TX, 16 pills each). **The RX
   side's backing feature already exists and works**: `ISstvSessionService.ForceMode` is fully
   implemented and documented as "the port of legacy's real RX quick-mode-button click"
   (`ISstvSessionService.cs:124-129` → `SstvSessionService.cs:310-314` →
   `RestartableSstvDecoder.cs:347-356` → `AnalogFmSstvDecoder.PerformForceMode`) but has **zero
   UI callers**. `en.json:93`'s tooltip and `RxImagePaneViewModel.cs:151-157`'s doc comment both
   currently assert "no manual lock-to-a-mode feature exists," which is now false. Cheap real win:
   wire the RX pills to `ForceMode`. **Correction (plan-review)**: the TX pills DO have a cheap
   backing feature too — `SelectedMode` is a settable, two-way-bound property
   (`TxControlsPaneView.axaml:67-68`), so wiring the TX pills to set it is exactly as cheap as the
   RX wiring; don't just disable them, wire both grids. Same batch: the Mode card's Auto/**Locked**
   segmented control (`MainWindow.axaml:192-193`) is disabled on the identical false "no mode-lock
   feature" premise as `en.json:93` — fix the tooltip/doc-comment claims for both controls together.
8. **First-run silent dead end.** A fresh install with no `settings.json` has no configured audio
   capture (or playback, see item 1) device; `SstvSessionService.ResolveDeviceAsync` throws
   (`SstvSessionService.cs:745-749`), `Program.cs:353-360` catches and logs a Warning, and **no
   error text or remediation affordance appears anywhere in the UI** (plan-review-corrected wording
   — `RadioStatusViewModel.cs:110` does render the Receiving toggle unchecked, so the state isn't
   fully invisible, but nothing explains why or how to fix it). Result: blank waterfall, blank RX
   pane, forever, with the only real explanation in a log file the user doesn't know exists.
   Minimum fix: default to the system's default capture AND playback device, or show a status-bar
   message with an "Options → Audio" affordance. Covers both the RX (this item) and TX (item 1)
   sides of the same underlying gap.
9. **RX incoming-frame progress bar is hardcoded, never moves.** `MainWindow.axaml:485` —
   `Value="0"` literal, while the real `RxImagePaneViewModel.Progress` (0.0-1.0) already exists and
   feeds two other consumers. This is the primary "is it working?" indicator during every decode.
   One-line binding fix. Frame/line count on the same card (`:484`) is similarly `"—"` while the
   real `LineProgressText` is already bound elsewhere (status bar).
10. **No live PTT/TX-keyed indicator anywhere in the UI.** `RadioStatusViewModel` has no
    PTT/TX-state property at all; the only TX-labeled status chip actually means "last TX errored,"
    not "currently keyed" (`MainWindow.axaml:1455-1461`). Safety-relevant gap for a transmit
    application — a user has no way to visually confirm the rig is/isn't keyed from readback.

---

## 🟡 Medium — should-have, not release-blocking

- TX image editor: no way to re-open/re-edit an image after Apply (`Applied`/`Cancelled` both null
  `ActiveEditor`, no Edit button exists — `MainViewModel.cs:72-73`); no undo/redo; overlay text on
  the editing canvas is not WYSIWYG (no `FontSize` binding, wrong plate rendering —
  `TxImageEditorPaneView.axaml:215-225` vs `TransmitImagePreparer.cs:67-90`); 6 brightness/contrast/
  saturation/gamma/sharpen/denoise sliders have zero backing code in `ITransmitImagePreparer`
  (implement or remove the row); card header + dimensions chip hardcode "640×496 · PD120"
  regardless of actual mode/size (`:28-30`, `:79`).
- Waterfall range caption is a stale literal ("1000…2600 Hz," `MainWindow.axaml:376-378`,
  `en.json:118`) that silently lies once the real Start/Span steppers are touched; those same
  steppers only window the spectrum half, not the waterfall (`WaterfallControl.cs:25-32` has no
  Start/Span params) — the two plots visibly disagree after any adjustment.
- TX golden-vector fixtures are stale/non-discriminating for the *current* encoder —
  `TxCaptureFixturesTests.cs` self-decodes checked-in `.mmv` files without ever invoking
  `AnalogFmSstvEncoder`, so "TX is golden-vector validated" is currently an overstated claim (each
  individual TX change was independently source-verified, but there's no chain-level regression
  net). Re-capture the 11 `TxCapture/*.mmv` fixtures against the current encoder.
- No TX send-progress feedback during transmit — a multi-minute PD290 send shows only the red
  toggle button, no percentage/time-remaining.
- Editor-open guard not enforced: `TxControlsPaneViewModel.cs:80-84` documents that picking a new
  image while the editor is open should be disabled; no `IsEnabled` binding actually does this
  (`:652-655`) — a second pick is a silent no-op.
- CI doesn't enforce the ≥80% line-coverage gate `spec/13-testing.md:25,56` requires — no coverage
  collection in `.github/workflows/ci.yml` at all.
- `README.md` is badly stale (still describes "Phase 0 — walking skeleton," links a gitignored
  `CLAUDE.md`). Only English locale ships (infra is ready, `assets/locale/locales.json` has 1
  entry). Default log level ships at `Debug`, not `Information` (`Program.cs:51`, already commented
  as "flip before the first release tag" — just needs actually flipping then). No log-file
  rotation. `Avalonia.Diagnostics` (DevTools) is referenced unconditionally, shipping in Release
  builds too.
- Default Avalonia template icon still in use (`MainWindow.axaml:15`, `avalonia-logo.ico`).

---

## 🔵 Human-action-required (not agent-completable)

- **Full manual hardware checklist across Windows/Linux/macOS.** Only ever run on Linux — Windows/
  macOS audio (WASAPI/CoreAudio) has zero real-device test coverage; all 33 real-hardware audio
  tests unconditionally skip on non-Linux CI legs by design
  (`RequiresPipeWireFactAttribute.cs:39-42`), so green CI on those legs proves compilation only.
  **The checklist document itself doesn't exist** — `spec/14-roadmap.md:307`/`:5118` cite it as
  the literal release gate, but `spec/13-testing.md`'s own line 59 is an unchecked box, not a
  written checklist. Open decision needing a human call: can 1.0 ship Linux-labeled-only, or does
  even a beta need all 3 platforms real-hardware-verified first? **Narrowed 2026-08-15**: this
  genuinely needs real audio hardware (WASAPI/CoreAudio) and a real rig — but the CAT/rigctld leg
  specifically does NOT need real hardware, see below.
- **CAT/`rigctld` interop is agent-completable, not human-action-required** (correction, this was
  originally lumped into the hardware checklist above). `rigctld` ships a `RIG_MODEL_DUMMY` backend
  (`rigctld -m 1`, confirmed available in this sandbox, Hamlib 4.5.5) — a real `rigctld` daemon
  speaking the real wire protocol, no physical rig needed. This app's own `RigctldClientProtocol`
  backend can be pointed at it for genuine integration testing (connect, poll frequency/mode, set
  frequency, set PTT) instead of only the in-process fakes the test suite currently uses. **Also
  the fastest real (not code-only) verification path for Critical item 1** — select the `rigctld`
  backend against a local dummy instance and confirm a real end-to-end transmit actually completes,
  not just that `NoneRadioProtocol` was the specific thing that threw.
- **Clublog `cty.dat` API key** — requires emailing Clublog's helpdesk for redistribution rights.
  Blocks the Tier-2 offline callsign/country lookup only; not itself release-blocking.
- **Chilkat/FastReport license status** — needs running the legacy binary directly to confirm
  actual usage (not verifiable from source alone); currently assumed unused/orphaned.

---

## ⚪ Parked — yoniq-compatibility-only, explicitly out of 1.0 scope (user instruction 2026-08-15)

These are real, legacy-documented features, but their only purpose is matching what MMSSTV/YONIQ
offered — not needed for a working 1.0 SSTV application:

- Advanced tab PLL/Zero-crossing tuning-parameter UI (the *defaults* are already verified correct
  against legacy — DSP core audit, item 1 above — this is only about exposing them as
  user-overridable, an advanced-user knob legacy had).
- VOX (TX tone-burst preamble to trigger a rig's own VOX circuit — the preamble itself is niche.
  **This is NOT the same thing as the no-CAT/VOX-operating case** — that's Critical item 1 above,
  which is a core-loop bug, not parked. Only the preamble-generation feature is parked here.).
- Sound-file ID (`.mmv` playback as an alternative to CW station ID).
- Auto-start (arm/disarm-the-decoder workflow convenience).
- `.ini` legacy settings importer (already parked earlier this session, see `spec/14-roadmap.md`
  Tier 3).
- `TemplateCatProtocol` fallback (niche last-resort rig support via user-authored byte templates).
- `MacroTextResolver`'s remaining tokens (his-callsign/name/QTH/RST — blocked on a "current QSO"
  context concept that doesn't exist and isn't needed for 1.0).
- Offline callsign/country lookup (QRZ.com's online lookup already works; this is a
  no-internet-required nicety, additionally blocked on the Clublog human action above).

---

## Suggested execution order

Critical item 1 first (unblocks the core TX loop for the majority of users), fixing BOTH the PTT
capability guard and the playback-device fallback (item 8's TX half) together, and verified against
`BackendId="none"` + a real/default playback device — NOT via `rigctld`, which exercises a
different, already-working backend and would pass regardless (plan-review correction) → item 8's
remaining RX-side fix (capture-device fallback + surfaced error) → High items 2-3 (both are
TX-image-editor/TX-mode-change correctness bugs, natural to fix together) → High item 5 (packaging
+ version/About, merged — do this early since it can reveal further publish-time surprises the
sooner it's tried) → remaining High items (6, 7, 9, 10), roughly in the order listed (each is
small/independent) → Medium items opportunistically → the `rigctld -m 1` CAT-integration work (a
real but separate verification improvement, not gating anything above) → Human-action items get
flagged to the user, not blocked on.
