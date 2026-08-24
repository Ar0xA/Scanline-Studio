# 16 — GUI Wiring Survey

**Date:** 2026-08-22 — full re-derivation from source. Every classification and every line citation
below was re-verified against the current `.axaml`/`.axaml.cs`/ViewModel files; **nothing was carried
forward from the previous revision of this document.** The prior revision (dated 2026-08-09 …
2026-08-18) had gone comprehensively stale: it described a `Dock.Avalonia` dockable-pane shell that no
longer exists, an 8-tab Options window that now has 9, and ~13 controls it classified STUB/FAKE-LIVE
that have since shipped as real. Its narrative "Refresh (…)" sections and rolled-forward summary
counts are not reproduced here — they documented a codebase that no longer matches.

**Scope:** every screen/card/control in `ScanlineStudio.UI`:

| Surface | File |
|---|---|
| Shell (menu bar, tab strip chips, status bar, 4 tabs) | `src/ScanlineStudio.UI/Views/MainWindow.axaml` (1539 lines) |
| Radio header (VFO / Favourites / Transceiver) | `src/ScanlineStudio.UI/Views/RadioHeaderView.axaml` (376) |
| Transmit left column | `src/ScanlineStudio.UI/Views/TxControlsPaneView.axaml` (436) |
| TX image editor (Transmit centre) | `src/ScanlineStudio.UI/Views/TxImageEditorPaneView.axaml` (2015) |
| Options window (9 tabs) | `src/ScanlineStudio.UI/Views/OptionsWindowView.axaml` (752) |
| About dialog | `src/ScanlineStudio.UI/Views/AboutWindowView.axaml` (52) |
| QSO-link dialog | `src/ScanlineStudio.UI/Views/QsoLinkWindowView.axaml` (98) |
| RX frame pane / waterfall pane | `RxImagePaneView.axaml` (31), `WaterfallPaneView.axaml` (13) |

**Shell shape (current, verified):** `MainWindow.axaml:47` is a `Grid RowDefinitions="26,102,*,25"` —
menu row, always-visible `RadioHeaderView` band, tab area, status bar. The tab area
(`MainWindow.axaml:152`) is a plain `TabControl` bound to `MainViewModel.SelectedTabIndex`
(`MainViewModel.cs:37`) with four fixed tabs in source order **Receive=0, Transmit=1, Gallery=2,
Logbook=3** (`MainViewModel.LogbookTabIndex = 3`, `MainViewModel.cs:22`). There are no dockable
panes, no dock factory, and no `Dock.Avalonia` package reference (`ScanlineStudio.UI.csproj` lists
only Avalonia, Avalonia.Desktop, Avalonia.Diagnostics, Avalonia.Themes.Fluent,
Avalonia.Controls.ColorPicker, CommunityToolkit.Mvvm and two Microsoft.Extensions packages). **RX
history lives in the Gallery tab** (`MainWindow.axaml:937-1182`, `DataContext="{Binding RxHistory}"`),
with the same `RxHistoryPaneViewModel` instance also feeding the Receive tab's Previous-frames strip.

---

## Methodology (revised — the old one no longer works)

Every interactive/data-displaying control was traced from its `.axaml` binding back to the backing
ViewModel property/command, and from there to whatever real service it does or doesn't call.

**Why the previous methodology is retired.** The prior revision's central signal for its most
important category, FAKE-LIVE, was "a hardcoded literal (either in the `.axaml`, e.g. `Value="3.400"`,
or a `{loc:Translate …}` key whose locale value is a static string, e.g. `"21.6 dB · +3.4"`)". **That
heuristic no longer discriminates.** A "0.9-beta UI-honesty pass" (visible in this codebase's own
XAML comments, e.g. `TxControlsPaneView.axaml:38-43`, `MainWindow.axaml:745-752`,
`MainWindow.axaml:915-920`) went through the app and:

- replaced essentially every deceptive numeric/textual literal with an em-dash `"—"` in
  `assets/locale/en.json` — e.g. `MainWindow.StatusBar.SnrValue` is now `"—"` (`en.json:35`), not the
  `"SNR 21.6 dB"` the old survey led with as its "most important single finding";
  `Panes.RxFrameMeta.SnrSlantValue` is `"—"` (`en.json:224`); the whole `Panes.TxOutgoing.*` block was
  `"—"` (formerly `en.json:357-372`) — **removed entirely 2026-08-23** (user-reported: the "BURNED
  INTO THE PICTURE" card did nothing), not just dashed out;
- collapsed fabricated populated lists into honest empty states (the 45-`TextBlock` Session-frames
  list → one `Panes.RxSessionFrames.NoSessionYet` line, `MainWindow.axaml:757`; the TX Queue's two
  invented filenames → `Panes.TxQueue.NoQueueYet`, `:832`; Recently-sent's three fake thumbnails →
  `Panes.TxRecentlySent.NoneYet`, `:930`);
- disabled and tooltipped previously-clickable dead controls (`RadioHeaderView.axaml:70-71` sideband
  group, `:98-100` BW/Split, `:127-129` Step/RIT, `:242-243` Edit-list/Import/Scan;
  `MainWindow.axaml:1419-1421` the AUTOSAVE chip).

So "is this a literal?" now mostly finds *honest* placeholders. This survey therefore classifies by
**whether a real backing property/command exists**, and splits the old FAKE-LIVE bucket in two:

- **REAL** — bound to a ViewModel property/command genuinely backed by working logic.
- **PARTIAL** — real wiring, but incomplete (half the row real, half hardcoded; writes without
  persisting; snapshot that never refreshes).
- **PLACEHOLDER** — no backing data, and the UI *says so*: an em-dash value, a "nothing yet" empty
  state, or a disabled control with the `Options.NotImplemented.Help` tooltip. Not deceptive.
- **STUB** — no binding/command at all. Two sub-kinds, and the distinction matters:
  *disabled stub* (`IsEnabled="False"` + tooltip — honest) vs. **dead-interactive stub** (still
  clickable/typeable, does nothing). Dead-interactive stubs are flagged explicitly; they are the
  remaining honesty gap.
- **FAKE-LIVE** — a static value that still reads as real data. **Now rare** (4 sites, listed in the
  Summary). This stays the most important category to watch, just no longer the largest.

Fully-wired screens (Logbook tab, TX image editor) get grouped coverage rather than one row per
control, with the verification that justifies the grouping stated inline.

---

## Receive tab

`MainWindow.axaml:153-761`. Three columns (`:156`, `ColumnDefinitions="236,*,312"`) — Mode/Sync/
Input/Signal cards (left), Waterfall + Incoming-frame + Decode-activity (centre), Frame-metadata/
Unattended-RX/Session-frames (right). Left, centre-top and right columns bind `DataContext="{Binding
RxImage}"` per card; the Incoming-frame card's own action row deliberately stays on `MainViewModel`
and qualifies each binding (`MainWindow.axaml:486-490`).

### Left column

**Mode card** (`MainWindow.axaml:181-233`, `DataContext="{Binding RxImage}"` at `:181`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Auto / Lock segment | PLACEHOLDER + STUB | `:197-198` | "Auto" carries a static `IsChecked="True"` with no binding — accurate, since auto-detect from VIS is the only behaviour that exists. "Lock" is `IsEnabled="False"` with an honest tooltip (`Panes.RxImage.LockedMode.Help` = *"No persistent mode lock exists -- use a quick-mode button below to force the next decode into a specific mode."*, `en.json:95`). |
| Active-mode `ComboBox` | REAL | `:200-202` | Single `ComboBoxItem` bound to `DetectedModeDisplay` (`RxImagePaneViewModel.cs:351`), driven by `ISstvSessionService.ModeDetected`. Not genuinely selectable (one item); the tooltip `Panes.RxImage.ModeOverride.Help` (`en.json:94`) says so. |
| Quick-mode pill grid (16 buttons, SC1…SC2180) | REAL | `:209-226` | Each `Command="{Binding QuickSelectModeCommand}"` (`RxImagePaneViewModel.cs:568`) with a real `SstvModeDefinition.Id` as `CommandParameter` (`scottie-s1`, `martin-m1`, `r24`, `pd240`, …). Forces the *next* decode's mode; not a persistent lock. |
| Line time | REAL | `:228` | `LineTimeText` (`.cs:353`), derived from the real `DetectedMode`. |
| Lines | REAL | `:229` | `LinesText` (`.cs:355`). |
| Remaining | PLACEHOLDER | `:230` | `Panes.RxImage.RemainingValue` = `"—"` (`en.json:93`). No remaining-time computation exists. |

**Sync & slant card** (`MainWindow.axaml:238-278`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Source | PLACEHOLDER | `:244` | `Panes.RxSync.SourceValue` = `"—"` (`en.json:143`). No product decision on what "Source" means here. |
| Slant ppm | REAL | `:245` | `SlantPpmDisplay` (`RxImagePaneViewModel.cs:392`) — read-only formatted readout, not an editable stepper. |
| Sync offset | REAL | `:246` | `SyncOffsetSamplesDisplay` (`.cs:405`). |
| Auto-correct | REAL | `:247` | `AutoCorrectDisplay` (`.cs:443`), 4-way AVT/Off/Locked/on-not-locked readout gated by `SstvDecoderSettings.AutoSlantEnabled`. |
| Re-sync button | REAL | `:266` | `RequestReSyncCommand` (`.cs:535-536`) → `ISstvSessionService.RequestReSync`. |
| Correct slant button | REAL | `:267` | `RequestCorrectSlantCommand` (`.cs:538-539`) — port of legacy's `KRCS` popup item, sibling to Re-sync. |
| Reset button | STUB (disabled) | `:268` | `IsEnabled="False"`, `Options.NotImplemented.Help`. No legacy-verified semantics decided; the file's own comment (`:249-256`) explicitly warns against guessing a mapping. |
| Advanced timing disclosure toggle | STUB (disabled) | `:270` | `IsEnabled="False"` + tooltip. |
| — Sample clock / Sync window / VIS threshold / Drop-line | **unreachable** | `:272-275` | Gated `IsVisible="{Binding #AdvancedTimingToggle.IsChecked}"` on a permanently-disabled toggle, so they can never render. Their loc values are still real-looking literals — `"47 999.66 Hz"` / `"±4.8 ms"` / `"−26 dB"` / `"interpolate"` (`en.json:144-147`) — a **latent FAKE-LIVE** the moment that toggle is ever enabled. Flagged in the Summary. |

**Input chain card** (`MainWindow.axaml:284-324`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Device | REAL | `:290` | `CaptureDeviceNameDisplay` (`.cs:179`), `CaptureDeviceName ?? "—"`. |
| Squelch | PLACEHOLDER | `:291` | `"—"` (`en.json:166`). |
| BPF | PLACEHOLDER | `:292` | `"—"` (`en.json:167`). **Nit:** RX BPF sharpness *is* a real, wired setting now (Options → Decode), but this readout is not bound to it — a cheap follow-up, not a hard gap. |
| Notch | PLACEHOLDER | `:293` | `"—"` (`en.json:168`). No notch filter exists in `Core.Sstv`. |
| AGC | REAL | `:294` | `AgcGainDisplay` (`.cs:525`). |
| Buffer | REAL | `:295` | `BufferedSampleCountDisplay` (`.cs:505`), "N samples · M XRUN". |
| Clipping | REAL | `:296` | `ClippingDisplay` (`.cs:513`). |
| Noise floor | PLACEHOLDER | `:297` | `"—"` (`en.json:173`). |
| Level L / Level R values | PLACEHOLDER | `:306`, `:316` | `"—"` (`en.json:164-165`). |
| Level L / Level R meters | PLACEHOLDER | `:312-315`, `:317-320` | Fill columns hardwired `0*,100*` (empty track), deliberately *not* a fake percentage — this port's demod path is mono-only. |

**Signal quality card** (`MainWindow.axaml:330-347`)

| Control | Class | File:line | Note |
|---|---|---|---|
| SNR-per-line plot | STUB (empty) | `:335-336` | Kicker label + empty `Border Classes="IndustryHatchPanel"`. No per-line SNR exists anywhere in the decode pipeline. |
| Min/Max | PLACEHOLDER | `:337` | `"—"` (`en.json:182`). |
| Luminance histogram plot | STUB (empty) | `:338-339` | Same empty-hatch pattern. |
| Clip Lo/Hi | REAL | `:341` | `ClipLoHiDisplay` (`.cs:372`) — image-domain pixel-luminance statistic, not audio DSP. |
| Sync tone | REAL | `:342` | `SyncToneDisplay` (`.cs:495`). |
| Black tone / White tone | PLACEHOLDER | `:343-344` | `"—"` (`en.json:185-186`). |

### Centre column

**Spectrum & waterfall card** (`MainWindow.axaml:382-459`) — **fully real.**

| Control | Class | File:line | Note |
|---|---|---|---|
| Range caption | REAL | `:387-389` | `Waterfall.RangeCaptionDisplay` (`WaterfallPaneViewModel.cs:91`). |
| Spectrum trace | REAL | `:396-405` | `controls:SpectrumTraceControl`, fed `Waterfall.LatestFrame`/`ZeroDb`/`GainDb`/`StartHz`/`SpanHz`/`PeakHoldEnabled`/`CurrentMode`. |
| Waterfall plot | REAL | `:406` | `ContentControl Content="{Binding Waterfall}"` → `WaterfallPaneView.axaml:9-10` → `controls:WaterfallControl`. |
| Bins/px `NumericUpDown` | REAL (read-only) | `:415` | `IsEnabled="False"` computed telemetry readout; the value is pushed *out* of `SpectrumTraceControl` via `BinsPerPixel="{Binding …, Mode=OneWayToSource}"` (`:404`). Disabled by design, not a stub. |
| Start / Span steppers | REAL | `:419`, `:423` | `Waterfall.StartHz` / `SpanHz` (`WaterfallPaneViewModel.cs:49,53`). |
| Gain / Zero sliders | REAL | `:438`, `:440` | `Waterfall.GainDb` / `ZeroDb` (`.cs:40,37`); `Minimum`/`Maximum` are load-bearing (see the `:425-435` comment). |
| Peak hold toggle | REAL | `:450` | `Waterfall.PeakHoldEnabled` (`.cs:68`). |
| Both / Spec / WF segment | REAL | `:452-454` | `IsViewBoth` / `IsViewSpectrumOnly` / `IsViewWaterfallOnly` (`.cs:105,117,129`), driving `ColumnDefinition.Width` via `WaterfallViewModeToColumnWidthConverter` (`:392-393`). |

**Incoming frame card** (`MainWindow.axaml:464-587`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Save frame | REAL | `:491` | `RxImage.SaveFrameCommand` (`RxImagePaneViewModel.cs:1179`), `CanSaveFrame` = a mode has been detected and no save in flight (`.cs:1177`). Real file-picker round-trip. |
| Abort | STUB (disabled) | `:492` | No command on `RxImagePaneViewModel`; disabled + tooltip rather than clickable-but-dead. |
| Re-decode | STUB (disabled) | `:493` | Same. Likely permanently infeasible — only the final image is retained, no raw audio. |
| Copy to TX | REAL | `:499` | `TxControls.CopyReceivedImageToTxCommand`, `IsEnabled="{Binding TxControls.CanChangeSourceOrMode}"` (`TxControlsPaneViewModel.cs:775`). |
| Log QSO | REAL | `:500` | `RxImage.LogQsoCommand` (`.cs:1160`), `CanLogQso` = `DetectedMode is not null` (`.cs:1158`); fires `LogQsoRequested` → `MainWindow.axaml.cs:235-249` → `LogbookPaneViewModel.PrefillForNewEntry` (`LogbookPaneViewModel.cs:299`) + `SelectedTabIndex = LogbookTabIndex`. |
| Line count | REAL | `:502` | `RxImage.LineProgressText` (`.cs:361`). |
| Progress bar | REAL | `:503` | `RxImage.Progress` (`.cs:128`), `Minimum=0 Maximum=1`. |
| Save-frame error banner | REAL | `:508-511` | `SaveFrameErrorMessage` (`.cs:1171`), `IsNotNull`-gated. |
| Received image | REAL | `:530-534` → `RxImagePaneView.axaml:17-29` | `Image Source="{Binding Image}"` + a real `NoImageYet` empty state; fed by `IReceivedImageBuffer.Updated` coalesced onto the UI thread. |
| Previous-frames strip | REAL | `:554-581` | `ItemsSource="{Binding RxHistory.Entries}"` (`RxHistoryPaneViewModel.cs:249`), live-updating via `IReceiveHistoryStore.Recorded`. Empty state at `:543-545`. |
| — thumbnail callsign | PLACEHOLDER | `:566` | `Panes.RxHistory.ThumbCallsignValue` = `"—"` (`en.json:191`) — `ReceiveHistoryEntry` has no callsign field. |
| — thumbnail timestamp | REAL | `:567` | `Entry.ReceivedAt`. |
| — thumbnail mode badge | REAL | `:574` | `Entry.ModeId`. |

**Decode activity card** (`MainWindow.axaml:597-640`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Decode-events table (UTC/Freq/Mode/Callsign/Grid/SNR/Slant/Lines/State) | STUB (header-only) | `:615-625` | Nine header cells, no `ItemsSource` anywhere. No structured decode-event log exists, distinct from `ReceiveHistoryStore`. Plain `Grid`/`IndustryTableHeaderRule` atoms — the `Avalonia.Controls.DataGrid` package is gone. |
| Trace panel `ItemsControl` | STUB (empty) | `:635` | Literally `<ItemsControl />`, no `ItemsSource`. |

### Right column

**Frame metadata card** (`MainWindow.axaml:667-731`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign | REAL | `:680` | `CallsignDisplay` (`.cs:276`) — mirrors `OverrideCallsign`, which is auto-filled by the real FSK station-ID decode path (`OnStationIdDecoded`, `.cs:735`). The old "· OCR" label suffix was dropped; the value no longer claims an OCR source. |
| Name | REAL | `:685` | `NameDisplay` (`.cs:297`), from the real QRZ lookup. |
| QTH | REAL | `:686` | `QthDisplay` (`.cs:303`). |
| Grid / dist | **PARTIAL** | `:687` | `GridDisplay` (`.cs:314`) is `$"{LookupGrid ?? "—"} / --"` — the grid half is real, the distance half is a hardcoded `"--"` inside the property itself. Needs the operator's own grid + a distance calc (a real `MaidenheadLocator` now exists in the TX editor's `{dist}`/`{bearing}` chips; wiring it here is unblocked). |
| Frequency | PLACEHOLDER | `:688` | `"—"` (`en.json:221`). |
| Mode / VIS | PLACEHOLDER | `:689` | `"—"` (`en.json:222`). |
| Started | REAL | `:690` | `StartedDisplay` (`.cs:379`). |
| SNR / slant | PLACEHOLDER | `:691` | `"—"` (`en.json:224`). |
| OCR confidence | PLACEHOLDER | `:692` | `"—"` (`en.json:225`). No OCR exists. |
| Dropped lines | PLACEHOLDER | `:693` | `"—"` (`en.json:226`). |
| File size | REAL | `:694` | `FileSizeDisplay` (`.cs:385`); only populates once the frame's own save completes, otherwise `"—"`. |
| Note `TextBox` | REAL | `:709` | `Text="{Binding Note}"` (`.cs:102`), `IsEnabled="{Binding CanEditFrameMetadata}"` (`.cs:95` — true once `_currentEntryId` is correlated via `OnHistoryRecorded`, `.cs:921`); debounce-persists through the real `IReceiveHistoryStore.SetNoteAsync` (`PersistNoteDebouncedAsync`, `.cs:963`). **This closes the previous revision's only remaining PARTIAL finding.** |
| Override callsign `TextBox` | REAL | `:711` | `Text="{Binding OverrideCallsign}"` (`.cs:270`). **Nit:** its `Watermark` is `Panes.RxFrameMeta.CallsignValue` = `"EA7KDT"` (`en.json:220`) — a real-looking callsign as watermark text; low severity (watermarks are visually distinct) but it is the last surviving instance of that literal. |
| Lookup QRZ button | REAL | `:713` | `LookupQrzCommand` (`.cs:1101`) → `ILogbookSessionService.LookupCallsignAsync` → real `IQrzCallsignLookup` HTTP round-trip. |
| Flag `ToggleButton` | REAL | `:720` | `IsChecked="{Binding IsFlagged}"` (`.cs:107`), same `CanEditFrameMetadata` gate; persists through `IReceiveHistoryStore.SetFlaggedAsync` (`PersistFlaggedAsync`, `.cs:1018`). |
| Frame-metadata error banner | REAL | `:722-725` | `FrameMetadataErrorMessage` (`.cs:116`). |
| QRZ-lookup error banner | REAL | `:726-729` | `QrzLookupErrorMessage` (`.cs:317`) — this is where the "QRZ disabled/unconfigured" case surfaces, rather than graying out the button. |

**Unattended RX card** (`MainWindow.axaml:733-743`) — Watch / Scan dwell / Alert / Session, all four
PLACEHOLDER `"—"` (`:738-741`, `en.json:238-241`). No scan/watch feature exists.

**Session frames card** (`MainWindow.axaml:753-758`) — PLACEHOLDER. One honest empty-state line
(`Panes.RxSessionFrames.NoSessionYet` = *"No frames received this session"*, `en.json:243`). The prior
revision's "15 hand-written literal rows / 45 `TextBlock`s" is gone — collapsed by the UI-honesty pass
(see the file's own comment at `:745-752`).

---

## Transmit tab

`MainWindow.axaml:762-936`, `ColumnDefinitions="236,*,312"` (`:766`). Left = `TxControlsPaneView`
(`:778`), centre = `ActiveEditor` (`:783`, a nullable `TxImageEditorPaneViewModel` — but see below:
the editor is now auto-opened at startup, so the centre column is no longer empty on first landing),
right = Queue/Mode-timing/TX-log/Recently-sent.

### Left column — `TxControlsPaneView.axaml` / `TxControlsPaneViewModel.cs`

**TX mode card** (`:26-143`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Auto / Manual segment | REAL | `:35-36` | `AutoFollowRxMode` (`TxControlsPaneViewModel.cs:217`), persisted to `TxPaneUiSettings`. |
| "Auto picks" caption value | PLACEHOLDER | `:46` | `"—"` (`en.json:323`). |
| Quick-mode pill grid (16 buttons) | REAL | `:59-76` | `QuickSelectModeCommand` (`.cs:808`), real mode ids as `CommandParameter`; sets `SelectedMode` on this pane (distinct from the RX tab's grid, which calls `ForceMode`). |
| Mode `ComboBox` | REAL | `:77-86` | `AvailableModes` / `SelectedMode` (`.cs:163`), `IsEnabled="{Binding CanChangeSourceOrMode}"` (`.cs:775`). Genuinely drives the encode pipeline. |
| Selected | REAL | `:93` | `SelectedMode.DisplayName`. |
| Duration / Geometry / VOX tone / VIS header | PLACEHOLDER | `:97`, `:101`, `:105`, `:109` | All `"—"` (`en.json:327,329,331,333`). |
| Favorites row | REAL | `:113-128` | `FavoriteModes` (`.cs:454`), each with a real `SelectCommand`. |
| "Edit favorites…" flyout | REAL | `:129-141` | `FavoriteModeOptions` (`.cs:444`) `CheckBox` list; persisted as `TxPaneUiSettings.FavoriteModeIds` (`.cs:543`). |

**Identification card** (`:149-167`) — **REAL, with one caveat.**

| Control | Class | File:line | Note |
|---|---|---|---|
| FSK ID | REAL (snapshot) | `:156` | `FskIdDisplay` (`.cs:322`). |
| CW ID | REAL (snapshot) | `:160` | `CwIdDisplay` (`.cs:327`). |
| Tail | REAL (snapshot) | `:164` | `TailDisplay` (`.cs:337`). |

All three reflect the current effective Options configuration via
`ISstvSessionService.GetStationIdTransmitOptionsAsync`, **loaded once at construction and not
re-read while the pane stays open** (the file's own comment, `:145-148`). Changing Identification
settings in Options and returning here shows stale values until restart — arguably PARTIAL; recorded
as REAL-with-caveat because the value it shows was genuinely correct when read.

**Output card** (`:177-265`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Drive slider + value | REAL | `:184-189` | `RadioStatus.TxVolumePercent` (`RadioStatusViewModel.cs:72`), debounce-persisted via `ISstvSessionService.SetTxVolumePercentAsync` (`.cs:725`). |
| Output device | REAL | `:194` | `OutputDeviceNameDisplay` (`TxControlsPaneViewModel.cs:286`), `OutputDeviceName ?? "—"`. |
| Power / ALC / SWR rows | REAL (capability-gated) | `:196-207` | `LivePowerPercent` / `LiveAlcLevel` / `LiveSwrRatio`, each row `IsVisible`-gated on the real `ShowPowerMeter`/`ShowAlcMeter`/`ShowSwrMeter` capability flags (`.cs:652-654`). Rows vanish rather than showing a placeholder on an unsupported rig. |
| SWR auto-cutoff toggle | REAL | `:220-224` | `SwrCutoffEnabled` (`.cs:263`), `IsEnabled="{Binding ShowSwrMeter}"`. |
| SWR threshold | REAL | `:226` | `SwrCutoffThreshold` (`.cs:266`). |
| Tune drive | PLACEHOLDER | `:243` | `"—"` (`en.json:335`). |
| Tone map | REAL | `:247` | `ToneMapText` (`.cs:174`), static per-mode `SstvModeDefinition.LuminanceMinHz/MaxHz`. |
| TX clock / Monitor audio / Occupied BW | PLACEHOLDER | `:251`, `:255`, `:259` | All `"—"` (`en.json:348,350,352`). Occupied BW was researched and deliberately deferred, per the file's own comment (`:229-236`). |
| Meter plot | STUB (empty) | `:262-263` | Kicker + empty `Border Classes="IndustryHatchPanel"`. |

**Stock / browse card** (`:269-345`) — fully REAL: `StockEntries` `ListBox` (`:294-316`, `.cs:439`,
`IsEnabled="{Binding CanChangeSourceOrMode}"`), Browse (`:317-319`, `SelectImageCommand`, `.cs:885`),
"Open blank editor" (`:326-328`, `OpenBlankEditorCommand`), selected filename (`:329`), preview image
(`:331`), "Edit image" (`:340-343`, `EditCurrentImageCommand`, `.cs:1226`).

**Error banner** (`:347-350`) — REAL, `ErrorMessage` (`.cs:214`).

**Transmit / Stop TX** (`:353-358`) — REAL: `TransmitCommand` (`.cs:1302`) with `IsChecked="{Binding
IsTransmitting, Mode=OneWay}"`, `StopTransmitCommand` (`.cs:1366`); real SWR-cutoff cancellation
wiring behind them.

**TX progress** (`:371-375`) — REAL: `ProgressBar` bound to `TransmitProgress` (`.cs:195`) +
`TransmitProgressText` (`.cs:205`), the whole block `IsVisible`-gated on `TransmitProgress` being
non-null.

**Outgoing metadata card** (`:384-432`) — PLACEHOLDER throughout. VIS code, FSK ID, CW ID, Callsign,
To-station, Grid/Beam, Report, Freq/Mode, Date UTC (`:393,397,401,408,412,416,420,424,428`) are all
`"—"` (`en.json:357-372`). Note the card's *own* FSK/CW rows are placeholders even though the
Identification card above knows the real values — blocked on a "to station"/report/QSO-context concept
that doesn't exist. The file's own comment (`:377-383`) still cites the operator-profile gap as a
blocker; **that half is stale** — `OperatorSettings` name/grid are real (Options → TX). The live
blocker is QSO context only.

### Centre column — TX image editor (`TxImageEditorPaneView.axaml`, 2015 lines)

**Verified as containing zero unbacked controls.** `IsEnabled="False"` and `NotImplemented` appear
**only inside comments** in this file (grep hits at `:104`, `:107`, `:108` are all comment prose
describing removed stubs) — there is no disabled placeholder, no dead button, and no unbound literal
value row anywhere in it. Given ~130 individually-bound controls and uniform classification, this
section is grouped by panel rather than one row per control; every group below was confirmed against
a real command/property.

| Region | Class | File:line | Backing |
|---|---|---|---|
| Status/error banner | REAL | `:60-63` | `StatusMessage`, doubles as the Cancel/Recall arm-confirm surface. |
| Context bar: frame readout, unsaved-edits chip, Undo, Redo, Revert | REAL | `:65`, `:81-87`, `:89-92` | `FrameReadoutText`, `HasUnsavedEdits`, `UndoCommand`/`RedoCommand`/`RevertCommand` (CanExecute-gated, not `IsEnabled="False"`). |
| Stage toolbar: Undo, Redo, Fit, Fit-modes flyout (Safe area/Width/Height), 100%, zoom slider + %, Snap grid, Safe area, crop dimensions chip, Preserve aspect, Lock aspect to mode, Rotate | REAL | `:129-130`, `:143`, `:150-160`, `:161`, `:176-182`, `:184`, `:185`, `:201`, `:207-209`, `:217-220`, `:237-239` | Fit variants use `Click` handlers (they need the live `ScrollViewer` viewport — see `:131-135`); everything else is a `Command`/`IsChecked` binding. `SnapToGrid`, `SafeAreaVisible`, `PreserveAspect`, `LockAspectToMode`, `ZoomFactor` (slider bounds bound via `x:Static` to the VM's own `MinZoomFactor`/`MaxZoomFactor` consts, `:178-179`), `RotateCommand`, `ZoomActualCommand`, `DimensionsChipText`. |
| READY RACK (9 numbered slots + empty-slot hint) | REAL | `:262-364` | `RecallCommand` + `SlotNumber` (`:315`); empty slots render a dashed placeholder + `PinFromLibraryHint` (`:331`, `:346`). D1–D9 key accelerators in `TxImageEditorPaneView.axaml.cs`. |
| TEMPLATE LIBRARY: count, filter box, list, Load, Pin, Delete (arm/confirm), name box, Save | REAL | `:366-423` | `ReadyRack.TemplateCount`/`LibraryFilterText`/`FilteredTemplates`/`HasNoTemplates`/`HasNoFilteredTemplates`; per-row `LoadCommand`/`TogglePinCommand` (`CanPin`-gated)/`DeleteCommand` (`IsPendingDelete` arm-confirm); `NewTemplateName` + `SaveTemplateCommand`. Backed by a real, tested `ITemplateStore`. |
| Canvas: crop rect (drag/8-handle resize), draggable elements, selection readout, frame border, crop/working-copy footers | REAL | `:434-1203` | Pointer handlers in `TxImageEditorPaneView.axaml.cs` (807 lines, incl. the `ResizeHandle` enum); `SelectionReadoutText`, `CropFooterText`, `WorkingCopyFooterText`, `CanvasDisplayWidth/Height`. Right-click context menus on text/box/image elements (`:747-798`, `:964-977`, `:1076-1090`) are all real commands. |
| PREVIEW panel | REAL | `:1229-1241` | `PreviewImage` + `PreviewMaxHeight`, nearest-neighbour so real SSTV resolution is visible. |
| ELEMENTS panel: Front/Back/Duplicate/Remove, +Text, +Image flyout (from file / from clipboard / last RX image / RX-history picker), +Box, per-element rows | REAL | `:1274-1490` | `BringToFrontCommand`, `SendToBackCommand`, `DuplicateCommand`, `RemoveOverlayElementCommand`, `AddOverlayElementCommand`, `AddImageFromFileCommand`, `AddImageFromClipboardCommand`, `AddLastRxImageCommand`, `RefreshRxHistoryPickerCommand`, `AddBoxElementCommand`. |
| Inspector tab strip (TEXT / GEOMETRY / IMAGE) | REAL | `:1530-1540` | Three `IsTextStyleTabSelected`/`IsGeometryTabSelected`/`IsImageTabSelected` bools; `IndustrySeg` radio group, not a `TabControl`. |
| TEXT tab: font family + unavailable warning, Bold, Italic, size (px), fill `ColorPicker`, outline enable/color/thickness, shadow (enable/color/offset X/Y), 3-D stack step X/Y, rotation, gradient kind/start/end, **12** insert-field chips incl. `{dist}`/`{bearing}`, "add plate behind text" | REAL | `:1542-1731` | `SelectedTextElement.*` plus parent-VM px-conversion properties (`SelectedTextElementFontSizePx`, `SelectedTextElementStrokeThicknessPx`); `InsertFieldCommand` with real macro tokens (`{his_call}`, `{his_grid}`, `%m`, `{grid}`, `{freq}`, `{mode}`, `%T`, `%D`, `{rsv}`, `{name}`, `{dist}`, `{bearing}`); `AddPlateBehindTextCommand`. Empty state `NoTextSelected` at `:1546`. |
| GEOMETRY tab: X/Y/W/H in px, BOX STYLE (fill, border enable/color/thickness, opacity, corner radius), IMAGE STYLE (fit mode), 6 align-to-crop buttons, snap toggle | REAL | `:1741-1852` | `SelectedElementLeftPx`/`TopPx`/`WidthPx`/`HeightPx`; `SelectedBoxElement.*` (type-gated via `IsBoxElementConverter`); `SelectedImageElement.Fit` + `AvailableImageFitModes`; `AlignSelectedElementToCropCommand`. Empty state `NoElementSelected` at `:1744`. |
| IMAGE tab: Brightness/Contrast/Saturation/Gamma/Sharpen/Denoise sliders + readouts | REAL | `:1862-1898` | Six two-way bound VM properties, applied through `ITransmitImagePreparer.ApplyAdjustments`. The `AdjustmentsNonDestructiveNote` (`:1905`) is honest static prose. LOOK PRESET / RESET ALL / AUTO LEVEL / REPLACE are documented as deliberately deferred (`:1899-1904`), not stubbed. |
| QSO FILL: token count, Clear fields, per-token rows | REAL | `:1949-1983` | `TemplateVariableCountText`, `ClearTemplateVariablesCommand`, `TemplateVariableRows` (`TemplateVariableRowViewModel`), `NoQsoFields` empty state. |
| SEND: meta line, Cancel (arm/confirm), Apply | REAL | `:1986-2008` | `SendMetaText`, `CancelCommand` + `IsCancelArmed`, `ApplyCommand`. |

**Deliberately deferred, documented in-file, not stubbed:** perspective transform; legacy `.mtm`
template import; the mock's PULL FROM RX DECODE / FROM LOGBOOK / QUEUE & TRANSMIT (`:1926-1932`); the
per-element visible/hidden "V" toggle and row-click selection (`:1256-1263`); the canvas's four corner
"+" marks (`:1169-1182`).

### Right column

| Card | Class | File:line | Note |
|---|---|---|---|
| Queue | PLACEHOLDER | `MainWindow.axaml:828-833` | One honest empty state (`Panes.TxQueue.NoQueueYet` = *"No queued frames"*, `en.json:380`). No queueing feature exists. |
| Mode-timing-reference table | REAL | `:845-876` | `TxControls.ModeTimingRows` (`TxControlsPaneViewModel.cs:437`), fully computed from `SstvModeDefinition.LineDurationMs`/`ImageHeight`. Real header + real `ItemsControl` rows, `MaxHeight="145"` for containment. |
| TX log table | STUB (header-only) | `:887-894` | Six header cells, no `ItemsSource`. No logging-of-sent-frames feature exists. |
| — TX time today / Duty cycle | PLACEHOLDER | `:902`, `:908` | `"—"` (`en.json:389,391`). |
| Recently sent | PLACEHOLDER + STUB | `:921-932` | Empty state `Panes.TxRecentlySent.NoneYet` (`en.json:395`); Refill&Queue (`:927`) and Open-in-editor (`:928`) are `IsEnabled="False"` + tooltip. |

---

## Gallery tab

`MainWindow.axaml:937-1182`, `DataContext="{Binding RxHistory}"` (`:943`) = `RxHistoryPaneViewModel`.
Mostly real — the filter/list/storage/note/flag/link machinery genuinely works and the list
live-updates as frames land. **The remaining honesty gaps in this app are concentrated in this tab's
filter row.**

| Control | Class | File:line | Note |
|---|---|---|---|
| Header + entry count | REAL | `:967-968` | `EntryCountText` via `UpdateEntryCountText` (`RxHistoryPaneViewModel.cs:253`), localized through `_localization.GetString`. |
| Search `TextBox` | REAL, **fixed 2026-08-22** | `:977` | `SearchText` (`RxHistoryPaneViewModel.cs`), client-side case-insensitive substring match against `Entry.Note`/`Entry.ModeId`. Watermark narrowed from `"callsign, grid, mode, note…"` to `"mode, note…"` (`en.json:260`) to match what it actually searches — `ReceiveHistoryEntry` still has no callsign/grid field. |
| All / Today segment | REAL | `:978-979` | `ShowTodayOnly` (`.cs:168`) → a real `IReceiveHistoryStore.QueryAsync` filter (`.cs:351`). |
| 14MHz `ToggleButton` | STUB (disabled), **fixed 2026-08-22** | `:980` | Was dead-interactive; now `IsEnabled="False"` + `Options.NotImplemented.Help` tooltip, matching the rest of the app's honesty convention. `ReceiveHistoryEntry` still has no frequency field — wiring this for real needs a schema change (new persisted field + capturing frequency at RX-save time), out of scope for the dead-control fix. |
| Unlogged / Flagged `ToggleButton`s | REAL, **fixed 2026-08-22** | `:981-982` | `FilterUnloggedOnly`/`FilterFlaggedOnly` (`.cs`), client-side over `Entries` (`Entry.LinkedQsoId is null` / `Entry.IsFlagged`) into a new `FilteredEntries` collection — the Gallery grid's real `ItemsSource` now, separate from `Entries` itself (which stays unfiltered, still backing the Receive tab's Previous-frames strip). |
| Sort chip | **FAKE-LIVE** | `:983` | Static `Border`, `Panes.RxHistory.SortChip` = `"SORT NEWEST"` (`en.json:261`). Asserts an active sort order; no sort logic exists anywhere. |
| Size chip | **FAKE-LIVE** | `:984` | Static `Border`, `Panes.RxHistory.SizeChip` = `"SIZE M"` (`en.json:262`). Asserts an active thumbnail-size setting that doesn't exist. |
| Latest button | REAL | `:985` | `SelectLatestCommand` (`.cs:320-321`), `CanSelectLatest` = list non-empty. Port of legacy's `SBPrim`, deliberately **not** `SBLatest` (which jumps to the *oldest* buffered frame). |
| Refresh button | REAL | `:986` | `RefreshCommand` (`.cs:336-337`). |
| Empty state | REAL | `:988-990` | `IsVisible="{Binding !Entries.Count}"`. |
| Thumbnail grid | REAL | `:1005-1045` | `ListBox` over `Entries` with `SelectedItem="{Binding SelectedEntry}"`; live-updating. |
| — thumbnail callsign / meta | PLACEHOLDER | `:1031-1032` | `"—"` (`en.json:191-192`). |
| — thumbnail mode badge | REAL | `:1038` | `Entry.ModeId`. |
| Selected-frame preview | REAL | `:1071` | `PreviewImage` (`.cs:121`), loaded per selection. |
| File path | REAL | `:1076` | `SelectedEntry.Entry.FilePath`. |
| Mode | REAL | `:1090` | `SelectedEntry.Entry.ModeId`. |
| SNR suffix | PLACEHOLDER | `:1091` | `"—"` (`en.json:252`). No per-frame SNR is tracked. |
| Frequency prefix | PLACEHOLDER | `:1097` | `"—"` (`en.json:254`). |
| Time | REAL | `:1098` | `SelectedEntry.Entry.ReceivedAt`. |
| Grid / distance | PLACEHOLDER | `:1103` | `"-- / --"` (`en.json:264`). |
| Log-entry status | REAL | `:1109-1115` | Two-`TextBlock` `IsNull`/`IsNotNull` swap on `SelectedEntry.Entry.LinkedQsoId`; flips live to "Logged" once Open-in-Log succeeds. |
| Note `TextBox` | REAL | `:1123-1125` | `SelectedEntryNote` (`.cs:130`), 600 ms debounce → `IReceiveHistoryStore.SetNoteAsync`; disabled with nothing selected. |
| Flag `CheckBox` | REAL | `:1126-1128` | `SelectedEntryIsFlagged` (`.cs:139`) → `SetFlaggedAsync`, persists immediately. |
| Open in Log button | REAL | `:1137` | `OpenInLogCommand` (`.cs:492`) → modal `QsoLinkWindowView`; writes both `ReceiveHistoryEntry.LinkedQsoId` and the `QsoRecord.ReceivedImageId` reverse FK. |
| Export frame button | REAL | `:1144` | `ExportFrameCommand` (`.cs:516`) → `IReceivedFrameExporter` + a real save dialog, optional JPEG re-encode at the Options-configured quality. |
| Re-decode button | STUB (disabled) | `:1145` | `IsEnabled="False"` + tooltip. Likely permanently infeasible — no raw audio is retained. |
| Error / export-status banners | REAL | `:1147-1154` | `ErrorMessage` (`.cs:148`), `ExportStatusMessage` (`.cs:158`). |
| Storage: Folder | REAL | `:1164` | `ImagesDirectory` (`.cs:173`), resolved via `IReceiveHistoryStore.GetImagesDirectoryAsync`. |
| Storage: Naming | PLACEHOLDER (accurate), **fixed 2026-08-22** | `:1168` | `Panes.RxHistory.NamingValue` = `"yyyyMMdd-HHmmssfff_MODE_ID8.png"` (`en.json:281`), corrected to match the real scheme (`{yyyyMMdd-HHmmssfff}_{modeId}_{entryId[..8]}.png`, `ReceiveHistoryRecorder.cs:328`, and `…_partial_…` at `:379`). Was actively wrong before (missing milliseconds and the entry-id suffix). Still a static description, not live-bound — accurate is enough for a format string. |
| Storage: Sidecar | PLACEHOLDER | `:1172` | `"—"` (`en.json:283`). |
| Storage: Disk free | PLACEHOLDER | `:1176` | `"-- GB"` (`en.json:285`). |

**Store-side coverage.** All three of `IReceiveHistoryStore.SetNoteAsync` / `SetFlaggedAsync` /
`SetLinkedQsoIdAsync` now have real callers — the previous revision's "Discovered 2026-08-11" note
about `SetLinkedQsoIdAsync` being unwired is closed (`QsoLinkWindowViewModel.cs:179`, `:285`).

---

## Logbook tab

`MainWindow.axaml:1183-1379`, `DataContext="{Binding Logbook}"` (`:1195`) =
`LogbookPaneViewModel.cs`. **Fully real — no placeholders, no stubs, no dead controls found.** One
line each:

- Callsign filter / From / To date fields + Refresh (`:1214`, `:1224`, `:1225`, `:1226`) — REAL;
  `CallsignFilter` is an exact case-insensitive match against `ILogbookSessionService.SearchAsync`;
  dates use `UtcTimestampTextConverter` with `ConverterParameter=AllowNull`. `RefreshCommand`
  (`LogbookPaneViewModel.cs:215`).
- Entry count (`:1209`) — REAL, `EntryCountDisplay` (`.cs:253`, localized).
- Empty state (`:1228-1230`) — REAL.
- 8-column QSO table (`:1231-1268`) — REAL; header + real `ItemsSource="{Binding Entries}"` with
  `SelectedItem="{Binding SelectedEntry}"`. First real `ItemsSource`-bound consumer of the
  `IndustryTable*` atoms.
- New button (`:1290`) — REAL, `NewCommand`.
- Add/Edit form (`:1292-1353`) — REAL, every field two-way bound: Callsign, Start UTC, End UTC,
  Frequency Hz, radio Mode (`SelectedValueBinding`/`SelectedValue`, deliberately not `SelectedItem`
  — see `:1306-1310`), SSTV mode, RST sent/received, Name, QTH, Grid, Country, Notes.
- Log / Update buttons (`:1356-1357`) — REAL, `LogCommand` (`.cs:363`) / `UpdateCommand` (`.cs:411`),
  visibility-swapped on `IsEditing`; genuinely persist and report ADIF-UDP/QRZ push status.
- Status message (`:1360-1363`) — REAL, reflects the actual last-operation outcome.
- ADIF Import / Export (`:1373-1374`) — REAL, `ImportAdifCommand` / `ExportAdifCommand`.

---

## Radio header (VFO / Favourites / Transceiver)

`RadioHeaderView.axaml`, backed by `RadioStatusViewModel.cs`. Fixed 102 px band at
`MainWindow.axaml:138`, always visible above the tabs.

**VFO card** (`:43-152`)

| Control | Class | File:line | Note |
|---|---|---|---|
| "VFO A · RX · M1" kicker | **FAKE-LIVE** (mild) | `:48` | `RadioStatus.VfoCaption` = `"VFO A · RX · M1"` (`en.json:64`). A static caption asserting VFO A, RX state, and memory M1 — none of which are tracked. Decorative in intent, but it reads as rig state. |
| Frequency readout (40 pt) | REAL | `:53-58` | `FrequencyDisplay` (`RadioStatusViewModel.cs:37`), assigned in `OnStateChanged` (`.cs:282`) from `IRadioSessionService.StateChanges`. |
| USB / LSB / FM sideband segment | STUB (disabled) | `:70-81` | Whole `StackPanel` `IsEnabled="False"` + `Options.NotImplemented.Help`. The `IsChecked="True"` on USB (`:73`) is inert. No sideband concept on `IRadioSessionService`. |
| UTC clock | REAL | `:88-91` | `UtcClockDisplay` (`.cs:114`), 1 s `DispatcherTimer` (`UpdateUtcClock`, `.cs:265`). |
| BW / Split pills | STUB (disabled) | `:98-107` | `IsEnabled="False"`, `Opacity="0.45"`, tooltip; values `"BW —"` / `"SPLIT —"` (`en.json:68-69`). |
| CAT link lozenge | REAL | `:113-122` | `CatLinked` (`.cs:94`), driven by `IRadioSessionService.ConnectionEvents` (`.cs:344`); LED + a real two-state text swap. |
| Step / RIT pills | STUB (disabled) | `:127-136` | Same treatment; `"STEP —"` / `"RIT —"` (`en.json:70-71`). |
| Rig-meters pill | REAL | `:146-149` | `RigMetersDisplay` (`.cs:146`), joined from the real polled `RadioState.SwrRatio`/`AlcLevel`/`PowerPercent`; reset to `"—"` when CAT drops (`.cs:364`). **Reclassified from STUB** — the previous revision's "no rig-meters concept exists" premise was checked against `RigctldClientProtocol`/`HamlibRadioProtocol` and found false. |

**Favourites card** (`:163-256`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Preset recall buttons | REAL | `:200-226` | `Presets` (`.cs:269`); each `SelectCommand`/`CommandParameter` applies frequency **and** mode via `IRadioSessionService`. |
| Store current | REAL | `:177-181` | `StoreCurrentPresetCommand` (`.cs:479`), `CanStoreCurrentPreset` = `_currentFrequencyHz > 0` (`.cs:500`). |
| Hint caption | PLACEHOLDER (decorative) | `:251-253` | `"Click a preset to recall it"` (`en.json:73`) — accurate instructional prose, not data. |
| Edit list / Import / Scan | STUB (disabled) | `:242-250` | Whole group `IsEnabled="False"` + tooltip. A real presets editor (`EditorRows`/`AddPresetRowCommand`/`SavePresetsCommand`) exists on the VM but is deliberately unmapped per a direct prior user request; disabling was the honesty fix. |

**Transceiver card** (`:272-349`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Receiving toggle | REAL | `:290-294` | `IsReceiving` (`.cs:84`); `Opacity` bound to `IsCapturePausedForTx` (`.cs:127`) so it visually dims while capture is paused for a local transmission, without changing the toggle's own contract. |
| Halt button | REAL | `:295-297` | `HaltReceivingCommand` (`.cs:665`). |
| RX level meter | REAL | `:315-331` | `RxLevelFillPercent` (`.cs:182`) drives both fill and marker columns via `DoubleToStarGridLengthConverter`. **Reclassified from FAKE-LIVE** — the old fixed 63 %/78 % literals are gone; `RadioState.SignalStrengthDb` is now really polled (`l STRENGTH` / `RIG_LEVEL_STRENGTH`). |
| RX level value | REAL | `:332` | `RxLevelDisplay` (`.cs:166`), `"—"` when the rig doesn't report it. |
| TX volume slider + value | REAL | `:344-346` | `TxVolumePercent` (`.cs:72`), the same property as the Transmit tab's Drive slider. |
| Error / maintenance messages | REAL | `:363-373` | `ErrorMessage` (`.cs:51`) and the deliberately separate `MaintenanceMessage` (`.cs:64`). |

---

## Menu bar, tab-strip chips, status bar (`MainWindow.axaml`)

**Menu bar** (`:55-134`)

| Item | Class | File:line |
|---|---|---|
| Brand label ("SSTV / CONSOLE") | decorative | `:63-70` |
| File > Open image (Ctrl+O) | REAL | `:76` — `TxControls.SelectImageCommand`, `IsEnabled="{Binding TxControls.CanChangeSourceOrMode}"` (a disabled `MenuItem` also suppresses its own `InputGesture`, closing the accelerator). |
| File > Save frame as (Ctrl+S) | STUB (disabled) | `:77` |
| File > Exit | REAL | `:79` — `ExitCommand`. |
| Configurations > Storage & naming, Macros | STUB (disabled) | `:86-87` |
| Rig & PTT > PTT method, Frequency memories, Test PTT | STUB (disabled) | `:96-98` |
| Calibration > Clock calibration, Loopback self-test, Tone generator, Slant reference | STUB (disabled) | `:101-104` |
| Tools > Re-decode from WAV, Export session log | STUB (disabled) | `:107-108` |
| Help > About | REAL | `:111` — `OpenAboutCommand` (`MainViewModel.cs:166`) → `AboutRequested` → `MainWindow.axaml.cs:173`. |
| Options… | REAL | `:113` — `OpenOptionsCommand` (`MainViewModel.cs:159`). |
| Callsign chip | REAL | `:126-133` — `CallsignDisplay` (`MainViewModel.cs:137`), `"N0CALL"` fallback until set in Options. |

Menu total: 2 real File items + Help>About + Options = 4 real; 12 disabled stubs. The three
Options-duplicating stubs (Station, Audio devices, CAT interface) were pruned outright rather than
left disabled.

**Tab-strip status chips** (`:1399-1423`, right-aligned in the tab band)

| Chip | Class | File:line | Note |
|---|---|---|---|
| AUTO-DETECT | PLACEHOLDER (decorative) | `:1401-1403` | `"AUTO-DETECT"` (`en.json:45`) — a static label that happens to be accurate (auto-detect is the only mode-selection behaviour). |
| Detected-mode LED + lozenge | REAL | `:1404-1409` | `RxImage.DetectedModeDisplay`; LED `.active` conditional on `DetectedMode` being non-null, so it isn't green during the "—" empty state. |
| Auto-correct chip | REAL | `:1410-1412` | `RxImage.AutoCorrectDisplay`. |
| Slant chip | REAL | `:1413-1415` | `RxImage.SlantPpmStatusBarDisplay` (`.cs:398`). |
| SNR chip | PLACEHOLDER | `:1416-1418` | `MainWindow.TabStrip.SnrValue` = `"—"` (`en.json:46`). |
| AUTOSAVE ON chip | STUB (disabled) | `:1419-1422` | `IsEnabled="False"`, `Opacity="0.45"`, `NotImplemented` tooltip. Text still literally reads `"AUTOSAVE ON"` (`en.json:47`) — the disabled/dimmed treatment is the mitigation, but the wording still asserts a state. Worth changing to `"—"`; noted, not overstated. |

**Status bar** (`:1459-1536`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Frames today | REAL | `:1467-1469` | `RxHistory.FramesTodayDisplay` (`RxHistoryPaneViewModel.cs:251`). |
| Log size | REAL | `:1470-1472` | `Logbook.LogSizeDisplay` (`LogbookPaneViewModel.cs:170`). |
| Receiving LED + lozenge | REAL | `:1478-1483` | `RadioStatus.IsReceiving`, with a `healthyTint` class binding. |
| TX-INHIBIT LED + lozenge | REAL | `:1488-1494` | `TxControls.ErrorMessage != null`, amber `attentionTint`; tooltip states the narrow meaning honestly (`en.json:29`). |
| TX-KEYED LED + lozenge | REAL | `:1500-1506` | `RadioStatus.IsKeyed` (`RadioStatusViewModel.cs:109`) — real rig PTT readback, cleared when CAT drops; red `dangerTint`. Tooltip states the polling lag and the VOX/DTR caveat (`en.json:31`). |
| Frequency / Mode | REAL | `:1507-1512` | `RadioStatus.FrequencyDisplay` / `ModeDisplay`. |
| Memory tag | PLACEHOLDER | `:1513-1515` | `"—"` (`en.json:32`). |
| Detected mode | REAL | `:1516-1518` | `RxImage.DetectedModeText` (`.cs:347`). |
| Line progress | REAL | `:1519-1521` | `RxImage.LineProgressText`. |
| SNR | PLACEHOLDER | `:1522-1524` | `"—"` (`en.json:35`). No per-line SNR computation exists anywhere in the decode pipeline. |
| Slant | REAL | `:1525-1527` | `RxImage.SlantPpmStatusBarDisplay`. |
| Buffer | REAL | `:1528-1530` | `RxImage.BufferedSampleCountStatusBarDisplay` (`.cs:509`). |
| Disk | PLACEHOLDER | `:1531-1533` | `"—"` (`en.json:41`). |

---

## Options window (`OptionsWindowView.axaml`, `OptionsWindowViewModel.cs`)

**Nine tabs**, in source order: General, Audio, Radio, Tx, Decode, Identification, Advanced, QRZ.com,
**Forwarding**. Window-level machinery is REAL throughout — `SaveCommand` (`.cs:852`) persists via
`OptionsSettingsService`, `CancelCommand` discards, per-section resets and the confirm-gated
`RequestResetAllCommand`/`ConfirmResetAllCommand`/`CancelResetAllCommand` (`:735-743`) all work.

### General tab (`:34-88`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Language `ComboBox` | REAL | `:43-51` | `AvailableCultures`/`SelectedCulture` → `ILocalizationService.SetCultureAsync` on Save. |
| Remember window position | REAL | `:53-55` | `RememberWindowPosition` → `WindowGeometrySettings`; restore/save in `MainWindow.axaml.cs:43-123`, gated the same two ways legacy is (flag set **and** `WindowState == Normal`). |
| JPEG quality stepper | REAL | `:59` | `JpegQuality`, `Minimum=1 Maximum=100`, backed by `ImageExportSettings`; consumed by the Gallery's Export-frame path. |
| 7 waterfall/spectrum colour buttons | STUB (disabled) | `:75-81` | All `IsEnabled="False"` + tooltip. Deliberate: the flat design has one fixed palette, not 7 per-element pickers (the section's own help text says so, `:64-66`). Not a regression — waterfall/spectrum *rendering* is real colour. |
| Reset section | REAL | `:85` | `ResetGeneralToDefaultCommand`. |

### Audio tab (`:90-185`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Capture / Playback device `ComboBox`es | REAL | `:96-115` | `CaptureDevices`/`PlaybackDevices` via `IAudioDeviceEnumerator`. |
| Sample rate | REAL | `:120` | `SampleRate`, persisted. |
| RX FIFO / TX FIFO steppers | STUB (disabled) | `:144`, `:150` | Hardcoded `Value="16"`, `IsEnabled="False"` + tooltip. Legacy's `m_SoundFifoRX/TX` configure Win32 `waveIn/waveOut` queued-buffer counts — no analog in the MiniAudio engine. **Superseded, not a gap** (`:128-131`). |
| Sound-card thread priority | STUB (disabled) | `:154-160` | `IsEnabled="False"` + tooltip; `IsChecked="True"` on Normal is inert. Legacy's `m_SoundPriority` is read/persisted but never applied anywhere in legacy either. This port's real `AudioDeviceSettings.CaptureThreadPriority` has different enum semantics and needs its own control (`:131-136`). |
| Application priority (Normal / High) | REAL | `:164-165` | `IsAppPriorityNormalSelected`/`IsAppPriorityHighSelected` (`.cs:692,704`) → `AppPerformanceSettings.ProcessPriority`, applied once at startup. Only 2 of .NET's 6 values, matching legacy's own UI scope. |
| Stereo capture source (Mono/Left/Right) | REAL | `:173-175` | `IsCaptureChannelMono/Left/RightSelected` (`.cs:655,667,679`) → `AudioDeviceSettings.CaptureChannelSource` → real per-channel extraction in `MiniAudioCaptureSession`. |
| Stereo TX | REAL | `:177-178` | `StereoTxEnabled` → `AudioDeviceSettings.StereoTxEnabled`. |
| Reset section | REAL | `:182` | `ResetAudioToDefaultCommand`. |

### Radio tab (`:187-288`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Backend radios (None / rigctld / Hamlib) | REAL | `:197-208` | `IsNoneBackendSelected`/`IsRigctldBackendSelected`/`IsHamlibBackendSelected` (`.cs:298,310,322`); all three backends registered in DI. |
| OmniRig radio | STUB (disabled) | `:211-215` | `IsEnabled="False"`; its own dedicated help tooltip (not the generic one) documents it as a speculative 5th backend. Correctly stays stub. |
| rigctld Host / Port | REAL | `:221`, `:225` | `RigctldHost` / `RigctldPort`, persisted; shown only while `IsRigctldSelected` (`.cs:292`). |
| **Test connection button + status** | REAL | `:234-240` | `TestRigctldConnectionCommand` (`.cs:351`) — tests the *currently typed* (not-yet-saved) host/port via a disposable connection, never disturbing the live session. **New since the previous survey revision; was not documented at all.** |
| Hamlib Model / Serial port / Baud / PTT type | REAL | `:248`, `:254`, `:259`, `:265` | All persisted; shown only while `IsHamlibSelected` (`.cs:334`). |
| RTS-on-RX / PTT-lock checkboxes | STUB (disabled) | `:275-280` | Both gate legacy's raw-serial RTS-pin PTT keying, a family explicitly excluded from this port; the real PTT path goes through Hamlib/rigctld/flrig. Correctly stays stub. |
| Reset section | REAL | `:285` | `ResetRadioToDefaultCommand`. |

### Tx tab (`:290-328`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign | REAL | `:299` | `Callsign`, persisted; also feeds the shell's menu-row chip. |
| Operator name / Operator grid | REAL | `:310`, `:314` | `OperatorName`/`OperatorGrid` → `OperatorSettings`. No legacy MMSSTV equivalent; added to back the TX editor's `{name}`/`{grid}` macro tokens. |
| Reset section | REAL | `:325` | `ResetTxToDefaultCommand`. |

The old disabled "QRZ lookup" placeholder was removed outright (see the in-file note, `:319-323`),
superseded by the real QRZ.com tab.

### Decode tab (`:341-428`) — **all real except one control**

| Control | Class | File:line | Note |
|---|---|---|---|
| Sense level (Very low / Low / High / Very high) | REAL | `:351-354` | `IsSenseLevel*Selected` (`.cs:434-482`) → `SstvDecoderSettings.SenseLevel` → `AnalogFmSstvDecoder.SenseLevelPresets`, a port of legacy `CSSTVDEM::SetSenseLvl`. Out-of-range persisted values fall back to preset 0 (matching legacy's `default:`), absent falls back to preset 1 (legacy's ctor default). |
| RX BPF sharpness (Normal / Wide / Sharp / Very sharp) | REAL | `:362-365` | `IsRxBpf{Off,Wide,Narrow,VeryNarrow}Selected` (`.cs:529-577`) → real `if(m_bpf)` bypass dispatch against a preset-parameterized `SearchBandpassFilter`; absent and out-of-range both clamp to Wide. |
| Demodulator type (PLL / Zero crossing / Hilbert) | REAL | `:373-375` | `IsDemodType*Selected` (`.cs:486-522`) → real runtime dispatch in `AnalogFmSstvDecoder`; absent and out-of-range both clamp to Hilbert. |
| **RX buffer (Off / On / Extended)** | REAL | `:383-385` | `IsRxBufferOffSelected`/`IsRxBufferOnSelected`/`IsRxBufferExtendedSelected` (`.cs:581,593,605`). **Reclassified from STUB** — the whole 9-phase RX-buffer subsystem, including this Options UI (Phase 9), has landed. The previous revision's separate "hardcoded-wrong-default (shows Off, real default On)" bug is also gone: the checked state is now bound, not literal. |
| Auto-start (Off / On) | STUB (disabled) | `:392-395` | Whole `WrapPanel` `IsEnabled="False"` + tooltip. **Note:** unlike the previous revision, there is now **no** `IsChecked` on either radio — the old hardcoded-wrong `IsChecked="True"` on "On" (which misreported a nonexistent feature as active) has been removed. Real legacy behaviour exists but lives on the legacy toolbar (`SBAuto`), not `Option.dfm` — a placement question. |
| Auto-stop on erratic/weak signal | REAL | `:406-407` | `AutoStopEnabled` → `SstvDecoderSettings.AutoStopEnabled`. Default OFF, matching legacy's fresh-install default. |
| Restart onto a stronger sync mid-reception | REAL | `:408-409` | `SyncRestartEnabled`. Default ON. |
| Auto-resynchronize during decode | REAL | `:415-416` | `AutoSyncEnabled`. |
| Auto-correct slant during decode | REAL | `:420-421` | `AutoSlantEnabled`, plus `IsEnabled="{Binding IsAutoSlantRowEnabled}"` (`.cs:623` = `RxBufferMode != Off`) — a faithful port of legacy's `CBASlant->Enabled = RGRBuf->ItemIndex ? TRUE : FALSE` (`Option.cpp:222`). |
| Reset section | REAL | `:423` | `ResetDecodeToDefaultCommand`. |

All decoder settings on this tab are **restart-required** — no live-reconfiguration path exists for
any DI-singleton-baked setting. Squelch/sense level is the most user-visible instance of that limit
(legacy applies it live).

### Identification tab (`:439-516`)

| Control | Class | File:line | Note |
|---|---|---|---|
| ID method: Off / CW | REAL | `:448-449` | `IsIdMethodOffSelected`/`IsIdMethodCwSelected` (`.cs:629,641`). |
| ID method: Sound file | STUB (disabled) | `:452` | Individually `IsEnabled="False"` (not the whole group) — `CwIdMode.SoundFile` is explicitly out of v1 scope. |
| CW text / CW frequency / CW speed | REAL | `:457`, `:461`, `:465` | `CwText` / `CwToneFrequencyHz` (100–3000) / `CwWpm` (10–50); the whole block `IsVisible`-gated on CW being selected. |
| Sound-file path + Browse | STUB (disabled) | `:474-475` | Both `IsEnabled="False"` + tooltip. |
| FSK encode / FSK decode | REAL | `:478-481` | `FskIdTxEnabled` / `FskIdRxEnabled`. |
| NR/RST enable + text | REAL | `:483-488` | `NrRstEnabled` / `NrRstText`, the text field `IsVisible`-gated on the checkbox. |
| VOX Off/On + Edit tone | STUB (disabled) | `:497-501` | Whole `WrapPanel` + button disabled; no VOX backend exists anywhere in this port. |
| Tune-satellite trigger | STUB (disabled) | `:507-508` | `IsEnabled="False"` + tooltip. Real Tune frequency/duration/command exist on `RadioStatusViewModel` (`TuneFrequencyHz`/`TuneDurationSeconds`/`TuneCommand`, `.cs:75,78`) but are intentionally not duplicated here — and are currently unmapped to any control anywhere in the UI. |
| Reset section | REAL | `:513` | `ResetIdentificationToDefaultCommand`. |

**Gap worth naming:** the RX-side decoded NR/RST value (`RxImagePaneViewModel.DecodedNrRst`, `.cs:288`)
is a real, populated property with **no control bound to it anywhere** — see the file's own note at
`OptionsWindowView.axaml:436-438`.

### Advanced tab (`:522-605`) — **fully STUB, by deliberate decision**

Every control is `IsEnabled="False"` with `Options.NotImplemented.Help`, and the tab's own caption
(`:525`, `Options.Advanced.Caption`) states so on screen. Contents: PLL VCO gain / loop order / loop
cutoff / out cutoff (`:534-542`, hardcoded 2600/1/200/1200); Zero-crossing order / cutoff / smoothing
(`:554-560`) + Differentiator (`:562-563`); TX BPF / TX LPF toggles (`:572-575`) + TX sample-clock
offset (`:579`); Loopback Off/Internal/External (`:590-594`); Polynomial calibration (`:595-596`);
Clock-adjust and Level-calibration wizards (`:598-599`). Filter-response preview buttons from legacy
(`DispTxBpf` etc.) were deliberately omitted rather than stubbed — nothing real to preview.

The one row with a real, bounded path to being wired is TX BPF/LPF: `TxOutputBandpassFilter` is
already applied unconditionally, so these are a real user-toggleable bypass away — but it is
TX-encode-path code with over-the-air spectral consequences, so it carries the same review weight as
the rest of the tab. The old "PLL/Zero-crossing tuning is gated behind demod-type dispatch"
precondition is **met** (that subsystem shipped); these rows are now the sole remaining piece.

### QRZ.com tab (`:611-643`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Enable QRZ.com lookup | REAL | `:619-620` | `QrzLookupEnabled`; gates `ILogbookSessionService.LookupCallsignAsync`'s live path. |
| Username / Password | REAL | `:622-626` | `QrzLookupUsername` / `QrzLookupPassword` (`PasswordChar="•"`), persisted plaintext with an on-screen hint (`:631`) stating the risk — no scoped-credential mode exists for this API. |
| Test button + status | REAL | `:633-635` | `TestQrzLookupCommand` (`.cs:1076`) → `IQrzCallsignLookup.TestCredentialsAsync` against the *current in-memory* fields; `TestQrzLookupStatus` readout. |
| Reset section | REAL | `:640` | `ResetQrzToDefaultCommand`. |

Deliberately **not** named "Logbook": the QRZ *Logbook upload* API (`QrzUploadSettings`) is a separate,
already-real backend that still has no Options UI of its own — a documented gap (`:607-610`).

### Forwarding tab (`:645-727`) — **new since the previous survey revision; previously undocumented**

ADIF-over-UDP multi-destination forwarding (generalizing the former GridTracker-only path).

| Control | Class | File:line | Note |
|---|---|---|---|
| Description / empty hint | REAL | `:653`, `:655-656` | Hint `IsVisible`-gated on `!AdifUdpDestinations.Count`. |
| Column-header row (Name / Host / Port) | REAL (layout) | `:682-688` | `IsVisible`-gated on the list being non-empty; uses invisible same-shape `CheckBox`/`Button` in columns 0/4 so `Auto` resolves identically to the data rows (Avalonia has no `SharedSizeGroup`) — see `:658-681`. |
| Per-destination rows: Enabled, Name, Host, Port, Remove | REAL | `:693-717` | `ItemsSource="{Binding AdifUdpDestinations}"` (`.cs:290`) over `AdifUdpDestinationRowViewModel`; `Enabled`/`Name`/`Host`/`Port` (1–65535) all two-way bound, `RemoveCommand` per row. |
| Add destination | REAL | `:719-720` | `AddAdifUdpDestinationCommand`. |
| Reset section | REAL | `:724` | `ResetForwardingToDefaultCommand`. |

---

## About window (`AboutWindowView.axaml`, `AboutWindowViewModel.cs`) — new, fully REAL

Opened from Help > About (`MainWindow.axaml:111` → `MainViewModel.OpenAboutCommand`, `.cs:166` →
`AboutRequested` → `MainWindow.axaml.cs:173`). 440×360, `CanResize="False"`, `CenterOwner`.

| Control | Class | File:line | Note |
|---|---|---|---|
| Application name | REAL | `:23` | `ApplicationName` — `AssemblyProductAttribute` off `Assembly.GetEntryAssembly()` (`AboutWindowViewModel.cs:46`), `"Scanline Studio"` fallback. |
| Version | REAL | `:27` | `VersionDisplay` — `AssemblyInformationalVersionAttribute` incl. the SDK-appended git SHA, falling back to the plain assembly version, then `"unknown"` (`.cs:47-49`). |
| Copyright | REAL | `:30` | `AssemblyCopyrightAttribute` (`.cs:50`). |
| Derivative / License body | static (localized prose) | `:33`, `:40` | `About.Derivative` / `About.LicenseBody` — genuine static licence text, not placeholder data. |
| Close button | REAL | `:47-48` | `CloseCommand` (`.cs:57-58`) → `RequestClose`; `IsDefault`+`IsCancel`. |

## QSO-link window (`QsoLinkWindowView.axaml`, `QsoLinkWindowViewModel.cs`) — fully REAL

Opened modally from the Gallery's "Open in Log" (`MainWindow.axaml:1137`). 460×560, `CenterOwner`.

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign filter + Search | REAL | `:30-33` | `CallsignFilter` (`.cs:71`), `SearchCommand` (`.cs:119`) → `ILogbookSessionService`. |
| Results `ListBox` (date / callsign / SSTV mode) | REAL | `:36-54` | `SearchResults` (`.cs:99`), `SelectedQso` (`.cs:75`). |
| Link selected | REAL | `:56-58` | `LinkSelectedCommand` (`.cs:162`) → `IReceiveHistoryStore.SetLinkedQsoIdAsync` (`.cs:179`) **first**, then the reverse-FK update — ordering is deliberate and documented (`.cs:156-162`). |
| Create-new form: Callsign, RST sent, RST received, Notes | REAL | `:69-78` | `NewCallsign`/`NewRstSent`/`NewRstReceived`/`NewNotes` (`.cs:88-97`). |
| Create and link | REAL | `:79-81` | `CreateAndLinkCommand` (`.cs:227`), `CanCreateAndLink` requires a non-empty callsign and no in-flight/already-created record (`.cs:219`); `_createdQsoId` (`.cs:58`) prevents duplicate QSO creation if only the link half failed. |
| Error banner | REAL | `:85-88` | `ErrorMessage` (`.cs:84`), `IsNotNull`-gated. |
| Cancel | REAL | `:93-94` | `CancelCommand` → `RequestClose`. |

---

## Summary

**Counts are approximate** — deliberately so. They count individually-classified interactive or
data-displaying controls (grid/table rows, buttons, sliders, chips), not decorative card headers,
captions, dividers or layout elements. The TX image editor's ~130 controls are counted as a block
(all REAL, verified by the zero-`IsEnabled="False"` grep above) rather than enumerated; treat every
figure below as ±10, not exact.

| Classification | Count (approx.) | Where it's concentrated |
|---|---|---|
| **REAL** | ~275 | TX image editor (~130, 100 %), Logbook tab (~25, 100 %), Options General/Audio/Radio/Tx/Decode/Identification/QRZ/Forwarding cores (~50), Receive tab telemetry (~30), Radio header (~12), status bar + tab strip (~13), Gallery (~20), About/QSO-link dialogs (~12). |
| **PLACEHOLDER** (honest) | ~60 | Receive Input-chain/Signal-quality/Frame-metadata/Unattended-RX unbacked rows, TX Outgoing-metadata card (9), TX mode/output scaffolding rows (8), Gallery per-entry SNR/freq/grid + Sidecar/Disk, status-bar Memory/SNR/Disk, TX Queue/Recently-sent/Session-frames empty states. |
| **STUB** (disabled + tooltip) | ~56 | Options Advanced tab (~17, whole tab), menu bar (12), Options Audio FIFO/priority (3), Options Radio OmniRig/RTS/PTT-lock (3), Options Identification Sound-file/VOX/Tune-sat (5), Options Decode Auto-start (1), Options General colour buttons (7), Radio header sideband/BW/Split/Step/RIT/Edit-Import-Scan (~10), RX Abort/Re-decode/Reset/Advanced-timing (4), Gallery Re-decode + 14MHz filter (2), TX Recently-sent buttons (2), Decode-activity + TX-log header-only tables (2), 4 empty hatch plots. |
| **STUB (dead-interactive)** | **0**, **closed 2026-08-22** | Was 4 (Gallery Search `TextBox`, 14MHz/Unlogged/Flagged `ToggleButton`s). Search and Unlogged/Flagged are now real (client-side `FilteredEntries`, `RxHistoryPaneViewModel`); 14MHz is now an honest disabled stub instead (see the STUB row above) — no frequency field exists on `ReceiveHistoryEntry` to filter by, and adding one is a schema change out of scope for a dead-control fix. No dead-interactive controls remain anywhere in the app. |
| **FAKE-LIVE** | **4** | Gallery Sort chip `"SORT NEWEST"` (`:983`), Gallery Size chip `"SIZE M"` (`:984`), VFO caption `"VFO A · RX · M1"` (`RadioHeaderView.axaml:48`), and the AUTOSAVE-ON chip's wording (`MainWindow.axaml:1421`, mitigated by being disabled+dimmed). Gallery Storage Naming (`:1168`) is fixed as of 2026-08-22 — see its own PLACEHOLDER row above. Plus **4 latent** — the Advanced-timing values (`:272-275`), currently unreachable behind a permanently-disabled toggle but still real-looking literals if it's ever enabled. |
| **PARTIAL** | **1** | Receive Frame-metadata "Grid / dist" (`:687`) — grid half real, distance half a hardcoded `"--"` inside `GridDisplay` itself (`RxImagePaneViewModel.cs:314`). |

**Fully real screens:** TX image editor (100 %, zero disabled/unbacked controls). Logbook tab (100 %).
About dialog. QSO-link dialog. Options Forwarding tab. Options QRZ.com tab. Spectrum & waterfall card.
Mode-timing-reference card.

**Mostly real, mixed:** Transmit tab left column (mode/ID/output/stock/transmit machinery real;
Outgoing-metadata card fully placeholder). Gallery tab (list/filter/storage/note/flag/link real; the
filter row is the honesty gap). Radio header (frequency/CAT/receiving/TX-volume/UTC/rig-meters/RX-level
real; sideband/BW/split/step/RIT stubbed). Receive tab Sync&Slant / Input-chain / Signal-quality
cards. Options General/Audio/Radio/Tx/Decode/Identification tabs.

**Mostly/fully stub:** Options Advanced tab (100 %). Receive tab Decode-activity, Unattended-RX and
Session-frames cards. Transmit tab right-column Queue / TX-log / Recently-sent. Menu bar outside
File > Open/Exit, Help > About, and Options.

### Most important findings, current

1. **The old headline finding is closed.** `MainWindow.StatusBar.SnrValue` and its slant counterpart
   were the previous revision's lead example of literals masquerading as live data. Slant is now real
   (`RxImage.SlantPpmStatusBarDisplay`); SNR is now an honest `"—"` (`en.json:35`). Per-line SNR still
   does not exist anywhere in the decode pipeline — but the UI no longer claims it does.
2. **The Gallery filter row's dead-interactive controls are fixed, 2026-08-22.** Search, Unlogged, and
   Flagged are now real client-side filters (`RxHistoryPaneViewModel.FilteredEntries`, distinct from
   the unfiltered `Entries` the Previous-frames strip still binds to); 14MHz is now an honest disabled
   stub instead of a dead one, since `ReceiveHistoryEntry` still has no frequency field. Sort and Size
   remain the app's only two FAKE-LIVE chips tied to genuinely unbuilt features.
3. **The Storage "Naming" row is fixed, 2026-08-22** — corrected to
   `yyyyMMdd-HHmmssfff_MODE_ID8.png`, matching `ReceiveHistoryRecorder.cs:328` exactly.
4. **`RxImagePaneViewModel.DecodedNrRst` is real and populated with no control bound to it anywhere** —
   a fully-built RX-side decode result with no display surface.
5. **Options → Decode settings are restart-only.** Every decoder toggle on that tab is baked into a DI
   singleton with no live-reconfiguration path. Sense level is the most user-visible case, since it's
   the control most likely to be adjusted while actively chasing a signal (legacy applies it live).
6. **The Identification card in the TX pane is a construction-time snapshot** (`TxControlsPaneView.axaml:145-148`)
   — it shows the Options values as they were when the pane was built, not as they are now.
