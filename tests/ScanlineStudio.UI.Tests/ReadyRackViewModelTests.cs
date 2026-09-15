using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Phase 5 (spec/15-template-designer.md, template persistence + ready rack). The
/// LoadCommand/TemplateSelected -> TxImageEditorPaneViewModel.LoadTemplateAsync path is already
/// covered end-to-end by TxImageEditorPaneViewModelTests (LoadTemplate_*) -- this file covers the
/// ReadyRackViewModel-internal logic those tests don't exercise: RecallSlot's own guards (the
/// number-key/numpad accelerator's real call path, TxImageEditorPaneView.axaml.cs's OnRootKeyDown),
/// pin/unpin, and the two dangling-pinned-id sweeps (delete-time and refresh-time).</summary>
public sealed class ReadyRackViewModelTests
{
    private static ReadyRackViewModel CreateReadyRack(
        FakeTemplateStore? templateStore = null, FakeSettingsStore? settingsStore = null, FakeFilePickerService? filePickerService = null) =>
        new(templateStore ?? new FakeTemplateStore(), settingsStore ?? new FakeSettingsStore(), new FakeLocalizationService(),
            filePickerService ?? new FakeFilePickerService(), NullLogger<ReadyRackViewModel>.Instance);

    private static async Task<string> SaveTemplateAsync(FakeTemplateStore store, string name)
    {
        var id = store.CreateTemplateId(name);
        await store.SaveAsync(id, name, new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        return id;
    }

    [Fact]
    public void RecallSlotCommand_EmptySlot_DoesNotRaiseTemplateSelected()
    {
        var readyRack = CreateReadyRack();
        var raised = false;
        readyRack.TemplateSelected += _ => raised = true;

        readyRack.RecallSlotCommand.Execute(1);

        Assert.False(raised);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-1)]
    public void RecallSlotCommand_OutOfRangeSlotNumber_NoOpsWithoutThrowing(int slotNumber)
    {
        var readyRack = CreateReadyRack();
        var raised = false;
        readyRack.TemplateSelected += _ => raised = true;

        readyRack.RecallSlotCommand.Execute(slotNumber);

        Assert.False(raised);
    }

    [Fact]
    public async Task RecallSlotCommand_PinnedSlot_RaisesTemplateSelectedWithThatTemplatesId()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var templateId = await SaveTemplateAsync(templateStore, "Pinned");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row);

        string? selectedId = null;
        readyRack.TemplateSelected += id => selectedId = id;
        readyRack.RecallSlotCommand.Execute(1);

        Assert.Equal(templateId, selectedId);
    }

    /// <summary>Ready Rack direct-fire plan (2026-09-01): DirectFireSlotCommand is a literal
    /// structural clone of RecallSlotCommand, raising TemplateDirectFireRequested instead of
    /// TemplateSelected -- same 3 guard behaviors (empty slot, out-of-range, pinned-slot success),
    /// mirrored here for the new command.</summary>
    [Fact]
    public void DirectFireSlotCommand_EmptySlot_DoesNotRaiseTemplateDirectFireRequested()
    {
        var readyRack = CreateReadyRack();
        var raised = false;
        readyRack.TemplateDirectFireRequested += _ => raised = true;

        readyRack.DirectFireSlotCommand.Execute(1);

        Assert.False(raised);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-1)]
    public void DirectFireSlotCommand_OutOfRangeSlotNumber_NoOpsWithoutThrowing(int slotNumber)
    {
        var readyRack = CreateReadyRack();
        var raised = false;
        readyRack.TemplateDirectFireRequested += _ => raised = true;

        readyRack.DirectFireSlotCommand.Execute(slotNumber);

        Assert.False(raised);
    }

    [Fact]
    public async Task DirectFireSlotCommand_PinnedSlot_RaisesTemplateDirectFireRequestedWithThatTemplatesId()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var templateId = await SaveTemplateAsync(templateStore, "Pinned");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row);

        string? selectedId = null;
        readyRack.TemplateDirectFireRequested += id => selectedId = id;
        readyRack.DirectFireSlotCommand.Execute(1);

        Assert.Equal(templateId, selectedId);
    }

    /// <summary>Confirms the two commands are genuinely independent -- plain RecallSlot must never
    /// raise TemplateDirectFireRequested, and DirectFireSlot must never raise TemplateSelected. A
    /// shared/miswired event would let a plain number-key press silently key the rig.</summary>
    [Fact]
    public async Task RecallSlotCommand_NeverRaisesTemplateDirectFireRequested_AndViceVersa()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Pinned");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row);

        var directFireRaised = false;
        var recallRaised = false;
        readyRack.TemplateDirectFireRequested += _ => directFireRaised = true;
        readyRack.TemplateSelected += _ => recallRaised = true;

        readyRack.RecallSlotCommand.Execute(1);
        Assert.False(directFireRaised);
        Assert.True(recallRaised);

        recallRaised = false;
        readyRack.DirectFireSlotCommand.Execute(1);
        Assert.True(directFireRaised);
        Assert.False(recallRaised);
    }

    [Fact]
    public async Task TogglePinAsync_PinsIntoFirstEmptySlot_ThenUnpinsOnSecondToggle()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Contest");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        await readyRack.TogglePinCommand.ExecuteAsync(row);
        Assert.NotNull(readyRack.Slots[0].Template);
        Assert.True(readyRack.AllTemplates[0].IsPinned);

        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates[0]);
        Assert.Null(readyRack.Slots[0].Template);
        Assert.False(readyRack.AllTemplates[0].IsPinned);
    }

    [Fact]
    public async Task TogglePinAsync_RackAlreadyFull_NewPinIsANoOp()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        for (var i = 0; i < 9; i++)
        {
            await SaveTemplateAsync(templateStore, $"Template{i}");
        }

        await readyRack.RefreshAsync();
        foreach (var row in readyRack.AllTemplates.Take(9))
        {
            await readyRack.TogglePinCommand.ExecuteAsync(row);
        }

        var tenthId = await SaveTemplateAsync(templateStore, "Overflow");
        await readyRack.RefreshAsync();
        var tenthRow = readyRack.AllTemplates.Single(r => r.Id == tenthId);

        // Backlog item (auditor usability review, 2026-08-17): "PIN silently no-ops on a full rack
        // but the toggle visually latches 'pinned' anyway." CanPin is now false for an unpinned row
        // on a full rack, computed fresh by RefreshAsync -- see its own doc comment for why disabling
        // the button pre-emptively is the fix, not chasing a visual desync after the fact.
        Assert.False(tenthRow.CanPin);

        await readyRack.TogglePinCommand.ExecuteAsync(tenthRow);

        Assert.False(tenthRow.IsPinned);
        Assert.All(readyRack.Slots, slot => Assert.NotNull(slot.Template));
    }

    [Fact]
    public async Task RefreshAsync_RackNotFull_EveryRowHasCanPinTrue()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Only one");

        await readyRack.RefreshAsync();

        Assert.All(readyRack.AllTemplates, row => Assert.True(row.CanPin));
    }

    // Backlog item (auditor usability review, 2026-08-17): "Template DELETE is a single unconfirmed
    // click in a dense row." User-reported feedback (2026-09-15): the original arm/confirm (a
    // second click on the SAME row's Delete) went stale once DeleteFromRackAsync got a real confirm
    // dialog -- migrated to match, same shape as DeleteFromRackAsync's own tests below.

    [Fact]
    public async Task DeleteAsync_ConfirmRequestedUnwired_DoesNotDelete()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        await readyRack.DeleteCommand.ExecuteAsync(row);

        Assert.Contains(id, templateStore.Templates.Keys);
    }

    [Fact]
    public async Task DeleteAsync_Declined_DoesNotDelete()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        readyRack.ConfirmRequested = _ => Task.FromResult(false);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        await readyRack.DeleteCommand.ExecuteAsync(row);

        Assert.Contains(id, templateStore.Templates.Keys);
    }

    [Fact]
    public async Task DeleteAsync_RequestsARealConfirmDialogWithTheRightText()
    {
        // Auditor nit: DeleteAsync's dialog and DeleteFromRackAsync's own differ ONLY in wording
        // (the rack's own names a slot number, the list's own doesn't) -- pinning the exact text is
        // the one thing that actually distinguishes them.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        ConfirmActionDialogViewModel? seenConfirmVm = null;
        readyRack.ConfirmRequested = confirmVm =>
        {
            seenConfirmVm = confirmVm;
            return Task.FromResult(false);
        };
        await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        await readyRack.DeleteCommand.ExecuteAsync(row);

        Assert.NotNull(seenConfirmVm);
        // FakeLocalizationService.GetString returns the raw key -- asserting the exact keys proves
        // title/body/both button labels all reach the dialog, not just "some text was set."
        Assert.Equal("Panes.TxImageEditor.ConfirmDeleteTitle", seenConfirmVm!.Title);
        Assert.Equal("Panes.TxImageEditor.ConfirmDeleteBody", seenConfirmVm.Message);
        Assert.Equal("Panes.TxImageEditor.ConfirmDeleteButton", seenConfirmVm.ConfirmLabel);
        Assert.Equal("Panes.TxImageEditor.DialogCancel", seenConfirmVm.CancelLabel);
    }

    [Fact]
    public async Task DeleteAsync_ConfirmedButStoreThrows_SurfacesErrorAndDoesNotDelete_ThenRetrySucceeds()
    {
        // Tier B audit finding, ported from the old arm/confirm's own equivalent test: a failed
        // delete must not silently disappear the error, and a later retry must still be able to
        // succeed. No more "stays armed" special case to test -- every click, retry or not, now
        // goes through the SAME real confirm dialog (ConfirmRequested stays wired the same way
        // across both calls here, exactly like an operator re-clicking Delete after reading the
        // error would).
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        readyRack.ConfirmRequested = _ => Task.FromResult(true);
        await SaveTemplateAsync(templateStore, "First");
        await readyRack.RefreshAsync();
        var row = readyRack.AllTemplates[0];

        templateStore.DeleteExceptionToThrow = new InvalidOperationException("simulated delete failure");
        await readyRack.DeleteCommand.ExecuteAsync(row);

        Assert.NotNull(readyRack.StatusMessage);
        Assert.Empty(templateStore.DeletedIds);

        templateStore.DeleteExceptionToThrow = null;
        await readyRack.DeleteCommand.ExecuteAsync(row);

        Assert.Contains(row.Id, templateStore.DeletedIds);
    }

    [Fact]
    public async Task DeleteAsync_StillPinnedTemplate_AlsoRemovesItFromTheSlotAndSettings()
    {
        var templateStore = new FakeTemplateStore();
        var settingsStore = new FakeSettingsStore();
        var readyRack = CreateReadyRack(templateStore, settingsStore);
        readyRack.ConfirmRequested = _ => Task.FromResult(true);
        var templateId = await SaveTemplateAsync(templateStore, "ToDelete");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row);
        Assert.NotNull(readyRack.Slots[0].Template);

        // Re-fetch the row: TogglePinCommand's own RefreshAsync rebuilt AllTemplates with a fresh
        // instance.
        await readyRack.DeleteCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == templateId));

        Assert.Contains(templateId, templateStore.DeletedIds);
        Assert.Empty(readyRack.AllTemplates);
        Assert.All(readyRack.Slots, slot => Assert.Null(slot.Template));
        var section = (await settingsStore.LoadAsync()).GetSection(
            ReadyRackSettings.SectionKey, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);
        Assert.NotNull(section);
        Assert.Empty(section!.PinnedTemplateIds);
    }

    [Fact]
    public async Task RefreshAsync_PinnedTemplateDeletedOutsideThisViewModel_DropsTheDanglingIdInsteadOfABlankSlot()
    {
        // Real edge case per Phase 5 plan-review: a template folder deleted by hand (or from a
        // different ReadyRackViewModel instance) leaves a pinned id in settings with no matching
        // template. RefreshAsync must sweep it, not leave slot 1 permanently dangling.
        var templateStore = new FakeTemplateStore();
        var settingsStore = new FakeSettingsStore();
        var readyRack = CreateReadyRack(templateStore, settingsStore);
        var templateId = await SaveTemplateAsync(templateStore, "WillBeDeletedElsewhere");
        await readyRack.RefreshAsync();
        await readyRack.TogglePinCommand.ExecuteAsync(Assert.Single(readyRack.AllTemplates));

        // Simulate deletion via a different store instance/session -- bypasses this VM's own
        // DeleteCommand (which would otherwise also sweep the pin list itself), matching the
        // "copied/deleted outside the app" scenario RefreshAsync's own doc comment describes.
        await templateStore.DeleteAsync(templateId);

        await readyRack.RefreshAsync();

        Assert.All(readyRack.Slots, slot => Assert.Null(slot.Template));
        var section = (await settingsStore.LoadAsync()).GetSection(
            ReadyRackSettings.SectionKey, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);
        Assert.NotNull(section);
        Assert.Empty(section!.PinnedTemplateIds);
    }

    [Fact]
    public async Task FilteredTemplates_LibraryFilterText_NarrowsCaseInsensitiveSubstringMatch()
    {
        // Design-fidelity Phase D (mockups/Editwindow): the TEMPLATE LIBRARY panel's own filter box
        // -- first text-filter surface in this codebase, no ICollectionView precedent to copy.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "CQ Call · Wide");
        await SaveTemplateAsync(templateStore, "QSO Reply");
        await SaveTemplateAsync(templateStore, "73 / Thanks");
        await readyRack.RefreshAsync();
        Assert.Equal(3, readyRack.FilteredTemplates.Count);

        readyRack.LibraryFilterText = "cq";

        Assert.Equal(["CQ Call · Wide"], readyRack.FilteredTemplates.Select(t => t.Name));
        Assert.False(readyRack.HasNoFilteredTemplates);

        readyRack.LibraryFilterText = "  ";

        Assert.Equal(3, readyRack.FilteredTemplates.Count);
    }

    [Fact]
    public async Task HasNoFilteredTemplates_TrueOnlyWhenLibraryIsNonEmptyButFilterExcludesEverything()
    {
        var readyRack = CreateReadyRack();
        Assert.False(readyRack.HasNoFilteredTemplates);
        Assert.True(readyRack.HasNoTemplates);

        var templateStore = new FakeTemplateStore();
        readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "CQ Call");
        await readyRack.RefreshAsync();
        Assert.False(readyRack.HasNoFilteredTemplates);

        readyRack.LibraryFilterText = "nomatch";

        Assert.True(readyRack.HasNoFilteredTemplates);
        Assert.False(readyRack.HasNoTemplates);
    }

    [Fact]
    public async Task RefreshAsync_ReappliesTheActiveFilter_SoANewlySavedTemplateRespectsIt()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Contest Serial");
        await readyRack.RefreshAsync();
        readyRack.LibraryFilterText = "contest";
        Assert.Single(readyRack.FilteredTemplates);

        await SaveTemplateAsync(templateStore, "DX Pool · Slow");
        await readyRack.RefreshAsync();

        Assert.Equal(["Contest Serial"], readyRack.FilteredTemplates.Select(t => t.Name));
    }

    // ui_transition_plan.md step 13 (native template bundle export/import) --------------------------

    [Fact]
    public async Task ExportCommand_UserCancelsThePicker_NeverCallsTheStore()
    {
        var templateStore = new FakeTemplateStore();
        var filePickerService = new FakeFilePickerService { SaveTemplateBundlePathToReturn = null };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);
        await SaveTemplateAsync(templateStore, "Field Day");
        await readyRack.RefreshAsync();
        var row = readyRack.AllTemplates[0];

        await readyRack.ExportCommand.ExecuteAsync(row);

        Assert.Empty(templateStore.Exported);
        Assert.False(row.IsExporting);
    }

    [Fact]
    public async Task ExportCommand_HappyPath_CallsStoreWithTheRowsOwnIdAndTheChosenPath_SuggestsTheTemplatesOwnName()
    {
        var templateStore = new FakeTemplateStore();
        var filePickerService = new FakeFilePickerService { SaveTemplateBundlePathToReturn = "/tmp/field-day.sstemplate" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);
        await SaveTemplateAsync(templateStore, "Field Day");
        await readyRack.RefreshAsync();
        var row = readyRack.AllTemplates[0];

        await readyRack.ExportCommand.ExecuteAsync(row);

        var exported = Assert.Single(templateStore.Exported);
        Assert.Equal(row.Id, exported.TemplateId);
        Assert.Equal("/tmp/field-day.sstemplate", exported.DestinationZipPath);
        Assert.Equal("Field Day.sstemplate", filePickerService.LastSuggestedTemplateBundleFileName);
        Assert.False(row.IsExporting);
        Assert.Null(readyRack.StatusMessage);
    }

    [Fact]
    public async Task ExportCommand_StoreThrows_SetsStatusMessageAndClearsIsExportingAfterward()
    {
        var templateStore = new FakeTemplateStore { ExportExceptionToThrow = new InvalidOperationException("simulated export failure") };
        var filePickerService = new FakeFilePickerService { SaveTemplateBundlePathToReturn = "/tmp/out.sstemplate" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);
        await SaveTemplateAsync(templateStore, "Field Day");
        await readyRack.RefreshAsync();
        var row = readyRack.AllTemplates[0];

        await readyRack.ExportCommand.ExecuteAsync(row);

        Assert.NotNull(readyRack.StatusMessage);
        Assert.False(row.IsExporting);
    }

    [Fact]
    public async Task ImportCommand_UserCancelsThePicker_NeverCallsTheStoreOrRefreshes()
    {
        var templateStore = new FakeTemplateStore();
        var filePickerService = new FakeFilePickerService { OpenTemplateBundlePathToReturn = null };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportCommand.ExecuteAsync(null);

        Assert.Empty(readyRack.AllTemplates);
    }

    [Fact]
    public async Task ImportCommand_HappyPath_RefreshesTheListSoTheNewTemplateAppearsImmediately()
    {
        var templateStore = new FakeTemplateStore
        {
            ImportResult = ("Imported Field Day", new PersistedTemplateDocument([])),
        };
        var filePickerService = new FakeFilePickerService { OpenTemplateBundlePathToReturn = "/tmp/in.sstemplate" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportCommand.ExecuteAsync(null);

        Assert.Contains(readyRack.AllTemplates, t => t.Name == "Imported Field Day");
        Assert.Null(readyRack.StatusMessage);
    }

    [Fact]
    public async Task ImportCommand_StoreThrows_SetsStatusMessageAndDoesNotRefresh()
    {
        var templateStore = new FakeTemplateStore { ImportExceptionToThrow = new InvalidOperationException("simulated import failure") };
        await SaveTemplateAsync(templateStore, "Pre-existing");
        var filePickerService = new FakeFilePickerService { OpenTemplateBundlePathToReturn = "/tmp/bad.sstemplate" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportCommand.ExecuteAsync(null);

        Assert.NotNull(readyRack.StatusMessage);
        // Confirms RefreshAsync (which would populate AllTemplates from the store) was never reached.
        Assert.Empty(readyRack.AllTemplates);
    }

    [Fact]
    public async Task ImportLegacyMtmCommand_UserCancelsThePicker_NeverCallsTheStoreOrRefreshes()
    {
        var templateStore = new FakeTemplateStore();
        var filePickerService = new FakeFilePickerService { OpenLegacyMtmTemplatePathToReturn = null };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportLegacyMtmCommand.ExecuteAsync(null);

        Assert.Empty(readyRack.AllTemplates);
    }

    [Fact]
    public async Task ImportLegacyMtmCommand_HappyPathWithNoNotes_RefreshesAndClearsStatusMessage()
    {
        var templateStore = new FakeTemplateStore
        {
            ImportLegacyMtmResult = ("Imported def1", new PersistedTemplateDocument([]), []),
        };
        var filePickerService = new FakeFilePickerService { OpenLegacyMtmTemplatePathToReturn = "/tmp/def1.mtm" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportLegacyMtmCommand.ExecuteAsync(null);

        Assert.Contains(readyRack.AllTemplates, t => t.Name == "Imported def1");
        Assert.Null(readyRack.StatusMessage);
    }

    [Fact]
    public async Task ImportLegacyMtmCommand_SucceedsWithNotes_StillRefreshesButSurfacesAStatusMessage()
    {
        // The import is not a failure when some elements needed approximating -- the template DOES
        // land in the rack, but the user must be told something changed, not left to notice by eye.
        var templateStore = new FakeTemplateStore
        {
            ImportLegacyMtmResult = ("Imported t1", new PersistedTemplateDocument([]), ["A box's dash style was approximated as solid."]),
        };
        var filePickerService = new FakeFilePickerService { OpenLegacyMtmTemplatePathToReturn = "/tmp/t1.mtm" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportLegacyMtmCommand.ExecuteAsync(null);

        Assert.Contains(readyRack.AllTemplates, t => t.Name == "Imported t1");
        Assert.NotNull(readyRack.StatusMessage);
    }

    [Fact]
    public async Task ImportLegacyMtmCommand_RejectedFile_SurfacesTheExceptionMessageDirectlyAndDoesNotRefresh()
    {
        // LegacyMtmFormatException's own message is written to be user-safe -- shown as-is, not
        // replaced by the generic failure string every other exception here falls back to.
        var templateStore = new FakeTemplateStore
        {
            ImportLegacyMtmExceptionToThrow = new LegacyMtmOleNotImportableException(),
        };
        await SaveTemplateAsync(templateStore, "Pre-existing");
        var filePickerService = new FakeFilePickerService { OpenLegacyMtmTemplatePathToReturn = "/tmp/ole.mtm" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportLegacyMtmCommand.ExecuteAsync(null);

        Assert.Contains("OLE", readyRack.StatusMessage);
        Assert.Empty(readyRack.AllTemplates);
    }

    [Fact]
    public async Task ImportLegacyMtmCommand_StoreThrowsAGenericException_SetsTheGenericStatusMessageAndDoesNotRefresh()
    {
        var templateStore = new FakeTemplateStore
        {
            ImportLegacyMtmExceptionToThrow = new InvalidOperationException("simulated I/O failure"),
        };
        await SaveTemplateAsync(templateStore, "Pre-existing");
        var filePickerService = new FakeFilePickerService { OpenLegacyMtmTemplatePathToReturn = "/tmp/bad.mtm" };
        var readyRack = CreateReadyRack(templateStore, filePickerService: filePickerService);

        await readyRack.ImportLegacyMtmCommand.ExecuteAsync(null);

        Assert.NotNull(readyRack.StatusMessage);
        Assert.DoesNotContain("simulated I/O failure", readyRack.StatusMessage);
        Assert.Empty(readyRack.AllTemplates);
    }

    [Fact]
    public async Task SetLoadedTemplate_MarksOnlyTheMatchingSlotAsLoaded()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var idA = await SaveTemplateAsync(templateStore, "A");
        var idB = await SaveTemplateAsync(templateStore, "B");
        await readyRack.RefreshAsync();
        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == idA));
        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == idB));

        readyRack.SetLoadedTemplate(idB);

        Assert.False(readyRack.Slots[0].IsLoaded);
        Assert.True(readyRack.Slots[1].IsLoaded);
    }

    [Fact]
    public async Task SetCanvasDirty_OnlyMarksTheLoadedSlotAsEdited()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == id));
        readyRack.SetLoadedTemplate(id);

        Assert.False(readyRack.Slots[0].IsLoadedAndEdited);
        readyRack.SetCanvasDirty(true);
        Assert.True(readyRack.Slots[0].IsLoadedAndEdited);
        readyRack.SetCanvasDirty(false);
        Assert.False(readyRack.Slots[0].IsLoadedAndEdited);
        // Still loaded either way -- only the "edited" half flips.
        Assert.True(readyRack.Slots[0].IsLoaded);
    }

    [Fact]
    public async Task RefreshAsync_ReappliesLoadedStateToFreshSlotInstances()
    {
        // Slots[i].Template is a FRESH TemplateListRowViewModel every refresh -- IsLoaded/
        // IsLoadedAndEdited live on the SLOT, not the row, specifically so a refresh triggered by an
        // unrelated mutation (rename/pin/delete of a DIFFERENT template) can't silently clear the
        // badge on this one.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var idA = await SaveTemplateAsync(templateStore, "A");
        var idB = await SaveTemplateAsync(templateStore, "B");
        await readyRack.RefreshAsync();
        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == idA));
        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == idB));
        readyRack.SetLoadedTemplate(idA);

        await readyRack.RefreshAsync();

        Assert.True(readyRack.Slots[0].IsLoaded);
    }

    [Fact]
    public async Task DeleteFromRackAsync_ConfirmRequestedUnwired_DoesNotDelete()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        await readyRack.DeleteFromRackCommand.ExecuteAsync(row);

        Assert.Contains(id, templateStore.Templates.Keys);
    }

    [Fact]
    public async Task DeleteFromRackAsync_Declined_DoesNotDelete()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        readyRack.ConfirmRequested = _ => Task.FromResult(false);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        await readyRack.DeleteFromRackCommand.ExecuteAsync(row);

        Assert.Contains(id, templateStore.Templates.Keys);
    }

    [Fact]
    public async Task DeleteFromRackAsync_Confirmed_DeletesAndClearsTheLoadedBadgeIfItWasLoaded()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        readyRack.ConfirmRequested = _ => Task.FromResult(true);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row);
        readyRack.SetLoadedTemplate(id);

        // Re-fetch the row: TogglePinCommand's own RefreshAsync rebuilt AllTemplates with a fresh
        // instance.
        await readyRack.DeleteFromRackCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == id));

        Assert.DoesNotContain(id, templateStore.Templates.Keys);
        Assert.Empty(readyRack.AllTemplates);
        Assert.False(readyRack.Slots[0].IsLoaded);
    }

    [Fact]
    public async Task TogglePinAsync_UnpinningTheLoadedTemplate_ClearsTheLoadedBadgeWithoutDeleting()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = readyRack.AllTemplates.Single(t => t.Id == id);
        await readyRack.TogglePinCommand.ExecuteAsync(row);
        readyRack.SetLoadedTemplate(id);
        Assert.True(readyRack.Slots[0].IsLoaded);

        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == id));

        Assert.False(readyRack.Slots[0].IsLoaded);
        Assert.Contains(id, templateStore.Templates.Keys); // still exists, just unpinned
    }

    [Fact]
    public async Task RenameAsync_EmptyEditingName_RevertsWithoutCallingTheStore()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Original");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        row.EditingName = "   ";

        await readyRack.RenameCommand.ExecuteAsync(row);

        Assert.Equal("Original", row.EditingName);
        Assert.Equal("Original", Assert.Single(readyRack.AllTemplates).Name);
    }

    [Fact]
    public async Task RenameAsync_ValidName_RenamesInPlaceAndRefreshes()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "Original");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        row.EditingName = "Renamed";

        await readyRack.RenameCommand.ExecuteAsync(row);

        var refreshed = Assert.Single(readyRack.AllTemplates);
        Assert.Equal("Renamed", refreshed.Name);
        Assert.Equal(id, refreshed.Id); // rename never changes the id/folder
    }

    [Fact]
    public async Task RenameAsync_StoreThrows_SetsFailureStatusAndDoesNotRefresh()
    {
        var templateStore = new FakeTemplateStore { RenameExceptionToThrow = new InvalidOperationException("simulated I/O failure") };
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Original");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        row.EditingName = "Renamed";

        await readyRack.RenameCommand.ExecuteAsync(row);

        Assert.NotNull(readyRack.StatusMessage);
        Assert.DoesNotContain("simulated I/O failure", readyRack.StatusMessage);
        Assert.Equal("Original", Assert.Single(readyRack.AllTemplates).Name);
    }

    [Fact]
    public async Task RenameAsync_NameAlreadyUsedByAnotherTemplate_RejectsAndReverts()
    {
        // yoniq-auditor finding: SaveTemplateAsync resolves its overwrite target by matching NAME
        // (case-insensitive) -- renaming B to A's name would let a later Save silently overwrite
        // whichever of the two same-named templates ListAsync happens to enumerate first. Rejected
        // up front instead.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "A");
        var idB = await SaveTemplateAsync(templateStore, "B");
        await readyRack.RefreshAsync();
        var rowB = readyRack.AllTemplates.Single(t => t.Id == idB);
        rowB.EditingName = "A";

        await readyRack.RenameCommand.ExecuteAsync(rowB);

        Assert.Equal("B", rowB.EditingName); // reverted, not "A"
        Assert.NotNull(readyRack.StatusMessage);
        Assert.Equal("B", Assert.Single(readyRack.AllTemplates, t => t.Id == idB).Name);
        Assert.Equal(2, readyRack.AllTemplates.Count); // both templates still exist, unchanged
    }

    [Fact]
    public async Task RenameAsync_SameNameAsSelf_IsANoOpNotACollision()
    {
        // Renaming a template to its OWN current name (whitespace trimmed differently, or just
        // re-confirming) must not trip the duplicate-name rejection against itself.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        row.EditingName = "A";

        await readyRack.RenameCommand.ExecuteAsync(row);

        Assert.Null(readyRack.StatusMessage);
        Assert.Equal("A", Assert.Single(readyRack.AllTemplates, t => t.Id == id).Name);
    }

    [Fact]
    public async Task SelectedLibraryItem_SurvivesARefresh_WithAFreshInstanceOfTheSameId()
    {
        // Unlike Slots (a stable wrapper whose own .Template swaps), AllTemplates/FilteredTemplates
        // get entirely FRESH row instances on every RefreshAsync -- a naive "leave SelectedLibraryItem
        // alone" would silently point it at a discarded instance no ListBox could ever show as
        // selected again once a rename/pin/delete ELSEWHERE triggers a refresh.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var id = await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var firstInstance = Assert.Single(readyRack.AllTemplates);
        readyRack.SelectedLibraryItem = firstInstance;

        await readyRack.RefreshAsync();

        Assert.NotNull(readyRack.SelectedLibraryItem);
        Assert.Equal(id, readyRack.SelectedLibraryItem!.Id);
        Assert.NotSame(firstInstance, readyRack.SelectedLibraryItem);
    }

    [Fact]
    public async Task SelectedLibraryItem_ClearsWhenTheUnderlyingTemplateIsDeleted()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "A");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        readyRack.SelectedLibraryItem = row;

        await templateStore.DeleteAsync(row.Id);
        await readyRack.RefreshAsync();

        Assert.Null(readyRack.SelectedLibraryItem);
    }

    [Fact]
    public async Task IsGridView_RoundTripsThroughSettingsAcrossTwoInstances()
    {
        var settingsStore = new FakeSettingsStore();
        var first = CreateReadyRack(settingsStore: settingsStore);
        await first.RefreshAsync(); // triggers the one-time settings load
        Assert.False(first.IsGridView); // default

        first.SelectGridViewCommand.Execute(null);

        // A fresh instance (a real editor re-open) must pick up the persisted choice.
        var second = CreateReadyRack(settingsStore: settingsStore);
        await second.RefreshAsync();

        Assert.True(second.IsGridView);
    }

    [Fact]
    public void SelectListViewAndSelectGridViewCommands_SetIsGridViewDirectly()
    {
        // Deliberately NOT exercised via a TwoWay-bound RadioButton IsChecked in this test -- see
        // TxImageEditorPaneView.axaml's own comment on the toggle for why (a TwoWay-bound negated
        // pair sharing one GroupName caused a real binding/group-exclusivity feedback loop that
        // pinned a test host at ~100% CPU). These commands are the only write path to IsGridView
        // from the view; IsChecked itself is Mode=OneWay only.
        var readyRack = CreateReadyRack();

        readyRack.SelectGridViewCommand.Execute(null);
        Assert.True(readyRack.IsGridView);

        readyRack.SelectListViewCommand.Execute(null);
        Assert.False(readyRack.IsGridView);
    }

    [Fact]
    public async Task LibraryFilterText_ExcludingTheSelectedRow_ClearsTheSelection()
    {
        // yoniq-auditor finding: RefreshFilteredTemplates used to leave SelectedLibraryItem
        // pointing at a row no longer in FilteredTemplates once a typed filter excluded it -- the VM
        // and the ListBox could disagree about what's selected. Explicit clear keeps them consistent.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Alpha");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        readyRack.SelectedLibraryItem = row;

        readyRack.LibraryFilterText = "Zulu"; // excludes "Alpha"

        Assert.Null(readyRack.SelectedLibraryItem);
    }

    [Fact]
    public async Task LibraryFilterText_StillMatchingTheSelectedRow_LeavesTheSelectionAlone()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "Alpha");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        readyRack.SelectedLibraryItem = row;

        readyRack.LibraryFilterText = "Alp";

        Assert.Same(row, readyRack.SelectedLibraryItem);
    }
}
