# BACKLOG — the one list of work that still needs doing

**This is the only backlog in the repo.** If a task is not here, nobody is meant to be working on it.
Every other document records measurements, decisions, design rationale or history.

Built 2026-09-10 by sweeping `production_audit.md`, `astra-audit.md`, `docs/known-decode-defects.md`,
`docs/functional-audit-playbook.md`, `spec/14-roadmap.md`, `spec/18-path-to-1.0.md`,
`spec/19-path-to-1.1.md`, `spec/15-template-designer.md`, `fsk_cwid.md`,
`decoder_quality_improvement.md`, `~/.claude/plans/`, and a `TODO`/`FIXME` grep over `src/` and
`tests/` (which returned **zero** markers).

**Every item below was re-verified against current source on 2026-09-10**, not copied forward on
trust. Where the source disagreed with the old entry, the entry is corrected here and the correction
is stated. Items that turned out to be already done are listed at the bottom so nobody re-derives
them.

**No known defect gates a release.** The transmitter-safety and wrong-data defects are fixed and
merged (`master`, 2026-09-09).

## How to read this

| Column | Meaning |
|---|---|
| ID | Stable handle. `PA-n` = from `production_audit.md`. `TT1-n` keeps its original test-suite ID. |
| Review tier | Per `CLAUDE.md` §7. "Full" = 2-round plan-review plus up-to-3-round code-review. |

---

## 1. Decode path — do the probe first

### D1. Green-cast Fault B — run the whole-image ideal-transport probe

Extend the existing per-RGB-triple Fault-A model to a whole image. Take a photographic source through
the real encoder's channel layout, transport the channel bytes ideally (no modulation, no filters, no
demodulation), reassemble through the matching scanline decoder, then measure green bias.

- About **+4.7** means Fault B is protocol-side or TX-side. It then closes as a parity decision, the
  same way Fault A did.
- About **+2.3** means the loss is in the DSP chain, and it earns the full review cadence.

Touches no shipping code. Largest audience of anything on this list — every YCbCr mode, which is most
real colour traffic. **Do this before D2.** It may close the biggest item for free.
Detail: `docs/known-decode-defects.md` §1. Review tier: none for the probe itself.

### D2. MN/MC right-edge stripe — the one confirmed visible defect

A coloured stripe on the right edge of every MN and MC decode. Six modes, deterministic, present with
all DSP options off.

Localized to one variable. MN140 and MP140 are structurally identical apart from sync 1900 against
1200 Hz, porch 2044 against 1500 Hz, and `LuminanceMin/MaxHz` 2044-2300 against the default
(`SstvModeRegistry.cs:418-438` against `:489-511`). MP140 is clean. **The defect follows the narrow
frequency plan and nothing else.** Start with that controlled pair on one source plus a clean
noise-free decode. Look at narrow-band and sync-tone handling, not a generic end-of-line off-by-one.

Detail: `docs/known-decode-defects.md` §4. Review tier: **full** — this is decode-path state.

### D3. Range-limit the output-cutoff control per demodulator

`OptionsWindowView.axaml:986` is a `NumericUpDown` bound to `PllOutputCutoffHz` with
`Minimum="1" Maximum="3900"`, and `OptionsWindowViewModel.cs:3413` applies the value live through
`RequestPllTuning`. `PllFmDemodulator`'s own clamp is `Math.Clamp(cutoffHz, 1.0, _sampleRate * 0.45)`,
which permits 4961 Hz at 11025 Hz. So 3600 Hz passes, and a user can reach it from the Options UI
today. At 3600 Hz on a clean signal the PLL demodulator reads R +26, G +43, B +21 and the picture
visibly breaks into a herringbone pattern. Hilbert and zero-crossing merely get grainy there.

**The safe range is not the same for all three demodulators, so one shared range is wrong.** Either
range-limit per demodulator or warn.

**Do not overclaim.** The original sweep's experiment code was never located, so which demodulator
cutoff that sweep actually changed is unconfirmed. Do not assert guaranteed PLL collapse, and do not
prescribe a new tuning range from that entry alone. Advanced tuning may legitimately permit poor
combinations. That argues for a range or a warning, not for closing the item.

**Contradiction to be aware of:** `production_audit.md:1337` still lists this as dropped and
unreachable. That entry rests on the false "no UI exposes it" premise, and `:1477` reopens it with
the verification above. The reopened version is correct.
Detail: `docs/known-decode-defects.md` §3. Review tier: **full** if the fix touches the demodulator.

---

## 2. Test-integrity work — the golden-vector guard does not currently run

### PA-1 / TT1-2. Golden-vector worst-row metric

`MeasureAveragePerChannelDelta` (`GoldenVectorTests.cs:966`) sums over the whole frame, and
tolerances run 1.99 to 13.76. One fully corrupted row out of 256 moves the average by at most 1.0, so
it passes today. Add a per-row max beside the frame average, measure the current worst row per
fixture, then pin at about 1.5x to 2x. **Needs its own measurement pass first** — do not bundle it
into another change.

### PA-2 / TT1-5. Map all 43 modes to their legacy `Main.cpp` `Line*` function

Then capture one TX golden fixture per uncovered function.

**Treat current TX channel-order coverage as zero, not as 11.** All 11 `TxCapture/*.provenance` files
read `UNKNOWN-STALE-PENDING-RECAPTURE`, `StaleFixtureTheoryAttribute` skips the comparison, and the
`.mmv` files are this port's own TX output rather than legacy-generated audio. The Scottie-class guard
therefore **does not currently run**.

Do the mapping first. It is mechanical, about 43 entries, zero design risk, and it is the only thing
that tells you whether the real gap is 5 fixtures or 30. `CLAUDE.md` §3 forbids assuming a sibling
mode shares a covered mode's channel order. Biggest single structural gap in the test suite.

### TT1-18 / PA-7. Convert the 4 remaining silent-pass tests to `[SkipOnWindowsFact]`

**Partly done — re-verified 2026-09-10, and the paths in the old entry were wrong.**
`JsonSettingsStoreTests.cs` is converted (`:230` and `:281`), and `ScanlineStudio.Settings.Tests`
already carries its own copy of the attribute.

Still a bare `if (OperatingSystem.IsWindows()) return;`:

1. `tests/ScanlineStudio.Settings.Tests/AppLocationOverridesTests.cs:47`
2. `tests/ScanlineStudio.Settings.Tests/AppLocationsServiceTests.cs:148`
3. `tests/ScanlineStudio.Settings.Tests/AppLocationsServiceTests.cs:241`
4. `tests/ScanlineStudio.Host.Tests/ApplyPendingRelocationsTests.cs:219`

Only **Host.Tests** still needs its own copy of the attribute. These tests report **passed**, not
skipped, so they are vacuous rather than honest. They rely on `File.SetUnixFileMode` denial, which
root ignores — **check what user the Linux CI leg runs as first**, because if it runs as root these
are vacuous everywhere, not only on Windows.

### TT1-15 / PA-6. Dispose-race test for `MiniAudioDeviceMuteQuery`

`tests/ScanlineStudio.Core.Audio.MiniAudio.Tests/MiniAudioDeviceMuteQueryTests.cs` exists but holds
one `[RequiresPipeWireFact]` mute-state test and no dispose case. Copy the shape of its sibling
`MiniAudioDeviceEnumeratorTests.DisposeAsync_RacingConcurrentRefreshAsync_NeverThrowsUnexpectedlyOrCorruptsState`.
The failure class it closes is a native-context use-after-release — an access violation at shutdown,
not a wrong value.

### PA-5. Assert every `{loc:Translate X}` key resolves

Extend `tests/ScanlineStudio.UI.Tests/NoHardcodedAxamlStringsTests.cs`. It holds one test today, and
that test only checks the reverse direction (no hardcoded literals). A typo'd key ships as a broken
label with nothing to catch it.

### TT1-13. Replace the 3-way dispose/PTT/poll race with deterministic gates

`HamlibRadioProtocolTests.cs:393-427` sequences a dispose/PTT/poll race using `CallDelay` plus two
bare `Task.Delay(20)` calls. That is assumed ordering, not enforced ordering. This test is the
regression gate for a physically-keyed-transmitter-on-disposed-handle bug, and it is a real CI flake
risk. Apply this project's own "deterministic gates, not shared race" rule.

### PA-Two-more. After PA-5 or TT1-18 lands

1. Scoped `MainWindow.axaml.cs` tests. Target T1-12's `DataContextChanged` re-entry guard plus the
   two or three highest-traffic cross-pane wirings. Budget it as a coverage task, not a fix. Do not
   chase 1268 lines.
2. Fold the 35 `logger is not null` guards in that same file into a null-object logger.

---

## 3. Small source items, verified open

### PA-Backfill-Throw. Widen the backfill's catch past `FormatException`

`SqliteReceiveHistoryStore.cs:910` catches only `FormatException`, but `DateTimeOffset.Parse` also
throws `ArgumentOutOfRangeException` on an out-of-range offset. That escapes `EnsureSchema`, which
runs from the constructor, which resolves inside DI — **the same shape as the 00f incident**, where a
constructor-time throw made the app fail to start on every launch until the row was repaired by hand.

Pre-existing, not introduced by the 2026-09-10 quick-wins batch. Found by `yoniq-auditor` during that
batch's review and filed here rather than widening that commit. The fix mirrors the existing
skip-and-log arm: catch the second exception type alongside the first and leave that one row NULL,
which the unconditional backfill self-heals on a later start. Small, but it touches a startup path,
so it warrants its own change and its own regression test.

### PA-Factory. Extract one factory-match resolver

"Exactly one factory match" is reimplemented three times with drifted error-message wording:
`RadioController.cs:287-301` and `RadioSessionService.cs:55-63` and `:125-133`. About 15 lines. The
only observable effect today is three different error strings for one condition. Do it when already
in those files.

---

## 4. Measure before building

### M1. Gallery filter latency

Measure `RxHistoryPaneViewModel.UpdateFilteredEntries()` at N = 1 000, 5 000 and 20 000 entries.
Above about 50 ms per keystroke, add a 150 to 250 ms debounce. The deeper fix is a row cap on
`QueryAsync` (`RxHistoryPaneViewModel.cs:797`), which has none today — so clearing "Show today only"
loads the entire history.

### M2. Hamlib header anchoring

Decide one of two: vendor a pinned `rig.h` into the test project with a `LICENSES.md` entry (Hamlib
is LGPL-2.1, so `CLAUDE.md` §5 requires the entry), or accept a conditional skip. `hamlib/` is
gitignored (`.gitignore:11`), so a test reading it directly would silently not run on CI or any other
machine — the same false-PASS pattern TT1-18 is about. Worth deciding, because a wrong `RIG_LEVEL_*`
bit-flag silently mis-reads SWR, and the SWR auto-cutoff is a safety feature.

### M3. T1-14 — cold imaging convert-in/convert-out

**Status: unmeasured, not dropped.** The earlier drop reason ("every remaining call site is a
one-shot user action on an image of at most 640x496") used the working-copy bound
(`WorkingCopyScaleFactor`, `TxImageEditorPaneViewModel.cs:202`, applied at `:6724`). The full
original is retained, and it is what gets cropped for final output and rotated, so that bound does
not apply to those paths. Measure a representative large source image before closing it again.

The hot preview path really was fused (`TxImageEditorPaneViewModel.RecomputePreviewPipeline`), and
that half stands. Six `ToImageSharp`/`FromImageSharp` call sites remain in
`TransmitImagePreparer.cs`.

---

## 5. Audit work still owed

### A1. Functional audit — chunk D0

The final whole-file field-lifecycle pass over `AnalogFmSstvDecoder.cs`: every mutable field's
reset-or-preserve correctness across every teardown path. It is the last chunk, and it counts as one
of the two required clean rounds. D1 through D9 are all closed. Playbook and chunk boundaries:
`docs/functional-audit-playbook.md`. **Re-verify the line ranges before reuse** — the file has
changed since they were written.

### A2. Milestone audit at the next workstream boundary

Not scheduled work, a trigger. Prompt and cost throttles: `docs/audit-playbook.md`.

---

## 6. Product and roadmap work, not defects

### R1. `TemplateCatProtocol` fallback

**Verified 2026-09-10: zero occurrences in `src/`.** Still a real planned deliverable — it is the
named example in `CLAUDE.md` §4's binary-is-bytes rule, so its byte fields must be modelled as
`byte[]`/`ReadOnlyMemory<byte>`, never string or JSON literal. Spec: `spec/03-cat-layer.md`.

### R2. Localization completion

Remaining unlocalized views, plus a community-translation workflow. Spec: `spec/10-localization.md`.
Pairs naturally with PA-5, which would catch broken keys.

### R3. Receive-tab Sync-and-Slant, Input-chain and Signal-quality cards

Real new DSP work. No live audio-chain measurement exists for most of these. This is the long-term
counterpart to the short-term grey-out already shipped.

### R4. Offline callsign and country lookup

Distinct from QRZ.com's online lookup, which already exists. **Blocked on H3 below** (the Clublog
`cty.dat` API key), but the feature itself is real planned work, not just its blocker.
Spec: `spec/08-logging.md`.

### R5. Station-ID decode defaults — turn both on

`FskIdRxEnabled` and `CwIdRxEnabled` both default to `false`
(`src/ScanlineStudio.Core.Sstv/StationIdSettings.cs:80` and `:88`). Plan:
`~/.claude/plans/default-config-changes.md`. Two things to settle **before** flipping:

1. **The FSK half is a deliberate divergence from legacy.** Legacy ships `RXFSKID=0`. Turning it on
   is a product decision, not a port correction, so that field's doc comment must stop citing the
   legacy default as its reason and say plainly that we diverge, and why. **This half is not blocked.**
2. **The CW half is gated on H1 below.** Its doc comment cites `fsk_cwid.md` §12: not default-on
   before the classical decoder has been exercised on the legacy fixture. Either do H1 first, or
   record that the gate was consciously lifted. Do **not** silently flip past it.

Verification: a fresh-install settings load yields both true, an existing `settings.json` holding an
explicit `false` still loads as false, `dotnet test tests/ScanlineStudio.Application.Tests`, and
`node docs/help/check-help.mjs`. Also update `docs/help/index.html` wherever it states these are off
by default.

### R6. Decide whether 1.1 stays a single-item milestone

`spec/19-path-to-1.1.md`'s one confirmed target (the TX Template Editor redesign) is implemented.
Whether 1.1 picks up further targets is undecided. A user call, not an agent guess.

---

## 7. Blocked on a human — cannot be finished in this environment

### H1. B-P4 — FSK-ID/CW-ID legacy golden-vector fixture

The one open item in `fsk_cwid.md`, which is otherwise fully closed. **It gates the CW half of R5.**

Needs a manual one-time capture from the real legacy YONIQ binary (Windows or a VM). Two `.wav`
captures via `File → Rec`:

1. Post-image auto-ID path. `CWID=1`, default `CWIDText` ("DE %m"), stock CW speed (10, about 28 WPM),
   a short Robot 36 image.
2. Manual CW-send path (`SendCWID` directly, not post-image). Speed 18 to 20 WPM. Uses the separate
   `CWText` field (default "%m", no leading "DE").

Once both exist: place them under `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/`, add
a `LICENSES.md` row, and write a test asserting each capture's own decoded text, WPM within ±5% of
its real speed, and — for capture 1 only — CW-ID starting after FSK-ID within the expected sample
range.

### H2. Windows and macOS audio have never been run against hardware

`ScanlineStudio.Core.Audio.MiniAudio` is real-hardware-tested on Linux only. Windows (WASAPI) and
macOS (CoreAudio) have never been run against real or virtual hardware, because this dev sandbox is
Linux-only. **Do not assume the PulseAudio-specific findings transfer.** Needs a human on each OS.

### H3. Clublog `cty.dat` licence

No fee, but redistribution requires a human to email Clublog's helpdesk and obtain an individual API
key before bundling. **Blocks R4.**

### H4. Chilkat and FastReport licence status

Needs confirming whether either actually backs a real legacy feature, by running the legacy binary
directly. Not verifiable from source alone. Currently assumed unused and orphaned, from a source-only
search.

### H5. Pooled-SQLite behaviour on Windows

No Windows machine in this dev environment.

### H6. Release-gate decision — can 0.9 beta ship Linux-validated-only?

The actual blocker for any tagged release is the full `spec/13-testing.md` manual hardware checklist
(real rig CAT session, real audio device round-trip, real third-party `rigctld` interop) passing on
at least one Windows, one Linux and one macOS machine. **Only Linux has ever been run.**

**Open decision, not yet made:** can 0.9 beta ship Linux-validated-only and clearly labelled, with
the full three-platform pass required before 1.0 instead? Or does even the beta need all three?
Needs a user call.

---

## 8. Parked by explicit decision — do not start these unprompted

Listed so nobody re-derives them as open work. Each was parked or dropped by a user decision or a
measured negative, not by neglect.

- **All new DSP improvement candidates.** The decode-quality improvement workstream was **closed
  2026-09-07 by user decision**. Decode **defects** stay open — that is D1 to D3 above. This closure
  also covers `~/.claude/plans/audio-domain-click-detection.md` (draft v2, awaiting a review that the
  closure cancelled) and `~/.claude/plans/auto-notch.md` (design only, nothing built).
- **Real-HF-noise sweep.** Stopped unfinished by the same closure. The harness is committed and green
  (33 tests) and was never run to a measured result. Plan: `~/.claude/plans/real-hf-noise-sweep.md`.
- **`decoder_quality_improvement.md` §15 and §16.** Historical record, not a backlog.
- **Legacy `.mtm`/`.mti` template import.** **Rejected outright 2026-08-29** — a final product call,
  not a deferral. See `docs/removed-features.md`.
- **`.ini` legacy settings importer.** Parked 2026-08-15 on user redirect ("not important, put it on
  the maybe one day"). Note it is still a stated `CLAUDE.md` §2 backward-compatibility commitment,
  and real legacy fixtures exist locally at `yoniq-old/YONIQ-main/Mmsstv.ini`.
- **Phase 5 plugin system** (`IPlugin`/`PluginHost`/`IImageFilter`). Verified 2026-09-10: none exist
  in `src/`. Tier 3, parked with no implied revisit date.
- **Everything else in `spec/14-roadmap.md` Tier 3.** Perspective correction and webcam capture, full
  Hamlib extended command-set coverage, plugin sandboxing, legacy `.MDT` log import, SSTV
  repeater/beacon mode, contest logging (fully out of scope), OCR (no legacy precedent), stereo L/R
  input-chain level meters (blocked on a mono-versus-stereo architecture decision), unattended RX,
  and session frames.
- **`ui_transition_plan.md` step 14.** Rejected outright, not deferred.
- **`production_audit.md` Tier 2 and Tier 3 residue.** Real but low-value. The 2026-09-07 `auditor`
  triage returned **no MUST FIX and no `principal` round needed** across every one of them. The
  items worth doing were promoted into sections 2 and 3 above. The rest stay in that document.
- **`production_audit.md`'s "Accepted residuals"** under "Fixing 00d-00h". The user accepted those
  explicitly after verification. Do not reopen them as bugs.
- **`ASTRA-036`, `ASTRA-037`** (accepted) and **`ASTRA-007`** (withdrawn). Not active bugs.

---

## 9. Verified done during this sweep — do not re-derive

Each of these was listed as open somewhere and is not.

| Was listed as | Verified state, 2026-09-10 |
|---|---|
| `production_audit.md` items 00a-00h | Fixed and merged (`07d1a54`, `d17005b`, and the branch merge). |
| `production_audit.md` item 0c — `m_sint1` VIS freeze | Done (`ASTRA-030`, `b30c760`). `_syncBypass1PrimaryHeld` no longer exists. |
| `production_audit.md` item 8 / **TT1-16** — QRZ non-OK status | **Done, both halves.** Source: `EnsureSuccessStatusCode` at `QrzCallsignLookup.cs:173` and `:188`, and `QrzLogbookUploader.cs:53`. Test: `QrzCallsignLookupTests.cs:17` uses `HttpStatusCode.ServiceUnavailable`. TT1-16's "OPEN" mark is stale. |
| Tier 2 — `TemplateStore.SaveAsync` non-atomic write | Done. GUID temp file plus `File.Move` at `TemplateStore.cs:109-114` and `:248-274`. The `ExportAdifFileAsync` half was not re-checked. |
| `spec/15-template-designer.md` — "real open items: multi-select move only" | **Stale, contradicted by its own later bullet.** Multi-select move shipped as group-ops-lite (`a3484fa`). That document has no real open items left. |
| `~/.claude/plans/tx-editor-overlay-wysiwyg.md` | Done. `FontSizePx`, `CanvasFontSize` and `SelectedTextElementFontSizePx` all bind in `TxImageEditorPaneView.axaml`. |
| All 13 Tier 0 and 17 of 18 Tier 1 items | Done, each with a naming commit. Only T1-14 remains, as M3 above. |
| All 7 test-suite Tier 0 items | Done. |
| **TT1-19** — `ConfigurationPresetStore` concurrent-writer test | **Not open at all.** `production_audit.md:1983` marks it `DROP` (2026-09-07): `JsonSettingsStore` is covered, and `ConfigurationPresetStore` needs no equivalent because every method runs under its own `SemaphoreSlim` and one preset is one whole file, so concurrent saves are correctly last-writer-wins. Only the stale Status banner called it unconfirmed. |
| **PA-3** — WAL mode on `history.db` | **Done 2026-09-10.** New shared helper `SqliteWriteAheadLogging.TryEnable`, called from both stores' `EnsureSchema`. **Busy timeout deliberately not added**: `Microsoft.Data.Sqlite` 8.0.10's own XML docs give `SqliteCommand.CommandTimeout` a 30-second default, fed by `DefaultTimeout`, so the retry budget already exists. |
| **PA-4** — hoist the command out of the loop | **Done 2026-09-10.** Both loops in `SqliteReceiveHistoryStore` now build one command with one typed parameter set above the loop and reassign only the values per iteration. |
| **PA-Dispose** — dispose `SqliteCommand` | **Done 2026-09-10.** All 20 bare sites now use `using`. (The earlier "5 already using" count in this file was wrong — it was 8, because `SqliteReceiveHistoryStore.Deletion.cs` was already fully correct and served as the pattern.) |

**Two plan files in `~/.claude/plans/` belong to other projects, not this one:**
`how-do-i-start-quirky-simon.md` (a wxPython launcher) and `iterative-tinkering-stearns.md` (an
AGCGuard history purge). Ignore both here.

---

## Known contradictions still unresolved

Two documents disagree with themselves. Neither blocks anything, but settle each before acting on it.

1. **`TxControlsPaneViewModel.Dispose()`.** `production_audit.md`'s Tier 2 banner lists it as open
   (cancels its CTS, never disposes it, unsubscribes no editor handler). The same file's triage list
   drops it as a false positive, on the grounds that `_transmitCts` is created and disposed inside
   the transmit method's own `finally` (`:1737`/`:1776`). **The source at `:2175-2182` shows only
   `Cancel()`.** Both claims can be partly right — settle which CTS each is talking about.
2. **The PLL output cutoff.** Covered in D3 above. `production_audit.md:1337` says dropped,
   `:1477` reopens it. The reopened version is later and verified.
