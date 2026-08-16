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
- **Replacement**: partial, updated 2026-08-09 (batch 7). [[spec/07-image-pipeline]]'s
  `RxHistoryPane` (a dockable, always-visible thumbnail grid, not a modal dialog or a page you must
  switch to) covers click-to-view browsing — click-to-select-any-thumbnail is a strict superset of
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
- **Not carried forward this pass — the real gap**: there is still no history→TX/template compositing
  path at all in the new design — selecting a `RxHistoryPane` entry only loads a read-only preview
  ([[spec/07-image-pipeline]]), it cannot be dragged into a template or the TX slot the way legacy's
  drag-in could. Also still dropped: both clipboard buttons (copying a history image out, pasting into
  TX) — investigated 2026-08-09 and deliberately deferred, not just skipped: Avalonia's cross-platform
  `IClipboard` has no first-class bitmap/image API (only `SetDataObjectAsync`/`GetDataAsync` against
  arbitrary format strings), so a genuinely cross-platform, actual-image-data clipboard copy (matching
  legacy's real `CopyBitmap`/`PasteBitmap` behavior, not a lower-fidelity file-reference copy) needs
  its own research pass into what format string(s) Windows/Linux/macOS clipboard consumers actually
  honor — not a same-batch wiring job. Clipboard paste as *file-picker-adjacent* TX input is separately
  named as in-scope in [[spec/07-image-pipeline]]'s TX flow step 1 ("file, clipboard paste, or
  webcam/screen-capture frame") but has no clipboard-specific UI affordance built yet — same
  underlying gap either way.
- **Impact**: users who relied on dragging a history thumbnail directly into a TX template to compose
  it, or on OS clipboard copy-from-history/paste-to-TX, need to use file-based load/save instead for
  now (save the history image to a file, then load it via the TX picker). Step-through nav and
  jump-to-latest are both now covered (see Replacement above). Revisit clipboard/drag-in if this turns
  out to matter in practice — logged here rather than silently dropped per CLAUDE.md's removal rule.

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

## FSK callsign-ID packet (RX)

- **Legacy**: `CSSTVDEM::DecodeFSK`'s modes 5–10 (`sstv.cpp:2465-2551`) — a distinct FSK-coded packet (STX `0x2a`, distinguishable from the mode-announce packet's `0x2d`) carrying a station callsign and optional numeric ID, decoded and surfaced via `m_fskcall`/`m_fskNRS` (referenced at `Main.cpp:3618`). TX side: `CSSTVMOD::OutputFSKID` (`Main.cpp:6904-6965`).
- **Replacement**: none. `ScanlineStudio.Core.Sstv.NarrowFskHeaderDecoder` (Piece 13, [[spec/14-roadmap]]) ports only modes 0–4/16/17/18 — the MN/MC narrow-mode-announce packet sharing the same guard-tone/start-bit/bit-sampling mechanism and mode-4 STX dispatch. A `0x2a` STX byte is treated identically to any other unrecognized value (reset, resume scanning) rather than routed to a callsign decode.
- **Impact**: no RX support for legacy's FSK callsign-ID feature. Structurally independent from the mode-announce packet (own leader/guard tone, own TX call sites) — not a partial replacement of a feature this port needs elsewhere, a standalone capability gap.

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

## Picture-demodulator selector (`m_Type`: PLL / zero-crossing / Hilbert)

- **Legacy**: `CSSTVDEM::m_Type`, a user-facing 3-way dispatch selecting the RX picture demodulator (`sstv.cpp:2256-2268`/`2310-2318`: `case 0` = PLL/`CPLL`, `case 1` = zero-crossing/`CFQC`, `default` = Hilbert/`CHILL`), exposed via `Option.cpp`'s `RGDemType` control and persisted to the `.ini` as `DemType` (`Main.cpp:1937`).
- **Replacement**: none — only the Hilbert (`default`) branch exists in this port (`HilbertFmDemodulator`, spec/14-roadmap.md's Piece 14). No settings/UI layer exists yet ([[spec/09-ui]], Phase 3) to expose an equivalent toggle, so `CPLL`/`CFQC` are structurally unreachable as the PICTURE demodulator today (`PllFmDemodulator`/`ZeroCrossingFrequencyCounter` do exist in this port, but only for other roles — AVT's dedicated training-lock PLL, and various sync/AFC tone detectors — never wired as the main picture demodulator).
- **Impact**: default-path parity holds — Hilbert is legacy's real compiled-in/shipped default, so every existing golden-vector/round-trip test exercises the same demodulator legacy users get out of the box. Users who manually switched legacy's `DemType` away from Hilbert (e.g. to try PLL on a hard/marginal signal) have no equivalent option in this port yet. Tracked as Band-3 item S13 in [[spec/14-roadmap]] — blocked on the Phase-3 settings UI landing before a real toggle can be wired.

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
