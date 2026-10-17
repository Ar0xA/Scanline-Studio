using System.Globalization;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>The built-in calling-frequency catalog and the header menu that applies it through CAT.</summary>
public sealed class CallingFrequencyTests
{
    private static readonly (string Band, long LowHz, long HighHz)[] Bands =
    [
        ("160 m", 1_800_000, 2_000_000), ("80 m", 3_500_000, 4_000_000), ("40 m", 7_000_000, 7_300_000),
        ("30 m", 10_100_000, 10_150_000), ("20 m", 14_000_000, 14_350_000), ("17 m", 18_068_000, 18_168_000),
        ("15 m", 21_000_000, 21_450_000), ("12 m", 24_890_000, 24_990_000), ("10 m", 28_000_000, 29_700_000),
        ("6 m", 50_000_000, 54_000_000), ("2 m", 144_000_000, 148_000_000), ("70 cm", 420_000_000, 450_000_000),
    ];

    private static long[] Khz(params long[] khz) => [.. khz.Select(k => k * 1000)];

    private static long[] Where(Func<CallingFrequency, bool> predicate) =>
        [.. CallingFrequencies.All.Where(predicate).Select(f => f.FrequencyHz)];

    [Fact]
    public void EveryRow_IsAWholeKhzAmateurFrequency_InItsBand_WithAPublishedSideband()
    {
        Assert.Equal(41, CallingFrequencies.All.Count);
        foreach (CallingFrequency f in CallingFrequencies.All)
        {
            Assert.Equal(0, f.FrequencyHz % 1000);
            Assert.Contains(f.Mode, new[] { RadioMode.Usb, RadioMode.Lsb, RadioMode.Fm });
            var band = Assert.Single(Bands, b => b.Band == f.Band);
            Assert.InRange(f.FrequencyHz, band.LowHz, band.HighHz);
        }

        Assert.Equal(Bands.Select(b => b.Band), CallingFrequencies.All.Select(f => f.Band).Distinct());
    }

    [Fact]
    public void Rows_AreSortedByFrequency_UsbBeforeFm_WithNoDuplicates()
    {
        var keys = CallingFrequencies.All.Select(f => (f.FrequencyHz, Fm: f.Mode == RadioMode.Fm)).ToArray();
        Assert.Equal(keys.OrderBy(k => k.FrequencyHz).ThenBy(k => k.Fm), keys);
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void DrmRows_AreExactlyTheSix_AndOnlyTheyRequest2800Hz()
    {
        long[] drm = Khz(3733, 7058, 7171, 7214, 14233, 21337);
        Assert.Equal(drm, Where(f => f.Kind.HasFlag(CallingFrequencyKind.Drm)));
        Assert.Equal(drm, Where(f => f.BandwidthHz is not null));
        Assert.All(CallingFrequencies.All.Where(f => f.BandwidthHz is not null), f => Assert.Equal(2800, f.BandwidthHz));
        Assert.Equal(Khz(7171, 14233), Where(f => f.Kind == (CallingFrequencyKind.Sstv | CallingFrequencyKind.Drm)));
    }

    [Fact]
    public void RegionAndTagRows_MatchTheSources()
    {
        Assert.Equal(Khz(3730, 3733, 7040, 7058, 7165, 14240), Where(f => f.Region.HasFlag(CallingFrequencyRegion.Eu)));
        Assert.Equal(Khz(3845, 7170, 7181), Where(f => f.Region.HasFlag(CallingFrequencyRegion.Us)));
        Assert.Equal(Khz(3640, 7170, 7214, 145625, 145625, 433775, 433775), Where(f => f.Region.HasFlag(CallingFrequencyRegion.Au)));
        Assert.Equal(Khz(145800), Where(f => f.Tag != CallingFrequencyTag.None));
        Assert.DoesNotContain(CallingFrequencies.All, f => f.Region != CallingFrequencyRegion.None && f.Tag != CallingFrequencyTag.None);
    }

    private static RadioStatusViewModel CreateViewModel(FakeRadioSessionService radioSession, ILocalizationService? localization = null)
    {
        var vm = new RadioStatusViewModel(radioSession, new FakeSstvSessionService(), localization ?? new FakeLocalizationService(),
            new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static FakeRadioSessionService Session() => new() { Capabilities = RadioCapabilities.SetBandwidth };

    private static CallingFrequencyItemViewModel Item(RadioStatusViewModel vm, long hz, RadioMode mode) =>
        vm.CallingFrequencyBands.SelectMany(b => b.Items).Single(i => i.Entry.FrequencyHz == hz && i.Entry.Mode == mode);

    private static async Task PickAsync(RadioStatusViewModel vm, CallingFrequencyItemViewModel item)
    {
        await vm.ApplyCallingFrequencyCommand.ExecuteAsync(item);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Menu_HasTwelveBands_HoldingEveryRow_EachWithThePickCommand()
    {
        var vm = CreateViewModel(Session());

        Assert.Equal(Bands.Select(b => b.Band), vm.CallingFrequencyBands.Select(b => b.Band));
        CallingFrequencyItemViewModel[] items = [.. vm.CallingFrequencyBands.SelectMany(b => b.Items)];
        Assert.Equal(CallingFrequencies.All, items.Select(i => i.Entry));
        Assert.All(items, i => Assert.Same(vm.ApplyCallingFrequencyCommand, i.SelectCommand));
    }

    [AvaloniaTheory]
    [InlineData(14_233_000, RadioMode.Usb, 2800)]
    [InlineData(14_230_000, RadioMode.Usb, 2400)]
    [InlineData(7_171_000, RadioMode.Lsb, 2800)]
    [InlineData(145_500_000, RadioMode.Fm, 15000)]
    public async Task APick_SendsFrequencyThenModeThenWidth(long hz, RadioMode mode, int bandwidthHz)
    {
        var session = Session();
        var vm = CreateViewModel(session);

        await PickAsync(vm, Item(vm, hz, mode));

        Assert.Equal(["frequency", "mode", "bandwidth"], session.CallOrder);
        Assert.Equal(hz, Assert.Single(session.SetFrequencyCalls));
        Assert.Equal(mode, Assert.Single(session.SetModeCalls));
        Assert.Equal(bandwidthHz, Assert.Single(session.SetBandwidthCalls));
    }

    [AvaloniaTheory]
    [InlineData(14_230_000, RadioMode.Usb, RadioMode.Data)]
    [InlineData(7_171_000, RadioMode.Lsb, RadioMode.DataR)]
    [InlineData(145_500_000, RadioMode.Fm, RadioMode.Fm)]
    public async Task WithSsbAsPkt_SidebandsAreSentAsTheirPktVariant_FmUnchanged(long hz, RadioMode catalogMode, RadioMode sent)
    {
        var session = Session();
        var vm = CreateViewModel(session);
        vm.SsbAsPkt = true;
        Dispatcher.UIThread.RunJobs();
        session.SetModeCalls.Clear();

        await PickAsync(vm, Item(vm, hz, catalogMode));

        Assert.Equal(sent, Assert.Single(session.SetModeCalls));
    }

    [AvaloniaFact]
    public async Task APick_ClaimsSsbAsPkt_SoALaterPollCannotClearIt()
    {
        var session = Session();
        var vm = CreateViewModel(session);
        // Rig-derived only: a poll reporting Data sets it without operator ownership.
        session.Push(new RadioState(14_230_000, RadioMode.Data, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.SsbAsPkt);

        await PickAsync(vm, Item(vm, 14_230_000, RadioMode.Usb));
        session.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
    }

    [AvaloniaFact]
    public async Task TheSameEntry_PickedTwice_IsAppliedTwice()
    {
        var session = Session();
        var vm = CreateViewModel(session);
        CallingFrequencyItemViewModel item = Item(vm, 14_230_000, RadioMode.Usb);

        await PickAsync(vm, item);
        await PickAsync(vm, item);

        Assert.Equal([14_230_000L, 14_230_000L], session.SetFrequencyCalls);
    }

    [AvaloniaFact]
    public async Task WhileAFavouriteApplies_APickIsDisabled()
    {
        var session = Session();
        var vm = CreateViewModel(session);
        CallingFrequencyItemViewModel item = Item(vm, 14_230_000, RadioMode.Usb);
        session.SetFrequencyGate = new TaskCompletionSource();

        Task favourite = vm.ApplyPresetCommand.ExecuteAsync(new FrequencyPreset("x", 7_171_000, RadioMode.Lsb));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ApplyCallingFrequencyCommand.CanExecute(item));

        session.SetFrequencyGate.SetResult();
        await favourite;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ApplyCallingFrequencyCommand.CanExecute(item));
    }

    [AvaloniaFact]
    public async Task AFavouriteStartingAndFinishing_RaisesThePickCommandsCanExecuteChanged()
    {
        var session = Session();
        var vm = CreateViewModel(session);
        int raised = 0;
        vm.ApplyCallingFrequencyCommand.CanExecuteChanged += (_, _) => raised++;
        session.SetFrequencyGate = new TaskCompletionSource();

        Task favourite = vm.ApplyPresetCommand.ExecuteAsync(new FrequencyPreset("x", 7_171_000, RadioMode.Lsb));
        Dispatcher.UIThread.RunJobs();
        Assert.True(raised >= 1, "not raised when the Favourite started");
        int atStart = raised;

        session.SetFrequencyGate.SetResult();
        await favourite;
        Dispatcher.UIThread.RunJobs();
        Assert.True(raised > atStart, "not raised when the Favourite finished");
    }

    [AvaloniaFact]
    public async Task WhileAPickApplies_FavouritesAreDisabled()
    {
        var session = Session();
        var vm = CreateViewModel(session);
        var preset = new FrequencyPreset("x", 7_171_000, RadioMode.Lsb);
        session.SetFrequencyGate = new TaskCompletionSource();

        Task pick = vm.ApplyCallingFrequencyCommand.ExecuteAsync(Item(vm, 14_230_000, RadioMode.Usb));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ApplyPresetCommand.IsRunning);
        Assert.False(vm.ApplyPresetCommand.CanExecute(preset));

        session.SetFrequencyGate.SetResult();
        await pick;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ApplyPresetCommand.CanExecute(preset));
    }

    [AvaloniaTheory]
    [InlineData(14_240_000, RadioMode.Usb, "14.240000 MHz · USB · SSTV · EU")]
    [InlineData(3_733_000, RadioMode.Lsb, "3.733000 MHz · LSB · DRM · EU")]
    [InlineData(7_170_000, RadioMode.Lsb, "7.170000 MHz · LSB · SSTV · AU/US")]
    [InlineData(14_233_000, RadioMode.Usb, "14.233000 MHz · USB · SSTV/DRM")]
    [InlineData(145_800_000, RadioMode.Fm, "145.800000 MHz · FM · SSTV · ISS downlink")]
    [InlineData(433_775_000, RadioMode.Fm, "433.775000 MHz · FM · SSTV · AU")]
    public void DisplayText_UsesTheRealEnglishStrings(long hz, RadioMode mode, string expected)
    {
        // The number follows the thread culture, as FrequencyDisplay does.
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var vm = CreateViewModel(Session(), new EnglishLocaleFile());

            Assert.Equal(expected, Item(vm, hz, mode).Display);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [AvaloniaFact]
    public async Task ACultureChange_RebuildsTheMenuText()
    {
        var localization = new FakeLocalizationService();
        var vm = CreateViewModel(Session(), localization);
        IReadOnlyList<CallingFrequencyBandViewModel> before = vm.CallingFrequencyBands;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await localization.SetCultureAsync(CultureInfo.GetCultureInfo("en"));

        Assert.NotSame(before, vm.CallingFrequencyBands);
        Assert.Contains(nameof(RadioStatusViewModel.CallingFrequencyBands), raised);
    }

    /// <summary>The real en.json strings, formatted the way the app's localizer formats them.</summary>
    private sealed class EnglishLocaleFile : ILocalizationService
    {
        private readonly Dictionary<string, string> _strings = Load();

        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [CultureInfo.GetCultureInfo("en")];

        public CultureInfo CurrentCulture { get; } = CultureInfo.GetCultureInfo("en");

        public event Action? CultureChanged { add { } remove { } }

        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key, params object[] args) =>
            _strings.TryGetValue(key, out string? format) ? string.Format(CurrentCulture, format, args) : key;

        public bool TryGetString(string key, out string value)
        {
            bool found = _strings.TryGetValue(key, out string? s);
            value = s ?? key;
            return found;
        }

        private static Dictionary<string, string> Load()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string json = File.ReadAllText(Path.Combine(dir!.FullName, "assets", "locale", "en.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        }
    }
}
