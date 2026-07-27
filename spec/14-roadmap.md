# Roadmap

## Related

Ties together every other document — each phase below is delivered by working through the "Definition of done" checklists in the referenced specs, in order, keeping commits small and reviewable per CLAUDE.md. Also see [LICENSES.md](../LICENSES.md) and [docs/removed-features.md](../docs/removed-features.md), both living documents that get new entries as work in these phases proceeds.

## Sequencing principle

Order is chosen so that at the end of every phase there is a **runnable, demoable** program, never a long stretch of code that doesn't build into something observable. This also front-loads the highest cross-platform risk (audio, DSP) rather than saving it for last, since [[05-audio-engine]] is the area most likely to reveal that an architectural assumption needs revisiting.

## Phase 0 — Walking skeleton

- [[01-architecture]]: solution scaffold, DI host, nullable+warnings-as-errors, empty Avalonia window boots on Windows/Linux/macOS.
- [[13-testing]]: CI matrix running (even with near-zero tests) so every subsequent PR is gated from day one.
- [[12-settings]]: `ISettingsStore` minimal implementation (no migration chain yet).

**Demo:** app launches on all three OSes, shows a blank window, settings file is created on disk.

## Phase 1 — Audio + DSP core (no UI, no radio)

- [[05-audio-engine]]: capture/playback working on all three OSes.
- [[06-sstv-dsp]]: encode/decode round-trip passing for the core mode set, against `FakeAudioEngine` and real audio; sample-clock calibration (slant correction) and FSK/CW station ID implemented (corrected into scope after review — these are correctness/regulatory requirements, not polish, see [[06-sstv-dsp]]).
  - Mode-by-mode sequencing, each verified individually before moving to the next (explicit user instruction, not a shortcut): Martin M1/M2 and Scottie S1/S2/DX are done (channel order, timing, VIS codes read from `sstv.cpp`/`Main.cpp`, each cross-checked against `CSSTVSET::GetTiming`). Remaining, in distinct families each needing their own from-source tracing: Robot 36/72 (BW+chroma), the PD-series (YCbCr, two-lines-per-transmission-line), Pasokon P3/P5/P7, the MR/ML/MP/MN/MC/R24/RM8/RM12 "half-scan-chroma" family, AVT.
  - **Do not lose this**: once every mode above is added and verified, port the legacy adaptive per-mode PLL filter tuning (the `PRODEM` "demod profile" system referenced in `Main.cpp`'s INI loading — see `SstvModeRegistry`'s doc comment) so faster modes (Martin M2, Scottie S2) work at the legacy-matching 11025Hz default instead of needing 44100Hz as a stand-in. Explicit user sequencing decision: modes first, adaptive filter after — recorded here so it isn't dropped once the mode list is "done."
- [[13-testing]]: fixture directories for audio/SSTV established, including at least one golden-vector fixture captured from the legacy binary (see [[13-testing]]'s golden-vector section) — do this early, since it requires standing up the legacy build once, which only gets harder to justify later in the project.

**Demo:** a console/test harness encodes a test image to a `.wav`, decodes it back, and the round-trip image matches within tolerance — provable before any UI exists.

## Phase 2 — Radio layer (no CAT rigs yet)

- [[02-radio-layer]]: `IRadioController` reference implementation against a fake transport/protocol, "no radio" path fully supported.
- [[04-rigctld]]: client mode (this is prioritized over hand-written CAT protocols because it unlocks the widest rig coverage fastest, and its ASCII line protocol is the simplest to get right first).

**Demo:** `IRadioController` connects to a real `rigctld` instance and reports live frequency/mode changes in a log/console.

## Phase 3 — Minimal UI, first end-to-end path

- [[09-ui]]: `MainWindow` walking skeleton — waterfall, RX image panel, basic TX button — wired to Phase 1/2 services through `Yoniq.Application`.
- [[10-localization]]: `ILocalizationService` + `Translate` extension in place from the start (retrofitting localization onto an already-built UI is far more expensive than building it in from the first window).
- [[07-image-pipeline]]: minimal `IReceivedImageBuffer`/basic TX image selection (crop/resize can follow in Phase 4).

**Demo:** a real over-the-air (or audio-cable-looped) SSTV RX/TX session, end-to-end, through the UI, with a rig's frequency shown live via rigctld.

## Phase 4 — CAT protocols, image tooling, logbook

- [[03-cat-layer]]: Icom CI-V and Kenwood ASCII first, then remaining Yaesu variants and JST-245, plus the template/fallback protocol.
- [[07-image-pipeline]]: full crop/resize/filter/overlay, stock library, RX history.
- [[08-logging]]: logbook, ADIF import/export, offline callsign lookup; QRZ.com opt-in lookup can trail slightly if needed.
- [[04-rigctld]]: server mode.

**Demo:** feature-complete for the "operate SSTV with a directly-connected rig and keep a proper log" use case — this is the point at which YONIQ v2 first matches legacy MMSSTV/YONIQ's core day-to-day workflow.

## Phase 5 — Extensibility and polish

- [[11-plugin-system]]: plugin host, at least one built-in extension point (`IImageFilter`) proven to load through the plugin path. If [[15-template-designer]] work has started by this point, its `ITemplateItem` extension point (the documented successor to the legacy CItems DLL ABI) is the higher-value target to prove the plugin path against, since it's the one with real legacy prior art and potential third-party demand.
- [[09-ui]]: remaining dialogs from the inventory table (macro editor, color settings, etc.).
- [[12-settings]]: legacy `.ini` importer, migration chain exercised by a real version bump.
- [[10-localization]]: remaining views localized, community-translation-friendly locale-file workflow documented.

**Demo:** a legacy user can import their old settings, pick up their macros/rig config, and optionally extend the app with a plugin.

## Explicitly deferred beyond v1

- Perspective correction / webcam capture ([[07-image-pipeline]]).
- Full Hamlib extended command-set coverage beyond frequency/mode/PTT ([[04-rigctld]]).
- Plugin sandboxing beyond same-process isolation ([[11-plugin-system]]).
- Legacy proprietary `.MDT` log format import (ADIF is the supported migration path instead, [[08-logging]]).
- The full QSL/template designer and `.mtm` import ([[15-template-designer]]) — specified but deferred; [[07-image-pipeline]]'s minimal `ImageOverlay` covers text-only TX overlay in the meantime.
- SSTV repeater/beacon mode ([[06-sstv-dsp]], legacy `RepSet.cpp`).
- Contest logging (JASTA application, `MMCG.DEF` JARL area database) — out of scope entirely, not just deferred; see [docs/removed-features.md](../docs/removed-features.md).

## Release gates

Before any tagged release: full [[13-testing]] manual hardware checklist (real rig CAT session, real audio device round-trip, real third-party `rigctld` interop) passes on at least one Windows, one Linux, and one macOS machine, in addition to the automated CI matrix being green.

## Open items requiring a decision before the relevant phase starts

- ~~Project license~~ — **decided**: LGPL-3.0-or-later, matching upstream. See [LICENSES.md](../LICENSES.md). The remaining open sub-item is confirming the `Terms.txt` freeware-clause interpretation with the upstream author (JE3HHT) if the project ever moves toward commercial distribution — not a blocker for development.
- [[08-logging]]: source and license-audit the callsign-prefix/country dataset before bundling (Phase 4) — `ARRL.DX` is already ruled out, see [LICENSES.md](../LICENSES.md); Clublog's `cty.dat` is the current candidate, needs its own audit entry before it ships.
- [[05-audio-engine]]: confirm PortAudio latency is acceptable on Windows before committing to it as the sole backend, vs. adding a native WASAPI backend later (Phase 1).
- [[04-rigctld]]: decide default behavior for server-mode "allow remote control" — off by default is the current spec position; confirm before Phase 4 ships it.
- [[15-template-designer]]: the `.mtm`/`PARALIST.BIN` binary format needs a proper reverse-engineering pass (cross-checked against `Draw.cpp`'s own `Load`/`Save` methods) before any implementation work on that subsystem can start — flagged as a prerequisite, not yet done.
- [LICENSES.md](../LICENSES.md): confirm whether Chilkat or FastReport actually back a real feature by building and running the legacy binary directly (not verifiable from source alone) — currently assumed unused/orphaned based on a source-only search.
