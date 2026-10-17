using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>the HamDRM status chip and the HamDRM half of the amber "Decoding" look.</summary>
public sealed class RadioStatusDrmTests
{
    /// <summary>Echoes the key with its format arguments, so chip text is assertable.</summary>
    private sealed class FormattingLocalization : ILocalizationService
    {
        public IReadOnlyList<System.Globalization.CultureInfo> AvailableCultures { get; } = [System.Globalization.CultureInfo.InvariantCulture];

        public System.Globalization.CultureInfo CurrentCulture => System.Globalization.CultureInfo.InvariantCulture;

        public event Action? CultureChanged { add { } remove { } }

        public Task SetCultureAsync(System.Globalization.CultureInfo culture, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key, params object[] args) =>
            args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

        public bool TryGetString(string key, out string value)
        {
            value = key;
            return false;
        }
    }

    private sealed class FakeRecorder : IDrmReceptionRecorder
    {
        public event Action<DrmStoredFile>? FileStored;

        public Task<bool> DrainAsync(TimeSpan? timeout = null) => Task.FromResult(true);

        public void Raise(string name) => FileStored?.Invoke(new DrmStoredFile(File(name), "/tmp/x", null));

        private static DrmReceivedFile File(string name) =>
            new(name, name, name, ReadOnlyMemory<byte>.Empty, 1, string.Empty, 0, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, false, false);
    }

    private static (RadioStatusViewModel Vm, FakeSstvSessionService Session, FakeRecorder Recorder) Create(bool receiving = true)
    {
        var session = new FakeSstvSessionService { IsReceiving = receiving };
        var recorder = new FakeRecorder();
        var vm = new RadioStatusViewModel(new FakeRadioSessionService(), session, new FormattingLocalization(),
            new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance, drmRecorder: recorder);
        Dispatcher.UIThread.RunJobs();
        return (vm, session, recorder);
    }

    private static DrmSignalState State(long generation = 1, bool active = true, bool frameSync = true, int received = 3,
        int total = 3, int expected = 40, bool complete = false, string callsign = "PA0ABC", string name = "pic.jp2") =>
        new(active, frameSync, frameSync, frameSync, frameSync ? 1 : -1, frameSync ? 1 : -1, frameSync ? 1 : -1, callsign,
            received, total, name, generation, expected, complete);

    private static void Ticks(RadioStatusViewModel vm, int n)
    {
        for (int i = 0; i < n; i++)
            vm.PollTickForTests();
    }

    [AvaloniaFact]
    public void Receiving_ShowsModeCallsignAndRealProgress_AndTurnsTheReceivingLookAmber()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State());
        vm.PollTickForTests();

        Assert.Equal("RadioStatus.Drm.Prefix(B) · PA0ABC · RadioStatus.Drm.Progress(3,40)", vm.DrmChipText);
        Assert.True(vm.IsDrmChipVisible);
        Assert.True(vm.IsDrmReceiving);
        Assert.True(vm.IsDecodingImage);
        Assert.False(vm.IsReceivingIdle);
        Assert.Equal("RadioStatus.Decoding", vm.ReceivingButtonLabel);
        Assert.Equal("MainWindow.StatusBar.Decoding", vm.RxStatusBarText);
        Assert.Equal("RadioStatus.Drm.Tooltip(pic.jp2,B,RadioStatus.Drm.Occupancy1)", vm.DrmChipTooltip);
    }

    [AvaloniaFact]
    public void UnknownTotal_ShowsASegmentCount_AndNoCallsignOmitsIt()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State(expected: 0, total: 5, received: 5, callsign: ""));
        vm.PollTickForTests();

        Assert.Equal("RadioStatus.Drm.Prefix(B) · RadioStatus.Drm.Segments(5)", vm.DrmChipText);
    }

    [AvaloniaFact]
    public void SyncWithoutAFile_ShowsSync_AndIsNotAmber()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State(received: 0, total: 0, expected: 0));
        vm.PollTickForTests();

        Assert.Equal("RadioStatus.Drm.Prefix(B) · RadioStatus.Drm.Sync", vm.DrmChipText);
        Assert.False(vm.IsDecodingImage);
        Assert.True(vm.IsReceivingIdle);
    }

    [AvaloniaFact]
    public void ACompletedFile_WithSyncStillHeld_IsNotReceiving()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State(received: 40, total: 40, expected: 40, complete: true));
        vm.PollTickForTests();

        Assert.False(vm.IsDrmReceiving);
        Assert.False(vm.IsDecodingImage);
        Assert.Equal("RadioStatus.Drm.Prefix(B) · RadioStatus.Drm.Sync", vm.DrmChipText);
    }

    [AvaloniaFact]
    public void OlderGenerations_AreDropped_EqualOnesAccepted()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State(generation: 5, received: 7));
        session.RaiseDrmSignalStateChanged(State(generation: 4, received: 1));
        vm.PollTickForTests();
        Assert.Contains("Progress(7,40)", vm.DrmChipText);

        session.RaiseDrmSignalStateChanged(State(generation: 5, received: 8));
        vm.PollTickForTests();
        Assert.Contains("Progress(8,40)", vm.DrmChipText);

        // A late live state of a stopped receiver never overwrites its newer terminal state.
        session.RaiseDrmSignalStateChanged(DrmSignalState.Inactive(6));
        session.RaiseDrmSignalStateChanged(State(generation: 5, received: 9));
        vm.PollTickForTests();
        Assert.Null(vm.DrmChipText);
        Assert.False(vm.IsDecodingImage);
    }

    [AvaloniaFact]
    public void SyncLossMidFile_HoldsSyncLostForFiveSeconds_ThenHides()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State());
        vm.PollTickForTests();
        session.RaiseDrmSignalStateChanged(State(frameSync: false));

        Ticks(vm, RadioStatusViewModel.DrmSyncLostHoldTicks);
        Assert.Equal("RadioStatus.Drm.SyncLost", vm.DrmChipText);
        Assert.False(vm.IsDrmReceiving);
        Assert.Equal("RadioStatus.Drm.Tooltip(pic.jp2,B,RadioStatus.Drm.Occupancy1)", vm.DrmChipTooltip);

        vm.PollTickForTests();
        Assert.Null(vm.DrmChipText);
    }

    [AvaloniaFact]
    public void SyncReturningWithinTheHold_ResumesProgress()
    {
        var (vm, session, _) = Create();
        session.RaiseDrmSignalStateChanged(State());
        vm.PollTickForTests();
        session.RaiseDrmSignalStateChanged(State(frameSync: false));
        Ticks(vm, 5);
        session.RaiseDrmSignalStateChanged(State(received: 4));
        vm.PollTickForTests();

        Assert.Contains("Progress(4,40)", vm.DrmChipText);
        Assert.True(vm.IsDrmReceiving);
    }

    [AvaloniaFact]
    public void TheTerminalState_ClearsAtOnce_ButReceivedSurvivesItsEightSeconds()
    {
        var (vm, session, recorder) = Create();
        session.RaiseDrmSignalStateChanged(State());
        vm.PollTickForTests();

        session.RaiseDrmSignalStateChanged(DrmSignalState.Inactive(2));
        vm.PollTickForTests();
        Assert.Null(vm.DrmChipText);

        recorder.Raise("pic\u0007.jp2");
        vm.PollTickForTests();
        Assert.Equal("RadioStatus.Drm.Received(pic.jp2)", vm.DrmChipText);
        Ticks(vm, RadioStatusViewModel.DrmReceivedTicks - 1);
        Assert.NotNull(vm.DrmChipText);
        vm.PollTickForTests();
        Assert.Null(vm.DrmChipText);
    }

    [AvaloniaFact]
    public void Precedence_ReceivingBeatsReceived_AndResyncAfterACompletedFileShowsNoStaleProgress()
    {
        var (vm, session, recorder) = Create();
        recorder.Raise("old.jp2");
        session.RaiseDrmSignalStateChanged(State(received: 2));
        vm.PollTickForTests();
        Assert.Contains("Progress(2,40)", vm.DrmChipText);

        // The new file is stored and the run-out keeps sync: "received" wins over "sync".
        session.RaiseDrmSignalStateChanged(State(received: 40, total: 40, complete: true));
        recorder.Raise("new.jp2");
        vm.PollTickForTests();
        Assert.Equal("RadioStatus.Drm.Received(new.jp2)", vm.DrmChipText);
        Assert.False(vm.IsDecodingImage);

        // After "received" expires, a sync drop and return on the completed file shows sync, not progress.
        Ticks(vm, RadioStatusViewModel.DrmReceivedTicks);
        session.RaiseDrmSignalStateChanged(State(frameSync: false, received: 40, total: 40, complete: true));
        vm.PollTickForTests();
        Assert.Null(vm.DrmChipText);
        session.RaiseDrmSignalStateChanged(State(received: 40, total: 40, complete: true));
        vm.PollTickForTests();
        Assert.Equal("RadioStatus.Drm.Prefix(B) · RadioStatus.Drm.Sync", vm.DrmChipText);
        Assert.False(vm.IsDecodingImage);
    }

    [Fact]
    public void ModeLetter_AndSanitize()
    {
        Assert.Equal(["A", "B", "E", "?", "?"], new[] { 0, 1, 2, 3, -1 }.Select(RadioStatusViewModel.ModeLetter));
        Assert.Equal("ab", RadioStatusViewModel.Sanitize(" a\r\nb\u0000 "));
        Assert.Equal(48, RadioStatusViewModel.Sanitize(new string('x', 100)).Length);
    }
}
