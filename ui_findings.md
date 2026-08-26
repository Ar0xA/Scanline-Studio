# Scanline Studio UI findings

Source-only review from the perspective of an active ham-radio SSTV operator familiar with YONIQ, MMSSTV, and QSSTV. I reviewed the Avalonia views, view-models, localization text, and the relevant legacy/reference UI sources. I did not run the application, operate a radio, or change application code, so findings about visibility and interaction are based on declared layout and bindings rather than a live usability session.

## What I understand quickly

- The four top-level jobs—Receive, Transmit, Gallery, and Logbook—are the right primary navigation and form a clear overall mental model.
- Keeping rig state and frequency visible above every tab makes sense for station operation.
- Receive's left-to-right progression from controls/telemetry, through spectrum and image, to metadata/trace is conceptually sound.
- Transmit's split between mode/source/output controls and the image editor is also understandable.
- The TX editor is substantially more capable than a superficial mock-up: crop, overlays, templates, QSO fields, undo/redo, and a ready rack all have real backing behavior.
- Gallery supports actual selection, export, notes, flags, and filtering; Logbook supports actual QSO entry and ADIF import/export.

The main problem is not the basic tab structure. It is that important everyday actions are missing, hidden below the fold, or represented by controls whose appearance promises more than their behavior delivers. At the same time, unfinished and highly technical controls occupy a great deal of visible space.

## Tier 1 — obstructs a normal SSTV workflow

### 1. The fixed 1920-DIP minimum width excludes ordinary station displays

`MainWindow.axaml:18` sets `MinWidth="1920"`. That does not fit a 1366-, 1440-, or 1600-pixel display, and it does not fit a 1920-pixel display when desktop scaling is above 100%. This is particularly serious in shacks where the SSTV application shares a monitor with a logger, browser, CAT program, or SDR display.

Reducing the height does not solve the problem: major tabs contain several independent fixed-height scroll areas. An operator can lose context while scrolling one narrow column without moving the adjacent image or controls. The core workspace needs to reflow or collapse at realistic desktop sizes rather than requiring a 1920-DIP canvas.

### 2. The main VFO display cannot be used to enter a frequency or choose a normal rig mode

The largest and most prominent control in the header is a read-only frequency display. The nearby USB/LSB/FM choices are disabled (`RadioHeaderView.axaml:53-81`). The operator can tune by applying a saved preset, but there is no general frequency-entry control in the view.

This is especially confusing because the backing view-model already contains `FrequencyInputMhz`, `SetFrequencyAsync`, `SelectedRadioMode`, and `SetModeSafeAsync` (`RadioStatusViewModel.cs:563-578` and `:907-920`). The UI therefore withholds working CAT operations while visually presenting itself as a VFO. I expect to click or focus the large frequency readout, type a frequency, press Enter, and select USB/LSB/FM or the radio modes actually supported by the service.

### 3. Sending an edited image is split across two distant places

The editor's bottom section is titled **SEND**, but its primary action is **APPLY**; it does not transmit. The comments in `TxImageEditorPaneView.axaml:1905-1929` explicitly leave “Queue & Transmit” out. The real Transmit and Stop TX buttons are at the very bottom of the independently scrollable control sidebar (`TxControlsPaneView.axaml:382-385`), after Mode, Identification, Output, and Stock.

The expected workflow is local and obvious: finish the card, then press Send/Transmit beside the preview. The current workflow is “Apply, move to another column, possibly scroll to its bottom, then Transmit.” That is easy to miss and makes the **SEND** heading misleading. Transmit and Stop should remain visible, and the editor should offer an unambiguous Apply & Transmit path.

### 4. Ctrl+S saves an RX frame even while working in Transmit

File → Save frame as… globally binds Ctrl+S to `RxImage.SaveFrameCommand` (`MainWindow.axaml:78`). The TX editor implements familiar editor shortcuts such as undo, redo, and paste, but not Save.

While editing a TX card, Ctrl+S conventionally means save the current template/layout. Saving an unrelated received frame is a high-surprise result and may create an unwanted duplicate without preserving the work the operator thought was being saved. The shortcut must be contextual, or the RX action must use a shortcut that cannot be mistaken for editor Save.

### 5. Received images and logbook records cannot be deleted in the application

The receive-history interface exposes query, load, record, note, flag, and QSO-link operations, but no delete operation. The logbook exposes add/search/update/import/export, also without delete. Storage settings select a folder, but do not supply retention or cleanup controls.

Bad syncs, empty captures, accidental decodes, duplicates, and incorrect QSOs are normal operating data, not exceptional events. With automatic RX saving and no automatic retention trim, the collection grows indefinitely. QSSTV's image viewer exposes Delete; Scanline should at least support deliberate per-item deletion, and preferably bulk cleanup/retention with confirmation.

### 6. There is no full-size or zoomable RX/history image viewer

The live RX image is fixed at about 320×256 (`MainWindow.axaml:640-644`), and Gallery's selected preview is only about 190 pixels high (`MainWindow.axaml:1125-1127`). Zoom exists in the TX editor and decoder trace, but not for the received picture itself.

Reading a weak callsign, grid, or report often requires zooming and comparing artifacts. YONIQ has a zoom/history view, while QSSTV's image viewer provides View and zoom actions. Clicking or double-clicking either the live image or a history image should open a resizable viewer with fit, 100%, zoom, and pan.

### 7. Existing MMSSTV/YONIQ template collections cannot be brought across

The template designer is useful, but legacy `.mtm` import is explicitly deferred in `spec/15-template-designer.md`. For the target user—someone moving from MMSSTV or YONIQ—years of reply cards, club layouts, and contest templates are part of the working station setup.

Rebuilding every card manually is a major adoption barrier. A read-only legacy importer is more important than several advanced editor refinements. A modern export/import bundle is also needed so Scanline templates can be backed up and shared.

### 8. The rapid “receive a station, fill a reply, transmit it” workflow is incomplete

The editor offers HIS CALL, HIS GRID, and RSV fields, but there is no central current-QSO model that automatically supplies the other station's details. `MacroTextResolver.cs:8-16` explicitly notes that legacy his-call/name/QTH/RST/greeting behavior is not represented. The editor also leaves Pull from RX Decode, From Logbook, and Queue & Transmit unimplemented.

There is no equivalent of the familiar F-key macro/action bank for one-click reply cards. Help → Macros is a token reference, not an operational macro bank. Scanline has the individual pieces, but the most common live-contact path still requires manual copying and navigation between Receive, Logbook, and Transmit.

## Tier 2 — major surprise, misleading state, or costly friction

### 1. RX mode “Lock” is visible but unavailable

Receive shows Auto as checked and Lock as disabled; the mode dropdown only reflects the detected mode (`MainWindow.axaml:228-238`). Quick-mode buttons force one next decode, but the tooltip states that they are not a persistent lock.

A persistent Robot/Scottie/Martin lock is a standard recovery tool when VIS is absent or damaged, and it is useful for repeated transmissions of a known mode. The one-shot override is helpful but not an adequate replacement. A disabled Lock beside Auto makes this missing behavior particularly obvious.

### 2. RX/TX quick-mode buttons cannot be reassigned as in YONIQ

YONIQ lets the operator left-click a quick-mode button to use it and right-click that button to choose which mode it represents. Scanline presents fixed quick-mode buttons without that per-button assignment workflow.

Restore the YONIQ behavior for both RX and TX: left-click selects/uses the assigned mode, while right-click opens the full mode list for that shortcut. Add a tooltip such as “Click to select; right-click to change this shortcut,” because right-click behavior is otherwise undiscoverable. Keep the ordinary dropdown as the complete, accessible way to select any mode without reprogramming a shortcut.

### 3. “Previous frames” thumbnails look selectable but do nothing

The Receive strip is an `ItemsControl` containing bordered images, with no selection or open command (`MainWindow.axaml:665-692`). It looks like a row of clickable history thumbnails beside the current image, but clicking cannot recall or inspect one.

Those thumbnails should select/open the frame, or the strip should look deliberately passive and include a clear Go to Gallery action.

### 4. Saved-frame metadata is too thin and can show the wrong frequency

Receive history stores time, mode, image path, state, note, flag, and QSO link, but not captured frequency, callsign, grid, or SNR. Gallery displays em-dashes for several of these values and filters mainly by mode/note. More seriously, the Receive metadata panel binds frequency to the current live VFO rather than a value latched when the displayed frame was received. Retuning can therefore make an old picture appear to have arrived on the new frequency.

Every completed frame should preserve its reception time, frequency, mode, decoded/entered callsign, grid, and useful quality data. Gallery sorting and filtering should operate on those saved values.

### 5. Autosave, Save frame, Save frame as, and Export are not clearly distinguished

Every RX frame is automatically stored, and the status area asserts **AUTOSAVE ON**. Receive still has Save frame, File has Save frame as…, and Gallery has Export. Nothing near the actions explains that manual Save creates another copy of an already archived image.

That invites duplicates and hides the disk-growth consequence of mandatory autosave. QSSTV exposes an autosave choice and a “save if complete” threshold. Scanline should either make its archive policy configurable or clearly explain it, and use distinct language such as Export copy for manual file output.

### 6. “Re-decode” does not re-decode the current frame

The Receive button opens a WAV file picker through `RxImagePaneViewModel`'s file-decode path. Tools more accurately says Re-decode from WAV…, but neither action reprocesses the currently displayed or selected history item, and history does not retain an associated recording.

The button should be called Decode WAV… unless per-frame audio is retained. If recordings can be linked later, Re-decode belongs on the selected history entry and should state which decoder settings will be reused.

### 7. “Pwr” looks like RF power but controls application audio gain

The header/output slider labelled **Pwr** binds to `TxVolumePercent` (`RadioHeaderView.axaml:265-341`). It is an application playback/drive level, not transmitter watts. It appears near genuine rig PWR, ALC, and SWR telemetry, so the natural interpretation is RF power.

This can lead to incorrect station setup or overdrive. Label it TX audio level or Drive, include its unit/scale, and explain the sound-card calibration relationship in a tooltip.

### 8. Radio setup requires a save-close-reopen-connect loop

Options' Connect action works from persisted settings and tells the operator to Save first. Save closes the dialog. The practical path after changing a port or backend is therefore Save, reopen Options, then Connect. In addition, a settings-save exception is logged but has no bound visible error message (`OptionsWindowViewModel.cs:1992-2082`).

Radio setup is already the most failure-prone first-run task. Test/Connect should validate the values currently shown in the dialog, and Save failures must be shown beside the action that failed.

### 9. Several static labels look like live controls or trustworthy telemetry

- Gallery's **SORT NEWEST** and **SIZE M** are styled borders rather than selectable controls.
- The header caption **VFO A · RX · M1** is static even though those three states are not tracked there.
- Decode Activity is a fully drawn table with no item source.
- Status fields such as Memory, SNR, and Disk can remain em-dashes.
- The top **AUTO-DETECT** chip is static rather than reflecting whether auto-detect is paused.

These are not harmless decoration in radio software: operators use compact status labels to decide what the station is doing. Static mock data should either become real state or be removed until it is real.

### 10. Auto-follow RX mode lacks the expected TX safety guards

`TxControlsPaneViewModel.OnModeDetected` changes `SelectedMode` whenever Auto is enabled and the editor is closed (`TxControlsPaneViewModel.cs:815-838`). It does not apply YONIQ's `TrackTxMode` guards for “not currently transmitting” and matching RX/TX image width (`yoniq-old/YONIQ-main/Main.cpp:4907-4915`, decoded as CP932). The UI describes Auto as following the received mode without disclosing those limits.

Mode should not change underneath an active transmission, and an incompatible image width should not silently alter the intended reply. This is visible as a simple convenience switch but has operational consequences that require the legacy guards.

### 11. “TX INHIBIT” should say “TX ERROR”

The indicator lights for the last TX error or SWR cutoff, while its tooltip explains that a new Transmit is not blocked (`MainWindow.axaml:1542-1547`). To a radio operator, inhibit has a specific safety meaning: PTT cannot be asserted while the condition remains.

Rename the indication **TX ERROR** (or, more specifically, **LAST TX ERROR / SWR STOP**). The current wording overstates the protection by implying that another transmission is actively blocked.

### 12. Complex decoder controls lack enough usable contextual help

Re-sync, Correct slant, Abort, Capture, Auto gain, waveform channels, waterfall Gain/Zero, and Bins/px all require domain knowledge or have non-obvious consequences. Help is inconsistent: some items have good explanatory tips, others have none, and unfinished ones often share the generic “Not yet implemented—tracked on roadmap” text.

The small `[?]` help glyph is hover-oriented rather than a proper click/focus help control, so it is poor for keyboard and touch use. The global styling does allow tooltips on disabled controls, but the tooltip still needs to explain what the feature would do, its present alternative, and why it is unavailable. The application also lacks a visible quick-start/manual or shortcut reference in Help.

## Tier 3 — understandable after study, but awkward or needlessly confusing

### 1. Too much unfinished UI is presented as part of the working product

The entire Advanced options page is disabled, as are several General color choices, Audio FIFO/thread priority, OmniRig, RTS/PTT lock, sound-file identification, legacy VOX, and calibration items. This makes it difficult to distinguish a configuration problem from a feature that simply does not exist yet.

Hide or clearly group roadmap previews. A disabled control is useful only when it tells an operator something actionable, such as the supported replacement or the release in which it is expected.

### 2. “VOX” names three different concepts

- Radio/PTT VOX means hardware audio-keyed transmit control.
- Identification VOX refers to the disabled legacy sound-file/preamble feature.
- TX mode VOX tone is a fixed pre-VIS leader and explicitly is not legacy VOX.

These are materially different. Keep VOX for the PTT method and use names such as Pre-VIS leader or ID audio for the other two. Current help text also contains stale claims—for example, some Options text says features are not ported when related controls now exist—so the English resource needs a behavior-focused review.

### 3. The TX sidebar is dense and repeats choices

One mode card contains a 4×4 quick grid, a full mode dropdown, separate user Favorites, Auto picks, and Selected rows. Undo/redo also appears in more than one editor area. Together, these repetitions push the actual Transmit action below less important setup controls.

Keep the dropdown as the complete mode selector and make the quick buttons the favorites: left-click selects the assigned mode and right-click changes that assignment. Once the quick buttons work this way, remove the separate TX Favorites feature because it duplicates the same function. Reserve other repeated controls for a persistent toolbar only where their availability genuinely helps.

### 4. Logbook entry uses software-shaped fields rather than operator-shaped fields

Frequency is entered as raw Hz while the rest of the application presents MHz. Callsign, name, QTH, grid, country, and notes rely heavily on watermark-only labels that disappear once populated. Search is exact callsign plus a manual Refresh, and there is no sort or delete.

Use a normal MHz frequency editor with band assistance, persistent field labels, live/explicitly submitted search, sortable results, and a safe correction/deletion path.

### 5. “Export session log…” actually exports the QSO log as ADIF

The Tools command invokes the logbook ADIF export, while Help separately exposes the application log. In amateur-radio software, session log could plausibly mean either the current QSOs or diagnostic events.

Call it Export logbook as ADIF… and keep diagnostic-log language distinct.

### 6. The interface exposes expert telemetry before it teaches the basic path

There are many small chips, meter labels, sync statistics, decoder trace controls, and compact 10–12 px labels. This can be valuable for diagnosis, but it competes with the basic receive/reply workflow and makes the initial screen read more like a service console than a radio application.

A Basic/Advanced presentation, collapsible diagnostic panels, or a first-run tour would preserve the expert data without making new operators decode the whole screen before their first contact.

## Tier 4 — worthwhile parity or specialist features

These are not universal blockers for analog SSTV, but users of the other programs will notice them.

- **Camera/webcam snapshot as a TX source.** QSSTV exposes Camera snapshot; Scanline currently emphasizes files, stock images, clipboard, and received images.
- **Copy, print, and open-received-image actions.** QSSTV's image viewer includes Print and a fuller action set. Scanline can paste into TX but does not provide the corresponding obvious RX/history copy workflow.
- **Per-frame WAV attachment and later re-decode.** This would make Re-decode operationally useful and allow decoder improvements to be tested against saved receptions.
- **Richer history navigation.** Add real newest/oldest sorting, adjustable thumbnail size, band/callsign/grid filters, and keyboard previous/next navigation.
- **Template exchange.** Beyond the Tier 1 legacy importer, provide a versioned Scanline template package for backup, import, export, and sharing.
- **OmniRig client support.** This matters on Windows stations where several applications share the rig; the current Options entry is only a placeholder.
- **Waterfall/spectrum color customization.** The visible disabled color controls advertise a capability that is common in weak-signal software but unavailable here.
- **Sound-file station identification.** CW/FSK identification covers many operators, but it does not fully replace existing personalized audio-ID workflows.
- **External logger integration.** ADIF and UDP are useful foundations; live interoperability with established logging workflows would remove duplicate typing.
- **QSSTV specialist workflows.** Digital SSTV/DRM profiles, hybrid/FTP upload, and repeater features are absent. They should be treated as explicit product-scope decisions rather than assumed requirements for the core analog-SSTV release.

## Overall operator verdict

I get the broad layout: radio at the top, four jobs underneath, controls on the left, working image in the center, details on the right. The individual Receive and Transmit panels also mostly sit in logical conceptual groups.

I would not yet trust the interface without studying it, however. The most prominent radio control is not editable, the editor's SEND area does not send, Ctrl+S targets the wrong job, archived material cannot be deleted or inspected full-size, and several static or placeholder elements look live. Fixing those expectation violations—and completing the receive-to-reply path—would improve usability more than adding further diagnostics or editor options.
