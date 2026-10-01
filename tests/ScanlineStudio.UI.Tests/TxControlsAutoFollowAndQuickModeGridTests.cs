using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class TxControlsAutoFollowAndQuickModeGridTests
{
    private static readonly SstvModeDefinition ModeA = new(
        Id: "robot36", DisplayName: "Robot 36", VisCode: 8, ImageWidth: 1, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly SstvModeDefinition ModeB = new(
        Id: "martin-m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 1, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    // Deliberately a DIFFERENT ImageWidth than ModeA/ModeB (both 1) -- the width-mismatch guard
    // tests below need a mode whose own picture width genuinely differs from whatever is loaded.
    private static readonly SstvModeDefinition ModeWiderWidth = new(
        Id: "wider", DisplayName: "Wider", VisCode: 99, ImageWidth: 2, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static TxControlsPaneViewModel CreateViewModel(FakeSettingsStore settingsStore, FakeSstvSessionService? sstvSession = null)
        => new(
            sstvSession ?? new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] },
            new FakeImageFileLoader(),
            new FakeStockImageLibrary(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService(),
            new FakeLocalizationService(),
            settingsStore,
            new FakeRadioSessionService(),
            new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance,
            NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

    [AvaloniaFact]
    public void Constructor_LoadsAutoFollowFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };

        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.AutoFollowRxMode);
    }

    [AvaloniaFact]
    public void ModeDetected_WithAutoFollowOn_UpdatesSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        sstvSession.RaiseModeDetected(ModeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("martin-m1", vm.SelectedMode?.Id);
    }

    [AvaloniaFact]
    public void ModeDetected_WithAutoFollowOff_DoesNotUpdateSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var vm = CreateViewModel(new FakeSettingsStore(), sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;
        Assert.False(vm.AutoFollowRxMode);

        sstvSession.RaiseModeDetected(ModeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
    }

    // Legacy TrackTxMode (Main.cpp:4907-4915) guard 1/2: don't switch TX mode while actively
    // transmitting -- see OnModeDetected's own doc comment for the full reasoning.
    [AvaloniaFact]
    public void ModeDetected_WhileTransmitting_DoesNotUpdateSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;
        vm.IsTransmitting = true;

        sstvSession.RaiseModeDetected(ModeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
    }

    // Legacy TrackTxMode guard 2/2: only follow when the detected mode's own picture width matches
    // the currently loaded TX image's width -- OnSelectedModeChanged unconditionally reflows the
    // loaded image to whatever mode is selected, so without this guard auto-follow would silently
    // re-crop an already-framed TX image. See OnModeDetected's own doc comment for the full
    // reasoning and the legacy citation.
    [AvaloniaFact]
    public async Task ModeDetected_WithMismatchedLoadedImageWidth_DoesNotUpdateSelectedModeOrReflowTheLoadedImage()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeWiderWidth] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(opened);
        opened!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, ExtractLoadedImage(vm)?.Width);

        sstvSession.RaiseModeDetected(ModeWiderWidth); // width 2, mismatched against the loaded width-1 image
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
        Assert.Equal(1, ExtractLoadedImage(vm)?.Width); // unchanged, not silently reflowed to width 2
    }

    // Auditor-flagged gap: with NOTHING loaded yet, the width guard must not block auto-follow at
    // all -- there is nothing to protect from a silent reflow. This is a deliberate, documented
    // deviation from legacy (legacy's pBitmapTX always exists and is always sized to the current TX
    // mode, so legacy's own width check is always live; this port's _loadedImage genuinely can be
    // null, e.g. a fresh session with nothing picked for TX yet).
    [AvaloniaFact]
    public void ModeDetected_WithNothingLoaded_StillUpdatesSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeWiderWidth] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;
        Assert.Null(ExtractLoadedImage(vm));

        sstvSession.RaiseModeDetected(ModeWiderWidth); // width 2, but nothing loaded to mismatch against
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("wider", vm.SelectedMode?.Id);
    }

    // Positive control for the width guard above: a MATCHING width must still auto-follow normally
    // -- proves the guard discriminates on width, not just "any image loaded blocks auto-follow."
    [AvaloniaFact]
    public async Task ModeDetected_WithMatchingLoadedImageWidth_UpdatesSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(opened);
        opened!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        sstvSession.RaiseModeDetected(ModeB); // width 1, same as ModeA and the loaded image
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("martin-m1", vm.SelectedMode?.Id);
    }

    /// <summary>Code-review finding (yoniq-auditor, 2026-09-19): the first draft of the
    /// IsEditorOpen-removal fix compared the RX detect's width against `_loadedImage`, which stays
    /// null until the FIRST Apply -- so a real, unapplied photo (Browse'd but not yet Applied) sitting
    /// in an open editor got no width check at all, letting a mismatched-width RX detect silently
    /// rebuild that editor via ReplaceEditorForModeSwitch, discarding its undo stack with no operator
    /// action. Must refuse here, same as the applied case above, but via SelectedMode's own width
    /// (the real analogue of legacy's always-live pBitmapTX-&gt;Width) since _loadedImage is null.</summary>
    [AvaloniaFact]
    public async Task ModeDetected_WithRealUnappliedPhotoAtMismatchedWidth_DoesNotChangeSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeWiderWidth] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;
        await vm.SelectImageCommand.ExecuteAsync(null); // real photo loaded, NOT Applied yet
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(opened);
        Assert.Null(ExtractLoadedImage(vm)); // confirms _loadedImage is genuinely null here

        var editorBeforeDetect = ExtractCurrentEditor(vm);
        sstvSession.RaiseModeDetected(ModeWiderWidth); // width 2, mismatched against ModeA's width 1
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
        Assert.Same(editorBeforeDetect, ExtractCurrentEditor(vm)); // editor untouched, not rebuilt
    }

    private static IImageSource? ExtractLoadedImage(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_loadedImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as IImageSource;

    private static TxImageEditorPaneViewModel? ExtractCurrentEditor(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_currentEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as TxImageEditorPaneViewModel;

    [AvaloniaFact]
    public void TogglingAutoFollowRxMode_LeavesAnUnrelatedSiblingSectionUntouched()
    {
        // TxPaneUiSettings owns AutoFollowRxMode/QuickModeGridIds today. This test's real target
        // is the `settings.WithSection(...)` mechanism itself: saving one section must never disturb
        // a completely different section already present in the same AppSettings -- AudioDeviceSettings
        // (an arbitrary unrelated section) with a distinctive SampleRate sentinel stands in for that.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ScanlineStudio.Core.Audio.AudioDeviceSettings.SectionKey,
                new ScanlineStudio.Core.Audio.AudioDeviceSettings { SampleRate = 77000 },
                ScanlineStudio.Core.Audio.AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();

        vm.AutoFollowRxMode = true;
        Dispatcher.UIThread.RunJobs();

        var audio = settingsStore.Settings.GetSection(ScanlineStudio.Core.Audio.AudioDeviceSettings.SectionKey, ScanlineStudio.Core.Audio.AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(77000, audio?.SampleRate);

        var txPaneUi = settingsStore.Settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
        Assert.True(txPaneUi?.AutoFollowRxMode);
    }

    [AvaloniaFact]
    public void QuickModeSlots_DefaultsTo16SlotsFromQuickModeGridDefaults()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = CreateViewModel(settingsStore, new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(16, vm.QuickModeSlots.Count);
        for (var i = 0; i < QuickModeGridDefaults.Ids.Count; i++)
        {
            Assert.Equal(QuickModeGridDefaults.Ids[i], vm.QuickModeSlots[i].CurrentMode.Id);
            Assert.Equal(43, vm.QuickModeSlots[i].MenuEntries.Count);
        }
    }

    [AvaloniaFact]
    public void QuickModeSlots_LoadsAValidPersistedAssignment()
    {
        var customIds = QuickModeGridDefaults.Ids.Reverse().ToArray();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { QuickModeGridIds = customIds },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };

        var vm = CreateViewModel(settingsStore, new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All });
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < customIds.Length; i++)
        {
            Assert.Equal(customIds[i], vm.QuickModeSlots[i].CurrentMode.Id);
        }
    }

    [AvaloniaFact]
    public void QuickModeSlots_AutoFollowInitialization_PreservesCustomSlotsAcrossLaunches()
    {
        var customIds = QuickModeGridDefaults.Ids.Reverse().ToArray();
        var settings = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { QuickModeGridIds = customIds, AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        for (var launch = 0; launch < 3; launch++)
        {
            var vm = CreateViewModel(settings, new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All });
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.AutoFollowRxMode);
            Assert.Equal(customIds, vm.QuickModeSlots.Select(s => s.CurrentMode.Id));
            var persisted = settings.Settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
            Assert.Equal(customIds, persisted!.QuickModeGridIds);
        }
    }

    [AvaloniaFact]
    public async Task ReassignQuickModeSlot_UpdatesCurrentModeAndPersists_WithoutClobberingAutoFollowRxMode()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All });
        Dispatcher.UIThread.RunJobs();
        var newMode = SstvModeRegistry.Mn73; // not one of the default 16

        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, newMode));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(newMode.Id, vm.QuickModeSlots[0].CurrentMode.Id);

        var persisted = settingsStore.Settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
        Assert.Equal(newMode.Id, persisted!.QuickModeGridIds[0]);
        // The reassignment's own persist is a read-modify-write against the same section --
        // AutoFollowRxMode (already loaded by the time this awaited-first reassignment runs) must survive.
        Assert.True(persisted.AutoFollowRxMode);
    }

    [AvaloniaFact]
    public async Task ReassignQuickModeSlot_ToModeAlreadyUsedElsewhere_IsASafeNoOp()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = CreateViewModel(settingsStore, new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All });
        Dispatcher.UIThread.RunJobs();
        var slot1Mode = vm.QuickModeSlots[1].CurrentMode;

        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, slot1Mode));

        Assert.Equal(QuickModeGridDefaults.Ids[0], vm.QuickModeSlots[0].CurrentMode.Id);
    }

    [AvaloniaFact]
    public async Task ReassignQuickModeSlot_IsIndependentOfRxImagePaneViewModelsOwnGrid()
    {
        // Confirmed with the user: reassigning here must never touch RxImagePaneViewModel's own
        // grid -- there is no shared state between the two beyond the pure QuickModeGridAssignment
        // algorithm and view-model shapes, so this asserts the TX-side persisted section alone
        // carries the change, under its OWN section key.
        var settingsStore = new FakeSettingsStore();
        var vm = CreateViewModel(settingsStore, new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All });
        Dispatcher.UIThread.RunJobs();
        var newMode = SstvModeRegistry.Mn73;

        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, newMode));
        Dispatcher.UIThread.RunJobs();

        var txPersisted = settingsStore.Settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
        var rxPersisted = settingsStore.Settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        Assert.Equal(newMode.Id, txPersisted!.QuickModeGridIds[0]);
        Assert.Null(rxPersisted); // no RX section was ever written by a TX-side reassignment
    }

    [AvaloniaFact]
    public void PreviewImage_Reassigned_DisposesTheOldBitmap_DeferredViaDispatcherPost()
    {
        // T0-11 (production_audit.md): OnPreviewImageChanged fires on every writer of this
        // property -- exercised directly rather than driving the whole Apply/mode-change pipeline
        // that normally sets it.
        var vm = CreateViewModel(new FakeSettingsStore());
        Dispatcher.UIThread.RunJobs();
        var first = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        vm.PreviewImage = first;

        vm.PreviewImage = null;

        Assert.False(IsWriteableBitmapDisposed(first), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsWriteableBitmapDisposed(first));
    }

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique WriteableBitmapPoolTests
    // uses: a disposed instance throws ObjectDisposedException from
    // any real operation, here .Lock().
    private static bool IsWriteableBitmapDisposed(WriteableBitmap bitmap)
    {
        try
        {
            using (bitmap.Lock())
            {
            }

            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
