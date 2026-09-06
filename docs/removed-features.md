# Removed features register

Per CLAUDE.md's removal rule: dropping a legacy capability requires an entry here naming the legacy files, the replacement (if any), and the user-visible impact. "Superseded" claims must state which users are actually covered — this document exists so that claim is checked, not assumed.

**Scope of the two `CLMS` entries below.** They cover `Do` (line enhancer) and `DoN` (auto-notch)
only. `CLMS::Sig` (`fir.cpp:217-236`), the LMS-predictor signal-level measurement behind legacy's
repeater squelch, is a separate method and is NOT dropped — it is still a live candidate, see
[[spec/17-rx-telemetry-feasibility]].

## `CLMS::DoN` — LMS auto-notch ("ANF" / "ANS")

**Not ported. Decided 2026-09-06 on measurement, not judgment.**

**Legacy:** `CLMS::DoN` (`fir.cpp:162-190`), `SetAN` (`fir.cpp:194-213`), N-constants
(`fir.cpp:96-103`). Applied to the RX audio buffer before demodulation (`Sound.cpp:348-354`).
UI: the `SBLMS` button's right-click popup (`Main.cpp:14425-14439`), captions "ANF" (an=1) and
"ANS" (an=2), rendered red when armed (`Main.cpp:2682-2692`). Persisted as `[Define] RXLMSAN`
(`Main.cpp:1919`, `2426`). Default off (`Sound.cpp:58-59`).

**Why not ported.** A faithful test-only port was built and measured. It is not a port defect —
the filter provably works, notching an injected carrier 8x down. It removes the WANTED signal
harder: 19x. Two causes, both structural rather than tunable:

1. **No resolution.** 49 taps at 11025 Hz span 4.4 ms, giving roughly 225 Hz of spectral
   resolution. Any notch it forms is at least 200 Hz wide inside an 800 Hz signal band, so
   removing a carrier at 1750 Hz necessarily removes 1650-1850 Hz of picture.
2. **Power weighting.** LMS fits the strongest correlated components first. Below signal level the
   carrier is not the strongest thing present, so the filter fits the picture instead.

Lengthening the decorrelation delay was tested as a fix (1.09 to 80 ms) and made it worse at every
setting; legacy's own 1.09 ms was the best of them, and every setting removed more picture than
interference.

**Measured, real HF noise corpus, test-card source:**

| condition | off | ported CNotch | ANF | ANS |
|---|---|---|---|---|
| no carrier, 5 modes | 22-48 | — | +52 to +78 worse | +53 to +74 worse |
| carrier -6 dB | 49.29 | — | 99.30 | 91.98 |
| carrier 0 dB | 86.34 | **37.18** | 99.90 | 95.29 |
| carrier +6 dB | **no lock** | **37.27** | 96.44 | 101.92 |
| carrier +12 dB | **no lock** | **37.54** | 105.28 | 117.37 |
| carrier +20 dB | **no lock** | **39.29** | 97.62 | 100.61 |

Mean absolute per-pixel delta, lower is better. ANF does produce a lock at +6 dB and above where
the baseline produces nothing at all — but into an unusable picture, while the notch we already
ship lands two to three times better. **In no tested condition does ANF rescue anything
`NotchFilter` does not rescue better.**

**Replacement:** `src/ScanlineStudio.Core.Sstv/NotchFilter.cs`, a direct port of legacy's `CNotch`,
with a manual frequency control in the RX pane. 96 taps, so roughly twice the spectral resolution.

**What `NotchFilter` does NOT cover** — the real gap, stated plainly:

- **Automatic carrier acquisition.** The user must find the frequency and click the spectrum.
- **Tracking a drifting carrier.**
- **More than one simultaneous carrier.**
- **A carrier appearing mid-picture** without user action.
- **The ANS gentle variant** has no equivalent.

**Affected users:** anyone importing an `.ini` with `RXLMSAN=1` or `2`. Import rule: honour `RXLMS`
for the line enhancer if that ships, drop the AN state, and log one information line. Not a silent
no-op.

**Better replacement, not built:** steer `NotchFilter` automatically by detecting a spectral peak
that persists at one frequency for seconds — which is what distinguishes a carrier from picture
content, and needs no adaptive filter. That closes the first four gaps above and reuses the ported
filter for the part that must be correct.


## `CLMS::Do` — LMS adaptive line enhancer ("LMS")

**Not ported. Decided 2026-09-06 on measurement, not judgment.**

**Legacy:** `CLMS::Do` (`fir.cpp:136-160`), `SetLMS` (`fir.cpp:105-134`), constants
(`fir.cpp:88-94`). Applied to the RX audio buffer before demodulation (`Sound.cpp:348-354`).
UI: the `SBLMS` button in the `Main.dfm` "DSP" group box, alongside `SBAFC`
(`Main.cpp:6011-6018`). Persisted as `[Define] RXLMS`. Default off.

**What it is.** A 5-tap adaptive predictor at 11025 Hz with a 1-sample decorrelation delay. It
outputs the prediction, normalised by the sum of the tap magnitudes. A periodic signal is
predictable and survives; broadband noise is not and is attenuated.

**Why not ported.** A faithful test-only port was built and measured across the whole mode
registry. It works. It is simply too small to be worth the decode-path state it needs.

Sweep coverage: all 43 registry modes, real HF noise corpus, 5 seeds x 4 SNR points
(20/16/12/9 dB), test-card source, plus a second pass on a photographic source for the modes that
passed. Qualification gate: mean gain >= 3%, no cell worse than -1%, no lock lost.

**8 of 43 modes passed the gate. Converted to the only unit that matters:**

| mode | mean pixel-error gain, photo source | equivalent SNR gain |
|---|---|---|
| mn73 | +20.2% | +2.4 dB |
| mn110 | +19.9% | +2.3 dB |
| mc110 | +16.8% | +2.3 dB |
| mn140 | +16.1% | +2.0 dB |
| mc140 | +13.3% | +2.0 dB |
| mc180 | +12.6% | +1.8 dB |
| mp140 | +9.7% | +1.0 dB |
| mr175 | +9.6% | +0.9 dB |

The dB column is the SNR the unfiltered decode would need to reach the filtered decode's error. It
averages the 9, 12 and 16 dB points. The 20 dB point sits at the end of the measured curve, so no
equivalent can be interpolated there.

**The other 35 modes.** Most are simply too small to matter (`ml180`-`ml320`, `rm8`, `rm12`, `p5`,
`r24`, `robot-36`, `robot-72`, the Scottie family: -0.5% to +1.2% mean). Six are actively harmful on
some noise draws: `sc2-180` -14.7% mean and -72.2% worst cell, `pd90` -53.2% worst, `pd160` -29.5%,
`pd50` -24.5%, `martin-m1` -22.1%, `mp73` -16.8%.

**Why 2 dB is not enough here.** 2 dB is about a third of an S-unit, and it is visible as slightly
less speckle rather than as a better picture. The modes that gain it are MN and MC — the same family
where the ported H3/HBPFN narrow bandpass already recovered 7-13 dB. Buying 2 dB on top of a
fixed problem is worth much less than 2 dB on a broken one. Against that, a per-mode gate would add
three pieces of decode-path state: a per-mode table, an acquisition-state rule (the filter runs
before VIS detection, so it cannot know the mode yet), and a mid-stream switch transient.

**Measurements that did NOT block it**, recorded so they are not re-run:

- No lock was lost in any cell of any of the 43 modes. The harness filtered the whole buffer before
  pushing it, so the filter was active during acquisition and VIS detection.
- Clean-signal alignment shift is identical with the filter on and off in every mode, so it adds no
  net pixel shift and needs no group-delay compensation.
- It does not smear. Horizontal detail (mean |dI/dx|) moves toward the clean-source value and never
  below it: source 10.6, `off` 22.7, filtered 18.2 for mn73 at 20 dB.
- A photographic source gives roughly double the test-card gain, so the content-dependence risk runs
  the opposite way to the one expected.

**Replacement:** none, and none needed. The H3/HBPFN locked narrow bandpass (commit `24f1cb4`)
already covers the MN/MC noise floor, which is where this filter's gain concentrates.

**Affected users:** anyone importing an `.ini` with `RXLMS=1`. Import rule: drop the state and log
one information line. Not a silent no-op.

**Data:** `impairment-reports/20260906T162011Z-lms-sweep` (14 modes, test card),
`.../20260906T170406Z-lms-sweep` (29 modes, test card), `.../20260906T195406Z-lms-sweep`
(8 modes, photo). Report JSON retained, decoded picture dumps deleted.


## Native per-rig CAT protocol implementations

- **Legacy**: `cradio.cpp`'s `Freq*` methods (`FreqYaesuHF`, `FreqYaesuVU`, `FreqYaesu9K2K`, `FreqICOM`, `FreqKenwood`, `FreqJST245`, plus the generic poll table backing Ten-Tec Omni VI) and `cradio.h`'s `RADIO_POLL*` enum/`CmdInit`/`CmdRx`/`CmdTx` templates, `RadioSet.cpp`, `ExtCmd.cpp`.
- **Replacement**: none in-house. Scanline Studio is a pure client of external CAT backends instead — Hamlib linked in-process, `rigctld`, flrig, or OmniRig-as-client (all [[spec/03-cat-layer]]), plus a user-editable `TemplateCatProtocol` escape hatch for anything none of them cover.
- **Why**: maintaining a hand-written parser per rig family duplicates work multiple existing, actively-maintained external projects already do; leaning on them (the same relationship WSJT-X has to Hamlib) trades a large ongoing in-house maintenance burden for a dependency on those projects' own coverage and correctness.
- **Not fully replaced**: rig coverage now depends entirely on whichever external backend(s) actually ship, not on a list this project controls — a rig with no Hamlib/flrig/OmniRig support and no user-authored `TemplateCatProtocol` template has no path to CAT control at all. Unlike legacy, there is no standalone/offline CAT mode: every backend except the template fallback requires an external process, library, or driver to be present (a running `rigctld`/flrig instance, a linked Hamlib build for the user's OS/arch, or OmniRig installed on Windows).
- **Impact**: users of rigs well-covered by Hamlib (the large majority) see no functional loss and gain the widest rig-support list of any option considered. Users on rigs Hamlib/flrig/OmniRig don't support, previously served by one of legacy's ~14 hand-written families, need a `TemplateCatProtocol` template (if the rig's command set is simple enough to hand-author) or lose CAT control until upstream Hamlib adds support.

## OmniRig ActiveX/COM integration

- **Legacy**: `OmniRig_OCX.cpp`, `OmniRig_OCX.h`, `OmniRig_TLB.cpp`, `OmniRig_TLB.h`. User-facing toggle in `Config.cfg` (`omnirig=0`).
- **Replacement**: `rigctld` client mode or linked Hamlib ([[spec/04-rigctld]], [[spec/03-cat-layer]]), or an OmniRig-as-*client* backend ([[spec/03-cat-layer]]) — Scanline Studio talking to an already-running OmniRig instance rather than bundling OmniRig's own OCX/TLB into itself.
- **Not fully replaced by rigctld/Hamlib alone**: OmniRig's core value was **rig-sharing arbitration** — letting multiple applications (e.g. YONIQ and a separate logger) share one serial-connected rig through a single OmniRig broker process. Linked Hamlib cannot replicate this at all (two processes cannot open the same serial port). `rigctld` replicates it only if every application on the machine is reconfigured to talk through the same `rigctld` instance instead of opening the port directly — a real migration step, not a transparent swap.
- **Fully replaced now that the OmniRig-as-client backend has shipped** (accepted 2026-08-29, implemented same day — see [[spec/03-cat-layer]]'s Definition of done): this restores the original arbitration case directly — Scanline Studio is just another OmniRig-aware client alongside the user's existing logger, no migration to `rigctld` needed. **Caveat**: the real COM path has no automated real-interop test (no Windows machine, no OmniRig install in this dev environment) — only `Rig1` frequency/mode/PTT is wired (legacy's own real call sites), never `Rig2`/Split/RIT/XIT/custom commands.
- **Impact**: users with a single application controlling the rig are unaffected regardless of backend. Users sharing a rig across multiple OmniRig-aware applications can now select OmniRig as Scanline Studio's own CAT backend (Options → Radio/CAT, Windows-only) or migrate everything to `rigctld`-mediated sharing instead.

## Legacy History-tab navigation affordances (step nav, history→template drag-in, clipboard)

- **Legacy**: `Main.h`'s `TabHist` page controls — `UDHist` (a `TUpDown` spinner for step
  prev/next through history), `SBPrim` (jump to the newest buffered frame, `Main.cpp:15851-15857`),
  `SBLatest` (despite its name, actually jumps to the OLDEST buffered frame still in the ring buffer,
  `Main.cpp:6407-6413` — see this entry's own Citation correction below), `HistStat` (a status
  label). `HistView.cpp`'s `THistViewDlg::PBMouseMove` calls `BeginDrag(TRUE,0)` on a history
  thumbnail; `Main.cpp`'s `TabTemp`/`TabTX` drag-accept handlers check
  `pHistView->IsPBox(Source) >= 0` and, on drop, insert a `CDrawPic` item into the template
  composition (`AdjustTempView`/`AdjustPage(pgTemp)`) — **this is drag-*in*, compositing a history
  image directly into the TX template, not drag-out to another application.** Separately, `SBCopy`
  (`SBCopyClick` → `CopyBitmap(pBitmapHist)`) copies the *selected history image* to the clipboard;
  `SBPaste` (`SBPasteClick` → `PasteBitmap(pBitmapTXM,...)`, then `AdjustPage(pgTX)`) pastes clipboard
  content *into* the TX slot — an asymmetric pair, not a matched copy/paste-to-TX pair.
- **Replacement**: partial, updated 2026-08-09 (batch 7), navigation-shell citation corrected
  2026-08-22 (see [[spec/07-image-pipeline]]'s and [[spec/09-ui]]'s own corrections — the dockable-pane
  shell this line originally described was fully replaced by a fixed Receive/Transmit/Gallery/Logbook
  `TabControl`). [[spec/07-image-pipeline]]'s `RxHistoryPane` — now surfaced in the Gallery tab, not a
  dockable pane, a modal dialog, or a page you must switch to — covers click-to-view browsing —
  click-to-select-any-thumbnail is a strict superset of
  `UDHist`'s own step-prev/next spinner (any entry reachable in one click, not just the immediate
  neighbor), so direct step-through nav is deliberately NOT being added as a separate control; this
  is a considered scope decision, not an oversight. "Jump to most recent" IS now real — a new
  `SelectLatestCommand`/"Latest" button (`RxHistoryPaneViewModel.cs`), since mock2's own Gallery draft
  has no slot for it. **Citation correction**: this ports legacy's `SBPrim` speed button (`Main.cpp:15851-15857`,
  `UDHist->Position = 0`), not `SBLatest` despite that name's misleading English reading — legacy's
  ring-buffer nav maps `Position` to a slot via `UpdateHist`'s `n = (m_wPnt-1) - Position`
  (`Main.cpp:6277-6281`), so `Position = 0` (`SBPrim`) is the newest slot while `SBLatestClick`
  (`Main.cpp:6407-6413`) actually sets `Position = RxHist.m_Head.m_Cnt-1`, the OLDEST slot still
  buffered — `SBLatest`'s own real behavior (jump to oldest) is deliberately NOT carried over here.
  The list itself (and the Receive tab's Previous-frames strip, same shared VM instance) now also
  refreshes live as new frames land (`IReceiveHistoryStore.Recorded`), fixing a real, separately-tracked
  gap (spec/16-gui-wiring-survey.md's own PARTIAL finding) that made "jump to newest" meaningless
  before this fix — the list never included the actual latest frame without a manual refresh. The TX
  Controls pane's inline stock/template picker plus its existing file-browse flow cover picking a TX
  source image.
- **Corrected 2026-08-22 — not actually still a full gap**: this line used to say there was no
  history→TX/template compositing path at all. `TxImageEditorPaneViewModel.ImageSourceKind` gained an
  `RxHistory` case since — an operator picks a history entry from the "+ IMAGE" flyout to composite it
  into the TX template ([[spec/07-image-pipeline]]), a picker-based path rather than legacy's drag-in
  gesture. The remaining real gap is narrower: legacy's specific *drag-in-from-thumbnail* gesture
  (`BeginDrag`/`IsPBox` drag-accept) is still not built, and selecting an entry for browsing purposes
  (not compositing) still only loads a read-only preview. Also still dropped: copying a history image
  OUT to the clipboard (legacy's `SBCopy`)
  — no such button/command exists anywhere in this port. **Corrected 2026-08-18** (commit `51beb68`):
  the OTHER half — pasting clipboard content INTO the TX slot (legacy's `SBPaste`) — is no longer
  dropped; the earlier "no first-class bitmap API" blocker was resolved by using Avalonia's own
  `ClipboardExtensions.TryGetBitmapAsync` helper (confirmed real, built-in, cross-platform against the
  pinned Avalonia 11.3.12 package — not a hand-rolled per-platform MIME sniff), wired to the TX image
  editor as a new `ImageSourceKind.Clipboard` source (Ctrl+V, `TxImageEditorPaneViewModel
  .AddImageFromClipboardAsync` → `IFilePickerService.PickClipboardImageAsync`). This is a genuine,
  actual-image-data clipboard read, not a lower-fidelity file-reference copy, so it now matches
  legacy's real `PasteBitmap` behavior for the paste-into-TX direction specifically.
- **Impact**: users who relied on dragging a history thumbnail directly into a TX template to compose
  it now use the "+ IMAGE" flyout's RX-History picker instead (see the correction above) — a picker
  click, not a file-based save/reload round trip, and not a drag gesture either. Only the specific
  drag-in gesture itself and copy-history-to-clipboard remain unbuilt. Paste-to-TX (`SBPaste`'s
  own direction) now works directly via Ctrl+V in the TX image editor. Step-through nav and
  jump-to-latest are both now covered (see Replacement above). Revisit copy-from-history/drag-in if
  this turns out to matter in practice — logged here rather than silently dropped per CLAUDE.md's
  removal rule.

## CItems custom-item plugin ABI

- **Legacy**: `CItems/` (PERIMG, QSLBox, TextArt, TEXTBOX subfolders), documented in `CItems/ECUSTOM.TXT` — a native Win32 DLL ABI that MMSSTV loads on-the-fly to extend the QSL/template designer.
- **Replacement**: none directly. [[spec/11-plugin-system]] provides a managed, cross-platform plugin model (`AssemblyLoadContext`-isolated, `IScanlineStudioPlugin`), but it is not binary-compatible with legacy CItems DLLs — those are native Win32 code built against a C++Builder-specific struct layout, incompatible with a cross-platform managed host by construction, not by choice.
- **Not carried forward**: any third-party custom-item DLLs built against the legacy `ECUSTOM.TXT` ABI (unknown how many exist in the wild) stop working with no automatic migration path.
- **Successor**: none planned. [[spec/15-template-designer]]'s 2026-08-16 redesign explicitly moved a CItems-equivalent `ITemplateItem`-style extension point out of scope (its own "Non-goals" section) rather than carrying the concept forward — this is a permanent, acknowledged gap, not a migration pending that document's implementation.
- **Impact**: any user of a third-party MMSSTV custom-item DLL loses that specific extension with no planned path back.

## MMlink inter-application broadcast

- **Legacy**: `MMlink.cpp`/`mml.h`/`mmrp.h` — a DLL-based link protocol (`mmlOpen`, `mmlSetFreq`, `mmlSetPTT`, `mmlLog`, `mmlEventVFO`) plus `SendMessage(HWND_BROADCAST, m_PSKGNRId, …)` broadcast messages used by `cradio.cpp` to notify other MM-family applications (MMTTY, MMVARI, etc.) of frequency/PTT changes.
- **Replacement**: none. This is Windows-specific inter-process broadcast messaging with no cross-platform equivalent. An earlier draft of this entry named [[spec/04-rigctld]]'s (then-planned) server mode as a possible alternative for the "another app wants to know the current frequency" use case; server mode itself was dropped before implementation (direct user decision, 2026-08-05 — built-in linked Hamlib plus rigctld client-mode coverage is sufficient CAT surface, a server role wasn't worth the added scope), so that alternative no longer exists either.
- **Impact**: users running YONIQ alongside other MM-family (Mori-authored) applications relying on live MMlink coordination lose that integration, with no replacement path.

## Loglink / Turbo HAMLOG live integration

- **Legacy**: `Loglink.cpp` — live IPC (`WM_COPYDATA`) with Turbo HAMLOG, a separate third-party logging application (`m_hLog`, `m_hLogIn`, `m_fHLV5`).
- **Replacement**: none live. [[spec/08-logging]]'s ADIF import/export is a **batch**, not live, integration path — the two logs stay independent, synced by explicit export/import rather than in real time.
- **Impact**: users who used Turbo HAMLOG as their primary log with live sync from MMSSTV/YONIQ need to switch to periodic ADIF export/import, or use Scanline Studio's own built-in logbook ([[spec/08-logging]]) instead of a separate application.

## Contest logging (JASTA) and JARL area codes

- **Legacy**: `yoniq-old/YONIQ-main/JASTA/` — a distinct bundled C++Builder application (`MMJASTA`) with its own logging/conversion/country-lookup code, plus `Mmcg.cpp`/`MmcgDlg.cpp`/`MMCG.DEF` (JARL contest area database) and `NVCG.txt` in the main tree.
- **Replacement**: none. Out of scope for Scanline Studio — this is a separate application bundled alongside MMSSTV/YONIQ historically, not a feature of YONIQ itself, and is not ported.
- **Impact**: users relying on JASTA for contest logging need to continue using the legacy application, or a different contest logger, alongside Scanline Studio.

## Chilkat and FastReport VCL

- **Legacy**: `CkRsa.h`/`chilkatDefs.h` (Chilkat, commercial) and `frxClass.hpp`/`frxCrypt.hpp` (FastReport VCL, commercial, package `frx22`), referenced from `Option.h`.
- **Investigation result**: no call sites for Chilkat types found anywhere in the source tree; FastReport is included but no `Tfrx*` components appear in `Option.cpp`/`Option.dfm`. Both appear to be unused, orphaned, or build-time-only dependencies with no observed runtime feature — see [LICENSES.md](../LICENSES.md) for the license reasoning.
- **Impact**: assumed none, pending confirmation against a built legacy binary (tracked as an open item in [[spec/14-roadmap]]). If either turns out to back a real feature, this entry must be corrected and the feature re-scoped.

## FSK callsign-ID packet (RX) — CORRECTED 2026-08-18, no longer a removed feature

**This entry is stale as a "removed feature" — the CW-ID/FSK station-ID subsystem (6 phases,
shipped 2026-08-12) ported both directions.** Kept here (not deleted) as a record of the earlier
gap and its resolution, per this doc's own removal-rule provenance — verified directly against
current source, not inferred from commit messages.

- **Legacy**: `CSSTVDEM::DecodeFSK`'s modes 5–10 (`sstv.cpp:2465-2551`) — a distinct FSK-coded packet (STX `0x2a`, distinguishable from the mode-announce packet's `0x2d`) carrying a station callsign and optional numeric ID, decoded and surfaced via `m_fskcall`/`m_fskNRS` (referenced at `Main.cpp:3618`). TX side: `CSSTVMOD::OutputFSKID` (`Main.cpp:6904-6965`).
- **Replacement — now real (RX and TX)**: `ScanlineStudio.Core.Sstv.NarrowFskHeaderDecoder` (originally Piece 13 for modes 0–4/16/17/18 only) was extended (commit `c7ac694`, "CW-ID/FSK subsystem Phase 3: RX FSK-ID continuation decoder") to also decode the station-ID packet — `StationIdStxByte = 0x2a` is now routed to its own mode 5-10 state machine (`_stationIdSubPacketCount`/`_stationIdCallsignBuffer`/`_stationIdNrStringBuffer`/`_stationIdNr`, reusing the shared checksum/length-counter fields the same way legacy's `m_fsks`/`m_fskcnt` do across both packet types), verified line-by-line against `sstv.cpp:2378-2606`/`sstv.h:710-717` across two rounds of auditor review. The decoded result reaches the UI via `ISstvDecoder.StationIdDecoded`/`FskStationIdDecodedInfo` → `RxImagePaneViewModel.OnStationIdDecoded`/`ApplyStationIdDecodedAsync`, auto-filling the Transmit tab's Identification-card-adjacent Receive-tab `OverrideCallsign` field (see `spec/16-gui-wiring-survey.md`'s Frame-metadata section). TX side: `ScanlineStudio.Core.Sstv.FskStationIdEncoder`, a direct port of `TMmsstv::OutputFSKID` (`Main.cpp:6903-6965`), wired to the Options window's Identification tab (`FskIdTxEnabled`).
- **Impact**: none remaining — legacy's FSK callsign-ID feature is ported on both RX and TX, structurally independent from the mode-announce packet as legacy's own source is (own leader/guard tone, own TX call sites, shared low-level state-machine fields only). This is no longer a capability gap.

## CQ100 mode (`-i` command-line switch)

- **Legacy**: `sys.m_bCQ100`/`g_dblToneOffset`, both set exactly once, at startup, from a `-i`
  command-line flag (`Main.cpp:1065-1077`) — never reachable via the GUI, an `.ini` setting, or any
  other path (confirmed by a whole-tree grep: no other assignment site for either variable exists
  besides `sstv.cpp:26`'s own `g_dblToneOffset = 0.0` initializer). Four distinct effects, all gated
  behind this same flag: (1) a global **-1000Hz** tone offset (`g_dblToneOffset = -1000.0`) added to
  nearly every hardcoded tone/filter frequency constant throughout `sstv.cpp` — VIS-decode envelope
  detectors, sync-interval/AFC frequencies, bandpass filter cutoffs, AVT training PLL center, `CHILL`'s
  own `m_OFF` (44 distinct referencing lines / 46 occurrences in `sstv.cpp` alone, plus further reads in
  `Main.cpp` and `Option.cpp` for display/labeling only, not DSP); (2) `CHILL::SetWidth` **triples**
  the Hilbert FIR's tap count when this flag is set (`sstv.cpp:3048-3050`, `m_tap *= 3`), independent of
  the sample-rate tiering this port's `HilbertFmDemodulator` constructor already models; (3) an
  `m_OFP` sync-timing shift (`sstv.cpp:1181-1184`): `m_OFP = (d + (1100.0/g_dblToneOffset)) *
  SampFreq/1000.0` — note this is a **division** by `g_dblToneOffset`, not the additive shift every
  other site uses, moving the sync/offset phase by roughly -1.1ms; (4) the AFC capture window
  (`m_AFC_LowVal`/`m_AFC_HighVal`) is narrowed to sync±50Hz in both the narrow and normal branches
  (`sstv.cpp:1678-1681`/`1688-1691`), replacing the wider default capture range — a real DSP effect
  independent of `g_dblToneOffset` itself, gated by the same `sys.m_bCQ100` flag.
- **Replacement**: none. CQ100 is a specific hardware satellite/digital-radio transceiver with a
  shifted audio passband; this port has no command-line-argument entry point (or any other
  equivalent) that could set an analogous flag, and no CQ100-specific hardware integration is planned.
- **Impact**: none for any user of this port today — `g_dblToneOffset`/`sys.m_bCQ100` are confirmed
  at their inert defaults (`0.0`/`FALSE`) on every path this port's own architecture can reach (no
  `-i`-equivalent flag exists to set them otherwise), so every existing tone/filter constant and AFC
  capture window in this port already matches legacy's own real shipped-default (non-CQ100) behavior
  exactly. Only relevant if CQ100 hardware support is ever explicitly scoped in as a new feature, which
  is not currently planned.

## Picture-demodulator selector (`m_Type`: PLL / zero-crossing / Hilbert) — CORRECTED 2026-08-18, no longer a removed feature

**This entry is stale as a "removed feature" — the demod-type runtime-dispatch subsystem (4 phases,
2026-08-12) ported the full 3-way selector.** Kept here (not deleted) as a record of the earlier gap
and its resolution, verified directly against current source, not inferred from commit messages.

- **Legacy**: `CSSTVDEM::m_Type`, a user-facing 3-way dispatch selecting the RX picture demodulator (`sstv.cpp:2256-2268`/`2310-2318`: `case 0` = PLL/`CPLL`, `case 1` = zero-crossing/`CFQC`, `default` = Hilbert/`CHILL`), exposed via `Option.cpp`'s `RGDemType` control and persisted to the `.ini` as `DemType` (`Main.cpp:1937`).
- **Replacement — now real**: `AnalogFmSstvDecoder` genuinely dispatches the main picture-demodulation path between all three ported classes (`PllFmDemodulator`/`ZeroCrossingFrequencyCounter`/`HilbertFmDemodulator`) based on a real `DemodType` setting (`AnalogFmSstvDecoder.cs:9-15,108,787`), a faithful port of legacy's `m_Type` switch. `PllFmDemodulator`'s dual role (AVT's separate training-lock PLL instance vs. this new main-picture-path instance, `AnalogFmSstvDecoder.cs:690-694,798`) is a documented, deliberate distinction, not a conflation. Wired through `SstvDecoderSettings.DemodType` → the Options window's Decode tab (`OptionsWindowView.axaml:~354-361`, 3-way radio group, absent/out-of-range persisted values both clamp to Hilbert matching legacy's compiled-in default) — see `spec/16-gui-wiring-survey.md`'s Decode tab section.
- **Impact**: none remaining. Default-path parity still holds (Hilbert is legacy's real compiled-in/shipped default and the port's own runtime default), and users can now genuinely switch to PLL/zero-crossing the same way legacy's `DemType` allowed. `spec/14-roadmap.md`'s Band-3 item S13 tracking this gap is resolved.

## Legacy UI font switching (WinFont / Japanese-English buttons)

- **Legacy**: `Option.cpp`'s `TOptionDlg::WinFontBtnClick`/`JaBtnClick`/`EngBtnClick` (a font picker dialog plus two one-click presets — `"ＭＳ ゴシック"`/`SHIFTJIS_CHARSET` for Japanese, `"Times New Roman"`/`ANSI_CHARSET` for English), backing `sys.m_WinFontName`/`m_WinFontCharset`/`m_WinFontStyle`, applied across the whole UI.
- **Replacement**: none, and none needed — this exists in legacy specifically to work around CP932/Shift-JIS rendering requiring a matching Windows GDI font+charset pair. Scanline Studio's UI uses a fixed cross-platform font stack (`ScanlineStudio.UI`'s design tokens, `Styles/Cards.axaml`) rendered via Skia, which handles Unicode text (including Japanese) correctly without a manual charset/font-pairing step — the underlying problem this feature solves does not exist in this port's architecture.
- **Impact**: none. No user-facing capability is lost; this is a workaround for a Windows-GDI-specific rendering limitation, not an independent feature. Not added even as a disabled Options-page placeholder (2026-08-06 pass) for this reason — it isn't a "not implemented yet," it's structurally inapplicable.

## Raw-serial RTS-pin PTT keying (`Comm.cpp`'s "RTS on RX" / "PTT lock")

- **Legacy**: `Comm.cpp`'s direct COM-port RTS/DTR-pin PTT toggling (`Comm.cpp:189-219`), gated by two
  Options-dialog checkboxes: `sys.m_RTSonRX` (`Option.cpp:279/438`, `CBRTS` — "RTS mientras escanea,"
  RTS on RX during scan) and `sys.m_TxRxLock` (`Option.cpp:278/439`, `PTTLock` — keep the serial port
  open across TX/RX transitions instead of closing/reopening it each time). Same architectural family
  as `cradio.cpp`'s hand-written per-rig CAT polling (the first entry in this doc) — direct,
  application-owned serial-port control, not a request through an external CAT backend.
- **Replacement**: `IRadioController.SetPttAsync`/`IRadioProtocol.SetPttAsync` — a single,
  protocol-agnostic PTT command sent through whichever real CAT backend is configured (Hamlib linked
  in-process, `rigctld`, flrig; see [[spec/03-cat-layer]]). Investigated 2026-08-12 while scoping the
  Options Radio tab: this port never opens or manages a raw serial port for PTT itself, so there is no
  "RTS pin" or "keep the port open across TX/RX" concept in this architecture for these two settings
  to gate — the backend owns its own connection lifecycle entirely.
- **Correction (2026-08-28)**: the 2026-08-12 investigation above was wrong about `m_TxRxLock`
  specifically, on two points — its actual behavior is "hold the PTT serial port open across TX/RX
  transitions instead of closing and reopening it every time" (not a failure-handling policy, an
  earlier misreading this same day), and Hamlib (already linked in-process) DOES expose a near-exact
  equivalent: its own `ptt_share` config key (`hamlib/src/conf.c`, applied in `hamlib/src/rig.c` for
  `RIG_PTT_SERIAL_DTR`/`RIG_PTT_SERIAL_RTS`), reachable through the same `ApplyConf` path
  `HamlibRadioProtocol.cs` already uses for other settings. So a narrow, Hamlib-only,
  DTR/RTS-PTT-type-only exposure of this WAS technically buildable, contrary to the original "no
  concept in this architecture" claim. Found during a fresh plan-review pass on the still-present
  disabled Options-tab placeholder (never actually deleted from the UI back in 2026-08-12 despite
  being documented as removed here) — the port chose removal anyway (direct user decision, informed
  by the corrected facts): `ptt_share`'s applicability is narrow enough (2 of several PTT types, one
  backend of three) that a real toggle for it wasn't judged worth building. `m_RTSonRX`'s own removal
  reasoning is unaffected by this correction — no equivalent was found for that one, corrected or
  otherwise.
- **Impact**: users of any Hamlib/rigctld/flrig-supported rig are unaffected — PTT keying works the
  same way regardless of whether legacy's own RTS-pin timing quirks would have mattered. No known
  real-world case is lost: `m_RTSonRX`'s "toggle RTS during an auto-scan" behavior and `m_TxRxLock`'s
  "avoid the port-reopen delay/glitch on every TX/RX transition" were both workarounds for the raw
  serial-port ownership model specifically, not independent user-facing features with their own value
  outside that model. A user relying on Hamlib's own `ptt_share=0` default (hold the port open, the
  same behavior legacy's own default (`m_TxRxLock=1`) already matched) gets it automatically from
  Hamlib itself with no app-level setting needed; a user who specifically wants `ptt_share=1`
  (release the port between transmits, e.g. to let a separate logger share it) has no way to request
  that in this port.

## YONIQ-fork external "log connection" (raw IP:port socket)

- **Legacy**: `Option.cpp`'s `GroupBox1`/`CheckBox1`/`Edit1`/`Edit2` (`FormShow`/`CheckBox1Click`/`FormCloseQuery`) — a YONIQ-fork-specific (not stock MMSSTV) toggle connecting `Mmsstv->ClientSocket1` to a user-entered IP:port, persisting `logconect`/`logip`/`logport` to a memo-backed config. No protocol documentation, message format, or companion-application identity exists anywhere in the available legacy source — the socket is opened and left connected, with no visible read/write logic beyond the connect toggle itself.
- **Replacement**: none, and none planned. `spec/08-logging`'s ADIF batch import/export is the closest analog for any "another app wants my QSO/frequency data" use case (see the separate MMlink/Loglink entries above for the same reasoning) — a live raw-socket link to an unspecified companion app is not a designed feature of this port.
- **Impact**: unknown/unquantifiable — no record of what real-world tool(s), if any, ever connected to this socket. Not added even as a disabled Options-page placeholder (2026-08-06 pass): unlike every other placeholder added that pass (which represent a real, nameable future feature), this one has no spec to point a "not yet implemented" tooltip at without inventing one.

## `m_MSync` sync-based mode-start toggle

- **Legacy**: `sstv.cpp`'s `CSSTVDEM::Do` gates its entire sync-interval-bypass detection block (all
  three `m_sint1`/`m_sint2`/`m_sint3` trackers — the mechanism that can start decoding from a
  detected sync-pulse cadence alone, without a full VIS header) on `if(!m_Sync && m_MSync)`
  (`sstv.cpp:1899`, and again at `:1949`/`:1953`/`:1959`), not on `!m_Sync` alone. `m_MSync` is a
  real, persisted user option (`Option.cpp:344`/`:610`'s `RGMSync` radio group, ini key
  `Define/SyncStart`, `Main.cpp:1856`/`:2428`), default on (`sstv.cpp:1485`) — the same shape as
  `m_SyncRestart`, which this port DID expose as a real setting
  (`SstvDecoderSettings.SyncRestartEnabled`).
- **Replacement**: none. Found during the functional-audit sweep (chunk D5, round 2,
  2026-08-19): `AnalogFmSstvDecoder.cs`'s port of this block (`TrySyncIntervalDetectionStep` and its
  callers) implements only the `!m_Sync` half of legacy's gate — the block always runs whenever
  `_mode is null`, with no equivalent of `m_MSync` at all, even though two comments at that call
  site quote legacy's full `!m_Sync && m_MSync` condition while the code next to them only checks
  half of it.
- **Impact**: unaffected at shipped defaults (`m_MSync` defaults on in legacy too, so the common case
  behaves the same). A legacy user who had explicitly turned this option OFF — disabling
  sync-pulse-only mode detection and requiring a full VIS header lock every time — has no way to
  reproduce that in this port: sync-bypass detection is always active here. No golden-vector or
  decode-correctness impact (this is a detection-trigger toggle, not a DSP/decode-math difference),
  but it is a real, silently-dropped user-facing capability, not a documented scope cut. Revisit if a
  user reports needing header-only lock (e.g. to avoid false-triggering on a busy band's sync-like
  interference) — the fix is a new `SstvDecoderSettings` flag mirroring `SyncRestartEnabled`'s own
  existing wiring, not a DSP change.

## RX history retention limit (auto-delete beyond newest 32)

- **Legacy**: `sys.m_HistMax = 32` (`Main.cpp:898`, read from ini `[Window]/HistMax`,
  `Main.cpp:1811`), applied unconditionally to the ring buffer's `CBitmapHist::m_Head.m_Max` on
  every `Open()` (`ComLib.cpp:2658-2686`, both the fresh-file and existing-file branches) — the
  class's own constructor default of 64 (`ComLib.h:591`) never survives to be the effective
  default. `HISTMAX 256` (`ComLib.h:561`) is a separate hard array-size cap, not this default.
- **Ported, then reversed**: this WAS a faithful port (2026-08-06) — `SqliteReceiveHistoryStore`
  deleted the oldest queryable rows beyond a configurable `MaxEntries` (default 32) after every
  insert, exempting any row carrying a note/flag/QSO-link. Reversed by direct user decision
  (2026-08-26): the Gallery tab's "All" filter is new UI with no legacy equivalent at all — legacy's
  own ring buffer has no "show everything ever received" concept to preserve, so "All" showing
  fewer than every recorded image read as broken, not as a faithful cap. `ReceiveHistorySettings.MaxEntries`/`DefaultMaxEntries`
  and `SqliteReceiveHistoryStore.TrimToRetentionLimitAsync` are deleted outright, not just disabled
  — every recorded entry now stays in `history.db` indefinitely.
- **Replacement**: none (by design) — no retention/pruning mechanism of any kind exists today.
- **Impact**: an installation's `history.db` and the RX-history-referenced PNG files under it now
  grow unbounded over the app's lifetime instead of being capped at ~32 untouched entries. No
  decode-correctness impact — this only affects the Gallery tab's browsable index, not any DSP/
  decode path. A future manual "prune old entries" action, or a configurable/visible retention
  setting, remains open if unbounded growth turns out to matter in practice — not designed here,
  since it wasn't asked for.

## Macro %D/%T clock-offset correction

- **Legacy**: `MacroText`'s `%D`/`%T` tokens (`Main.cpp:10762-10776`) call `GetUTC` (`ComLib.cpp:319-323`),
  which applies the user's own `m_TimeOffset`/`m_TimeOffsetMin` clock-correction setting
  (`ComLib.cpp:249-251`) on top of `GetSystemTime` before formatting.
- **Replacement**: none. Found during the functional-audit sweep (Tier A Batch 10, chunk 10b, round 1,
  2026-08-22). `MacroTextResolver.ResolvePercentTokens` reads `DateTime.UtcNow` directly, with no
  equivalent offset setting anywhere in this port (confirmed by grep).
- **Impact**: unaffected for the common case (a user whose system clock is accurate needs no offset,
  so `m_TimeOffset` defaults to zero). A legacy user who had configured a non-zero clock-offset
  correction (compensating for a known-wrong system clock) would see `{name}`'s `%D`/`%T` fill values
  resolve to a different date/time in this port than legacy would have produced, with no setting to
  reproduce the correction. No decode-correctness impact — this only affects text baked into a TX
  overlay via macro substitution, not any DSP/decode path.

## RGLoopBack hardware loopback (Internal/External)

- **Legacy**: `sys.m_echo` (`Option.cpp`'s `RGLoopBack` 3-way radio group — Off/Internal/
  External, `[Define] TXLoopBack`) runs a REAL, simultaneous TX+RX session: audio is genuinely
  transmitted while capture keeps running, either looped back internally in software (`Sound.cpp`'s
  "Interno" path) or through real external hardware (a physical audio cable from output back to
  input). Both modes let the operator monitor their own signal live, during an actual on-air (or
  cabled) transmission.
- **Replacement**: partial. Stub survey Tier 3 (2026-08-26), "Loopback self-test" (Calibration menu)
  — `ISstvSessionService.RunLoopbackSelfTestAsync` — is a one-shot SOFTWARE preview: encode the
  currently loaded TX image, decode it back entirely in-process, show the result in a dialog. This
  port's TX/RX are architecturally mutually exclusive (`ISstvSessionService.TransmitAsync`'s own doc
  comment — capture is paused for the whole duration of a transmit), so a literal port of either
  Internal or External mode is impossible without a fundamentally different capture/playback
  architecture. User decision (AskUserQuestion, 2026-08-26): build the simpler one-shot preview
  instead of pursuing that architecture change.
- **Impact**: NOT a substitute for either legacy mode. No live monitoring during a real transmission
  (no PTT is keyed, no audio device is touched at all — the whole round trip runs synthetically, in
  memory). No real hardware or sound-card path is exercised, so this cannot catch a genuine
  audio-chain problem (a bad cable, a misconfigured output device, RF getting into the audio path)
  the way External mode could. No real TX sample-clock drift is measured either — the round trip has
  no physical clock in it, so the self-test always runs with a synthetic zero-offset encode
  regardless of the user's persisted TX clock-offset setting (see that setting's own "Clock
  calibration" entry above), and must never be read as a calibration/drift measurement. What it DOES
  give: a way to sanity-check that the current TX image/mode round-trips through this port's own
  encoder→decoder pipeline before going on air — a real, useful diagnostic, just a narrower one than
  either legacy mode provided.
- **Addendum (2026-08-28)**: the disabled `RGLoopBack`-shaped placeholder radio group left over in
  `OptionsWindowView.axaml`'s Advanced tab was removed — a leftover UI stub from before this decision
  shipped, not a new decision. No behavior change; this decision was already final.

## Polynomial (piecewise-linear) demod-level calibration + Level Calibration Wizard

- **Legacy**: `sys.m_DemCalibration` (`CBCalWay`, `Option.cpp:372/558`, ini key `[Define]
  ColorCalibration` — a misleading key name, unrelated to color) toggles an alternative, nonlinear
  demodulated-PIXEL-LEVEL/brightness correction curve in place of the default linear `sys.m_DemOff`
  offset correction (`GetPixelLevel`, `Main.cpp:4038-4046`). Not a frequency-domain or TX correction.
  `MakeCalibrationTable()` (`Main.cpp:12556-12588`) is NOT a fitted polynomial despite the name (a
  `pow(x,i)`-based `Teira()` function exists in the source but is dead, `#if 0`d out) — the real,
  shipped algorithm is a 17-breakpoint piecewise-linear lookup table (`sys.m_Dem17[17]`, ini section
  `[Polynomial]`, another misleading key name), built once into a cached 4097-entry `short` table and
  re-applied per pixel. Per-profile, not global — part of the same `SetProFile`/`InitProfile` demod-
  profile-slot system items 1/2 of this backlog both already found and ported (`ComLib.h`'s
  `DemCalibration`/`Dem17[17]` fields on `m_DemPro[9]`, `Main.cpp:12344-12346` save,
  `:12389-12391` load).
- **Correction (2026-08-28, code-review finding): the "Level Calibration Wizard" does NOT require
  simultaneous TX+RX — an earlier version of this entry claimed it shared item 4's (`RGLoopBack`)
  architectural blocker, and that claim is wrong, verified directly against source.**
  `TOptionDlg::TimerTimer` (`Option.cpp:880-963`) sweeps a synthetic test tone (`sys.m_TestDem`)
  across 17 points (1500Hz + 16×50Hz steps), averaging 3 samples per point into `sys.m_Dem17[]` — but
  `SBTestClick` explicitly leaves TX first if it was active (`Option.cpp:988`,
  `if(SBTX->Down||SBTune->Down) ToRX();`), and the sweep itself is a PURE IN-PROCESS SOFTWARE
  round trip: `sys.m_TestDem` makes the modulator's `Do()` return the synthetic tone directly
  (`sstv.cpp:2857-2859`), the RX capture buffer is overwritten IN PLACE with that synthetic output
  (`Sound.cpp:336-340`) — the real microphone input is discarded, nothing is ever sent to the output
  device (`Wave.OutAbort()`, `Sound.cpp:322`, `m_Tx==0`) — and `pDem->m_CurSig` is read back from
  that same synthetic signal (`sstv.cpp:2309-2320`). **No audio hardware carries the signal** — the
  capture device stays open and its own read call still paces the demod loop (`Wave.InRead`,
  `Sound.cpp:327-333`), but every SAMPLE it returns is discarded and overwritten before use. This
  is architecturally closer to this port's own already-shipped `RunLoopbackSelfTestAsync` (encode
  synthetically, decode synthetically, all in-process) than to `RGLoopBack`'s real full-duplex
  requirement — a genuinely different, and considerably more buildable, mechanism than first assumed.
  The wizard ALSO doubles as the measurement pass for the (separately still-unported) LINEAR
  `DemOff`/`DemWhite`/`DemBlack` correction fields (`Option.cpp:895/913/931`, disabled when the
  polynomial checkbox is checked, `:213-219`) — not exclusively a polynomial-table data-collection
  tool.
- **The real reason this stays out of scope: this port's decoder has NO pixel-level-correction hook
  at all to plug a calibration table into, not an architecture blocker on the wizard itself.** Legacy's
  `GetPixelLevel` (`Main.cpp:4038-4046`) is a real, configurable, ALWAYS-PRESENT correction stage
  (linear `DemOff` by default, this nonlinear table as the alternative) that every decoded pixel
  passes through. This port's own decoder has never ported that stage at all — pixel levels use
  hardcoded legacy-default constants directly (`YCbCr.cs:20-22`), with no live `DemOff`/`DemWhite`/
  `DemBlack` UI and no correction hook of any kind. Building JUST the polynomial-table application
  half means first retrofitting a whole new configurable per-pixel correction stage the decoder
  doesn't have today — genuinely large, out of scope for an Options-stub fill (matches this
  backlog's own original framing of this item as "the largest confirmed item"). The wizard could be
  built (it's simpler than first thought), but without a decoder-side hook to feed, it has nothing to
  calibrate.
- **Replacement**: none. Removed entirely (both the "enable polynomial calibration" checkbox AND the
  wizard button) rather than building a wizard with no decoder-side effect. Found and removed during
  the Options-tab stub backlog (item 5, 2026-08-28); a code-review pass caught and corrected the
  wizard's own real mechanism after this entry's first draft got it wrong (see the correction above) —
  the removal call itself survives on the corrected, narrower grounds stated here.
- **A real safety note for anyone reconsidering this later, corrected**: legacy's own
  `MakeCalibrationTable()` divides by the gap between adjacent breakpoints (`16.0 /
  (sys.m_Dem17[j] - sys.m_Dem17[j-1])`, `Main.cpp:12572`) with no zero-guard — but `InitProfile`
  (`Main.cpp:12205-12245`) seeds all 9 built-in profiles with a real, strictly-monotonic 17-value
  default table, so the "all-zero, never-configured" scenario an earlier draft of this entry
  described is NOT reachable in legacy. The real reachable trigger is narrower: a hand-corrupted
  `[Polynomial]` ini section producing `Dem17[0] == Dem17[1]` (the `j==1` case only — for `j>=2`,
  reaching that branch already implies a non-zero gap by construction). Any future port of the
  table-application half would still need an explicit adjacent-breakpoint-equality guard legacy
  itself never had, just for this narrower, corrupted-config case rather than a default-state one.
- **Impact**: an alternative nonlinear brightness-correction curve, used by a small minority of legacy
  users who ran the wizard (2 of legacy's 9 built-in demod profiles ship with the flag pre-enabled,
  `Main.cpp:12256,12264` — their actual `Dem17[]` values weren't inspected, out of this research
  pass's scope). Corrected wording (code-review finding — the original said the linear correction was
  "already fully ported," contradicting this entry's own point above): legacy's default linear
  `sys.m_DemOff`/`m_DemWhite`/`m_DemBlack` values are baked into this port's decoders as fixed
  constants (`RobotScanlineDecoder.cs:33`'s `PixelLevelScaleFactor`), not ported as a configurable
  stage — so behavior matches legacy's own default (unchecked) state, but there is no live `DemOff`
  setting behind it either, same as the polynomial table's own missing hook.

## 7 per-element waterfall/spectrum colors (PCLow/PCHigh/PCFFTB/PCFFT/PCFFTStg/PCSync/PCFreq)

- **Legacy**: `sys.m_ColorLow`/`m_ColorHigh` (waterfall gradient endpoints, real defaults `clBlack`/
  `clWhite`, `Main.cpp:806-807`), `m_ColorFFTB` (FFT panel background, `TColor(4227327)` = RGB
  `(255,128,64)`, VCL's BGR-order encoding), `m_ColorFFT` (main spectrum trace, `clYellow`),
  `m_ColorFFTStg` (the max-hold trace over `m_FFTMAX`, `Main.cpp:3391-3396`, gated by the `m_FFTStg`
  setting, `Main.cpp:11578-11596`; `clBlue`), `m_ColorFFTSync`/`m_ColorFFTFreq` (sync/
  frequency marker lines, `clLime`/`clYellow`) — all 7 real, independently user-editable colors
  (`Option.cpp:269-275` load, `:577-583` save), persisted individually (`[Color]` ini section,
  `Main.cpp:1840-1846` load / `:2354-2360` save). Applied via direct VCL `Brush`/`Pen->Color`
  assignment at several `Main.cpp` draw sites (`:971-972,3210,3263-3281,3319,3392,3462`) and
  `InitColorTable(sys.m_ColorLow, sys.m_ColorHigh)` (`ComLib.cpp:338-361`), which builds a flat
  2-color LINEAR gradient table for the waterfall.
- **Replacement**: a fixed, deliberately-designed modern palette, not a per-element user override.
  `WaterfallPalette.cs` (already shipped, its own doc comment cites `spec/09-ui.md` explicitly
  exempting this visualization from strict legacy-port fidelity, and `spec/14-roadmap.md`'s own
  waterfall backlog entry inviting exactly this) replaces legacy's flat black/white 2-stop
  interpolation with a 6-stop SDR-style multi-hue heatmap gradient (WSJT-X/SDR++/GQRX convention) —
  a deliberate, already-shipped design improvement, not a stub. `SpectrumTraceControl.cs` similarly
  already has fixed, cohesive equivalents for the 4 trace/marker colors (`TracePen`/`PeakHoldPen`/
  `SyncMarkerPen`/`FreqMarkerPen`, plus a `NotchMarkerPen` legacy never had at all); the FFT-panel
  background equivalent lives in the shared theme instead (`Atoms.axaml`'s `IndustryPlot` style,
  `Background="{StaticResource IndustryAccent900}"`) — all 7 legacy colors have a fixed, non-
  user-editable equivalent SOMEWHERE in this port's already-shipped code, just not all in one file.
  This is a real prior design decision this backlog item's own research surfaced, not something
  invented to justify removal.
- **Why not build the 7-color-picker UI instead**: doing so would let a user override this already-
  shipped, deliberately cohesive palette with arbitrary colors that clash with the surrounding
  "Industry" design system — working against a design decision already made and shipped, not filling
  a genuine gap. Found and removed during the Options-tab stub backlog (item 6, 2026-08-28), the same
  pattern items 4-5 of that backlog already established (research revealing a real reason not to
  build as originally scoped, not an oversight).
- **Impact**: a user cannot recolor individual waterfall/spectrum elements to their own taste the way
  legacy allowed. The waterfall's own low-to-high semantic (weakest signal -> near-black, strongest
  -> red) is preserved, just via a richer fixed gradient instead of a user-chosen 2-color one.

## Differentiator (picture-channel edge-enhancement filter)

- **Legacy**: `sys.m_Differentiator` (`CBDiff`, `Option.cpp:253,508`) + `sys.m_DiffLevelP`/
  `m_DiffLevelM` (a slider mapped 0.0-10.0 in 0.1 steps — `TBDiff`'s own real `Min`/`Max` live in a
  binary `.dfm`, unreadable as text, so the 0-100 slider-position range is a reasonable inference from
  the ×10/÷10 scaling, `Option.cpp:254,509-510`, not independently confirmed; `m_DiffLevelM =
  m_DiffLevelP / 3.0` always, an asymmetric attenuation) — real GLOBAL default `Differentiator=0`
  (off), `DiffLevelP=1.0` (`Main.cpp:827-829`) — but the PER-PROFILE default differs: `InitProfile`
  seeds `DiffLevel=0.8` (`Main.cpp:12225`), not 1.0. Persisted per-profile (same `SetProFile`/
  `InitProfile` system items 1/2/5 of this backlog all hit, `ComLib.h:161-162`'s `Differentiator`/
  `DiffLevel` fields, `Main.cpp:12348-12349` save/`:12395-12397` load) and also to `[Define]`
  `Differentiator`/`DiffLevel` ini keys (`Main.cpp:1867-1869` load/`:2421-2422` save). Applied via
  `GetPictureLevelDiff` (`Main.cpp:4075-4100`), a discrete Laplacian-like (second-derivative) sharpening
  kernel — `o = -0.5*d + m_Z[0] - 0.5*m_Z[1] + m_Z[2]` — with asymmetric post-gain applied by sign;
  only 2 DISTINCT history taps, not 3 (`m_Z[2] = m_Z[0] = d` on the same line, `:4098`, so `m_Z[2]`
  always duplicates `m_Z[0]`) — dispatched from `DrawSSTV` (`:4113-4120`) in place of the normal
  drawing path whenever the checkbox is on and the current mode isn't `smSCTDX` (reason for that
  specific exclusion not found in the source).
  **Corrected (code-review finding): NOT luminance-only.** An earlier draft of this entry claimed the
  differentiator applies to luminance only — false, checked directly against every mode-family case in
  `DrawSSTVDiff`. It runs on ALL THREE R/G/B channels for RGB-family modes (`smSCT1`/`smSCT2`,
  `Main.cpp:4595-4636`; the `default:` case covering most remaining modes, `:4822-4851`) — luminance-
  only ONLY for the YC-family modes (R36 `:4644`, R24/R72/MRxx/MLxx `:4695`, PD/MP/MN `:4750`, RM8/RM12
  `:4804`, the YC-paired Y channel `:4786`). What IS accurate: every CHROMA (R-Y/B-Y) extraction site
  stays a plain, undifferentiated `GetPixelLevel` read in every mode family, including RGB ones (the
  R/G/B "channels" in an RGB-family mode ARE its picture channels, not a chroma pair) — the
  differentiator never touches a true chroma-difference channel, in any mode.
- **The real blocker: `GetPictureLevelDiff` itself calls `GetPixelLevel` internally** (`d =
  GetPixelLevel(ip+SSTVSET.m_KSB)` or `d = GetPixelLevel(ip)`, `Main.cpp:4079-4087`) — the exact
  same decoder-side pixel-level-correction hook already found missing from this port entirely (see
  the "Polynomial (piecewise-linear) demod-level calibration" entry above): this port's decoder uses
  hardcoded legacy-default pixel-level constants directly (`RobotScanlineDecoder.cs:33`'s
  `PixelLevelScaleFactor`, `PixelSampleReader.cs`'s own raw-sample-read/KSB-peak-pick, the
  dereference half of `GetPixelLevel` with no correction half behind it),
  with no configurable correction stage of any kind to layer a sharpening filter on top of. Porting
  the differentiator would ALSO mean touching every one of this port's 5 concrete per-mode-family
  decoder classes at their own picture-channel extraction call site(s) — one site for the YC families,
  THREE sites (R/G/B) for the RGB families, given the corrected scope above — contained in shape per
  class, but touching all 5, not a single isolated addition, and still resting on the same missing
  foundational hook.
- **Replacement**: none. Removed 2026-08-28 alongside the polynomial-calibration removal above, same
  backlog pass, same root cause (not a coincidence — the Differentiator is literally built on top of
  the calibration feature's own `GetPixelLevel` call).
- **Impact**: no luminance edge-enhancement/sharpening option for the decoded image. A small minority
  of legacy users who enabled this (default off) lose it; the vast majority (default configuration)
  see no change, since legacy's own default is off.

## Audio-tab performance placeholders: RX/TX FIFO size, Sound card thread priority, App priority

- **Legacy**: `Option.dfm`'s RX/TX sound-buffer (FIFO) size spinners (Win32 `waveIn`/`waveOut` buffer
  count), `m_SoundPriority` (a 4-level sound-thread priority radio group), and `AppPriority` (a
  2-level `Normal`/`High` process-priority radio group backing `SetPriorityClass`).
- **Replacement**: none, and none planned. Found and removed 2026-08-27 during an Options
  restart-required audit, under a "does this have any measurable benefit on a modern OS" pass (the
  same lens that already justified dropping the Clock-adjust wizard, below).
- **Why**:
  - RX/TX FIFO: legacy's Win32 `waveIn`/`waveOut` buffer-count concept has no analog in this port's
    MiniAudio-based audio engine, which manages its own buffering automatically. This was a disabled
    placeholder in this port from the start — never functional here, and per the point above, never
    going to be.
  - Sound priority: legacy's own `m_SoundPriority` was read from and written to `Config.cfg` but
    never actually applied to anything — confirmed directly against legacy source, this was dead UI
    even in the original MMSSTV/YONIQ. Also a disabled placeholder in this port from the start.
  - App priority: this one DID work in this port — a real, live `Process.PriorityClass` toggle,
    applied once at startup. Removed anyway: on a modern OS, the mechanism that actually prevents
    audio glitches under load is OS/audio-stack-specific (Windows MMCSS, Linux PipeWire/JACK
    real-time threads, macOS Core Audio thread policies), not whole-process scheduling priority.
    Raising it also has a real downside with little corresponding benefit — a "High"-priority process
    can make the rest of the system feel sluggish under contention. This port's real capture-thread
    priority knob (`AudioDeviceSettings.CaptureThreadPriority`, a different, already-existing
    mechanism) is the more targeted tool for this problem, and remains in place — this removal is
    scoped to the whole-process `AppPriority` toggle only.
- **Impact**: no functional loss for any user. Two of the three were never wired to anything in this
  port; the third worked but its real-world payoff on current hardware/OS audio stacks was judged not
  worth the settings-file field, DI wiring, and UI surface it required. `AppPerformanceSettings` (the
  settings record backing App priority) is deleted entirely, not deprecated in place — a
  `settings.json` with a stale `AppPerformance` section from before this change is read via
  `ISettingsStore`'s normal unknown-section tolerance and simply ignored, not migrated.

## Advanced-tab "Clock-adjust wizard" button (Options dialog)

- **Legacy**: `ClockAdj.cpp`, a live master-clock calibration wizard invoked from
  `Option.cpp:791-805`'s `SBClockAdjClick` — real-time RX audio through a tone-locked envelope
  detector, a scrolling waterfall-style canvas, and a manual two-click line-marking interaction to
  correct the app's assumed sample rate against a WWV/JJY/BPM time-standard tone.
- **Replacement**: this port's Auto Slant mechanism, which auto-corrects per-line clock drift on
  every reception automatically, tested to a 500 ppm and a 1% (10,000 ppm) mismatch
  ([[spec/06-sstv-dsp]], `SlantTests.cs`) — well past any real-world sound-card clock error.
- **Why**: a live-port design was scoped and passed a round-1 auditor plan-review (with fixable
  defects: wrong scroll direction, a click-anchor drift bug, a missing AGC/intensity mapping, an
  `internal`-visibility compile error, several sign/rounding bugs) — buildable, but non-trivial.
  Before a round-2 review, the team re-examined whether it was worth building at all: the wizard's
  only remaining edge over Auto Slant is faster lock on a reception's first few lines, before Auto
  Slant's own tracking catches up. That narrow payoff didn't justify the build cost. User decision:
  "maybe one day." The Calibration-menu item and its locale key were removed 2026-08-26; a leftover,
  already-disabled duplicate button on the Options dialog's Advanced tab (a placeholder that
  predated, and was never reconciled with, that menu removal) was found and removed in the same pass
  as the Audio-tab items above, 2026-08-27.
- **Impact**: no functional loss — this was never implemented in this port; only two dead UI entry
  points (a menu item, then a placeholder button) pointing at it are gone. A user who needs
  faster-than-Auto-Slant clock lock on a reception's very first lines has no path to that in this
  port; no user has asked for this since Auto Slant shipped.

## Tune-timer auto-switch-to-transmit ("satellite" mode)

- **Legacy**: `sys.m_TuneSat` (`Option.h`/`Option.cpp`), checked in `TMmsstv`'s main timer loop
  (`Main.cpp:3689-3697`) against `m_TuneTimer` (armed at `Main.cpp:7680-7686` when the Tune button is
  pressed, set to `::GetTickCount() + sys.m_TuneTXTime * 1000`, or `+30s` if `TuneTXTime` is negative).
  When the timer expires while Tune is still held, legacy calls `ToTX()` instead of the normal
  `ToRX()` if `m_TuneSat` is enabled — auto-switching straight to transmit once the tune tone's
  duration elapses, for satellite passes where TX should follow tuning immediately with no manual
  step in between.
- **Replacement**: none. This port's Options > Identification tab has an already-disabled
  `IsEnabled="False"` "Tune satellite" placeholder checkbox (with help text already describing this
  exact legacy behavior, written before this entry existed) — removed outright 2026-08-28, not
  implemented.
- **Why**: direct user decision, made with the real legacy behavior in hand (an initial research pass
  incorrectly reported no legacy grounding at all, since the field is named `m_TuneSat` rather than
  containing the literal word "satellite" — corrected before this decision was made, not after). This
  app has no supported satellite-pass workflow the auto-switch would serve.
- **Impact**: a user tuning ahead of a satellite pass must switch from Tune to Transmit manually once
  ready, instead of it happening automatically when the tune timer elapses. No other Tune-button
  behavior is affected — the timer/duration mechanism itself (`TuneTXTime`) has no other consumer in
  this port to begin with.

## VOX leader-tone priming

- **Legacy**: `sys.m_VOX` (`RGV`, a 2-way radio group Off/On, `Option.cpp:401,621`, `[Define] VOX`
  ini key, real default `0`/off, `Main.cpp:822`). **Correction — the earlier backlog inventory row
  for this item mischaracterized its real mechanism** (guessed "needs an audio-level-triggered PTT
  path `IRadioSessionService` doesn't have," an assumption never checked against source): the real
  feature is nothing like that. It is a fully self-contained TX-audio-generation change with no
  external hardware/detection dependency at all. `OutHEAD` (`Main.cpp:7270-7351`, the function that
  writes the fixed 8-tone leader-tone burst before every VIS header — already fully ported in this
  codebase as `AnalogFmSstvEncoder.GetLeaderToneDurationMs`/its own header-generation path) branches
  on `sys.m_VOX`: mode 0 (off) writes legacy's normal fixed leader pattern, unchanged, matching this
  port's own current always-on behavior; mode 1 (on) instead parses `sys.m_VOXSound` — **not a file
  path despite the field name** — a plain, directly user-editable comma-separated
  frequency(Hz)/duration(ms) text string (real default
  `"1500,100,1700,100,2300,100,2100,100,1900,100,1500,100"`, 6 pairs, `Main.cpp:824`; edited via the
  "Edit VOX tone" button's own text-box dialog, `Option.cpp:1184`; persisted directly as that string,
  `[Define] VOXTone` ini key with a Japanese-locale CR/LF escape encoding, `Main.cpp:1898-1899,2407`)
  — and writes THOSE tones instead (each tone's frequency clamped `[0,2800]` Hz and duration
  defaulted to 100ms if `<=0`, `Main.cpp:7324-7329`), capped at a total duration limit (1800ms for AVT
  mode, 8000ms otherwise, `Main.cpp:7320-7336`). THREE directives are recognized in place of a tone
  sequence, not two: `#id` (send FSK ID instead) and `#cw` (send CW ID instead), `Main.cpp:7300-7308`
  — plus `#<n>` (skip `n` further lines of the underlying multi-line tone-sequence text and use the
  line landed on instead, `Main.cpp:7309-7315`; the "Edit VOX tone" dialog opens the field as a real
  multi-line text box, `Option.cpp:1184`'s own `TRUE` argument, so multi-line values are reachable, not
  theoretical). The real-world purpose: priming an external VOX-activated transmitter/relay with a
  longer or differently-shaped tone sequence than the normal 8-tone burst, giving that external
  hardware's own voice-operated-switch circuit enough time to key up before the actual SSTV picture
  data begins — a real ham-radio accessory-compatibility feature, genuinely simpler to build than
  first assumed. **`sys.m_VOX` also gates the TX FOOTER, not just the leader**
  (`Main.cpp:6999-7008`/`SendSSTV`): with VOX off (this port's own only-modeled state) and a non-
  narrow mode, the footer sends a capped carrier (`WriteC(1500, min(m_TW, SampFreq/2))`) followed by
  an alternating 1900/1500/1900/1500 tail; VOX on instead sends a plain, non-alternating `WriteC(1900,
  ...)` carrier with no tail — this port's own encoder code already correctly models only the VOX-off
  footer arm (`AnalogFmSstvEncoder.cs`'s own `FooterAlternatingToneDurationMs` and its neighboring
  comment, corrected 2026-08-28 alongside this entry), the same real default legacy itself ships.
- **Replacement**: none. Removed by direct user request (2026-08-28), after the corrected finding
  above was reported — the feature turned out buildable (no architecture gap, unlike several other
  items this session), but the user chose removal anyway rather than building it. Note: "Edit VOX
  tone" above is THIS PORT'S OWN button label (`Options.Identification.Vox.EditTone`, now deleted) —
  legacy's real equivalent prompt was Spanish, `"Tono del VOX  freq(Hz), tiempo(ms), ..."`
  (`Option.cpp:1184`, this YONIQ fork's own localization), not an English label.
- **Impact**: a user relying on an external VOX-activated transmitter/relay behind this port's TX
  audio output cannot customize the leader-tone priming sequence (or get the alternate footer shape)
  to suit that hardware's own VOX timing — this port always sends legacy's fixed 8-tone burst and
  alternating footer (mode 0's own behavior), matching
  legacy's own default (unchecked) state.

## Camera/webcam TX image source

- **Legacy**: `PicSel.cpp`'s camera-capture path — lets an operator use a live webcam frame as the TX
  source image, alongside the file/clipboard sources this port already has.
- **Replacement**: none. Removed by direct user request (2026-08-29), a `ui_transition_plan.md` Tier
  3 scope decision — not attempted, not architecturally blocked, purely a "don't want it" call.
- **Impact**: an operator wanting to send a live camera frame must capture it with separate software
  first and import it as a normal file/clipboard image — no in-app camera capture exists or is
  planned.

## Legacy `.mtm`/`.mti` template import

- **Legacy**: `Draw.cpp`'s `CDrawGroup::SaveToStream`/`LoadFromStream` (lines 4963-5385, per-element
  records dispatched via `CM_*` in `Draw.h:80-88`) — the binary format backing legacy's saved TX
  templates (`def1-5.mtm`, `t1-5.mtm`, `Stock/*.mtm`, `Current.mtm`) and its single-item variant
  (`.mti`, confirmed to share the identical `CDrawGroup::LoadFromStream` path, differing only in the
  file-picker's extension filter — `Main.cpp:10090`/`10134`).
- **Replacement**: Scanline Studio's own native `.sstemplate` bundle format
  (`ui_transition_plan.md` step 13, shipped 2026-08-29) covers template sharing/portability going
  forward — but it is a Scanline Studio-to-Scanline Studio format only, not an importer for existing
  legacy content. `spec/15-template-designer.md`'s own "Decisions"/"Deferred, not dropped" sections
  originally scoped `.mtm` import as a real (if sequenced-later) goal; this entry and that document's
  own corrected "Rejected" section supersede that.
- **Impact**: an operator with existing `.mtm`/`.mti` templates from a legacy MMSSTV/YONIQ install
  cannot import them into Scanline Studio — they would need to manually recreate the template in the
  new TX Template Editor. Removed by direct user request (2026-08-29), a `ui_transition_plan.md` step
  14 / Tier 3 scope decision — not attempted, not architecturally blocked (the format is fully
  specified by `Draw.cpp`, per that document's own research), purely a "don't want it" call.

## QSSTV specialist workflows (digital SSTV, DRM, hybrid/FTP upload, repeater support)

- **Legacy**: not a YONIQ/MMSSTV feature at all — these are QSSTV-specific workflows (QSSTV is this
  project's secondary cross-reference for DSP/decoding edge cases only, per `CLAUDE.md`'s top-of-file
  precedence note, never a port target in its own right). No `yoniq-old/` source implements digital
  SSTV modes, DRM, hybrid/FTP picture upload, or SSTV repeater support.
- **Replacement**: none, and none planned. Scanline Studio stays a pure analog-SSTV application,
  matching its actual port target (YONIQ/MMSSTV).
- **Impact**: an operator wanting digital-SSTV modes, DRM, FTP-based picture sharing, or repeater
  operation needs a separate application (e.g. QSSTV itself, on a platform QSSTV supports) —
  Scanline Studio was never going to cover this ground and does not now. Rejected by direct user
  request (2026-08-29), a `ui_transition_plan.md` Tier 3 scope decision recorded here per that
  document's own instruction (record explicit reject/defer if rejected), even though nothing is
  technically being "removed" from a port that never included it.

## Interactive bitmap-mask paint/draw editor (TBitMaskDlg)

- **Legacy**: `BitMask.h`'s `class TBitMaskDlg : public TForm` (line 39) and its implementation in
  `BitMask.cpp` — an in-app modal dialog for hand-building the bitmap used to mask a text/box fill,
  with a real freehand paint canvas (`PBoxPaint` line 68, `PBoxMouseDown`/`PBoxMouseMove`/`PBoxMouseUp`
  lines 70/73/75, `CBSizeChange` line 85 for brush size, `PaintBtnClick` line 77), load-from-file
  (`LoadBtnClick` line 69), clipboard copy/paste (`SBCopyClick`/`SBPasteClick` lines 89/88), a color
  adjust action (`SBColAdjClick` line 90), and drag-drop compositing between the mask's own upper/lower
  halves (`PBoxLDragDrop`/`PBoxUDragDrop` lines 96/102). Scoped deliberately narrow: `MakeBitmapPtn`
  (`BitMask.h` line 37, `BitMask.cpp`), the SEPARATE procedural 2-color dithered-pattern generator
  `TBitMaskDlg` also used, is NOT covered by this entry — it already shipped, under a different name,
  as `TextGradientKind.BitmapPattern` (Auditor usability review follow-up, 2026-08-18).
- **Replacement**: TX editor gap-items plan item 4b (picture fill, 2026-09-01) — text elements can now
  be filled with a real picture, picked from a file or the clipboard (`Panes.TxImageEditor.PictureFillFromFile`/
  `PictureFillFromClipboard`), reusing the SAME `IFilePickerService` picker commands the "+ IMAGE"
  toolbar button already uses for adding a whole image element.
- **Not fully replaced**: the freehand paint canvas (`PBoxPaint`/the 3 mouse handlers/`CBSizeChange`)
  and the upper/lower-half drag-drop compositing (`PBoxLDragDrop`/`PBoxUDragDrop`) have no replacement
  — real image editors already exist and cover this ground far better than an in-app mini paint tool
  would (CLAUDE.md §2: "improved on, not replicated," not a literal legacy re-implementation).
- **Impact**: an operator who used to hand-paint a mask bitmap directly inside YONIQ, or drag-compose
  it from the mask's own upper/lower halves, must now draw or compose that picture in external
  software and import the finished file (or paste it from the clipboard) instead — the load and paste
  paths are covered, the interactive paint/compose paths are not.
