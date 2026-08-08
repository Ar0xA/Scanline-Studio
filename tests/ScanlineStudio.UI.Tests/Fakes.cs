using System.Linq;
using System.Reactive.Subjects;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

/// <summary>Real <see cref="ISettingsStore"/> backing an in-memory <see cref="AppSettings"/> --
/// used to construct a REAL <see cref="OptionsSettingsService"/> in tests (exercising its actual
/// section-translation logic) rather than faking that service away entirely.</summary>
internal sealed class FakeSettingsStore : ISettingsStore, IDisposable
{
    private readonly Subject<AppSettings> _changes = new();

    public AppSettings Settings { get; set; } = new();

    public IObservable<AppSettings> Changes => _changes;

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _changes.OnNext(settings);
        return Task.CompletedTask;
    }

    public void Dispose() => _changes.Dispose();
}

internal sealed class FakeAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; set; } = [];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; set; } = [];

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeLocalizationService : ILocalizationService
{
    public IReadOnlyList<System.Globalization.CultureInfo> AvailableCultures { get; } = [System.Globalization.CultureInfo.GetCultureInfo("en")];

    public System.Globalization.CultureInfo CurrentCulture { get; } = System.Globalization.CultureInfo.GetCultureInfo("en");

    public event Action? CultureChanged;

    public Task SetCultureAsync(System.Globalization.CultureInfo culture, CancellationToken ct = default)
    {
        CultureChanged?.Invoke();
        return Task.CompletedTask;
    }

    public string GetString(string key, params object[] args) => key;
}

internal sealed class FakeWaterfallSource : IWaterfallSource, IDisposable
{
    private readonly Subject<WaterfallFrame> _frames = new();

    public IObservable<WaterfallFrame> Frames => _frames;

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
    }

    public void Emit(WaterfallFrame frame) => _frames.OnNext(frame);

    public void Dispose() => _frames.Dispose();
}

internal sealed class FakeReceivedImageBuffer : IReceivedImageBuffer
{
    public IImageSource Current { get; set; } = new ArrayImageSourceStub();

    public double? Progress { get; set; }

    public event Action? Updated;

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public void RaiseUpdated() => Updated?.Invoke();

    private sealed class ArrayImageSourceStub : IImageSource
    {
        public int Width => 1;

        public int Height => 1;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => new Rgb24[1];
    }
}

internal sealed class FakeSstvSessionService : ISstvSessionService
{
    public List<(SstvModeDefinition Mode, IImageSource Image)> TransmitCalls { get; } = [];

    public IWaterfallSource Waterfall { get; } = new FakeWaterfallSource();

    public IReceivedImageBuffer ReceivedImage { get; } = new FakeReceivedImageBuffer();

    public IReadOnlyList<SstvModeDefinition> AvailableModes { get; set; } = [];

    public bool IsReceiving { get; set; }

    public bool ThrowOnStartReceiving { get; set; }

    public bool ThrowOnStopReceiving { get; set; }

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action? MaintenanceWarningRaised;

    public event Action? MaintenanceWarningCleared;

    public event Action? MaintenanceCriticalStopRaised;

    public int RequestReSyncCallCount { get; private set; }

    public void RequestReSync() => RequestReSyncCallCount++;

    public int ForceModeCallCount { get; private set; }

    public SstvModeDefinition? LastForcedMode { get; private set; }

    public void ForceMode(SstvModeDefinition mode)
    {
        ForceModeCallCount++;
        LastForcedMode = mode;
    }

    public Task StartReceivingAsync(CancellationToken ct = default)
    {
        if (ThrowOnStartReceiving)
        {
            throw new InvalidOperationException("no audio device configured");
        }

        IsReceiving = true;
        return Task.CompletedTask;
    }

    public Task StopReceivingAsync()
    {
        if (ThrowOnStopReceiving)
        {
            throw new InvalidOperationException("no audio device configured");
        }

        IsReceiving = false;
        return Task.CompletedTask;
    }

    /// <summary>When true, <see cref="TransmitAsync"/> doesn't return until <paramref name="ct"/> is
    /// cancelled (throwing <see cref="OperationCanceledException"/> then) -- lets a test simulate an
    /// in-flight transmission long enough to push several <c>RadioState</c> updates through
    /// <c>IRadioSessionService.StateChanges</c> and observe a Stop TX/SWR-cutoff actually cancel it,
    /// instead of the call completing instantly before any cutoff check could ever run.</summary>
    public bool BlockUntilCancelled { get; set; }

    public Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        TransmitCalls.Add((mode, image));
        if (BlockUntilCancelled)
        {
            var tcs = new TaskCompletionSource();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        return Task.CompletedTask;
    }

    public List<(double FrequencyHz, TimeSpan Duration, bool LeaveKeyedAfterTune)> TuneCalls { get; } = [];

    public Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default)
    {
        TuneCalls.Add((frequencyHz, duration, leaveKeyedAfterTune));
        return Task.CompletedTask;
    }

    public int TxVolumePercent { get; set; } = 100;

    public Task<int> GetTxVolumePercentAsync(CancellationToken ct = default) => Task.FromResult(TxVolumePercent);

    public bool IsPttLocked { get; private set; }

    public List<bool> PttLockCalls { get; } = [];

    public Task SetPttLockAsync(bool locked, CancellationToken ct = default)
    {
        PttLockCalls.Add(locked);
        IsPttLocked = locked;
        return Task.CompletedTask;
    }

    public Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        TxVolumePercent = percent;
        return Task.CompletedTask;
    }

    public string? ConfiguredPlaybackDeviceName { get; set; }

    public Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(ConfiguredPlaybackDeviceName);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseMaintenanceWarningRaised() => MaintenanceWarningRaised?.Invoke();

    public void RaiseMaintenanceWarningCleared() => MaintenanceWarningCleared?.Invoke();

    public void RaiseMaintenanceCriticalStopRaised() => MaintenanceCriticalStopRaised?.Invoke();
}

internal sealed class FakeRadioSessionService : IRadioSessionService, IDisposable
{
    private readonly Subject<RadioState> _stateChanges = new();
    private readonly Subject<RadioConnectionEvent> _connectionEvents = new();

    public RadioState? LastKnownState { get; set; }

    public RadioCapabilities Capabilities { get; set; } = RadioCapabilities.None;

    public IObservable<RadioState> StateChanges => _stateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _connectionEvents;

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public List<long> SetFrequencyCalls { get; } = [];

    public List<RadioMode> SetModeCalls { get; } = [];

    /// <summary>Simulates the real <c>RadioController.RequireProtocol</c> throw when no radio is
    /// connected -- a routine, common state that a real hands-on run once crashed the whole app on
    /// (an uncaught <see cref="InvalidOperationException"/> propagating out of an
    /// <c>AsyncRelayCommand</c>); guards against that regression.</summary>
    public bool ThrowOnSetFrequencyOrMode { get; set; }

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default)
    {
        if (ThrowOnSetFrequencyOrMode)
        {
            throw new InvalidOperationException("No radio connected -- call ConnectAsync first.");
        }

        SetFrequencyCalls.Add(hz);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default)
    {
        if (ThrowOnSetFrequencyOrMode)
        {
            throw new InvalidOperationException("No radio connected -- call ConnectAsync first.");
        }

        SetModeCalls.Add(mode);
        return Task.CompletedTask;
    }

    public Task SetPttAsync(bool tx, CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<FrequencyPreset> Presets { get; set; } = [];

    public Task<IReadOnlyList<FrequencyPreset>> GetFrequencyPresetsAsync(CancellationToken ct = default) => Task.FromResult(Presets);

    public Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default)
    {
        Presets = presets;
        return Task.CompletedTask;
    }

    public RadioSafetySpec SafetySpec { get; set; } = new(false, 3.0);

    public Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default) => Task.FromResult(SafetySpec);

    public Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default)
    {
        SafetySpec = spec;
        return Task.CompletedTask;
    }

    public void Push(RadioState state) => _stateChanges.OnNext(state);

    public void PushConnectionEvent(RadioConnectionEvent evt) => _connectionEvents.OnNext(evt);

    public void Dispose()
    {
        _stateChanges.Dispose();
        _connectionEvents.Dispose();
    }
}

internal sealed class FakeImageFileLoader : IImageFileLoader
{
    public IImageSource? ResultToReturn { get; set; }

    /// <summary>When true, neither <see cref="LoadAsync"/> nor <see cref="LoadOriginalAsync"/>
    /// resolves immediately -- each queues a <see cref="TaskCompletionSource{TResult}"/> onto
    /// <see cref="PendingLoads"/> for the test to complete manually, in whatever order it needs to
    /// provoke a specific interleaving (e.g. two overlapping picks racing). Deliberately does NOT
    /// auto-cancel when <c>ct</c> is cancelled -- a fake that auto-throws on cancellation would mask
    /// whatever cancellation-guard behavior the test is trying to exercise.</summary>
    public bool UseManualGating { get; set; }

    public List<TaskCompletionSource<IImageSource>> PendingLoads { get; } = [];

    public Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
        => GatedOrImmediate();

    public Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default)
        => GatedOrImmediate();

    private Task<IImageSource> GatedOrImmediate()
    {
        if (!UseManualGating)
        {
            return Task.FromResult(ResultToReturn ?? throw new InvalidOperationException("No result configured."));
        }

        var tcs = new TaskCompletionSource<IImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingLoads.Add(tcs);
        return tcs.Task;
    }
}

internal sealed class FakeFilePickerService : IFilePickerService
{
    public string? PathToReturn { get; set; } = "/tmp/fake.png";

    public Task<string?> PickImageFileAsync() => Task.FromResult(PathToReturn);
}

internal sealed class FakeStockImageLibrary : IStockImageLibrary
{
    public List<StockImageEntry> EntriesToReturn { get; set; } = [];

    public IImageSource? ThumbnailToReturn { get; set; }

    public IImageSource? FullImageToReturn { get; set; }

    public Task<IReadOnlyList<StockImageEntry>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StockImageEntry>>(EntriesToReturn);

    public Task<IImageSource> LoadThumbnailAsync(StockImageEntry entry, int maxDimension, CancellationToken ct = default)
        => Task.FromResult(ThumbnailToReturn ?? throw new InvalidOperationException("No thumbnail configured."));

    public Task<IImageSource> LoadFullAsync(StockImageEntry entry, int targetWidth, int targetHeight, CancellationToken ct = default)
        => Task.FromResult(FullImageToReturn ?? throw new InvalidOperationException("No full image configured."));

    public Task<IImageSource> LoadOriginalAsync(StockImageEntry entry, CancellationToken ct = default)
        => Task.FromResult(FullImageToReturn ?? throw new InvalidOperationException("No full image configured."));
}

/// <summary>Records every call instead of doing real image work — <see cref="Crop"/>/
/// <see cref="ApplyOverlay"/> return the input unchanged (identity) so callers can assert on
/// exactly which <see cref="IImageSource"/> instance was passed in (e.g. working copy vs.
/// original); <see cref="Resize"/> returns a genuinely new <see cref="ArrayImageSource"/> at the
/// requested dimensions, since several call sites depend on the returned size being real.</summary>
internal sealed class FakeTransmitImagePreparer : ITransmitImagePreparer
{
    public int CropCallCount { get; private set; }

    public int ResizeCallCount { get; private set; }

    public int ApplyOverlayCallCount { get; private set; }

    public List<IImageSource> CropSources { get; } = [];

    public List<(int Width, int Height, bool PreserveAspect)> ResizeCalls { get; } = [];

    public List<ImageOverlay> Overlays { get; } = [];

    public IImageSource Crop(IImageSource source, NormalizedRect region)
    {
        CropCallCount++;
        CropSources.Add(source);
        return source;
    }

    public IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect)
    {
        ResizeCallCount++;
        ResizeCalls.Add((width, height, preserveAspect));
        return new ArrayImageSource(width, height, new Rgb24[width * height]);
    }

    public IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay)
    {
        ApplyOverlayCallCount++;
        Overlays.Add(overlay);
        return source;
    }
}

internal sealed class FakeReceiveHistoryStore : IReceiveHistoryStore
{
    public List<ReceiveHistoryEntry> EntriesToReturn { get; set; } = [];

    public IImageSource? ThumbnailToReturn { get; set; }

    public List<ReceiveHistoryEntry> RecordedEntries { get; } = [];

    public List<ReceiveHistoryFilter> QueryFilters { get; } = [];

    public string ImagesDirectory { get; set; } = "/tmp/scanlinestudio-history";

    /// <summary>Actually applies the filter (unlike a bare stub) so a test can verify the
    /// Gallery tab's All/Today wiring, not just that some entries render.</summary>
    public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default)
    {
        QueryFilters.Add(filter);
        IEnumerable<ReceiveHistoryEntry> results = EntriesToReturn;
        if (filter.ModeId is not null)
        {
            results = results.Where(e => e.ModeId == filter.ModeId);
        }

        if (filter.From is not null)
        {
            results = results.Where(e => e.ReceivedAt >= filter.From.Value);
        }

        if (filter.To is not null)
        {
            results = results.Where(e => e.ReceivedAt <= filter.To.Value);
        }

        return Task.FromResult<IReadOnlyList<ReceiveHistoryEntry>>(results.ToList());
    }

    public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
        => Task.FromResult(ThumbnailToReturn ?? throw new InvalidOperationException("No thumbnail configured."));

    public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        RecordedEntries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => Task.FromResult(ImagesDirectory);

    /// <summary>Actually mutates <see cref="EntriesToReturn"/> (matching <see cref="QueryAsync"/>'s
    /// own "actually applies" convention above), so a Gallery-side test can verify a note/flag/
    /// QSO-link edit round-trips through a subsequent query, not just that the call was made.</summary>
    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
        Task.FromResult(TryUpdateEntry(entryId, e => e with { Note = note }));

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
        Task.FromResult(TryUpdateEntry(entryId, e => e with { IsFlagged = isFlagged }));

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
        Task.FromResult(TryUpdateEntry(entryId, e => e with { LinkedQsoId = qsoId }));

    private bool TryUpdateEntry(string entryId, Func<ReceiveHistoryEntry, ReceiveHistoryEntry> update)
    {
        var index = EntriesToReturn.FindIndex(e => e.Id == entryId);
        if (index < 0)
        {
            return false;
        }

        EntriesToReturn[index] = update(EntriesToReturn[index]);
        return true;
    }
}
