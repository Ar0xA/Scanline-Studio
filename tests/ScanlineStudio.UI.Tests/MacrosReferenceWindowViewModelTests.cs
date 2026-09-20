using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class MacrosReferenceWindowViewModelTests
{
    private static MacrosReferenceWindowViewModel CreateViewModel(FakeSettingsStore? settingsStore = null)
        => new(new MacroTextResolver(), settingsStore ?? new FakeSettingsStore(), new FakeLocalizationService(), NullLogger<MacrosReferenceWindowViewModel>.Instance);

    [AvaloniaFact]
    public void Constructor_PopulatesTheFullFixedTokenSet()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        // MacroTextResolver.cs's own known-token set: %m/%D/%T (percent-tokens) plus
        // name/grid/freq/mode/dist/bearing (brace-tokens) -- this dialog has no user-extensible
        // list, so this is meant to be the complete, fixed set.
        Assert.Equal(
            ["%m", "%D", "%T", "{name}", "{grid}", "{freq}", "{mode}", "{dist}", "{bearing}"],
            vm.Tokens.Select(t => t.Token));
        Assert.All(vm.Tokens, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [AvaloniaFact]
    public void PreviewText_ResolvesOperatorTokens_AgainstThePersistedOperatorSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                OperatorSettings.SectionKey,
                new OperatorSettings { Callsign = "KD9TAW", Name = "Ada", Grid = "EM12" },
                OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();

        vm.SampleText = "De %m ({name}, {grid})";

        Assert.Equal("De KD9TAW (Ada, EM12)", vm.PreviewText);
    }

    [AvaloniaFact]
    public void PreviewText_RadioAndVariableDependentTokens_ResolveEmpty_NotLeftLiteral()
    {
        // No live RadioState/template variables are ever available in this preview (this class's
        // own doc comment) -- {freq}/{mode}/{dist}/{bearing} must resolve to empty, matching
        // MacroTextResolver's own "input not available yet" convention, not stay as literal
        // "{freq}" text (which would look like a broken/unrecognized token instead of an
        // unavailable one).
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.SampleText = "[{freq}][{mode}][{dist}][{bearing}]";

        Assert.Equal("[][][][]", vm.PreviewText);
    }

    [AvaloniaFact]
    public void PreviewText_UpdatesLiveAsSampleTextChanges()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.SampleText = "%D";
        var first = vm.PreviewText;
        vm.SampleText = "%T";
        var second = vm.PreviewText;

        Assert.NotEqual(first, second);
    }

    [AvaloniaFact]
    public async Task PreviewText_OperatorSettingsLoadCompletesAfterConstruction_RaisesPropertyChangedAndReResolves()
    {
        // Auditor-caught (2026-08-26): the constructor's own OperatorSettings load is async, and
        // every other test's FakeSettingsStore.LoadAsync happens to complete synchronously (no real
        // await point), so the manual OnPropertyChanged(nameof(PreviewText)) call once that load
        // lands was completely untested -- a deterministic gate, not a race on task-scheduling
        // order (see the "Deterministic gates" convention this codebase already follows elsewhere).
        var gate = new TaskCompletionSource();
        var settingsStore = new FakeSettingsStore
        {
            Gate = gate.Task,
            Settings = new AppSettings().WithSection(
                OperatorSettings.SectionKey,
                new OperatorSettings { Callsign = "KD9TAW" },
                OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var vm = CreateViewModel(settingsStore);
        vm.SampleText = "%m";
        Dispatcher.UIThread.RunJobs();

        // Load still parked -- OperatorSettings hasn't landed yet, so %m resolves against the
        // still-default (empty-callsign) OperatorSettings this VM starts with, which is
        // OperatorSettings.CallsignFallback ("N0CALL"), not empty (user-reported 2026-09-20).
        Assert.Equal(OperatorSettings.CallsignFallback, vm.PreviewText);

        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.PreviewText))
            {
                raised.TrySetResult();
            }
        };

        gate.SetResult();
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("KD9TAW", vm.PreviewText);
    }

    [AvaloniaFact]
    public void CloseCommand_RaisesRequestClose()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }
}
