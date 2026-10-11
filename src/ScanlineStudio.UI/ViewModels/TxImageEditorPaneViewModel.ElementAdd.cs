using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Converters;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class TxImageEditorPaneViewModel
{
    [RelayCommand]
    private void AddOverlayElement() =>
        AddOverlayElementAt(CropRect.X + (CropRect.Width / 2), CropRect.Y + (CropRect.Height / 2), DefaultElementWidth, DefaultTextElementHeight);

    /// <summary>TX workflow modernization plan, Phase 3a -- <see cref="AddOverlayElement"/> (the
    /// toolbar button's parameterless command) now delegates here with its own original
    /// crop-centered seed as the default rect. The draw-to-place gesture
    /// (<c>TxImageEditorPaneView.axaml.cs</c>'s armed-placement tunnel handler) calls this directly
    /// with a caller-computed rect instead. <paramref name="centerX"/>/<paramref name="centerY"/>
    /// are CENTER-anchored, matching <see cref="ITemplateElementViewModel.X"/>/<c>Y</c>'s own
    /// convention (NOT <see cref="CropRect"/>'s top-left-anchored one) -- see
    /// <see cref="ProjectRectToCropRelative"/>'s own doc comment for why X/Y are stored relative to
    /// the full working copy, not the crop, despite this.</summary>
    public void AddOverlayElementAt(double centerX, double centerY, double width, double height)
    {
        PushUndoSnapshot();
        var element = CreateOverlayElement(NewTextElementDefaults with
        {
            X = centerX,
            Y = centerY,
            Width = width,
            Height = height,
            Z = NextZ(),
            // Phase 4: the preparer's own default family, queried fresh, not a second hardcoded literal.
            FontFamily = _preparer.AvailableFontFamilies.FirstOrDefault(string.Empty),
        });
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally (TX workflow modernization plan, Phase 2) -- unlike plain
        // selection change, a brand-new element overrides even an open Geometry tab, since there's
        // nothing to "compare positions" against yet.
        SelectTextStyleTab();
        RecomputePreview();
    }

    /// <summary>New in Phase 1 (spec/15-template-designer.md) -- mirrors
    /// <see cref="AddOverlayElement"/> exactly (crop-centered seed, same undo/select/recompute
    /// shape), just for the box element type instead of text.</summary>
    [RelayCommand]
    private void AddBoxElement() =>
        AddBoxElementAt(CropRect.X + (CropRect.Width / 2), CropRect.Y + (CropRect.Height / 2), DefaultElementWidth, DefaultBoxElementHeight);

    /// <summary>TX workflow modernization plan, Phase 3a -- same delegation shape as
    /// <see cref="AddOverlayElementAt"/> right above, see its own doc comment.</summary>
    public void AddBoxElementAt(double centerX, double centerY, double width, double height)
    {
        PushUndoSnapshot();
        var element = CreateBoxElement(NewBoxElementDefaults with
        {
            X = centerX,
            Y = centerY,
            Width = width,
            Height = height,
            Z = NextZ(),
        });
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally (TX workflow modernization plan, Phase 2) -- see
        // AddOverlayElement's own identical comment. Box's applicable tab is Geometry (no dedicated
        // Box Style tab exists), so this is also the "already the fallback" case made explicit.
        SelectGeometryTab();
        RecomputePreview();
    }

    /// <summary>TX editor gap-items plan (2026-09-01) -- same delegation shape as
    /// <see cref="AddOverlayElement"/>/<see cref="AddBoxElement"/>, a fixed horizontal default seed
    /// (matching the plan's own "horizontal, DefaultElementWidth long, centered" default) via the
    /// endpoint-parameterized <see cref="AddLineElementAt"/> below.</summary>
    [RelayCommand]
    private void AddLineElement()
    {
        var centerX = CropRect.X + (CropRect.Width / 2);
        var centerY = CropRect.Y + (CropRect.Height / 2);
        var halfWidth = DefaultElementWidth / 2;
        AddLineElementAt(centerX - halfWidth, centerY, centerX + halfWidth, centerY);
    }

    /// <summary>TX workflow modernization plan, Phase 3a's own delegation shape (see
    /// <see cref="AddOverlayElementAt"/>'s doc comment), endpoint-parameterized instead of
    /// center/width/height -- the draw-to-place gesture's own anchor/release points ARE a line's
    /// endpoints directly, with no rect-normalization step in between (deliberately NOT run through
    /// <c>ComputeRectFromDrag</c>, which would lose direction/order).</summary>
    public void AddLineElementAt(double x1, double y1, double x2, double y2)
    {
        PushUndoSnapshot();
        var element = CreateLineElement(NewLineElementDefaults with
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Z = NextZ(),
        });
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally -- see AddOverlayElement's own identical comment. Line's
        // applicable tab is Geometry (no dedicated Line Style tab, same as box).
        SelectGeometryTab();
        RecomputePreview();
    }

    /// <summary>New elements default to drawing on top of everything already on the canvas --
    /// existing max Z + 1, or 0 for the first element.</summary>
    private int NextZ() => OverlayElements.Count == 0 ? 0 : OverlayElements.Max(e => e.Z) + 1;

    /// <summary>Phase 2 (spec/15-template-designer.md) source 1/3 -- direct reuse of the same
    /// picker/loader the TX editor's own "Browse..." stock-image flow already uses
    /// (<see cref="TxControlsPaneViewModel.SelectImageAsync"/>), not new I/O. Mirrors that method's
    /// own error-handling shape: log + return on picker/load failure, silent return on cancel (a
    /// null path from the picker is a normal "user hit Cancel", not an error).</summary>
    [RelayCommand]
    private Task AddImageFromFileAsync() =>
        ImportImageAsync(ImagePickSource.File, "Panes.TxImageEditor.AddImageFailed",
            (source, path) => InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.File, path)));

    private enum ImagePickSource
    {
        File,
        Clipboard,
    }

    /// <summary>Shared pick -> load -> <paramref name="apply"/> body of the four image-import commands
    /// (new element or text picture fill, from file or clipboard). Picker cancel (null path) is a
    /// silent no-op; a picker or load failure is logged per source and sets
    /// <paramref name="failureKey"/> as the status. <paramref name="apply"/> runs after the load, with
    /// the picked file's path (null for clipboard, whose temp file is deleted here); an exception
    /// from it propagates and is not reported as a load failure. The clipboard temp PNG is deleted in
    /// the <c>finally</c>, success or failure. NOT shared with
    /// <see cref="AddImagesFromDroppedFilesAsync"/> -- that method deliberately re-implements
    /// load+downsample+insert on its own (Task.Run offload, batched push/recompute via
    /// <see cref="InsertImageElementCore"/> instead of one push/recompute per file), so there are
    /// genuinely two "load a path into an image element" bodies, not one shared one.</summary>
    private async Task ImportImageAsync(ImagePickSource from, string failureKey, Action<IImageSource, string?> apply)
    {
        string? path;
        try
        {
            path = from == ImagePickSource.File
                ? await _filePickerService.PickImageFileAsync()
                : await _filePickerService.PickClipboardImageAsync();
        }
        catch (Exception ex)
        {
            LogImportFailed(from, ex);
            StatusMessage = _localization.GetString(failureKey);
            return;
        }

        if (path is null)
        {
            // Cancelled file pick, or no image on the clipboard right now -- a normal, silent no-op.
            return;
        }

        try
        {
            IImageSource source;
            try
            {
                source = await _imageFileLoader.LoadOriginalAsync(path);
            }
            catch (Exception ex)
            {
                LogImportFailed(from, ex);
                StatusMessage = _localization.GetString(failureKey);
                return;
            }

            StatusMessage = null;
            apply(source, from == ImagePickSource.File ? path : null);
        }
        finally
        {
            if (from == ImagePickSource.Clipboard)
            {
                // Best-effort: a cleanup failure must not mask or replace the load result above.
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log.ClipboardTempFileCleanupFailed(_logger, path, ex);
                }
            }
        }
    }

    private void LogImportFailed(ImagePickSource from, Exception ex)
    {
        if (from == ImagePickSource.File)
        {
            Log.AddImageFromFileFailed(_logger, ex);
        }
        else
        {
            Log.AddImageFromClipboardFailed(_logger, ex);
        }
    }

    /// <summary>Phase 2 source 2/3 -- snapshot at insert time, NOT a live binding (plan-review
    /// decided open question: a saved template must not carry a reference to whatever RX buffer
    /// state happens to exist when it's reused later -- <see cref="IReceivedImageBuffer.Current"/>'s
    /// own instance is never mutated in place once read, so this assignment genuinely freezes it,
    /// though that's copy-on-WRITE inside the buffer, not copy-on-read here -- see this method body's
    /// own comment for why that distinction matters).
    /// <para>Mid-decode insert is intentionally ALLOWED (not gated on
    /// <see cref="IReceivedImageBuffer.Progress"/> being null/complete) -- a live CanExecute gate
    /// would need this VM to subscribe to <see cref="IReceivedImageBuffer.Updated"/>, a long-lived DI
    /// singleton event. Correction (live-locale-switch review, 2026-09-20): this class IS
    /// <c>IDisposable</c> (its own <c>Dispose()</c> now also unsubscribes from
    /// <see cref="ScanlineStudio.Abstractions.Localization.ILocalizationService.CultureChanged"/>,
    /// which is the exact same class of subscription this comment used to claim was impossible to
    /// unsubscribe here) -- <see cref="IReceivedImageBuffer.Updated"/> specifically was just never
    /// wired to it, not genuinely unwireable. Left unwired: a user who inserts a still-decoding
    /// (partially black) frame can simply Undo/Remove it -- an acceptable, low-severity v1 tradeoff
    /// documented here rather than left unspecified.</para></summary>
    [RelayCommand]
    private void AddLastRxImage()
    {
        // Code-review finding: the idle/never-received case is distinct from the mid-decode case
        // this method's own class doc comment already covers -- IReceivedImageBuffer.Current
        // defaults to (and resets to, on decode restart) a 1x1 black placeholder, never null. A
        // click here with nothing ever received would otherwise silently insert that black square
        // with no visible feedback. No live subscription needed to guard this -- checked at click
        // time instead, a body-level no-op rather than a CanExecute gate that would need its own
        // live subscription to IReceivedImageBuffer.Updated to stay non-stale (see this method's
        // own doc comment, just above, for why that specific subscription stays unwired).
        //
        // Code-review finding (T0-10 pass): read exactly ONCE into a local, not twice -- a second,
        // separate Current read here could race a concurrent decode-restart swapping in a fresh
        // 1x1 placeholder BETWEEN the guard check and the insert below (same reasoning already
        // applied to TxControlsPaneViewModel.CopyReceivedImageToTxAsync's own identical read).
        var current = _receivedImageBuffer.Current;
        if (current is { Width: <= 1, Height: <= 1 })
        {
            return;
        }

        // T0-10 (production_audit.md): _receivedImageBuffer.Current's getter now lazily allocates
        // a fresh, independently-owned array the first time it's read since the underlying decode
        // last changed (was: a fresh array on every single scanline-group decode, regardless of
        // whether anything ever read it -- real LOH pressure on the capture drain thread). Either
        // way, whatever Current returns here is a genuinely independent snapshot this class is
        // free to retain indefinitely -- IReceivedImageBuffer.Current's own doc comment states
        // this contract explicitly now; capturing the reference here still freezes it for this
        // element, unchanged.
        InsertImageElement(current, new ImageSourceOrigin(ImageSourceKind.LastRx, null));
    }

    /// <summary>Auditor usability review follow-up (2026-08-18) -- the "+ IMAGE" flyout's 4th source
    /// (clipboard paste, Phase 2's own logged scope cut, picked back up: "zero precedent for either
    /// [clipboard-paste/OS drag-drop] anywhere in this codebase" at the time; Avalonia's own
    /// <c>ClipboardExtensions.TryGetBitmapAsync</c> is real, cross-platform precedent now used here).
    /// Same shape as <see cref="AddImageFromFileAsync"/> (picker call -> loader call -> insert), just
    /// via <see cref="IFilePickerService.PickClipboardImageAsync"/> instead of
    /// <see cref="IFilePickerService.PickImageFileAsync"/> -- reuses the SAME
    /// <see cref="_imageFileLoader"/> call, one image-loading code path (EXIF orientation included),
    /// not a second one. The picker's own returned path is a throwaway temp PNG file (see that
    /// method's own doc comment) -- deleted here once loaded, success or failure, since nothing else
    /// ever needs it again (<see cref="ImageSourceKind.Clipboard"/>'s own Payload is always null, same
    /// "ephemeral, nothing to re-resolve" tier as <see cref="ImageSourceKind.LastRx"/>).</summary>
    [RelayCommand]
    private Task AddImageFromClipboardAsync() =>
        ImportImageAsync(ImagePickSource.Clipboard, "Panes.TxImageEditor.AddImageFailed",
            (source, _) => InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.Clipboard, null)));

    /// <summary>TX editor gap-items plan, item 4b (picture fill) -- picker call -> loader call ->
    /// assign onto the SELECTED text element's own BitmapFillSource/Enabled, same shape as
    /// <see cref="AddImageFromFileAsync"/> immediately above, but assigning onto an EXISTING
    /// element's property instead of inserting a new one (this fills the text's own glyphs, it
    /// doesn't add a separate image element). Gated on a text element actually being selected --
    /// same <see cref="CanInsertField"/>-style guard.</summary>
    private bool CanSetTextBitmapFill() => SelectedTextElement is not null;

    [RelayCommand(CanExecute = nameof(CanSetTextBitmapFill))]
    private Task PickTextBitmapFillFromFileAsync() =>
        SelectedTextElement is { } text
            ? ImportImageAsync(ImagePickSource.File, "Panes.TxImageEditor.TextBitmapFillFailed", (source, _) => ApplyTextBitmapFill(text, source))
            : Task.CompletedTask;

    /// <summary>Fills the text element captured when the pick started, not whatever is selected
    /// once the pick returns.</summary>
    private static void ApplyTextBitmapFill(OverlayElementViewModel text, IImageSource source)
    {
        // Source before Enabled, matching OverlayElementViewModel.ApplyStyleFrom's convention.
        text.BitmapFillSource = source;
        text.BitmapFillEnabled = true;
    }

    /// <summary>Same shape as <see cref="AddImageFromClipboardAsync"/> immediately above --
    /// <see cref="PickTextBitmapFillFromFileAsync"/>'s own doc comment covers the "assigns onto the
    /// selected element" difference from the image-ELEMENT-adding commands this mirrors.</summary>
    [RelayCommand(CanExecute = nameof(CanSetTextBitmapFill))]
    private Task PickTextBitmapFillFromClipboardAsync() =>
        SelectedTextElement is { } text
            ? ImportImageAsync(ImagePickSource.Clipboard, "Panes.TxImageEditor.TextBitmapFillFailed", (source, _) => ApplyTextBitmapFill(text, source))
            : Task.CompletedTask;

    /// <summary>Turns the fill mode back to solid (matching the GRADIENT checkbox's own
    /// "unchecking just clears the flag" shape) -- deliberately does NOT clear
    /// <see cref="OverlayElementViewModel.BitmapFillSource"/> itself, so re-checking PICTURE without
    /// re-picking a file restores the last picture, same "flag gates whether the resolved value is
    /// USED, the value itself persists" convention <see cref="OverlayElementViewModel.StrokeColor"/>/
    /// <see cref="OverlayElementViewModel.ShadowColor"/> already establish (those go null<->set
    /// instead of a separate enabled flag, but the "toggling off doesn't destroy data" spirit is the
    /// same).</summary>
    [RelayCommand(CanExecute = nameof(CanSetTextBitmapFill))]
    private void ClearTextBitmapFill()
    {
        if (SelectedTextElement is { } text)
        {
            text.BitmapFillEnabled = false;
        }
    }

    /// <summary>One row in the Phase 2 "From RX history" picker flyout -- deliberately NOT a reuse of
    /// <c>RxHistoryPaneViewModel</c>'s own row type (that VM has its own, unrelated concerns/
    /// dependencies; the plan's own scope calls for injecting <see cref="IReceiveHistoryStore"/>
    /// directly here instead).</summary>
    /// <summary><see cref="SelectCommand"/> is parent-pushed (same pattern as
    /// <see cref="ITemplateElementViewModel.RemoveCommand"/>/<c>MoveUpCommand</c>) so the AXAML
    /// picker list can bind <c>Command="{Binding SelectCommand}" CommandParameter="{Binding}"</c>
    /// directly on each row -- deliberately NOT a
    /// <c>$parent[ItemsControl].((vm:TxImageEditorPaneViewModel)DataContext).AddImageFromRxHistoryCommand</c>
    /// binding path, which this codebase has already hit as a real
    /// <c>ArgumentException: Unable to resolve type</c> at first DataTemplate realization elsewhere
    /// (see <see cref="ITemplateElementViewModel.RemoveCommand"/>'s own doc comment).
    /// <see cref="ReceivedAt"/> (UX friction fix, Fable operator-perspective review): the AXAML row
    /// used to display <see cref="Id"/> raw (a GUID-shaped string, meaningless to an operator picking
    /// between up to <see cref="RxHistoryPickerMaxEntries"/> recent receptions) -- already
    /// local-offset (<c>DateTimeOffset.Now</c> at write time, see
    /// <c>ReceiveHistoryRecorder.RecordCompletedImageAsync</c>), same as <c>MainWindow.axaml</c>'s
    /// own established <c>ReceivedAt</c> display convention, so no extra conversion needed here.</summary>
    public sealed record RxHistoryPickerEntry(string Id, DateTimeOffset ReceivedAt, string FilePath, Bitmap? Thumbnail, IRelayCommand<RxHistoryPickerEntry>? SelectCommand);

    /// <summary>Refreshes <see cref="RxHistoryPickerEntries"/> -- called when the "From RX history"
    /// flyout opens (View-level), not kept live/subscribed (same "no lifecycle hook to unsubscribe"
    /// reasoning as <see cref="AddLastRxImage"/>). <see cref="IReceiveHistoryStore.QueryAsync"/>
    /// already returns newest-first (<c>SqliteReceiveHistoryStore</c>'s own <c>ORDER BY
    /// ReceivedAtUtc DESC</c>), capped client-side to <see cref="RxHistoryPickerMaxEntries"/> since
    /// the store has no server-side limit parameter.</summary>
    [RelayCommand]
    private async Task RefreshRxHistoryPickerAsync()
    {
        // Tier B audit finding: this was the one sibling among the 4 image-source add/refresh paths
        // (AddImageFromFileAsync, AddImageFromClipboardAsync, AddImageFromRxHistoryAsync) with no
        // StatusMessage on failure -- log-only, so a failure here (e.g. an unreadable RX history
        // SQLite file) left the "From RX history" flyout silently empty (or stale, showing the
        // PREVIOUS successful refresh's entries pointing at possibly-deleted files) with zero
        // explanation. StatusMessage now cleared on entry / set on failure, same as the siblings.
        StatusMessage = null;
        IReadOnlyList<ReceiveHistoryEntry> entries;
        try
        {
            entries = await _receiveHistoryStore.QueryAsync(new ReceiveHistoryFilter());
        }
        catch (Exception ex)
        {
            Log.RefreshRxHistoryPickerFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.RxHistoryPickerRefreshFailed");
            return;
        }

        var picked = new List<RxHistoryPickerEntry>();
        foreach (var entry in entries.Take(RxHistoryPickerMaxEntries))
        {
            Bitmap? thumbnail = null;
            try
            {
                var thumbnailSource = await _receiveHistoryStore.LoadThumbnailAsync(entry, RxHistoryPickerThumbnailMaxDimension);
                thumbnail = ImageSourceBitmapConverter.ToBitmap(thumbnailSource);
            }
            catch (Exception ex)
            {
                // One bad thumbnail (e.g. a history row whose backing file was deleted out-of-band)
                // shouldn't blank the whole picker list -- degrade to a null-thumbnail row instead.
                Log.RxHistoryThumbnailLoadFailed(_logger, entry.Id, ex);
            }

            picked.Add(new RxHistoryPickerEntry(entry.Id, entry.ReceivedAt, entry.FilePath, thumbnail, AddImageFromRxHistoryCommand));
        }

        RxHistoryPickerEntries = new ObservableCollection<RxHistoryPickerEntry>(picked);
    }

    /// <summary>Phase 2 source 3/3 -- <see cref="IReceiveHistoryStore"/> has no full-resolution
    /// loader (only <see cref="IReceiveHistoryStore.LoadThumbnailAsync"/>), so the full-res load
    /// reuses the same <see cref="IImageFileLoader.LoadOriginalAsync"/> the file-picker source
    /// already uses, against the entry's own real on-disk <see cref="ReceiveHistoryEntry.FilePath"/>
    /// -- confirmed the only available move, not a gap glossed over.</summary>
    [RelayCommand]
    private async Task AddImageFromRxHistoryAsync(RxHistoryPickerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        IImageSource source;
        try
        {
            source = await _imageFileLoader.LoadOriginalAsync(entry.FilePath);
        }
        catch (Exception ex)
        {
            Log.AddImageFromRxHistoryFailed(_logger, entry.Id, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
            return;
        }

        StatusMessage = null;
        InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.RxHistory, entry.Id));
    }

    /// <summary>Shared by all 4 Phase 2/missing-feature-sweep image sources -- same crop-centered
    /// seed/undo/select/recompute shape as <see cref="AddOverlayElement"/>/<see cref="AddBoxElement"/>.
    /// Default Contain fit (not Stretch) -- an inserted photo keeping its own aspect ratio by default
    /// is the less-surprising choice; <see cref="ImageElementViewModel.Fit"/> isn't yet user-editable
    /// in Phase 2 (Phase 4's style panel territory), but the pipeline/VM plumbing already supports it.
    /// One-source-at-a-time push+recompute -- <see cref="AddImagesFromDroppedFilesAsync"/>'s own
    /// multi-file batch does NOT call this (would be N undo steps + N pipeline runs for one user
    /// gesture, violating this editor's own established "one gesture, one undo step" convention --
    /// see <see cref="ApplySnappedElementBounds"/>'s own doc comment); it calls
    /// <see cref="InsertImageElementCore"/> directly instead, batching the push/recompute itself.</summary>
    private void InsertImageElement(IImageSource source, ImageSourceOrigin origin)
    {
        // TX workflow modernization plan, Phase 7 -- captured BEFORE DownsampleToBudget: "original
        // size" (ResetImageElementToOriginalSize) means the source's own native resolution, not the
        // working-copy-budget-capped copy this editor actually composites with.
        var naturalPixelWidth = source.Width;
        var naturalPixelHeight = source.Height;

        // Downsampled to the SAME working-copy budget as the background image itself (code-review
        // finding -- see DownsampleToBudget's own doc comment) before ANY of it touches the UI
        // thread's WriteableBitmap conversion or the real per-frame pipeline. Resolved once here,
        // at insert time, same as the file/RX-history sources' own LoadOriginalAsync resolution --
        // consistent with RawImageElementSnapshot's own "resolve once, snapshot the resolved value"
        // contract, not a departure from it.
        var downsampled = DownsampleToBudget(
            source, (int)(WorkingCopyWidth * WorkingCopyScaleFactor), (int)(WorkingCopyHeight * WorkingCopyScaleFactor), _preparer);

        PushUndoSnapshot();
        InsertImageElementCore(downsampled, naturalPixelWidth, naturalPixelHeight, origin, cascadeIndex: 0);
        RecomputePreview();
    }

    /// <summary>Missing-feature sweep (2026-08-31): the shared core <see cref="InsertImageElement"/>
    /// factors down to -- create the element, add it, select it -- with NO push/no recompute, so a
    /// caller inserting several elements for one user gesture (<see cref="AddImagesFromDroppedFilesAsync"/>)
    /// can batch those into one undo step and one pipeline run instead of one each.
    /// <paramref name="cascadeIndex"/> offsets each successive element diagonally (wrapped at 8 steps
    /// -- <c>DefaultElementWidth</c> is 0.3 of the normalized canvas, so unwrapped growth would push
    /// element centers outside [0,1] well before a 20-file drop finishes, and this editor's own
    /// established convention is that out-of-bounds content is clipped at render time, not
    /// repositioned -- so those elements would be invisible on the canvas, reachable only via the
    /// elements list) so a multi-file drop doesn't stack every element exactly on top of the first
    /// one, without ever pushing later elements off-canvas.</summary>
    private void InsertImageElementCore(
        IImageSource downsampled, int naturalPixelWidth, int naturalPixelHeight, ImageSourceOrigin origin, int cascadeIndex)
    {
        const double CascadeOffset = 0.03;
        const int CascadeWrap = 8;
        var offset = CascadeOffset * (cascadeIndex % CascadeWrap);
        var element = CreateImageElement(NewImageElementDefaults(downsampled, origin) with
        {
            X = CropRect.X + (CropRect.Width / 2) + offset,
            Y = CropRect.Y + (CropRect.Height / 2) + offset,
            Width = DefaultElementWidth,
            Height = DefaultElementWidth,
            Z = NextZ(),
            NaturalPixelWidth = naturalPixelWidth,
            NaturalPixelHeight = naturalPixelHeight,
        });
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally (TX workflow modernization plan, Phase 2) -- covers both
        // call sites (toolbar/clipboard InsertImageElement AND the OS drag-drop path below), since
        // both insert genuinely new image content, not a copy of something already on the canvas.
        SelectImageTab();
    }

    /// <summary>Missing-feature sweep (2026-08-31): OS file drag-and-drop onto the TX editor,
    /// wired from <c>TxImageEditorPaneView.axaml.cs</c>'s own <c>OnEditorDrop</c> code-behind
    /// handler. Legacy YONIQ's own equivalent (<c>Main.cpp</c>'s <c>DropFile</c>) replaces the
    /// WHOLE TX bitmap with the one dropped file (<c>DragQueryFile(hDrop, 0, ...)</c> -- index 0
    /// only, silently ignoring any additional files); this port's own architecture is
    /// template/compositing-based, not "one flat bitmap," so a dropped file is a new picture
    /// ELEMENT (the same shape as the existing "+ IMAGE" flyout's 3 sources), and -- a deliberate
    /// improvement over legacy, not an oversight -- a multi-file drop inserts one element PER file
    /// instead of silently discarding all but the first.
    ///
    /// One undo step, one pipeline recompute, for the WHOLE drop (not per file) -- matches this
    /// editor's own established "one gesture, one undo step" convention (see
    /// <see cref="ApplySnappedElementBounds"/>'s own doc comment); each file is loaded and
    /// downsampled INSIDE the loop, one at a time, with the full-resolution decode discarded before
    /// the next iteration starts, so a large multi-file drop doesn't hold every full-res decode in
    /// memory at once. Capped at <see cref="MaxDroppedImageFiles"/> -- nothing bounds how many files
    /// an OS drag-drop can hand this method otherwise, and each one is a real decode plus a
    /// synchronous downsample.</summary>
    public async Task AddImagesFromDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            if (paths.Count == 0)
            {
                Log.DroppedFilesEmpty(_logger);
                StatusMessage = _localization.GetString("Panes.TxImageEditor.NoImageFilesDropped");
                return;
            }

            var truncated = paths.Count > MaxDroppedImageFiles;
            var capped = truncated ? paths.Take(MaxDroppedImageFiles).ToList() : paths;

            var loaded = new List<(string Path, IImageSource Downsampled, int NaturalWidth, int NaturalHeight)>();
            var failureCount = 0;
            foreach (var path in capped)
            {
                try
                {
                    var source = await _imageFileLoader.LoadOriginalAsync(path);
                    // TX workflow modernization plan, Phase 7 -- captured before the downsample below,
                    // same reasoning as InsertImageElement's own capture.
                    var naturalWidth = source.Width;
                    var naturalHeight = source.Height;
                    // Downsample-then-discard-the-full-res-copy INSIDE the loop, same reasoning as
                    // the class doc comment above -- `source` is never retained past this iteration.
                    // Offloaded via Task.Run: DownsampleToBudget/_preparer.Resize is real synchronous
                    // CPU work that would otherwise stall the UI thread once per dropped file (the
                    // single-image path via InsertImageElement pays this cost once; a multi-file drop
                    // would pay it up to MaxDroppedImageFiles times in a row with no progress feedback).
                    var downsampled = await Task.Run(() => DownsampleToBudget(
                        source, (int)(WorkingCopyWidth * WorkingCopyScaleFactor), (int)(WorkingCopyHeight * WorkingCopyScaleFactor), _preparer));
                    loaded.Add((path, downsampled, naturalWidth, naturalHeight));
                }
                catch (Exception ex)
                {
                    Log.AddImageFromDroppedFileFailed(_logger, path, ex);
                    failureCount++;
                }
            }

            if (loaded.Count == 0)
            {
                StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
                return;
            }

            PushUndoSnapshot();
            _suspendPreview = true;
            try
            {
                for (var i = 0; i < loaded.Count; i++)
                {
                    InsertImageElementCore(
                        loaded[i].Downsampled, loaded[i].NaturalWidth, loaded[i].NaturalHeight,
                        new ImageSourceOrigin(ImageSourceKind.File, loaded[i].Path), cascadeIndex: i);
                }
            }
            finally
            {
                _suspendPreview = false;
            }

            RecomputePreview();

            // Priority: truncation > failures > success -- both are real, but they mean different
            // things and call for different operator actions (re-drop the remainder that got cut,
            // vs. those specific files are just unreadable), so collapsing both into one generic
            // "some failed" message would destroy the information the status line exists to convey.
            StatusMessage = truncated
                ? _localization.GetString("Panes.TxImageEditor.DroppedImagesTruncated", loaded.Count, MaxDroppedImageFiles, paths.Count)
                : failureCount > 0
                    ? _localization.GetString("Panes.TxImageEditor.SomeDroppedImagesFailed", loaded.Count, capped.Count, failureCount)
                    : null;
        }
        catch (Exception ex)
        {
            // Same outer-guard shape as OnReadyRackTemplateSelected's own try/catch -- this is an
            // async void event handler's real body (TxImageEditorPaneView.axaml.cs's own
            // OnEditorDrop has no caller to observe a fault), so any exception this loop's own
            // per-file try/catch didn't already handle (a bug in DownsampleToBudget/CreateImageElement/
            // RecomputePreview itself) must be caught HERE, not left to crash the process --
            // AppDomain.UnhandledException only logs, it does not prevent termination.
            Log.AddImageFromFileFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
        }
    }

    /// <summary>Missing-feature sweep (2026-08-31): the code-behind's own <c>OnEditorDrop</c> extracts
    /// local file paths from the drop payload BEFORE calling <see cref="AddImagesFromDroppedFilesAsync"/>
    /// -- that extraction itself can throw (a malformed platform drag payload, a revoked storage
    /// handle) and the code-behind has no logger of its own to report through, so it calls this
    /// instead of swallowing the exception silently.</summary>
    public void ReportDroppedFilesUnreadable(Exception exception)
    {
        Log.DroppedFilesUnreadable(_logger, exception);
        StatusMessage = _localization.GetString("Panes.TxImageEditor.DroppedFilesUnreadable");
    }

    private bool CanAddPlateBehindText() => SelectedOverlayElement is OverlayElementViewModel;

    /// <summary>Phase 4 "plate" (background box behind text) -- DECIDED as a one-shot BUTTON action,
    /// not a stateful toggle (plan-review: a persistent toggle would need Z-collision handling, a
    /// new persisted "is plated" flag with no home on <see cref="RawTextElementSnapshot"/> today,
    /// LIVE bounds tracking on every text drag/resize, and a real parent/child relationship this
    /// element model is deliberately flat today -- none of that is "pure UI convenience"). Inserts
    /// one ordinary, INDEPENDENT <see cref="BoxElementViewModel"/> sized to the text's CURRENT
    /// bounds plus a small padding margin, directly behind it in draw order -- after insertion the
    /// two are just two separate elements the user can move/resize/remove independently, same as if
    /// they'd used the existing "Box" button and positioned it by hand.
    /// <para>Inserted at the text element's own COLLECTION index (i.e. immediately behind it in
    /// draw order), then EVERY element's <c>Z</c> is renumbered to match the collection's own order
    /// exactly (<c>0, 1, 2, ...</c>) -- avoids any Z-collision with an existing element (a plain
    /// <c>text.Z - 1</c> could collide with whatever's already there, unlike
    /// <see cref="SetAsBackdrop"/>'s own <c>Min(Z) - 1</c>, which is collision-free BY
    /// CONSTRUCTION only because it always targets the absolute bottom). A full renumber keeps
    /// everyone else's RELATIVE order untouched, is simple, and Undo already restores the pre-
    /// renumber Z values for free via the existing whole-state snapshot mechanism.</para></summary>
    [RelayCommand(CanExecute = nameof(CanAddPlateBehindText))]
    private void AddPlateBehindText()
    {
        if (SelectedOverlayElement is not OverlayElementViewModel text)
        {
            return;
        }

        var index = OverlayElements.IndexOf(text);
        if (index < 0)
        {
            return;
        }

        PushUndoSnapshot();
        const double padding = 0.02;
        var plate = CreateBoxElement(NewBoxElementDefaults with
        {
            X = text.X, Y = text.Y,
            Width = text.Width + (padding * 2), Height = text.Height + (padding * 2),
            FillColor = new Rgb24(0, 0, 0), Opacity = 0.6,
            Z = text.Z,
        });

        // _suspendPreview-guarded (same pattern as SetAsBackdrop/Rotate's own multi-element
        // loops) so the up-to-N Z reassignments below don't each independently trigger their own
        // RecomputePreview() pass -- one full pipeline run at the end instead.
        _suspendPreview = true;
        try
        {
            OverlayElements.Insert(index, plate);
            for (var i = 0; i < OverlayElements.Count; i++)
            {
                OverlayElements[i].Z = i;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        SelectedOverlayElement = plate;
        RecomputePreview();
    }
}
