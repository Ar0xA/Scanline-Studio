using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
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
    private readonly IReceiveHistoryStore _historyStore;
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

    /// <summary>ui_transition_plan.md step 15, piece (c) -- consume-on-use armed state for the
    /// duplicate-QSO warning (round-1/round-2 plan-review fix for the original bare-bool design's
    /// undefined lifetime): records exactly which (callsign, frequency, Log-vs-Update) combination
    /// was last warned about, so clicking the SAME command a second time re-checks this tuple
    /// instead of a bare flag -- editing the callsign or frequency after a warning re-runs the
    /// duplicate check against the NEW values rather than silently confirm-logging a different QSO
    /// than the one flagged. <c>ForUpdate</c> keeps <see cref="LogAsync"/>'s and
    /// <see cref="UpdateAsync"/>'s own arms from satisfying each other's guard (an arm from one
    /// command must never let the other skip its own check). Deliberately compares the RAW
    /// <see cref="QsoRecord.FrequencyHz"/>, not a derived band label -- computing a band here would
    /// need <c>ScanlineStudio.Core.Logbook.AmateurBandLookup</c>, a layer this UI project must not
    /// reference directly (band-matching stays entirely inside
    /// <see cref="ILogbookSessionService.FindLikelyDuplicateAsync"/>); comparing the raw frequency is
    /// sufficient for "is this the same submission the operator already saw a warning for" and only
    /// costs one extra (cheap, fail-open) re-check if the operator nudges the frequency within the
    /// same band before re-clicking. Cleared in <see cref="ResetForm"/> and
    /// <see cref="OnSelectedEntryChanged"/> so the armed state can never survive a navigation.</summary>
    private (string Callsign, long? FrequencyHz, bool ForUpdate)? _armedDuplicateConfirm;

    /// <summary>Backs the Log/Update button's "anyway" wording -- see
    /// <see cref="_armedDuplicateConfirm"/>'s own doc comment. Notified explicitly via
    /// <see cref="ObservableObject.OnPropertyChanged(string?)"/> at every mutation site (code-review
    /// finding: a plain derived property with no notification never updates the binding once
    /// computed).</summary>
    public bool IsConfirmingDuplicate => _armedDuplicateConfirm is not null;

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
    /// today's/this-month's search results. Loaded at construction and re-loaded after
    /// <see cref="LogAsync"/>/a successful delete (Fable UX-review finding, 2026-08-30 -- this used
    /// to only refresh at construction/delete, leaving a newly-logged QSO uncounted until the pane
    /// was reconstructed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogSizeDisplay))]
    private int _totalLoggedCount;

    // Add/Edit form fields -- every QsoRecord field except Id (generated) and ReceivedImageId (no
    // FORM field for it -- it's carried through _editingReceivedImageId instead, set via
    // PrefillForNewEntry/LoadIntoForm, not directly editable in the form).
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

    [ObservableProperty]
    private bool _formQslSent;

    [ObservableProperty]
    private bool _formQslReceived;

    public LogbookPaneViewModel(
        ILogbookSessionService logbook,
        IFilePickerService filePicker,
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        IReceiveHistoryStore historyStore,
        ILogger<LogbookPaneViewModel> logger)
    {
        _logbook = logbook;
        _filePicker = filePicker;
        _localization = localization;
        _historyStore = historyStore;
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
        ClearArmedDuplicateConfirm();
        LoadIntoForm(value);
    }

    private void ClearArmedDuplicateConfirm()
    {
        if (_armedDuplicateConfirm is null)
        {
            return;
        }

        _armedDuplicateConfirm = null;
        OnPropertyChanged(nameof(IsConfirmingDuplicate));
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
    ///
    /// <paramref name="defaultRst"/> (RST default plan, 2026-09-01): seeds BOTH
    /// <see cref="FormRstSent"/> and <see cref="FormRstReceived"/> from this one value -- SSTV's
    /// real-world convention doesn't distinguish direction, see <c>OperatorSettings.DefaultRst</c>'s
    /// own doc comment for why "595", not ham radio's classic "599". Deliberately NOT seeded from
    /// <c>RxImagePaneViewModel.DecodedNrRst</c> -- that field is "595" plus a hardcoded 3-digit
    /// picture number, never an actually-decoded RST, so it would be wrong-shaped here.
    /// </summary>
    public void PrefillForNewEntry(
        string? callsign, string? sstvModeId, DateTimeOffset startUtc, string? name, string? qth, string? gridSquare,
        long? frequencyHz = null, RadioMode? radioMode = null, string? receivedImageId = null, string? defaultRst = null)
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
        FormRstSent = defaultRst;
        FormRstReceived = defaultRst;
        // Fable UX-review finding, 2026-08-30: set AFTER ResetForm() above (which clears this to
        // null) -- LogAsync's BuildRecordFromForm call reads it, so a QSO logged from this prefill
        // now links back to the frame it came from, the same way an edited existing entry already
        // does via _editingReceivedImageId.
        _editingReceivedImageId = receivedImageId;
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
        FormQslSent = false;
        FormQslReceived = false;
        ClearArmedDuplicateConfirm();
        UpdateCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
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
        FormQslSent = record.QslSent;
        FormQslReceived = record.QslReceived;
        StatusMessage = null;
        UpdateCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
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
        // Fable UX-review finding, 2026-08-30: _editingReceivedImageId now carries through from
        // PrefillForNewEntry when this form was seeded from a decoded RX frame -- ResetForm() (called
        // by New()/prior PrefillForNewEntry) is the only thing that clears it back to null, so a
        // plain "type a callsign and click Log" still correctly logs with no link.
        var record = BuildRecordFromForm(Guid.NewGuid().ToString(), _editingReceivedImageId);

        if (await ShouldWarnInsteadOfProceedAsync(record, excludeId: null, forUpdate: false, formGeneration))
        {
            return;
        }

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

        // Round-1 code-review finding: the FK above (QsoRecord.ReceivedImageId) is only the reverse
        // side -- SqliteReceiveHistoryStore.ReceiveHistoryEntry.LinkedQsoId is what Gallery's own
        // "Logged / Not logged" row, its Unlogged filter, and the stronger delete-confirm all
        // actually read (QsoLinkWindowViewModel's own doc comment). Without this, a QSO logged
        // straight from a decoded frame set the reverse FK but Gallery still showed the frame as
        // unlogged. Best-effort, same "primary action already succeeded, don't block on this"
        // reasoning QsoLinkWindowViewModel.LinkSelectedAsync uses for its own reverse-FK write, just
        // mirrored: here the QSO record (not the entry link) is the primary action, already
        // persisted above, so a failure here is logged and swallowed rather than surfaced as a log
        // failure.
        if (record.ReceivedImageId is { } linkedEntryId)
        {
            try
            {
                await _historyStore.SetLinkedQsoIdAsync(linkedEntryId, record.Id);
            }
            catch (Exception ex)
            {
                Log.LinkReceivedImageFailed(_logger, linkedEntryId, record.Id, ex);
            }
        }

        // Worked-before plan (2026-09-01): best-effort, same reasoning as SetLinkedQsoIdAsync above --
        // the QSO is already persisted; a subscriber failure must never be reported to the operator as
        // a failed log (a retry would create a real duplicate row).
        try
        {
            QsoLogged?.Invoke(record.Callsign);
        }
        catch (Exception ex)
        {
            Log.QsoLoggedNotifyFailed(_logger, ex);
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

        // Fable UX-review finding, 2026-08-30: this count was only ever refreshed at construction
        // and after a delete -- logging a new QSO left the status bar's "log N entries" stale until
        // the next delete, observed live as "log 0 entries" with 1 QSO already in the book.
        // Global count, not form state -- unlike the guarded writes above, doesn't need the
        // formGeneration check.
        _ = LoadTotalLoggedCountAsync();
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

        if (await ShouldWarnInsteadOfProceedAsync(record, excludeId: _editingId, forUpdate: true, formGeneration))
        {
            return;
        }

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

    /// <summary>ui_transition_plan.md step 15, piece (c) -- shared by <see cref="LogAsync"/>/
    /// <see cref="UpdateAsync"/>. Returns <see langword="true"/> if the caller should STOP without
    /// logging/updating (a duplicate warning is now showing instead); <see langword="false"/> means
    /// proceed (either no duplicate found, or the operator already saw the warning for this exact
    /// callsign+frequency+command and clicked the same button again). <paramref name="excludeId"/>
    /// is <see langword="null"/> for <see cref="LogAsync"/> (a brand-new QSO can't match itself) and
    /// <see cref="_editingId"/> for <see cref="UpdateAsync"/> (so editing a QSO never flags itself).
    /// </summary>
    private async Task<bool> ShouldWarnInsteadOfProceedAsync(QsoRecord record, string? excludeId, bool forUpdate, int formGeneration)
    {
        if (_armedDuplicateConfirm is { } armed && armed.ForUpdate == forUpdate && armed.Callsign == record.Callsign && armed.FrequencyHz == record.FrequencyHz)
        {
            // Operator already saw the warning for exactly this submission and clicked the same
            // button again -- proceed, and clear the arm so a LATER, genuinely different duplicate
            // gets its own fresh warning instead of silently reusing a stale confirmation.
            ClearArmedDuplicateConfirm();
            return false;
        }

        QsoRecord? duplicate;
        try
        {
            duplicate = await _logbook.FindLikelyDuplicateAsync(record.Callsign, record.StartUtc, record.FrequencyHz, excludeId);
        }
        catch (Exception ex)
        {
            // Defense-in-depth, not redundant with the interface's own "never throws, fails open"
            // contract (see ILogbookSessionService.FindLikelyDuplicateAsync's doc comment) -- this VM
            // must never let its OWN bug (or a future implementation that forgets that contract)
            // block a real log/update over a duplicate check that couldn't run.
            Log.DuplicateCheckFailed(_logger, ex);
            duplicate = null;
        }

        if (duplicate is null)
        {
            return false;
        }

        if (formGeneration != _formGeneration)
        {
            // Stale -- the operator navigated away during the check. Don't arm/warn against a form
            // they're no longer looking at (this generation's own StatusMessage/ResetForm writes
            // would already be getting skipped below anyway, same guard as everywhere else in this
            // file -- returning true here just also skips the actual log/update on this stale call).
            return true;
        }

        _armedDuplicateConfirm = (record.Callsign, record.FrequencyHz, forUpdate);
        OnPropertyChanged(nameof(IsConfirmingDuplicate));
        StatusMessage = _localization.GetString("Panes.Logbook.Warning.PossibleDuplicate", record.Callsign);
        return true;
    }

    /// <summary>ui_transition_plan.md step 15 -- same delegate-property shape as
    /// <see cref="RxHistoryPaneViewModel.ConfirmRequested"/>'s own doc comment (a genuine
    /// request/response the Delete command awaits before continuing), set exactly once by
    /// <c>MainWindow.axaml.cs</c>. Returns <see langword="false"/> (decline) when unwired -- the
    /// safe default for a destructive action.</summary>
    public Func<ConfirmActionDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    /// <summary>Worked-before plan (2026-09-01): parent-pushed delegate, same shape as
    /// <c>TxControlsPaneViewModel.RequestTransmitTabFocus</c>/<c>RxHistoryPaneViewModel.SendToTxRequested</c>
    /// (plain assignment, not <c>+=</c> -- the assigning side, <c>MainWindow.axaml.cs</c>, is the
    /// initiator). Invoked with the just-logged callsign so <c>RxImagePaneViewModel</c>'s own
    /// worked-before indicator can refresh -- the ONLY trigger that indicator otherwise has is
    /// <c>OverrideCallsign</c> changing, so without this it would keep showing "New station" for a
    /// station just logged, until the next decode.</summary>
    public Action<string>? QsoLogged { get; set; }

    // Code-review finding: keyed off _editingId (the FORM's identity), not SelectedEntry -- a
    // RefreshAsync/RefreshInternalAsync in between (e.g. the operator clicks Refresh while a row is
    // loaded) clears Entries and, via the ListBox's own TwoWay SelectedItem binding, nulls
    // SelectedEntry right back through this VM -- but OnSelectedEntryChanged early-returns on null
    // (see its own doc comment), so IsEditing/_editingId stay set. Keying CanDeleteSelected on
    // SelectedEntry instead left the Delete button (bound to IsEditing, matching Update) visible,
    // rendered full-strength danger-red (IndustryBtnDanger has no disabled styling), and dead --
    // clicking it did nothing. Same failure class RxHistoryPaneViewModel already hit once.
    private bool CanDeleteSelected() => _editingId is not null;

    /// <summary>ui_transition_plan.md step 15 -- per-QSO manual delete, mirroring
    /// <see cref="RxHistoryPaneViewModel.DeleteSelectedEntryAsync"/>'s own shape: resolves the
    /// target QSO from <see cref="_editingId"/> (the form's own identity, matching
    /// <see cref="CanDeleteSelected"/>'s own gate) BEFORE the confirm dialog's own await, and never
    /// reads <see cref="SelectedEntry"/> at all -- a concurrent <see cref="RefreshAsync"/> (e.g. the
    /// user clicks Refresh while the dialog is open) can null <see cref="SelectedEntry"/> and even
    /// drop the row out of <see cref="Entries"/>, but <see cref="_editingId"/> and the still-live
    /// form fields survive that, so this method still knows exactly which QSO the operator meant
    /// and can still show its callsign in the dialog. One command, not a 3-command arm/confirm
    /// (unlike <c>OptionsWindowViewModel.RequestResetAll</c>'s own pattern) -- that inline shape fits
    /// a modal settings window with no live-refreshing list underneath it; this pane already has the
    /// real modal-confirm-dialog infrastructure <see cref="RxHistoryPaneViewModel"/> established for
    /// exactly this "destructive action against a row in a live list" shape.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (_editingId is not { } id)
        {
            return;
        }

        // Best-effort display name for the confirm dialog only -- Entries may no longer contain this
        // row (see this method's own doc comment), in which case the still-live form field is the
        // next best source; the delete itself always targets `id`, never a re-derived value.
        var callsign = Entries.FirstOrDefault(e => e.Id == id)?.Callsign ?? FormCallsign ?? string.Empty;

        var confirmed = await RequestConfirmDeleteAsync(callsign);
        if (!confirmed)
        {
            return;
        }

        Log.DeleteInvoked(_logger, id);
        bool deleted;
        try
        {
            deleted = await _logbook.DeleteQsoAsync(id);
        }
        catch (Exception ex)
        {
            Log.DeleteFailed(_logger, id, ex);
            StatusMessage = _localization.GetString("Panes.Logbook.Error.DeleteFailed");
            return;
        }

        // The record no longer exists either way (deleted just now, or already gone) -- reset the
        // form so Update doesn't stay live against a nonexistent row (code-review finding: the
        // `!deleted` branch used to skip this, unlike the real-delete path below, leaving Update
        // "succeed" with SqliteLogbookRepository.UpdateAsync silently affecting zero rows).
        if (_editingId == id)
        {
            ResetForm();
        }

        // RefreshInternalAsync (not RefreshAsync): RefreshAsync clears StatusMessage on success,
        // which would wipe the status line this method is about to set -- same reasoning
        // LogAsync/UpdateAsync's own trailing refresh already documents. Also fixed (code-review
        // finding): StatusMessage now set AFTER the refresh on the not-deleted path too, so a
        // refresh failure's own SearchFailed message can't clobber it -- matches every other path
        // in this file.
        await RefreshInternalAsync();
        StatusMessage = _localization.GetString(deleted ? "Panes.Logbook.Status.Deleted" : "Panes.Logbook.Error.EntryNoLongerExists");
        if (deleted)
        {
            _ = LoadTotalLoggedCountAsync();
        }
    }

    private async Task<bool> RequestConfirmDeleteAsync(string callsign)
    {
        if (ConfirmRequested is null)
        {
            return false;
        }

        // The dialog message names the callsign being deleted AND states the external-push
        // limitation up front (auditor plan-review finding, round 2): DeleteQsoAsync does not
        // retract an already-made GridTracker/ADIF-UDP broadcast or QRZ upload, and the confirm
        // dialog is the only moment the operator can actually act on that information -- a
        // doc-comment-only note would be invisible to the person making the decision.
        var confirmVm = new ConfirmActionDialogViewModel(
            _localization.GetString("Panes.Logbook.ConfirmDeleteTitle"),
            _localization.GetString("Panes.Logbook.ConfirmDeleteMessage", callsign));
        return await ConfirmRequested(confirmVm).ConfigureAwait(true);
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
        receivedImageId,
        FormQslSent,
        FormQslReceived);

    /// <summary>ADIF-UDP forwarding status clause: a distinct "not forwarded (no destinations
    /// configured)" string when <see cref="LogQsoResult.AdifUdpEnabledCount"/> is 0, rather than a
    /// confusing "0/0" reading -- <see cref="LogQsoResult.AdifUdpSentCount"/> only carries meaning
    /// once at least one destination is enabled.</summary>
    private string BuildLogStatusMessage(LogQsoResult result)
    {
        // Auditor code-review finding (2026-08-31): a settings/ADIF-export/ADIF-UDP failure (i.e.
        // not a QRZ-specific one) must not render through the QRZ-only "QRZ: failed (...)" string
        // below -- see LogQsoResult.PostPersistError's own doc comment.
        if (result.PostPersistError is not null)
        {
            return _localization.GetString("Panes.Logbook.Status.LoggedWithPostPersistError", result.PostPersistError);
        }

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetLinkedQsoIdAsync failed for entry {EntryId} -> qso {QsoId}")]
        public static partial void LinkReceivedImageFailed(ILogger logger, string entryId, string qsoId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QsoLogged subscriber threw; the RX pane's worked-before indicator may go stale until the next decode")]
        public static partial void QsoLoggedNotifyFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "UpdateQsoAsync failed")]
        public static partial void UpdateFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ImportAdifFileAsync failed")]
        public static partial void ImportFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ExportAdifFileAsync failed")]
        public static partial void ExportFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "DeleteQsoAsync invoked: {Id}")]
        public static partial void DeleteInvoked(ILogger logger, string id);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DeleteQsoAsync failed: {Id}")]
        public static partial void DeleteFailed(ILogger logger, string id, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "FindLikelyDuplicateAsync failed; proceeding as if no duplicate was found")]
        public static partial void DuplicateCheckFailed(ILogger logger, Exception ex);
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
