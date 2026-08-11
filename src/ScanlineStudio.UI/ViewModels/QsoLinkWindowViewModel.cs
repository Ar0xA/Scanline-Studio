using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Modal dialog backing the Gallery tab's "Open in Log" button
/// (<c>RxHistoryPaneViewModel.OpenInLogCommand</c>) -- links a <see cref="ReceiveHistoryEntry"/> to
/// a <see cref="QsoRecord"/> via <see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/>, either by
/// picking an already-logged QSO or by logging a new one on the spot. Constructed directly with
/// <c>new</c> by <c>RxHistoryPaneViewModel</c> (not DI-resolved) -- unlike
/// <see cref="OptionsWindowViewModel"/> (resolved via the app's <c>IServiceProvider</c> because
/// <c>MainViewModel</c> already holds one for other reasons), the caller here doesn't have one and
/// this VM only needs 4 already-injectable services.
///
/// <b>Thread affinity</b>: deliberately does NOT use <c>ConfigureAwait(false)</c> anywhere (code-review
/// correction -- an earlier version did, matching <c>RxHistoryPaneViewModel.PersistNoteDebouncedAsync</c>'s
/// convention, but that was wrong here: every command below is invoked from the UI thread and then
/// mutates UI-bound state -- <see cref="SearchResults"/>, <see cref="ErrorMessage"/>,
/// <see cref="CreateAndLinkCommand"/>'s own <c>NotifyCanExecuteChanged</c>, and
/// <see cref="RequestClose"/> (which calls <c>Window.Close()</c>) -- all of which need the captured
/// UI `SynchronizationContext` to resume correctly. The two service calls this VM awaits
/// (<c>SqliteReceiveHistoryStore</c>/<c>SqliteLogbookRepository</c>) happen to complete synchronously
/// today, which is why an earlier manual run didn't surface this, but that's an implementation detail
/// of the SQLite provider, not a contract -- <c>ILogbookSessionService.LogQsoAsync</c> also awaits a
/// JSON settings load and an optional QRZ HTTP POST, both genuine thread-pool hops. Same await style
/// as <see cref="OptionsWindowViewModel"/>, the only other dialog VM in this app. <see cref="Linked"/>
/// therefore always fires on the UI thread; <c>RxHistoryPaneViewModel.OpenInLog</c>'s own
/// <c>Dispatcher.UIThread.Post</c> around its handler is harmless extra defense, not load-bearing.
///
/// <b>Reverse FK</b>: <see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/> only writes
/// <see cref="ReceiveHistoryEntry.LinkedQsoId"/>. <see cref="QsoRecord.ReceivedImageId"/> is the
/// matching reverse foreign key (that interface method's own doc comment: "already designed for
/// this exact link") -- both link paths here also write it via
/// <see cref="ILogbookSessionService.UpdateQsoAsync"/>/<see cref="ILogbookSessionService.LogQsoAsync"/>.
/// Not transactional across the two stores, same non-transactional precedent as
/// <c>LogbookSessionService.ImportAdifFileAsync</c>.</summary>
public sealed partial class QsoLinkWindowViewModel : ViewModelBase
{
    private readonly ILogbookSessionService _logbook;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ILocalizationService _localization;
    private readonly ILogger<QsoLinkWindowViewModel> _logger;
    private readonly ReceiveHistoryEntry _entry;

    /// <summary>Set once <see cref="CreateAndLinkAsync"/>'s own <see cref="ILogbookSessionService.LogQsoAsync"/>
    /// call succeeds -- guards <see cref="CanCreateAndLink"/> against a second click after a
    /// subsequent <see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/> failure. Without this, a
    /// user reading "the frame could no longer be linked" (which reads like nothing was saved) would
    /// naturally retry, creating a SECOND <see cref="QsoRecord"/> and pushing it to GridTracker/QRZ
    /// a second time -- the QSO from the first click is already real and already logged by the time
    /// that failure can happen.</summary>
    private string? _createdQsoId;

    /// <summary>Fires with the linked <see cref="QsoRecord.Id"/> once the entry-side link write
    /// (<see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/>) succeeds -- before
    /// <see cref="RequestClose"/>. Always fires on the UI thread, see this class's own doc
    /// comment.</summary>
    public event Action<string>? Linked;

    /// <summary>Same convention as <see cref="OptionsWindowViewModel.RequestClose"/> -- the View's
    /// code-behind subscribes <c>vm.RequestClose += Close;</c>.</summary>
    public event Action? RequestClose;

    [ObservableProperty]
    private string? _callsignFilter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkSelectedCommand))]
    private QsoRecord? _selectedQso;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateAndLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateAndLinkCommand))]
    private string? _newCallsign;

    [ObservableProperty]
    private string? _newRstSent;

    [ObservableProperty]
    private string? _newRstReceived;

    [ObservableProperty]
    private string? _newNotes;

    public ObservableCollection<QsoRecord> SearchResults { get; } = [];

    public QsoLinkWindowViewModel(
        ILogbookSessionService logbook,
        IReceiveHistoryStore historyStore,
        ILocalizationService localization,
        ILogger<QsoLinkWindowViewModel> logger,
        ReceiveHistoryEntry entry)
    {
        _logbook = logbook;
        _historyStore = historyStore;
        _localization = localization;
        _logger = logger;
        _entry = entry;

        // Best-effort initial load, same fire-and-forget convention as every other pane's
        // construction-time query (e.g. RxHistoryPaneViewModel's own RefreshAsync).
        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var callsign = string.IsNullOrWhiteSpace(CallsignFilter) ? null : CallsignFilter.Trim();

        // Drops the recency floor entirely once the user types an exact callsign, so an older
        // matching QSO stays reachable -- only the unfiltered browse view needs a bound to avoid
        // pulling the whole logbook (same reasoning as LogbookPaneViewModel's own 30-day default,
        // just a wider 90-day window since this is a lookup, not a browse).
        DateTimeOffset? from = callsign is null
            ? new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-90), TimeSpan.Zero)
            : null;

        try
        {
            var results = await _logbook.SearchAsync(new LogbookQuery(callsign, from, null));
            SearchResults.Clear();
            foreach (var result in results)
            {
                SearchResults.Add(result);
            }
        }
        catch (Exception ex)
        {
            Log.SearchFailed(_logger, ex);
            ErrorMessage = _localization.GetString("QsoLink.Error.SearchFailed");
        }
    }

    private bool CanLinkSelected() => SelectedQso is not null && !IsBusy;

    /// <summary>Links the frame to an ALREADY-LOGGED QSO. Order matters (auditor-caught, plan-review
    /// fix): <see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/> runs FIRST. If the entry has
    /// aged out of retention (returns <see langword="false"/>), nothing has been written to the QSO
    /// side either -- there's no partial state to clean up. Only once the entry-side link succeeds
    /// does this best-effort write the reverse FK via <see cref="ILogbookSessionService.UpdateQsoAsync"/>;
    /// a failure there is logged and swallowed (the Gallery UI reads <c>LinkedQsoId</c>, already
    /// correct at that point, not the reverse FK -- see this class's own doc comment).</summary>
    [RelayCommand(CanExecute = nameof(CanLinkSelected))]
    private async Task LinkSelectedAsync()
    {
        // Defense-in-depth re-entrancy guard, same reasoning as CreateAndLinkAsync's own -- a direct
        // ExecuteAsync call bypasses CanExecute.
        if (IsBusy || SelectedQso is not { } selected)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            bool linked;
            try
            {
                linked = await _historyStore.SetLinkedQsoIdAsync(_entry.Id, selected.Id);
            }
            catch (Exception ex)
            {
                Log.LinkFailed(_logger, _entry.Id, selected.Id, ex);
                ErrorMessage = _localization.GetString("QsoLink.Error.LinkFailed");
                return;
            }

            if (!linked)
            {
                // Reachable, not defensive -- IReceiveHistoryStore.SetLinkedQsoIdAsync's own doc
                // comment: the retention-trim ring buffer can delete this row between the Gallery
                // load and this click.
                Log.LinkEntryMissing(_logger, _entry.Id);
                ErrorMessage = _localization.GetString("Panes.RxHistory.Error.EntryNoLongerExists");
                return;
            }

            try
            {
                await _logbook.UpdateQsoAsync(selected with { ReceivedImageId = _entry.Id });
            }
            catch (Exception ex)
            {
                // Best-effort, non-transactional (see class doc comment) -- the entry-side link
                // above already succeeded and is what the Gallery UI actually reads, so this failure
                // doesn't block Linked/RequestClose below, only the reverse FK stays stale.
                Log.ReverseFkUpdateFailed(_logger, selected.Id, ex);
            }

            Linked?.Invoke(selected.Id);
            RequestClose?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateAndLink() => !string.IsNullOrWhiteSpace(NewCallsign) && !IsBusy && _createdQsoId is null;

    /// <summary>Logs a NEW QSO for the frame and links it. Order stays create-then-link (the QSO
    /// must exist before anything can reference it): once <see cref="ILogbookSessionService.LogQsoAsync"/>
    /// succeeds, <see cref="_createdQsoId"/> is set and <see cref="CreateAndLinkCommand"/> is
    /// permanently disabled for this dialog instance BEFORE attempting the link write -- a QSO that
    /// already exists and was already pushed to GridTracker/QRZ (if enabled) must never be logged a
    /// second time just because the entry-side link happened to fail afterward.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateAndLink))]
    private async Task CreateAndLinkAsync()
    {
        // Defense-in-depth, not redundant with CanCreateAndLink: a bound Button's IsEnabled respects
        // CanExecute, but a direct ExecuteAsync call (e.g. a fast double-invoke racing the
        // CanExecuteChanged notification) does NOT re-check it -- this guard is the one that actually
        // prevents a second QsoRecord/QRZ push, CanCreateAndLink only prevents the button from being
        // clickable in the first place.
        // Code-review nit fix: also re-checks NewCallsign, not just IsBusy/_createdQsoId -- the
        // comment above says this guard exists because a direct ExecuteAsync bypasses CanExecute
        // entirely, so it must actually re-verify every CanCreateAndLink condition, not a subset.
        if (IsBusy || _createdQsoId is not null || string.IsNullOrWhiteSpace(NewCallsign))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var record = new QsoRecord(
                Guid.NewGuid().ToString(),
                NewCallsign!.Trim(),
                _entry.ReceivedAt.ToUniversalTime(),
                null,
                null,
                null,
                _entry.ModeId,
                NewRstSent,
                NewRstReceived,
                null,
                null,
                null,
                null,
                NewNotes,
                _entry.Id);

            LogQsoResult result;
            try
            {
                result = await _logbook.LogQsoAsync(record);
            }
            catch (Exception ex)
            {
                Log.CreateFailed(_logger, ex);
                ErrorMessage = _localization.GetString("QsoLink.Error.CreateFailed");
                return;
            }

            Log.QsoLogged(_logger, result.Record.Id, result.GridTrackerSent, result.QrzUploaded, result.QrzError);

            // Set BEFORE attempting the link write below -- see this method's own doc comment.
            _createdQsoId = result.Record.Id;
            CreateAndLinkCommand.NotifyCanExecuteChanged();

            bool linked;
            try
            {
                linked = await _historyStore.SetLinkedQsoIdAsync(_entry.Id, result.Record.Id);
            }
            catch (Exception ex)
            {
                // Code-review fix: this used to share LinkSelectedAsync's own QsoLink.Error.LinkFailed
                // ("Could not link this frame to the QSO") -- misleading here specifically, same
                // reasoning as the !linked branch below: the QSO record already exists and was already
                // pushed to GridTracker/QRZ by this point, an exception here isn't "nothing happened."
                Log.LinkFailed(_logger, _entry.Id, result.Record.Id, ex);
                ErrorMessage = _localization.GetString("QsoLink.Error.LoggedButLinkFailed");
                return;
            }

            if (!linked)
            {
                // Distinct message from LinkSelectedAsync's own missing-entry case: by this point the
                // QSO record genuinely exists (and was already pushed) -- "not logged" would be
                // misleading. CanCreateAndLink already stays false via _createdQsoId, so retrying
                // this exact click is impossible; the dialog stays open so the user can read this.
                Log.LinkEntryMissing(_logger, _entry.Id);
                ErrorMessage = _localization.GetString("QsoLink.Error.LoggedButLinkFailed");
                return;
            }

            Linked?.Invoke(result.Record.Id);
            RequestClose?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCancel() => !IsBusy;

    /// <summary>Code-review fix: guarded on <see cref="CanCancel"/> (not always-enabled as an earlier
    /// version had it) -- without this, clicking Cancel while a write is in flight (e.g. a slow QRZ
    /// upload during <see cref="CreateAndLinkAsync"/>) closed the dialog while the QSO still got
    /// logged/linked and the Gallery badge still flipped underneath the now-closed window, and
    /// <see cref="RequestClose"/> then fired a second time once that write's own success path
    /// reached its own <c>RequestClose?.Invoke()</c> call. No <see cref="CancellationToken"/> is
    /// threaded through the service calls (that's a bigger change than this dialog's scope) -- this
    /// only stops the user from closing OUT FROM UNDER an in-flight write, it doesn't cancel the
    /// write itself.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => RequestClose?.Invoke();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "SearchAsync failed; QSO picker results stay empty")]
        public static partial void SearchFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetLinkedQsoIdAsync failed for entry {EntryId} -> qso {QsoId}")]
        public static partial void LinkFailed(ILogger logger, string entryId, string qsoId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetLinkedQsoIdAsync: entry {EntryId} no longer exists")]
        public static partial void LinkEntryMissing(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "UpdateQsoAsync (reverse ReceivedImageId FK) failed for qso {QsoId}")]
        public static partial void ReverseFkUpdateFailed(ILogger logger, string qsoId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LogQsoAsync failed while creating a QSO from a Gallery frame")]
        public static partial void CreateFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QSO {QsoId} logged: gridTrackerSent={GridTrackerSent}, qrzUploaded={QrzUploaded}, qrzError={QrzError}")]
        public static partial void QsoLogged(ILogger logger, string qsoId, bool gridTrackerSent, bool qrzUploaded, string? qrzError);
    }
}
