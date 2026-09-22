# BACKLOG — the one list of work that still needs doing

## 1. Decode path

### RM12-TEAL. OPEN — `rm12` fails to decode a flat teal picture

Our encoder → our decoder, clean, 11025 Hz, flat colour (40,160,150): 73680 pixels off by more than 60,
the whole picture. Flat grey decodes fine. Found incidentally 2026-09-22, not investigated. First step:
check whether a non-flat picture also fails. Review tier: full if the fix touches the decode path.

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
  or three highest-traffic cross-pane wirings. A coverage task, not a fix. Do not chase all 1371 lines.
  No test constructs `MainWindow` today.
- **b.** Fold the 35 `logger is not null` guards in that file into a null-object logger.

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
  `[UnsupportedOSPlatform("windows")]`, like TT1-18.
- **W9-a. OPEN, needs a Windows run** — Hamlib's Windows DLL discovery (`HamlibLibraryLocator.cs:20`,
  `:87-89`, `:116-118`) has tests (`HamlibWindowsDiscoveryTests.cs`, 3 `[WindowsFact]`) never run on
  Windows. This decides whether CAT works at all on Windows.
- **W9-b. OPEN** — the OmniRig row's visibility/`IsEnabled` binding (`OptionsWindowView.axaml:380`) and
  tooltip key (`OptionsWindowViewModel.cs:1281`) are unasserted. The availability flag itself is covered
  (`OptionsWindowViewModelTests.cs:2818-2820`).
- **W9-c. OPEN** — `DirectoryPathComparer` normalization (`DirectoryPathComparer.cs:17`) on Windows:
  drive-relative (`C:foo`), UNC, `\\?\`-prefixed and trailing-dot/space paths. These feed
  `AppLocationsService` and `SqliteReceiveHistoryStore`. None is tested.
- **W9-d. OPEN, low priority** — assert that the Windows-built shim exports everything `NativeAudio`
  P/Invokes. A missing export surfaces as `EntryPointNotFoundException` at runtime.
- **W9-e. OPEN** — `scanline_audio_get_device_mute` (`scanline_audio.c:1497`) does its own UTF-8-to-wide
  `MultiByteToWideChar` at `:1506`. Unasserted; `WasapiMuteQueryTests` has no multi-byte case.
- **W-PTT. OPEN, needs a Windows run** — re-run `SstvSessionServiceTests.TuneAsync_TokenCancelledMidTone_*`
  (or the full suite) on Windows to confirm `c9d9b1b3`'s deterministic cancellation gate resolves
  `windows_tests.md` Item C.
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

### UX-TR1-b. OPEN — dirty check misses load → Undo → one edit

`IsDirtySinceLastCheckpoint` (`TxImageEditorPaneViewModel.cs:1117`) compares `_editVersion` against
`_editVersionAtLastCheckpoint` (`:411`), but `Undo()` does `_editVersion--` (`:7061`). So Undo then one
edit lands back on the checkpoint value and reads clean: a stale "Loaded" badge and a skipped
discard-confirm on the next rack load or New Template. Fix: make the counter monotonic (increment on
undo too, never decrement).

### UX-THUMB1. OPEN — Gallery memory growth

`RxHistoryPaneViewModel.RefreshAsync` re-runs the whole query and re-thumbnails every entry on every
completed reception. On the "ALL" filter with a long history that is ~180 KB of undisposed unmanaged
bitmap per entry (240px cap, `Bgra8888`), churned per received frame — ~180 MB at 1000 entries.
- **a.** Dispose the previous thumbnail set on refresh. `Entries.Clear()` (`RxHistoryPaneViewModel.cs:926`)
  just drops references; the T0-11 fix (`:195-214`) disposes only `PreviewImage`.
- **b.** Add a row cap / pagination. `ReceiveHistoryFilter` is `(ModeId, From, To)`
  (`IReceiveHistoryStore.cs:121`) and `SqliteReceiveHistoryStore.QueryAsync` (`:49`) emits no `LIMIT`.
  Also fixes M1's "clearing Show today only loads the entire history".
- **c.** Virtualize the Gallery grid — a plain `UniformGrid Columns="6"` in a `ScrollViewer` today
  (`MainWindow.axaml:1301`).

### POLL. OPEN — `PollingStrategy.OnDemand` is a no-op

`RadioConnectionSpec` declares it, but `RadioController`'s poll loop never reads `Strategy`
(`RadioController.cs:181` only rejects `Scan`), so `OnDemand` behaves like `Continuous`.
`spec/02-radio-layer.md`, "Polling".

### RIG-BW. OPEN, low priority — non-atomic bandwidth read-modify-write

`RigctldClientProtocol.SetBandwidthAsync` reads then writes mode+bandwidth without holding it atomic.
Real but low-probability (Hamlib caches the just-set mode for 500 ms). A fix would touch 4 backends.
Found 2026-09-19 during the "SSB as PKT" investigation.

### ADIF-EXPORT. OPEN, verification only

`TemplateStore.SaveAsync`'s atomic write was verified; the matching `ExportAdifFileAsync` half of that
`production_audit.md` Tier 2 item was never re-checked. Check whether it writes atomically.

### SETTINGS-MIG. OPEN — `ISettingsMigration` chain has never run

No `ISettingsMigration` exists; `AppSettings.cs:22` is `CurrentSchemaVersion = 1` and `ISettingsStore.cs:3`
says migration is not implemented. Build the chain and exercise it against one real schema bump before
v1.0. `spec/12-settings.md`.

### QRZ-PW. OPEN — QRZ password stored in plaintext

`QrzLookupSettings.Password` (`QrzLookupSettings.cs:25`) lives in plain `settings.json`. Implement
`ICredentialStore` (Windows Credential Manager / libsecret) per the spec's "Secrets" design.
`spec/12-settings.md`. Only partial mitigation today: `ConfigurationPresetStore.cs:347-359` strips it from
exported presets.

---

## 4. Measure before building

### M1-a. OPEN — Gallery filter latency

Measure `RxHistoryPaneViewModel.UpdateFilteredEntries()` (`:564`) at N = 1 000, 5 000 and 20 000 entries.
Above about 50 ms per keystroke, add a 150 to 250 ms debounce. `OnSearchTextChanged` (`:558`) calls it
directly today. The row cap is UX-THUMB1-b.

### M2. DECISION — Hamlib header anchoring

Vendor a pinned `rig.h` into the test project with a `LICENSES.md` entry (Hamlib is LGPL-2.1, `CLAUDE.md`
§5), or accept a conditional skip. `hamlib/` is gitignored (`.gitignore:11`), so a test reading it
directly would silently not run on CI. `RIG_LEVEL_*` constants are verified only by comment today
(`HamlibRadioProtocolTests.cs:14`). A wrong bit-flag silently mis-reads SWR, and the SWR auto-cutoff is a
safety feature.

### M3. OPEN — T1-14, cold imaging convert-in/convert-out

Unmeasured. The working-copy bound (`WorkingCopyScaleFactor`, `TxImageEditorPaneViewModel.cs:235`) does
not apply to the paths that crop and rotate the full original for final output. Measure a representative
large source image. `TransmitImagePreparer.cs` has 10 `ToImageSharp(` call sites
(`:87, 108, 171, 228, 260, 456, 713, 919, 1758, 1783`). The hot preview path is already fused (`a7bc5be7`).

### PERF. OPEN — decoder real-time throughput is unmeasured

Benchmark and document that `ISstvDecoder` sustains real-time throughput on a defined reference machine,
ideally asserted in CI. No benchmark exists. `spec/06-sstv-dsp.md` Definition of done.

---

## 5. Product and roadmap

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
- **R3-a. DECISION, then OPEN — reception SNR.** A live SNR / noise-floor estimate in the Signal-quality
  card, optionally per line, plus one summary figure saved per received picture (Gallery, status bar).
  Legacy has no measurement (`CNoise` is a generator). First decide what "SNR" means for FM SSTV (e.g.
  sync-tone power against the between-tone floor, or frequency spread on the sync pulse), then validate
  against the noise harness and the OTA recordings. Starting it reopens the closed decode-quality
  workstream — measurement only, but a user call. DSP improvement rules apply (UI toggle).
- **R3-b. OPEN, after R3-a** — Signal-quality Min/Max.
- **R3-c. DECISION** — an Input-chain squelch. No legacy basis. Legacy's only squelch is repeater-scoped
  `m_RepSQ` (gated on `m_Repeater && !m_Sync`; uses at `sstv.cpp:1502`, `:1865`, `:2688`, `:2734`), so it
  is not a drop-in general RX squelch. Distinct from the Sync & Slant card's "Squelch level" (VIS sense
  level). Citations: `spec/17-rx-telemetry-feasibility.md`.

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

---

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
