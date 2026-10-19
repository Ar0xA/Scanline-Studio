# BACKLOG — the one list of work that still needs doing

## 1. Decode path

### RM12-TEAL. OPEN — `rm12` fails to decode a flat teal picture

Our encoder → our decoder, clean, 11025 Hz, flat colour (40,160,150): 73680 pixels off by more than 60,
the whole picture. Flat grey decodes fine. Found incidentally 2026-09-22, not investigated. First step:
check whether a non-flat picture also fails. Review tier: full if the fix touches the decode path.

### AVT-PARITY. OPEN — AVT decode diverges from legacy's own decode

Same `avt.mmv`, our decode vs legacy's `avt_RX.bmp` (measured 2026-10-11 at `29016f37`): ours−src 9.12,
legacy−src 4.23 (gate: excess 4.89 > 0.63), ours−legacy 6.92. R-ramp offset −13.55 px (−10.83 lv), median R
+10 lv, slope ratio 1.0008. The vertical G fit is 0, so this is either a ~12.5 px horizontal shift or an
R-only level/gain error; undetermined. ours−src drifted 6.74 → 9.12 under its 14.0 limit with no record.
Pinned as a regression guard in `GoldenVectorTests.Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource`;
table in `Fixtures/GoldenVectors/README.md` ("Port decode vs legacy decode"). Review tier: full.

### RM8-SLOPE. OPEN — rm8 decode ramp slope 2.75% off legacy's own decode

Same `rm8.mmv`, our decode vs legacy's `rm8_RX.bmp` (2026-10-11, `29016f37`): offsets 1.50 px / 1.70 rows
under a mean-|·| fit (gate tripped), 0.60 / 0.43 under the least-squares fit now used (passes); median R
−1 lv, slope ratio 1.0275. rm8's 1-px gate (0.46 lv) is below one byte level, so the verdict is
fit-sensitive. ours−src 5.06 < legacy−src 5.45, so this does not claim our decode is the wrong one.
Unverified suspect: legacy's gain `d *= (256.0 / (256.0 - 32.0))` (`Main.cpp:4438`). Pinned normally with
a WATCH comment; same test and README table as AVT-PARITY.

---

## 2. Test integrity

### PA-1 / TT1-2. OPEN — golden-vector worst-row metric

`MeasureAveragePerChannelDelta` (`GoldenVectorTests.cs:966`) sums over the whole frame, and tolerances
run 1.99 to 13.76. One fully corrupted row out of 256 moves the average by at most 1.0, so it passes
today. Add a per-row max beside the frame average, measure the current worst row per fixture, then pin
at about 1.5x to 2x. **Needs its own measurement pass first** — do not bundle it into another change.

### PA-2 / TT1-5. BLOCKED on the legacy binary — TX golden fixtures

The mode→function channel-order mapping is done (`LegacyTxChannelOrderTests.cs`). It compares the port
against legacy *source*, not legacy's emitted *audio*, so a shared misreading would pass. Only real
captures close that.
- **PA-2-b.** Revalidate the 11 stale TX fixtures. All 11 `Fixtures/GoldenVectors/TxCapture/*.provenance`
  read `UNKNOWN-STALE` (avt, martin-m1, mn110, mr73, pd90, r24, rm8, robot-36, robot-72, scottie-dx,
  scottie-s1), and `StaleFixtureTheoryAttribute` skips the comparison.
- **PA-2-c.** Capture new TX fixtures for the 4 never-covered functions: `LineSC2180`, `LineP`, `LineMP`,
  `LineMC`. One fixture per function is enough for channel ORDER.

### PA-5. OPEN — assert every `{loc:Translate X}` key resolves

Extend `tests/ScanlineStudio.UI.Tests/NoHardcodedAxamlStringsTests.cs`. It holds one test (`:22-23`),
which checks only the reverse direction (no hardcoded literals). A typo'd key ships as a broken label
with nothing to catch it.

### PA-Two-more. OPEN — `MainWindow.axaml.cs` coverage

- **a.** Scoped tests: T1-12's `DataContextChanged` re-entry guard (`MainWindow.axaml.cs:33`) plus the two
  or three highest-traffic cross-pane wirings. A coverage task, not a fix. Do not chase all ~1520 lines
  (`Views/MainWindow.axaml.cs`). No test constructs `MainWindow` today.
- **b.** Fold the 42 `logger is not null` guards in that file into a null-object logger.

### W. Windows-only test coverage

- **W2-a. OPEN, needs a Windows run** — WASAPI loopback capture is plumbed (`scanline_audio.c:707-710`,
  `NativeAudio.cs:72`, `MiniAudioCaptureSession.cs:131`) with 2 tests (`WasapiLoopbackCaptureTests.cs:29,50`).
  Written on Linux, never run on Windows. Native interop on the audio path: full review cadence.
- **W2-b. OPEN** — split `[RequiresPipeWireFact]` (`RequiresPipeWireFactAttribute.cs:32`, 40 tests) into a
  capability gate (e.g. `RequiresRealAudioFact`) that probes `pactl` on Linux and WASAPI loopback on
  Windows. This unblocks W2-c, W2-d and W-RT.
- **W2-c. OPEN, after W2-b** — port the ~27 device-only tests (lifecycle, concurrency, disposal).
- **W2-d. OPEN, after W2-b** — port the ~16 cable/loopback tests onto WASAPI loopback.
- **W2-e. DECISION ×3** — the odd cases: `RefreshAsync_FindsRealVirtualCable_...` needs one device that is
  both sink and source (loopback cannot; argues for VB-CABLE); both `HotplugDisposeTests` need a device
  that disappears mid-session (no Windows one-liner); the mute-control test needs mute control.
- **W-RT. OPEN, after W2-b** — move `MiniAudioEngineSstvRoundTripTests.EncodeThenDecode_ThroughRealMiniAudioEngine_RoundTripsWithinTolerance`
  (`:35`) onto WASAPI loopback so the audio round trip runs on Windows. `windows_tests.md`.
- **W8-b. DECISION per site** — whether to write Windows equivalents for the four Unix-permission tests
  that skip on Windows: `AppLocationOverridesTests.cs:39`, `AppLocationsServiceTests.cs:142` and `:229`,
  `Host.Tests/ApplyPendingRelocationsTests.cs:239`.
- **W8-c. OPEN** — a fifth silent-pass site: `JsonSettingsStoreTests.cs:200-208`
  (`LoadAsync_UnreadableFile_ReturnsDefaultsRatherThanThrowing`) is a plain `[Fact]` with a bare
  `if (OperatingSystem.IsWindows()) return;`. Convert to `[SkipOnWindowsFact]` +
  `[UnsupportedOSPlatform("windows")]`, like TT1-18. The same pattern is also at `:240` and `:288`.
- **W9-a. OPEN, needs a Windows run** — Hamlib's Windows DLL discovery (`HamlibLibraryLocator.cs:20`,
  `:87-89`, `:116-118`) has tests (`HamlibWindowsDiscoveryTests.cs`, 3 `[WindowsFact]`) never run on
  Windows. This decides whether CAT works at all on Windows.
- **W9-b. OPEN** — the OmniRig row's visibility/`IsEnabled` binding (`OptionsWindowView.axaml:380`) and
  tooltip key (`OptionsWindowViewModel.cs:1281`) are unasserted. The availability flag itself is covered
  (`OptionsWindowViewModelTests.cs:2818-2820`).
- **W9-c. PARTLY** — `DirectoryPathComparer` normalization (`DirectoryPathComparer.cs:17`) on Windows.
  Case-insensitivity is tested (`WindowsDirectoryPathComparerTests.cs`, `1c3916de`). Still untested:
  drive-relative (`C:foo`), UNC, `\\?\`-prefixed and trailing-dot/space paths. These feed
  `AppLocationsService` and `SqliteReceiveHistoryStore`.
- **W9-d. OPEN, low priority** — assert that the Windows-built shim exports everything `NativeAudio`
  P/Invokes. A missing export surfaces as `EntryPointNotFoundException` at runtime.
- **W9-e. OPEN** — `scanline_audio_get_device_mute` (`scanline_audio.c:1497`) does its own UTF-8-to-wide
  `MultiByteToWideChar` at `:1506`. Unasserted; `WasapiMuteQueryTests` has no multi-byte case.
- **W-PTT. OPEN, needs a Windows run** — re-run `SstvSessionServiceTests.TuneAsync_TokenCancelledMidTone_*`
  (or the full suite) on Windows to confirm `c9d9b1b3`'s deterministic cancellation gate resolves
  `windows_tests.md` Item C.
- **W-CRED. OPEN, needs a Windows run** — `WindowsCredentialStore` (QRZ password, `CredWriteW`/`CredReadW`/
  `CredDeleteW` via `LibraryImport`) has 5 `[WindowsFact]`s in `ScanlineStudio.Credentials.Tests` that were
  written on Linux and never run on Windows. Also check the Options QRZ tab shows the keyring hint there.
- **W-HOTPLUG. OPEN, needs Windows** — WASAPI hot-unplug notification and a possible `Dispose` hang after
  the device vanished are confirmed only on PulseAudio (`HotplugDisposeTests.cs:25`, `:94`).
  `spec/05-audio-engine.md`, "Device hot-plug". macOS is out of scope.

### COV. DECISION — CI coverage gate below the stated 80% floor

`coverage-thresholds.json` pins each `ScanlineStudio.Core.*`/`ScanlineStudio.Application` project a few
points above its own baseline (36%-97%, `Core.Sstv` excluded). That catches regressions but does not
enforce `spec/13-testing.md`'s stated 80%. Decide: raise the floor, or change the stated target.

### FIX-DIRS. Missing fixture directories

- **a. OPEN** — `tests/ScanlineStudio.Core.Radio.Tests/Fixtures/` (CAT fixtures) does not exist.
- **b. OPEN** — no real-world third-party ADIF fixture. Add some under
  `tests/ScanlineStudio.Core.Logbook.Tests/Fixtures/` (does not exist) and round-trip
  `AdifImporter`/`AdifExporter` against them; today's tests use only synthetic `QsoRecord`s.
  `spec/08-logging.md`, `spec/13-testing.md`.

---

## 3. Source items

### PA-Factory. OPEN — extract one factory-match resolver

"Exactly one factory match" is written out three times: `RadioController.cs:407-420` (`ResolveProtocol`)
and `RadioSessionService.cs:75-81` and `:280-286`, with two different error strings for one condition.
About 15 lines. Do it when already in those files.

### GALLERY-VIRT. DECISION, then OPEN — virtualize the Gallery grid

Measured 2026-09-22 (UX-THUMB1 probe, `GalleryDisplayCapMeasurementProbe.cs`, gated
`SCANLINE_RUN_GALLERY_PROBE=1`): laying out the displayed set costs ~140-360 ms at 300 tiles and ~750 ms at
1 500, one container per tile. Avalonia 11.3 had no virtualizing wrap panel (re-check on 12.1.3, now in use); rows-of-6 in a
`VirtualizingStackPanel` breaks item-level `ListBox` behaviour (TwoWay `SelectedItem`, selected styling,
keyboard navigation, right-click select). Candidate design (needs a UI plan-review first): an outer list of
rows with a virtualizing panel, each row its own 6-column `ListBox` with `SelectedItem` bound OneWay to
`SelectedEntry` and written back from `SelectionChanged`; arrow keys move within a row only; "Show older"
moves outside the list. Also remove the outer `ScrollViewer` (`MainWindow.axaml` ~:1291) first.

### POLL. OPEN — `PollingStrategy.OnDemand` is a no-op

`RadioConnectionSpec` declares it, but `RadioController`'s poll loop never reads `Strategy`
(`RadioController.cs:181` only rejects `Scan`), so `OnDemand` behaves like `Continuous`.
`spec/02-radio-layer.md`, "Polling".

### RIG-BW. OPEN, low priority — non-atomic bandwidth read-modify-write

`RigctldClientProtocol.SetBandwidthAsync` reads then writes mode+bandwidth. Our own client already holds one
lock across the `m` read and `M` write (`e4730fdf`); what stays open is atomicity against other rigctld clients.
Real but low-probability (Hamlib caches the just-set mode for 500 ms). A fix would touch 4 backends.
Found 2026-09-19 during the "SSB as PKT" investigation.

### SETTINGS-MIG. OPEN — `ISettingsMigration` chain has never run

No `ISettingsMigration` exists; `AppSettings.cs:22` is `CurrentSchemaVersion = 1` and `ISettingsStore.cs:3`
says migration is not implemented. Build the chain and exercise it against one real schema bump before
v1.0. `spec/12-settings.md`.

### QRZ-APIKEY. OPEN — QRZ Logbook upload API key still in plaintext

`QrzUploadSettings.ApiKey` stays in `settings.json`. It has no Options control, so moving it into the OS
keyring (as QRZ-PW did for the lookup password via `QrzCredentialService`) would leave it unmanageable.
Needs an Options control first, then the same store. `spec/12-settings.md` "Secrets" DoD stays unticked
for it.

### M2. DECISION — Hamlib header anchoring

Vendor a pinned `rig.h` into the test project with a `LICENSES.md` entry (Hamlib is LGPL-2.1, `CLAUDE.md`
§5), or accept a conditional skip. `hamlib/` is gitignored (`.gitignore:11`), so a test reading it
directly would silently not run on CI. `RIG_LEVEL_*` constants are verified only by comment today
(`HamlibRadioProtocolTests.cs:14`). A wrong bit-flag silently mis-reads SWR, and the SWR auto-cutoff is a
safety feature.

### M3. OPEN — T1-14, cold imaging convert-in/convert-out

Unmeasured. The working-copy bound (`WorkingCopyScaleFactor`,
`TxImageEditorPaneViewModel.SourceTransformAndPreview.cs:741`) does not apply to the paths that crop and rotate
the full original for final output. Measure a representative large source image. The conversions now go
through the shared `ImageSharpPixelConversion` (`f3b087dd`). The hot preview path is already fused (`a7bc5be7`)
and has a `ComposePreview` probe (`538bbf12`); the cold path has none.

### PERF. OPEN — decoder real-time throughput is unmeasured

Benchmark and document that `ISstvDecoder` sustains real-time throughput on a defined reference machine,
ideally asserted in CI. No benchmark exists. `spec/06-sstv-dsp.md` Definition of done.

---

## 5. Product and roadmap

### FREQ-PRESETS. DONE `af9b345c` — SSTV/DRM calling-frequency menu beside the frequency counter
41 entries (pd2tx, vk6ysf, Wikipedia), 12 band submenus, mode/kind/region/ISS labels; a pick sets frequency, mode
(SSB-as-PKT aware) and width (2800 Hz on DRM rows) through CAT. Plan `~/.claude/plans/freq-presets.md` rev 3; plan
review 2 rounds, code review go (round 1); live check on the rigctld dummy rig passed.

### R1. OPEN — `TemplateCatProtocol` fallback

Zero occurrences in `src/` or git history. A planned deliverable and the named example in `CLAUDE.md` §4's
binary-is-bytes rule: byte fields are `byte[]`/`ReadOnlyMemory<byte>`, never string or JSON literal.
Spec: `spec/03-cat-layer.md`.

### R2. OPEN — localization completion

German (`de`) shipped 2026-09-20. Spec: `spec/10-localization.md`.
- **a.** A community-translation workflow. No CONTRIBUTING or translation doc; `spec/10-localization.md:69`
  only describes dropping in an `xx.json` + `locales.json`.
- **b.** Check C# view-models for hardcoded user-visible strings. AXAML is clean by heuristic (0 literal
  hits; `en.json`/`de.json` both 1064 keys).
- **c.** `ja.json` was never created. Populate it via the documented per-dialog `.dfm` caption/hint
  extraction + `sys.m_MsgEng`-branch source-mining workflow (CP932-decoded) from `yoniq-old`.
- **d.** Manually confirm a language switch updates every currently open window live. The root cause was
  fixed in `404e4337` and Options is covered by a real-window test
  (`OptionsWindowLiveLocaleSwitchTests.cs:55`); other windows are unverified.

### R3. Receive-tab signal telemetry — the parts with no legacy precedent

The three cards exist and every shown row is bound to real data. What is left is new design:
- **R3-b. OPEN** — Signal-quality Min/Max (the per-line SNR it would summarise shipped with R3-a, `SyncSnrTracker`).
- **R3-c. DECISION** — an Input-chain squelch. No legacy basis. Legacy's only squelch is repeater-scoped
  `m_RepSQ` (gated on `m_Repeater && !m_Sync`; uses at `sstv.cpp:1502`, `:1865`, `:2688`, `:2734`), so it
  is not a drop-in general RX squelch. Distinct from the Sync & Slant card's "Squelch level" (VIS sense
  level). Citations: `spec/17-rx-telemetry-feasibility.md`.

### SNR-HFNOISE. OPEN — validate reception SNR on real HF noise

The reception-SNR estimator (`SyncSnrEstimator`, shipped on 0.9.1-beta) was validated with full-band white
noise only (`docs/reception-snr-validation.md`); the real-noise corpus that `RealNoiseImpairmentSweepHarness`
needs is not on this machine. Coloured noise (sub-400 Hz hum, adjacent QRM) can leak into the in-band
periodogram bins and make the figure read low. Run `SyncSnrValidationProbe`'s accuracy grid on the real-noise
corpus once it exists; also add a decoder-level test that a replay backward snap resets SNR placement
(ReSync is the only discontinuity tested at decoder level today).

### SNR-GALLERY-ROW. OPEN — visual check of the Gallery SNR row

The new SNR row in the Gallery's Selected-frame panel sits in a fixed-height panel and was never checked on
screen for clipping. Check it at MainWindow's declared default size and at the Large font preset.

### R4. BLOCKED on H3 — offline callsign and country lookup

Distinct from the existing QRZ.com online lookup. Spec: `spec/08-logging.md`.

### R6. DECISION — does 1.1 stay a single-item milestone?

`spec/19-path-to-1.1.md`'s one target (the TX Template Editor redesign) is implemented.

### WATERFALL. DECISION, then maybe OPEN — waterfall smoothing

Legacy has two user-selectable (default off/1) smoothing settings. `ultracode_review.md` #20.
- **a.** Peak-hold exists on the FFT spectrum trace (`bf3da0c9`, toggle `MainWindow.axaml:715`), not on the
  waterfall. Decide whether that covers legacy's setting.
- **b.** Inter-frame averaging on the waterfall does not exist (`WaterfallSource.cs`).

### STOCK-MIG. DECISION — `StockImageLibrary` legacy `History.bin`

Today a live `Directory.EnumerateFiles` scan with no index and no import path. "Old folder left
untouched" is de facto, not decided. Decide and document, or build the migration.
`spec/07-image-pipeline.md:411`.

### AV12. MERGED into `0.9.1-beta` (`0f63fd67`), release blockers below — Avalonia 12.1 + opt-in native Wayland

Avalonia 11 has no Wayland backend, so Linux ran through XWayland. 12.1 adds an experimental native one,
opt-in via `SCANLINE_WAYLAND=1`. Local plan (gitignored): `docs/plans/avalonia-12-migration-plan.md`.

**Done (verified 2026-10-01 on nested sway 1.11 + weston 14, headless, native Wayland):**
- Build toolchain: `global.json` (10.0.100, latestFeature), `ci.yml` and `release-macos.yml` install SDK 10 +
  runtime 8, README/`build_*.md` updated. The CI change itself is unproven until a CI run.
- `Watermark=` → `PlaceholderText=` (Release build: 0 warnings). `LostFocus` handlers need no change (no warning).
- Visual pass: Avalonia 11 (`16c7103b`) vs 12 (`0f63fd67`) on X11, same click-through of all main tabs and all
  10 Options tabs: main screens differ by ≤156 blurred px (clock/live state), Options tabs ≈0.
- Wayland Phase B: `WindowingBackendInfo` + `WindowGeometryPolicy.CanPlaceWindow/BuildClosingGeometry` (position
  never saved/restored under Wayland, size still is); fail-loud on `UseWayland()` failure (stderr names
  `SCANLINE_WAYLAND`, exit 1); help guide (en + de) Troubleshooting section.
- Live Wayland checks: menus, popups, modal dialog, typing, clipboard text + image both directions, Alt+F access
  key, drag, context menu, scale 1.5 and 2, size restore/save. Alt+F/Alt+C also verified on the X11 backend
  (Xwayland) and `TxImageEditorQuickStyleFlyoutRealClickTests` is green.

**Still open** (user, 2026-10-19: items 1 and 2 stay release blockers):
1. **Windows hardware check** of the maximize workaround, `MainWindow.axaml.cs` (Avalonia#19434 block): 12 made
   `WindowState` a direct property and reworked Windows decorations. Remove only if proven unneeded. (User, other box.)
2. **Locked-keyring prompt check (manual, Linux).** No test reaches `RunPromptAsync`. Lock the login keyring in
   Seahorse → open Options (QRZ password load prompts) → unlock. Field must fill within seconds, no
   "dismissed or timed out" log line.
3. **Wayland known limits:** (a) weston kiosk-shell (forced fullscreen smaller than the fixed 1920×1032 size) → protocol
   error 4. DECIDED (user, 2026-10-19): accept and document, no code change. Already in the help, en and de
   (Troubleshooting › native Wayland). (b) wlroots 0.17 compositors (sway 1.9, labwc 0.7, Ubuntu 24.04) advertise xdg_wm_base v2, Avalonia needs
   ≥3 → documented, fails loudly. (c) `app_id` is empty on Wayland (no desktop-file/icon matching). (d) Not tested:
   file picker portal, external file drag-drop, Hyprland/GNOME/KDE, real input devices.
4. **macOS**: skipped for the spike (user, 2026-09-26). Manual `release-macos.yml` runs succeeded 2026-10-01, 10-16
   and 10-17 (run 39066615162). DECIDED (user, 2026-10-19): ship it, marked untested on a real Mac. The download text
   says so (`website/index.html`, publishes when the branch merges to master).
5. **Release metadata:** DONE — `Directory.Build.props` is `0.9.1-beta`.

Friend test build (not a release): `0.9.1-avalonia12-test+0f63fd6`, Linux x64 tarball in local `publish/dist/`.

### COMPILED-BINDINGS. OPEN, after AV12 — move XAML to compiled bindings

AV12 pins `AvaloniaUseCompiledBindingsByDefault=false` to keep today's reflection bindings (1,457 of them).
Gain: binding typos fail the build instead of blanking the UI; enables trimming/AOT later. Risk: null-intermediate
and runtime-typed bindings change behavior silently. Do it per file (`x:CompileBindings="True"`) when a file
is touched anyway, not as one big change. Not started until AV12 ships.

---

### DRM. DRM / EasyPal (HamDRM) port from QSSTV — IN PROGRESS

User decision 2026-10-16: RX + TX, BSR/FIX last, hybrid FTP excluded, DRM RX in parallel with SSTV (Options toggle).
Plan: `~/.claude/plans/drm-easypal-port.md` (2 plan-review rounds, ready to build). Ground truth is QSSTV's on-air
output. easypal-toolkit is not interoperable (its carrier/pilot/FAC layout differs from QSSTV's). User can supply
real QSSTV/EasyPal WAVs for P8.

- **DRM-P0.** DONE — QSSTV golden-vector harness (`~/tools/qsstv-drm-harness`, unmodified QSSTV 9.5.11 sources) and
  fixtures `tests/ScanlineStudio.Core.Drm.Tests/Fixtures/QsstvGolden/` (79 TX cases, bit-exact through the cell grid,
  six full 48 kHz streams). Auditor: 2 code-review rounds, go.
- **DRM-P1.** DONE — `ScanlineStudio.Core.Drm` + tests: HamDRM tables and the TX cell map (all 6 mode×SO maps match
  QSSTV's runtime table cell-for-cell), TX/RX CRC (QSSTV's CRC16 is GENIBUS-shaped, not X.25) and PRBS, mixed-radix
  2/3/4/5 FFT (FFTW conventions), Latin-1 per UTF-16 unit. Auditor: 1 code-review round, go.
- **DRM-P2.** DONE — TX source side: Karn RS + file header/distribute, naming (Qt UTF-8 round trip of MOT names
  reproduced), MOT object/schedule (incl. FIX branch), data groups, packets, FAC with callsign and QSSTV's in-place
  scrambler leak in bits 31-39, EOT tail. Byte/bit-exact on all 79 goldens. Tiny RS payloads zero-padded (QSSTV is UB;
  harness-RX gate moves to P4a). Auditor: 1 code-review round, go.
- **DRM-P3.** DONE — TX channel side: dispersal, K=7 conv code + puncturing, bit/symbol interleavers, QAM,
  per-symbol FIFO cell mapping, OFDM (IFFT, guard, −5650 Hz rotation), x86 int16 truncation. Sample-exact against
  QSSTV on all compared cases (8.7M samples, 0 off by one). Auditor: 1 code-review round, EQUIVALENT, go.
- **DRM-P4a.** DONE — `ISstvSessionService.TransmitDrmAsync` (VOX tone, fresh name stamp per send as QSSTV, PTT/RX
  safety via PlayWithPttAsync, bounded 3-frame producer), `IJpeg2000Codec` + `ScanlineStudio.Imaging.OpenJpeg`
  (official OpenJPEG 2.5.4 binaries, SHA-256 pinned, header-first decode limits; JP2 byte-identical to QSSTV's on
  linux-x64). QSSTV harness RX decodes our TX output to the identical file. Auditor: 2 code-review rounds, go.
- **DRM-REL.** OPEN — release checklist: (1) per OS, confirm `libopenjp2` sits beside the published exe and the
  "OpenJPEG 2.5.4 loaded" log appears on first DRM use (release workflows run no tests); (2) state "macOS 14+,
  glibc 2.34+" on the download page (official OpenJPEG osx-arm64 minos 14.0; libscanlineaudio already needs glibc 2.34);
  (1)+(2) DONE on Linux 2026-10-17: published linux-x64 has `libopenjp2.so.7` beside the exe, log "OpenJPEG 2.5.4
  loaded from '/tmp/p7b-pub/libopenjp2.so.7'"; download card states glibc 2.34+ and macOS 14+ (website/index.html,
  ships when the branch merges). Windows/macOS (1): the release workflows now fail the build if OpenJPEG is missing or differs from the
  vendored binary (step "Check bundled OpenJPEG"); that check passed in Windows run 39066612924 and macOS run
  39066615162 (2026-10-17). Still open for (1): the "OpenJPEG 2.5.4 loaded" log on real Windows/macOS first use.
  (3) EasyPal interop: CLOSED by the user 2026-10-17 ("EasyPal == HamDRM"). Include BSR in EasyPal mode once P9 ships.
- **DRM-P4b.** DONE — TX UI: "DRM (EasyPal)" target in the TX mode list (TargetGeometry refactor of the editor,
  SSTV unchanged), DRM block (profile, size, file size, live airtime estimate while editing, on-air callsign), Options ›
  Station profiles (Robust/Normal/Fast defaults) and VOX, sent-frames/resend, help en/de. Live check: QSSTV harness
  RX decoded a UI-sent JP2; PTT via rigctld dummy rig; estimate 80.4 s = airtime. Auditor: 2 plan-review + 2
  code-review rounds, go. The interim DRM level offset is replaced by DRM-PWR.
- **DRM-PWR.** DONE — separate Pwr per mode type (SSTV/DRM), user decision: header/TX-pane slider edits the selected
  type's level ("Pwr · SSTV"/"Pwr · DRM"), Options shows both, Tune uses the selected type, each transmission keeps
  its own type's level, presets carry the DRM level forward, DRM starts at the SSTV value. Replaced the interim DRM
  level offset. Auditor: 2 plan-review rounds + principal review, 1 code-review round, go.
- **DRM-SRC.** OPEN — the "Source (max 1024)" DRM size option, cut from P4b: its geometry, re-load and ratio rules
  need their own design (QSSTV sends the source size capped at 1024).
- **DRM-QGRID.** OPEN — a DRM slot on the TX quick-mode grid, cut from P4b: the grid types are shared with RX
  (`QuickModeGridViewModels.cs`, `RxImagePaneViewModel.cs` quick-grid), so DRM is selectable from the mode ComboBox only.
- **TX-NAME-SYNC.** OPEN, minor — the TX file name is not re-synced with the background in three pre-existing cases:
  (a) `ShowBackgroundPreview` (`TxControlsPaneViewModel.cs` ~:1075) pairs the last applied `SelectedFileName` with
  the live `HasRealBackground`, so an in-place pick after an earlier Apply of a blank canvas shows "Blank" on the
  card until the next Apply; (b) Undo of an in-place pick or Remove Background keeps the picked name, which TX
  history then records; (c) Demote-backdrop-to-background (`TxImageEditorPaneViewModel.Bake.cs` ~:61) never
  updates the name. Found by `yoniq-auditor` during TX-BLANKNAME, 2026-10-19.
- **DRM-JP2TIME.** OPEN (record) — JP2 encode time on this machine (Ryzen 7 5800X, OpenJPEG 2.5.4, median of 5):
  640×480 20 KB 17 ms / 100 KB 19 ms; 800×600 20 KB 27 ms / 100 KB 28 ms (photo-like source; a flat card
  9-15 ms). Frame count for the recount: 12-46 ms. The 400 ms debounce, not the encode, dominates the estimate
  latency; revisit only if a slower machine shows > 250 ms. Estimate compose from a 24 MP original at 800×600: 96 ms
  median (Release), so about 125 ms per estimate with the encode.
- **DRM-P5.** DONE — RX demodulation, exact QSSTV port: P5a front end (30bf4d29), P5b acquisition (cea9f8ef), P5c
  channel estimation/tracking/cells with own LU (7ff248e0). Goldens on all 79 cases; CPU ≤ 0.6 % of one core mean.
- **DRM-P6.** DONE — RX channel decoding, exact QSSTV port (FAC, mkmscmap, deinterleave, MSD + Viterbi,
  de-dispersal); all 79 cases + noisy rx_chdec goldens exact. Seam for P7a: `IDrmMscSink`.
- **DRM-P6b.** CLOSED 2026-10-17 — results in docs/drm-rx-measurements.md. Shipped: P6b-1 PPI wrap (always on).
  Dropped with data: P6b-2 lost last frame (≤ 0.3 % of runs), P6b-3 knife-edge sync (frames lost, files not), P6b-4
  facreset (23/21) and snrfull (10/19) and Tg_C (no effect). P6b-5 (capture gap → reset threshold) not measured: deferred past release, the current constant stays. P6b-UI
  shipped inside P7b. History:
  OPEN, STARTED 2026-10-17 — plan `~/.claude/plans/drm-p6b-rx-improvements.md` (harness first, then one fix per
  sub-phase, then the Options/help/docs step). P6b-1 PPI wrap DONE 2026-10-17 (always on, recorded in
  docs/improving-on-legacy.md). P6b-0 harness DONE (4f4dbe96; results in docs/drm-rx-measurements.md). Dropped:
  P6b-4 Tg_C (no effect). Measuring: P6b-2 lost last frame (corrected oracle), P6b-4 resync counters (A/B). RX improvements over QSSTV (user direction: compatible, but don't copy RX weaknesses). After an
  exact port + goldens: decode the last frame (QSSTV always loses it), fix the knife-edge sync drop (B0_q4_high_long),
  E0_q64_low_short first-frame bit errors, PPI 8-bit byte-count wrap (files with a last packet ≥ 256 bytes never
  complete, QSSTV→QSSTV too), stale code rates on a PL-only change, enough_frames on short→long interleave, type mask
  &0x07. Each with proof + UI toggle; TX stays QSSTV-identical. Candidate list with citations: parent plan, notes at
  the end. (RS1 signed-char fix already shipped in P7a-1, no toggle — user-agreed.)
  Also: the worker's 1 s gap→reset threshold is unmeasured; `DrmReceiverTests.GapInjection` shows zero-fill beating a
  reset at 2 s too.
- **HAMDRM-GATE.** OPEN, WAITING ON THE USER (set 2026-10-17, user: loop "until HamDRM is good enough for release").
  2026-10-17: items (1)-(7) DONE (P6b closed, P7b incl. P6b-UI, P7a-2 (b)(c), DRM-REL (1)-(2) Linux, DRM-DOC, P8
  automated). EasyPal (DRM-REL 3) CLOSED by the user 2026-10-17: "EasyPal == HamDRM" confirmed on their side.
  Real-QSSTV-app interop matrix DONE 2026-10-17: 18 configs (every mode × SO × QAM, protection/interleave/RS 0-4
  rotating), 18/18 byte-identical both directions (`~/tools/qsstv-app/matrix/results-2026-10-17.csv`). Left: real-station recordings (P8),
  and DRM-REL (1) at runtime on the macOS release build (the workflows' OpenJPEG check passed 2026-10-17). Windows RX
  DONE 2026-10-17 (user, `~/tools/qsstv-app/windows-check.zip`, no QSSTV on the machine): 4 real-QSSTV WAVs (A/4/RS0,
  B/64/RS3, E/64/RS4, B/16/RS1 20 kB), all saved files SHA-256-identical to the sent ones, pictures shown, callsign correct. Release needs:
  (1) P6b closed; (2) P6b-UI receive on/off in Options + help; (3) P7b minimal RX UI (sync, segment progress,
  callsign); (4) P7a-2 follow-ups (b) ImageSharp advisory and (c) orphan PNG; (5) DRM-REL (1)-(2) on Linux;
  (6) DRM-DOC; (7) P8 automated interop. Deferred past release: P9/P10, DRM-SRC, DRM-QGRID. User-only: EasyPal
  interop (DRM-REL 3), real-station recordings (P8).
- **DRM-P7a-1.** DONE `60bb3cc7` — RX source decoding in Core (packets, MOT, RS erasure decode, name sanitizer);
  79 cases + 17 rx_srcdec scenarios exact.
- **DRM-P7a-2.** DONE `afdba6f4` — Application integration (worker, file store, gallery/history, file-decode feed),
  plan rev 4. Code review: RX integration go (round 2), JP2 go (round 3). Live check: real QSSTV TX WAV (B, 16-QAM,
  RS1) received byte-identical by file decode (48 kHz and 11025 Hz, app at both rates) and by live capture; JP2 decode
  39.3 dB PSNR vs the source testcard.
- **DRM-P7a-2 follow-ups (found while building).** (a) Re-measure the RS per-level worst case on a quiet machine
  (`RsFileDecoder_WorstCaseCostPerLevel`; the build-time run was loaded). (b) CLOSED 2026-10-17 — ImageSharp NU1903 advisories:
  all 7 of 2026-10-07 are unreachable from the received-file path on 3.1.12 (TIFF encoder/decoder and
  HistogramEqualization unused, `DrmImageDecoder` registers JPEG/PNG/BMP/GIF only; the ICC CLUT parser is reachable
  in v3 only through `IccProfile.Entries`, never read; ICC conversion and EXR are v4-only). The patched 4.1.2+ needs a
  Six Labors licence key for Release builds (Drawing 3.1.3 targets) and breaks `RectangularPolygon`: stay on 3.1.12.
  Re-check on each new advisory. User: keep every file raw in `<images>/drm` + PNG.
  (c) CLOSED 2026-10-17 — an orphan `*_drm_*.png` (PNG written,
  `RecordAsync` failed) is imported by `ReconcileWithDiskAsync` as a "drm" row (its filename regex matches) without
  callsign/frequency: a recovery, same as the SSTV recorder. No change.
- **DRM-P7b.** DONE `ecd8ff54` — HamDRM receive UI, includes P6b-UI: Options › Decode toggle (live apply, failure keeps
  the dialog open), status-bar chip (mode, callsign, segments received/expected, sync, sync lost 5 s, received 8 s),
  amber Receiving look, help en/de. Plan rev 3, code review go (round 1, minor findings fixed). Live: published
  build + QSSTV interop WAV at 1920 and 1280 px, Large font. Known limits: a file with no name never reads complete;
  the chip holds its last state while TX pauses capture. Off-scope: at 1280 px + Large font the status bar already
  overflowed before P7b (frames-today/log chips hidden).
- **DRM-P8.** Automated part DONE 2026-10-17 (`7246f820`): our RX decodes QSSTV TX byte-identical (golden suite + live
  WAV), QSSTV RX decodes our TX byte-identical (2026-10-16), and AWGN no-regression against QSSTV's receiver on
  identical input (780 runs: per-run identical on normal/fast/e4high; QSSTV corrupts RS2 and loses PPI-wrap files,
  we do not). OPEN for the user: real-station recordings (QSSTV and EasyPal).
- **DRM-P9 / P10.** OPEN — BSR/FIX / waterfall text (each its own plan review).
- **DRM-DOC.** DONE 2026-10-17 — `docs/removed-features.md:808` revision (receive shipped, UI in P7b, HamDRM name,
  P9/P10 not yet). (CLAUDE.md QSSTV-as-port-target line added 2026-10-16,
  user-approved; CLAUDE.md is gitignored.)

### RESEARCH. Research list — not committed work

Look into these before deciding whether to build anything. Each needs a user decision first.

- **RES-SSTVAE. SSTVAE neural image mode** (`arodland/SSTVAE`, C++, Artistic-2.0). Experimental: an autoencoder
  sends image latents as OFDM carrier amplitudes, inspired by FreeDV RADE. Working beta on real HF, but the on-air
  format is not frozen (both stations need the same commit and model checkpoint), and it has no integration with
  other SSTV software. To check: licence of the model checkpoint (the code's Artistic-2.0 is GPL-compatible via its §4(c)(ii) relicense option), runtime/model size, whether it
  can run as a library or only as its own app, and whether the format has stabilised.
- **RES-BMPSSTV. bmp-sstv bitmap-over-PCM mode** (`ClearAct290/bmp-sstv`, Python, 2 files: `codec.py`, `GUI.py`;
  no licence file, so all rights reserved by default). Converts an image to a bitmap and plays its bytes as PCM
  audio slowly enough to stay under 1.75 kHz, then reverses it on receive. 1 star, no other software uses it. To
  check: the actual on-air format (sample rate, framing, sync, error handling), whether it survives real HF noise
  and drift, and the licence (needs one from the author before any port).

## 6. Blocked on a human

### H1. BLOCKED — FSK-ID/CW-ID legacy golden-vector fixture (B-P4)

Verifies `ClassicalCwDecoder` against real legacy behaviour, which its synthetic tests cannot. No longer
gates any default. Needs a manual capture from the real legacy YONIQ binary (Windows or a VM), two
`.wav` via `File → Rec`:
1. Post-image auto-ID: `CWID=1`, default `CWIDText` ("DE %m"), stock CW speed (10, about 28 WPM), a short
   Robot 36 image.
2. Manual CW send (`SendCWID` directly): 18 to 20 WPM, the separate `CWText` field (default "%m").

Then: place them under `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/`, add a
`LICENSES.md` row, and assert each capture's decoded text, WPM within ±5%, and (capture 1 only) CW-ID
starting after FSK-ID within the expected sample range.

### H2. BLOCKED — Windows audio round trip

The user built and tested on Windows; macOS is out of scope by user decision. Unconfirmed: whether the
`spec/13-testing.md` audio-device round trip was among what was exercised. A green `dotnet test` on
Windows is no evidence about WASAPI (see W). Needs a manual transmit-and-receive through a real Windows
audio device. Do not assume PulseAudio findings transfer to WASAPI.

### H3. BLOCKED — Clublog `cty.dat` licence

No fee, but redistribution needs a human to email Clublog's helpdesk for an individual API key. Blocks R4.

### H4. BLOCKED — Chilkat and FastReport licence status

Confirm whether either backs a real legacy feature by running the legacy binary. Assumed unused from a
source-only search (`LICENSES.md:26-29`, `docs/removed-features.md:246`).

### H5. OPEN, needs Windows — pooled-SQLite / WAL behaviour on Windows

The WAL change (`88cbc67`, `SqliteWriteAheadLogging.cs:50`) is the kind of file-locking behaviour that
differs on Windows, which locks more strictly. Never verified there.

### H6. Release-gate bookkeeping

The gate is the `spec/13-testing.md` manual hardware checklist (real rig CAT session, real audio round
trip, real third-party `rigctld` interop). Platform scope is settled: Linux plus Windows.
- **a. BLOCKED on H2** — confirm which checklist items the Windows pass covered (`spec/13-testing.md:59`
  is still `[ ]`).
- **b. OPEN** — record the two-platform scope in `spec/13-testing.md`, which still describes three
  platforms (`:51` lists `macos-latest`). `spec/` is gitignored (`.gitignore:72`), so the edit will not be
  committed.

---

## 7. Code-quality audit (2026-10-11)

Source: `docs/code-quality-audit/` (gitignored). Each item below was verified against the code by an
independent `yoniq-auditor` pass; the area report holds evidence, the corrected facts and the
recommendation. Refs are `<report>-F<n>`. Only P1 and P2 items are listed here.
**Work order (user decision 2026-10-11): code quality and efficiency first, bug fixes in a later pass.**
The quality pass follows `docs/plans/cq-README.md`: 9 reviewed plans, in this order: cq-03, cq-02, cq-q3,
cq-01, cq-q1, cq-04/cq-q4, cq-q5, then cq-q2 file splits last. The bug pass picks up later from
`docs/code-quality-audit/auditor-top10.md` and the parked plans cq-09, cq-05, cq-06, cq-10 and cq-11.

### CQ-P1. DONE — all fix-soon items are in §9

### CQ-P2. OPEN — fix when touching the code

User data, safety and error paths:
- **FLAKY-AUTOSAVE-SCRATCH.** OPEN, test-only — `SstvSessionServiceAudioAutoSaveTests.ScratchRetention_MoreThanEightUnconsumedSlices_EvictsOldestFirst_NewestExempt`
  failed in 2 of 3 full Application.Tests runs on 2026-10-19 and passes 5/5 standalone: a timing dependency under load.
  `TrySaveReceptionAudioAsync_CalledTwiceForTheSameReception_SecondCallReturnsFalse` in the same class also failed once
  under full-suite load (2026-10-19); treat the whole class as suspect.
- **CQ-09-F2-FOLLOWUPS.** OPEN, minor. (a) With `QrzLookup` unreadable, Options shows "keyring locked" plus an Unlock
  button that cannot help (`QrzCredentialService.ReadAsync` returns Unavailable; `OptionsWindowViewModel.QrzAndForwarding.cs`
  ~:65); a distinct "unreadable" status would fix it. (b) A plaintext password inside `QrzLookup.unreadable` is never
  moved to the keyring (help says to delete the entry). (d) A
  malformed `QrzLookup` inside a preset file overwrites a readable live one on apply and drops the live plaintext
  password without setting it aside (`ConfigurationPresetService.cs` ~:275). Pre-existing.
- **CQ-11-F14-FOLLOWUPS.** OPEN, minor. If CI is re-enabled: `ci.yml` checkout still persists credentials and has no
  `permissions: contents: read`. Release workflows paste `${{ github.event.inputs.tag }}` straight into `run:` scripts
  (write-access users only; pass it through `env:`). No dependabot config keeps the pinned action SHAs current.
- **CQ-09-F8.** Preset exclude/redact lists are string literals; new sections are included by default
  (`ConfigurationPresetStore.cs:26-27,45-47`). New secret fields are not pinned by tests.
- **CQ-10-F4-REST.** OPEN, minor — `StockImageLibrary` and `ReceivedFrameExporter` still decode with ImageSharp
  directly (no size cap, no `ImageLoadException`). Also: for a picture over 4096 px, an image element's natural size
  ("reset to original size") is now the downscaled size, not the file's own; user impact not checked.
- **CQ-11-F1.** Coverage check cannot pass: Core.Cw, Radio.Flrig, Radio.OmniRig missing from
  `coverage-thresholds.json`. Related: COV.
- **CQ-05-F6 / CQ-06-F5.** Raw English `ex.Message` and English file-picker type names reach the UI.
  Related: R2.

Test integrity:
- **CQ-03-F2.** PARTLY — undisposed decoder sites in SSTV tests leak temp files. Left:
  `RxBufferCaptureTests.cs:328` (`extendedDecoder` and `onDecoder`) and `RxBufferModeGatingTests.cs:40` (inline
  `new AnalogFmSstvDecoder(...Extended)`).
- **CQ-07-F2 / F3 / F4.** Giant UI test classes (10.5k / 9.5k lines), 18 reflection call sites (was 32), no shared
  VM builders (`new OptionsWindowViewModel(` inline 211 times, `new RxImagePaneViewModel(` 155 times).
- **CQ-08-F5.** Positive Application-test assertions follow fixed `Task.Delay`.
- **CQ-10-F6.** Logbook tests use fixed sleeps; one IPv6 test can pass without running.

Structure (do as partial-file splits first, then extract):
- **CQ-GOD.** 01-F1 decoder (2,645 code lines), 04-F2 TX editor VM, 05-F2 Options VM, 08-F2 session
  service, 10-F1 `TransmitImagePreparer`, 12-F2. Seams are named in each report. Step 1 (partial-file split) is
  done for all five (see verified done); step 2 (extract types along the partial-file seams) is open.
  - follow-up (doc-only): comments stranded by the fields-stay rule. Session: T1-3 Task.Yield invariant sits on
    `ToneBatchSize` in main, not on `GenerateTone` (.Transmit.cs); `ResolveTransmitSettingsAsync`'s summary sits above
    `MaxSoundFileIdBytes` in main (pre-existing misattachment). TX editor: T0-12 coalescing block on
    `_recomputePreviewScheduled`, Default* consts comment. Decoder: the getter concurrency contract
    (`AnalogFmSstvDecoder.cs` ~:889, "four properties below" → now `.Requests.cs`). Plus cross-file "above/below"
    wording in all split files.
- **CQ-04-F7 / F8.** Interaction policy in code-behind; non-re-entrant batch flag. (F3/F6 done, see below.)
- **CQ-06-F1.** `MainWindow` code-behind acts as the dialog service.
- **CQ-08-F4.** `ISstvSessionService` has ~86 members.
- **CQ-01-F2 / CQ-12-F1.** Fix-history narrative in `//` comments (~1,279 lines). Do not backfill (comment
  sweep was abandoned); add a check against new ones, and delete false comments when touching a file.

### CQ-P3. OPEN — 128 nice-to-have items

Listed with evidence in the area reports. Pick them up only when already working in that file.

- **CQ-TESTHYG.** Leftover real-clock UI test leaks found in the cq-q1 review (2026-10-11): RxImage tests that set
  `OverrideCallsign` (e.g. `PaneViewModelTests.cs:1316`) schedule a real-clock 250 ms lookup that can post into a later
  test; `PaneViewModelTests.cs:1178` `Assert.Empty(SetNoteCalls)` never waits out the 600 ms, so it is vacuous. Fix: a
  never-advanced or advanced FakeTimeProvider, as cq-q1 did for 21 tests.

- **QRZ-CANCEL-NET10.** `QrzLogbookUploaderTests.UploadAsync_GenuineCancellation_ThrowsRatherThanReturningFailure`
  passes on .NET 8.0.31 but fails deterministically when run on .NET 10 via roll-forward (no OperationCanceledException
  for a pre-cancelled token). No Logbook code changed on `codequality`. Matters only if the target moves past .NET 8.
  Found 2026-10-12 in the cq-q2 full-suite run. Bug pass.

- **CQ-PASTE-ROT.** Paste Style from a rotated box onto a perspective box leaves both rotation and perspective on
  (`TxImageEditorPaneViewModel.cs:5611` assigns `RotationDegrees` unconditionally); the editor otherwise keeps them
  exclusive. Pinned as today's behaviour in `TxEditorElementRoundTripTests` (paste-style pair (2,3)) — update that pin
  when fixing. Found by cq-04 step 1, 2026-10-11. Bug pass.

- **CQ-BLIT-FAROUT.** `TransmitImagePreparer` element blit (`DrawTemplateImage`, `(int)MathF.Round(bounds.X/Y)` ~:920)
  does an out-of-range float→int conversion for element coordinates ≥ 2^31 px: .NET 8 x64 yields int.MinValue, .NET 9+
  saturates; near int.MinValue ImageSharp's DrawImage overflows and draws the element at x/y = 0. Needs a normalized X of
  about −6.7e6, so unreachable from the UI. Pinned by a skipped strict test in `TransmitImagePreparerElementBlitTests`.
  Fix: skip an element that is fully off-canvas, computed in `long`. Found by cq-q5 step 1, 2026-10-11. Bug pass.

### CQ verified done

- **CQ-03-F1.** DONE 2026-10-11 — `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource` now compares our
  RX decode against legacy's own `*_RX.bmp` (colour limit plus horizontal/vertical ramp-offset bands, all
  measured; ±1 px, ±1 row and +N-on-B mutations each fail all 8 rows). Found AVT-PARITY and RM8-SLOPE (§1).
- **CQ-02-F1.** DONE 2026-10-11 — `f1af36ca` pins the production TX loop (14 bit-identical loop-parity cases,
  17 output fingerprints identical on .NET 8.0.31 and 10.0.12, production LPF/silence tests; 7 mutations fail).
  `c79b76e7` moves the per-sample math into one shared `TxToneSynthesizer`, with every pin unchanged.
- **CQ-02-F4/F5/F8 (P3, plan cq-q3).** DONE 2026-10-11 — `d1788f2f` one `FirDesign.MakeFilter` (2,095-case frozen-oracle
  bit identity); `3f3284b2` Notch + TX BPF on `FirDelayLine` (132-case parity); `72692a06` one Sync/Hold/Scan line loop
  for the four sequential TX encoders (43-mode segment fingerprints, 11 mutations).
- **CQ-01-F4 (+12-F10 decoder half).** DONE 2026-10-11 — `beb082f7`: `RestartableSstvDecoder` holds one
  `ResolvedSstvDecoderSettings` record; every setter stores and forwards under `_gate` (fixes a lost-update race in
  four Request methods); full-snapshot swap-survival tests plus a surface guard; 4 mutations fail.
- **CQ-07-F1 (+12-F3, 05-F4 debounce part).** DONE 2026-10-11 — one `Debouncer`/`KeyedDebouncer` on an injected
  `TimeProvider` replaces 8 hand-rolled UI debounces; 29 fixed test sleeps removed (UI.Tests ~30 s → ~11 s); 0/40
  flakes after moving 3 dispatcher-posting tests to `[AvaloniaFact]` and giving 21 never-waiting tests a fake clock.
- **CQ-04-F3 / F6 / F15 (+12-F10 geometry half; plan cq-q4).** DONE 2026-10-11 — `5d02cde6` one image-import helper;
  `2da41c9f` one Flatten/Demote bake-and-commit helper (all 5 stale conditions individually pinned); `841c642c`
  `PerspectiveElementViewModelBase` for Box/Image (notification multisets and preview-size rules unchanged).
- **CQ-04-F1.** DONE 2026-10-11 — `c6ca56db` round-trip guard tests + saved-format golden; `e5f32ae4` no Raw* defaults;
  `ffe4584d` ToSnapshot per element; `6f96bf5c` single-snapshot factories with per-type defaults; `ca1d938d`
  ApplyStyleFrom per element; `bec4e0f9` TemplateElementPersistenceMapper. Golden unchanged throughout.
- **CQ-06-F8 / F9, CQ-10-F9 / F10 (P3, plan cq-q5).** CLOSED 2026-10-11 — measured first (`538bbf12` probes). Shipped
  on correctness: `f3b087dd` shared pixel conversions (10-F9) and a weak-keyed per-source resize cache (10-F10 retention).
  Not shipped, below the plan's 20% gate: waterfall ring buffer (<=~4.5% ceiling), StrokedTextBlock cache (5-7%), direct
  element copy (<=13%), spectrum polyline (measured 16% faster, +1 KB/op, also changes line joins).
- **CQ-GOD step 1 (plan cq-q2).** DONE 2026-10-12 — pure-move partial-file splits, each proven by a Roslyn member-multiset
  diff (all nine plan checks) plus identical source-generator output: `62845f7e` TransmitImagePreparer, `3d19fef8`
  OptionsWindowViewModel, `32ebb5ed` TxImageEditorPaneViewModel, `f7d34d62` SstvSessionService, `092ce7a7`
  AnalogFmSstvDecoder (golden vectors green). Line citations into those five files predate these commits; use
  `git blame -C`.

## 8. Verified done — 0.9.2-beta features (2026-10-16)

- **FB-FORMS.** DONE — Help → Report a bug… / Send feedback… open bundled `assets/feedback/` pages in the browser, which
  post to web3forms (its free plan rejects non-browser posts with HTTP 403). Access key stored XOR-masked in `form.js`;
  tests check only its SHA-256 (`check-feedback.mjs` runs the real JS decoder). User submitted one real form of each
  kind successfully. Auditor plan review + code review: go. FB-SNAP (snap browsers cannot read hidden folders such as
  `~/.local/share`) closed by design, untested on a snap system: on Linux the filled form goes to the visible
  `~/Pictures/ScanlineStudio/Feedback/`.
- **VERSION-NOTICE.** DONE — startup "What's new" notice shown for every version until "Don't show this again for
  version X" is ticked (key = version without `+sha`); beta box only for prerelease versions. Each `<Version>` needs a
  `VersionNotice.ReleaseSummary.<version>` key in `en.json`, enforced by `BundledLocale_HasReleaseSummaryForTheBuiltVersion`.
  Adds `ILocalizationService.TryGetString`. Presets exclude the `VersionNotice` section. Auditor plan review + code review: go.
- **UPDATE-CHECK.** DONE — startup check of GitHub's release list (`/releases/latest` 404s while every release is a
  prerelease) plus Help → Check for updates…; prerelease builds are offered newer betas and releases, release builds
  only releases. "Don't show again for version X" and "Never show update notifications" (also stops the startup
  request) are remembered in the `UpdateCheck` section, excluded from presets, and can be changed in every manual
  check result. Network-supplied release URLs are validated before opening. Verified live with a 0.9.0-beta build
  against real GitHub. Auditor plan review + code review: go.

## 9. Verified done — 0.9.3-beta fixes (2026-10-16)

- **CQ-09-F2.** DONE 2026-10-19 — one unreadable `settings.json` section no longer stops the Options dialog: it shows
  that section's defaults and names it in the footer. Save moves the raw text to `<key>.unreadable` and writes valid
  values (new `TryReadSection`/`SetSectionAside`; `GetSection` still throws). Covers the 7 service sections, the
  AdifUdp/legacy GridTracker key, the dialog's own 4 sections, RadioSafety (load and save) and DrmRx (load only).
  Presets drop every `.unreadable` key (it may hold the QRZ password). Help en+de. 17 new tests, 6 mutations caught.
  Plan: `docs/plans/cq-09-settings-store-plan.md` steps 3-7. Auditor: 1 plan review + 1 code review, go.
- **CQ-10-F4.** DONE 2026-10-19 — `ImageFileLoader` reads the header first. Over 250 MP it refuses with
  `ImageLoadException(TooLarge)` before any pixel allocation. A long side over 4096 px decodes scaled to fit 4096×4096
  (`DecoderOptions.TargetSize`, first frame only). Every failure is logged and becomes `ImageLoadException`
  (NotAnImage / Corrupt / TooLarge / Unreadable). The TX picker and the editor's image add show a "too large" message
  for TooLarge, the generic one otherwise. Tradeoff: a very tight crop of a >4096 px photo can be slightly softer on air
  (help text says so). Allocation-failure → TooLarge branch untested (no allocator injection). 16 new tests,
  7 mutations caught. Auditor: 1 plan review + 1 code review, go. Rest: CQ-10-F4-REST.
- **CQ-09-F2-FOLLOWUPS (c).** DONE 2026-10-19 — an unreadable `DrmRx` or `StationId` section no longer stops receive or
  file decode. DrmRx: the start seed is skipped, so the toggle keeps its last persisted/Set value (fresh process: on).
  StationId: receive-side defaults. Both log a Warning naming the section. Read-only, never writes. An unreadable
  `Audio` section still stops receive and transmit on purpose (device and TX level). 5 new tests, 4 mutations caught.
  Auditor: 2 plan reviews + 1 code review, go.
- **CQ-11-F14.** DONE 2026-10-19 — every `uses:` in the three workflows is pinned to a commit SHA (checkout v4.4.0,
  setup-dotnet v4.3.1, msvc-dev-cmd v1.13.0; each matched its floating tag, so behaviour is unchanged). Both release
  checkouts set `persist-credentials: false`; the upload step uses `GH_TOKEN`. Auditor: 1 code review, go.
- **CQ-09-F2 follow-ups (DrmRx, QRZ writes, preset apply).** DONE 2026-10-19 — Options repairs an unreadable `DrmRx`
  section on the next Save/Apply even with the toggle untouched. QRZ password writes (plaintext, keyring set, keyring
  delete) set an unreadable `QrzLookup` aside and write, so entering the password and one Save repairs it (no more false
  "keyring write failed"). Applying a preset that carries `QrzLookup`/`QrzUpload` over an unreadable live one keeps the
  live raw text as `.unreadable`. 10 new tests, 7 mutations caught. Auditor: 1 plan review + 1 code review, go.
- **CQ-10-F2.** DONE 2026-10-19 — the QRZ XML login now sends username/password in a form POST body, never in the
  request URI (`QrzCallsignLookup.LoginAsync`). A live probe showed QRZ reads POST-body credentials ("incorrect" vs
  "required"). `RemoveAllLoggers()` stays on the QrzXmlLookup client: the lookup GET still carries the session key.
  **Release gate:** one Options › Test credentials run with a real account (not done; no credentials here). Test
  rewritten to check every request URI, 2 mutations caught. Auditor: 1 plan review + 1 code review, go.
- **SENTFRAMES-THUMB.** DONE 2026-10-19 — the TX-tab sent-frames strip now disposes a trimmed thumbnail after a
  Background-priority post, like PREVFRAMES-THUMB. `Thumbnail` is get-only, so there is no binding to null first.
  Unverified: an open Enlarge flyout on the oldest row at the moment a transmit ends (Avalonia is expected to close it
  with the row; a redraw of the disposed bitmap would otherwise throw, now caught by the UI exception handler). One new
  test, 2 mutations caught. Auditor: 1 plan review + 1 code review, go.
- **UI-UNHANDLED.** DONE 2026-10-19 — an exception that escaped a UI-thread job (event handler, async-void
  continuation, posted work) ended the app. New `UnhandledUiExceptionHandler` on `Dispatcher.UIThread.UnhandledException`
  logs it, marks it handled, and shows a non-modal "Unexpected error" notice (one open at a time, 60 s cooldown after
  close; full stack for 20 per minute, then one line each). Worker-thread exceptions are NOT covered and still end the
  app. Live-checked on native Wayland (headless rig): logged, app alive, main window usable behind the notice. Windows
  and X11 input paths are inferred, not verified — do one Windows live check before release notes claim coverage.
  Help en+de updated. 7 tests, 3 mutations caught. Auditor: 1 plan review + 1 code review, go. Open nits: wall-clock
  cooldown (use GetTimestamp), `Handle` is public, invisible active window skips the notice.
- **CQ-08-F6.** DONE 2026-10-19 — ADIF import froze the UI: measured 2000 QSOs = 17.4 s, and the work never left the
  UI thread (Microsoft.Data.Sqlite's async calls complete synchronously). Import now runs in `Task.Run` and inserts
  through the new `ILogbookRepository.AddRangeAsync` (one transaction; the same 2000 inserts took 17 ms in the probe).
  All-or-nothing: a failure adds no QSOs, so a retry cannot duplicate rows; the "partly imported" message became
  "Import failed; no QSOs were added". Help en+de updated. 4 new tests, both mutations caught. Auditor: 1 plan
  review + 1 code review, go.
- **UX-COALESCE.** DONE 2026-10-19 — a Ctrl-drag duplicate armed the "OverlayGeometry" undo-coalesce marker
  without its idle clear, so later element moves/edits were not undoable and did not count as unsaved. New
  `ArmCoalescing` pairs the marker with the clear at every site. User-accepted side effect: a Ctrl-drag is usually two
  undo steps, so Esc puts the clone back at its start and one more Ctrl+Z removes it. One new test, mutation-proven.
  Auditor: 1 plan review + 1 code review, go.
- **CQ-06-F3.** DONE 2026-10-19 — a Configurations prompt/confirm dialog that fails to open is now a cancel:
  logged, the action's failure text shown, nothing written. `InvokeConfigurationActionAsync` (async-void menu click)
  is fully guarded, including the refresh after the action. The Log QSO, Gallery re-decode (now awaits `ExecuteAsync`,
  since `Execute` rethrows a faulted task on the UI context) and File > Exit handlers log instead of crashing; Exit
  was guarded too, decided during review. 8 new VM test cases (sync throw and faulted task), mutation-proven. The
  MainWindow parts have no test (PA-Two-more). Auditor: 1 plan review + 1 code review, go.
- **CQ-11-F3.** DONE 2026-10-19 — `app.log` lines now start with `yyyy-MM-dd HH:mm:ss.fff` (local time), formatted
  with the invariant culture. New test under a culture with a `.` time separator; the shared-rotation test's cap moved
  400 → 480 for the longer line. Auditor: 1 code review, go.
- **PREVFRAMES-THUMB.** DONE 2026-10-19 — the Receive-tab previous-frames strip now nulls a trimmed row's thumbnail
  and disposes it with a Background-priority post, as the Gallery eviction does. One new test that tells a deferred
  dispose from an immediate one; both mutations caught. Auditor: 1 plan review + 1 code review, go.
- **TX-BLANKNAME.** DONE 2026-10-19 — an in-place Browse/Stock pick into the blank editor now sets the editor's
  file name, and Apply reads the current name instead of the one captured when the editor opened. A pick that
  completes after the editor was replaced, or after another pick already set a background, is dropped and logged.
  Five new tests in `PaneViewModelTests`, all mutation-proven. Auditor: 1 plan review + 1 code review, go.
- **UX-MODESWITCH-DIRTY.** DONE 2026-10-19 — a mode switch no longer marks a clean loaded template dirty. The new
  editor's checkpoint-relative dirtiness is seeded from the old editor's `IsDirtySinceLastCheckpoint` (new ctor param
  `carriedOverDirtySinceCheckpoint`); `HasUnsavedEdits` still carries over for the blank-and-untouched check.
  Two new tests in `PaneViewModelTests`, both mutation-proven. Auditor: 1 plan review + 1 code review, go.
- **ADIF-EXPORT.** DONE (verified 2026-10-19) — `LogbookSessionService.ExportAdifFileAsync` writes a `.{guid}.tmp`
  file and then `File.Move(..., overwrite: true)` (`LogbookSessionService.cs:145-156`, `3ff51563`).

- **CQ-11-F2.** DONE — `OptionsWindowViewModel` is created by a registered `Func<>` factory
  (`ActivatorUtilities`), outside the root provider's disposal tracking; the dialog's `Closed` still disposes it, and
  MainWindow now also disposes it when opening the window fails. `OptionsViewModelLifetimeTests`: per open command,
  the view-model is absent from the root provider's disposables list (read by reflection on MS.DI internals; a probe
  test proves the reader sees a tracked transient), fresh-instance check, and a graph walk pinning "no transient
  `IDisposable` among its dependencies"; each fails under its mutation. A GC-based test was dropped after review: queued
  Avalonia dispatcher posts also hold the view-model, and Host.Tests cannot drain them reliably (xunit 2, no headless
  dispatcher owner). Auditor plan review + code review: go.
- **CQ-05-F1.** DONE — `ISstvSessionService.TxVolumePercentChanged` (percent + version in settings-commit order) is
  raised by every Pwr write and by `ResyncTxVolumeFromSettingsAsync`, which a configuration preset switch now calls
  when the preset carries the Audio section (the merge bypassed the setter). The header slider follows other writers
  without writing back: it holds changes back while its own write is pending or in flight (counted from `Schedule`
  to the end of its Task) and applies the newest version when that ends. Live gain writes, including the TX-start
  seed, are version-guarded. Auditor: 2 plan-review + 2 code-review rounds, go. Known gap kept: if a header write
  fails, the header keeps the unsaved value (pre-existing).
- **CQ-09-F1 + CQ-09-F4.** DONE — a damaged `settings.json` (bad JSON, empty, NUL-filled, `null`) is moved aside as
  `settings.json.corrupt-<UTC>` and the previous save's copy `settings.json.bak` is restored on disk (staged before the
  original is touched); with no usable backup, defaults. An unreadable file (or backup) serves defaults or the backup's
  data and refuses every changing write (`SettingsUnavailableException : IOException`; a same-reference no-op
  `UpdateAsync` still succeeds). Every save keeps the replaced file as `.bak` (copy → flush → rename, after the new
  temp is flushed). `Flush(true)` before the rename in JsonSettingsStore, AppLocationOverrides and
  ConfigurationPresetStore. Startup dialog via `ISettingsRecoveryNotice` (before the version notice). ReadyRack
  TogglePin guarded; Refresh keeps working when settings are unavailable. Help en/de. Auditor: 2 plan-review rounds +
  plan text fixes, 1 code-review round, go.
- **CQ-08-F1.** DONE — removed `SetPttLockAsync`/`IsPttLocked` and `TuneAsync(leaveKeyedAfterTune)` (no production
  caller, no UI/setting/help), with their coordinator state (lock, deferred RX resume, leave-keyed latch). Normal
  TX/Tune/Stop TX/Dispose PTT safety is unchanged; the key-failure epoch bump is kept and re-justified (pinned by a new
  two-transmit test), and the confirmed-unkey skip branch is now asserted. 08-F7: Risk B deleted outright; Race 1 closed
  in effect (epochs stay; the only overlap left is the dispose backstop vs. a transmit whose bounded wait expired).
  No removed-features entry needed (legacy PTTLock is a different feature, :308); a cross-reference was added to the
  `m_TuneSat` entry. Auditor: 2 plan-review rounds + 1 code-review round, go.
- **CQ-09-F1-GAP.** OPEN — the Options dialog opened during a transient settings-read failure shows defaults, and its
  Save writes them once the file is readable again (`OptionsWindowViewModel.LoadSave.cs:40-104`).
- **CQ-09-F1-RELOC.** OPEN, minor — `Program.cs` `ApplyPendingRelocations` (legacy bridge) moves only `settings.json`,
  not `settings.json.bak`; `RelocateAsync`'s destination check ignores a stale foreign `.bak` there.
- **CQ-09-F4-OTHERS.** OPEN — `TemplateStore`, `ReceiveHistoryRecorder` and `LogbookSessionService` also `File.Move` a
  temp file; check whether they need the same flush.
