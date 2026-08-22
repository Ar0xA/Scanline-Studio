using System.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
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

    /// <summary>When set, <see cref="LoadAsync"/> throws this instead of returning
    /// <see cref="Settings"/> -- lets a test simulate a corrupt/locked settings file without a real
    /// filesystem, e.g. TxControlsPaneViewModel_OpenEditorForSourceAsync_SettingsLoadThrows_
    /// ResetsIsEditorOpen_InsteadOfStayingStuckOpen (spec/18-path-to-1.0.md High item 2 code
    /// review).</summary>
    public Exception? LoadAsyncException { get; set; }

    public IObservable<AppSettings> Changes => _changes;

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
        LoadAsyncException is { } ex ? Task.FromException<AppSettings>(ex) : Task.FromResult(Settings);

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

    public Exception? RefreshAsyncException { get; set; }

    public Task RefreshAsync(CancellationToken ct = default) =>
        RefreshAsyncException is { } ex ? Task.FromException(ex) : Task.CompletedTask;
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

    /// <summary>Captures the most recent call's key/args, in addition to still returning the raw
    /// key -- lets a test verify the CALLER passed the right computed numeric arguments (e.g. a
    /// display property's own arithmetic) without needing a real locale-file-backed formatter.</summary>
    public string? LastKey { get; private set; }

    public object[] LastArgs { get; private set; } = [];

    public Exception? ThrowOnGetString { get; set; }

    /// <summary>When set, <see cref="ThrowOnGetString"/> only fires for this specific key -- lets a
    /// test simulate a single locale entry with a mismatched format placeholder (a real
    /// FormatException source) without also breaking every OTHER GetString call the method under
    /// test makes along the way (e.g. an unrelated "now testing..." status key with no args).</summary>
    public string? ThrowOnGetStringForKey { get; set; }

    public string GetString(string key, params object[] args)
    {
        if (ThrowOnGetString is not null && (ThrowOnGetStringForKey is null || ThrowOnGetStringForKey == key))
        {
            throw ThrowOnGetString;
        }

        LastKey = key;
        LastArgs = args;
        return key;
    }
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

    public int Generation { get; set; }

    public event Action? Updated;

    public event Action<string, int>? Saved;

    public List<string> SavedPaths { get; } = [];

    public Task SaveAsync(string path, CancellationToken ct = default)
    {
        SavedPaths.Add(path);
        return Task.CompletedTask;
    }

    public void NotifySaved(string path, int generation) => Saved?.Invoke(path, generation);

    public void RaiseUpdated() => Updated?.Invoke();

    public void RaiseSaved(string path, int generation) => Saved?.Invoke(path, generation);

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

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    public event Action<TransmitProgressInfo>? TransmitProgressChanged;

    public event Action<bool>? CapturePausedForTransmitChanged;

    public string? OperatorCallsign { get; set; }

    /// <summary>When set, <see cref="GetOperatorCallsignAsync"/> awaits this instead of completing
    /// synchronously -- lets a test hold the call mid-flight (matching the real, uncached settings
    /// disk read it stands in for) to reproduce a generation-changes-while-awaiting race.</summary>
    public TaskCompletionSource<string?>? OperatorCallsignGate { get; set; }

    public Task<string?> GetOperatorCallsignAsync(CancellationToken ct = default) =>
        OperatorCallsignGate?.Task ?? Task.FromResult(OperatorCallsign);

    public StationIdTransmitOptions StationIdTransmitOptionsToReturn { get; set; } = StationIdTransmitOptions.None;

    public Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default) => Task.FromResult(StationIdTransmitOptionsToReturn);

    public event Action? MaintenanceWarningRaised;

    public event Action? MaintenanceWarningCleared;

    public event Action? MaintenanceCriticalStopRaised;

    public int RequestReSyncCallCount { get; private set; }

    public void RequestReSync() => RequestReSyncCallCount++;

    public int RequestCorrectSlantCallCount { get; private set; }

    public void RequestCorrectSlant() => RequestCorrectSlantCallCount++;

    public int ForceModeCallCount { get; private set; }

    public SstvModeDefinition? LastForcedMode { get; private set; }

    public void ForceMode(SstvModeDefinition mode)
    {
        ForceModeCallCount++;
        LastForcedMode = mode;
    }

    public int StartReceivingCallCount { get; private set; }

    public Task StartReceivingAsync(CancellationToken ct = default)
    {
        StartReceivingCallCount++;
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

    public string? ConfiguredCaptureDeviceName { get; set; }

    public Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(ConfiguredCaptureDeviceName);

    public double? SlantPpm { get; set; }

    public int? SyncOffsetSamples { get; set; }

    public double SignalPeakLevel { get; set; }

    public bool IsLevelOverdriven { get; set; }

    public bool AutoSlantEnabled { get; set; } = true;

    public double? SyncFrequencyCorrectionHz { get; set; }

    public int BufferedSampleCount { get; set; }

    public int CaptureOverrunCount { get; set; }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseDecodeRestarted(SstvModeDefinition mode) => DecodeRestarted?.Invoke(mode);

    public void RaiseStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

    public void RaiseTransmitProgress(TransmitProgressInfo info) => TransmitProgressChanged?.Invoke(info);

    public void RaiseCapturePausedForTransmitChanged(bool paused) => CapturePausedForTransmitChanged?.Invoke(paused);

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

    // ScanlineStudio.UI.Tests never constructs a real SstvSessionService (only FakeSstvSessionService),
    // so unlike the same-named fake in ScanlineStudio.Application.Tests, this value never actually
    // gates anything -- present only to satisfy IRadioSessionService's interface contract.
    public string RigId { get; set; } = "fake-radio";

    public IObservable<RadioState> StateChanges => _stateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _connectionEvents;

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public RadioConnectionTestResult TestConnectionResultToReturn { get; set; } = new(true, "fake-rig", RadioCapabilities.None, null);

    public List<RadioConnectionSpec> TestConnectionCalls { get; } = [];

    public Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default)
    {
        TestConnectionCalls.Add(spec);
        return Task.FromResult(TestConnectionResultToReturn);
    }

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

    public bool ThrowOnSaveFrequencyPresets { get; set; }

    public Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default)
    {
        if (ThrowOnSaveFrequencyPresets)
        {
            throw new InvalidOperationException("Simulated settings-store failure.");
        }

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

internal sealed class FakeImageSourceWriter : IImageSourceWriter
{
    public List<(IImageSource Source, string Path)> Calls { get; } = [];

    public Task WritePngAsync(IImageSource source, string path, CancellationToken ct = default)
    {
        Calls.Add((source, path));
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ITemplateStore"/> -- no real filesystem I/O, matching this
/// project's own hand-rolled `Fake*` test-double convention (no mocking library anywhere in this
/// codebase). <see cref="GetAssetPath"/> returns a deterministic fake path string; nothing ever
/// reads it back as a real file since <see cref="FakeImageSourceWriter"/>/<see cref="FakeImageFileLoader"/>
/// don't touch disk either.</summary>
internal sealed class FakeTemplateStore : ITemplateStore
{
    private readonly Dictionary<string, (string Name, DateTimeOffset SavedAt, PersistedTemplateDocument Document)> _templates = [];

    public List<string> DeletedIds { get; } = [];

    public Exception? DeleteExceptionToThrow { get; set; }

    public string CreateTemplateId(string name) => $"{name}_{Guid.NewGuid():N}";

    public string GetAssetPath(string templateId, string assetFileName) => $"/fake/templates/{templateId}/assets/{assetFileName}";

    public Task SaveAsync(string templateId, string name, PersistedTemplateDocument document, CancellationToken ct = default)
    {
        _templates[templateId] = (name, DateTimeOffset.Now, document);
        return Task.CompletedTask;
    }

    public Task<PersistedTemplateDocument> LoadAsync(string templateId, CancellationToken ct = default)
        => Task.FromResult(_templates[templateId].Document);

    public Task<IReadOnlyList<TemplateMetadata>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TemplateMetadata>>(
            _templates.Select(kv => new TemplateMetadata(kv.Key, kv.Value.Name, kv.Value.SavedAt, $"/fake/templates/{kv.Key}/thumbnail.png")).ToList());

    public Task DeleteAsync(string templateId, CancellationToken ct = default)
    {
        if (DeleteExceptionToThrow is { } ex)
        {
            throw ex;
        }

        _templates.Remove(templateId);
        DeletedIds.Add(templateId);
        return Task.CompletedTask;
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

    // Defaults to null (not a fake path like PathToReturn) -- the real-world default state is
    // "usually nothing image-shaped on the clipboard," and this default keeps every OTHER test that
    // doesn't care about clipboard paste from accidentally exercising it as a side effect.
    public string? ClipboardPathToReturn { get; set; }

    public string? AdifPathToReturn { get; set; } = "/tmp/fake.adi";

    public string? SaveAdifPathToReturn { get; set; } = "/tmp/fake.adi";

    public (string Path, ImageExportFormat Format)? SaveImagePathToReturn { get; set; } = ("/tmp/fake-export.png", ImageExportFormat.Png);

    public string? LastSuggestedFileName { get; private set; }

    public string? LastSuggestedImageFileName { get; private set; }

    public Exception? ThrowOnPickSaveImageFile { get; set; }

    public Task<string?> PickImageFileAsync() => Task.FromResult(PathToReturn);

    public Task<string?> PickClipboardImageAsync() => Task.FromResult(ClipboardPathToReturn);

    public Task<string?> PickAdifFileAsync() => Task.FromResult(AdifPathToReturn);

    public Task<string?> PickSaveAdifFileAsync(string suggestedFileName)
    {
        LastSuggestedFileName = suggestedFileName;
        return Task.FromResult(SaveAdifPathToReturn);
    }

    public Task<(string Path, ImageExportFormat Format)?> PickSaveImageFileAsync(string suggestedFileName)
    {
        if (ThrowOnPickSaveImageFile is not null)
        {
            throw ThrowOnPickSaveImageFile;
        }

        LastSuggestedImageFileName = suggestedFileName;
        return Task.FromResult(SaveImagePathToReturn);
    }
}

internal sealed class FakeReceivedFrameExporter : IReceivedFrameExporter
{
    public List<(string SourcePath, string DestinationPath, int JpegQuality)> Calls { get; } = [];

    public Exception? ExceptionToThrow { get; set; }

    public Task ExportAsync(string sourcePath, string destinationPath, int jpegQuality, CancellationToken ct = default)
    {
        if (ExceptionToThrow is { } ex)
        {
            throw ex;
        }

        Calls.Add((sourcePath, destinationPath, jpegQuality));
        return Task.CompletedTask;
    }
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

    public int ApplyAdjustmentsCallCount { get; private set; }

    public List<IImageSource> CropSources { get; } = [];

    public List<(int Width, int Height, bool PreserveAspect)> ResizeCalls { get; } = [];

    public List<IImageSource> ResizeResults { get; } = [];

    public List<ImageOverlay> Overlays { get; } = [];

    public List<IImageSource> OverlaySources { get; } = [];

    public List<ImageAdjustments> Adjustments { get; } = [];

    public List<IImageSource> AdjustmentsSources { get; } = [];

    public List<IImageSource> AdjustmentsResults { get; } = [];

    public int RotateCallCount { get; private set; }

    public List<IImageSource> RotateSources { get; } = [];

    public int ApplyTemplateCallCount { get; private set; }

    public List<TemplateDocument> TemplateDocuments { get; } = [];

    public List<IImageSource> ApplyTemplateSources { get; } = [];

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
        var result = new ArrayImageSource(width, height, new Rgb24[width * height]);
        ResizeResults.Add(result);
        return result;
    }

    /// <summary>Unlike Crop/ApplyOverlay's identity-return, this returns a genuinely new instance
    /// (code-review finding) -- an identity pass-through here would let a future Resize-&gt;
    /// ApplyOverlay-&gt;ApplyAdjustments reordering bug pass every existing test silently, since
    /// nothing would distinguish "ran between Resize and ApplyOverlay" from "never ran at all."
    /// Same dimensions as the input (adjustments don't resize), same reasoning as
    /// <see cref="Rotate"/>'s own dimension-swapped return.</summary>
    public IImageSource ApplyAdjustments(IImageSource source, ImageAdjustments adjustments)
    {
        ApplyAdjustmentsCallCount++;
        Adjustments.Add(adjustments);
        AdjustmentsSources.Add(source);
        var result = new ArrayImageSource(source.Width, source.Height, new Rgb24[source.Width * source.Height]);
        AdjustmentsResults.Add(result);
        return result;
    }

    public IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay)
    {
        ApplyOverlayCallCount++;
        Overlays.Add(overlay);
        OverlaySources.Add(source);
        return source;
    }

    /// <summary>Unlike Crop/ApplyOverlay's identity-return, this returns a genuinely new,
    /// dimension-swapped <see cref="ArrayImageSource"/> -- VM-level rotate tests depend on
    /// WorkingCopyWidth/Height actually swapping, same reasoning as <see cref="Resize"/>
    /// above.</summary>
    public IImageSource Rotate(IImageSource source)
    {
        RotateCallCount++;
        RotateSources.Add(source);
        return new ArrayImageSource(source.Height, source.Width, new Rgb24[source.Width * source.Height]);
    }

    /// <summary>Identity-return for an empty document (matches the real implementation's own
    /// no-op convention), genuinely-new instance otherwise -- same "distinguish ran vs. didn't
    /// run" reasoning as <see cref="ApplyAdjustments"/> above.</summary>
    public IImageSource ApplyTemplate(IImageSource existingBase, TemplateDocument document)
    {
        ApplyTemplateCallCount++;
        TemplateDocuments.Add(document);
        ApplyTemplateSources.Add(existingBase);

        if (document.IsEmpty)
        {
            return existingBase;
        }

        return new ArrayImageSource(existingBase.Width, existingBase.Height, new Rgb24[existingBase.Width * existingBase.Height]);
    }

    public double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0)
        => font.Size * imageHeightPx;

    // Phase 4: two plain names, no real font loading -- this fake never touches SixLabors.Fonts, so
    // there's nothing to resolve; the VM/AXAML layer only needs a real, non-empty list to populate
    // the font-family picker's ItemsSource against.
    public IReadOnlyList<string> AvailableFontFamilies { get; } = ["DejaVu Sans Mono", "Barlow"];
}

internal sealed class FakeReceiveHistoryStore : IReceiveHistoryStore
{
    public List<ReceiveHistoryEntry> EntriesToReturn { get; set; } = [];

    public IImageSource? ThumbnailToReturn { get; set; }

    public List<ReceiveHistoryEntry> RecordedEntries { get; } = [];

    public List<ReceiveHistoryFilter> QueryFilters { get; } = [];

    /// <summary>Tracks every <see cref="LoadThumbnailAsync"/> call's (entry, requested dimension) --
    /// lets a test prove a preview was (or, for the flicker-fix regression, was NOT) re-decoded.</summary>
    public List<(string EntryId, int MaxDimension)> ThumbnailLoadCalls { get; } = [];

    public string ImagesDirectory { get; set; } = "/tmp/scanlinestudio-history";

    public event Action<ReceiveHistoryEntry>? Recorded;

    public Exception? ThrowOnQuery { get; set; }

    /// <summary>Actually applies the filter (unlike a bare stub) so a test can verify the
    /// Gallery tab's All/Today wiring, not just that some entries render.</summary>
    public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default)
    {
        if (ThrowOnQuery is not null)
        {
            throw ThrowOnQuery;
        }

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

        // Matches SqliteReceiveHistoryStore.QueryAsync's own real "ORDER BY ReceivedAt DESC" -- added
        // for SelectLatestCommand's own test coverage (batch 7), which relies on Entries already
        // being newest-first, same as production.
        return Task.FromResult<IReadOnlyList<ReceiveHistoryEntry>>(results.OrderByDescending(e => e.ReceivedAt).ToList());
    }

    public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
    {
        ThumbnailLoadCalls.Add((entry.Id, maxDimension));
        return Task.FromResult(ThumbnailToReturn ?? throw new InvalidOperationException("No thumbnail configured."));
    }

    public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        RecordedEntries.Add(entry);
        Recorded?.Invoke(entry);
        return Task.CompletedTask;
    }

    /// <summary>Test-only: raises <see cref="Recorded"/> WITHOUT going through <see cref="RecordAsync"/>
    /// -- lets a test simulate "another component recorded something" independently of this fake's
    /// own <see cref="RecordedEntries"/> tracking.</summary>
    public void RaiseRecorded(ReceiveHistoryEntry entry) => Recorded?.Invoke(entry);

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => Task.FromResult(ImagesDirectory);

    /// <summary>Every <see cref="SetNoteAsync"/> call, in order -- lets a test prove a selection
    /// change alone did NOT fire a spurious persist (records compare by value, so asserting against
    /// <see cref="EntriesToReturn"/>'s own content can't distinguish "never called" from "called
    /// with the same value it already had").</summary>
    public List<(string EntryId, string? Note)> SetNoteCalls { get; } = [];

    public List<(string EntryId, bool IsFlagged)> SetFlaggedCalls { get; } = [];

    /// <summary>Queue of per-call delays for <see cref="SetFlaggedAsync"/>, consumed FIFO (one entry
    /// per call, falling back to no delay once exhausted) -- lets a test make an EARLIER call finish
    /// AFTER a later one, the only way to actually exercise an ordering guard. Without this, every
    /// call completes synchronously via <see cref="Task.FromResult{TResult}"/>, which trivially
    /// preserves call order regardless of whether the caller's own serialization logic is even
    /// present -- a test asserting final state alone can't distinguish "correctly serialized" from
    /// "never raced in the first place".</summary>
    public Queue<TimeSpan> SetFlaggedCallDelays { get; } = [];

    /// <summary>Actually mutates <see cref="EntriesToReturn"/> (matching <see cref="QueryAsync"/>'s
    /// own "actually applies" convention above), so a Gallery-side test can verify a note/flag/
    /// QSO-link edit round-trips through a subsequent query, not just that the call was made.</summary>
    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default)
    {
        SetNoteCalls.Add((entryId, note));
        return Task.FromResult(TryUpdateEntry(entryId, e => e with { Note = note }));
    }

    public async Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default)
    {
        SetFlaggedCalls.Add((entryId, isFlagged));
        if (SetFlaggedCallDelays.TryDequeue(out var delay))
        {
            await Task.Delay(delay, ct);
        }

        return TryUpdateEntry(entryId, e => e with { IsFlagged = isFlagged });
    }

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

internal sealed class FakeLogbookSessionService : ILogbookSessionService
{
    public List<QsoRecord> Records { get; } = [];

    public LogQsoResult? LogResultToReturn { get; set; }

    public Exception? ThrowOnLog { get; set; }

    public Exception? ThrowOnSearch { get; set; }

    public Exception? ThrowOnUpdate { get; set; }

    public Exception? ThrowOnImport { get; set; }

    public Exception? ThrowOnExport { get; set; }

    public int UpdateCallCount { get; private set; }

    public string? LastExportPath { get; private set; }

    public LogbookQuery? LastSearchQuery { get; private set; }

    public IReadOnlyList<QsoRecord> ImportResultToReturn { get; set; } = [];

    /// <summary>When set, <see cref="LogQsoAsync"/> suspends on this instead of completing
    /// immediately -- a dedicated gate for this one operation (this project's own "deterministic
    /// gates, not a shared race" convention), letting a test simulate the UI thread doing something
    /// else (e.g. selecting a different row) while a real QRZ-upload-shaped await is still pending.</summary>
    public TaskCompletionSource<LogQsoResult>? LogGate { get; set; }

    public Task<LogQsoResult> LogQsoAsync(QsoRecord record, CancellationToken ct = default)
    {
        if (ThrowOnLog is not null)
        {
            throw ThrowOnLog;
        }

        Records.Add(record);
        if (LogGate is not null)
        {
            return LogGate.Task;
        }

        return Task.FromResult(LogResultToReturn ?? new LogQsoResult(record, 0, 0, false, null));
    }

    public Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default)
    {
        LastSearchQuery = query;
        if (ThrowOnSearch is not null)
        {
            throw ThrowOnSearch;
        }

        return Task.FromResult<IReadOnlyList<QsoRecord>>(Records);
    }

    public Task UpdateQsoAsync(QsoRecord record, CancellationToken ct = default)
    {
        UpdateCallCount++;
        if (ThrowOnUpdate is not null)
        {
            throw ThrowOnUpdate;
        }

        var index = Records.FindIndex(r => r.Id == record.Id);
        if (index >= 0)
        {
            Records[index] = record;
        }

        return Task.CompletedTask;
    }

    public LogbookQuery? LastExportQuery { get; private set; }

    public Task ExportAdifFileAsync(string filePath, LogbookQuery query, CancellationToken ct = default)
    {
        LastExportPath = filePath;
        LastExportQuery = query;
        if (ThrowOnExport is not null)
        {
            throw ThrowOnExport;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QsoRecord>> ImportAdifFileAsync(string filePath, CancellationToken ct = default)
    {
        if (ThrowOnImport is not null)
        {
            throw ThrowOnImport;
        }

        Records.AddRange(ImportResultToReturn);
        return Task.FromResult(ImportResultToReturn);
    }

    public QrzCallsignLookupResult LookupResultToReturn { get; set; } = new(true, "Test Name", "Test QTH", "AA00", null);

    public Exception? ThrowOnLookup { get; set; }

    public string? LastLookupCallsign { get; private set; }

    public int LookupCallCount { get; private set; }

    public Task<QrzCallsignLookupResult> LookupCallsignAsync(string callsign, CancellationToken ct = default)
    {
        LookupCallCount++;
        LastLookupCallsign = callsign;
        if (ThrowOnLookup is not null)
        {
            throw ThrowOnLookup;
        }

        return Task.FromResult(LookupResultToReturn);
    }

    public QrzLoginResult TestQrzLookupResultToReturn { get; set; } = new(true, null);

    public Exception? ThrowOnTestQrzLookup { get; set; }

    public string? LastTestUsername { get; private set; }

    public string? LastTestPassword { get; private set; }

    public int TestQrzLookupCallCount { get; private set; }

    public Task<QrzLoginResult> TestQrzLookupCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        TestQrzLookupCallCount++;
        LastTestUsername = username;
        LastTestPassword = password;
        if (ThrowOnTestQrzLookup is not null)
        {
            throw ThrowOnTestQrzLookup;
        }

        return Task.FromResult(TestQrzLookupResultToReturn);
    }
}

/// <summary>Captures log entries for assertion, matching the hand-rolled `Fake*` test-double
/// convention already established for this project (no mocking library is used anywhere in this
/// codebase) -- same shape as `ScanlineStudio.Core.Localization.Tests.FakeLogger&lt;T&gt;`.</summary>
internal sealed class FakeLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
