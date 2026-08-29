using System.Collections.ObjectModel;
using System.Globalization;
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

    /// <summary>Round-2 audit finding: <see cref="LoadIntoForm"/> used to not carry this field over
    /// at all, so <see cref="BuildRecordFromForm"/> always passed a hardcoded <see langword="null"/>
    /// for it -- editing and saving a QSO that had been linked to an RX-history frame (via
    /// <see cref="QsoLinkWindowViewModel"/>) silently destroyed that reverse FK on every Update, even
    /// though nothing on this form lets the user see or change it. The entry-side link
    /// (<see cref="ReceiveHistoryEntry.LinkedQsoId"/>) is the authoritative copy the Gallery UI
    /// actually reads and survives independently, so this was never user-visible data loss -- but a
    /// real silent field-drop nonetheless, exactly this sweep's tracked failure class.</summary>
    private string? _editingReceivedImageId;

    /// <summary>Bumped by every action that changes WHAT the form represents -- <see cref="New"/>'s
    /// own click and a row-selection change (<see cref="OnSelectedEntryChanged"/>) -- never by
    /// <see cref="ResetForm"/> itself (called internally by <see cref="LogAsync"/>/<see cref="UpdateAsync"/>
    /// on their own success path, which must NOT look like a navigation event to this guard).
    /// <see cref="LogAsync"/>/<see cref="UpdateAsync"/> capture this before their own multi-second
    /// await (<see cref="ILogbookSessionService.LogQsoAsync"/> can make a real QRZ HTTPS upload) and
    /// compare it after -- if it changed, the user has since selected a different row or clicked New,
    /// so the eventual <see cref="ResetForm"/>/status-message write is skipped rather than silently
    /// wiping whatever the user is now doing (Tier B audit finding: no guard existed at all before
    /// this, unlike <see cref="QsoLinkWindowViewModel"/>'s own explicit re-entrancy guards for the
    /// same class of race).</summary>
    private int _formGeneration;

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

    /// <summary>ui_transition_plan.md step 5 (T2-8): the rest of this app speaks MHz
    /// (<see cref="RadioStatusViewModel.FrequencyDisplay"/>, <c>RxImagePaneViewModel.LatchedFrequencyDisplay</c>)
    /// -- <see cref="FormFrequencyHz"/> itself stays Hz (the storage/ADIF unit, unchanged), this is
    /// the MHz-facing edit surface the form actually binds to. Same 6-decimal-place (1 Hz)
    /// resolution as those other displays' own "{0:0.000000} MHz" format. Invalid/empty input on
    /// SET is a no-op, not a value-clearing side effect -- an in-progress keystroke (Avalonia's
    /// default TextBox binding trigger fires per-keystroke, not on lost-focus) must not blank out an
    /// otherwise-valid <see cref="FormFrequencyHz"/> just because the operator briefly typed
    /// something unparseable while editing (e.g. a trailing "14." mid-entry) -- explicitly clearing
    /// the field (Backspace to empty) is the one recognized way to null it, matching every other
    /// nullable form field's own "empty means unset" convention here.
    /// Code-review finding: this is a REAL backing field, not a computed proxy over
    /// <see cref="FormFrequencyHz"/> -- a computed proxy re-raises its own PropertyChanged from
    /// inside the setter's own write, which risks Avalonia rewriting the TextBox mid-keystroke.
    /// <see cref="OnFormFrequencyHzChanged"/> pushes model -> text only for non-editing-driven
    /// changes (guarded by <see cref="_isEditingFrequencyMhzText"/>); <see cref="OnFormFrequencyMhzTextChanged"/>
    /// pushes text -> model.</summary>
    [ObservableProperty]
    private string _formFrequencyMhzText = string.Empty;

    private bool _isEditingFrequencyMhzText;

    partial void OnFormFrequencyMhzTextChanged(string value)
    {
        _isEditingFrequencyMhzText = true;
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                FormFrequencyHz = null;
                return;
            }

            // Range guard: an out-of-range double (e.g. a pasted "1e20") converts to `long` with an
            // unspecified result -- reject instead of storing garbage into the QSO row/ADIF FREQ.
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
                && double.IsFinite(mhz) && mhz is >= 0 and <= 1_000_000)
            {
                FormFrequencyHz = (long)Math.Round(mhz * 1_000_000.0);
            }
        }
        finally
        {
            _isEditingFrequencyMhzText = false;
        }
    }

    partial void OnFormFrequencyHzChanged(long? value)
    {
        if (_isEditingFrequencyMhzText)
        {
            return;
        }

        FormFrequencyMhzText = value is { } hz
            ? (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture)
            : string.Empty;
    }

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
    /// signal) lives in <see cref="RefreshInternalAsync"/> below.
    ///
    /// Tier B audit finding: <see cref="RefreshInternalAsync"/> itself deliberately does NOT clear
    /// <see cref="StatusMessage"/> on success -- every OTHER caller of it (<see cref="LogAsync"/>/
    /// <see cref="UpdateAsync"/>/<see cref="ImportAdifAsync"/>/<see cref="ExportAdifAsync"/>) sets
    /// its OWN status message right after refreshing and must not have that clobbered by a generic
    /// "refresh succeeded" no-op. This standalone command is the one caller that genuinely wants
    /// "clear the banner on success" -- without it, a Search failure's error message stuck around
    /// forever, even once a later search succeeded.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (await RefreshInternalAsync())
        {
            StatusMessage = null;
        }
    }

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

        _formGeneration++;
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
        _formGeneration++;
        ResetForm();
        StatusMessage = null;
    }

    /// <summary>Called from <c>MainWindow.axaml.cs</c>'s <c>RxImagePaneViewModel.LogQsoRequested</c>
    /// handler -- "Log QSO" on the RX pane switches to this tab with a fresh entry pre-filled from
    /// what that pane already knows live. Uses <see cref="New"/>'s full "start clean" semantics
    /// (clears <see cref="StatusMessage"/> too, not just <see cref="ResetForm"/>'s form fields) --
    /// landing on a freshly-prefilled form under a stale "Logged. ADIF forwarded 1/1..." message
    /// left over from a previous action would read as "this was already logged."
    ///
    /// <paramref name="frequencyHz"/>/<paramref name="radioMode"/> (ui_transition_plan.md step 5,
    /// T1-6): now real, sourced from the RX frame's own LATCHED metadata (step 6, T2-4) -- the
    /// caller passes <c>RxImagePaneViewModel.LatchedFrequencyHz</c>/<c>LatchedRigMode</c>, falling
    /// back to <c>RadioStatusViewModel.CurrentFrequencyHz</c>/<c>CurrentRadioModeOrNull</c> only when
    /// the frame has none (an abandoned/partial frame, or one received before this feature existed).
    /// Both stay <see langword="null"/> -- never a fabricated value -- when neither source has one;
    /// the form fields remain freely editable either way, same as every other prefilled field here.
    /// <paramref name="name"/>/<paramref name="qth"/>/<paramref name="gridSquare"/> carry over
    /// (code-review finding, rx-log-qso.md) from a QRZ lookup the RX pane already performed
    /// (<c>RxImagePaneViewModel.LookupName</c>/<c>LookupQth</c>/<c>LookupGrid</c>); dropping them
    /// would silently discard a lookup the user already did and make them repeat it on this tab.
    /// </summary>
    public void PrefillForNewEntry(string? callsign, string? sstvModeId, DateTimeOffset startUtc, string? name, string? qth, string? gridSquare, long? frequencyHz = null, RadioMode? radioMode = null)
    {
        Log.PrefillForNewEntryInvoked(_logger, callsign, sstvModeId);
        _formGeneration++;
        ResetForm();
        StatusMessage = null;
        FormCallsign = callsign;
        FormSstvModeId = sstvModeId;
        FormStartUtc = startUtc;
        FormName = name;
        FormQth = qth;
        FormGridSquare = gridSquare;
        FormFrequencyHz = frequencyHz;
        FormMode = radioMode;
    }

    /// <summary>Also clears <see cref="SelectedEntry"/> -- without this, selecting row A, clicking
    /// New, then clicking row A again would never re-fire <see cref="OnSelectedEntryChanged"/>
    /// (same value assigned twice, no <c>PropertyChanged</c>), leaving the form stuck empty with
    /// Update disabled until a manual Refresh.</summary>
    private void ResetForm()
    {
        _editingId = null;
        _editingReceivedImageId = null;
        IsEditing = false;
        SelectedEntry = null;
        FormCallsign = null;
        FormStartUtc = DateTimeOffset.UtcNow;
        FormEndUtc = null;
        FormFrequencyHz = null;
        // Code-review nit: an equal-value assignment above raises no PropertyChanged (source
        // generator suppresses it), so OnFormFrequencyHzChanged wouldn't fire if FormFrequencyHz was
        // already null -- an unparseable value the operator left typed in the box would otherwise
        // survive a New click. Display-only staleness (BuildRecordFromForm reads FormFrequencyHz,
        // never the text), but clear it explicitly so New always shows an empty field.
        FormFrequencyMhzText = string.Empty;
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
        _editingReceivedImageId = record.ReceivedImageId;
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
        // Tier B audit finding: LogQsoAsync can make a real, multi-second QRZ HTTPS upload
        // (LogbookSessionService.LogQsoAsync). Captured BEFORE that await -- if the user selects a
        // different row or clicks New while this is in flight, _formGeneration changes, and every
        // write below that would otherwise clobber their now-current form/status is skipped instead
        // of silently wiping whatever they've since navigated to.
        var formGeneration = _formGeneration;
        var record = BuildRecordFromForm(Guid.NewGuid().ToString());

        LogQsoResult result;
        try
        {
            result = await _logbook.LogQsoAsync(record);
        }
        catch (Exception ex)
        {
            Log.LogFailed(_logger, ex);
            if (formGeneration == _formGeneration)
            {
                StatusMessage = _localization.GetString("Panes.Logbook.Error.LogFailed");
            }

            return;
        }

        // ResetForm() (not New()) -- New() would null the status line this just set.
        var statusMessage = BuildLogStatusMessage(result);
        if (formGeneration == _formGeneration)
        {
            ResetForm();
        }

        // Tier B audit finding: this used to set StatusMessage BEFORE the trailing refresh below,
        // so a refresh failure's own SearchFailed message silently clobbered it -- the QSO (and any
        // QRZ upload) had already genuinely succeeded or failed by this point, and the user never
        // saw which. Set AFTER the refresh instead, so this method's own outcome always wins
        // regardless of whether the follow-up list refresh happened to succeed.
        await RefreshInternalAsync();
        if (formGeneration == _formGeneration)
        {
            StatusMessage = statusMessage;
        }
    }

    private bool CanUpdate() => _editingId is not null && !string.IsNullOrWhiteSpace(FormCallsign);

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (_editingId is null)
        {
            return;
        }

        var formGeneration = _formGeneration;
        var record = BuildRecordFromForm(_editingId, _editingReceivedImageId);

        try
        {
            await _logbook.UpdateQsoAsync(record);
        }
        catch (Exception ex)
        {
            Log.UpdateFailed(_logger, ex);
            if (formGeneration == _formGeneration)
            {
                StatusMessage = _localization.GetString("Panes.Logbook.Error.UpdateFailed");
            }

            return;
        }

        // Deliberately no ADIF-UDP/QRZ status line here -- UpdateQsoAsync never re-pushes (see
        // its own doc comment), so there is nothing to report beyond "saved." ResetForm() (not
        // New()) -- New() would null the status line just set below.
        if (formGeneration == _formGeneration)
        {
            ResetForm();
        }

        // Same reordering as LogAsync's own fix, same reasoning: set AFTER the trailing refresh so
        // a refresh failure can't clobber this method's own genuine "saved" outcome.
        await RefreshInternalAsync();
        if (formGeneration == _formGeneration)
        {
            StatusMessage = _localization.GetString("Panes.Logbook.Status.Updated");
        }
    }

    /// <summary><paramref name="receivedImageId"/> defaults to <see langword="null"/> for a brand
    /// new QSO (<see cref="LogAsync"/> -- no RX-history link can exist yet for a record that doesn't
    /// exist yet); <see cref="UpdateAsync"/> passes <see cref="_editingReceivedImageId"/> explicitly
    /// so editing an already-linked QSO preserves that link instead of silently dropping it.</summary>
    private QsoRecord BuildRecordFromForm(string id, string? receivedImageId = null) => new(
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
        receivedImageId);

    /// <summary>ADIF-UDP forwarding status clause: a distinct "not forwarded (no destinations
    /// configured)" string when <see cref="LogQsoResult.AdifUdpEnabledCount"/> is 0, rather than a
    /// confusing "0/0" reading -- <see cref="LogQsoResult.AdifUdpSentCount"/> only carries meaning
    /// once at least one destination is enabled.</summary>
    private string BuildLogStatusMessage(LogQsoResult result)
    {
        var adifUdp = result.AdifUdpEnabledCount > 0
            ? _localization.GetString("Panes.Logbook.Status.AdifUdpForwarded", result.AdifUdpSentCount, result.AdifUdpEnabledCount)
            : _localization.GetString("Panes.Logbook.Status.AdifUdpNotConfigured");

        var qrz = result.QrzUploaded
            ? _localization.GetString("Panes.Logbook.Status.QrzUploaded")
            : result.QrzError is not null
                ? _localization.GetString("Panes.Logbook.Status.QrzFailed", result.QrzError)
                : _localization.GetString("Panes.Logbook.Status.QrzNotSent");

        return _localization.GetString("Panes.Logbook.Status.LoggedFormat", adifUdp, qrz);
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
            //
            // Tier B audit finding: only report ImportPartial if that recovery refresh itself
            // actually succeeded -- this used to overwrite unconditionally, so if the refresh ALSO
            // failed, its own Error.SearchFailed message (which is the one telling the user the list
            // they're looking at might not even reflect the partial import) was silently discarded
            // in favor of a message implying the list is now trustworthy.
            if (await RefreshInternalAsync())
            {
                StatusMessage = _localization.GetString("Panes.Logbook.Error.ImportPartial");
            }

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
        //
        // Tier B audit finding: this used to ignore the refresh's own success/failure. If it failed,
        // Entries stayed at whatever it was before (a DIFFERENT query's stale results, or empty),
        // and the code below still proceeded to export (the real file is always correct --
        // ExportAdifFileAsync re-queries independently) and then reported "Exported N" using that
        // stale Entries.Count -- a provably wrong number shown to the user, with the actual error
        // that explains it silently discarded. Bails out on failure instead, leaving
        // RefreshInternalAsync's own Error.SearchFailed message in place.
        if (!await RefreshInternalAsync())
        {
            return;
        }

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

        [LoggerMessage(Level = LogLevel.Information, Message = "Prefilled a new entry from the RX pane: callsign={Callsign}, sstvModeId={SstvModeId}")]
        public static partial void PrefillForNewEntryInvoked(ILogger logger, string? callsign, string? sstvModeId);

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
