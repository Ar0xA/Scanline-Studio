# 16 — GUI Wiring Survey

**Date:** 2026-08-09 (originally), refreshed 2026-08-10 — see "Refresh (2026-08-10)" note below.
**Scope:** Every screen/card/control in `ScanlineStudio.UI` — Receive/Transmit/Gallery/Logbook
tabs, the menu bar and status bar (`MainWindow.axaml`), the Radio header
(`RadioHeaderView.axaml`), the Options window (`OptionsWindowView.axaml`), and the small
Rx-image/Waterfall panes.

**Why:** the DSP/codec/CAT layers are largely done; the team is now turning to the GUI to find and
replace remaining placeholders. Before that work starts, this is a from-first-principles inventory
of what is real vs. not — not a re-statement of impressions.

**Methodology:** every interactive/data-displaying control was traced from its `.axaml` binding
back to the backing ViewModel property/command, and from there to whatever real service it does or
doesn't call. Four classifications:

- **REAL** — bound to a ViewModel property/command that is genuinely backed by working logic.
- **STUB** — `IsEnabled="False"` and/or an `Options.NotImplemented.Help`-style tooltip. Obviously unfinished.
- **FAKE-LIVE** — enabled, looks like live data, but is actually a hardcoded literal (either a literal in the `.axaml` itself, e.g. `Value="3.400"`, or a `{loc:Translate ...}` key whose locale-file value is a static string, e.g. `Panes.RxFrameMeta.SnrSlantValue` → `"21.6 dB · +3.4"`). **The most important category** — these are the ones that read as "already done" on a casual look.
- **PARTIAL** — real wiring, but incomplete (updates a local UI value without persisting, calls a stub layer, etc.).

One notable finding validating the FAKE-LIVE category, as of the original 2026-08-09 pass:
`assets/locale/en.json` was read directly. `MainWindow.StatusBar.SnrValue` = `"SNR 21.6 dB"` and
`MainWindow.StatusBar.SlantValue` = `"slant +3.4 ppm"` were both literal strings in the locale
file — not computed anywhere. **As of the 2026-08-10 refresh, this is half-stale**: `SnrValue` is
still exactly that literal (`en.json:36`), but `SlantValue` no longer exists at all — it was
replaced by `MainWindow.StatusBar.SlantValueFormat`/`SlantValueNoLock` (`en.json:37-38`) once slant
became a real computed readout (batch 1; see the Status-bar table and this doc's own "Most important
single finding" note below for the current split). The codebase's own XAML comments are unusually
candid about the FAKE-LIVE distinction already (nearly every scaffolding card carries a comment
naming it as such); this survey cross-checked those comments against the actual ViewModel code
rather than taking them at face value, and they held up.

Controls already obviously fully wired (Logbook pane, TX macro token substitution, RadioStatus VFO
frequency) get one line each per the brief; everything STUB/FAKE-LIVE/PARTIAL gets full detail.

**Auditor-verified (2026-08-09)**: the auditor spot-checked ~40 citations plus every REAL/PARTIAL
claim it sampled, confirmed all quoted `en.json` literals verbatim, and found the survey trustworthy
as the master inventory. 4 findings folded in: one missed FAKE-LIVE (TX image editor's canvas
safe-area/callsign/report plate text overlay), one reclassification (Receive tab's Previous-frames
strip, REAL → PARTIAL — genuinely populated from a real store but never refreshed by any live
session event), and two label-consistency fixes (the summary's PARTIAL category previously had zero
matching rows in the body tables; a `DataContext`-inheritance claim was imprecise about which card
it actually applies to).

**Refresh (2026-08-10)**: this doc had gone stale (its own "stale-as-of-shipping" note, now removed,
flagged this but the row-by-row edit was never done) — RX-telemetry batches 1-8b (2026-08-09,
`spec/17-rx-telemetry-feasibility.md`) and the macro-engine commit (`8556738`) shipped real wiring
behind 28 fields this doc still listed as STUB/FAKE-LIVE (20 from batches 1-7 plus the macro-engine
commit, another 8 from the waterfall/spectrum card's batches 8a/8b — the row-level tables for that
card were already updated inline back on 2026-08-09, but the Summary section's counts were never
recomputed against them until now; see the corrected Summary below). Every table below was
re-verified against current `.axaml`/ViewModel line numbers (re-grepped, not trusted from the
original pass) and updated in place; classifications that didn't change were left alone. Two
genuinely new controls found and added: Options window's Tx tab gained a real Operator name/grid
section (`8556738`), and batch 8b's Peak-hold checkbox (no prior row existed for it at all). No other
net-new controls were found across `MainWindow.axaml`, `TxControlsPaneView.axaml`,
`TxImageEditorPaneView.axaml`, `RadioHeaderView.axaml`, `OptionsWindowView.axaml` since 2026-08-09 —
everything else that changed was an existing row's classification flipping, not a new control
appearing. Options window's Decode tab was specifically re-checked against the original survey's own
speculation ("Auto-Sync/Auto-Slant toggles may now be cheap to wire since the underlying engines
exist") — confirmed still **not done**: every control on that tab, including AutoSync/AutoSlant, is
still `IsEnabled="False"` in the current `.axaml`; the speculation is still just speculation, not
completed work. **A subsequent auditor pass (2026-08-10) also caught and fixed several stale
citations left over from before this refresh** (three `RadioStatusViewModel.cs` line ranges, two
`MainWindow.axaml` card-boundary ranges, a couple of off-by-one control citations, one wrong
`en.json` line, and one flatly false off-scope note about `EntryCountText` — see each fix inline,
marked "auditor-caught"). Updated counts: see Summary section below.

---

## Receive tab

Grid: `MainWindow.axaml:97-705` (**corrected 2026-08-10, auditor-caught**: was cited as `97-648`).
Three columns — Mode/Sync/Input/Signal cards (left), Waterfall + Incoming-frame + Decode-activity
(center), Frame-metadata/Unattended-RX/Session-frames (right).

### Left column

**Mode card** (`MainWindow.axaml:116-159`, backed by `RxImage` = `RxImagePaneViewModel.cs`) — unchanged since 2026-08-09, line numbers only shifted.

| Control | Class | File:line | Note |
|---|---|---|---|
| Auto/Locked segment | FAKE-LIVE | `MainWindow.axaml:119-124` | `IsChecked="True"` on "Auto" is a static literal, not bound; "Locked" has no backing mode-lock feature at all (`RxImagePaneViewModel` only ever auto-detects). |
| Active-mode dropdown | REAL | `MainWindow.axaml:125-127` | Single `ComboBoxItem` bound to `DetectedModeDisplay`, real: driven by `ISstvSessionService.ModeDetected`. Not a real selectable combo (only ever one item), but the displayed value is genuine. |
| Quick-mode pill grid (SC1…SC2180) | STUB | `MainWindow.axaml:128-145` | 15 `Border`/`TextBlock` pills, no `Command`, pure visual. |
| Line time / Lines | REAL | `MainWindow.axaml:146-153` | `LineTimeText`/`LinesText`, both derived from the real `DetectedMode`. |
| Remaining | FAKE-LIVE | `MainWindow.axaml:154-157` | `Panes.RxImage.RemainingValue` literal loc key, no backing property. |

**Sync & slant card** (`MainWindow.axaml:166-210`, `DataContext="{Binding RxImage}"` at `:166`) — **UPDATED 2026-08-10**: Slant ppm/Sync offset/Auto-correct/Re-sync are now real, wired to `RxImagePaneViewModel` (batch 1/6, `spec/17-rx-telemetry-feasibility.md`). Source/Reset/Advanced-timing remain unwired.

| Control | Class | File:line | Note |
|---|---|---|---|
| Source | FAKE-LIVE | `MainWindow.axaml:170-172` | `Panes.RxSync.SourceValue` → `"VIS + 1200 Hz"` literal in `en.json`. Unwired — no product decision made on what "Source" should even mean here yet. |
| Slant ppm | REAL | `MainWindow.axaml:173-176` | `RxImagePaneViewModel.SlantPpmDisplay` (`RxImagePaneViewModel.cs:237`) — plain bound `TextBlock` now, not a `NumericUpDown` (the field is a live readout, not an editable input; the original survey's "editable but writes nowhere" finding no longer applies). |
| Sync offset | REAL | `MainWindow.axaml:177-180` | `SyncOffsetSamplesDisplay` (`RxImagePaneViewModel.cs:250`). |
| Auto-correct | REAL | `MainWindow.axaml:181-184` | `AutoCorrectDisplay` (`RxImagePaneViewModel.cs:288`) — 4-way AVT/Off/Locked/on-not-locked readout (batch 6), gated by new `SstvDecoderSettings.AutoSlantEnabled`. |
| Resync button | REAL | `MainWindow.axaml:186` | `RequestReSyncCommand`. |
| Reset button | STUB | `MainWindow.axaml:187` | Still `IsEnabled="False"`, no `Command` — unlike Resync, no legacy-verified semantics decided for this one yet (`spec/17`). |
| Advanced timing (Sample clock/Sync window/VIS threshold/Drop-line) | FAKE-LIVE | `MainWindow.axaml:189-207` | All 4 rows still literal loc-key values inside an `Expander` — unchanged, needs a product decision per `spec/17`. |

**Input chain card** (`MainWindow.axaml:218-258`) — **UPDATED 2026-08-10**: Device/AGC/Buffer/Clipping are now real (batches 1/3/4/5). Squelch/BPF/Notch/Noise-floor/Level L·R remain unwired — no live audio-chain measurement path exists for those specifically (Squelch/Noise-floor need a product decision, Notch is an unported filter, true stereo L/R doesn't exist in this port's mono-only demod path).

| Control | Class | File:line | Note |
|---|---|---|---|
| Device | REAL | `MainWindow.axaml:222-224` | `CaptureDeviceNameDisplay` (`RxImagePaneViewModel.cs:106`), mirrors the existing TX device-name pattern (batch 3). |
| Squelch | FAKE-LIVE | `MainWindow.axaml:225-228` | `Panes.RxInput.SquelchValue` literal — unwired, needs a product decision (`spec/17`). |
| BPF | FAKE-LIVE | `MainWindow.axaml:229-232` | `Panes.RxInput.BpfValue` literal. |
| Notch | FAKE-LIVE | `MainWindow.axaml:233-236` | `Panes.RxInput.NotchValue` literal — no notch filter exists in `Core.Sstv`. |
| AGC | REAL | `MainWindow.axaml:237-240` | `AgcGainDisplay` (`RxImagePaneViewModel.cs:370`), pure client-side derivation matching legacy's `LevelAgc.cs:103` formula exactly (batch 4). |
| Buffer | REAL | `MainWindow.axaml:241-244` | `BufferedSampleCountDisplay` (`RxImagePaneViewModel.cs:350`) — now the combined "N samples · M XRUN" format (batches 1+5). |
| Clipping | REAL | `MainWindow.axaml:245-248` | `ClippingDisplay` (`RxImagePaneViewModel.cs:358`). |
| Noise floor | FAKE-LIVE | `MainWindow.axaml:249-252` | `Panes.RxInput.NoiseFloorValue` literal — unwired. |
| Level L / Level R `ProgressBar`s | FAKE-LIVE | `MainWindow.axaml:254,256` | `Value="-14.2"`/`"-15.0"` still hardcoded directly in XAML, not bound — this port's demod path is mono-only, no real stereo L/R levels exist to bind to. |

**Signal quality card** (`MainWindow.axaml:271-299`) — **UPDATED 2026-08-10**: Sync tone and Clip lo/hi are now real (batches 1/3). SNR-per-line/Min-Max/histogram/Black·White tone remain unwired — no per-line SNR or histogram computation exists in the decode pipeline; Black/White tone isn't a well-defined measurement even in principle, unlike Sync tone.

| Control | Class | File:line | Note |
|---|---|---|---|
| SNR-per-line plot | STUB | `MainWindow.axaml:275` | Bare empty `Border Classes="plot"`, no content/data at all — unchanged. |
| Min/Max | FAKE-LIVE | `MainWindow.axaml:276-279` | `Panes.RxSignal.MinMaxValue` → `"14.8 / 24.1 dB"` literal — unchanged. |
| Luminance histogram plot | STUB | `MainWindow.axaml:281` | Same empty-`Border` pattern — unchanged. |
| Clip Lo/Hi | REAL | `MainWindow.axaml:282-285` | `ClipLoHiDisplay` (`RxImagePaneViewModel.cs:217`) — pure image-domain pixel-luminance arithmetic over the decoded image (not audio DSP), a new non-legacy statistic (batch 3). |
| Sync tone | REAL | `MainWindow.axaml:286-289` | `SyncToneDisplay` (`RxImagePaneViewModel.cs:340`), legacy-calibrated (batch 1: fixed a sign inversion and a missing +3.125Hz/+1.0Hz offset term before shipping). |
| Black tone | FAKE-LIVE | `MainWindow.axaml:290-293` | `Panes.RxSignal.BlackToneValue` literal — unwired, not a well-defined measurement per `spec/17`. |
| White tone | FAKE-LIVE | `MainWindow.axaml:294-297` | `Panes.RxSignal.WhiteToneValue` literal — same. |

### Center column

**Spectrum & waterfall card** (`MainWindow.axaml:324-395`, **corrected 2026-08-10, auditor-caught**: was cited as `311-395`, which starts one `Grid` element too early — `:311` is the centre-column layout `Grid`, not the card's own `Border`) — **UPDATED 2026-08-09, batches 8a/8b**:
every row in this card is now REAL; the whole card was FAKE-LIVE/STUB when this survey was
originally written. See `PROJECT_BRIEF.md`'s batch 8a/8b entries and `spec/14-roadmap.md`'s waterfall
color/palette item for the full auditor-reviewed implementation history — not re-derived here.

| Control | Class | File:line | Note |
|---|---|---|---|
| Spectrum plot (left half) | REAL | `MainWindow.axaml:337`, `Controls/SpectrumTraceControl.cs`, `Controls/SpectrumTraceMath.cs` | New (batch 8b): live FFT amplitude-vs-frequency trace, legacy-real SSTV control-tone markers derived from the currently-locked `SstvModeDefinition`, optional peak-hold overlay. Shares `WaterfallControl`'s `ZeroDb`/`GainDb` normalization window. |
| Waterfall control (right half) | REAL | `MainWindow.axaml:346`, `WaterfallPaneView.axaml`, `WaterfallPaneViewModel.cs`, `Controls/WaterfallControl.cs`, `Controls/WaterfallPalette.cs` | Live per `ISstvSessionService.Waterfall.Frames` as before; now colorized via a 6-stop heatmap gradient (batch 8a, was flat grayscale when this survey was written). |
| Bins/px `NumericUpDown` | REAL (read-only) | `MainWindow.axaml:355` | New (batch 8b): `IsEnabled="False"` computed telemetry readout (`SpectrumTraceControl.BinsPerPixel`, pushed via a `Mode=OneWayToSource` binding), not a user input — was previously hardcoded `Value="4"`. |
| Start, Span `NumericUpDown`s | REAL | `MainWindow.axaml:359,363` | New (batch 8b): bound to `WaterfallPaneViewModel.StartHz`/`SpanHz`, controlling the frequency window both plots render — was previously hardcoded `Value="1000"/"1600"`. |
| Gain / Zero `Slider`s | REAL | `MainWindow.axaml:372,382` | New (batch 8a): bound to `WaterfallPaneViewModel.GainDb`/`ZeroDb`, defaults measured against real captured `.mmv` audio — was previously hardcoded `Value="60"/"20"`. |
| Peak hold `CheckBox` | REAL | `MainWindow.axaml:384` | New (batch 8b): no mock2 slot existed for this control before batch 8b added it (documented addition, same class as batch 7's "Latest" button) — bound to `WaterfallPaneViewModel.PeakHoldEnabled`. |
| Both/Spec/WF view segment | REAL | `MainWindow.axaml:387-389` | New (batch 8b): bound to `WaterfallPaneViewModel.IsViewBoth`/`IsViewSpectrumOnly`/`IsViewWaterfallOnly`, driving `ColumnDefinition.Width` via `WaterfallViewModeToColumnWidthConverter` — was previously cosmetic-only `IsChecked="True"` on "Both" with no binding on any of the three.

**Incoming frame card** (`MainWindow.axaml:396-477`, **corrected 2026-08-10, auditor-caught**: was cited as `344-425`, which actually lands inside the waterfall card's `SpectrumTraceControl` bindings)

| Control | Class | File:line | Note |
|---|---|---|---|
| Received image | REAL | `MainWindow.axaml:439`, `RxImagePaneView.axaml`, `RxImagePaneViewModel.cs:70-91` | Genuinely live: `IReceivedImageBuffer.Updated` coalesced onto the UI thread. |
| Save Frame / Abort / Re-decode / Copy to TX / Log QSO buttons | STUB | `MainWindow.axaml:410-414` | No `Command` on any of the five; comment explicitly confirms `RxImagePaneViewModel` has none of these commands. |
| Frame/line count | FAKE-LIVE | `MainWindow.axaml:416` | `Panes.RxImage.FrameLineCount` literal loc key. |
| Progress bar | FAKE-LIVE | `MainWindow.axaml:417` | `Value="70"` hardcoded. |
| Previous-frames strip | REAL | `MainWindow.axaml:448` | **RECLASSIFIED 2026-08-10 (was PARTIAL)**: `ItemsSource="{Binding RxHistory.Entries}"`, populated by `RxHistoryPaneViewModel` from a real `IReceiveHistoryStore`. Batch 7 (`spec/17`) added a live `IReceiveHistoryStore.Recorded` event subscription (`RxHistoryPaneViewModel.cs:138,181`, marshaled to the UI thread) — the exact gap that earlier drove the auditor's PARTIAL reclassification (no live-session update) is now closed; this strip and the Gallery tab's list (same shared VM instance) both genuinely refresh as new frames land during an active session, not just at construction/manual-refresh/filter-change. |
| — thumbnail mode-badge pill | REAL | `MainWindow.axaml:463` | `Entry.ModeId`, real field on the real history entry. |
| — thumbnail callsign line | FAKE-LIVE | `MainWindow.axaml:466` | `Panes.RxHistory.ThumbCallsignValue` literal — `ReceiveHistoryEntry` has no callsign field at all. |
| — thumbnail timestamp | REAL | `MainWindow.axaml:467` | `Entry.ReceivedAt`, real. |

**Decode activity card** (`MainWindow.axaml:482-527`) — unchanged since 2026-08-09.

| Control | Class | File:line | Note |
|---|---|---|---|
| Decode-events `DataGrid` (UTC/Freq/Mode/Callsign/Grid/SNR/Slant/Lines/State columns) | STUB | `MainWindow.axaml:486-516` | Header-only, no `ItemsSource` anywhere — no structured decode-history event log exists, distinct from `ReceiveHistoryStore`. |
| Trace panel `ItemsControl` | STUB | `MainWindow.axaml:521` | No `ItemsSource`, empty. |

### Right column

**Frame metadata card** (`MainWindow.axaml:542-590`) — **UPDATED 2026-08-10**: Started and File size rows are now real (batch 4). Everything else remains visual scaffolding; no backing data model exists for it.

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign, Grid/Distance, Frequency, Mode/VIS, SNR/Slant, OCR confidence, Dropped lines | FAKE-LIVE | `MainWindow.axaml:546-576` | All literal loc-key values, e.g. `Panes.RxFrameMeta.CallsignValue` → `"EA7KDT"`, `Panes.RxFrameMeta.SnrSlantValue` → `"21.6 dB · +3.4"`. Unchanged. |
| Started | REAL | `MainWindow.axaml:561-564` | `StartedDisplay` (`RxImagePaneViewModel.cs:224`), new client-side capture at `ModeDetected` (batch 2). |
| File size | REAL | `MainWindow.axaml:577-580` | `FileSizeDisplay` (`RxImagePaneViewModel.cs:230`), new `IReceivedImageBuffer.Saved`/`Generation` API — only populates once the frame's own save completes; a still-in-progress or abandoned/partial frame reads "—" (batch 4). |
| Note `TextBox` | PARTIAL | `MainWindow.axaml:582` | `Text` bound to a static loc key (`Panes.RxFrameMeta.NoteValue`) via `{loc:Translate}`, which resolves to a get-only `Value` property while `TextBox.Text` is TwoWay by default — edits are visually possible but don't persist, and actually log a binding error per keystroke rather than silently going nowhere. Unchanged. |
| Override-callsign `TextBox` | PARTIAL | `MainWindow.axaml:584` | Same pattern — `Text` bound to a static loc key via the same get-only-Value/TwoWay-TextBox mismatch. Unchanged. |
| Lookup QRZ / Flag buttons | STUB | `MainWindow.axaml:586-587` | No `Command`. Unchanged. |

**Unattended RX card** (`MainWindow.axaml:592-612`) — STUB/FAKE-LIVE throughout; no scan/watch feature exists. Unchanged since 2026-08-09.

| Control | Class | File:line | Note |
|---|---|---|---|
| Watch, Scan dwell, Alert, Session | FAKE-LIVE | `MainWindow.axaml:595-610` | All literal loc-key values. |

**Session frames card** (`MainWindow.axaml:619-702`) — 15 hand-written literal rows (Callsign/Mode/Meta), not an `ItemsSource`-bound list. `SessionFrames` was grepped repo-wide and only exists in `mockups/`. STUB/FAKE-LIVE — every one of the 45 `TextBlock`s (`Row1Callsign` … `Row15Meta`) is a static loc-key value with no real property behind it. Unchanged since 2026-08-09.

---

## Transmit tab

Grid: `MainWindow.axaml:706-862`. Left = `TxControlsPaneView`, center = `ActiveEditor`
(`TxImageEditorPaneView`, nullable/swapped in), right = Queue/Mode-timing/TX-log/Recently-sent.

### Left column — TxControlsPaneView (`TxControlsPaneView.axaml`, `TxControlsPaneViewModel.cs`)

**TX mode card**

| Control | Class | File:line | Note |
|---|---|---|---|
| Auto/Manual segment | REAL | `TxControlsPaneView.axaml:19-24` | `AutoFollowRxMode`, persisted to `TxPaneUiSettings` (`TxControlsPaneViewModel.cs:356`). |
| "Auto picks" caption | FAKE-LIVE | `TxControlsPaneView.axaml:28-31` | Literal loc key. |
| Mode `ComboBox` | REAL | `TxControlsPaneView.axaml:32-40` | `AvailableModes`/`SelectedMode`, genuinely drives the encode pipeline. |
| Quick-mode pill grid | STUB | `TxControlsPaneView.axaml:41-58` | Same 15 static pills as the Receive tab's Mode card, no binding. |
| Duration / Geometry / VOX tone | FAKE-LIVE | `TxControlsPaneView.axaml:59-70` | Literal loc-key values. |
| Favorites row + "Edit favorites…" flyout | REAL | `TxControlsPaneView.axaml:71-99` | Genuinely persisted (`TxPaneUiSettings.FavoriteModeIds`, `TxControlsPaneViewModel.cs:276-330`). |

**Identification card** (`TxControlsPaneView.axaml:105-121`) — STUB, **re-confirmed 2026-08-10**: still fully literal (FSK ID/CW ID/Tail), no binding of any kind. Commit `8556738` (macro-engine + operator-profile) did NOT wire this card — it only added the Options window's Tx-tab Operator name/grid fields and 5 of the TX image editor's 12 insert-field chips (both covered elsewhere in this doc); this card's own FSK/CW/Tail rows are untouched, still blocked on a "current QSO" concept this port doesn't have (same limitation as the Outgoing-metadata card below).

**Output card** (`TxControlsPaneView.axaml:131-197`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Drive slider + value | REAL | `TxControlsPaneView.axaml:136-141` | `RadioStatus.TxVolumePercent`, genuinely persisted (debounced) via `ISstvSessionService.SetTxVolumePercentAsync`. |
| Output device | PARTIAL | `TxControlsPaneView.axaml:144-146` | The label says "Output device" but binds to the literal loc key `Panes.TxControls.OutputDeviceValue` (`"System default"`) — note the VM *does* have a real `OutputDeviceName` property (`TxControlsPaneViewModel.cs:176-177,300-312`) that is never actually wired to this control; the row displays the static placeholder instead of the real value. Flag this specifically — it's a case where the real data exists one property away from where the View reads. |
| Power/ALC/SWR meters | REAL (conditional) | `TxControlsPaneView.axaml:147-158` | `LivePowerPercent`/`LiveAlcLevel`/`LiveSwrRatio`, gated on real `ShowPowerMeter`/`ShowAlcMeter`/`ShowSwrMeter` capability flags (`TxControlsPaneViewModel.cs:415-444`). |
| SWR auto-cutoff checkbox + threshold | REAL | `TxControlsPaneView.axaml:163-168` | Persisted via `IRadioSessionService.SaveSafetySettingsAsync`, real cutoff logic (`TxControlsPaneViewModel.cs:453-468`). |
| Tone map | REAL | `TxControlsPaneView.axaml:179-181` | `ToneMapText`, real static per-mode lookup (`SstvModeDefinition.LuminanceMinHz/MaxHz`). |
| TX clock / Monitor audio / Occupied BW | FAKE-LIVE | `TxControlsPaneView.axaml:182-193` | Literal loc-key values; "Occupied BW" was explicitly researched and deliberately deferred per the file's own comment, not merely unimplemented. |
| Meter plot | STUB | `TxControlsPaneView.axaml:195` | Empty `Border Classes="plot"`. |

**Stock/browse card** — REAL (`TxControlsPaneView.axaml:200-229`): `StockEntries` from `IStockImageLibrary`, `SelectImageCommand` opens a real file picker → `TxImageEditorPaneViewModel`.

**Transmit/Stop TX buttons** — REAL (`TxControlsPaneView.axaml:236-243`): genuinely call `ISstvSessionService.TransmitAsync`, with real SWR-cutoff cancellation wiring.

**Outgoing metadata card** (`TxControlsPaneView.axaml:248-290`) — entirely STUB/FAKE-LIVE, **re-confirmed 2026-08-10**: VIS code, FSK ID, CW ID, Callsign, To-station, Grid/Beam, Report, Freq/Mode, Date UTC are all still literal loc-key values; blocked on a "current QSO context" concept that doesn't exist yet (same limitation noted on Identification card above). Not touched by the macro-engine commit.

### Center column — TX Image Editor (`TxImageEditorPaneView.axaml`, `TxImageEditorPaneViewModel.cs`)

Mixed, more real than most other scaffolding cards in this app.

| Control | Class | File:line | Note |
|---|---|---|---|
| Tool strip (Move/Crop/Scale/Rotate/Text/Box/Line/Mask/Pick, Undo/Redo/Fit/100%/Snap-grid/Safe-area) | STUB | `TxImageEditorPaneView.axaml:26-50` | Decorative only — no per-tool mode exists behind any of these; only "Move" is ever checked. |
| Preserve-aspect checkbox | REAL | `TxImageEditorPaneView.axaml:56-57` | `PreserveAspect`, drives real crop/resize math. |
| Add text button | REAL | `TxImageEditorPaneView.axaml:58-59` | `AddOverlayElementCommand`, adds a genuine draggable `OverlayElementViewModel`. |
| Apply / Cancel buttons | REAL | `TxImageEditorPaneView.axaml:60-63` | Run the real `ITransmitImagePreparer` Crop→Resize→ApplyOverlay pipeline. |
| Crop rectangle (drag body/handle) | REAL | `TxImageEditorPaneView.axaml:138-152` | Bound to `CropLeftPixels`/`CropTopPixels`/etc., genuine pointer-driven crop with legacy-verified nudge/resize semantics (`TxImageEditorPaneViewModel.cs:111-131`). |
| Overlay text elements (drag) | REAL | `TxImageEditorPaneView.axaml:154-175` | `ResolvedText` genuinely macro-resolved via `IMacroTextResolver`. |
| Safe-area guide + callsign/report plate text overlays on the canvas | FAKE-LIVE | `TxImageEditorPaneView.axaml:184-192` | `Panes.TxImageEditor.PlateCallsign`/`PlateCaption` literal loc keys (`"DL2QSK"`, `"EA7KDT · RSV 595 · 14.230 USB"`, `en.json:405-406`, **line corrected 2026-08-10, auditor-caught**: was cited as `393-394`) rendered directly over the editor canvas — arguably the most deceptive FAKE-LIVE in the app, since it reads as callsign/report text burned into the actual outgoing image. Auditor-caught gap in the original survey pass. |
| Bottom action bar (Transmit/Tune/Preview audio/Halt + progress) | STUB | `TxImageEditorPaneView.axaml:83-94` | Explicitly called out as "decorative duplicate…no Command this pass" in the file's own comment. |
| Adjustments row (Brightness/Contrast/Saturation/Gamma/Sharpen/Denoise sliders) | STUB | `TxImageEditorPaneView.axaml:97-118` | All `Value="0"` literal, no binding. |
| Real-time pipeline preview | REAL | `TxImageEditorPaneView.axaml:210-213` | `PreviewImage`, genuinely re-derived on every crop/overlay change (`TxImageEditorPaneViewModel.cs:236-247`). |
| Overlay element list (Text/X/Y fields + Remove) | REAL | `TxImageEditorPaneView.axaml:217-234` | Two-way bound to real `OverlayElementViewModel` fields. |
| Insert-field chips (12 total) | MIXED — REAL + STUB in one control | `TxImageEditorPaneView.axaml:249-266` | Only 5 of 12 chips have a `Command`: `%m` (chip3), `{grid}` (chip4), `%T` (chip7), `%D` (chip8), `{name}` (chip10) — all genuinely macro-resolved via `IMacroTextResolver`/`OperatorSettings`. The other 7 (HIS CALL/HIS GRID/FREQ/MODE/HIS RSV/DIST/BEAM — chips 1,2,5,6,9,11,12) have no `Command` at all — STUB, blocked on a "current QSO" concept, per the file's own comment. This is the single clearest example in the app of a control ROW that's half-real, half-stub with identical visual styling. |
| Source label/value row | FAKE-LIVE | `TxImageEditorPaneView.axaml:267-270` | Literal loc-key values. |
| Fill all / Clear fields buttons | STUB | `TxImageEditorPaneView.axaml:272-273` | No `Command`. |
| Text style card (Font size, Fill/stroke) | FAKE-LIVE | `TxImageEditorPaneView.axaml:280-286` | Literal loc-key values; no selection-tracking exists to apply them to. |
| Saved templates card | STUB | `TxImageEditorPaneView.axaml:293-345` | 3 hardcoded example thumbnail cards, no store exists; Fill&Send / Save template buttons have no `Command`. |

### Right column

| Card | Class | File:line | Note |
|---|---|---|---|
| Queue | STUB | `MainWindow.axaml:728-744` | Two literal rows + a literal "between frames" value; no queueing feature exists at all. |
| Mode-timing-reference table | REAL | `MainWindow.axaml:748-770` | `TxControls.ModeTimingRows`, fully computed from real `SstvModeDefinition.LineDurationMs`/`ImageHeight` — zero new data, zero placeholders. |
| TX log `DataGrid` (Utc/Mode/Dur/Drive/SWR/Result) | STUB | `MainWindow.axaml:774-800` | Header-only, no `ItemsSource` — no logging-of-sent-frames feature exists. |
| Recently sent | STUB | `MainWindow.axaml:805-857` | Refill&Queue / Open-in-editor buttons have no `Command`; 3 literal thumbnail cards, no send-history feature exists. |

---

## Gallery tab

Grid: `MainWindow.axaml:863-1032`, `DataContext="{Binding RxHistory}"` = `RxHistoryPaneViewModel.cs`. This tab is mostly real — the filter/list/storage machinery genuinely works, and (batch 7) the list now live-updates as new frames land, not just on manual refresh — with a handful of FAKE-LIVE per-entry fields where `ReceiveHistoryEntry` simply has no such field.

| Control | Class | File:line | Note |
|---|---|---|---|
| Received-frame grid + count | REAL | `MainWindow.axaml:879-880` | `Entries`/`EntryCountText`, real — `UpdateEntryCountText` (`RxHistoryPaneViewModel.cs:151-155`) correctly localizes via `_localization.GetString`. |
| Search `TextBox` | STUB | `MainWindow.axaml:889` | No `Text` binding at all — cosmetic watermark only. |
| All/Today filter segment | REAL | `MainWindow.axaml:892-893` | `ShowTodayOnly`, drives a real `IReceiveHistoryStore.QueryAsync` filter. |
| 14MHz / Unlogged / Flagged `ToggleButton`s | STUB | `MainWindow.axaml:896-898` | No binding — `ReceiveHistoryEntry` has no frequency/callsign/grid/flag field to filter by. |
| Sort combo (Newest/Callsign/SNR) | STUB | `MainWindow.axaml:899-903` | `SelectedIndex="0"` literal, no binding, no sort logic anywhere. |
| Latest button | REAL | `MainWindow.axaml:914` | **NEW row, added by batch 7 (2026-08-09), not in the original survey.** `SelectLatestCommand`, a port of legacy's real `SBPrim` "jump to most recent" speed button — deliberately NOT `SBLatest` despite that name's misleading English reading (`SBLatest` actually jumps to the OLDEST buffered frame per `Main.cpp`; see `RxHistoryPaneViewModel.SelectLatest`'s own doc comment and `docs/removed-features.md`'s "History-tab navigation" entry). No mock2 slot existed for this; added as new UI since the gap is real and legacy-documented. |
| Refresh button | REAL | `MainWindow.axaml:915` | `RefreshCommand`. |
| Thumbnail grid (image + mode badge) | REAL | `MainWindow.axaml:920-955` | `Entries`, `Entry.ModeId`, `Thumbnail` — all real; now live-updates during an active session (batch 7, `IReceiveHistoryStore.Recorded`), not just at construction/manual-refresh/filter-change. |
| Thumbnail callsign/meta lines | FAKE-LIVE | `MainWindow.axaml:947-948` | `Panes.RxHistory.ThumbCallsignValue`/`ThumbMetaValue` literal loc keys. |
| Selected-frame preview image | REAL | `MainWindow.axaml:969-970` | `PreviewImage`, real, loaded per-selection. |
| File path | REAL | `MainWindow.axaml:974` | `SelectedEntry.Entry.FilePath`. |
| Mode | REAL | `MainWindow.axaml:983` | `SelectedEntry.Entry.ModeId`. |
| SNR suffix | FAKE-LIVE | `MainWindow.axaml:984` | `Panes.RxHistory.SnrSuffixValue` → `"· 21 dB"` literal — no per-frame SNR is tracked anywhere. |
| Frequency | FAKE-LIVE | `MainWindow.axaml:990` | `Panes.RxHistory.FreqPrefixValue` literal. |
| Time | REAL | `MainWindow.axaml:991` | `SelectedEntry.Entry.ReceivedAt`. |
| Grid/Distance | FAKE-LIVE | `MainWindow.axaml:995-996` | Literal loc key. |
| Log entry status | FAKE-LIVE | `MainWindow.axaml:999-1000` | `Panes.RxHistory.LogEntryValue` literal — `LinkedQsoId` is never set non-null by any code path today. |
| Open in Log / Export frame / Re-decode buttons | STUB | `MainWindow.axaml:1003-1005` | No `Command` on any of the three. |
| Storage card: Folder | REAL | `MainWindow.axaml:1014` | `ImagesDirectory`, real, resolved via `IReceiveHistoryStore.GetImagesDirectoryAsync`. |
| Storage card: Naming / Sidecar / Disk-free | FAKE-LIVE | `MainWindow.axaml:1017-1026` | All 3 literal loc-key values — neither naming scheme nor sidecar format nor disk-free is tracked anywhere. |

---

## Logbook tab

Grid: `MainWindow.axaml:1033-1171`, `DataContext="{Binding Logbook}"` = `LogbookPaneViewModel.cs`. Fully real, no placeholders found — every field, filter, and command traces to a genuine `ILogbookSessionService` call (search, log, update, ADIF import/export). One line each, per the brief:

- Callsign/date-range search + Refresh — REAL, exact-match `CallsignFilter` against `ILogbookSessionService.SearchAsync`.
- Entry list — REAL, `Entries` populated from the search.
- Add/Edit form (Callsign/Start/End/Freq/Mode/SSTV-mode/RST×2/Name/QTH/Grid/Country/Notes) — REAL, every field is a genuine two-way-bound `QsoRecord` field; Log/Update commands genuinely persist and report GridTracker/QRZ push status.
- New button — REAL, resets the form.
- Status message — REAL, reflects actual success/failure of the last operation.
- ADIF Import/Export — REAL, `ImportAdifCommand`/`ExportAdifCommand` call the real `ILogbookSessionService` ADIF pipeline.

---

## Radio header (VFO / Favourites / Transceiver cards)

`RadioHeaderView.axaml`, backed by `RadioStatusViewModel.cs`. Always visible above the tabs. Mixed — the frequency/CAT-link/receiving state is genuinely real; most of the surrounding chrome is decorative.

**VFO card**

| Control | Class | File:line | Note |
|---|---|---|---|
| Frequency readout (40pt) | REAL | `RadioHeaderView.axaml:59-63` | `FrequencyDisplay`, genuinely driven by `IRadioSessionService.StateChanges` — subscription at `RadioStatusViewModel.cs:108`, assignment in `OnStateChanged` at `RadioStatusViewModel.cs:135-149` (**citation corrected 2026-08-10, auditor-caught**: previously cited `:118-132`, which is the constructor tail/`UpdateUtcClock`/collection declarations, not this). |
| USB/LSB/FM sideband `ToggleButton`s | STUB | `RadioHeaderView.axaml:76-78` | No binding — no sideband concept on `IRadioSessionService`; `IsChecked="True"` on USB is a static literal. |
| UTC clock | REAL | `RadioHeaderView.axaml:82` | **UPDATED 2026-08-10**: `UtcClockDisplay`, a 1s `DispatcherTimer`-driven real UTC readout (batch 4) — was a static non-ticking literal when this survey was originally written. |
| BW / Split pills | STUB | `RadioHeaderView.axaml:85-86` | Literal loc-key values, no such concept exists. |
| CAT link pill | REAL | `RadioHeaderView.axaml:88-95` | `CatLinked` property at `RadioStatusViewModel.cs:84-90`, subscription at `:109`, assignment in `OnConnectionEvent` at `:151-159` (**citation corrected 2026-08-10, auditor-caught**: previously cited `:78-84,134-142`, which is `IsReceiving`'s doc comment and `OnStateChanged`, not this). Genuinely driven by `IRadioSessionService.ConnectionEvents`. |
| Step / RIT pills | STUB | `RadioHeaderView.axaml:97-99` | Literal loc-key values. |
| Rig-meters pill | STUB | `RadioHeaderView.axaml:101-103` | Literal loc-key value. |

**Favourites card**

| Control | Class | File:line | Note |
|---|---|---|---|
| Preset recall buttons | REAL | `RadioHeaderView.axaml:127-150` | `Presets`, genuinely calls `IRadioSessionService.SetFrequencyAsync`/`SetModeAsync` on click via `ApplyPresetAsync` (`RadioStatusViewModel.cs:228-236`, the two calls at `:235-236`) (**citation corrected 2026-08-10, auditor-caught**: previously cited `:211-226`, which is a different, unrelated method). |
| Store current | PARTIAL | `RadioHeaderView.axaml:123-126` | No `Command` in this view (the VM has a real preset-save path, `SavePresetsCommand`, but this specific button isn't wired to it). |
| Hint caption | FAKE-LIVE | `RadioHeaderView.axaml:117` | Literal loc key. |
| Edit favourites list / Import / Scan buttons | STUB | `RadioHeaderView.axaml:159-164` | No `Command` on any — the real "Edit presets…" editor (`EditorRows`/`AddPresetRowCommand`/`SavePresetsCommand`) exists on the ViewModel but is deliberately unmapped to any control per direct user request (file's own comment). |

**Transceiver card**

| Control | Class | File:line | Note |
|---|---|---|---|
| Receiving toggle | REAL | `RadioHeaderView.axaml:186-187` | `IsReceiving`, genuinely starts/stops `ISstvSessionService` receive. |
| Halt button | REAL | `RadioHeaderView.axaml:188-189` | `HaltReceivingCommand`, real. |
| RX level slider | FAKE-LIVE | `RadioHeaderView.axaml:192-194` | `Value="-14.2"` hardcoded directly in XAML, no binding — no RX gain parameter exists anywhere. |
| RX level value | FAKE-LIVE | `RadioHeaderView.axaml:194` | `RadioStatus.RxLevelValue` literal loc key (matches the hardcoded slider value by coincidence, not by binding). |
| TX volume slider + value | REAL | `RadioHeaderView.axaml:198-199` | `TxVolumePercent`, same real property as the Transmit-tab Drive slider. |

Error/maintenance message rows below the grid — REAL (`RadioHeaderView.axaml:204-214`), both genuinely driven by real `ErrorMessage`/`MaintenanceMessage` properties tied to actual radio/session failure and maintenance-stop events.

---

## Menu bar and status bar (`MainWindow.axaml`)

**Menu bar** (`MainWindow.axaml:41-85`) — unchanged since 2026-08-09 (line numbers only shifted). Only `File > Open image` (`TxControls.SelectImageCommand`) and `File > Exit` (`ExitCommand`) plus the `Options...` item (`OpenOptionsCommand`) are REAL. Every other menu item across Configurations/Rig & PTT/Calibration/Tools is STUB (`IsEnabled="False"`, `Options.NotImplemented.Help` tooltip). `Help` menu has no submenu items at all (`MainWindow.axaml:71`). Callsign chip is REAL (`CallsignDisplay`, loaded from `OptionsSettingsService`).

**Status bar** (`MainWindow.axaml:1195-1219`) — **UPDATED 2026-08-10**: Frames today/Log size, Line progress, Slant, and Buffer are all now real (batches 1, 2, 4). SNR is the one field in this bar still genuinely fake — no per-line SNR computation exists anywhere in the decode pipeline.

| Control | Class | File:line | Note |
|---|---|---|---|
| Frames today | REAL | `MainWindow.axaml:1198` | `RxHistory.FramesTodayDisplay` (batch 4, independent count on `RxHistoryPaneViewModel`). |
| Log size | REAL | `MainWindow.axaml:1199` | `Logbook.LogSizeDisplay` (batch 4, independent count on `LogbookPaneViewModel`). |
| Receiving pill | REAL | `MainWindow.axaml:1202-1204` | `RadioStatus.IsReceiving`. |
| TX-inhibit LED | REAL | `MainWindow.axaml:1206-1208` | `TxControls.ErrorMessage != null`, genuinely lights on an SWR-cutoff/transmit error; this app has no persistent lockout state beyond that, so the LED's "meaning" is real but narrower than the label implies. |
| Frequency / Mode | REAL | `MainWindow.axaml:1209-1210` | `RadioStatus.FrequencyDisplay`/`ModeDisplay`. |
| Memory tag | FAKE-LIVE | `MainWindow.axaml:1211` | Literal loc key. Unchanged. |
| Detected mode | REAL | `MainWindow.axaml:1212` | `RxImage.DetectedModeText`. |
| Line progress | REAL | `MainWindow.axaml:1213` | `RxImage.LineProgressText` (batch 2), from the already-computed `IReceivedImageBuffer.Progress`. |
| SNR | FAKE-LIVE | `MainWindow.axaml:1214` | `MainWindow.StatusBar.SnrValue` → `"SNR 21.6 dB"` literal in `en.json` — still unwired, no per-line SNR computation exists in the decode pipeline. **This is the one field of the task prompt's original motivating SNR/slant pair that is still genuinely fake** — slant (below) shipped real in batch 1. |
| Slant | REAL | `MainWindow.axaml:1215` | `RxImage.SlantPpmStatusBarDisplay` (batch 1). |
| Buffer | REAL | `MainWindow.axaml:1216` | `RxImage.BufferedSampleCountStatusBarDisplay` (batches 1+5, combined "N samples · M XRUN" format). |

---

## Options window (`OptionsWindowView.axaml`, `OptionsWindowViewModel.cs`)

Window-level Save/Cancel/Reset-ALL machinery is REAL throughout (`SaveCommand` persists via `OptionsSettingsService`, `CancelCommand` discards, per-section Reset commands and the confirm-gated `RequestResetAllCommand`/`ConfirmResetAllCommand` all genuinely work). Per-tab breakdown:

### General tab

| Control | Class | File:line | Note |
|---|---|---|---|
| Language `ComboBox` | REAL | `OptionsWindowView.axaml:24-32` | `AvailableCultures`/`SelectedCulture`, genuinely calls `ILocalizationService.SetCultureAsync` on Save. |
| Remember-window-position checkbox | STUB | `OptionsWindowView.axaml:40-42` | `IsEnabled="False"` — no window-geometry persistence exists. |
| JPEG quality `NumericUpDown` | STUB | `OptionsWindowView.axaml:47-49` | `IsEnabled="False"`, `Value="85"` literal. |
| 7 waterfall/spectrum color buttons (Low/High/FFT-BG/FFT-Signal/FFT-History/FFT-Sync/FFT-Freq) | STUB | `OptionsWindowView.axaml:63-69` | All `IsEnabled="False"` — deliberately, not an oversight: re-confirmed during batch 8 (spec/14-roadmap.md's now-DONE waterfall color/palette item) that the new flat design intentionally has ONE fixed palette, not 7 user-customizable colors (this row's own help text already said so before batch 8 started). The waterfall/spectrum RENDERING is real color now (batches 8a/8b); these 7 per-element pickers were never that item's actual gap and staying disabled is not new/regressed scope. |
| Reset section | REAL | `OptionsWindowView.axaml:74` | `ResetGeneralToDefaultCommand`. |

### Audio tab

| Control | Class | File:line | Note |
|---|---|---|---|
| Capture/Playback device `ComboBox`es | REAL | `OptionsWindowView.axaml:84-104` | `CaptureDevices`/`PlaybackDevices`/`SelectedCaptureDevice`/`SelectedPlaybackDevice`, genuinely enumerated via `IAudioDeviceEnumerator`. |
| Sample rate `TextBox` | REAL | `OptionsWindowView.axaml:110` | `SampleRate`, persisted. |
| RX/TX FIFO size, Sound priority, App priority, Stereo source, Stereo TX | STUB | `OptionsWindowView.axaml:118-156` | All `IsEnabled="False"` — no FIFO-size/process-priority/stereo-source hook exists in the audio engine. |
| Reset section | REAL | `OptionsWindowView.axaml:158` | `ResetAudioToDefaultCommand`. |

### Radio tab

| Control | Class | File:line | Note |
|---|---|---|---|
| Backend radio buttons (None/rigctld/Hamlib) | REAL | `OptionsWindowView.axaml:172-180` | `IsNoneBackendSelected`/`IsRigctldBackendSelected`/`IsHamlibBackendSelected`, all 3 backends genuinely registered in DI. |
| OmniRig radio button | STUB | `OptionsWindowView.axaml:183-186` | `IsEnabled="False"` — documented as "speculative/not yet designed" 5th backend. |
| rigctld Host/Port | REAL | `OptionsWindowView.axaml:189-193` | `RigctldHost`/`RigctldPort`, persisted. |
| Hamlib Model/Serial-port/Baud-rate/PTT-type | REAL | `OptionsWindowView.axaml:196-216` | All 4 persisted. |
| RTS-on-RX / PTT-lock checkboxes | STUB | `OptionsWindowView.axaml:224-230` | Both `IsEnabled="False"` — no PTT-during-RX control surface on `IRadioController` yet. |
| Reset section | REAL | `OptionsWindowView.axaml:234` | `ResetRadioToDefaultCommand`. |

### Tx tab

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign | REAL | `OptionsWindowView.axaml:248` | Persisted. |
| Operator name / Operator grid | REAL | `OptionsWindowView.axaml:257-260` | **Confirmed 2026-08-10 (was flagged possibly-stale in the original survey)**: both persisted via `OptionsSettingsService`/`OperatorSettings`, added by commit `8556738`. No legacy MMSSTV equivalent exists (its own macro engine has no "my name"/"my QTH" token) — added specifically to back mock2's "MY NAME"/"MY GRID" overlay insert-field chips and the new `IMacroTextResolver`'s `{name}`/`{grid}` tokens (see TX Image Editor's insert-field chip row elsewhere in this doc). |
| QRZ lookup checkbox | STUB | `OptionsWindowView.axaml:270-271` | `IsEnabled="False"` — legacy hardcodes a personal API password for this feature, deliberately not resurrected. |
| Reset section | REAL | `OptionsWindowView.axaml:276` | `ResetTxToDefaultCommand`. |

### Decode tab — fully STUB (re-confirmed 2026-08-10)

Sense level, RX BPF, Demod type, RX buffer, Auto-start, Auto-stop, Auto-restart, Auto-sync,
Auto-slant — every single control (`OptionsWindowView.axaml:293-341`) is still `IsEnabled="False"`
in the current `.axaml`. The original survey speculated that commits `8556738`/`cda094d`/`4ecfea6`
(which ported real Auto-Sync/Auto-Stop engines and an operator-profile setting) might make this
tab's toggles cheap to wire, since the underlying logic may already exist — **checked directly,
still speculation, not done**: `AnalogFmSstvDecoder` and its settings surface do have some of this
now (`SstvDecoderSettings.AutoSlantEnabled` ships as of batch 6, `spec/17`), but nothing on this
Options tab was ever connected to it. Genuinely low-hanging fruit if someone picks it up, but not
shipped.

### Identification tab — fully STUB

ID method (Off/CW/Sound file), CW text/frequency/speed, sound-file path+browse, FSK
encode/decode, VOX mode/edit-tone, Tune-satellite trigger — every control
(`OptionsWindowView.axaml:355-409`) is `IsEnabled="False"`. None of CW/FSK/sound-file ID, VOX, or
satellite-tune-trigger exist. (Note: the real Tune frequency/duration fields live on
`RadioStatusViewModel` — `TuneFrequencyHz`/`TuneDurationSeconds`/`TuneCommand` — and are
intentionally NOT duplicated on this tab; they're just unmapped to any control anywhere in the
current UI, per `RadioHeaderView.axaml:173-176`'s comment.)

### Advanced tab — fully STUB

PLL (VCO gain/loop order/loop cutoff/out cutoff), Zero-crossing (order/cutoff/smoothing) +
Differentiator, TX BPF/LPF + sample-clock offset, Loopback mode, Polynomial calibration,
Clock-adjust wizard, Level-calibration wizard — every control (`OptionsWindowView.axaml:429-490`)
is `IsEnabled="False"`. Filter-response preview buttons from legacy (`DispTxBpf` etc.) were
deliberately omitted rather than stubbed, since there's nothing real yet to preview.

---

## Summary

Rough counts of individually-classified controls/rows across the whole survey (grid/table rows,
buttons, sliders, etc. — decorative structural elements like card headers/captions not counted).
**Recomputed 2026-08-10** to reflect the 2026-08-10 refresh pass (batches 1-8b + the macro-engine
commit); the 2026-08-09 originals are kept alongside for comparison.

| Classification | 2026-08-09 (original) | 2026-08-10 (current) | Notes |
|---|---|---|---|
| REAL | ~94 | ~122 | +28, in two waves that predate this specific 2026-08-10 refresh session: **wave 1 (batches 1-7 + macro-engine, accounted for by this refresh)**, +20 — 17 fields flipped from FAKE-LIVE (Sync&Slant's Slant ppm/Sync offset/Auto-correct, Input-chain's Device/AGC/Buffer/Clipping, Signal-quality's Clip lo/hi/Sync tone, Frame-metadata's Started/File size, RadioHeaderView's UTC clock, status bar's Frames-today/Log-size/Line-progress/Slant/Buffer), 1 from STUB (Sync&Slant's Resync button), 1 reclassified from PARTIAL (Previous-frames strip, now genuinely live-updating, batch 7), 1 brand-new real control (Gallery's "Latest" button, batch 7); **wave 2 (batches 8a/8b, shipped 2026-08-09 but never folded into this Summary table until now — auditor-caught gap)**, +8 — 6 fields flipped from FAKE-LIVE (waterfall's Bins/px/Start/Span/Gain/Zero, Both/Spec/WF segment), 1 from STUB (Spectrum plot, filled what was an empty placeholder), 1 brand-new real control (Peak-hold checkbox). Still concentrated in Logbook (100%), plus the areas above. |
| STUB (`IsEnabled="False"` or no-`Command`) | ~110 | ~108 | -2: Sync&Slant's Resync button (wave 1); waterfall's Spectrum plot (wave 2). Still concentrated in: Options Decode/Identification/Advanced tabs (fully stub, ~55 controls alone, re-confirmed 2026-08-10 including the Decode tab's Auto-Sync/Auto-Slant toggles specifically), Menu bar (Configurations/Rig&PTT/Calibration/Tools, ~16 items), Receive tab's Decode-activity/Session-frames/Unattended-RX cards, Sync&Slant's Reset button and Advanced-timing rows, TX image editor's tool strip and 7 of 12 insert-field chips. |
| FAKE-LIVE (literal masquerading as data) | ~71 | ~48 | -23: 17 from wave 1, 6 from wave 2 (see REAL row above for the full lists). Still concentrated in: Receive tab's remaining Sync&Slant/Input-chain/Signal-quality rows (Squelch/BPF/Notch/Noise-floor/Level L·R/Min-Max/Black·White-tone/Advanced-timing/Source), Frame-metadata card's Callsign/Grid/Frequency/Mode/SNR-Slant/OCR/Dropped-lines rows, status bar's SNR (the one field of the task's original motivating SNR/slant pair still genuinely fake — slant itself shipped real), RadioHeaderView's RX-level/BW·Split/Step·RIT/rig-meters pills, Gallery's per-entry SNR/frequency/grid-dist/log-status, TX image editor's canvas-overlay safe-area/callsign/report plate text (still the most deceptive one in the app — reads as real burned-in TX content). |
| PARTIAL | ~5 | ~4 | -1 (Previous-frames strip reclassified to REAL, batch 7 closed its live-update gap). Remaining: TxControls Output-device row (real `OutputDeviceName` property exists but the View reads a static loc key instead); RxFrameMeta Note/Override-callsign `TextBox`es (editable-looking but bound to static loc keys via a get-only `{loc:Translate}` binding, not two-way VM properties — logs a binding error per keystroke); RadioHeaderView "Store current" (real save-preset path exists on the VM but this button isn't wired to it). |

Total individually-classified controls: ~280 (2026-08-09) → ~282 (2026-08-10), the +2 being the two
brand-new controls (Gallery's "Latest" button, waterfall's Peak-hold checkbox) — every other change
above is an existing row's classification flipping, not a row appearing or disappearing.

**Fully real screens:** Logbook tab (100%). Mode-timing-reference card (Transmit tab). Menu bar's
File>Open/Exit + Options item.

**Mostly real, mixed:** Transmit tab's left column (TxControlsPaneView — mode/output/stock/
transmit machinery real, Identification/Outgoing-metadata cards fully stub, re-confirmed
2026-08-10). TX image editor (crop/overlay/preview real, tool strip and half the insert-field chips
decorative). Gallery tab (list/filter/storage real and now live-updating, several per-entry display
fields FAKE-LIVE). Radio header (frequency/CAT-link/receiving/TX-volume/UTC-clock real, sideband/
BW/step/RIT/rig-meters decorative). Options window General/Audio/Radio/Tx tabs (each has a real core
plus several STUB rows). Receive tab's Sync&Slant/Input-chain/Signal-quality cards moved from
"mostly/fully stub" into this tier as of 2026-08-10 — each now has a genuinely real core (Slant/
Sync-offset/Auto-correct/Resync; Device/AGC/Buffer/Clipping; Clip-lo-hi/Sync-tone) alongside
remaining FAKE-LIVE/STUB rows, not the near-total placeholder state they were in originally.

**Mostly/fully stub:** Receive tab's Frame-metadata card (Started/File-size now real, but Callsign/
Grid/Frequency/Mode/SNR-Slant/OCR/Dropped-lines remain fake), Decode-activity/Unattended-RX/
Session-frames cards (still fully FAKE-LIVE/STUB top to bottom — now the single densest
concentration of placeholder UI in the app, having lost that title's other three co-owners to
2026-08-10's wiring work). Options window's Decode, Identification, and Advanced tabs (100% stub,
~55 controls, re-confirmed 2026-08-10). Transmit tab's right-column Queue/TX-log/Recently-sent
cards (100% stub). MainWindow menu bar outside File/Options.

**Most important single finding (as of the original 2026-08-09 survey):** the status bar's
`SNR 21.6 dB` / `slant +3.4 ppm` readouts were literal strings sitting in `assets/locale/en.json`,
not computed anywhere. **Status as of 2026-08-10: half-closed.** Slant shipped real in batch 1
(`RxImage.SlantPpmStatusBarDisplay`, `MainWindow.axaml:1215`); SNR (`MainWindow.axaml:1214`) is
still exactly the literal it always was — no per-line SNR computation exists anywhere in the decode
pipeline. The same literal-value pattern still repeats across the Receive tab's remaining
Sync&Slant/Input-chain/Signal-quality/Frame-metadata rows and Gallery's per-entry SNR field, all
sourced from the same `en.json`. Anyone skimming the running app would still see plausible-looking
numeric readouts in these places and reasonably assume they're live.

**Correction (2026-08-10, auditor-caught):** the previous revision of this doc carried an "off-scope
note" here claiming `RxHistoryPaneViewModel.cs:69-73` builds `EntryCountText` as hardcoded English
string interpolation, bypassing `ILocalizationService`. **That claim was false** — lines 69-73 are
unrelated fields (`_previewGeneration`/`_selectedEntry`); the real implementation,
`UpdateEntryCountText` (`RxHistoryPaneViewModel.cs:151-155`), correctly calls
`_localization.GetString("Panes.RxHistory.EntryCountSingular")` /
`"Panes.RxHistory.EntryCountFormat"`, both present in `assets/locale/en.json:177-178`. No
localization violation exists here; the note is removed rather than left to mislead a future reader.
