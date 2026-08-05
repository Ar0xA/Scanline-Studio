# License provenance

## Project license

Scanline Studio is licensed under the **GNU Lesser General Public License v3.0 or later** (LGPL-3.0-or-later). See [COPYING](COPYING) (GPL-3.0 text, incorporated by reference) and [COPYING.LESSER](COPYING.LESSER) (the LGPL-3.0 additional permissions), fetched verbatim from gnu.org.

## Why LGPL-3.0-or-later

Scanline Studio is a derivative work of YONIQ/MMSSTV: the rewrite specs in `spec/` explicitly plan to port DSP/codec algorithms and constants from the legacy C++Builder source (`sstv.cpp`, `Fft.cpp`, `fir.cpp`), not to reimplement them clean-room. (CAT/rig-control code — `cradio.cpp` — is explicitly **not** ported; see [[spec/03-cat-layer]] and `CLAUDE.md` §2 — Scanline Studio is a pure client of external CAT backends instead. This doesn't change the LGPL rationale below, since the DSP core alone is still a substantial derivative work.) Upstream YONIQ/MMSSTV states its own license as LGPL-3.0-or-later:

- `yoniq-old/YONIQ-main/README.md`: *"MMSSTV LGPL source repository"*.
- Every ported source file carries a per-file LGPL-3-or-later header (e.g. `sstv.h`, `fir.h`): *"MMSSTV is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License ... version 3 ... or (at your option) any later version."*

Matching the upstream license is both the legally consistent choice for a derivative work and practically the right fit: LGPL permits commercial use and charging for distribution (unlike the freeware clause below), while not forcing GPL on downstream consumers of the plugin API ([[spec/11-plugin-system]]).

## `Terms.txt` / `License.TXT` (legacy freeware clause) — not carried forward

The legacy tree also contains `Terms.txt`/`License.TXT`, which state MMSSTV is non-commercial freeware and that *"under no circumstances can you make any charge for the MMSSTV program or Documentation."* That restriction is incompatible with LGPL (which explicitly permits charging for distribution) and is treated here as **stale, superseded historical text** predating the LGPL relicensing documented in the upstream README — not the controlling license for the code as currently distributed. This determination is not a legal certainty; if Scanline Studio is ever distributed commercially or at scale, get this confirmed by counsel or directly with the upstream author (JE3HHT, Makoto Mori) before relying on it further.

## Assets explicitly excluded, not ported

| Asset | Legacy location | Reason excluded |
|---|---|---|
| ARRL.DX callsign/country dataset | `yoniq-old/YONIQ-main/ARRL.DX` | ARRL data has historically carried its own usage restrictions independent of MMSSTV's code license, and was never itself covered by the LGPL header. [[spec/08-logging]]'s `ICallsignLookup` prefix table is sourced from an unambiguously licensed alternative (e.g. Clublog's `cty.dat`) instead. |
| `MMCG.DEF` (JARL contest area database) | `yoniq-old/YONIQ-main/MMCG.DEF` | Tied to the contest/JASTA subsystem, which is out of scope for Scanline Studio — see [docs/removed-features.md](docs/removed-features.md). Not ported, license not evaluated. |
| Chilkat (`CkRsa.h`, `chilkatDefs.h`) | `yoniq-old/YONIQ-main/` | Commercial third-party library. No call sites found anywhere in the legacy source tree — appears to be an orphaned/unused vendor header, not a real runtime dependency. Not ported. |
| FastReport VCL (`frxClass.hpp`, `frxCrypt.hpp`, package `frx22`) | `yoniq-old/YONIQ-main/Option.h` | Commercial third-party reporting library. Included by `Option.h` but no `Tfrx*` components appear in `Option.cpp`/`Option.dfm` — a build-time dependency with no observed runtime feature. Not ported. |

If either Chilkat or FastReport turns out to back a real feature once someone can build and run the legacy binary directly (not verifiable from source alone), re-evaluate before assuming they're safe to drop — see the corresponding open item in [[spec/14-roadmap]].

## Candidate future asset — pre-audit note (not yet bundled)

Clublog's `cty.dat`/`cty.xml` (the callsign-prefix dataset named as the planned replacement for `ARRL.DX` above, [[spec/08-logging]]'s `ICallsignLookup`) is **not yet bundled** — Phase 4 work, not started. Checked its terms early (2026-08-02) so this isn't discovered as a blocker mid-Phase-4:

- No fee, and Clublog states "no licensing" is required to use the prefix/exception data in third-party software.
- However, downloading it for redistribution requires an **individual API key obtained by emailing Clublog's helpdesk** with details of the proposed use — it is not a blanket, download-and-go open license like MIT/CC0. (Source: [Club Log support — "Downloading The Prefixes And Exceptions As XML"](https://clublog.freshdesk.com/support/solutions/articles/54902-downloading-the-prefixes-and-exceptions-as-xml).)
- Attribution/promotion is requested but not contractually mandated.

**Action item before Phase 4 bundles this data**: a human needs to email Clublog's helpdesk describing Scanline Studio's proposed use and obtain an API key before the table can be fetched and bundled — not something an agent can do from source alone. Once that's done, add the actual row to the "Third-party libraries bundled" table below (asset, exact version/snapshot date, license/permission terms as granted) per the process-going-forward rule.

## Third-party libraries bundled in Scanline Studio (not legacy YONIQ/MMSSTV assets)

Distinct from the legacy-asset audit above: these are new third-party dependencies introduced by
the rewrite itself (see [[spec/05-audio-engine]]), not anything ported from `yoniq-old/`. Still
covered by the same license-audit rule in `CLAUDE.md` — recorded here for the same reason.

| Asset | Source | License | Notes |
|---|---|---|---|
| `miniaudio.h` v0.11.25 | [github.com/mackron/miniaudio](https://github.com/mackron/miniaudio), pinned at tag `0.11.25`, vendored at `src/ScanlineStudio.Core.Audio.MiniAudio/native/miniaudio.h` | Dual-licensed by the author (David Reid): Unlicense (public domain) **or** MIT-0 (MIT No Attribution), author's choice granted to users. Scanline Studio elects **MIT-0** — a clearer, better-recognized statement of the same permissive terms than an unlicense/public-domain dedication, which doesn't hold the same legal weight in every jurisdiction. | Used as the native audio backend (device enumeration, capture, playback) behind a hand-written C shim (`native/yoniq_audio.c`/`.h`) — see [[spec/05-audio-engine]]'s Backend choice section for why PortAudio was rejected in favor of this. The single-header amalgamation also contains `dr_wav`/`dr_flac`/`dr_mp3` (same author, same dual license) — disclosed here even though this project compiles them out via `MA_NO_DECODING`/`MA_NO_ENCODING` (see the shim's own `#define`s), since their source text is still physically present in the vendored file. |
| `SixLabors.ImageSharp` v3.1.12 | Plain NuGet package reference (not vendored source), `src/ScanlineStudio.Core.Imaging` — [nuget.org/packages/SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp) | **Six Labors Split License v1.0** (not MIT/Apache — a conditional dual license, documented here specifically because its shape could otherwise be misread by a license scanner as a blocker). Terms grant Apache-2.0-equivalent free use to consumers of software licensed under an Open Source/Source-Available license — Scanline Studio (LGPL-3.0-or-later) qualifies outright, no revenue threshold applies (that threshold only gates closed-source *direct* commercial consumers). Verified against the license text bundled in the NuGet package itself, not secondhand. | **Pinned to the 3.1.x line deliberately, not latest (4.0.0+)**: v4.0.0 added build-time license-key enforcement (`$(SixLaborsLicenseKey)`/`sixlabors.lic`) that fails the build outright with no automatic detection of the open-source exemption — confirmed by actually attempting the build, not assumed. 3.1.12 carries the identical SLSL v1.0 terms (same free grant for this project) without that build-time gate. Used for `IImageFileLoader`/`ReceivedImageBuffer.SaveAsync` (TX image loading, RX image save) per `spec/07-image-pipeline.md`'s existing choice of ImageSharp over `System.Drawing`/GDI+. |
| `SixLabors.ImageSharp.Drawing` v2.1.7 + `SixLabors.Fonts` v2.1.3 (transitive) | Plain NuGet package references (not vendored source), `src/ScanlineStudio.Core.Imaging` — [nuget.org/packages/SixLabors.ImageSharp.Drawing](https://www.nuget.org/packages/SixLabors.ImageSharp.Drawing) | Same **Six Labors Split License v1.0** as the `SixLabors.ImageSharp` row above — verified directly against the license file bundled in this package too, not assumed identical. Same free-grant analysis applies (Scanline Studio's LGPL-3.0-or-later qualifies). | `ITransmitImagePreparer.ApplyOverlay`'s text-rendering companion to the base ImageSharp package (spec/07-image-pipeline.md's "TX image editor" section). 2.1.7's own floor dependency is `SixLabors.ImageSharp >= 3.1.11`, at or below this project's pinned 3.1.12, so it resolves to the existing pin rather than bumping it. |
| DejaVu Sans Mono (TrueType font) | [dejavu-fonts.github.io](https://dejavu-fonts.github.io/), vendored at `assets/fonts/DejaVuSansMono.ttf`, copied from this dev machine's installed `fonts-dejavu-core` Debian package (`/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf`) | **Bitstream Vera License** (a permissive font license predating and distinct from the Bitstream Vera *trademark*) — copyright Bitstream, Inc. 2003, DejaVu's own changes released to the public domain (verified directly against `/usr/share/doc/fonts-dejavu-core/copyright` on this machine, not secondhand). Explicitly permits reproduction, distribution, bundling as part of a larger software package, and commercial sale of that larger package — the one restriction (no selling the font *by itself*, and no reusing the "Bitstream"/"Vera" names if modified) doesn't apply to unmodified bundling here. Compatible with LGPL-3.0-or-later: font and code licenses are independent, no copyleft is imposed on the software consuming the font. | Loaded explicitly into a `SixLabors.Fonts.FontCollection` at startup for `ITransmitImagePreparer.ApplyOverlay`'s text rendering (spec/07-image-pipeline.md's "TX image editor" section) — deliberately NOT relying on `SixLabors.Fonts.SystemFonts` enumeration, which can legitimately be empty on a minimal Linux install with no system fonts registered; a feature that burns text into transmitted pixel data needs a guaranteed-present font, not best-effort system font discovery. |

## Runtime dependencies consumed but not bundled

Distinct from the "bundled" table above: these are external libraries Scanline Studio talks to at runtime
(P/Invoke, subprocess, network) without ever including their source or a compiled binary in this repo
or any Scanline Studio installer. Recorded here for the same provenance reason, even though no license text or
code is actually being redistributed.

| Dependency | Relationship | License | Notes |
|---|---|---|---|
| Hamlib (`libhamlib`) | `ScanlineStudio.Core.Radio.Hamlib` P/Invokes against a **system-installed** copy the user's own OS/package manager provides — see [[spec/03-cat-layer]]'s "Linked Hamlib: bring-your-own-libhamlib." Scanline Studio never builds, forks, vendors, or ships Hamlib source/binaries. | LGPL-2.1-or-later | No header-derived data tables (e.g. `riglist.h` rig-model numbers) are copied in either — Scanline Studio has no `IRigRegistry` ([[spec/02-radio-layer]]). |

## Test fixtures captured by running the legacy binary

Distinct from both audits above: these are neither excluded assets nor bundled third-party code —
they're golden-vector test fixtures (per [[spec/13-testing]]) produced by running a real, locally-installed
legacy YONIQ/MMSSTV binary on originally-authored input, not copied from `yoniq-old/`'s source tree.
Recorded here per the same `CLAUDE.md` rule, which names "bitmap" and "dataset" explicitly.

| Asset | Captured how | License |
|---|---|---|
| `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/{robot36,martin-m1}.mmv` | Real-time modulator/soundcard tap via legacy's own `File → Rec` (`Sound.cpp`'s `CWaveFile::Rec`), transmitting an originally-authored synthetic gradient image (`{robot36,martin-m1}.bmp`, not a legacy asset) through a locally-built/run legacy install. Originally also contained incidental non-legacy content outside the TX window (confirmed by reading `Sound.cpp:325-395` directly that legacy's sound-card input is only closed while transmitting) — trimmed down to the TX region plus a 1.0s safety margin per-file, at the user's request, to remove that content; see the fixture directory's own `README.md` for exact trim provenance. | LGPL-3.0-or-later (output of running the LGPL-licensed legacy binary), included here solely as a golden-vector comparison fixture in this repo's own test suite — never redistributed as a product asset. |
| `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/{robot36,martin-m1}_RX.bmp` | Legacy's own decode of the corresponding `.mmv` above, played back via `File → Play` and saved via legacy's auto-History feature. | Same as above. |

## Process going forward

Per the license-audit rule in `CLAUDE.md`: no legacy asset (source file, data table, bitmap, `.mtm` template, prefix table) may be ported or bundled into Scanline Studio without a new row in this document recording what it is, where it came from, and under what license it's being included.
