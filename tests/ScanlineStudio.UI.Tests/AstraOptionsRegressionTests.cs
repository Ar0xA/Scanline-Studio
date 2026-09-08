using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class AstraOptionsRegressionTests
{
    private static OptionsWindowViewModel CreateVm(FakeRadioSessionService? radio = null,
        FakeSstvSessionService? sstv = null, FakeSettingsStore? settings = null)
    {
        settings ??= new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settings, NullLogger<OptionsSettingsService>.Instance),
            new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settings,
            radio ?? new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(),
            sstv ?? new FakeSstvSessionService(), new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(),
            new FakeAppLocationsService(), new FakeApplicationRestarter(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    [AvaloniaFact]
    public async Task Tune_StopCommandRemainsExecutableAndCancelsPendingPlayback()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSstvSessionService { TuneGate = gate };
        var vm = CreateVm(sstv: session);
        var running = vm.TuneCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsTuning);
            Assert.Equal("Options.Radio.Tune.Stop", vm.TuneButtonLabel);
            Assert.True(vm.TuneCommand.CanExecute(null));
            await vm.TuneCommand.ExecuteAsync(null);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.Task.IsCanceled);
            Assert.Single(session.TuneCalls);
            Assert.False(vm.IsTuning);
        }
        finally
        {
            vm.StopTuneIfActive();
            gate.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AvaloniaTheory]
    [InlineData("rigctld", "edit")]
    [InlineData("flrig", "edit")]
    [InlineData("hamlib", "edit")]
    [InlineData("omnirig", "backend")]
    [InlineData("rigctld", "reset")]
    [InlineData("flrig", "reset")]
    [InlineData("hamlib", "reset")]
    [InlineData("omnirig", "reset")]
    [InlineData("rigctld", "backend")]
    [InlineData("flrig", "backend")]
    [InlineData("hamlib", "backend")]
    [InlineData("hamlib", "library")]
    public async Task CatResult_AfterEditResetOrBackendSwitch_DoesNotAuthorizeUntestedConfiguration(string backend, string change)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var radio = new FakeRadioSessionService { RigId = "none", TestConnectionGate = gate.Task };
        var vm = CreateVm(radio);
        ConfigureBackend(vm, backend);
        var command = CatCommand(vm, backend);
        var running = command.ExecuteAsync(null);
        Assert.True(vm.IsTestingConnection);
        ChangeConfiguration(vm, backend, change);
        gate.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        AssertAllUntested(vm);
        Assert.False(vm.IsTestingConnection);
        Assert.Null(vm.TestConnectionStatusMessage);
        Assert.False(vm.CanConnectRadio);

        // A new test of the fields now displayed is still able to publish its result.
        ConfigureBackend(vm, backend);
        radio.TestConnectionGate = null;
        await command.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(backend switch
        {
            "rigctld" => vm.RigctldTestSucceeded,
            "flrig" => vm.FlrigTestSucceeded,
            "hamlib" => vm.HamlibCatTestSucceeded,
            _ => vm.OmniRigTestSucceeded,
        });
    }

    [AvaloniaTheory]
    [InlineData("hamlib", "edit", true)]
    [InlineData("flrig", "edit", true)]
    [InlineData("hamlib", "reset", true)]
    [InlineData("flrig", "reset", true)]
    [InlineData("hamlib", "backend", true)]
    [InlineData("flrig", "backend", true)]
    [InlineData("hamlib", "edit", false)]
    [InlineData("flrig", "reset", false)]
    public async Task PttResult_AfterConfigurationInvalidation_DoesNotRestoreSuccessOrStatus(string backend, string change, bool success)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var radio = new FakeRadioSessionService
        {
            RigId = "none", TestPttGate = gate.Task,
            TestPttResultToReturn = new RadioConnectionTestResult(success, "test", RadioCapabilities.PttControl, success ? null : "failed"),
        };
        var vm = CreateVm(radio);
        ConfigureBackend(vm, backend);
        var command = backend == "hamlib" ? vm.TestPttCommand : vm.TestFlrigPttCommand;
        var running = command.ExecuteAsync(null);
        Assert.True(vm.IsTestingPtt);
        ChangeConfiguration(vm, backend, change);
        gate.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        AssertAllUntested(vm);
        Assert.False(vm.IsTestingPtt);
        Assert.Null(vm.TestPttErrorMessage);
        Assert.False(vm.CanConnectRadio);
    }

    [AvaloniaFact]
    public async Task CatResult_ResetWithAlreadyDefaultFields_DiscardsPendingResult()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var radio = new FakeRadioSessionService { RigId = "none", TestConnectionGate = gate.Task };
        var vm = CreateVm(radio);
        vm.ResetRadioToDefaultCommand.Execute(null);
        var running = vm.TestOmniRigConnectionCommand.ExecuteAsync(null);
        vm.ResetRadioToDefaultCommand.Execute(null);
        gate.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        AssertAllUntested(vm);
        Assert.Null(vm.TestConnectionStatusMessage);
        Assert.False(vm.IsTestingConnection);
    }

    [AvaloniaTheory]
    [InlineData("hamlib")]
    [InlineData("flrig")]
    public async Task PttResult_OlderQueuedResultCannotAuthorizeNewPendingTest(string backend)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var radio = new FakeRadioSessionService { RigId = "none" };
        var vm = CreateVm(radio);
        ConfigureBackend(vm, backend);
        var command = backend == "hamlib" ? vm.TestPttCommand : vm.TestFlrigPttCommand;
        Task? newer = null;
        // Start the second attempt at the first attempt's cleanup notification, before any
        // previously queued result callback can run after that notification.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsTestingPtt) && !vm.IsTestingPtt && newer is null)
            {
                radio.TestPttGate = gate.Task;
                newer = command.ExecuteAsync(null);
            }
        };

        await command.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(newer);
        try
        {
            Assert.True(vm.IsTestingPtt);
            Assert.False(vm.HamlibPttTestSucceeded);
            Assert.False(vm.FlrigPttTestSucceeded);
            Assert.True(command.CanExecute(null));
            await command.ExecuteAsync(null);
            await newer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(radio.WasCancelled);
        }
        finally
        {
            gate.TrySetResult();
            await newer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AvaloniaFact]
    public async Task ResetAll_ResetsAndPersistsAppearanceAndAdvancedSettings()
    {
        var settings = new FakeSettingsStore();
        var vm = CreateVm(settings: settings);
        vm.AppTheme = AppTheme.Dark;
        vm.FontScale = AppFontScale.Large;
        vm.PllVcoGain = OptionsSettingsService.Defaults.PllVcoGain + 1;
        vm.TxLpfEnabled = !OptionsSettingsService.Defaults.TxLpfEnabled;
        vm.TxLpfFrequencyHz = OptionsSettingsService.Defaults.TxLpfFrequencyHz + 100;
        vm.RequestResetAllCommand.Execute(null);
        vm.ConfirmResetAllCommand.Execute(null);

        Assert.Equal(AppTheme.Light, vm.AppTheme);
        Assert.Equal(AppFontScale.Normal, vm.FontScale);
        Assert.Equal(OptionsSettingsService.Defaults.PllVcoGain, vm.PllVcoGain);
        Assert.Equal(OptionsSettingsService.Defaults.TxLpfEnabled, vm.TxLpfEnabled);
        Assert.Equal(OptionsSettingsService.Defaults.TxLpfFrequencyHz, vm.TxLpfFrequencyHz);
        await vm.ApplyCommand.ExecuteAsync(null);

        var appearance = settings.Settings.GetSection(AppearanceSettings.SectionKey, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        Assert.Equal(AppTheme.Light, appearance!.Theme);
        Assert.Equal(AppFontScale.Normal, appearance.FontScale);
        var decoder = settings.Settings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.Equal(OptionsSettingsService.Defaults.PllVcoGain, decoder!.PllVcoGain);
        var audio = settings.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(OptionsSettingsService.Defaults.TxLpfEnabled, audio!.TxLpfEnabled);
        Assert.Equal(OptionsSettingsService.Defaults.TxLpfFrequencyHz, audio.TxLpfFrequencyHz);
    }

    private static void ConfigureBackend(OptionsWindowViewModel vm, string backend)
    {
        vm.RadioBackendId = backend == "hamlib" ? "hamlib-native" : backend == "omnirig" ? "omnirig-client" : backend;
        vm.RigctldHost = "host-a";
        vm.RigctldPort = 4532;
        vm.FlrigHost = "host-a";
        vm.FlrigPort = 12345;
        vm.HamlibModel = 1035;
        vm.HamlibSerialPort = "port-a";
        vm.HamlibPttType = "RIG";
    }

    private static IAsyncRelayCommand CatCommand(OptionsWindowViewModel vm, string backend) => backend switch
    {
        "rigctld" => vm.TestRigctldConnectionCommand,
        "flrig" => vm.TestFlrigConnectionCommand,
        "hamlib" => vm.TestHamlibConnectionCommand,
        _ => vm.TestOmniRigConnectionCommand,
    };

    private static void ChangeConfiguration(OptionsWindowViewModel vm, string backend, string change)
    {
        if (change == "reset") vm.ResetRadioToDefaultCommand.Execute(null);
        else if (change == "backend") vm.RadioBackendId = "none";
        else if (change == "library") vm.HamlibLibraryPath = "/different/hamlib";
        else if (backend == "rigctld") vm.RigctldHost = "host-b";
        else if (backend == "flrig") vm.FlrigPort = 12346;
        else vm.HamlibPttPort = "port-b";
    }

    private static void AssertAllUntested(OptionsWindowViewModel vm)
    {
        Assert.False(vm.RigctldTestSucceeded);
        Assert.False(vm.FlrigTestSucceeded);
        Assert.False(vm.OmniRigTestSucceeded);
        Assert.False(vm.HamlibCatTestSucceeded);
        Assert.False(vm.HamlibPttTestSucceeded);
        Assert.False(vm.FlrigPttTestSucceeded);
    }
}
