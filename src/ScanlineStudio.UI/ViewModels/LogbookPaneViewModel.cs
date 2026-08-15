using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Search/browse + add/edit/import/export QSO logbook pane -- see spec/08-logging.md and
/// the accompanying plan file. <see cref="RxHistoryPaneViewModel"/> is the closest architectural
/// analog (constructor-injected service dep(s), <see cref="ObservableCollection{T}"/> of entries,
/// best-effort try/catch around every async load). All timestamps shown/edited here are raw UTC --
/// deliberately no local-time conversion anywhere in this pane (see the plan's round-2 fix).</summary>
public sealed partial class LogbookPaneViewModel : ViewModelBase
{
    private readonly ILogbookSessionService _logbook;
    private readonly IFilePickerService _filePicker;
    private readonly ILocalizationService _localization;
    private readonly ILogger<LogbookPaneViewModel> _logger;

    private string? _editingId;

    [ObservableProperty]
    private string? _callsignFilter;

    [ObservableProperty]
    private DateTimeOffset? _fromDate;

    [ObservableProperty]
    private DateTimeOffset? _toDate;

    [ObservableProperty]
    private QsoRecord? _selectedEntry;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Card-title count readout, same "explicit update after Entries changes" pattern as
    /// <see cref="RxHistoryPaneViewModel"/>'s own EntryCountText -- Entries is a plain
    /// ObservableCollection, not an [ObservableProperty], so nothing derives this automatically.</summary>
    [ObservableProperty]
    private string _entryCountDisplay = string.Empty;

    /// <summary>Status bar's "log size" readout -- a SEPARATE, unfiltered query
    /// (<c>new LogbookQuery()</c>, every field null), independent of whatever <see cref="Entries"/>'
    /// own current search filter currently shows (which defaults to the last 30 days, per this
    /// pane's own construction-time default below) -- "log size" means the whole logbook, not
    /// today's/this-month's search results. Loaded once at construction, same "best-effort, not
    /// re-fetched live" convention as this pane's sibling telemetry properties elsewhere in this
    /// session's work; a newly-logged QSO doesn't bump this count until the pane is reconstructed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogSizeDisplay))]
    private int _totalLoggedCount;

    // Add/Edit form fields -- every QsoRecord field except Id (generated) and ReceivedImageId (no
    // UI source for it yet -- see the plan's Gallery-linking exclusion).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private string? _formCallsign;

    [ObservableProperty]
    private DateTimeOffset _formStartUtc = DateTimeOffset.UtcNow;

    [ObservableProperty]
    private DateTimeOffset? _formEndUtc;

    [ObservableProperty]
    private long? _formFrequencyHz;

    [ObservableProperty]
    private RadioMode? _formMode;

    [ObservableProperty]
    private string? _formSstvModeId;

    [ObservableProperty]
    private string? _formRstSent;

    [ObservableProperty]
    private string? _formRstReceived;

    [ObservableProperty]
    private string? _formName;

    [ObservableProperty]
    private string? _formQth;

    [ObservableProperty]
    private string? _formGridSquare;

    [ObservableProperty]
    private string? _formCountry;

    [ObservableProperty]
    private string? _formNotes;

    public LogbookPaneViewModel(
        ILogbookSessionService logbook,
        IFilePickerService filePicker,
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        ILogger<LogbookPaneViewModel> logger)
    {
        _logbook = logbook;
        _filePicker = filePicker;
        _localization = localization;
        _logger = logger;

        // Leading RadioModeOption.None gives the ComboBox an explicit "(none)" option -- a real,
        // non-null item (never a bare `null` list entry: Avalonia's ContentPresenter skips
        // ContentTemplate entirely for null Content, which would render that row blank). Mapped
        // to/from FormMode (RadioMode?) via SelectedValueBinding/SelectedValue in the view, not
        // SelectedItem -- the ComboBox's ItemsSource element type (RadioModeOption) and FormMode's
        // type (RadioMode?) are different, so SelectedItem would silently fail to match and null
        // out FormMode on every selection. RadioMode.Unknown is omitted entirely -- AdifExporter
        // maps Unknown to the same null-on-export behavior as an actual null Mode
        // (AdifRadioModeMapping.ToAdif), so offering both would be identical-behavior UI noise, not
        // a real distinction.
        AvailableModes = new[] { RadioModeOption.None }
            .Concat(Enum.GetValues<RadioMode>().Where(m => m != RadioMode.Unknown).Select(m => new RadioModeOption(m)))
            .ToArray();
        AvailableSstvModes = sstvSession.AvailableModes;

        // Cheap insurance against the facade's own unbounded-query paging gap (spec/14-roadmap.md) --
        // not a fix for it, just a sane default so the very first load isn't the whole logbook.
        FromDate = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-30), TimeSpan.Zero);

        _ = RefreshAsync();
        _ = LoadTotalLoggedCountAsync();
    }

    public ObservableCollection<QsoRecord> Entries { get; } = [];

    public IReadOnlyList<RadioModeOption> AvailableModes { get; }

    public IReadOnlyList<SstvModeDefinition> AvailableSstvModes { get; }

    public string LogSizeDisplay => _localization.GetString("MainWindow.StatusBar.LogSizeValueFormat", TotalLoggedCount);

    private async Task LoadTotalLoggedCountAsync()
    {
        try
        {
            var all = await _logbook.SearchAsync(new LogbookQuery(null, null, null));
            TotalLoggedCount = all.Count;
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as RxHistoryPaneViewModel.LoadFramesTodayCountAsync --
            // the status bar just shows 0.
            Log.LoadTotalLoggedCountFailed(_logger, ex);
        }
    }

    /// <summary>Both dates are built as UTC calendar dates, never taken as-is from the
    /// <c>DatePicker</c>-bound property (which emits a LOCAL-offset <see cref="DateTimeOffset"/>) and
    /// never <c>.ToUniversalTime()</c> (see the plan's round-2 fix: that would shift the day
    /// depending on the local offset). "To" is end-of-day inclusive -- a bare "To" date must cover
    /// that whole day, not just its midnight.</summary>
    private LogbookQuery BuildCurrentQuery()
    {
        var callsign = string.IsNullOrWhiteSpace(CallsignFilter) ? null : CallsignFilter.Trim();
        DateTimeOffset? from = FromDate is { } fromDate
            ? new DateTimeOffset(fromDate.Date, TimeSpan.Zero)
            : null;
        DateTimeOffset? to = ToDate is { } toDate
            ? new DateTimeOffset(toDate.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero)
            : null;
        return new LogbookQuery(callsign, from, to);
    }

    /// <summary>Bound to the Refresh button -- <c>[RelayCommand]</c> methods must return
    /// <see cref="Task"/>, not <c>Task&lt;bool&gt;</c>, so the actual work (and its success/failure
    /// signal) lives in <see cref="RefreshInternalAsync"/> below.</summary>
    [RelayCommand]
    private async Task RefreshAsync() => await RefreshInternalAsync();

    /// <summary>Returns <see langword="false"/> on failure so callers that chain more UI feedback
    /// after a refresh (e.g. <see cref="ImportAdifAsync"/>'s "Imported N" status line) don't clobber
    /// this method's own <see cref="Log.SearchFailed"/>-triggered status message with a false
    /// "success."</summary>
    private async Task<bool> RefreshInternalAsync()
    {
        var query = BuildCurrentQuery();
        var callsign = query.Callsign;

        Log.RefreshInvoked(_logger, callsign);

        IReadOnlyList<QsoRecord> results;
        try
        {
            results = await _logbook.SearchAsync(query);
        }
        catch (Exception ex)
        {
            Log.SearchFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.Logbook.Error.SearchFailed");
            return false;
        }

        Entries.Clear();
        foreach (var entry in results)
        {
            Entries.Add(entry);
        }

        EntryCountDisplay = _localization.GetString("Panes.Logbook.EntryCountFormat", Entries.Count);

        Log.RefreshCompleted(_logger, Entries.Count);
        return true;
    }

    partial void OnSelectedEntryChanged(QsoRecord? value)
    {
        if (value is null)
        {
            return;
        }

        LoadIntoForm(value);
    }

    /// <summary>Bound to the "New" button -- resets the form AND clears any leftover
    /// <see cref="StatusMessage"/> (the user explicitly asked to start over). Internal callers that
    /// need the form reset without losing a just-set status line (<see cref="LogAsync"/>,
    /// <see cref="UpdateAsync"/>) call <see cref="ResetForm"/> directly instead.</summary>
    [RelayCommand]
    private void New()
    {
        Log.NewInvoked(_logger);
        ResetForm();
        StatusMessage = null;
    }

    /// <summary>Also clears <see cref="SelectedEntry"/> -- without this, selecting row A, clicking
    /// New, then clicking row A again would never re-fire <see cref="OnSelectedEntryChanged"/>
    /// (same value assigned twice, no <c>PropertyChanged</c>), leaving the form stuck empty with
    /// Update disabled until a manual Refresh.</summary>
    private void ResetForm()
    {
        _editingId = null;
        IsEditing = false;
        SelectedEntry = null;
        FormCallsign = null;
        FormStartUtc = DateTimeOffset.UtcNow;
        FormEndUtc = null;
        FormFrequencyHz = null;
        FormMode = null;
        FormSstvModeId = null;
        FormRstSent = null;
        FormRstReceived = null;
        FormName = null;
        FormQth = null;
        FormGridSquare = null;
        FormCountry = null;
        FormNotes = null;
        UpdateCommand.NotifyCanExecuteChanged();
    }

    private void LoadIntoForm(QsoRecord record)
    {
        _editingId = record.Id;
        IsEditing = true;
        FormCallsign = record.Callsign;
        FormStartUtc = record.StartUtc;
        FormEndUtc = record.EndUtc;
        FormFrequencyHz = record.FrequencyHz;
        FormMode = record.Mode;
        FormSstvModeId = record.SstvModeId;
        FormRstSent = record.RstSent;
        FormRstReceived = record.RstReceived;
        FormName = record.Name;
        FormQth = record.Qth;
        FormGridSquare = record.GridSquare;
        FormCountry = record.Country;
        FormNotes = record.Notes;
        StatusMessage = null;
        UpdateCommand.NotifyCanExecuteChanged();
    }

    private bool CanLog() => !string.IsNullOrWhiteSpace(FormCallsign);

    [RelayCommand(CanExecute = nameof(CanLog))]
    private async Task LogAsync()
    {
        var record = BuildRecordFromForm(Guid.NewGuid().ToString());

        LogQsoResult result;
        try
        {
            result = await _logbook.LogQsoAsync(record);
        }
        catch (Exception ex)
        {
            Log.LogFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.Logbook.Error.LogFailed");
            return;
        }

        // ResetForm() (not New()) -- New() would null the status line this just set.
        var statusMessage = BuildLogStatusMessage(result);
        ResetForm();
        StatusMessage = statusMessage;
        await RefreshInternalAsync();
    }

    private bool CanUpdate() => _editingId is not null && !string.IsNullOrWhiteSpace(FormCallsign);

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (_editingId is null)
        {
            return;
        }

        var record = BuildRecordFromForm(_editingId);

        try
        {
            await _logbook.UpdateQsoAsync(record);
        }
        catch (Exception ex)
        {
            Log.UpdateFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.Logbook.Error.UpdateFailed");
            return;
        }

        // Deliberately no GridTracker/QRZ status line here -- UpdateQsoAsync never re-pushes (see
        // its own doc comment), so there is nothing to report beyond "saved." ResetForm() (not
        // New()) -- New() would null the status line just set below.
        ResetForm();
        StatusMessage = _localization.GetString("Panes.Logbook.Status.Updated");
        await RefreshInternalAsync();
    }

    private QsoRecord BuildRecordFromForm(string id) => new(
        id,
        FormCallsign!.Trim(),
        FormStartUtc,
        FormEndUtc,
        FormFrequencyHz,
        FormMode,
        FormSstvModeId,
        FormRstSent,
        FormRstReceived,
        FormName,
        FormQth,
        FormGridSquare,
        FormCountry,
        FormNotes,
        null);

    private string BuildLogStatusMessage(LogQsoResult result)
    {
        var gridTracker = result.GridTrackerSent
            ? _localization.GetString("Panes.Logbook.Status.GridTrackerSent")
            : _localization.GetString("Panes.Logbook.Status.GridTrackerNotSent");

        var qrz = result.QrzUploaded
            ? _localization.GetString("Panes.Logbook.Status.QrzUploaded")
            : result.QrzError is not null
                ? _localization.GetString("Panes.Logbook.Status.QrzFailed", result.QrzError)
                : _localization.GetString("Panes.Logbook.Status.QrzNotSent");

        return _localization.GetString("Panes.Logbook.Status.LoggedFormat", gridTracker, qrz);
    }

    [RelayCommand]
    private async Task ImportAdifAsync()
    {
        var path = await _filePicker.PickAdifFileAsync();
        if (path is null)
        {
            return;
        }

        IReadOnlyList<QsoRecord> imported;
        try
        {
            imported = await _logbook.ImportAdifFileAsync(path);
        }
        catch (Exception ex)
        {
            Log.ImportFailed(_logger, ex);
            // A malformed file can leave SOME rows already committed before the exception surfaces
            // (LogbookSessionService.ImportAdifFileAsync persists record-by-record, non-
            // transactionally) -- refresh so the user sees what DID get imported, and say so.
            await RefreshInternalAsync();
            StatusMessage = _localization.GetString("Panes.Logbook.Error.ImportPartial");
            return;
        }

        // Only report "Imported N" if the follow-up refresh actually succeeded -- otherwise this
        // would silently overwrite RefreshInternalAsync's own Error.SearchFailed status line with a
        // false "success," masking a real failure right after a real import.
        if (await RefreshInternalAsync())
        {
            StatusMessage = _localization.GetString("Panes.Logbook.Status.Imported", imported.Count);
        }
    }

    [RelayCommand]
    private async Task ExportAdifAsync()
    {
        var suggestedFileName = $"scanline-studio-logbook-{DateTime.UtcNow:yyyyMMdd-HHmmss}.adi";
        var path = await _filePicker.PickSaveAdifFileAsync(suggestedFileName);
        if (path is null)
        {
            return;
        }

        // Re-refresh first so the reported count below always matches what SearchAsync (called
        // again, with the same query, inside ExportAdifFileAsync itself) actually exports -- Entries
        // can otherwise be stale relative to the live filter (e.g. CallsignFilter edited but Refresh
        // never clicked before hitting Export).
        await RefreshInternalAsync();
        var query = BuildCurrentQuery();

        try
        {
            await _logbook.ExportAdifFileAsync(path, query);
        }
        catch (Exception ex)
        {
            Log.ExportFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.Logbook.Error.ExportFailed");
            return;
        }

        StatusMessage = _localization.GetString("Panes.Logbook.Status.Exported", Entries.Count);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh invoked: callsign={Callsign}")]
        public static partial void RefreshInvoked(ILogger logger, string? callsign);

        [LoggerMessage(Level = LogLevel.Debug, Message = "New invoked")]
        public static partial void NewInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SearchAsync failed; logbook list stays as-is")]
        public static partial void SearchFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the total-logged count failed")]
        public static partial void LoadTotalLoggedCountFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh completed: {Count} entries")]
        public static partial void RefreshCompleted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LogQsoAsync failed")]
        public static partial void LogFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "UpdateQsoAsync failed")]
        public static partial void UpdateFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ImportAdifFileAsync failed")]
        public static partial void ImportFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ExportAdifFileAsync failed")]
        public static partial void ExportFailed(ILogger logger, Exception ex);
    }
}

/// <summary>Wraps a <see cref="RadioMode"/>? for the Mode <c>ComboBox</c>'s items -- a real,
/// never-null object even for the "(none)" entry (<see cref="None"/>), so Avalonia's
/// <c>ContentPresenter</c> (which skips <c>ContentTemplate</c> entirely for a literal <c>null</c>
/// <c>Content</c>) doesn't render that row blank. The view binds via
/// <c>SelectedValueBinding="{Binding Value}"</c>/<c>SelectedValue="{Binding FormMode}"</c>, not
/// <c>SelectedItem</c> -- <see cref="LogbookPaneViewModel.FormMode"/> is <see cref="RadioMode"/>?,
/// a different type than this wrapper, so <c>SelectedItem</c> binding would never match an item and
/// would silently null out <c>FormMode</c> on every selection.</summary>
public sealed record RadioModeOption(RadioMode? Value)
{
    public static readonly RadioModeOption None = new((RadioMode?)null);
}
