# Removed features register

Per CLAUDE.md's removal rule: dropping a legacy capability requires an entry here naming the legacy files, the replacement (if any), and the user-visible impact. "Superseded" claims must state which users are actually covered — this document exists so that claim is checked, not assumed.

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
- **Fully replaced if the OmniRig-as-client backend ships**: unlike the above two, this restores the original arbitration case directly — Scanline Studio becomes just another OmniRig-aware client alongside the user's existing logger, no migration to `rigctld` needed. This backend is speculative/not yet designed (see [[spec/03-cat-layer]]'s Definition of done), so treat this line as the target, not a shipped guarantee.
- **Impact**: users with a single application controlling the rig are unaffected regardless of backend. Users sharing a rig across multiple OmniRig-aware applications should stay on OmniRig (with Scanline Studio as an OmniRig client, once built) or migrate everything to `rigctld`-mediated sharing.

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
- **Impact**: users of any Hamlib/rigctld/flrig-supported rig are unaffected — PTT keying works the
  same way regardless of whether legacy's own RTS-pin timing quirks would have mattered. No known
  real-world case is lost: `m_RTSonRX`'s "toggle RTS during an auto-scan" behavior and `m_TxRxLock`'s
  "avoid the port-reopen delay/glitch on every TX/RX transition" were both workarounds for the raw
  serial-port ownership model specifically, not independent user-facing features with their own value
  outside that model.

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

## TX output bandpass filter toggle/tap setting

- **Legacy**: `CSSTVMOD::Do`'s always-on filter (`sstv.cpp:2914`) is actually gated by a user
  checkbox, `m_bpf` (`CBTXBPF`, persisted as `TXBPF`, `Option.cpp:266-289,450`), and its tap count,
  `m_bpftap` (`TxBpfTap`/`TXBPFTAP`), is user-editable, rebuilt via `CalcFilter`
  (`sstv.cpp:2918-2928`) whenever changed. Both default on/24 (`sstv.cpp:2759,2764`).
- **Replacement**: none. Found during the functional-audit sweep (Tier A Batch 7, chunk 7d, round 1,
  2026-08-21). `TxOutputBandpassFilter.cs` always applies the filter at a fixed 24 taps, matching
  legacy's shipped defaults exactly.
- **Impact**: unaffected at shipped defaults (the common case — most users never touch this
  checkbox/setting). A legacy user who had disabled the TX output filter, or changed its tap count
  for a narrower/wider transmit passband, has no way to reproduce that in this port. No golden-vector
  or decode-correctness impact for the default case — this is a TX-side spectral-shaping option, not
  a core DSP-math difference — but it is a real, silently-dropped user-facing capability.

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
