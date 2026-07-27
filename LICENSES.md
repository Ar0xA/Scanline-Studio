# License provenance

## Project license

Yoniq v2 is licensed under the **GNU Lesser General Public License v3.0 or later** (LGPL-3.0-or-later). See [COPYING](COPYING) (GPL-3.0 text, incorporated by reference) and [COPYING.LESSER](COPYING.LESSER) (the LGPL-3.0 additional permissions), fetched verbatim from gnu.org.

## Why LGPL-3.0-or-later

Yoniq v2 is a derivative work of YONIQ/MMSSTV: the rewrite specs in `spec/` explicitly plan to port algorithms, protocol framing logic, and DSP constants from the legacy C++Builder source (`sstv.cpp`, `Fft.cpp`, `fir.cpp`, `cradio.cpp`), not to reimplement them clean-room. Upstream YONIQ/MMSSTV states its own license as LGPL-3.0-or-later:

- `yoniq-old/YONIQ-main/README.md`: *"MMSSTV LGPL source repository"*.
- Every ported source file carries a per-file LGPL-3-or-later header (e.g. `cradio.h`, `sstv.h`): *"MMSSTV is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License ... version 3 ... or (at your option) any later version."*

Matching the upstream license is both the legally consistent choice for a derivative work and practically the right fit: LGPL permits commercial use and charging for distribution (unlike the freeware clause below), while not forcing GPL on downstream consumers of the plugin API ([[spec/11-plugin-system]]).

## `Terms.txt` / `License.TXT` (legacy freeware clause) — not carried forward

The legacy tree also contains `Terms.txt`/`License.TXT`, which state MMSSTV is non-commercial freeware and that *"under no circumstances can you make any charge for the MMSSTV program or Documentation."* That restriction is incompatible with LGPL (which explicitly permits charging for distribution) and is treated here as **stale, superseded historical text** predating the LGPL relicensing documented in the upstream README — not the controlling license for the code as currently distributed. This determination is not a legal certainty; if Yoniq v2 is ever distributed commercially or at scale, get this confirmed by counsel or directly with the upstream author (JE3HHT, Makoto Mori) before relying on it further.

## Assets explicitly excluded, not ported

| Asset | Legacy location | Reason excluded |
|---|---|---|
| ARRL.DX callsign/country dataset | `yoniq-old/YONIQ-main/ARRL.DX` | ARRL data has historically carried its own usage restrictions independent of MMSSTV's code license, and was never itself covered by the LGPL header. [[spec/08-logging]]'s `ICallsignLookup` prefix table is sourced from an unambiguously licensed alternative (e.g. Clublog's `cty.dat`) instead. |
| `MMCG.DEF` (JARL contest area database) | `yoniq-old/YONIQ-main/MMCG.DEF` | Tied to the contest/JASTA subsystem, which is out of scope for Yoniq v2 — see [docs/removed-features.md](docs/removed-features.md). Not ported, license not evaluated. |
| Chilkat (`CkRsa.h`, `chilkatDefs.h`) | `yoniq-old/YONIQ-main/` | Commercial third-party library. No call sites found anywhere in the legacy source tree — appears to be an orphaned/unused vendor header, not a real runtime dependency. Not ported. |
| FastReport VCL (`frxClass.hpp`, `frxCrypt.hpp`, package `frx22`) | `yoniq-old/YONIQ-main/Option.h` | Commercial third-party reporting library. Included by `Option.h` but no `Tfrx*` components appear in `Option.cpp`/`Option.dfm` — a build-time dependency with no observed runtime feature. Not ported. |

If either Chilkat or FastReport turns out to back a real feature once someone can build and run the legacy binary directly (not verifiable from source alone), re-evaluate before assuming they're safe to drop — see the corresponding open item in [[spec/14-roadmap]].

## Process going forward

Per the license-audit rule in `CLAUDE.md`: no legacy asset (source file, data table, bitmap, `.mtm` template, prefix table) may be ported or bundled into Yoniq v2 without a new row in this document recording what it is, where it came from, and under what license it's being included.
