using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class QsoLinkWindowViewModelTests
{
    private static readonly ReceiveHistoryEntry SampleEntry =
        new("entry-1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);

    private static QsoLinkWindowViewModel CreateVm(
        FakeLogbookSessionService? logbook = null,
        FakeReceiveHistoryStore? historyStore = null,
        ReceiveHistoryEntry? entry = null) =>
        new(
            logbook ?? new FakeLogbookSessionService(),
            historyStore ?? new FakeReceiveHistoryStore { EntriesToReturn = [entry ?? SampleEntry] },
            new FakeLocalizationService(),
            NullLogger<QsoLinkWindowViewModel>.Instance,
            entry ?? SampleEntry);

    private static QsoRecord SampleQsoRecord(string id = "qso-1") =>
        new(id, "N0CALL", DateTimeOffset.UtcNow, null, null, null, "robot36", null, null, null, null, null, null, null, null, false, false);

    [AvaloniaFact]
    public void Constructor_EntryHasDecodedCallsignAndNrRst_SeedsNewCallsignAndNewRstReceived()
    {
        // fsk_cwid.md A-P3b: "seed ... in the Gallery's Log-entry flow when no QSO is linked (today
        // both refuse to guess -- with a real decoded value there is no guessing)".
        var entry = SampleEntry with { DecodedCallsign = "W1AW", DecodedNrRst = "595001" };
        var vm = CreateVm(entry: entry);

        Assert.Equal("W1AW", vm.NewCallsign);
        Assert.Equal("595001", vm.NewRstReceived);
        Assert.Null(vm.NewRstSent);
    }

    [AvaloniaFact]
    public void Constructor_EntryHasNoDecodedStationId_LeavesNewCallsignAndNewRstReceivedNull()
    {
        // Regression pin for the "no guess" behavior this seeding must not break -- an entry with
        // nothing decoded still leaves the create-new fields genuinely empty, not some fabricated
        // value.
        var vm = CreateVm();

        Assert.Null(vm.NewCallsign);
        Assert.Null(vm.NewRstReceived);
    }

    [AvaloniaFact]
    public async Task LinkSelectedAsync_ExistingQso_LinksEntryBeforeUpdatingReverseFk_RaisesLinkedAndRequestClose()
    {
        // FakeLogbookSessionService.UpdateQsoAsync silently no-ops if the record's Id isn't already in
        // Records -- the QSO must be seeded first or the reverse-FK assertion below would pass
        // vacuously without proving anything.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("qso-1"));

        var historyStore = new FakeReceiveHistoryStore { EntriesToReturn = [SampleEntry] };
        var vm = CreateVm(logbook, historyStore);
        Dispatcher.UIThread.RunJobs();

        var linkedQsoId = "";
        var closed = false;
        vm.Linked += id => linkedQsoId = id;
        vm.RequestClose += () => closed = true;

        vm.SelectedQso = logbook.Records[0];
        await vm.LinkSelectedCommand.ExecuteAsync(null);

        Assert.Equal("qso-1", linkedQsoId);
        Assert.True(closed);
        Assert.Equal("qso-1", historyStore.EntriesToReturn[0].LinkedQsoId);
        Assert.Equal(SampleEntry.Id, logbook.Records[0].ReceivedImageId);
    }

    [AvaloniaFact]
    public async Task LinkSelectedAsync_EntryNoLongerExists_SurfacesErrorMessage_DoesNotClose_NeverTouchesTheQso()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("qso-1"));

        // Entry NOT in EntriesToReturn -- simulates the entry no longer existing in the store by
        // the time this click reaches it.
        var historyStore = new FakeReceiveHistoryStore { EntriesToReturn = [] };
        var vm = CreateVm(logbook, historyStore);
        Dispatcher.UIThread.RunJobs();

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.SelectedQso = logbook.Records[0];
        await vm.LinkSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Panes.RxHistory.Error.EntryNoLongerExists", vm.ErrorMessage);
        Assert.False(closed);
        // Auditor-caught write-ordering fix: SetLinkedQsoIdAsync runs FIRST, so a missing entry must
        // mean UpdateQsoAsync (the reverse-FK write) was never even attempted -- proven here via
        // UpdateCallCount, not just "the record looks unchanged" (which a same-value overwrite would
        // also satisfy).
        Assert.Equal(0, logbook.UpdateCallCount);
    }

    [AvaloniaFact]
    public async Task CreateAndLinkAsync_Success_LogsWithUtcStartAndReceivedImageId_ThenLinksAndCloses()
    {
        var logbook = new FakeLogbookSessionService();
        var historyStore = new FakeReceiveHistoryStore { EntriesToReturn = [SampleEntry] };
        var vm = CreateVm(logbook, historyStore);
        Dispatcher.UIThread.RunJobs();

        var linkedQsoId = "";
        var closed = false;
        vm.Linked += id => linkedQsoId = id;
        vm.RequestClose += () => closed = true;

        vm.NewCallsign = "DL2QSK";
        await vm.CreateAndLinkCommand.ExecuteAsync(null);

        Assert.True(closed);
        var logged = Assert.Single(logbook.Records);
        Assert.Equal(linkedQsoId, logged.Id);
        Assert.Equal("DL2QSK", logged.Callsign);
        Assert.Equal(SampleEntry.ReceivedAt.ToUniversalTime(), logged.StartUtc);
        Assert.Equal(SampleEntry.Id, logged.ReceivedImageId);
        Assert.Equal(SampleEntry.ModeId, logged.SstvModeId);
        Assert.Equal(logged.Id, historyStore.EntriesToReturn[0].LinkedQsoId);
    }

    [AvaloniaFact]
    public async Task CreateAndLinkAsync_EntryNoLongerExists_SurfacesDistinctMessage_DoesNotClose_DisablesRetry()
    {
        var logbook = new FakeLogbookSessionService();
        // Entry NOT in EntriesToReturn -- LogQsoAsync still succeeds (it's a separate store), only
        // the entry-side link fails.
        var historyStore = new FakeReceiveHistoryStore { EntriesToReturn = [] };
        var vm = CreateVm(logbook, historyStore);
        Dispatcher.UIThread.RunJobs();

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.NewCallsign = "DL2QSK";
        await vm.CreateAndLinkCommand.ExecuteAsync(null);

        // Distinct from LinkSelectedAsync's own missing-entry message: the QSO genuinely exists now.
        Assert.Equal("QsoLink.Error.LoggedButLinkFailed", vm.ErrorMessage);
        Assert.False(closed);
        Assert.Single(logbook.Records);

        // The double-log/double-QRZ-push guard: a second click must be a complete no-op, not a
        // second QSO record.
        Assert.False(vm.CreateAndLinkCommand.CanExecute(null));
        await vm.CreateAndLinkCommand.ExecuteAsync(null);
        Assert.Single(logbook.Records);
    }

    [AvaloniaFact]
    public void CreateAndLinkCommand_WhitespaceCallsign_CannotExecute()
    {
        var vm = CreateVm();
        Dispatcher.UIThread.RunJobs();

        vm.NewCallsign = "   ";
        Assert.False(vm.CreateAndLinkCommand.CanExecute(null));

        vm.NewCallsign = "N0CALL";
        Assert.True(vm.CreateAndLinkCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void LinkSelectedCommand_NoSelection_CannotExecute()
    {
        var vm = CreateVm();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.LinkSelectedCommand.CanExecute(null));

        vm.SelectedQso = SampleQsoRecord();
        Assert.True(vm.LinkSelectedCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Cancel_RaisesRequestCloseOnly()
    {
        var vm = CreateVm();
        Dispatcher.UIThread.RunJobs();

        var closed = false;
        var linked = false;
        vm.RequestClose += () => closed = true;
        vm.Linked += _ => linked = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.False(linked);
    }
}
