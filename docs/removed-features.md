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
