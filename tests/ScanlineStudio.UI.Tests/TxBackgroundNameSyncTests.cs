using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>The TX file name and the Background card follow the editor's background: a name is shown and
/// recorded only while the background is still the picture it was recorded for.</summary>
public sealed class TxBackgroundNameSyncTests
{
    private const string BlankName = "Panes.TxControls.BlankImageName";

    private static readonly SstvModeDefinition ModeA = new(
        Id: "a", DisplayName: "A", VisCode: 0, ImageWidth: 8, ImageHeight: 8,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly SstvModeDefinition ModeB = ModeA with { Id = "b", DisplayName = "B", ImageWidth = 6, ImageHeight = 6 };

    private static TxControlsPaneViewModel CreateVm() =>
        new(new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] },
            new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) },
            new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService { PathToReturn = "/tmp/a.png" },
            new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeReceivedImageBuffer { Current = new ArrayImageSource(2, 2, new Rgb24[4]) }, new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

    private static async Task<TxImageEditorPaneViewModel> OpenEditorAsync(TxControlsPaneViewModel vm, Func<Task> open)
    {
        TxImageEditorPaneViewModel? opened = null;
        void OnOpened(TxImageEditorPaneViewModel editor) => opened = editor;
        vm.EditorOpened += OnOpened;
        await open();
        Dispatcher.UIThread.RunJobs();
        vm.EditorOpened -= OnOpened;
        Assert.NotNull(opened);
        return opened!;
    }

    private static Task<TxImageEditorPaneViewModel> OpenPickedAsync(TxControlsPaneViewModel vm) =>
        OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

    private static async Task<TxImageEditorPaneViewModel> OpenBlankThenPickInPlaceAsync(TxControlsPaneViewModel vm, bool applyBlankFirst = false)
    {
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        if (applyBlankFirst)
        {
            Apply(editor);
        }

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.HasRealBackground);
        return editor;
    }

    private static Task<TxImageEditorPaneViewModel> SwitchModeAsync(TxControlsPaneViewModel vm) =>
        OpenEditorAsync(vm, () =>
        {
            vm.SelectedMode = ModeB;
            return Task.CompletedTask;
        });

    private static void Apply(TxImageEditorPaneViewModel editor)
    {
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Undo(TxImageEditorPaneViewModel editor)
    {
        editor.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static ImageElementViewModel AddRxImage(TxImageEditorPaneViewModel editor)
    {
        editor.AddLastRxImageCommand.Execute(null);
        return (ImageElementViewModel)Assert.Single(editor.OverlayElements);
    }

    [AvaloniaFact]
    public async Task AppliedBlank_ThenInPlacePick_CardStaysHiddenUntilApply_ThenShowsThePickedName()
    {
        var vm = CreateVm();
        var editor = await OpenBlankThenPickInPlaceAsync(vm, applyBlankFirst: true);

        Assert.Equal(BlankName, vm.SelectedFileName);
        Assert.False(vm.ShowBackgroundPreview);

        Apply(editor);

        Assert.Equal("a.png", vm.SelectedFileName);
        Assert.True(vm.ShowBackgroundPreview);
    }

    [AvaloniaFact]
    public async Task InPlacePick_Apply_Undo_Apply_ShowsAndRecordsNoName()
    {
        var vm = CreateVm();
        var editor = await OpenBlankThenPickInPlaceAsync(vm);
        Apply(editor);
        Assert.Equal("a.png", vm.SelectedFileName);

        Undo(editor);
        Apply(editor);

        Assert.False(editor.HasRealBackground);
        Assert.Null(vm.SelectedFileName);
        Assert.False(vm.ShowBackgroundPreview);
        Assert.True(vm.TransmitCommand.CanExecute(null));
        await vm.TransmitCommand.ExecuteAsync(null);
        Assert.Null(vm.SentFrames[0].SourceFileName);
    }

    [AvaloniaFact]
    public async Task RemoveBackground_Apply_ShowsNoName_UndoThenApply_BringsTheNameBack()
    {
        var vm = CreateVm();
        var editor = await OpenPickedAsync(vm);
        Apply(editor);
        Assert.Equal("a.png", vm.SelectedFileName);

        editor.RemoveBackgroundCommand.Execute(null);
        Apply(editor);
        Assert.Null(vm.SelectedFileName);

        Undo(editor);
        Apply(editor);
        Assert.Equal("a.png", vm.SelectedFileName);
        Assert.True(vm.ShowBackgroundPreview);
    }

    [AvaloniaFact]
    public async Task PromoteBackground_Apply_ShowsNoName_UndoThenApply_BringsTheNameBack()
    {
        var vm = CreateVm();
        var editor = await OpenPickedAsync(vm);

        editor.PromoteBackgroundToBackdropCommand.Execute(null);
        Assert.False(editor.HasRealBackground);
        Apply(editor);
        Assert.Null(vm.SelectedFileName);

        Undo(editor);
        Apply(editor);
        Assert.Equal("a.png", vm.SelectedFileName);
    }

    [AvaloniaFact]
    public async Task DemoteBackdrop_Apply_ShowsNoName()
    {
        var vm = CreateVm();
        var editor = await OpenPickedAsync(vm);
        var backdrop = AddRxImage(editor);
        editor.SetAsBackdropCommand.Execute(backdrop);

        await editor.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);
        Assert.Empty(editor.OverlayElements);
        Apply(editor);

        Assert.Null(vm.SelectedFileName);
        Assert.False(vm.ShowBackgroundPreview);
    }

    [AvaloniaFact]
    public async Task FlattenOntoAPickedPhoto_Apply_KeepsTheName_AndUndoThenApplyStillKeepsIt()
    {
        var vm = CreateVm();
        var editor = await OpenPickedAsync(vm);
        var element = AddRxImage(editor);

        await editor.FlattenElementCommand.ExecuteAsync(element);
        Assert.Empty(editor.OverlayElements);
        Apply(editor);
        Assert.Equal("a.png", vm.SelectedFileName);

        Undo(editor);
        Apply(editor);
        Assert.Equal("a.png", vm.SelectedFileName);
    }

    [AvaloniaFact]
    public async Task FlattenOntoTheBlankCanvas_Apply_ShowsNoNameAndHidesTheCard()
    {
        var vm = CreateVm();
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var element = AddRxImage(editor);

        await editor.FlattenElementCommand.ExecuteAsync(element);
        Assert.Empty(editor.OverlayElements);
        Apply(editor);

        Assert.Null(vm.SelectedFileName);
        Assert.False(vm.ShowBackgroundPreview);
    }

    [AvaloniaFact]
    public async Task Rotate_ThenModeSwitch_ThenApply_KeepsTheName()
    {
        var vm = CreateVm();
        var editor = await OpenPickedAsync(vm);
        editor.RotateCommand.Execute(null);

        var reopened = await SwitchModeAsync(vm);
        Assert.NotSame(editor, reopened);
        Apply(reopened);

        Assert.Equal("a.png", vm.SelectedFileName);
    }

    [AvaloniaFact]
    public async Task RemoveBackground_WithAnElement_ThenModeSwitch_ThenApply_ShowsNoName()
    {
        var vm = CreateVm();
        var editor = await OpenPickedAsync(vm);
        editor.AddOverlayElementCommand.Execute(null); // populated, so the switch rebuilds this editor instead of opening a fresh blank one
        editor.RemoveBackgroundCommand.Execute(null);

        var reopened = await SwitchModeAsync(vm);
        Assert.NotSame(editor, reopened);
        Apply(reopened);

        Assert.Null(vm.SelectedFileName);
        Assert.False(vm.ShowBackgroundPreview);
    }

    [AvaloniaFact]
    public async Task InPlacePick_Undo_ThenModeSwitch_ThenApply_ShowsTheBlankName_CardHidden()
    {
        var vm = CreateVm();
        var editor = await OpenBlankThenPickInPlaceAsync(vm);
        Undo(editor);

        var reopened = await SwitchModeAsync(vm);
        Assert.NotSame(editor, reopened);
        Apply(reopened);

        Assert.Equal(BlankName, vm.SelectedFileName);
        Assert.False(vm.ShowBackgroundPreview);
    }
}
