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
    private static ReadyRackViewModel CreateReadyRack(FakeTemplateStore? templateStore = null, FakeSettingsStore? settingsStore = null) =>
        new(templateStore ?? new FakeTemplateStore(), settingsStore ?? new FakeSettingsStore(), new FakeLocalizationService(), NullLogger<ReadyRackViewModel>.Instance);

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
    // click in a dense row." Arm/confirm -- see DeleteAsync's own doc comment.

    [Fact]
    public async Task DeleteAsync_ClickingADifferentRow_ReArmsForTheNewTargetInsteadOfConfirmingTheOldOne()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        await SaveTemplateAsync(templateStore, "First");
        await SaveTemplateAsync(templateStore, "Second");
        await readyRack.RefreshAsync();
        var first = readyRack.AllTemplates[0];
        var second = readyRack.AllTemplates[1];

        await readyRack.DeleteCommand.ExecuteAsync(first);
        Assert.True(first.IsPendingDelete);

        await readyRack.DeleteCommand.ExecuteAsync(second);

        Assert.False(first.IsPendingDelete);
        Assert.True(second.IsPendingDelete);
        Assert.Equal(2, (await templateStore.ListAsync()).Count); // neither actually deleted yet
    }

    [Fact]
    public async Task DeleteAsync_StillPinnedTemplate_AlsoRemovesItFromTheSlotAndSettings()
    {
        var templateStore = new FakeTemplateStore();
        var settingsStore = new FakeSettingsStore();
        var readyRack = CreateReadyRack(templateStore, settingsStore);
        var templateId = await SaveTemplateAsync(templateStore, "ToDelete");
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row);
        Assert.NotNull(readyRack.Slots[0].Template);

        // Backlog item (auditor usability review, 2026-08-17): DeleteAsync is now arm/confirm --
        // see its own doc comment. The FIRST call only arms (IsPendingDelete flips true, nothing
        // deleted yet); the SECOND call on the same row actually deletes.
        await readyRack.DeleteCommand.ExecuteAsync(readyRack.AllTemplates[0]);
        Assert.True(readyRack.AllTemplates[0].IsPendingDelete);
        Assert.Empty(templateStore.DeletedIds);
        await readyRack.DeleteCommand.ExecuteAsync(readyRack.AllTemplates[0]);

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
}
