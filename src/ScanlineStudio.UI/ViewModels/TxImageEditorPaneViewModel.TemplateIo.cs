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
    private bool CanSaveTemplate() => !string.IsNullOrWhiteSpace(NewTemplateName) && !IsSavingTemplate;

    /// <summary>Canvas right-click "Save Template" entry point (called from
    /// <c>OnSaveTemplateContextMenuClick</c>, both the canvas's own empty-area menu AND -- 2026-09-19,
    /// user-reported gap -- an image/background element's own menu, since that element's Border stays
    /// unconditionally hit-testable and makes the canvas's own menu unreachable once one exists) --
    /// user-reported gap (2026-09-15): the old behavior just focused an empty name field instead of
    /// saving, requiring the operator to type something first before the shortcut did anything. Now
    /// auto-fills the next free "tmp{N}" name (N starting at 0, user-requested 2026-09-19, case-
    /// insensitive against every existing template's real <see cref="ITemplateStore.ListAsync"/> name
    /// -- same source of truth and same case-insensitive match <see cref="SaveTemplateAsync"/> itself
    /// already uses for its overwrite-by-matching-name check) and saves immediately with that name. If
    /// a name was already typed, this is identical to the ordinary Save button -- the typed name
    /// always wins, auto-naming only fills a genuinely empty field.</summary>
    public async Task SaveTemplateWithAutoNameAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTemplateName))
        {
            try
            {
                var existingNames = (await _templateStore.ListAsync()).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var n = 0;
                while (existingNames.Contains($"tmp{n}"))
                {
                    n++;
                }

                NewTemplateName = $"tmp{n}";
            }
            catch (Exception ex)
            {
                Log.SaveTemplateFailed(_logger, "tmp<auto>", ex);
                StatusMessage = _localization.GetString("Panes.TxImageEditor.SaveTemplateFailed");
                return;
            }
        }

        if (SaveTemplateCommand.CanExecute(null))
        {
            await SaveTemplateCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Phase 5 (spec/15-template-designer.md) -- builds <see cref="PersistedTemplateElement"/>s
    /// from the CURRENT live <see cref="OverlayElements"/> (via <see cref="RawOverlayElements"/>,
    /// already exists), writing any image element's live <see cref="IImageSource"/> pixels to a real
    /// file under the template's own <c>assets/</c> folder via <see cref="_persistenceMapper"/>
    /// BEFORE calling <see cref="ITemplateStore.SaveAsync"/> -- that store's own <c>SaveAsync</c>
    /// only ever writes the manifest + thumbnail, never a per-element asset (see its own doc
    /// comment), since only this VM holds the resolved pixels to write. A successful save also
    /// resets the checkpoint baseline (2026-09-19 user request) -- see
    /// <see cref="_stateIdAtLastCheckpoint"/>'s own doc comment.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private async Task SaveTemplateAsync()
    {
        var name = NewTemplateName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        IsSavingTemplate = true;
        string? existingId = null;
        string? templateId = null;
        long stateIdOfSavedContent;
        try
        {
            // Backlog item (auditor usability review, 2026-08-17): "Saving a template under an
            // existing name creates a duplicate entry, not an update/rename." CreateTemplateId
            // always mints a fresh guid8-suffixed id (see its own doc comment) -- saving under a
            // name that already exists reuses THAT existing id instead, so the save overwrites in
            // place (also preserves any pin referencing it, since pins are keyed by id).
            //
            // Tier B audit finding: this used to resolve existingId from ReadyRack.AllTemplates, a
            // separate VM's own in-memory projection populated by a FIRE-AND-FORGET RefreshAsync
            // call at editor-open time (TxControlsPaneViewModel's own OpenEditorWithLoadedSourceAsync)
            // -- saving before that refresh completes (or after it silently
            // swallowed a transient failure) read a stale, possibly-EMPTY list, so an overwrite of a
            // real existing template could silently degrade into creating a duplicate instead --
            // exactly the bug this whole feature exists to fix. _templateStore.ListAsync() is the
            // actual source of truth (same store SaveAsync/DeleteAsync below both write through).
            existingId = (await _templateStore.ListAsync()).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
            templateId = existingId ?? _templateStore.CreateTemplateId(name);

            // Tier B audit finding (blocker): this used to DeleteAsync(idToReplace) BEFORE writing
            // anything, then unconditionally DeleteAsync(templateId) again in the catch below -- on
            // the overwrite path both target the SAME existing id, so a save that fails ANY time
            // after the up-front delete (SaveAsync alone does a thumbnail render + 2 file writes,
            // any of which can throw: disk full, a network-backed MyPictures, an AV lock, a
            // permissions change) permanently destroyed the operator's PRE-EXISTING template with no
            // way to recover it -- StatusMessage just said "Save template failed." SaveAsync already
            // overwrites template.json/thumbnail.png in place (Directory.CreateDirectory is
            // idempotent), so no up-front delete is needed for the manifest/thumbnail at all. The
            // one thing the up-front delete bought -- not leaving orphaned GUID-named asset PNGs
            // behind from a PRIOR save of the same template (BuildPersistedElementAsync always mints
            // a fresh GUID filename, by design, never reusing/overwriting a prior asset in place) --
            // is deliberately accepted as a lesser, non-destructive trade-off (a slow accumulation of
            // unreferenced files in that one template's own assets/ folder across repeated
            // overwrite-saves) rather than risk deleting real, still-referenced data on a failure
            // path. A proper orphan sweep would need ITemplateStore to expose per-asset deletion,
            // which doesn't exist today and is out of scope for this fix.
            // The checkpoint must identify exactly the state that gets serialized: snapshot the elements
            // and read the state id in the same synchronous block, after the last await before it.
            var elementsToSave = RawOverlayElements;
            stateIdOfSavedContent = _stateId;
            var elements = new List<PersistedTemplateElement>(elementsToSave.Count);
            foreach (var raw in elementsToSave)
            {
                elements.Add(await _persistenceMapper.BuildPersistedElementAsync(templateId, raw, ComputeFittedFontSizeRelative));
            }

            await _templateStore.SaveAsync(templateId, name, new PersistedTemplateDocument(elements));
            NewTemplateName = string.Empty;
            StatusMessage = null;
            // User-requested (2026-09-19): "when you save a template into the template library
            // 'unsaved edits' should be removed and only added again if actual edits are made." A
            // successful save is now a checkpoint, same as a successful load -- the canvas the
            // operator just saved is, by definition, no longer "unsaved" relative to the library.
            // The id of the serialized state, not the live one -- edits made during the awaits
            // above are not in the template, so they must keep the canvas dirty.
            _stateIdAtLastCheckpoint = stateIdOfSavedContent;
            _carriedOverDirtySinceCheckpoint = false;
            OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
            ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
            await ReadyRack.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.SaveTemplateFailed(_logger, name, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.SaveTemplateFailed");

            // existingId is null check preserved (blocker fix): only clean up a folder THIS call
            // freshly minted -- never delete a folder that pre-existed this save attempt. A failure
            // partway through the foreach above (e.g. WritePngAsync throws on element 2 of 3)
            // already wrote element 1's asset PNG under a brand-new templateId's own folder, with no
            // template.json ever referencing it -- ListAsync skips manifest-less folders, so that
            // asset is permanently invisible garbage, one new orphaned folder per failed save, with
            // no way for the operator to ever reach it via this app's own UI. DeleteAsync is safe to
            // call unconditionally on a NEW id: it no-ops if nothing was ever written (directory
            // doesn't exist), and removes the whole folder -- assets included -- if something
            // partial was. Best-effort: a cleanup failure here must not mask or replace the original
            // save failure already logged above.
            if (existingId is null && templateId is { } freshlyMintedId)
            {
                try
                {
                    await _templateStore.DeleteAsync(freshlyMintedId);
                }
                catch (Exception cleanupEx)
                {
                    Log.SaveTemplateCleanupFailed(_logger, freshlyMintedId, cleanupEx);
                }
            }
        }
        finally
        {
            IsSavingTemplate = false;
        }
    }

    /// <summary>Phase 5 -- calls <see cref="ITemplateStore.LoadAsync"/>, reconstructs every image
    /// element's <see cref="IImageSource"/> via the EXISTING <see cref="IImageFileLoader.LoadOriginalAsync"/>
    /// (same loader Phase 2's file source already uses), maps back to <see cref="RawElementSnapshot"/>s,
    /// and feeds them into <see cref="LoadTemplateIntoLiveEditor"/>.</summary>
    /// <summary>Returns whether the template was actually applied -- <see langword="false"/> means a
    /// newer selection superseded this one while it was loading (see the stale-generation branch
    /// below). Accepted-vestigial: this return value was added for the Ready Rack pileup/direct-fire
    /// feature (removed 2026-09-19) to tell "loaded" from "discarded as stale" and abandon a fire
    /// cleanly; the sole remaining caller (<see cref="OnReadyRackTemplateSelected"/>) just awaits and
    /// ignores it, same as before that feature existed (source-compatible either way, so left in
    /// place rather than widening this change into a signature edit).</summary>
    private async Task<bool> LoadTemplateAsync(string templateId, int generation)
    {
        var document = await _templateStore.LoadAsync(templateId);
        if (_disposed || generation != _templateLoadGeneration) return false;
        var snapshots = new List<RawElementSnapshot>(document.Elements.Count);
        foreach (var element in document.Elements)
        {
            snapshots.Add(await _persistenceMapper.ToRawElementSnapshotAsync(templateId, element));
            if (_disposed || generation != _templateLoadGeneration) return false;
        }

        if (_disposed || generation != _templateLoadGeneration)
        {
            // A newer template selection has already started (and will apply ITS OWN result) since
            // this one began -- discard this stale load rather than clobber the canvas with an
            // out-of-date template.
            Log.TemplateLoadDiscardedAsStale(_logger, templateId);
            return false;
        }

        LoadTemplateIntoLiveEditor(snapshots);

        // UX friction fix (Fable operator-perspective review): pre-fills the Save-template name field
        // with the just-loaded template's OWN name, so a "tweak then re-save" workflow doesn't need
        // the operator to retype it from scratch -- SaveTemplateAsync's own existing
        // overwrite-by-matching-name lookup (right above) then does the rest. _templateStore.ListAsync()
        // is deliberately used here, not ReadyRack.AllTemplates -- SaveTemplateAsync's own doc comment
        // right above already documents why AllTemplates can be a stale, possibly-empty in-memory
        // projection (Tier B audit finding), and this same class of staleness would apply here too.
        //
        // Auditor code-review findings, all fixed here: (1) the generation re-check right below is
        // NOT redundant with the one above -- this ListAsync() await is itself a suspension point, so
        // a newer load (which bumps _templateLoadGeneration and sets its OWN NewTemplateName) can
        // finish first; without re-checking, this call's OWN continuation would resume afterward and
        // clobber the newer template's name onto the newer canvas, corrupting the next Save. (2) a
        // miss (loadedMetadata null -- a mid-flight rename/delete, or a manifest ListAsync's own
        // per-template catch skipped) now clears the field instead of leaving it holding whatever
        // OTHER template's name was there before, which SaveTemplateAsync's overwrite-by-matching-name
        // logic would otherwise silently apply to the wrong template on the next save. (3) this lookup
        // is cosmetic, not load-critical -- a failure here must not surface as "Load template failed"
        // for a load that in fact succeeded (OnReadyRackTemplateSelected's catch), so it gets its own
        // try/catch and its own, accurately-worded log message instead of reusing LoadTemplateFailed.
        try
        {
            var loadedMetadata = (await _templateStore.ListAsync()).FirstOrDefault(t => t.Id == templateId);
            if (!_disposed && generation == _templateLoadGeneration)
            {
                NewTemplateName = loadedMetadata?.Name ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.TemplateNamePrefillFailed(_logger, templateId, ex);
        }

        return !_disposed && generation == _templateLoadGeneration;
    }

    /// <summary>Phase 5 -- loads a template into an ALREADY-LIVE editor. NOT a reuse of the
    /// constructor's <see cref="EditorInitialState"/>-seeding block (which only ever ADDS into an
    /// EMPTY <see cref="OverlayElements"/>) -- mirrors <see cref="ApplyState"/>'s own detach/clear/
    /// re-add discipline instead (plan-review blocker 3): every existing element's
    /// <see cref="OnOverlayElementPropertyChanged"/> handler is detached first, the collection
    /// cleared, <see cref="SelectedOverlayElement"/> nulled, before the new elements are added.
    /// Unlike <see cref="ApplyState"/>, deliberately does NOT touch <see cref="CropRect"/>/
    /// adjustments/rotation/<see cref="_templateVariables"/> -- a template load replaces the
    /// OVERLAY LAYOUT, not the photo being edited or the operator's already-typed fill-bar values
    /// (Phase 5 plan: <c>TemplateVariables</c> are deliberately not persisted, and
    /// <see cref="RescanTemplateVariables"/> -- run inside <see cref="RecomputePreview"/> below --
    /// already re-derives which KEYS are referenced from the newly loaded elements' own text,
    /// preserving whatever VALUES are already in <see cref="_templateVariables"/>). Preceded by a
    /// real <see cref="PushUndoSnapshot"/> -- a template load must be undoable, like every other
    /// structural change in this editor. DECIDED: load REPLACES the current document, never
    /// merges.</summary>
    private void LoadTemplateIntoLiveEditor(IReadOnlyList<RawElementSnapshot> snapshots)
    {
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            foreach (var element in OverlayElements)
            {
                element.PropertyChanged -= OnOverlayElementPropertyChanged;
                // T0-11 (production_audit.md): every discarded ImageElementViewModel owns a
                // WriteableBitmap nothing else disposes -- this whole-collection discard is
                // separate from ImageElementViewModel.OnSourceChanged's own dispose-on-reassign
                // (that only covers a SURVIVING element's Source changing, not the element itself
                // being dropped). Widened to a generic IDisposable check (TX editor gap-items plan,
                // item 4b) -- OverlayElementViewModel now owns the same kind of WriteableBitmap when
                // a text element's own picture fill is set, for the same reason.
                if (element is IDisposable disposableElement)
                {
                    disposableElement.Dispose();
                }
            }

            OverlayElements.Clear();
            SelectedOverlayElement = null;

            foreach (var snapshot in snapshots.OrderBy(s => s.Z))
            {
                OverlayElements.Add(CreateElementFromSnapshot(snapshot));
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();

        // Checkpoint reset lives HERE, not in the caller (OnReadyRackTemplateSelected), so it lands
        // right after the load's own PushUndoSnapshot + element swap complete, before any later
        // awaits (yoniq-auditor risk finding -- see OnReadyRackTemplateSelected's own comment). The
        // notify calls must live here too (yoniq-auditor follow-up finding), not just in the caller --
        // OnReadyRackTemplateSelected's own re-notify runs AFTER a further await
        // (LoadTemplateAsync's own name-prefill ListAsync call), so without notifying right here the
        // chip/badge still visibly flash dirty-then-clean across that window, and on a stale-generation
        // discard the caller's re-notify never runs at all, leaving them stuck showing dirty.
        _stateIdAtLastCheckpoint = _stateId;
        _carriedOverDirtySinceCheckpoint = false;
        OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
    }
}
