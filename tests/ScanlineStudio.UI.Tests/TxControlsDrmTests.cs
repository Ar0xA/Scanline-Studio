using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>P4b: DRM as a TX target -- geometry, auto-follow, transmit settings, estimate scheduling, history,
/// settings ownership and the codec-unavailable state.</summary>
public sealed class TxControlsDrmTests
{
    // Same width as DRM's default 640×480, so only the DRM guard stops auto-follow from switching.
    private static readonly SstvModeDefinition Wide640 = new(
        Id: "pd-like", DisplayName: "PD-like", VisCode: 95, ImageWidth: 640, ImageHeight: 496, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly SstvModeDefinition Small = new(
        Id: "small", DisplayName: "Small", VisCode: 8, ImageWidth: 4, ImageHeight: 3, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private sealed class Rig
    {
        public FakeSstvSessionService Session { get; } = new() { AvailableModes = [Small, Wide640] };

        public FakeSettingsStore Settings { get; } = new();

        public FakeRadioSessionService Radio { get; } = new();

        public FakeLocalizationService Localization { get; } = new();

        public FakeImageFileLoader Loader { get; } = new() { ResultToReturn = new ArrayImageSource(8, 6, new Rgb24[48]) };

        public FakeStockImageLibrary Stock { get; } = new() { FullImageToReturn = new ArrayImageSource(8, 6, new Rgb24[48]) };

        public FakeReceivedImageBuffer Received { get; } = new();

        public FakeFilePickerService Picker { get; } = new() { PathToReturn = "/home/op/pictures/holiday photo.png" };

        public TxControlsPaneViewModel Create()
        {
            var vm = new TxControlsPaneViewModel(
                Session, Loader, Stock, new FakeTransmitImagePreparer(), Picker, Localization, Settings, Radio,
                new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance,
                Received, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
            vm.EditorOpened += editor => LastEditor = editor;
            Dispatcher.UIThread.RunJobs();
            return vm;
        }

        public TxImageEditorPaneViewModel? LastEditor { get; private set; }

        public void Store<T>(string key, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
            Settings.Settings = Settings.Settings.WithSection(key, value, info);
    }

    private static void SelectDrm(TxControlsPaneViewModel vm)
    {
        vm.SelectedTarget = vm.AvailableTargets.OfType<DrmTarget>().Single();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<TxImageEditorPaneViewModel> BrowseAndApplyAsync(Rig rig, TxControlsPaneViewModel vm)
    {
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var editor = rig.LastEditor!;
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return editor;
    }

    private static IImageSource? LoadedImage(TxControlsPaneViewModel vm) =>
        typeof(TxControlsPaneViewModel).GetField("_loadedImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm) as IImageSource;

    private static TxImageEditorPaneViewModel? CurrentEditor(TxControlsPaneViewModel vm) =>
        typeof(TxControlsPaneViewModel).GetField("_currentEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm) as TxImageEditorPaneViewModel;

    private static void AutoFollowOn(Rig rig) =>
        rig.Store(TxPaneUiSettings.SectionKey, new TxPaneUiSettings { AutoFollowRxMode = true }, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);

    // Test 1: auto-follow never leaves DRM -- closed, blank, and populated (same-width) editor.
    [AvaloniaFact]
    public void AutoFollow_UnderDrm_WithNoEditorOpen_StaysOnDrm()
    {
        var rig = new Rig();
        AutoFollowOn(rig);
        var vm = rig.Create();
        SelectDrm(vm);
        Assert.False(vm.IsEditorOpen);

        rig.Session.RaiseModeDetected(Wide640);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<DrmTarget>(vm.SelectedTarget);
        Assert.Equal("Panes.TxControls.AutoPicksValue.SstvOnly", vm.AutoPicksText);
    }

    [AvaloniaFact]
    public async Task AutoFollow_UnderDrm_WithABlankEditor_StaysOnDrm()
    {
        var rig = new Rig();
        AutoFollowOn(rig);
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.OpenBlankEditorCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsEditorOpen);

        rig.Session.RaiseModeDetected(Small);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<DrmTarget>(vm.SelectedTarget);
    }

    [AvaloniaFact]
    public async Task AutoFollow_UnderDrm_WithAPopulatedEditorOfTheSameWidth_StaysOnDrm()
    {
        var rig = new Rig();
        AutoFollowOn(rig);
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        rig.Session.RaiseModeDetected(Wide640);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<DrmTarget>(vm.SelectedTarget);
    }

    [AvaloniaFact]
    public void AutoFollow_UnderSstv_StillFollows()
    {
        var rig = new Rig();
        AutoFollowOn(rig);
        var vm = rig.Create();

        rig.Session.RaiseModeDetected(Wide640);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(Wide640, vm.SelectedMode);
    }

    // Test 2: every way into the editor composes for the DRM geometry.
    [AvaloniaFact]
    public async Task Sources_UnderDrm_OpenTheEditorAtTheDrmGeometry()
    {
        foreach (var open in new Func<Rig, TxControlsPaneViewModel, Task>[]
        {
            (_, vm) => vm.SelectImageCommand.ExecuteAsync(null),
            (rig, vm) => vm.SelectStockImageCommand.ExecuteAsync(new StockImageEntry("sunset", "sunset.jpg", "/stock/sunset.jpg")),
            (rig, vm) => { rig.Received.Current = new ArrayImageSource(8, 6, new Rgb24[48]); return vm.CopyReceivedImageToTxCommand.ExecuteAsync(null); },
            (_, vm) => vm.OpenBlankEditorCommand.ExecuteAsync(null),
        })
        {
            var rig = new Rig();
            var vm = rig.Create();
            SelectDrm(vm);
            vm.DrmSizeIndex = (int)DrmSizePreset.Size800x600;
            Dispatcher.UIThread.RunJobs();

            await open(rig, vm);
            Dispatcher.UIThread.RunJobs();

            var editor = CurrentEditor(vm)!;
            Assert.Equal("drm-800x600", editor.TargetModeId);
            Assert.Equal(600, editor.PreviewMaxHeight);
            editor.ApplyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((800, 600), (LoadedImage(vm)!.Width, LoadedImage(vm)!.Height));
        }
    }

    // Test 3: Apply & Transmit sends profile, raw callsign, size target and the file-name rule.
    [AvaloniaFact]
    public async Task ApplyAndTransmit_UnderDrm_ReachesTransmitDrmWithTheSelectedSettings()
    {
        var rig = new Rig();
        rig.Store(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "DLÄ1ABC/PORTABLE" }, OperatorSettingsJsonContext.Default.OperatorSettings);
        rig.Store(DrmSettings.SectionKey, new DrmSettings { ActiveProfileIndex = 0, TargetKb = 30, VoxTone = true }, DrmSettingsJsonContext.Default.DrmSettings);
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        rig.LastEditor!.ApplyAndTransmitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var (image, fileName, settings) = Assert.Single(rig.Session.TransmitDrmCalls);
        Assert.Empty(rig.Session.TransmitCalls);
        Assert.Equal((640, 480), (image.Width, image.Height));
        Assert.Equal("holiday photo.png", fileName);
        var robust = DrmSettingsDefaults.Robust;
        Assert.Equal(new DrmTxSettings(robust.Mode, robust.Bandwidth, robust.Qam, robust.Protection, robust.Interleave, robust.ReedSolomon, "DLÄ1ABC/PORTABLE", true, 30_000), settings);
        Assert.Equal("DLD1ABC/P", vm.DrmCallsignDisplay);
        Assert.True(vm.DrmCallsignDiffers);
    }

    [AvaloniaFact]
    public async Task Transmit_UnderDrm_FromABlankCanvas_UsesTheDefaultFileName()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.OpenBlankEditorCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        rig.LastEditor!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        await vm.TransmitCommand.ExecuteAsync(null);

        Assert.Equal("image", Assert.Single(rig.Session.TransmitDrmCalls).FileName);
    }

    // Test 4: SSTV→DRM reflows the applied image; a size change rebuilds the editor; a profile change does not.
    [AvaloniaFact]
    public async Task SwitchingToDrm_ReflowsTheAppliedImage_SizeRebuildsTheEditor_ProfileDoesNot()
    {
        var rig = new Rig();
        var vm = rig.Create();
        await BrowseAndApplyAsync(rig, vm);
        Assert.Equal((4, 3), (LoadedImage(vm)!.Width, LoadedImage(vm)!.Height));

        SelectDrm(vm);

        Assert.Equal((640, 480), (LoadedImage(vm)!.Width, LoadedImage(vm)!.Height));
        Assert.True(vm.TransmitCommand.CanExecute(null));
        var drmEditor = CurrentEditor(vm)!;
        Assert.Equal("drm-640x480", drmEditor.TargetModeId);

        vm.DrmSizeIndex = (int)DrmSizePreset.Size320x240;
        Dispatcher.UIThread.RunJobs();

        var resizedEditor = CurrentEditor(vm)!;
        Assert.NotSame(drmEditor, resizedEditor);
        Assert.Equal("drm-320x240", resizedEditor.TargetModeId);
        Assert.Equal((320, 240), (LoadedImage(vm)!.Width, LoadedImage(vm)!.Height));
        var imageBeforeProfile = LoadedImage(vm);

        vm.DrmProfileIndex = 2;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(resizedEditor, CurrentEditor(vm));
        Assert.Same(imageBeforeProfile, LoadedImage(vm));
    }

    // Test 6: single flight, latest wins, nothing while transmitting, and a profile-only change recounts.
    [AvaloniaFact]
    public async Task Estimate_SupersededResultIsDropped_OneEncodeAtATime_ProfileChangeOnlyRecounts()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        await BrowseAndApplyAsync(rig, vm);
        var firstGate = new TaskCompletionSource<DrmEstimate>();
        rig.Session.EstimateGates.Enqueue(firstGate);

        var first = vm.FlushDrmEstimateAsync();
        await WaitForAsync(() => rig.Session.EstimateDrmCalls.Count == 1);
        Assert.Single(rig.Session.EstimateDrmCalls);
        vm.DrmTargetKb = 55;
        await vm.FlushDrmEstimateAsync();
        Assert.Single(rig.Session.EstimateDrmCalls);

        firstGate.SetResult(new DrmEstimate(new byte[11_000], TimeSpan.FromSeconds(30)));
        await first;
        await WaitForAsync(() => rig.Localization.Calls.Exists(c => c.Key == "Panes.TxControls.Drm.EstimateFormat"));

        Assert.Equal(2, rig.Session.EstimateDrmCalls.Count);
        Assert.Equal(55_000, rig.Session.EstimateDrmCalls[1].Settings.TargetJp2Bytes);
        Assert.Equal(1, rig.Session.MaxEstimatesInFlight);
        var shown = rig.Localization.Calls.Where(c => c.Key == "Panes.TxControls.Drm.EstimateFormat").ToList();
        Assert.NotEmpty(shown);
        Assert.All(shown, c => Assert.Equal(20.0, (double)c.Args[0]));

        vm.DrmProfileIndex = 2;
        await vm.FlushDrmEstimateAsync();

        Assert.Equal(2, rig.Session.EstimateDrmCalls.Count);
        var recount = Assert.Single(rig.Session.EstimateDrmDurationCalls);
        Assert.Equal(DrmQamOrder.Qam64, recount.Settings.Qam);
    }

    [AvaloniaFact]
    public async Task Estimate_DoesNotRunWhileTransmitting_AndRerunsWhenTxEnds()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        await BrowseAndApplyAsync(rig, vm);
        await vm.FlushDrmEstimateAsync();
        Assert.Single(rig.Session.EstimateDrmCalls);
        var gate = new TaskCompletionSource();
        rig.Session.TransmitGate = gate;
        var transmit = vm.TransmitCommand.ExecuteAsync(null);
        Assert.True(vm.IsTransmitting);

        vm.DrmTargetKb = 60;
        await vm.FlushDrmEstimateAsync();
        Assert.Single(rig.Session.EstimateDrmCalls);
        Assert.False(vm.IsDrmEstimateScheduled);

        gate.SetResult();
        await transmit;
        Assert.True(vm.IsDrmEstimateScheduled);
        await vm.FlushDrmEstimateAsync();

        Assert.Equal(2, rig.Session.EstimateDrmCalls.Count);
        Assert.Equal(60_000, rig.Session.EstimateDrmCalls[1].Settings.TargetJp2Bytes);
    }

    // Test 8: the loopback self-test is SSTV only.
    [AvaloniaFact]
    public async Task LoopbackSelfTest_IsDisabledForDrm()
    {
        var rig = new Rig();
        var vm = rig.Create();
        await BrowseAndApplyAsync(rig, vm);
        Assert.True(vm.RunLoopbackSelfTestCommand.CanExecute(null));

        SelectDrm(vm);

        Assert.False(vm.RunLoopbackSelfTestCommand.CanExecute(null));
    }

    // Test 9: codec unavailable.
    [AvaloniaFact]
    public async Task CodecUnavailable_ShowsTheMessage_AndDisablesTransmitForDrmOnly()
    {
        var rig = new Rig();
        rig.Session.DrmTransmitAvailable = false;
        var vm = rig.Create();
        await BrowseAndApplyAsync(rig, vm);
        Assert.False(vm.IsDrmUnavailableMessageVisible);
        Assert.True(vm.TransmitCommand.CanExecute(null));

        SelectDrm(vm);

        Assert.True(vm.IsDrmUnavailableMessageVisible);
        Assert.False(vm.TransmitCommand.CanExecute(null));
        Assert.False(CurrentEditor(vm)!.ApplyAndTransmitCommand.CanExecute(null));
        await vm.TransmitCommand.ExecuteAsync(null);
        Assert.Empty(rig.Session.TransmitDrmCalls);
    }

    // Test 11: defaults for bad stored values; profile selection; the two writers.
    [Fact]
    public void Settings_OutOfRangeValues_ResolveToDefaults()
    {
        var stored = new DrmSettings
        {
            Profiles = [new DrmProfileSettings { Mode = (DrmRobustnessMode)99, Qam = (DrmQamOrder)(-1), ReedSolomon = DrmReedSolomonLevel.Rs4, Name = "  " }],
            Custom = new DrmProfileSettings { Interleave = (DrmInterleaving)7 },
            ActiveProfileIndex = 7,
            SizePreset = (DrmSizePreset)42,
            TargetKb = 500,
        };

        var robust = DrmSettingsDefaults.ResolveProfile(stored, 0);
        Assert.Equal(DrmSettingsDefaults.Robust with { ReedSolomon = DrmReedSolomonLevel.Rs4 }, robust);
        Assert.Equal(DrmSettingsDefaults.Fast, DrmSettingsDefaults.ResolveProfile(stored, 2));
        Assert.Equal(DrmSettingsDefaults.Normal, DrmSettingsDefaults.ResolveProfile(stored, DrmSettingsDefaults.CustomProfileIndex));
        Assert.Null(DrmSettingsDefaults.StoredProfileName(stored, 0));
        Assert.Equal(1, DrmSettingsDefaults.ResolveActiveProfileIndex(stored));
        Assert.Equal(DrmSizePreset.Size640x480, DrmSettingsDefaults.ResolveSizePreset(stored));
        Assert.Equal(100, DrmSettingsDefaults.ResolveTargetKb(stored));
        Assert.False(DrmSettingsDefaults.ResolveVoxTone(stored));
        Assert.Equal(DrmSettingsDefaults.Normal, DrmSettingsDefaults.ResolveProfile(null, 1));
    }

    [AvaloniaFact]
    public void SelectingAProfile_WritesItsFields_AndStaysOnIt_AnEditFlipsToCustom()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        Assert.Equal(1, vm.DrmProfileIndex);
        Assert.Equal(DrmSettingsDefaults.Normal, vm.CurrentDrmProfile);

        vm.DrmProfileIndex = 2;

        Assert.Equal(2, vm.DrmProfileIndex);
        Assert.Equal(DrmSettingsDefaults.Fast, vm.CurrentDrmProfile);

        vm.DrmReedSolomonIndex = (int)DrmReedSolomonLevel.Rs3;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DrmSettingsDefaults.CustomProfileIndex, vm.DrmProfileIndex);
        var stored = rig.Settings.Settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings)!;
        Assert.Equal(DrmSettingsDefaults.CustomProfileIndex, stored.ActiveProfileIndex);
        Assert.Equal(DrmSettingsDefaults.Fast with { ReedSolomon = DrmReedSolomonLevel.Rs3 }, DrmSettingsDefaults.ResolveProfile(stored, DrmSettingsDefaults.CustomProfileIndex));
    }

    [AvaloniaFact]
    public async Task OptionsSave_AndTxPaneSave_DoNotRevertEachOthersFields()
    {
        var rig = new Rig();
        rig.Store(DrmSettings.SectionKey, new DrmSettings { ActiveProfileIndex = 2, SizePreset = DrmSizePreset.Size800x600, TargetKb = 50, VoxTone = true }, DrmSettingsJsonContext.Default.DrmSettings);
        var tx = rig.Create();
        var options = new OptionsWindowViewModel(TestOptionsSettings.Create(rig.Settings), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), rig.Settings, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.True(options.DrmVoxTone);

        tx.DrmSizeIndex = (int)DrmSizePreset.Size320x240;
        Dispatcher.UIThread.RunJobs();
        options.DrmVoxTone = false;
        options.DrmProfiles[0].Name = "Contest";
        options.DrmProfiles[0].ReedSolomonIndex = (int)DrmReedSolomonLevel.Rs4;
        await options.ApplyCommand.ExecuteAsync(null);

        var afterOptions = rig.Settings.Settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings)!;
        Assert.Equal((DrmSizePreset.Size320x240, 2, 50), (afterOptions.SizePreset, afterOptions.ActiveProfileIndex, afterOptions.TargetKb));
        Assert.False(afterOptions.VoxTone);

        await tx.ReloadDrmOptionsAsync();
        tx.DrmProfileIndex = 0;
        Dispatcher.UIThread.RunJobs();

        var afterTx = rig.Settings.Settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings)!;
        Assert.Equal(0, afterTx.ActiveProfileIndex);
        Assert.False(afterTx.VoxTone);
        Assert.Equal("Contest", afterTx.Profiles![0]!.Name);
        Assert.Equal(DrmReedSolomonLevel.Rs4, tx.CurrentDrmProfile.ReedSolomon);
        Assert.Equal("Contest", tx.DrmProfileOptions[0]);
    }

    // Test 12: history caption and resend.
    [AvaloniaFact]
    public async Task DrmHistoryEntry_CaptionUsesTheSentJp2Size_AndResendReplaysTheStoredSettings()
    {
        var rig = new Rig();
        rig.Store(DrmSettings.SectionKey, new DrmSettings { TargetKb = 20 }, DrmSettingsJsonContext.Default.DrmSettings);
        rig.Session.DrmResultToReturn = new DrmTxResult(42_400, "20260101000000-holiday photo.jp2");
        var vm = rig.Create();
        SelectDrm(vm);
        await BrowseAndApplyAsync(rig, vm);

        await vm.TransmitCommand.ExecuteAsync(null);

        var entry = Assert.Single(vm.SentFrames);
        Assert.Equal(42_400, entry.Jp2Bytes);
        Assert.Equal("DRM", entry.ModeToken);
        var caption = rig.Localization.Calls.Last(c => c.Key == "Panes.TxControls.Drm.HistoryCaptionFormat");
        Assert.Equal(("B", "Drm.Qam.Short.Qam16", 42.4), ((string)caption.Args[0], (string)caption.Args[1], (double)caption.Args[2]));

        rig.Store(DrmSettings.SectionKey, new DrmSettings { TargetKb = 60 }, DrmSettingsJsonContext.Default.DrmSettings);
        await vm.ReloadDrmOptionsAsync();
        await vm.ResendSentFrameCommand.ExecuteAsync(entry);

        Assert.Equal(2, rig.Session.TransmitDrmCalls.Count);
        var resend = rig.Session.TransmitDrmCalls[1];
        Assert.Same(entry.Image, resend.Image);
        Assert.Equal("holiday photo.png", resend.FileName);
        Assert.Equal(entry.DrmSettings, resend.Settings);
    }

    [AvaloniaFact]
    public async Task StoppedDrmEntry_CaptionShowsTheTargetSize()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        await BrowseAndApplyAsync(rig, vm);
        var gate = new TaskCompletionSource();
        rig.Session.TransmitGate = gate;
        var transmit = vm.TransmitCommand.ExecuteAsync(null);
        rig.Session.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Dispatcher.UIThread.RunJobs();

        gate.SetCanceled();
        await transmit;

        var entry = Assert.Single(vm.SentFrames);
        Assert.Equal(TxHistoryOutcome.Stopped, entry.Outcome);
        Assert.Null(entry.Jp2Bytes);
        var caption = rig.Localization.Calls.Last(c => c.Key == "Panes.TxControls.Drm.HistoryCaptionFormat");
        Assert.Equal(20.0, (double)caption.Args[2]);
    }

    private static async Task<string> TransmitAndGetFileNameAsync(Rig rig, TxControlsPaneViewModel vm)
    {
        await vm.TransmitCommand.ExecuteAsync(null);
        return rig.Session.TransmitDrmCalls[^1].FileName;
    }

    private static void NewTemplate(TxControlsPaneViewModel vm)
    {
        var editor = CurrentEditor(vm)!;
        editor.ConfirmRequested = _ => Task.FromResult(true);
        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    // Follow-up 1: the on-air name follows the APPLIED image, not a picture loaded or removed since.
    [AvaloniaFact]
    public async Task OnAirName_FollowsTheAppliedImage_AcrossNewTemplateInPlaceLoadRemoveAndUndo()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        var editor = await BrowseAndApplyAsync(rig, vm);
        Assert.Equal("holiday photo.png", await TransmitAndGetFileNameAsync(rig, vm));

        NewTemplate(vm);
        Assert.Equal("holiday photo.png", await TransmitAndGetFileNameAsync(rig, vm));

        editor = await BrowseAndApplyAsync(rig, vm);
        editor.RemoveBackgroundCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        rig.Picker.PathToReturn = "/home/op/pictures/beacon.png";
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(editor, CurrentEditor(vm));
        Assert.Equal("holiday photo.png", await TransmitAndGetFileNameAsync(rig, vm));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("beacon.png", await TransmitAndGetFileNameAsync(rig, vm));

        editor.RemoveBackgroundCommand.Execute(null);
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("image", await TransmitAndGetFileNameAsync(rig, vm));

        editor.UndoCommand.Execute(null);
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("beacon.png", await TransmitAndGetFileNameAsync(rig, vm));

        editor.PromoteBackgroundToBackdropCommand.Execute(null);
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("image", await TransmitAndGetFileNameAsync(rig, vm));
    }

    [AvaloniaFact]
    public async Task OnAirName_UsesTheStockAndSendToTxFileNames()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.SelectStockImageCommand.ExecuteAsync(new StockImageEntry("sunset", "sunset.jpg", "/stock/sunset.jpg"));
        Dispatcher.UIThread.RunJobs();
        rig.LastEditor!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("sunset.jpg", await TransmitAndGetFileNameAsync(rig, vm));

        var rig2 = new Rig();
        var vm2 = rig2.Create();
        SelectDrm(vm2);
        Assert.True(await vm2.OpenEditorForExternalFileAsync("/home/op/rx/received 0042.png", null));
        Dispatcher.UIThread.RunJobs();
        rig2.LastEditor!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("received 0042.png", await TransmitAndGetFileNameAsync(rig2, vm2));
    }

    // Follow-up 4: a macro rebake replaces the applied image, so the estimate re-encodes it.
    [AvaloniaFact]
    public async Task MacroRebake_ReEncodesTheEstimate_OnceTheTransmissionEnds()
    {
        var rig = new Rig();
        rig.Radio.LastKnownState = new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow);
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var editor = rig.LastEditor!;
        editor.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)editor.OverlayElements[0]).Text = "{freq}";
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        DetachEditor(vm);
        RequestEstimate(vm);
        await vm.FlushDrmEstimateAsync();
        var applied = LoadedImage(vm);
        Assert.Same(applied, Assert.Single(rig.Session.EstimateDrmCalls).Image);

        rig.Radio.LastKnownState = rig.Radio.LastKnownState.Value with { FrequencyHz = 7_045_000 };
        await vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(applied, LoadedImage(vm));
        await vm.FlushDrmEstimateAsync();

        Assert.Equal(2, rig.Session.EstimateDrmCalls.Count);
        Assert.Same(LoadedImage(vm), rig.Session.EstimateDrmCalls[1].Image);
    }

    // Item 1, round 2: a blank, untouched editor is what Apply & Transmit sends, so that is what gets estimated.
    [AvaloniaFact]
    public async Task Estimate_WithABlankUntouchedEditor_DescribesTheBlankCanvas_NotTheAppliedImage()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        await BrowseAndApplyAsync(rig, vm);
        NewTemplate(vm);
        await WaitForAsync(() => !ReferenceEquals(CurrentEditor(vm), null) && CurrentEditor(vm)!.HasRealBackground == false);
        var applied = LoadedImage(vm);

        await vm.FlushDrmEstimateAsync();

        var call = rig.Session.EstimateDrmCalls[^1];
        Assert.NotSame(applied, call.Image);
        Assert.Equal((640, 480), (call.Image.Width, call.Image.Height));
        Assert.Equal("image", call.FileName);
    }

    // The only path that estimates the applied image directly: no editor attached.
    private static void DetachEditor(TxControlsPaneViewModel vm) =>
        typeof(TxControlsPaneViewModel).GetField("_currentEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(vm, null);

    private static void RequestEstimate(TxControlsPaneViewModel vm) =>
        typeof(TxControlsPaneViewModel).GetMethod("RequestDrmEstimate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [true]);

    // Follow-up 9 and 11: the estimate runs on the editor's picture before Apply and feeds the editor's readouts.
    [AvaloniaFact]
    public async Task Estimate_RunsOnTheEditorsPictureBeforeApply_AndShowsItsDurationInTheEditor()
    {
        var rig = new Rig();
        rig.Session.EstimateToReturn = new DrmEstimate(new byte[20_000], TimeSpan.FromSeconds(80.4));
        var vm = rig.Create();
        SelectDrm(vm);
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(LoadedImage(vm));
        Assert.True(vm.IsDrmEstimateScheduled);

        await vm.FlushDrmEstimateAsync();

        var call = Assert.Single(rig.Session.EstimateDrmCalls);
        Assert.Equal((640, 480), (call.Image.Width, call.Image.Height));
        Assert.Equal("holiday photo.png", call.FileName);
        _ = rig.LastEditor!.SendMetaText;
        _ = rig.LastEditor!.FrameReadoutText;
        Assert.Contains(rig.Localization.Calls, c => c.Key == "Panes.TxImageEditor.SendMetaFormat" && Equals(c.Args[1], 80.4));
        Assert.Contains(rig.Localization.Calls, c => c.Key == "Panes.TxImageEditor.FrameReadoutFormat" && Equals(c.Args[3], 80.4));

        rig.LastEditor!.Brightness += 10;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDrmEstimateScheduled);
    }

    // Follow-up 5: closing the pane saves a file-size change still waiting on the debounce.
    [AvaloniaFact]
    public void Dispose_SavesAPendingFileSizeChange()
    {
        var rig = new Rig();
        var vm = rig.Create();
        vm.DrmTargetKb = 42;
        Dispatcher.UIThread.RunJobs();

        vm.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(42, rig.Settings.Settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings)?.TargetKb);
    }

    // Follow-up 2 and 6: a transient -1 from a ComboBox is rejected, and a language switch re-reads the lists.
    [AvaloniaFact]
    public void ProfileEditor_RejectsAnOutOfRangeIndex_AndRefreshesItsListsOnALanguageSwitch()
    {
        var localization = new FakeLocalizationService();
        var settings = new FakeSettingsStore();
        var options = new OptionsWindowViewModel(TestOptionsSettings.Create(settings), localization, new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settings, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var profile = options.DrmProfiles[2];
        profile.QamIndex = 1;
        var before = profile.ToSettings();
        var raised = new List<string?>();
        profile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        profile.QamIndex = -1;
        profile.ModeIndex = 3;

        Assert.Equal(1, profile.QamIndex);
        Assert.Equal(before, profile.ToSettings());
        Assert.Equal([nameof(DrmProfileEditorViewModel.QamIndex), nameof(DrmProfileEditorViewModel.ModeIndex)], raised);

        raised.Clear();
        _ = localization.SetCultureAsync(System.Globalization.CultureInfo.GetCultureInfo("de"));
        Assert.Contains(string.Empty, raised);
        Assert.Equal("Options.Drm.ProfileHeadingFormat", HeadingKey(localization, profile));
    }

    private static string HeadingKey(FakeLocalizationService localization, DrmProfileEditorViewModel profile)
    {
        _ = profile.Heading;
        Assert.Equal(3, localization.Calls[^1].Args[0]);
        return localization.Calls[^1].Key;
    }

    // Addendum A: the header shows the selected target's Pwr family, pushed on assignment and on every target change.
    [AvaloniaFact]
    public void TxLevelFamily_IsPushedOnRadioStatusAssignment_AndOnTargetChange_WithoutAWrite()
    {
        var rig = new Rig();
        var vm = rig.Create();
        SelectDrm(vm);
        var radioStatus = new RadioStatusViewModel(rig.Radio, rig.Session, rig.Localization, new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);

        vm.RadioStatus = radioStatus;
        Assert.Equal(TxLevelFamily.Drm, radioStatus.ActiveTxLevelFamily);

        vm.SelectedTarget = vm.AvailableTargets.OfType<SstvTarget>().First();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TxLevelFamily.Sstv, radioStatus.ActiveTxLevelFamily);
        SelectDrm(vm);
        Assert.Equal(TxLevelFamily.Drm, radioStatus.ActiveTxLevelFamily);
        Assert.Equal(0, rig.Session.SetTxVolumePercentCallCount);
    }

    // Tune plays at the selected family's level, so the target cannot change while it runs.
    [AvaloniaFact]
    public async Task Target_IsLockedWhileTuning()
    {
        var rig = new Rig();
        rig.Session.TuneGate = new TaskCompletionSource();
        var vm = rig.Create();
        var radioStatus = new RadioStatusViewModel(rig.Radio, rig.Session, rig.Localization, new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);
        vm.RadioStatus = radioStatus;
        Assert.True(vm.CanChangeMode);
        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(TxControlsPaneViewModel.CanChangeMode) ? 1 : 0;

        var tune = radioStatus.TuneCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CanChangeMode);
        Assert.False(vm.QuickSelectModeCommand.CanExecute(Small.Id));
        Assert.Equal(1, raised);

        rig.Session.TuneGate.SetResult();
        await tune;
        await WaitForAsync(() => raised == 2);
        Assert.True(vm.CanChangeMode);
    }

    // The editor's frame is composed off the UI thread before the encode is requested.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition());
    }
}
