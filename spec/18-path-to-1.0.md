# 18 — Path to 1.0

## Status: 1.0 COMPLETE (2026-08-16)

🔴 Critical, 🟠 High, and 🟡 Medium are all closed (final item: the TX image editor cluster's
undo/redo sub-piece, commit `629ecb6`). Remaining items in this doc (🔵 Human-action-required,
⚪ Parked) are explicitly non-agent-completable or out-of-scope-by-user-decision, not open 1.0
work — see their own sections below. Next target is 1.1; see [[19-path-to-1.1]].

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

- TX image editor cluster (sub-pieces sequenced small/mechanical first — see `PROJECT_BRIEF.md` for
  full history):
  - ✅ **DONE** (commit `307e528`): card header + dimensions chip hardcoded "640×496 · PD120"
    regardless of actual mode/size.
  - ✅ **DONE** (commit `7f1d32c`): overlay text's crop-relative position/font-size math fixed —
    `BuildOverlay()` used to pass raw, full-photo-normalized X/Y straight into `ApplyOverlay`, which
    interprets them as normalized against the CROPPED+RESIZED final image; wrong whenever the crop
    wasn't the full identity rect. Also added the missing canvas `FontSize`/`FontFamily` binding.
    **Two new findings surfaced by this fix, not yet resolved:**
    - **Overlay text never renders visibly on the interactive editing canvas at all** (confirmed via
      `git stash` to pre-date this fix; confirmed unrelated to the new binding via a hardcoded
      `FontSize="40"` test and a `ClipToBounds="False"` experiment, neither fixed it). The separate
      pipeline-accurate side preview panel (`PreviewImage`) DOES render it correctly. Root cause not
      found — worth a dedicated investigation pass, `TxImageEditorPaneView.axaml`'s overlay
      `ItemsControl`/`ItemsPanel` (nested `Canvas` inside the outer `EditorCanvas`) is the prime
      suspect area.
    - `TxControlsPaneViewModel.EditState.Overlay` captures already-crop-projected coordinates
      (`TxControlsPaneViewModel.cs:850`), so switching TX mode mid-edit re-applies those same
      coordinates against the NEW mode's aspect (`:965-967`) — stale whenever the two modes' aspect
      ratios differ. Not a regression (pre-fix those coordinates were wrong at both modes), but the
      composition-across-modes comments at `:74-75`/`:948-950` now overclaim. **Update (commit
      `faeef61`)**: `EditState` now ALSO carries the raw (un-projected) positions
      (`RawOverlay`, added for the re-open/re-edit sub-piece below) — the data this fix needs
      already exists, but `OnSelectedModeChanged`'s own reflow was deliberately left unchanged
      (still reads the crop-projected `Overlay`, per that sub-piece's own explicit scope
      boundary). Wiring `RawOverlay` + a fresh `ProjectToCropRelative`-equivalent into the reflow
      closes this for good — now a small, well-scoped fix, not a research question.
  - ✅ **DONE** (commit `bcf8504`): 6 brightness/contrast/saturation/gamma/sharpen/denoise sliders implemented (not
    removed) against a new `ITransmitImagePreparer.ApplyAdjustments` — real ImageSharp-backed
    Brightness/Contrast/Saturate + a hand-rolled gamma curve (ImageSharp has no `GammaCorrection`
    operation) + conditionally-skipped Gaussian sharpen/denoise, applied between Resize and
    ApplyOverlay so adjustments never touch already-burned-in overlay text. Real-window verified
    (Brightness slider dragged in the running app; side preview panel's pixel values measurably
    brightened).
  - ✅ **DONE** (commit `faeef61`): "Edit..." button re-opens the TX image editor after Apply,
    restoring crop/preserve-aspect/adjustments AND overlay text (with its raw macro template
    intact, not the baked-in resolved value) via a new `EditorInitialState`/`RawOverlayElements`
    mechanism. Real-window verified (Brightness=44 + a non-default crop survived a full
    Apply→Edit round trip).
  - ✅ **DONE** (undo/redo, the last sub-piece — see `PROJECT_BRIEF.md` for full history): snapshot-
    based (not command-pattern) undo/redo across CropRect/PreserveAspect/LockAspectToMode/all 6
    adjustment sliders/overlay add-remove-position/Rotate. Two push mechanisms: View-level
    drag-gesture push for crop move/resize (lazy, on the first real pointer move, never a no-op
    click); VM-level `On*Changing` + dispatcher-idle coalescing for sliders/toggles and (per a
    code-review finding on the first draft) overlay element X/Y, which a drag-only push would have
    left completely untracked for the sidebar TextBox path. Rotation reconciled via a
    `RotationCount`-plus-delta-replay (no per-snapshot image bytes). Auditor code-review: GO WITH
    CHANGES, no blockers — the one real finding (overlay X/Y untracked) and three defensive/test
    fixes were folded in and mutation-tested; a few low-severity nits (a `_pendingCoalesceProperty`
    edge case, `Undo`/`Redo`'s own un-trimmed opposite-stack push, no test for the View's
    once-per-gesture flag itself) were deliberately left as documented, low-risk gaps. No `xdotool`/
    `ydotool`/`xte` in this sandbox and no passwordless `sudo` to install one, but a system-available
    `pyautogui`+`mss`+`pillow` combo (per this file's own screenshot-toolchain note) worked instead
    — real-window verified end-to-end: dragged Brightness to 46 and clicked Undo (reverted to 0,
    preview darkened back), clicked Redo (reapplied), Rotated (canvas visibly went portrait) and
    clicked Undo (visually un-rotated back to landscape, Brightness=46 correctly untouched by that
    Undo), and dragged the crop-resize handle then clicked Undo (crop rect reverted to the full
    frame). All four matched the automated suite's own predictions. Undo history intentionally does
    NOT persist across an editor re-open (a fresh session starts empty).
  - **TX image editor cluster: fully closed.**
- ✅ **DONE** (commit `9f6ea47`): waterfall range caption's own stale-literal half —
  `WaterfallPaneViewModel.RangeCaptionDisplay` is now computed live from `StartHz`/`SpanHz`
  (`NotifyPropertyChangedFor` on both), not a static "1000…2600 Hz" localized literal. Real-window
  verified (Start stepper "+" x3 updated the caption live, no other action needed).
  - **Remaining (deliberately scoped separately, per that commit's own note — "a much larger
    feature")**: the Start/Span steppers only window the SPECTRUM plot; `WaterfallControl.cs:25-32`
    has no Start/Span params at all, so the waterfall plot itself still shows its own full range
    regardless of the steppers — the two plots visibly disagree after any adjustment, only the
    caption text was fixed.
- ✅ **DONE** (commit `8a9089e`): TX golden-vector fixtures re-captured against the current encoder
  and made discriminating — a new opt-in `TxCaptureFixtureGenerator.cs`
  (`SCANLINE_REGENERATE_TX_FIXTURES=1`) re-runs the current `AnalogFmSstvEncoder` and overwrites all
  11 checked-in `.mmv` fixtures; a new load-bearing
  `LiveEncoderOutput_MatchesCheckedInFixture_WithinQuantizationTolerance` test live-encodes each
  mode and asserts against the checked-in fixture (both sides round-tripped through the real `.mmv`
  format for an apples-to-apples comparison) — a future encoder regression now fails loudly on the
  next ordinary test run instead of silently drifting. Two rounds of plan-review, one code-review,
  4-way mutation-tested. **Follow-up, human-dependent** (tracked in the 🔵 tier below): the
  `*_TX_RX.bmp` legacy-Wine-decoded companion files no longer pair with the freshly re-captured
  `.mmv`s and need a human with a real legacy install to refresh.
- ✅ **DONE** (commit `cffd4b5`): TX send-progress feedback during transmit —
  `AnalogFmSstvEncoder.EstimateSampleCount` computes the exact total sample count up front (mirrors
  `EncodeAsyncCore`'s own accumulator, not a duration-sum-then-multiply that floating-point
  non-associativity doesn't guarantee to agree), threaded through a new `TransmitProgressChanged`
  event; `TxControlsPaneViewModel` exposes `TransmitProgress`/`TransmitProgressText`, bound below
  the Transmit/Stop TX buttons. Plan-review + code-review, mutation-tested. Real-window testing
  caught a real layout-overlap bug the review passes didn't (fixed, reverified 0%→29%→82%→done).
- ✅ **DONE** (commit `963f16d`): editor-open guard's visual half — `OpenEditorForSourceAsync`
  already blocked a second pick while the editor is open (silent no-op, already tested); added the
  missing `IsEnabled="{Binding !IsEditorOpen}"` bindings (Browse button, stock-image ListBox) plus a
  third ungated entry point code-review found (File > Open image, Ctrl+O).
- ✅ **DONE** (commit `7bb8fe0`): CI now enforces a per-project line-coverage gate — a new `coverage`
  CI job (Ubuntu-only; per-OS platform-conditional code makes one threshold unsafe across all 3
  legs) collects Cobertura reports and `scripts/check-coverage.py` fails the build below each
  project's own threshold in `coverage-thresholds.json`. Thresholds set a few points below each
  project's own *currently-measured* coverage (spec's own "configurable per-project" allowance),
  not a blanket 80% — catches regressions from today's baseline, not a retroactive demand.
  `ScanlineStudio.Core.Sstv.Tests` excluded (2 separate coverage-instrumented runs each took
  45-90+ min uninstrumented; still runs fully in the existing fast matrix). Plan-review + code-review
  (caught real CI-vs-dev-machine coverage gaps — MiniAudio/Hamlib/Rigctld thresholds re-measured
  with the real hardware/daemons unavailable, not assumed). Mutation-tested 3 ways.
- ✅ **DONE** (commit `29651a7`): `README.md` brought up to date — replaced the stale "Phase 0"
  status with an accurate summary of what's shipped, repointed the roadmap link to this file,
  removed the dead gitignored-`CLAUDE.md` link, and stated the single-locale status honestly.
- ✅ **DONE** (commit `e18023a`): file logger now rotates by size (default 10 MB, 5 backups) instead
  of growing `app.log` unbounded — code-review found and fixed 2 real defects (a failed rotation
  left every subsequent log call throwing `ObjectDisposedException`; `Dispose()` raced with an
  in-flight write). New `ScanlineStudio.Host.Tests` project (15 tests), both fixes mutation-tested.
- ✅ **DONE** (commit `59ccaee`): `Avalonia.Diagnostics` (DevTools) package reference is now
  conditioned on `Configuration == Debug`, matching the `#if DEBUG` gate its own `AttachDevTools()`
  call already had — the DLL no longer ships in Release output. Also fixed a CI Restore-step bug
  (missing `Configuration`, defaulting to Debug) that would have hidden this fix from CI entirely.
- ✅ **DONE** (commit `b8e2aea`): default Avalonia template icon replaced with a real app icon — a
  4-bar mark built from the established "Industry" brand palette, embedded at 7 standard resolutions
  (16-256px), wired as both the in-app window icon and the Windows EXE's `ApplicationIcon`.
  Real-window verified via the X11 `_NET_WM_ICON` property.
- **Remaining, deliberately deferred, not oversights**: default log level still ships at `Debug`
  (`Program.cs:51`), explicitly commented as a one-line flip reserved for the actual first release
  tag, not this milestone. Only English locale ships — infra is ready
  (`spec/10-localization.md`), but writing a second translation is a content task, not something to
  invent.

**🟡 Medium tier: fully closed** (except the two explicitly-deferred/non-code items just above,
which are gated on a release-tag event and on translation content respectively, not agent-completable
right now).

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
- **TX golden-vector `*_TX_RX.bmp` companion files are stale** (surfaced by commit `8a9089e`,
  re-capturing the `TxCapture/*.mmv` fixtures against the current encoder): the `_TX_RX.bmp` files
  in the same directory were decoded from the *previous* `.mmv` set via a real legacy Wine install
  and no longer pair with the freshly re-captured ones. Needs a human with that real legacy install
  to refresh; not reproducible in this sandbox.

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
