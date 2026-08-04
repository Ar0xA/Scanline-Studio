# Removed features register

Per CLAUDE.md's removal rule: dropping a legacy capability requires an entry here naming the legacy files, the replacement (if any), and the user-visible impact. "Superseded" claims must state which users are actually covered — this document exists so that claim is checked, not assumed.

## OmniRig ActiveX/COM integration

- **Legacy**: `OmniRig_OCX.cpp`, `OmniRig_OCX.h`, `OmniRig_TLB.cpp`, `OmniRig_TLB.h`. User-facing toggle in `Config.cfg` (`omnirig=0`).
- **Replacement**: native CAT protocols ([[spec/03-cat-layer]]) plus [[spec/04-rigctld]].
- **Not fully replaced**: OmniRig's core value was **rig-sharing arbitration** — letting multiple applications (e.g. YONIQ and a separate logger) share one serial-connected rig through a single OmniRig broker process. Native CAT cannot replicate this at all (two processes cannot open the same serial port). rigctld replicates it only if every application on the machine is reconfigured to talk through the same `rigctld` instance instead of opening the port directly — a real migration step for affected users, not a transparent swap. Users who relied on OmniRig specifically to share a rig between YONIQ and another OmniRig-aware application should be told, explicitly, to install and point everything at `rigctld` before upgrading.
- **Impact**: users with a single application controlling the rig are unaffected. Users sharing a rig across multiple OmniRig-aware applications need to migrate to rigctld-mediated sharing.

## CItems custom-item plugin ABI

- **Legacy**: `CItems/` (PERIMG, QSLBox, TextArt, TEXTBOX subfolders), documented in `CItems/ECUSTOM.TXT` — a native Win32 DLL ABI that MMSSTV loads on-the-fly to extend the QSL/template designer.
- **Replacement**: none directly. [[spec/11-plugin-system]] provides a managed, cross-platform plugin model (`AssemblyLoadContext`-isolated, `IYoniqPlugin`), but it is not binary-compatible with legacy CItems DLLs — those are native Win32 code built against a C++Builder-specific struct layout, incompatible with a cross-platform managed host by construction, not by choice.
- **Not carried forward**: any third-party custom-item DLLs built against the legacy `ECUSTOM.TXT` ABI (unknown how many exist in the wild) stop working with no automatic migration path.
- **Successor**: the concept — a loadable extension that draws into the QSL/template designer — is the intended scope of a future `ITemplateItem`-style extension point once [[spec/15-template-designer]] is implemented (currently deferred, see [[spec/14-roadmap]]). Until then this is a real capability gap, not a completed replacement.
- **Impact**: any user of a third-party MMSSTV custom-item DLL loses that specific extension until the template designer and its plugin point ship.

## MMlink inter-application broadcast

- **Legacy**: `MMlink.cpp`/`mml.h`/`mmrp.h` — a DLL-based link protocol (`mmlOpen`, `mmlSetFreq`, `mmlSetPTT`, `mmlLog`, `mmlEventVFO`) plus `SendMessage(HWND_BROADCAST, m_PSKGNRId, …)` broadcast messages used by `cradio.cpp` to notify other MM-family applications (MMTTY, MMVARI, etc.) of frequency/PTT changes.
- **Replacement**: none in v1. This is Windows-specific inter-process broadcast messaging with no cross-platform equivalent; [[spec/04-rigctld]]'s server mode covers the "another app wants to know the current frequency" use case for any Hamlib-aware client, but does not replicate MMlink's specific broadcast/PTT-coordination protocol.
- **Impact**: users running YONIQ alongside other MM-family (Mori-authored) applications relying on live MMlink coordination lose that integration. rigctld server mode is the recommended alternative where the other application can speak it.

## Loglink / Turbo HAMLOG live integration

- **Legacy**: `Loglink.cpp` — live IPC (`WM_COPYDATA`) with Turbo HAMLOG, a separate third-party logging application (`m_hLog`, `m_hLogIn`, `m_fHLV5`).
- **Replacement**: none live. [[spec/08-logging]]'s ADIF import/export is a **batch**, not live, integration path — the two logs stay independent, synced by explicit export/import rather than in real time.
- **Impact**: users who used Turbo HAMLOG as their primary log with live sync from MMSSTV/YONIQ need to switch to periodic ADIF export/import, or use Yoniq's own built-in logbook ([[spec/08-logging]]) instead of a separate application.

## Contest logging (JASTA) and JARL area codes

- **Legacy**: `yoniq-old/YONIQ-main/JASTA/` — a distinct bundled C++Builder application (`MMJASTA`) with its own logging/conversion/country-lookup code, plus `Mmcg.cpp`/`MmcgDlg.cpp`/`MMCG.DEF` (JARL contest area database) and `NVCG.txt` in the main tree.
- **Replacement**: none. Out of scope for Yoniq v2 — this is a separate application bundled alongside MMSSTV/YONIQ historically, not a feature of YONIQ itself, and is not ported.
- **Impact**: users relying on JASTA for contest logging need to continue using the legacy application, or a different contest logger, alongside Yoniq v2.

## Chilkat and FastReport VCL

- **Legacy**: `CkRsa.h`/`chilkatDefs.h` (Chilkat, commercial) and `frxClass.hpp`/`frxCrypt.hpp` (FastReport VCL, commercial, package `frx22`), referenced from `Option.h`.
- **Investigation result**: no call sites for Chilkat types found anywhere in the source tree; FastReport is included but no `Tfrx*` components appear in `Option.cpp`/`Option.dfm`. Both appear to be unused, orphaned, or build-time-only dependencies with no observed runtime feature — see [LICENSES.md](../LICENSES.md) for the license reasoning.
- **Impact**: assumed none, pending confirmation against a built legacy binary (tracked as an open item in [[spec/14-roadmap]]). If either turns out to back a real feature, this entry must be corrected and the feature re-scoped.

## FSK callsign-ID packet (RX)

- **Legacy**: `CSSTVDEM::DecodeFSK`'s modes 5–10 (`sstv.cpp:2465-2551`) — a distinct FSK-coded packet (STX `0x2a`, distinguishable from the mode-announce packet's `0x2d`) carrying a station callsign and optional numeric ID, decoded and surfaced via `m_fskcall`/`m_fskNRS` (referenced at `Main.cpp:3618`). TX side: `CSSTVMOD::OutputFSKID` (`Main.cpp:6904-6965`).
- **Replacement**: none. `Yoniq.Core.Sstv.NarrowFskHeaderDecoder` (Piece 13, [[spec/14-roadmap]]) ports only modes 0–4/16/17/18 — the MN/MC narrow-mode-announce packet sharing the same guard-tone/start-bit/bit-sampling mechanism and mode-4 STX dispatch. A `0x2a` STX byte is treated identically to any other unrecognized value (reset, resume scanning) rather than routed to a callsign decode.
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
