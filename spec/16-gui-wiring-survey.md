# 16 — GUI Wiring Survey

**Date:** 2026-08-09
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

One notable finding validating the FAKE-LIVE category: `assets/locale/en.json` was read directly.
`MainWindow.StatusBar.SnrValue` = `"SNR 21.6 dB"` and `MainWindow.StatusBar.SlantValue` =
`"slant +3.4 ppm"` are literal strings in the locale file — not computed anywhere. The codebase's
own XAML comments are unusually candid about this distinction already (nearly every scaffolding
card carries a comment naming it as such); this survey cross-checked those comments against the
actual ViewModel code rather than taking them at face value, and they held up.

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

---

## Receive tab

Grid: `MainWindow.axaml:97-648`. Three columns — Mode/Sync/Input/Signal cards (left), Waterfall +
Incoming-frame + Decode-activity (center), Frame-metadata/Unattended-RX/Session-frames (right).

### Left column

**Mode card** (`MainWindow.axaml:114-157`, backed by `RxImage` = `RxImagePaneViewModel.cs`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Auto/Locked segment | FAKE-LIVE | `MainWindow.axaml:117-122` | `IsChecked="True"` on "Auto" is a static literal, not bound; "Locked" has no backing mode-lock feature at all (`RxImagePaneViewModel` only ever auto-detects, `RxImagePaneViewModel.cs:29-35`). |
| Active-mode dropdown | REAL | `MainWindow.axaml:123-125` | Single `ComboBoxItem` bound to `DetectedModeDisplay`, real: driven by `ISstvSessionService.ModeDetected` (`RxImagePaneViewModel.cs:37-65`). Not a real selectable combo (only ever one item), but the displayed value is genuine. |
| Quick-mode pill grid (SC1…SC2180) | STUB | `MainWindow.axaml:126-143` | 15 `Border`/`TextBlock` pills, no `Command`, pure visual. |
| Line time / Lines | REAL | `MainWindow.axaml:144-151` | `LineTimeText`/`LinesText`, both derived from the real `DetectedMode` (`RxImagePaneViewModel.cs:53-55`). |
| Remaining | FAKE-LIVE | `MainWindow.axaml:152-155` | `Panes.RxImage.RemainingValue` literal loc key, no backing property. |

**Sync & slant card** (`MainWindow.axaml:162-206`) — entirely visual scaffolding, no ViewModel backs it at all (not even a `DataContext` override; the `DataContext="{Binding RxImage}"` override at `:114` is scoped to the Mode card's own `Border` only — this card is a sibling that inherits the outer `MainViewModel` instead, but none of its bindings resolve to real members either way).

| Control | Class | File:line | Note |
|---|---|---|---|
| Source | FAKE-LIVE | `MainWindow.axaml:166-168` | `Panes.RxSync.SourceValue` → `"VIS + 1200 Hz"` literal in `en.json`. |
| Slant ppm `NumericUpDown` | FAKE-LIVE | `MainWindow.axaml:171` | `Value="3.400"` hardcoded directly in XAML — no binding at all, editable but writes nowhere. Matches the task's own example exactly. |
| Offset px `NumericUpDown` | FAKE-LIVE | `MainWindow.axaml:175` | `Value="-12"` hardcoded, same as above. |
| Auto-correct | FAKE-LIVE | `MainWindow.axaml:178-180` | `Panes.RxSync.AutoCorrectValue` → `"On · locked"` literal. |
| Resync / Reset buttons | STUB | `MainWindow.axaml:182-184` | No `Command` attribute at all. |
| Advanced timing (Sample clock/Sync window/VIS threshold/Drop-line) | FAKE-LIVE | `MainWindow.axaml:187-202` | All 4 rows are literal loc-key values inside an `Expander`. |

**Input chain card** (`MainWindow.axaml:210-246`) — same story: no live audio-chain measurement exists anywhere in `ScanlineStudio.Core.Audio` today.

| Control | Class | File:line | Note |
|---|---|---|---|
| Device / Squelch / BPF / Notch·AGC / Buffer / Clipping / Noise floor | FAKE-LIVE | `MainWindow.axaml:213-240` | All literal loc-key values (e.g. `Panes.RxInput.SquelchValue` → `"−38 dB"`, `Panes.RxInput.DeviceValue` → `"hw:2,0 L"`). |
| Level L / Level R `ProgressBar`s | FAKE-LIVE | `MainWindow.axaml:242,244` | `Value="-14.2"`/`"-15.0"` hardcoded directly in XAML, not bound. |

**Signal quality card** (`MainWindow.axaml:253-281`) — no per-line SNR/histogram computation exists in the decode pipeline.

| Control | Class | File:line | Note |
|---|---|---|---|
| SNR-per-line plot | STUB | `MainWindow.axaml:257` | Bare empty `Border Classes="plot"`, no content/data at all. |
| Min/Max | FAKE-LIVE | `MainWindow.axaml:258-261` | `Panes.RxSignal.MinMaxValue` → `"14.8 / 24.1 dB"` literal. |
| Luminance histogram plot | STUB | `MainWindow.axaml:263` | Same empty-`Border` pattern. |
| Clip Lo/Hi, Sync tone, Black tone, White tone | FAKE-LIVE | `MainWindow.axaml:264-279` | All literal loc-key values. |

### Center column

**Spectrum & waterfall card** (`MainWindow.axaml:303-343`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Spectrum plot (left half) | STUB | `MainWindow.axaml:310` | Empty `Border Classes="plot"` — no separate spectrum-only control exists. |
| Waterfall control (right half) | REAL | `MainWindow.axaml:311`, `WaterfallPaneView.axaml`, `WaterfallPaneViewModel.cs` | Genuinely live: `ISstvSessionService.Waterfall.Frames` pushed from the audio drain thread, coalesced onto the UI thread (`WaterfallPaneViewModel.cs:29-53`), rendered by `Controls/WaterfallControl`. This is the one real plot in the app. |
| Bins/px, Start, Span `NumericUpDown`s | FAKE-LIVE | `MainWindow.axaml:315,319,323` | Hardcoded `Value="4"/"1000"/"1600"`, no binding. |
| Gain / Zero `Slider`s | FAKE-LIVE | `MainWindow.axaml:327,331` | Hardcoded `Value="60"/"20"`, no binding. |
| Both/Spec/WF view segment | STUB | `MainWindow.axaml:333-339` | No `Command`/binding, purely cosmetic `IsChecked="True"` on "Both". |

**Incoming frame card** (`MainWindow.axaml:344-425`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Received image | REAL | `MainWindow.axaml:387`, `RxImagePaneView.axaml`, `RxImagePaneViewModel.cs:70-91` | Genuinely live: `IReceivedImageBuffer.Updated` coalesced onto the UI thread. |
| Save Frame / Abort / Re-decode / Copy to TX / Log QSO buttons | STUB | `MainWindow.axaml:358-362` | No `Command` on any of the five; comment explicitly confirms `RxImagePaneViewModel` has none of these commands. |
| Frame/line count | FAKE-LIVE | `MainWindow.axaml:364` | `Panes.RxImage.FrameLineCount` literal loc key. |
| Progress bar | FAKE-LIVE | `MainWindow.axaml:365` | `Value="70"` hardcoded. |
| Previous-frames strip | PARTIAL | `MainWindow.axaml:396-421` | `ItemsSource="{Binding RxHistory.Entries}"`, genuinely populated by `RxHistoryPaneViewModel.RefreshAsync` from a real `IReceiveHistoryStore` (`RxHistoryPaneViewModel.cs:90-135`) — but that VM subscribes to no live session/decode event (its own doc comment says so explicitly, `:11-17`), refreshing only at construction, on its own `RefreshCommand`, or on `ShowTodayOnly` change. The Receive tab has no Refresh control anywhere (only Gallery does, `MainWindow.axaml:843`), so during a live session this strip shows whatever existed at app start and never updates as new frames land, even though they're genuinely being recorded underneath. Auditor-caught reclassification (was REAL). |
| — thumbnail mode-badge pill | REAL | `MainWindow.axaml:411` | `Entry.ModeId`, real field on the real history entry. |
| — thumbnail callsign line | FAKE-LIVE | `MainWindow.axaml:414` | `Panes.RxHistory.ThumbCallsignValue` literal — `ReceiveHistoryEntry` has no callsign field at all. |
| — thumbnail timestamp | REAL | `MainWindow.axaml:415` | `Entry.ReceivedAt`, real. |

**Decode activity card** (`MainWindow.axaml:430-475`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Decode-events `DataGrid` (UTC/Freq/Mode/Callsign/Grid/SNR/Slant/Lines/State columns) | STUB | `MainWindow.axaml:434-464` | Header-only, no `ItemsSource` anywhere — no structured decode-history event log exists, distinct from `ReceiveHistoryStore`. |
| Trace panel `ItemsControl` | STUB | `MainWindow.axaml:469` | No `ItemsSource`, empty. |

### Right column

**Frame metadata card** (`MainWindow.axaml:486-534`) — entirely visual scaffolding; no backing data model exists for any of it.

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign, Grid/Distance, Frequency, Mode/VIS, Started, SNR/Slant, OCR confidence, Dropped lines, File size | FAKE-LIVE | `MainWindow.axaml:490-524` | All literal loc-key values, e.g. `Panes.RxFrameMeta.CallsignValue` → `"EA7KDT"`, `Panes.RxFrameMeta.SnrSlantValue` → `"21.6 dB · +3.4"`. |
| Note `TextBox` | PARTIAL | `MainWindow.axaml:526` | `Text` bound to a static loc key (`Panes.RxFrameMeta.NoteValue`) via `{loc:Translate}`, which resolves to a get-only `Value` property (`TranslateExtension.cs:44-47`) while `TextBox.Text` is TwoWay by default — edits are visually possible but don't persist, and (auditor-caught detail) actually log a binding error per keystroke rather than silently going nowhere. |
| Override-callsign `TextBox` | PARTIAL | `MainWindow.axaml:528` | Same pattern — `Text` bound to a static loc key via the same get-only-Value/TwoWay-TextBox mismatch. |
| Lookup QRZ / Flag buttons | STUB | `MainWindow.axaml:530-531` | No `Command`. |

**Unattended RX card** (`MainWindow.axaml:536-556`) — STUB/FAKE-LIVE throughout; no scan/watch feature exists.

| Control | Class | File:line | Note |
|---|---|---|---|
| Watch, Scan dwell, Alert, Session | FAKE-LIVE | `MainWindow.axaml:539-554` | All literal loc-key values. |

**Session frames card** (`MainWindow.axaml:563-646`) — 15 hand-written literal rows (Callsign/Mode/Meta), not an `ItemsSource`-bound list. `SessionFrames` was grepped repo-wide and only exists in `mockups/`. STUB/FAKE-LIVE — every one of the 45 `TextBlock`s (`Row1Callsign` … `Row15Meta`) is a static loc-key value with no real property behind it.

---

## Transmit tab

Grid: `MainWindow.axaml:650-805`. Left = `TxControlsPaneView`, center = `ActiveEditor`
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

**Identification card** (`TxControlsPaneView.axaml:105-121`) — STUB. FSK ID, CW ID, Tail are all literal loc-key values; blocked on the still-missing "current QSO"/operator-profile-driven concept (per the file's own comment — note this predates the operator-profile setting added in commit `8556738`, so it may be partially stale, but no binding exists here regardless).

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

**Outgoing metadata card** (`TxControlsPaneView.axaml:248-290`) — entirely STUB/FAKE-LIVE: VIS code, FSK ID, CW ID, Callsign, To-station, Grid/Beam, Report, Freq/Mode, Date UTC are all literal loc-key values; blocked on a "current QSO context" concept that doesn't exist yet (same limitation noted on Identification card above).

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
| Safe-area guide + callsign/report plate text overlays on the canvas | FAKE-LIVE | `TxImageEditorPaneView.axaml:184-192` | `Panes.TxImageEditor.PlateCallsign`/`PlateCaption` literal loc keys (`"DL2QSK"`, `"EA7KDT · RSV 595 · 14.230 USB"`, `en.json:393-394`) rendered directly over the editor canvas — arguably the most deceptive FAKE-LIVE in the app, since it reads as callsign/report text burned into the actual outgoing image. Auditor-caught gap in the original survey pass. |
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
| Queue | STUB | `MainWindow.axaml:672-688` | Two literal rows + a literal "between frames" value; no queueing feature exists at all. |
| Mode-timing-reference table | REAL | `MainWindow.axaml:690-714` | `TxControls.ModeTimingRows`, fully computed from real `SstvModeDefinition.LineDurationMs`/`ImageHeight` — zero new data, zero placeholders. |
| TX log `DataGrid` (Utc/Mode/Dur/Drive/SWR/Result) | STUB | `MainWindow.axaml:718-744` | Header-only, no `ItemsSource` — no logging-of-sent-frames feature exists. |
| Recently sent | STUB | `MainWindow.axaml:749-801` | Refill&Queue / Open-in-editor buttons have no `Command`; 3 literal thumbnail cards, no send-history feature exists. |

---

## Gallery tab

Grid: `MainWindow.axaml:807-959`, `DataContext="{Binding RxHistory}"` = `RxHistoryPaneViewModel.cs`. This tab is mostly real — the filter/list/storage machinery genuinely works — with a handful of FAKE-LIVE per-entry fields where `ReceiveHistoryEntry` simply has no such field.

| Control | Class | File:line | Note |
|---|---|---|---|
| Received-frame grid + count | REAL | `MainWindow.axaml:815-820` | `Entries`/`EntryCountText`, real. |
| Search `TextBox` | STUB | `MainWindow.axaml:828` | No `Text` binding at all — cosmetic watermark only. |
| All/Today filter segment | REAL | `MainWindow.axaml:831-833` | `ShowTodayOnly`, drives a real `IReceiveHistoryStore.QueryAsync` filter. |
| 14MHz / Unlogged / Flagged `ToggleButton`s | STUB | `MainWindow.axaml:835-837` | No binding — `ReceiveHistoryEntry` has no frequency/callsign/grid/flag field to filter by. |
| Sort combo (Newest/Callsign/SNR) | STUB | `MainWindow.axaml:838-842` | `SelectedIndex="0"` literal, no binding, no sort logic anywhere. |
| Refresh button | REAL | `MainWindow.axaml:843` | `RefreshCommand`. |
| Thumbnail grid (image + mode badge) | REAL | `MainWindow.axaml:848-883` | `Entries`, `Entry.ModeId`, `Thumbnail` — all real. |
| Thumbnail callsign/meta lines | FAKE-LIVE | `MainWindow.axaml:875-876` | `Panes.RxHistory.ThumbCallsignValue`/`ThumbMetaValue` literal loc keys. |
| Selected-frame preview image | REAL | `MainWindow.axaml:897-898` | `PreviewImage`, real, loaded per-selection. |
| File path | REAL | `MainWindow.axaml:902` | `SelectedEntry.Entry.FilePath`. |
| Mode | REAL | `MainWindow.axaml:911` | `SelectedEntry.Entry.ModeId`. |
| SNR suffix | FAKE-LIVE | `MainWindow.axaml:912` | `Panes.RxHistory.SnrSuffixValue` → `"· 21 dB"` literal — no per-frame SNR is tracked anywhere. |
| Frequency | FAKE-LIVE | `MainWindow.axaml:918` | `Panes.RxHistory.FreqPrefixValue` literal. |
| Time | REAL | `MainWindow.axaml:919` | `SelectedEntry.Entry.ReceivedAt`. |
| Grid/Distance | FAKE-LIVE | `MainWindow.axaml:923-924` | Literal loc key. |
| Log entry status | FAKE-LIVE | `MainWindow.axaml:927-928` | `Panes.RxHistory.LogEntryValue` literal — `LinkedQsoId` is never set non-null by any code path today. |
| Open in Log / Export frame / Re-decode buttons | STUB | `MainWindow.axaml:930-934` | No `Command` on any of the three. |
| Storage card: Folder | REAL | `MainWindow.axaml:942` | `ImagesDirectory`, real, resolved via `IReceiveHistoryStore.GetImagesDirectoryAsync`. |
| Storage card: Naming / Sidecar / Disk-free | FAKE-LIVE | `MainWindow.axaml:945-954` | All 3 literal loc-key values — neither naming scheme nor sidecar format nor disk-free is tracked anywhere. |

---

## Logbook tab

Grid: `MainWindow.axaml:961-1099`, `DataContext="{Binding Logbook}"` = `LogbookPaneViewModel.cs`. Fully real, no placeholders found — every field, filter, and command traces to a genuine `ILogbookSessionService` call (search, log, update, ADIF import/export). One line each, per the brief:

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
| Frequency readout (40pt) | REAL | `RadioHeaderView.axaml:59-63` | `FrequencyDisplay`, genuinely driven by `IRadioSessionService.StateChanges` (`RadioStatusViewModel.cs:118-132`). |
| USB/LSB/FM sideband `ToggleButton`s | STUB | `RadioHeaderView.axaml:76-78` | No binding — no sideband concept on `IRadioSessionService`; `IsChecked="True"` on USB is a static literal. |
| UTC clock | FAKE-LIVE | `RadioHeaderView.axaml:80-82` | `RadioStatus.UtcValue` → `"14:24:07Z"` literal, does not tick. |
| BW / Split pills | STUB | `RadioHeaderView.axaml:85-86` | Literal loc-key values, no such concept exists. |
| CAT link pill | REAL | `RadioHeaderView.axaml:88-95` | `CatLinked`, genuinely driven by `IRadioSessionService.ConnectionEvents` (`RadioStatusViewModel.cs:78-84,134-142`). |
| Step / RIT pills | STUB | `RadioHeaderView.axaml:97-99` | Literal loc-key values. |
| Rig-meters pill | STUB | `RadioHeaderView.axaml:101-103` | Literal loc-key value. |

**Favourites card**

| Control | Class | File:line | Note |
|---|---|---|---|
| Preset recall buttons | REAL | `RadioHeaderView.axaml:127-150` | `Presets`, genuinely calls `IRadioSessionService.SetFrequencyAsync`/`SetModeAsync` on click (`RadioStatusViewModel.cs:211-226`). |
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

**Menu bar** (`MainWindow.axaml:40-83`) — only `File > Open image` (`TxControls.SelectImageCommand`) and `File > Exit` (`ExitCommand`) plus the `Options...` item (`OpenOptionsCommand`) are REAL. Every other menu item across Configurations/Rig & PTT/Calibration/Tools is STUB (`IsEnabled="False"`, `Options.NotImplemented.Help` tooltip). `Help` menu has no submenu items at all (`MainWindow.axaml:70`). Callsign chip is REAL (`CallsignDisplay`, loaded from `OptionsSettingsService`, `MainViewModel.cs:81-86,112-123`).

**Status bar** (`MainWindow.axaml:1118-1142`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Frames today / Log size | FAKE-LIVE | `MainWindow.axaml:1121-1122` | Literal loc-key values (`"frames today 26"`, `"log 4,412 entries"`). |
| Receiving pill | REAL | `MainWindow.axaml:1125-1127` | `RadioStatus.IsReceiving`. |
| TX-inhibit LED | REAL | `MainWindow.axaml:1129-1131` | `TxControls.ErrorMessage != null`, genuinely lights on an SWR-cutoff/transmit error; the comment is explicit that this app has no persistent lockout state beyond that, so the LED's "meaning" is real but narrower than the label implies. |
| Frequency / Mode | REAL | `MainWindow.axaml:1132-1133` | `RadioStatus.FrequencyDisplay`/`ModeDisplay`. |
| Memory tag | FAKE-LIVE | `MainWindow.axaml:1134` | Literal loc key. |
| Detected mode | REAL | `MainWindow.axaml:1135` | `RxImage.DetectedModeText`. |
| Line progress, SNR, Slant, Buffer | FAKE-LIVE | `MainWindow.axaml:1136-1139` | All literal loc-key values — confirmed in `en.json`: `"line 168 / 256"`, `"SNR 21.6 dB"`, `"slant +3.4 ppm"`, `"buffer 512 · 0 XRUN"`. This is the exact pair (SNR/slant) named in the task prompt as the motivating example. |

---

## Options window (`OptionsWindowView.axaml`, `OptionsWindowViewModel.cs`)

Window-level Save/Cancel/Reset-ALL machinery is REAL throughout (`SaveCommand` persists via `OptionsSettingsService`, `CancelCommand` discards, per-section Reset commands and the confirm-gated `RequestResetAllCommand`/`ConfirmResetAllCommand` all genuinely work). Per-tab breakdown:

### General tab

| Control | Class | File:line | Note |
|---|---|---|---|
| Language `ComboBox` | REAL | `OptionsWindowView.axaml:24-32` | `AvailableCultures`/`SelectedCulture`, genuinely calls `ILocalizationService.SetCultureAsync` on Save. |
| Remember-window-position checkbox | STUB | `OptionsWindowView.axaml:40-42` | `IsEnabled="False"` — no window-geometry persistence exists. |
| JPEG quality `NumericUpDown` | STUB | `OptionsWindowView.axaml:47-49` | `IsEnabled="False"`, `Value="85"` literal. |
| 7 waterfall/spectrum color buttons (Low/High/FFT-BG/FFT-Signal/FFT-History/FFT-Sync/FFT-Freq) | STUB | `OptionsWindowView.axaml:63-69` | All `IsEnabled="False"` — the new flat-design palette isn't per-element customizable yet. |
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
| Callsign / Operator name / Operator grid | REAL | `OptionsWindowView.axaml:248,258,260` | All 3 persisted; these back the TX macro/overlay chips (`Options.Tx.OperatorName`/`OperatorGrid` — added in commit `8556738`, the "operator-profile setting" mentioned in recent commits). |
| QRZ lookup checkbox | STUB | `OptionsWindowView.axaml:270-271` | `IsEnabled="False"` — legacy hardcodes a personal API password for this feature, deliberately not resurrected. |
| Reset section | REAL | `OptionsWindowView.axaml:276` | `ResetTxToDefaultCommand`. |

### Decode tab — fully STUB

Sense level, RX BPF, Demod type, RX buffer, Auto-start, Auto-stop, Auto-restart, Auto-sync,
Auto-slant — every single control (`OptionsWindowView.axaml:287-343`) is `IsEnabled="False"`.
`AnalogFmSstvDecoder` hardcodes all of this internally with no public knob, per the file's own
comment (worth noting: commits `8556738`/`cda094d`/`4ecfea6` recently ported real Auto-Sync/
Auto-Stop/operator-profile engines — this tab's toggles for those may now be low-hanging fruit to
wire up, since the underlying logic may already exist even though this survey found no binding
here).

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
buttons, sliders, etc. — decorative structural elements like card headers/captions not counted):

| Classification | Approx. count | Notes |
|---|---|---|
| REAL | ~94 | Concentrated in Logbook (100%), TxControls output/transmit/stock/favorites, TX image editor's crop/overlay/preview machinery, Options General/Audio/Radio/Tx tabs, RadioStatus VFO/CAT-link/Receiving/TX-volume, Gallery's filter/list/storage core. |
| STUB (`IsEnabled="False"` or no-`Command`) | ~110 | Concentrated in: Options Decode/Identification/Advanced tabs (fully stub, ~55 controls alone), Menu bar (Configurations/Rig&PTT/Calibration/Tools, ~16 items), Receive tab's Decode-activity/Session-frames/Unattended-RX cards, TX image editor's tool strip and 7 of 12 insert-field chips. |
| FAKE-LIVE (literal masquerading as data) | ~71 | Concentrated in: Receive tab's Sync&Slant/Input-chain/Signal-quality cards (nearly every value row), status bar (SNR/Slant/Buffer/Frames-today/Log-size — the task's own motivating example), Frame-metadata card, RadioHeaderView's UTC clock/RX-level/BW·Split/Step·RIT/rig-meters pills, Gallery's per-entry SNR/frequency/grid-dist/log-status, TX image editor's canvas-overlay safe-area/callsign/report plate text (auditor-caught, arguably the most deceptive one in the app — reads as real burned-in TX content). |
| PARTIAL | ~5 | TxControls Output-device row (real `OutputDeviceName` property exists but the View reads a static loc key instead); RxFrameMeta Note/Override-callsign `TextBox`es (editable-looking but bound to static loc keys via a get-only `{loc:Translate}` binding, not two-way VM properties — logs a binding error per keystroke); RadioHeaderView "Store current" (real save-preset path exists on the VM but this button isn't wired to it); Receive tab's Previous-frames strip (auditor-caught reclassification from REAL — genuinely populated from a real store, but the VM subscribes to no live session event, so it never updates during an actual receive session, only on construction/manual refresh/filter change). |

**Fully real screens:** Logbook tab (100%). Mode-timing-reference card (Transmit tab). Menu bar's
File>Open/Exit + Options item.

**Mostly real, mixed:** Transmit tab's left column (TxControlsPaneView — mode/output/stock/
transmit machinery real, Identification/Outgoing-metadata cards fully stub). TX image editor
(crop/overlay/preview real, tool strip and half the insert-field chips decorative). Gallery tab
(list/filter/storage real, several per-entry display fields FAKE-LIVE). Radio header (frequency/
CAT-link/receiving/TX-volume real, sideband/BW/step/RIT/rig-meters/UTC-clock decorative). Options
window General/Audio/Radio/Tx tabs (each has a real core plus several STUB rows).

**Mostly/fully stub:** Receive tab's Sync&Slant, Input-chain, Signal-quality, Frame-metadata,
Unattended-RX, and Session-frames cards (all six are FAKE-LIVE/STUB almost top to bottom — this is
the single densest concentration of placeholder UI in the app). Options window's Decode,
Identification, and Advanced tabs (100% stub, ~55 controls). Transmit tab's right-column Queue/
TX-log/Recently-sent cards (100% stub). MainWindow menu bar outside File/Options.

**Most important single finding:** the status bar's `SNR 21.6 dB` / `slant +3.4 ppm` readouts
(`MainWindow.axaml:1137-1138`) are literal strings sitting in `assets/locale/en.json`, not computed
anywhere — exactly the pattern named in the task brief, confirmed by reading the locale file
directly. The same literal-value pattern repeats at least a dozen more times across the Receive
tab's Sync&Slant/Input-chain/Signal-quality/Frame-metadata cards and Gallery's per-entry SNR field,
all sourced from the same `en.json`. Anyone skimming the running app would see plausible-looking
numeric readouts in all of these places and reasonably assume they're live.

**Off-scope note (auditor-caught while verifying, not chased further here):**
`RxHistoryPaneViewModel.cs:69-73` builds its `EntryCountText` ("1 frame"/"N frames") as hardcoded
English string interpolation, not through `ILocalizationService` — a real, small, separate
violation of the no-hardcoded-UI-strings rule, unrelated to this survey's own accuracy. Worth a
one-line fix whenever that pane is touched next.
