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
        new(templateStore ?? new FakeTemplateStore(), settingsStore ?? new FakeSettingsStore(), NullLogger<ReadyRackViewModel>.Instance);

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

        await readyRack.TogglePinCommand.ExecuteAsync(tenthRow);

        Assert.False(tenthRow.IsPinned);
        Assert.All(readyRack.Slots, slot => Assert.NotNull(slot.Template));
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
}
