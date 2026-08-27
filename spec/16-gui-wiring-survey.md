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
| Shell (menu bar, tab strip chips, status bar, 4 tabs) | `src/ScanlineStudio.UI/Views/MainWindow.axaml` (1612 lines) |
| Radio header (VFO / Favourites / Transceiver) | `src/ScanlineStudio.UI/Views/RadioHeaderView.axaml` (370) |
| Transmit left column | `src/ScanlineStudio.UI/Views/TxControlsPaneView.axaml` (436) |
| TX image editor (Transmit centre) | `src/ScanlineStudio.UI/Views/TxImageEditorPaneView.axaml` (2015) |
| Options window (9 tabs) | `src/ScanlineStudio.UI/Views/OptionsWindowView.axaml` (837) |
| About dialog | `src/ScanlineStudio.UI/Views/AboutWindowView.axaml` (52) |
| QSO-link dialog | `src/ScanlineStudio.UI/Views/QsoLinkWindowView.axaml` (98) |
| RX frame pane / waterfall pane | `RxImagePaneView.axaml` (31), `WaterfallPaneView.axaml` (13) |

**Shell shape (current, verified):** `MainWindow.axaml:53` is a `Grid RowDefinitions="26,102,*,25"` —
menu row, always-visible `RadioHeaderView` band, tab area, status bar. The tab area
(`MainWindow.axaml:156`) is a plain `TabControl` bound to `MainViewModel.SelectedTabIndex`
(`MainViewModel.cs:37`) with four fixed tabs in source order **Receive=0, Transmit=1, Gallery=2,
Logbook=3** (`MainViewModel.LogbookTabIndex = 3`, `MainViewModel.cs:22`). There are no dockable
panes, no dock factory, and no `Dock.Avalonia` package reference (`ScanlineStudio.UI.csproj` lists
only Avalonia, Avalonia.Desktop, Avalonia.Diagnostics, Avalonia.Themes.Fluent,
Avalonia.Controls.ColorPicker, CommunityToolkit.Mvvm and two Microsoft.Extensions packages). **RX
history lives in the Gallery tab** (`MainWindow.axaml:1006-1251`, `DataContext="{Binding RxHistory}"`),
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
Input/Signal cards (left), Waterfall + Incoming-frame + Decode-activity (centre), Frame-metadata
(right — Unattended-RX/Session-frames cards removed 2026-08-25, see below). Left, centre-top and
right columns bind `DataContext="{Binding
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
| Remaining | REAL — **wired 2026-08-25, time formula fixed same day** | `:230` | `RemainingText` (`RxImagePaneViewModel.cs`). Lines-remaining shares `LineProgressText`'s `Progress` x `DetectedMode.ImageHeight` source; seconds-remaining is `(1 - Progress) * TxControlsPaneViewModel.GetFrameSeconds(mode)`, NOT `linesRemaining * LineDurationMs` (that double-counted for `YCbCrLinePaired`/`MonoAveragedPaired` modes — `LineDurationMs` is per transmission line, not per image row — same bug class as `GetFrameSeconds`'s own PD90 fix). |
| Listening / Paused segment | REAL — **new 2026-08-25** | `:240-247` | Port of legacy's RX-page `SBAuto` toggle (`TMmsstv::RxAutoPush`, `Main.cpp:6042-6060`). `IsAutoDetectPaused` (`RxImagePaneViewModel.cs`) → `ISstvSessionService.SetAutoDetectPaused` → session-layer audio gate (capture/waterfall untouched) + `ISstvDecoder.RequestAbandonReception` (decoder-side cleanup, one-shot deferred command). A SEPARATE control from the still-stub Auto/Locked pair above it — different concept, not repurposed. Four rounds of plan-review; see `elegant-wondering-hinton.md`. |

**Sync & slant card** (`MainWindow.axaml:238-273`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Source | REAL — **wired 2026-08-25** | `:251` | `SyncSourceDisplay` (`RxImagePaneViewModel.cs`), live-polled `SstvSyncSource` (`Idle`/`Locked`/`AvtTraining`) from `ISstvDecoder.SyncSource` — a 3-value classification, not the originally-considered 4-way Search/VisLock/Forced/AvtTraining split (see `SstvSyncSource`'s own doc comment for why that split isn't honestly derivable from existing decoder state). |
| Slant ppm | REAL | `:249` | `SlantPpmDisplay` (`RxImagePaneViewModel.cs:392`) — read-only formatted readout, not an editable stepper. |
| Sync offset | REAL | `:250` | `SyncOffsetSamplesDisplay` (`.cs:405`). |
| Auto-correct | REAL | `:251` | `AutoCorrectDisplay` (`.cs:443`), 4-way AVT/Off/Locked/on-not-locked readout gated by `SstvDecoderSettings.AutoSlantEnabled`. |
| VIS threshold | REAL — **wired 2026-08-25** | `:266` | `VisThresholdDisplay` (`RxImagePaneViewModel.cs`), restart-only construction-time read of `ISstvDecoder.SenseLevel` (previously only test-only exposed as `SenseLevelForTests`, now a real public member). New plain row, not behind the removed Advanced Timing disclosure. Preset NAME only (Very low/Low/High/Very high) — the underlying threshold is a raw AGC-domain amplitude, not dB; the old stub's "−26 dB" placeholder was a fake literal. |
| Re-sync button | REAL | `:275` | `RequestReSyncCommand` (`.cs:535-536`) → `ISstvSessionService.RequestReSync`. |
| Correct slant button | REAL | `:276` | `RequestCorrectSlantCommand` (`.cs:538-539`) — port of legacy's `KRCS` popup item, sibling to Re-sync. |
| ~~Reset button~~ | **REMOVED 2026-08-25** | — | Legacy does have a real Slant-Reset (`Main.cpp:13186-13193`), but this port rebuilds its slant tracker per lock and tears it down at end-of-image, making a reset structurally inert against this port's own architecture — not a missing port, a structural mismatch. |
| ~~Advanced timing disclosure toggle~~ / ~~Sample clock~~ / ~~Sync window~~ / ~~Drop-line~~ | **REMOVED 2026-08-25** | — | Sample clock's legacy equivalent is a manual calibration dialog (`ClockAdj.cpp`), not a passive readout, and this port's own Slant ppm row above is already the modern automatic equivalent; Sync window is static across a decoder instance's lifetime and its old placeholder value matched no real ported constant; Drop-line has zero legacy grounding, same class as the already-removed Frame Metadata "Dropped lines" row. VIS threshold survives — planned to return as a real plain row, not behind this disclosure (same plan, item 6). |

**Input chain card** (`MainWindow.axaml:280-315`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Device | REAL | `:290` | `CaptureDeviceNameDisplay` (`.cs:179`), `CaptureDeviceName ?? "—"`. |
| ~~Squelch~~ / ~~Noise floor~~ | **REMOVED 2026-08-25** | — | Legacy's only "squelch"-labeled control is a mistranslation of the VIS-lock sensitivity threshold (`Option.cpp`'s Spanish "Nivel de Squelch" binds to `m_SenseLvl`, the same quantity as the Sync & Slant card's planned "VIS threshold" row), not a real RF squelch. Noise floor has zero legacy equivalent anywhere, same undefined-measurement class as the already-removed Signal Quality SNR rows. |
| BPF | REAL — **wired 2026-08-25** | `:315` | `RxBpfDisplay` (`RxImagePaneViewModel.cs`), restart-only construction-time read of `ISstvDecoder.RxBpfPreset` (previously only test-only exposed as `RxBpfPresetForTests`, now a real public member). Preset NAME only, not a cutoff figure — the locked-filter cutoff differs from the search/pre-lock cutoff, so a single number would be wrong whenever this reads while unlocked. |
| Notch | PLACEHOLDER | `:293` | `"—"` (`en.json:168`). No notch filter exists in `Core.Sstv`. |
| AGC | REAL | `:294` | `AgcGainDisplay` (`.cs:525`). |
| Buffer | REAL | `:295` | `BufferedSampleCountDisplay` (`.cs:505`), "N samples · M XRUN". |
| Clipping | REAL | `:296` | `ClippingDisplay` (`.cs:513`). |
| Noise floor | PLACEHOLDER | `:297` | `"—"` (`en.json:173`). |
| ~~Level L / Level R values / meters~~ | **REMOVED 2026-08-25** | — | Blocked on a real architecture decision (does stereo capture belong in this mono-only-demod port at all), not just wiring. Moved to `spec/14-roadmap.md`'s "Explicitly deferred beyond v1" list. |

**Signal quality card** (`MainWindow.axaml:330-347`)

| Control | Class | File:line | Note |
|---|---|---|---|
| ~~SNR-per-line plot~~ | **REMOVED 2026-08-25** | — | No per-line SNR exists anywhere in the decode pipeline, and no defined concept of what it should mean (`spec/17-rx-telemetry-feasibility.md`). Row and locale keys deleted, not just unwired. |
| ~~Min/Max~~ | **REMOVED 2026-08-25** | — | Depended on the same undefined SNR concept above. |
| ~~Luminance histogram plot~~ | **REMOVED 2026-08-25** | — | No histogram computation exists; same undefined-concept category. |
| Clip Lo/Hi | REAL | `:341` | `ClipLoHiDisplay` (`.cs:372`) — image-domain pixel-luminance statistic, not audio DSP. |
| Sync tone | REAL | `:342` | `SyncToneDisplay` (`.cs:495`). |
| ~~Black tone / White tone~~ | **REMOVED 2026-08-25** | — | Not a well-defined measurement even in principle, unlike Sync tone. Row and locale keys deleted, not just unwired. |

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
| Grid / dist | REAL — **wired 2026-08-25** | `:687` | `GridDisplay` (`.cs:326`) computes the distance half via `MaidenheadLocator.TryComputeDistanceBearing(OperatorGrid, LookupGrid)` + `FormatDistance`; `OperatorGrid` loaded once at construction via `ISstvSessionService.GetOperatorGridAsync` (new). Falls back to `"--"` if either grid is missing/malformed, never throws. |
| Frequency | REAL — **wired 2026-08-25** | `:722` | Ancestor-relative binding to `RadioStatus.FrequencyDisplayOrPlaceholder` (`RadioStatusViewModel.cs`, new) — a second, narrower property than the status bar's own `FrequencyDisplay`, since that one deliberately shows a `"000.000.000"` idle readout unsuitable for this card's `"—"` convention. Live VFO, not latched to the displayed frame's own receive frequency (documented tradeoff — `ReceiveHistoryEntry` carries no frequency field to latch from). |
| Mode / VIS | REAL — **wired 2026-08-25** | `:743` | Binds `DetectedModeDisplay` (`.cs:351`), same `"{DisplayName} — VIS {VisCode}"` property already used by the Mode panel's own ComboBox. For a forced-mode reception this shows the locked mode's expected VIS code, not a raw decoded byte (none is captured anywhere in the RX pipeline). |
| Started | REAL | `:690` | `StartedDisplay` (`.cs:379`). |
| ~~SNR / slant~~ | **REMOVED 2026-08-25** | — | No defined concept for SNR on an FM-demodulated signal (same open question as the Signal Quality card's SNR row); slant half was redundant with the real Sync & Slant card. Row and locale keys deleted, not just unwired. |
| ~~OCR confidence~~ | **REMOVED 2026-08-25** | — | OCR has no legacy precedent and is user-deferred to "maybe one day" (`spec/14-roadmap.md`'s "Explicitly deferred beyond v1"); row, locale keys, and the decode-log "· OCR" label suffix all removed, not just unwired. |
| ~~Dropped lines~~ | **REMOVED 2026-08-25** | — | `spec/17-rx-telemetry-feasibility.md` classifies this as likely not a clean concept given the port's per-pixel decode approach; no counter exists anywhere in the decoder. Row and locale keys deleted, not just unwired. |
| File size | REAL | `:694` | `FileSizeDisplay` (`.cs:385`); only populates once the frame's own save completes, otherwise `"—"`. |
| Note `TextBox` | REAL | `:709` | `Text="{Binding Note}"` (`.cs:102`), `IsEnabled="{Binding CanEditFrameMetadata}"` (`.cs:95` — true once `_currentEntryId` is correlated via `OnHistoryRecorded`, `.cs:921`); debounce-persists through the real `IReceiveHistoryStore.SetNoteAsync` (`PersistNoteDebouncedAsync`, `.cs:963`). **This closes the previous revision's only remaining PARTIAL finding.** |
| Override callsign `TextBox` | REAL | `:711` | `Text="{Binding OverrideCallsign}"` (`.cs:270`). **Nit:** its `Watermark` is `Panes.RxFrameMeta.CallsignValue` = `"EA7KDT"` (`en.json:220`) — a real-looking callsign as watermark text; low severity (watermarks are visually distinct) but it is the last surviving instance of that literal. |
| Lookup QRZ button | REAL | `:713` | `LookupQrzCommand` (`.cs:1101`) → `ILogbookSessionService.LookupCallsignAsync` → real `IQrzCallsignLookup` HTTP round-trip. |
| Flag `ToggleButton` | REAL | `:720` | `IsChecked="{Binding IsFlagged}"` (`.cs:107`), same `CanEditFrameMetadata` gate; persists through `IReceiveHistoryStore.SetFlaggedAsync` (`PersistFlaggedAsync`, `.cs:1018`). |
| Frame-metadata error banner | REAL | `:722-725` | `FrameMetadataErrorMessage` (`.cs:116`). |
| QRZ-lookup error banner | REAL | `:726-729` | `QrzLookupErrorMessage` (`.cs:317`) — this is where the "QRZ disabled/unconfigured" case surfaces, rather than graying out the button. |

**Unattended RX card** — **REMOVED 2026-08-25.** No design exists for what "watching" or "scanning"
should mean here; moved to `spec/14-roadmap.md`'s "Explicitly deferred beyond v1" list instead of
left as a placeholder with no defined target.

**Session frames card** — **REMOVED 2026-08-25.** No per-session frame-log concept has ever been
designed (distinct from the real `ReceiveHistoryStore`, which persists across sessions). Moved to
`spec/14-roadmap.md`'s "Explicitly deferred beyond v1" list.

---

## Transmit tab

`MainWindow.axaml:836-876`, `ColumnDefinitions="236,*"` (two columns — was `236,*,312` until the
third, right-hand column's sole remaining card, Mode-timing-reference, was removed 2026-08-25, user
decision; the editor column absorbs the freed width automatically). Left = `TxControlsPaneView`,
centre = `ActiveEditor` (a nullable `TxImageEditorPaneViewModel` — but see below: the editor is now
auto-opened at startup, so the centre column is no longer empty on first landing). No right column
remains — Queue/TX-log/Recently-sent (removed 2026-08-25, "maybe one day") and Mode-timing-reference
(removed 2026-08-25, no replacement, just freed space for the editor) all gone.

### Left column — `TxControlsPaneView.axaml` / `TxControlsPaneViewModel.cs`

**TX mode card** (`:26-143`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Auto / Manual segment | REAL | `:35-36` | `AutoFollowRxMode` (`TxControlsPaneViewModel.cs:217`), persisted to `TxPaneUiSettings`. |
| "Auto picks" caption value | REAL (2026-08-25) | `:56` | `AutoPicksText` (`.cs`) — selected mode's name while Auto, "Manual selection" while Manual. |
| Quick-mode pill grid (16 buttons) | REAL | `:59-76` | `QuickSelectModeCommand` (`.cs:808`), real mode ids as `CommandParameter`; sets `SelectedMode` on this pane (distinct from the RX tab's grid, which calls `ForceMode`). |
| Mode `ComboBox` | REAL | `:77-86` | `AvailableModes` / `SelectedMode` (`.cs:163`), `IsEnabled="{Binding CanChangeSourceOrMode}"` (`.cs:775`). Genuinely drives the encode pipeline. |
| Selected | REAL | `:93` | `SelectedMode.DisplayName`. |
| Duration / Geometry / VOX tone / VIS header | REAL (2026-08-25) | `:97`, `:101`, `:105`, `:109` | `DurationText`/`GeometryText`/`VoxToneText`/`VisHeaderText` (`.cs`), all off `SelectedMode`; VOX tone and VIS header route through new `ISstvSessionService.GetLeaderToneDurationMs`/`GetVisHeaderInfo` (backed by `AnalogFmSstvEncoder`/`VisHeader`). |
| Favorites row / "Edit favorites…" flyout | REMOVED (2026-08-27) | — | Direct user request, once the quick-mode grid itself became right-click-reassignable (`ReassignQuickModeSlotCommand`) it had no remaining use. `TxPaneUiSettings.FavoriteModeIds` and the `FavoriteModeOptionViewModel`/`FavoriteModeButtonViewModel` types are gone too. |

**Identification card** (`:149-167`) — **REAL.**

| Control | Class | File:line | Note |
|---|---|---|---|
| FSK ID | REAL | `:156` | `FskIdDisplay` (`.cs:322`). |
| CW ID | REAL | `:160` | `CwIdDisplay` (`.cs:327`). |
| Tail | REAL | `:164` | `TailDisplay` (`.cs:337`). |

All three reflect the current effective Options configuration via
`ISstvSessionService.GetStationIdTransmitOptionsAsync`. **Fixed 2026-08-25**: previously loaded
once at construction and never re-read while the pane stayed open (changing Identification
settings in Options and returning here showed stale values until restart); now also re-loaded when
the Options dialog closes (`MainWindow.axaml.cs`'s `OptionsRequested` handler, same fix as the
header callsign chip's own `LoadCallsignAsync`). Same fix applied to the Output card's Device row
below (`OutputDeviceName`).

**Output card** (`:177-265`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Drive slider + value | REAL | `:184-189` | `RadioStatus.TxVolumePercent` (`RadioStatusViewModel.cs:72`), debounce-persisted via `ISstvSessionService.SetTxVolumePercentAsync` (`.cs:725`). |
| Output device | REAL | `:194` | `OutputDeviceNameDisplay` (`TxControlsPaneViewModel.cs:286`), `OutputDeviceName ?? "—"`. |
| Power / ALC / SWR rows | REAL (capability-gated) | `:196-207` | `LivePowerPercent` / `LiveAlcLevel` / `LiveSwrRatio`, each row `IsVisible`-gated on the real `ShowPowerMeter`/`ShowAlcMeter`/`ShowSwrMeter` capability flags (`.cs:652-654`). Rows vanish rather than showing a placeholder on an unsupported rig. |
| SWR auto-cutoff toggle | REAL | `:220-224` | `SwrCutoffEnabled` (`.cs:263`), `IsEnabled="{Binding ShowSwrMeter}"`. |
| SWR threshold | REAL | `:226` | `SwrCutoffThreshold` (`.cs:266`). |
| Tune drive | REAL (2026-08-25) | `:243` | `RadioStatus.TxVolumeDisplay` — same value as the Drive slider above it. |
| Tone map | REAL | `:247` | `ToneMapText` (`.cs:174`), static per-mode `SstvModeDefinition.LuminanceMinHz/MaxHz`. |
| TX clock | REAL (2026-08-25) | `:251` | `TxClockText` — elapsed transmit time while transmitting, idle placeholder otherwise. |
| ~~Monitor audio~~ | **REMOVED 2026-08-25** | — | No legacy precedent and no tappable TX audio signal. Moved to `spec/14-roadmap.md`'s "maybe one day" list; row and locale keys deleted, not just unwired. |
| ~~Occupied BW~~ | **REMOVED 2026-08-25** | — | Researched with a full implementation plan (`~/.claude/plans/wandering-glowing-otter.md`) then deliberately abandoned after design review (commit `c8b99ad`). Moved to `spec/14-roadmap.md`'s "maybe one day" list; row and locale keys deleted, not just unwired. |
| POWER/ALC meters | REAL (2026-08-25) | `:262-263` | Fill-bar meters (`Atoms.axaml`'s `IndustryMeter` atom) off the already-real `LivePowerPercent`/`LiveAlcPercentDisplay`, replacing the previously-empty hatch panel. `LiveAlcPercentDisplay` also fixed a real scale bug: `RadioState.AlcLevel` is 0.0-1.0, not 0-100 like `PowerPercent` — the ALC text row above it was rendering the raw fraction as if it were a percentage. |

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

### Right column — gone entirely, 2026-08-25

The whole third column was removed, not just its cards — the centre (editor) column takes the
freed width instead, since it was already the `*` column. Four cards lived here at various points;
all four are now gone.

| Card | Class | File:line | Note |
|---|---|---|---|
| ~~Queue~~ | **REMOVED 2026-08-25** | — | No queueing feature exists. Card and `Panes.TxQueue.*` locale keys deleted, not just an empty state. Moved to `spec/14-roadmap.md`'s "maybe one day" list. |
| ~~Mode-timing-reference table~~ | **REMOVED 2026-08-25** | — | Was fully real (`TxControlsPaneViewModel.GetFrameSeconds`, fully computed from `SstvModeDefinition.LineDurationMs`/`ImageHeight`, zero new data) — removed anyway, direct user instruction, to give the editor column its width back rather than for any correctness reason. `ModeTimingRows`/`ModeTimingRowViewModel` and the `Panes.TxControls.ModeTiming*` locale keys deleted from the ViewModel along with it (nothing else referenced them). |
| ~~TX log table~~ | **REMOVED 2026-08-25** | — | No logging-of-sent-frames feature exists. Table, TX time today/Duty cycle rows, and `Panes.TxLog.*` locale keys deleted, not just header-only. Moved to `spec/14-roadmap.md`'s "maybe one day" list. |
| ~~Recently sent~~ | **REMOVED 2026-08-25** | — | No send-history feature exists. Card and `Panes.TxRecentlySent.*` locale keys deleted, not just an empty state. Moved to `spec/14-roadmap.md`'s "maybe one day" list. |

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
| Sort chip | **REMOVED 2026-08-26** | — | User decision "maybe one day." Was a static `Border` asserting an active sort order that never existed anywhere in `RxHistoryPaneViewModel`. `Panes.RxHistory.SortChip` locale key deleted; see `spec/14-roadmap.md`'s backlog. |
| Size chip | **REMOVED 2026-08-26** | — | Same decision, same session as Sort chip above. Was a static `Border` asserting an active thumbnail-size setting that never existed. `Panes.RxHistory.SizeChip` locale key deleted; see `spec/14-roadmap.md`'s backlog. |
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
| Grid / distance | **REMOVED 2026-08-26** | — | User decision "maybe one day." Was a static placeholder — the Receive tab's OWN Grid/dist row is real (`RxImagePaneViewModel.GridDisplay`, wired 2026-08-25), but its value comes from a live FSK station-ID decode at reception time, never persisted onto `ReceiveHistoryEntry` — nothing to show for a past entry without a real persistence change. `Panes.RxHistory.GridDistLabel`/`GridDistValue` locale keys deleted; see `spec/14-roadmap.md`'s backlog. |
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
`MainWindow.axaml:53`, always visible above the tabs.

**VFO card** (`:43-152`)

| Control | Class | File:line | Note |
|---|---|---|---|
| "VFO A · RX · M1" kicker | **FAKE-LIVE** (mild) | `:48` | `RadioStatus.VfoCaption` = `"VFO A · RX · M1"` (`en.json:64`). A static caption asserting VFO A, RX state, and memory M1 — none of which are tracked. Decorative in intent, but it reads as rig state. |
| Frequency readout (40 pt) | REAL | `:53-58` | `FrequencyDisplay` (`RadioStatusViewModel.cs:37`), assigned in `OnStateChanged` (`.cs:282`) from `IRadioSessionService.StateChanges`. |
| USB / LSB / FM sideband segment | REAL, **wired 2026-08-26** | `:62-79` | Bound to `RadioStatusViewModel.IsSidebandUsb/Lsb/Fm` (three plain derived booleans over the already-real `SelectedRadioMode`). `SelectedRadioMode`'s own CAT wiring (`SetModeAsync`) predates this fix — the stub was this segment's own binding, not a missing concept. |
| UTC clock | REAL | `:88-91` | `UtcClockDisplay` (`.cs:114`), 1 s `DispatcherTimer` (`UpdateUtcClock`, `.cs:265`). |
| BW pill | REAL, **wired 2026-08-26, capability-gated** | `:106-121` | `BandwidthDisplay`/`CanReadBandwidth`/`CanSetBandwidth`/`SetBandwidthCommand` (`RadioStatusViewModel.cs`) — Hamlib/rigctld both genuinely carry a passband value on their existing mode calls, flrig deliberately doesn't (unreliable Hz readback); falls back to the old disabled stub pill when neither capability is present. |
| Split pill | **REMOVED 2026-08-26** | — | User decision "maybe one day." No legacy precedent (`cradio.h`/`cradio.cpp`/`Main.cpp`/`Option.cpp` have zero mentions of it — only trace anywhere in legacy is an unused property on OmniRig's auto-generated COM binding, `OmniRig_OCX.h`, never read/written by YONIQ's own code); also raises its own unresolved design question (how a second TX frequency fits this port's single-`FrequencyHz` model). Pill and `RadioStatus.SplitValue` locale key deleted; see `spec/14-roadmap.md`'s backlog. |
| CAT link lozenge | REAL | `:113-122` | `CatLinked` (`.cs:94`), driven by `IRadioSessionService.ConnectionEvents` (`.cs:344`); LED + a real two-state text swap. |
| Step / RIT pills | **REMOVED 2026-08-26 (both)** | — | User decision "maybe one day," both times. No legacy precedent for either (Step: `cradio.h` has zero mentions; RIT: same zero-mentions result across `cradio.h`/`cradio.cpp`/`Main.cpp`/`Option.cpp`, only trace anywhere in legacy is the same unused OmniRig COM property pattern as Split above). Pills and `RadioStatus.StepValue`/`RadioStatus.RitValue` locale keys deleted; see `spec/14-roadmap.md`'s backlog. |
| Rig-meters pill | REAL | `:146-149` | `RigMetersDisplay` (`.cs:146`), joined from the real polled `RadioState.SwrRatio`/`AlcLevel`/`PowerPercent`; reset to `"—"` when CAT drops (`.cs:364`). **Reclassified from STUB** — the previous revision's "no rig-meters concept exists" premise was checked against `RigctldClientProtocol`/`HamlibRadioProtocol` and found false. |

**Favourites card** (`:163-256`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Preset recall buttons | REAL | `:200-226` | `Presets` (`.cs:269`); each `SelectCommand`/`CommandParameter` applies frequency **and** mode via `IRadioSessionService`. |
| Store current | REAL | `:177-181` | `StoreCurrentPresetCommand` (`.cs:479`), `CanStoreCurrentPreset` = `_currentFrequencyHz > 0` (`.cs:500`). |
| Hint caption | PLACEHOLDER (decorative) | `:251-253` | `"Click a preset to recall it"` (`en.json:73`) — accurate instructional prose, not data. |
| Edit list / Import / Scan | STUB (disabled) | `:242-250` | Whole group `IsEnabled="False"` + tooltip. A real presets editor (`EditorRows`/`AddPresetRowCommand`/`SavePresetsCommand`) exists on the VM but is deliberately unmapped per a direct prior user request; disabling was the honesty fix. |

**Transceiver card** (`:276-341`) — RX level and Pwr rewired 2026-08-24, direct user redesign;
see each row's own note below.

| Control | Class | File:line | Note |
|---|---|---|---|
| Receiving toggle | REAL | `:294-298` | `IsReceiving` (`.cs:84`); `Opacity` bound to `IsCapturePausedForTx` (`.cs:127`) so it visually dims while capture is paused for a local transmission, without changing the toggle's own contract. |
| Halt button | REAL | `:299-301` | `HaltReceivingCommand` (`.cs:716`). Styled `IndustryBtnDanger` (2026-08-24, direct user request) to match the Transmit pane's own "Stop TX" button — was `IndustryBtnSecondary` (plain gray), a deliberate mockup-fidelity call from an earlier phase that this direct request supersedes. |
| RX level meter | REAL | `:310-327` | **Redesigned 2026-08-24, direct user correction of a same-session OS-mixer-volume detour that was fully reverted.** No longer a rig-signal-strength readout at all: `RxLevelFillPercent` (`RadioStatusViewModel.cs:116`) is a plain WSJT-X-style incoming-AUDIO-level meter driven by `ISstvSessionService.RawInputPeakLevel` (the raw captured buffer's own peak amplitude, polled on a 250 ms `DispatcherTimer`, `.cs:37`) — not `RadioState.SignalStrengthDb`, which the previous survey revision (2026-08-22) had this bound to. Fill color switches green/red via `RxLevelInGoodRange` (`.cs:140`), using the same `DoubleToStarGridLengthConverter` two-column-fill technique as before. |
| RX level value | REAL | `:328` | `RxLevelDisplay` (`.cs:121`) — a plain 0–100 number now, no `"%"` suffix (dropped 2026-08-24, direct user request: read as redundant next to the bar). |
| Pwr slider + value | REAL | `:338-340` | Renamed from "TX volume"/"Drive" to "Pwr" (`RadioStatus.TxVolumeLabel`/`Panes.TxControls.DriveLabel`, both now "Pwr"). `TxVolumePercent` (`.cs:82`) is app-internal TX playback gain, same shape WSJT-X's/fldigi's own Pwr controls use — **not** OS device volume; an earlier same-session detour through real OS-mixer volume control for both RX and TX was fully reverted per direct user correction, see this row's git history for the full back-and-forth. `TxVolumeDisplay` (`.cs:98`) swaps to a muted-speaker glyph (U+1F507) instead of the percent number when `TxIsMuted` (`.cs:92`) reports the resolved TX device's real OS mute state (query-only, via the new `IAudioDeviceMuteQuery` native shim path — WASAPI/PulseAudio/ALSA/CoreAudio — no setter exists; mute is independent of the Pwr gain). Same underlying `TxVolumePercent` value as the Transmit tab's own Pwr slider and the new Options → Radio/CAT tab's own Pwr slider (three sliders, one persisted setting, each independently live-loaded — see the Options-window section below). |
| Error / maintenance messages | REAL | `:357-367` | `ErrorMessage` (`.cs:56`) and the deliberately separate `MaintenanceMessage` (`.cs:69`). |

---

## Menu bar, tab-strip chips, status bar (`MainWindow.axaml`)

**Menu bar** (`:61-110`) — brand label ("SSTV / CONSOLE") removed entirely 2026-08-24, direct user
request: it was purely decorative, no bound state, so nothing else changed when it was dropped
(the `Menu` itself stayed in the row's own `"*"` star column, `:67`; the now-empty `Auto` column 0
just collapses to zero width).

| Item | Class | File:line |
|---|---|---|
| File > Open image (Ctrl+O) | REAL | `:72` — `TxControls.SelectImageCommand`, `IsEnabled="{Binding TxControls.CanChangeSourceOrMode}"` (a disabled `MenuItem` also suppresses its own `InputGesture`, closing the accelerator). |
| File > Save frame as (Ctrl+S) | STUB (disabled) | `:73` |
| File > Exit | REAL | `:75` — `ExitCommand`. |
| Configurations > Storage & naming, Macros | STUB (disabled) | `:82-83` |
| Calibration > Clock calibration, Loopback self-test, Tone generator, Slant reference | STUB (disabled) | (line numbers shifted by the Rig & PTT removal below — re-verify at the next docs audit) |
| Tools > Re-decode from WAV, Export session log | STUB (disabled) | (line numbers shifted, see above) |
| Help > About | REAL | `OpenAboutCommand` (`MainViewModel.cs:179`) → `AboutRequested` → `MainWindow.axaml.cs:173`. |
| Options… | REAL | `OpenOptionsCommand` (`MainViewModel.cs:159`). |
| Callsign chip | REAL | `CallsignDisplay` (`MainViewModel.cs:137`), `"N0CALL"` fallback until set in Options. Opens Options straight to the Station tab (`OpenOptionsToTxTabCommand`, `.cs:170` — internal name unchanged by the tab's 2026-08-24 "TX"→"Station" rename, see the Options-window section below). |

Menu total: 2 real File items + Help>About + Options = 4 real; 9 disabled stubs. The three
Options-duplicating stubs (Station, Audio devices, CAT interface) were pruned outright rather than
left disabled. Rig & PTT (PTT method/Frequency memories/Test PTT, all 3 disabled stubs) was removed
outright too, 2026-08-25, per direct user request — same treatment as the Options-duplicating stubs
above, not left disabled.

**Tab-strip status chips** (`:1473-1497`, right-aligned in the tab band)

| Chip | Class | File:line | Note |
|---|---|---|---|
| AUTO-DETECT | PLACEHOLDER (decorative) | `:1475-1477` | `"AUTO-DETECT"` (`en.json:45`) — a static label that happens to be accurate (auto-detect is the only mode-selection behaviour). |
| Detected-mode LED + lozenge | REAL | `:1478-1483` | `RxImage.DetectedModeDisplay`; LED `.active` conditional on `DetectedMode` being non-null, so it isn't green during the "—" empty state. |
| Auto-correct chip | REAL | `:1484-1486` | `RxImage.AutoCorrectDisplay`. |
| Slant chip | REAL | `:1487-1489` | `RxImage.SlantPpmStatusBarDisplay` (`.cs:398`). |
| SNR chip | **REMOVED 2026-08-26** | -- | Status-bar, tab-strip, and Gallery Mode/SNR-suffix SNR chips all removed outright; real feature scoped as a backlog item, `spec/14-roadmap.md`. |
| AUTOSAVE ON chip | STUB (disabled) | `:1493-1496` | `IsEnabled="False"`, `Opacity="0.45"`, `NotImplemented` tooltip. Text still literally reads `"AUTOSAVE ON"` (`en.json:47`) — the disabled/dimmed treatment is the mitigation, but the wording still asserts a state. Worth changing to `"—"`; noted, not overstated. |

**Status bar** (`:1533-1610`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Frames today | REAL | `:1541-1543` | `RxHistory.FramesTodayDisplay` (`RxHistoryPaneViewModel.cs:251`). |
| Log size | REAL | `:1544-1546` | `Logbook.LogSizeDisplay` (`LogbookPaneViewModel.cs:170`). |
| Receiving LED + lozenge | REAL | `:1551-1556` | `RadioStatus.IsReceiving`, with a `healthyTint` class binding. |
| TX-ERROR LED + lozenge | REAL, **renamed from TX-INHIBIT 2026-08-26** | `:1566-1572` | `TxControls.ErrorMessage != null`, amber `attentionTint`; tooltip states the narrow meaning honestly (`en.json:24`). Renamed per direct user correction — the chip never actually inhibits/blocks a new Transmit, it's a status readout of whether the last attempt errored or hit an automatic SWR cutoff. |
| TX-KEYED LED + lozenge | REAL | `:1573-1579` | `RadioStatus.IsKeyed` (`RadioStatusViewModel.cs:109`) — real rig PTT readback, cleared when CAT drops; red `dangerTint`. Tooltip states the polling lag and the VOX/DTR caveat (`en.json:31`). |
| Frequency / Mode | REAL | `:1581-1586` | `RadioStatus.FrequencyDisplay` / `ModeDisplay`. |
| Memory tag | PLACEHOLDER | `:1587-1589` | `"—"` (`en.json:32`). |
| Detected mode | REAL | `:1590-1592` | `RxImage.DetectedModeText` (`.cs:347`). |
| Line progress | REAL | `:1593-1595` | `RxImage.LineProgressText`. |
| SNR | PLACEHOLDER | `:1596-1598` | `"—"` (`en.json:35`). No per-line SNR computation exists anywhere in the decode pipeline. |
| Slant | REAL | `:1599-1601` | `RxImage.SlantPpmStatusBarDisplay`. |
| Buffer | REAL | `:1602-1604` | `RxImage.BufferedSampleCountStatusBarDisplay` (`.cs:509`). |
| Disk | PLACEHOLDER | `:1605-1607` | `"—"` (`en.json:41`). |

---

## Options window (`OptionsWindowView.axaml`, `OptionsWindowViewModel.cs`)

**Nine tabs**, in source order: General, Audio, Radio, **Station** (renamed from "TX" 2026-08-24,
direct user request — internal identifiers like `TxTabIndex`/`OpenOptionsToTxTabCommand` are
unchanged, only the on-screen label moved), Decode, Identification, Advanced, QRZ.com,
**Forwarding**. Window-level machinery is REAL throughout — `SaveCommand` (`.cs:1209`) persists via
`OptionsSettingsService`, `CancelCommand` discards, per-section resets and the confirm-gated
`RequestResetAllCommand`/`ConfirmResetAllCommand`/`CancelResetAllCommand` (`:817-831`) all work.
`SaveAsync` itself was fixed 2026-08-24 (auditor-caught blocker): it used to rebuild settings from
the snapshot captured when the dialog opened, so a field written by something OTHER than this
dialog while it stayed open (exactly what the new Radio tab's own live Pwr slider does, see below)
got silently reverted on Save — now re-reads fresh from disk first, closing that staleness risk for
every section, not just Pwr.

**Citation caveat for this Options-window section:** `OptionsWindowViewModel.cs` has grown
substantially across several rounds of unrelated feature work since this doc's 2026-08-22 baseline
(Hamlib auto-detect, QRZ tab, Forwarding tab, and now Pwr/Tune) — confirmed by spot-checking several
`.cs:` line numbers below against the current file and finding drift with no consistent constant
offset (unlike the `.axaml` citations, which drift by a verified constant per tab and were corrected
accordingly). The `.axaml` (view) line citations below were re-verified for the Radio and Station
tabs directly, and for Decode/Identification/Advanced/QRZ/Forwarding via that verified per-tab
constant-offset shift; the `.cs:` (ViewModel) citations for General/Audio/Decode/Identification/
Advanced/QRZ/Forwarding were **not** individually re-verified in this pass and may be stale — a full
re-derivation (the same kind this doc's own 2026-08-22 revision did once already) would be needed to
guarantee every one.

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

### Radio tab (`:187-373`) — grew substantially this pass (Hamlib auto-detect + a new Pwr/Tune card)

| Control | Class | File:line | Note |
|---|---|---|---|
| Backend radios (None / rigctld / Hamlib) | REAL | `:197-208` | `IsNoneBackendSelected`/`IsRigctldBackendSelected`/`IsHamlibBackendSelected` (`.cs:359,371,383`); all three backends registered in DI. |
| OmniRig radio | STUB (disabled) | `:211-215` | `IsEnabled="False"`; its own dedicated help tooltip (not the generic one) documents it as a speculative 5th backend. Correctly stays stub. |
| rigctld Host / Port | REAL | `:221`, `:225` | `RigctldHost` / `RigctldPort`, persisted; shown only while `IsRigctldSelected`. |
| Test connection button + status | REAL | `:234-240` | `TestRigctldConnectionCommand` (`.cs:413`) — tests the *currently typed* (not-yet-saved) host/port via a disposable connection, never disturbing the live session. |
| Hamlib library path + Browse/Auto-detect/Test path buttons + rig-model `ComboBox` | REAL | `:246-297` | **New since the previous survey revision; was fully undocumented.** `HamlibLibraryPath`/`BrowseHamlibLibraryCommand`/`AutoDetectHamlibCommand`/`ProbeHamlibCommand`/`HamlibRigModels`/`SelectedHamlibRigModel`, backed by the new `IHamlibDiscoveryService`/`HamlibDiscoveryService` (probes a candidate library path off-thread via `Task.Run`, lists real Hamlib rig models via `rig_list_foreach`). `HamlibDiscoveryStatusMessage` reports probe results inline. |
| Hamlib Model / Serial port / Baud / PTT type | REAL | `:299-320` | All persisted; shown only while `IsHamlibSelected` (`.cs:395`). |
| RTS-on-RX / PTT-lock checkboxes | STUB (disabled) | `:329-334` | Both gate legacy's raw-serial RTS-pin PTT keying, a family explicitly excluded from this port; the real PTT path goes through Hamlib/rigctld/flrig. Correctly stays stub. |
| **Pwr slider + value, Tune button** | REAL | `:346-368` | **New 2026-08-24, direct user request** ("TUNE button that works like WSJTX ... so that we can set our Pwr slider to the TX power output we want"). `TxVolumePercent`/`TxVolumeDisplay` (`.cs:621,626`) are a SEPARATE live-loaded copy of the same `AudioDeviceSettings.TxVolumePercent` setting the header's own Pwr slider edits — not instantly two-way-bound to it while both happen to be open, only synced on each one's own load/save. Deliberately **not** gated behind this dialog's usual Save button (unlike every other field on this tab): it debounce-persists immediately, same shape as the header slider, because the whole point is dragging it while `TuneCommand` (`.cs:714`) is actively playing a tone and watching the radio's own power meter. Tune is a real start/stop TOGGLE (`IsTuning`/`TuneButtonLabel`, `.cs:695,700`) — WSJT-X-style, not fire-and-forget — capped at a 30 s safety duration (`MaxTuneDuration`) either way, closes cleanly if the dialog itself closes mid-tone (`StopTuneIfActive`, called from `OptionsWindowView.axaml.cs`'s own `Closed` handler). Needed a real backend change to work at all: `SstvSessionService`'s Pwr gain used to be captured once when PTT keyed and frozen for the whole call, so dragging Pwr mid-tone had no effect until the next call — now re-read from a live volatile field once per playback chunk, reviewed by the project's own auditor subagent (a genuine concurrency change, not a port) and shipped after fixing one blocker that review found (the Options dialog's own Save button was silently reverting a live Pwr change made while the dialog stayed open — see the intro note above this section). |
| Reset section | REAL | `:370` | `ResetRadioToDefaultCommand`. |

### Station tab (`:375-413`) — renamed from "Tx" 2026-08-24 (see this section's own intro note)

| Control | Class | File:line | Note |
|---|---|---|---|
| Callsign | REAL | `:384` | `Callsign`, persisted; also feeds the shell's menu-row chip. |
| Operator name / Operator grid | REAL | `:395`, `:399` | `OperatorName`/`OperatorGrid` → `OperatorSettings`. No legacy MMSSTV equivalent; added to back the TX editor's `{name}`/`{grid}` macro tokens. |
| Reset section | REAL | `:410` | `ResetTxToDefaultCommand` (internal name unchanged by the tab rename). |

The old disabled "QRZ lookup" placeholder was removed outright (see the in-file note, `:404-409`),
superseded by the real QRZ.com tab.

### Decode tab (`:426-513`) — **all real except one control**

| Control | Class | File:line | Note |
|---|---|---|---|
| Sense level (Very low / Low / High / Very high) | REAL | `:436-439` | `IsSenseLevel*Selected` (`.cs:434-482`) → `SstvDecoderSettings.SenseLevel` → `AnalogFmSstvDecoder.SenseLevelPresets`, a port of legacy `CSSTVDEM::SetSenseLvl`. Out-of-range persisted values fall back to preset 0 (matching legacy's `default:`), absent falls back to preset 1 (legacy's ctor default). |
| RX BPF sharpness (Normal / Wide / Sharp / Very sharp) | REAL | `:447-450` | `IsRxBpf{Off,Wide,Narrow,VeryNarrow}Selected` (`.cs:529-577`) → real `if(m_bpf)` bypass dispatch against a preset-parameterized `SearchBandpassFilter`; absent and out-of-range both clamp to Wide. |
| Demodulator type (PLL / Zero crossing / Hilbert) | REAL | `:458-460` | `IsDemodType*Selected` (`.cs:486-522`) → real runtime dispatch in `AnalogFmSstvDecoder`; absent and out-of-range both clamp to Hilbert. |
| **RX buffer (Off / On / Extended)** | REAL | `:468-470` | `IsRxBufferOffSelected`/`IsRxBufferOnSelected`/`IsRxBufferExtendedSelected` (`.cs:581,593,605`). **Reclassified from STUB** — the whole 9-phase RX-buffer subsystem, including this Options UI (Phase 9), has landed. The previous revision's separate "hardcoded-wrong-default (shows Off, real default On)" bug is also gone: the checked state is now bound, not literal. |
| ~~Auto-start (Off / On)~~ | **REMOVED 2026-08-25** | — | Its real legacy behavior (`SBAuto`, `Main.cpp:6042-6079`) is a live RX pause/resume toggle, not a settings-dialog checkbox — ported instead as the Receive tab's own real "Listening / Paused" segment in the Mode card (`MainWindow.axaml`), backed by `ISstvSessionService.SetAutoDetectPaused`/`ISstvDecoder.RequestAbandonReception`. Four rounds of plan-review went into that design (session-layer pause, decoder-side cleanup on a one-shot deferred command) — see `elegant-wondering-hinton.md`. Legacy's SAME toggle on the TX page (`TrackTxMode`) was already ported as `TxControlsPaneViewModel.AutoFollowRxMode`'s real Auto/Manual segment; it just got a `[?]` tooltip added this same batch. |
| Auto-stop on erratic/weak signal | REAL | `:491-492` | `AutoStopEnabled` → `SstvDecoderSettings.AutoStopEnabled`. Default OFF, matching legacy's fresh-install default. |
| Restart onto a stronger sync mid-reception | REAL | `:493-494` | `SyncRestartEnabled`. Default ON. |
| Auto-resynchronize during decode | REAL | `:500-501` | `AutoSyncEnabled`. |
| Auto-correct slant during decode | REAL | `:505-506` | `AutoSlantEnabled`, plus `IsEnabled="{Binding IsAutoSlantRowEnabled}"` (`.cs:623` = `RxBufferMode != Off`) — a faithful port of legacy's `CBASlant->Enabled = RGRBuf->ItemIndex ? TRUE : FALSE` (`Option.cpp:222`). |
| Reset section | REAL | `:508` | `ResetDecodeToDefaultCommand`. |

All decoder settings on this tab are **restart-required** — no live-reconfiguration path exists for
any DI-singleton-baked setting. Squelch/sense level is the most user-visible instance of that limit
(legacy applies it live).

### Identification tab (`:524-601`)

| Control | Class | File:line | Note |
|---|---|---|---|
| ID method: Off / CW | REAL | `:533-534` | `IsIdMethodOffSelected`/`IsIdMethodCwSelected` (`.cs:629,641`). |
| ID method: Sound file | STUB (disabled) | `:537` | Individually `IsEnabled="False"` (not the whole group) — `CwIdMode.SoundFile` is explicitly out of v1 scope. |
| CW text / CW frequency / CW speed | REAL | `:542`, `:546`, `:550` | `CwText` / `CwToneFrequencyHz` (100–3000) / `CwWpm` (10–50); the whole block `IsVisible`-gated on CW being selected. |
| Sound-file path + Browse | STUB (disabled) | `:559-560` | Both `IsEnabled="False"` + tooltip. |
| FSK encode / FSK decode | REAL | `:563-566` | `FskIdTxEnabled` / `FskIdRxEnabled`. |
| NR/RST enable + text | REAL | `:568-573` | `NrRstEnabled` / `NrRstText`, the text field `IsVisible`-gated on the checkbox. |
| VOX Off/On + Edit tone | STUB (disabled) | `:582-586` | Whole `WrapPanel` + button disabled; no VOX backend exists anywhere in this port. |
| Tune-satellite trigger | STUB (disabled) | `:592-593` | `IsEnabled="False"` + tooltip — this specific checkbox (auto-transmit once a Tune tone's duration elapses) stays unwired regardless of the note below. **Corrected 2026-08-24:** the previous revision's claim that Tune itself is "currently unmapped to any control anywhere in the UI" is now false — `RadioStatusViewModel`'s own `TuneFrequencyHz`/`TuneDurationSeconds`/`TuneCommand` (`.cs:143,146,607`) are still unmapped to any header control, but the Radio tab's new Pwr+Tune card (see that tab's own table above) gives Tune a real, working UI surface via a SEPARATE `OptionsWindowViewModel`-owned implementation, not this one. |
| Reset section | REAL | `:598` | `ResetIdentificationToDefaultCommand`. |

**Closed 2026-08-26:** the RX-side decoded NR/RST value (`RxImagePaneViewModel.DecodedNrRst`) is now
bound to a real "NR / RST" row on the RxFrameMeta card (`MainWindow.axaml`, `Panes.RxFrameMeta.NrRst`)
— was previously a real, populated property with no control bound to it anywhere.

### Advanced tab (`:607-690`) — **fully STUB, by deliberate decision**

Every control is `IsEnabled="False"` with `Options.NotImplemented.Help`, and the tab's own caption
(`:610`, `Options.Advanced.Caption`) states so on screen. Contents: PLL VCO gain / loop order / loop
cutoff / out cutoff (`:619-627`, hardcoded 2600/1/200/1200); Zero-crossing order / cutoff / smoothing
(`:639-645`) + Differentiator (`:647-648`); TX BPF / TX LPF toggles (`:657-660`) + TX sample-clock
offset (`:664`); Loopback Off/Internal/External (`:675-679`); Polynomial calibration (`:680-681`);
Clock-adjust and Level-calibration wizards (`:683-684`). Filter-response preview buttons from legacy
(`DispTxBpf` etc.) were deliberately omitted rather than stubbed — nothing real to preview.

The one row with a real, bounded path to being wired is TX BPF/LPF: `TxOutputBandpassFilter` is
already applied unconditionally, so these are a real user-toggleable bypass away — but it is
TX-encode-path code with over-the-air spectral consequences, so it carries the same review weight as
the rest of the tab. The old "PLL/Zero-crossing tuning is gated behind demod-type dispatch"
precondition is **met** (that subsystem shipped); these rows are now the sole remaining piece.

### QRZ.com tab (`:696-728`)

| Control | Class | File:line | Note |
|---|---|---|---|
| Enable QRZ.com lookup | REAL | `:704-705` | `QrzLookupEnabled`; gates `ILogbookSessionService.LookupCallsignAsync`'s live path. |
| Username / Password | REAL | `:707-711` | `QrzLookupUsername` / `QrzLookupPassword` (`PasswordChar="•"`), persisted plaintext with an on-screen hint (`:716`) stating the risk — no scoped-credential mode exists for this API. |
| Test button + status | REAL | `:718-720` | `TestQrzLookupCommand` (`.cs:1076`) → `IQrzCallsignLookup.TestCredentialsAsync` against the *current in-memory* fields; `TestQrzLookupStatus` readout. |
| Reset section | REAL | `:725` | `ResetQrzToDefaultCommand`. |

Deliberately **not** named "Logbook": the QRZ *Logbook upload* API (`QrzUploadSettings`) is a separate,
already-real backend that still has no Options UI of its own — a documented gap (`:692-695`).

### Forwarding tab (`:730-812`) — **new since the previous survey revision; previously undocumented**

ADIF-over-UDP multi-destination forwarding (generalizing the former GridTracker-only path).

| Control | Class | File:line | Note |
|---|---|---|---|
| Description / empty hint | REAL | `:738`, `:740-741` | Hint `IsVisible`-gated on `!AdifUdpDestinations.Count`. |
| Column-header row (Name / Host / Port) | REAL (layout) | `:767-773` | `IsVisible`-gated on the list being non-empty; uses invisible same-shape `CheckBox`/`Button` in columns 0/4 so `Auto` resolves identically to the data rows (Avalonia has no `SharedSizeGroup`) — see `:743-766`. |
| Per-destination rows: Enabled, Name, Host, Port, Remove | REAL | `:778-802` | `ItemsSource="{Binding AdifUdpDestinations}"` (`.cs:290`) over `AdifUdpDestinationRowViewModel`; `Enabled`/`Name`/`Host`/`Port` (1–65535) all two-way bound, `RemoveCommand` per row. |
| Add destination | REAL | `:804-805` | `AddAdifUdpDestinationCommand`. |
| Reset section | REAL | `:809` | `ResetForwardingToDefaultCommand`. |

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
| **PLACEHOLDER** (honest) | ~40, **updated 2026-08-25, not re-audited beyond the items below** | Receive Input-chain/Signal-quality/Frame-metadata/Unattended-RX unbacked rows, Gallery per-entry SNR/freq/grid + Sidecar/Disk, status-bar Memory/SNR/Disk. TX mode/output scaffolding rows (8) fixed 2026-08-25 (un-stub-TX-tab pieces 1-9, see the Left column table above), not counted here anymore. The "TX Outgoing-metadata card (9)" this row used to count was already stale before this pass — that card doesn't exist in `src/` (removed commit `010b60d`, 2026-08-24). TX Queue/Recently-sent/Session-frames empty states are gone too (removed outright, not placeholders, 2026-08-25). |
| **STUB** (disabled + tooltip) | ~44, **updated 2026-08-25, not re-audited beyond the items below** | Options Advanced tab (~17, whole tab), menu bar (12), Options Audio FIFO/priority (3), Options Radio OmniRig/RTS/PTT-lock (3), Options Identification Sound-file/VOX/Tune-sat (5), Options General colour buttons (7), Radio header sideband/BW/Split/Step/RIT/Edit-Import-Scan (~10), RX Abort/Re-decode (2, Reset/Advanced-timing removed 2026-08-25, see the Sync & Slant card table above), Gallery Re-decode + 14MHz filter (2), Decode-activity header-only table (1). Options Decode Auto-start removed entirely (its real behavior ported as a real control instead — see the Receive tab's Mode card table above). TX Recently-sent buttons and the TX-log header-only table removed entirely 2026-08-25 (see the Transmit tab's right-column table above), not counted here anymore. The 4 empty hatch plots this row used to count are also gone — 0 remain in `src/` (the TX POWER/ALC one became real meters 2026-08-25 in an earlier pass; no other hatch-panel usage exists). |
| **STUB (dead-interactive)** | **0**, **closed 2026-08-22** | Was 4 (Gallery Search `TextBox`, 14MHz/Unlogged/Flagged `ToggleButton`s). Search and Unlogged/Flagged are now real (client-side `FilteredEntries`, `RxHistoryPaneViewModel`); 14MHz is now an honest disabled stub instead (see the STUB row above) — no frequency field exists on `ReceiveHistoryEntry` to filter by, and adding one is a schema change out of scope for a dead-control fix. No dead-interactive controls remain anywhere in the app. |
| **FAKE-LIVE** | **1**, **down from 4** | Only the VFO caption `"VFO A · RX · M1"` (`RadioHeaderView.axaml:48`) remains. Gallery Sort/Size chips **removed 2026-08-26** (see their own rows above), not just mitigated. AUTOSAVE-ON chip's wording was **resolved 2026-08-26** (stub survey Tier 1) — confirmed the claim is simply true (RX images are unconditionally auto-saved, no setting gates it), needs no backing property. Gallery Storage Naming (`:1168`) is fixed as of 2026-08-22 — see its own PLACEHOLDER row above. The previously-flagged "4 latent" Advanced-timing values are moot — that whole disclosure (Sample clock/Sync window/Drop-line) was removed 2026-08-25, not left reachable, so that risk closed rather than materialized. |
| ~~**PARTIAL**~~ | **0**, **closed 2026-08-25** | Was 1 (Receive Frame-metadata "Grid / dist") — now REAL, see that card's own table above. |

**Fully real screens:** TX image editor (100 %, zero disabled/unbacked controls, and as of
2026-08-25 the tab's entire centre+right width). Logbook tab (100 %). About dialog. QSO-link
dialog. Options Forwarding tab. Options QRZ.com tab. Spectrum & waterfall card.

**Mostly real, mixed:** Transmit tab left column (mode/ID/output/stock/transmit machinery fully
real as of 2026-08-25 — un-stub-TX-tab pieces 1-9; no placeholder rows remain there). Gallery tab
(list/filter/storage/note/flag/link real; the filter row is the honesty gap). Radio header
(frequency/CAT/receiving/TX-volume/UTC/rig-meters/RX-level/sideband/BW real as of 2026-08-26;
split/step/RIT removed outright, no stubs remain on the VFO card; the persistent give-up-after-5
error text line was removed 2026-08-25, the must-acknowledge
popup is the only surface for that signal now). Receive tab Sync&Slant / Input-chain / Signal-quality
cards. Options General/Audio/Radio/Tx/Decode/Identification tabs.

**Mostly/fully stub:** Options Advanced tab (100 %). Receive tab Decode-activity, Unattended-RX and
Session-frames cards. Menu bar outside File > Open/Exit, Help > About, and Options. (Transmit tab's
right column — formerly Queue/TX-log/Recently-sent — no longer exists at all, removed 2026-08-25,
so it's dropped from this list rather than counted as stub.)

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
4. **`RxImagePaneViewModel.DecodedNrRst` — closed 2026-08-26.** Was real and populated with no control
   bound to it anywhere; now bound to a real "NR / RST" row on the RxFrameMeta card.
5. **Options → Decode settings are restart-only.** Every decoder toggle on that tab is baked into a DI
   singleton with no live-reconfiguration path. Sense level is the most user-visible case, since it's
   the control most likely to be adjusted while actively chasing a signal (legacy applies it live).
6. **Fixed 2026-08-25.** The Identification card in the TX pane used to be a construction-time
   snapshot (`TxControlsPaneView.axaml:145-148`) — it showed the Options values as they were when
   the pane was built, not as they are now. Now re-loaded whenever the Options dialog closes.
