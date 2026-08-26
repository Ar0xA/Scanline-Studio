using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One row of <see cref="MacrosReferenceWindowViewModel.Tokens"/>.</summary>
public sealed record MacroTokenReference(string Token, string Description);

/// <summary>Stub survey Tier 3's "Configurations &gt; Macros" dialog -- user decision (2026-08-26,
/// AskUserQuestion) narrowed this to a read-only token reference + live preview, NOT an editor:
/// <see cref="IMacroTextResolver"/> has no user-extensible macro list, only a fixed known-token set
/// (<see cref="MacroTextResolver"/>'s own doc comment) -- there is nothing to "add" a macro to.
///
/// <see cref="ISettingsStore"/> injected directly (not via <c>ScanlineStudio.Application</c>) is a
/// pre-existing, already-established pattern for THIS specific settings section --
/// <c>TxControlsPaneViewModel.OpenEditorAsync</c> already does the exact same
/// <c>LoadAsync().GetSection(OperatorSettings.SectionKey, ...)</c> read to build the
/// <see cref="OperatorSettings"/> its own <see cref="IMacroTextResolver"/> call needs.
/// <see cref="OperatorSettings"/> lives in <c>ScanlineStudio.Application</c>, not <c>Core.*</c> --
/// unlike the Storage settings dialog's own <c>ReceiveHistorySettings</c> case, this is NOT the
/// layering violation that plan-review caught there.</summary>
public sealed partial class MacrosReferenceWindowViewModel : ViewModelBase
{
    private readonly IMacroTextResolver _macroTextResolver;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<MacrosReferenceWindowViewModel> _logger;
    private OperatorSettings _operatorSettings = new();

    public MacrosReferenceWindowViewModel(IMacroTextResolver macroTextResolver, ISettingsStore settingsStore, ILocalizationService localization, ILogger<MacrosReferenceWindowViewModel> logger)
    {
        _macroTextResolver = macroTextResolver;
        _settingsStore = settingsStore;
        _logger = logger;

        // Built once from the resolver's own known-token set (MacroTextResolver.cs) -- {freq}/
        // {mode}/{dist}/{bearing} genuinely resolve to empty in THIS preview (no live RadioState,
        // no {his_grid} template variable is ever available here), documented as such rather than
        // silently showing a misleading blank with no explanation.
        Tokens =
        [
            new MacroTokenReference("%m", localization.GetString("Macros.Token.OperatorCallsign")),
            new MacroTokenReference("%D", localization.GetString("Macros.Token.Date")),
            new MacroTokenReference("%T", localization.GetString("Macros.Token.Time")),
            new MacroTokenReference("{name}", localization.GetString("Macros.Token.Name")),
            new MacroTokenReference("{grid}", localization.GetString("Macros.Token.Grid")),
            new MacroTokenReference("{freq}", localization.GetString("Macros.Token.Frequency")),
            new MacroTokenReference("{mode}", localization.GetString("Macros.Token.Mode")),
            new MacroTokenReference("{dist}", localization.GetString("Macros.Token.Distance")),
            new MacroTokenReference("{bearing}", localization.GetString("Macros.Token.Bearing")),
        ];

        _ = LoadOperatorSettingsSafeAsync();
    }

    public IReadOnlyList<MacroTokenReference> Tokens { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _sampleText = "De %m {name} {grid}, %D %T";

    /// <summary>Resolved live as <see cref="SampleText"/> changes -- radioState/variables are both
    /// <see langword="null"/> (see this class's own doc comment for why), so {freq}/{mode}/{dist}/
    /// {bearing} always resolve to empty here regardless of what the operator types.</summary>
    public string PreviewText => _macroTextResolver.Resolve(SampleText, _operatorSettings);

    private async Task LoadOperatorSettingsSafeAsync()
    {
        try
        {
            _operatorSettings = (await _settingsStore.LoadAsync())
                .GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
                ?? new OperatorSettings();
            OnPropertyChanged(nameof(PreviewText));
        }
        catch (Exception ex)
        {
            Log.LoadOperatorSettingsFailed(_logger, ex);
        }
    }

    /// <summary>Same view-model-never-touches-a-Window pattern as <see cref="OptionsWindowViewModel.RequestClose"/>.</summary>
    public event Action? RequestClose;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading operator settings for the Macros preview failed")]
        public static partial void LoadOperatorSettingsFailed(ILogger logger, Exception ex);
    }
}
