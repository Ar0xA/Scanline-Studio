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

## Third-party libraries bundled in Yoniq v2 (not legacy YONIQ/MMSSTV assets)

Distinct from the legacy-asset audit above: these are new third-party dependencies introduced by
the rewrite itself (see [[spec/05-audio-engine]]), not anything ported from `yoniq-old/`. Still
covered by the same license-audit rule in `CLAUDE.md` — recorded here for the same reason.

| Asset | Source | License | Notes |
|---|---|---|---|
| `miniaudio.h` v0.11.25 | [github.com/mackron/miniaudio](https://github.com/mackron/miniaudio), pinned at tag `0.11.25`, vendored at `src/Yoniq.Core.Audio.MiniAudio/native/miniaudio.h` | Dual-licensed by the author (David Reid): Unlicense (public domain) **or** MIT-0 (MIT No Attribution), author's choice granted to users. Yoniq v2 elects **MIT-0** — a clearer, better-recognized statement of the same permissive terms than an unlicense/public-domain dedication, which doesn't hold the same legal weight in every jurisdiction. | Used as the native audio backend (device enumeration, capture, playback) behind a hand-written C shim (`native/yoniq_audio.c`/`.h`) — see [[spec/05-audio-engine]]'s Backend choice section for why PortAudio was rejected in favor of this. The single-header amalgamation also contains `dr_wav`/`dr_flac`/`dr_mp3` (same author, same dual license) — disclosed here even though this project compiles them out via `MA_NO_DECODING`/`MA_NO_ENCODING` (see the shim's own `#define`s), since their source text is still physically present in the vendored file. |

## Test fixtures captured by running the legacy binary

Distinct from both audits above: these are neither excluded assets nor bundled third-party code —
they're golden-vector test fixtures (per [[spec/13-testing]]) produced by running a real, locally-installed
legacy YONIQ/MMSSTV binary on originally-authored input, not copied from `yoniq-old/`'s source tree.
Recorded here per the same `CLAUDE.md` rule, which names "bitmap" and "dataset" explicitly.

| Asset | Captured how | License |
|---|---|---|
| `tests/Yoniq.Core.Sstv.Tests/Fixtures/GoldenVectors/{robot36,martin-m1}.mmv` | Real-time modulator/soundcard tap via legacy's own `File → Rec` (`Sound.cpp`'s `CWaveFile::Rec`), transmitting an originally-authored synthetic gradient image (`{robot36,martin-m1}.bmp`, not a legacy asset) through a locally-built/run legacy install. Originally also contained incidental non-legacy content outside the TX window (confirmed by reading `Sound.cpp:325-395` directly that legacy's sound-card input is only closed while transmitting) — trimmed down to the TX region plus a 1.0s safety margin per-file, at the user's request, to remove that content; see the fixture directory's own `README.md` for exact trim provenance. | LGPL-3.0-or-later (output of running the LGPL-licensed legacy binary), included here solely as a golden-vector comparison fixture in this repo's own test suite — never redistributed as a product asset. |
| `tests/Yoniq.Core.Sstv.Tests/Fixtures/GoldenVectors/{robot36,martin-m1}_RX.bmp` | Legacy's own decode of the corresponding `.mmv` above, played back via `File → Play` and saved via legacy's auto-History feature. | Same as above. |

## Process going forward

Per the license-audit rule in `CLAUDE.md`: no legacy asset (source file, data table, bitmap, `.mtm` template, prefix table) may be ported or bundled into Yoniq v2 without a new row in this document recording what it is, where it came from, and under what license it's being included.
