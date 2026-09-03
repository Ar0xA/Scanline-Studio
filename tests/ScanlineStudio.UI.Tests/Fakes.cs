using System.Linq;
using System.Reactive.Subjects;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
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

    /// <summary>When set, <see cref="LoadAsync"/> parks on this until it completes -- lets a test
    /// deterministically observe VM state BEFORE an async settings load resolves (a real,
    /// controllable gate, not a race on task-scheduling order -- same convention as
    /// ScanlineStudio.Application.Tests' own <c>FakeSettingsStore.Gate</c>).</summary>
    public Task? Gate { get; set; }

    public IObservable<AppSettings> Changes => _changes;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (Gate is not null)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
        }

        if (LoadAsyncException is { } ex)
        {
            throw ex;
        }

        return Settings;
    }

    /// <summary>When set, <see cref="SaveAsync"/> throws this instead of persisting -- lets a test
    /// simulate a read-only/locked settings file (ui_transition_plan.md step 7's own "surface a
    /// visible save-failure message" requirement), same convention as <see cref="LoadAsyncException"/>.</summary>
    public Exception? SaveAsyncException { get; set; }

    /// <summary>When set, <see cref="SaveAsync"/> parks on this until it completes -- lets a test
    /// deterministically observe VM state WHILE an async save is still in flight (e.g. that a busy
    /// flag is set), a real controllable gate rather than a sleep-based race. Same convention as
    /// <see cref="Gate"/> for <see cref="LoadAsync"/>.</summary>
    public Task? SaveGate { get; set; }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (SaveGate is not null)
        {
            await SaveGate.WaitAsync(ct).ConfigureAwait(false);
        }

        if (SaveAsyncException is { } ex)
        {
            throw ex;
        }

        Settings = settings;
        _changes.OnNext(settings);
    }

    /// <summary>T0-2: naive forwarding is sufficient here -- this fake has no test that depends on
    /// genuine cross-caller mutual exclusion (that's <c>Application.Tests.FakeSettingsStore</c>'s
    /// job). Still funnels through this class's own <see cref="LoadAsync"/>/<see cref="SaveAsync"/>,
    /// so <see cref="LoadAsyncException"/>/<see cref="SaveAsyncException"/>/<see cref="Gate"/>/
    /// <see cref="SaveGate"/> all still apply.</summary>
    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        var current = await LoadAsync(ct).ConfigureAwait(false);
        var updated = mutate(current);
        await SaveAsync(updated, ct).ConfigureAwait(false);
        return updated;
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

/// <summary>Configurations-preset backlog, Phase 4 (2026-08-28). In-memory, not the real filesystem-
/// backed <c>ConfigurationPresetStore</c> -- matches this test project's own established "fake
/// everything, no real I/O" convention (unlike <c>ScanlineStudio.Application.Tests</c>' own
/// <c>ConfigurationPresetServiceTests</c>, which deliberately DOES use the real store against a temp
/// directory). Mirrors the real store's own name/collision semantics closely enough for a UI-layer VM
/// test (case-insensitive names, Clone/Rename collision-reject, Save silently overwrites, Delete is a
/// no-op if absent) -- NOT a re-verification of the store's own contract, which
/// `ScanlineStudio.Settings.Tests` already owns.</summary>
internal sealed class FakeConfigurationPresetStore : IConfigurationPresetStore
{
    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private readonly Dictionary<string, AppSettings> _presets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phase 4b (cascading-menu redesign) test probe: proves a genuine
    /// <c>ConfigurationsManagerWindowViewModel.RefreshAsync</c> ran, now that the earlier arm-state
    /// flags (<c>IsSaveAsNewOverwriteArmed</c>/<c>_pendingDeleteName</c>) it used to be observed
    /// through are both gone. <c>RefreshAsync</c> calls <see cref="ListPresetsAsync"/> TWICE on its
    /// Default-seed path (once to check, once again after seeding) -- assert this as a DELTA
    /// (before/after), never an absolute value.</summary>
    public int ListPresetsCallCount { get; private set; }

    public bool TryValidatePresetName(string name, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Name cannot be empty.";
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            errorMessage = "Name cannot contain '..'.";
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || InvalidNameChars.Contains(c))
            {
                errorMessage = $"Name cannot contain '{c}'.";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    public Task<IReadOnlyList<string>> ListPresetsAsync(CancellationToken ct = default)
    {
        ListPresetsCallCount++;
        return Task.FromResult<IReadOnlyList<string>>(_presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<AppSettings?> LoadPresetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_presets.TryGetValue(name, out var settings) ? settings : null);

    public Task SavePresetAsync(string name, AppSettings content, CancellationToken ct = default)
    {
        if (!TryValidatePresetName(name, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }

        _presets[name] = content;
        return Task.CompletedTask;
    }

    public Task ClonePresetAsync(string sourceName, string newName, CancellationToken ct = default)
    {
        if (!_presets.TryGetValue(sourceName, out var source))
        {
            throw new InvalidOperationException($"No preset named '{sourceName}' exists.");
        }

        if (!TryValidatePresetName(newName, out var error))
        {
            throw new ArgumentException(error, nameof(newName));
        }

        if (_presets.ContainsKey(newName))
        {
            throw new InvalidOperationException($"A preset named '{newName}' already exists.");
        }

        _presets[newName] = source;
        return Task.CompletedTask;
    }

    public Task DeletePresetAsync(string name, CancellationToken ct = default)
    {
        _presets.Remove(name);
        return Task.CompletedTask;
    }

    public Task RenamePresetAsync(string oldName, string newName, CancellationToken ct = default)
    {
        if (!_presets.TryGetValue(oldName, out var content))
        {
            throw new InvalidOperationException($"No preset named '{oldName}' exists.");
        }

        if (!TryValidatePresetName(newName, out var error))
        {
            throw new ArgumentException(error, nameof(newName));
        }

        // Round-1 code-review fix: a CASE-ONLY rename ("field day" -> "Field Day") must be allowed --
        // the real ConfigurationPresetStore explicitly permits this (its own collision check compares
        // the colliding PATH against the source's own path, not just the name), so a naive
        // ContainsKey(newName) here (true for the source's own entry too, since _presets is
        // OrdinalIgnoreCase-keyed) would silently diverge from production behavior and make a test
        // written against this fake enshrine the OPPOSITE of what the real store actually does.
        if (_presets.ContainsKey(newName) && !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"A preset named '{newName}' already exists.");
        }

        _presets.Remove(oldName);
        _presets[newName] = content;
        return Task.CompletedTask;
    }

    /// <summary>Test-only seeding helper -- bypasses <see cref="SavePresetAsync"/>'s own validation so
    /// a test can pre-populate the store without depending on that validation passing.</summary>
    public void Seed(string name, AppSettings? content = null) => _presets[name] = content ?? new AppSettings();
}

/// <summary>Configurations-preset backlog, Phase 4 (2026-08-28). Deliberately a FAKE, not a real
/// <c>ConfigurationPresetService</c> -- that concrete type needs a real <c>ISstvSessionService</c>/
/// <c>IRadioSessionService</c> and touches live audio/radio state, far too heavy for a UI-layer VM
/// test whose only job is to verify it correctly interprets whatever
/// <see cref="ConfigurationPresetSwitchResult"/> comes back.</summary>
internal sealed class FakeConfigurationPresetService : IConfigurationPresetService
{
    public ConfigurationPresetSwitchResult ResultToReturn { get; set; } =
        new(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);

    public Exception? ThrowOnSwitch { get; set; }

    public string? LastRequestedName { get; private set; }

    public int SwitchCallCount { get; private set; }

    public Task<ConfigurationPresetSwitchResult> SwitchToPresetAsync(string name, CancellationToken ct = default)
    {
        LastRequestedName = name;
        SwitchCallCount++;
        return ThrowOnSwitch is { } ex ? Task.FromException<ConfigurationPresetSwitchResult>(ex) : Task.FromResult(ResultToReturn);
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

    public double LeaderToneDurationMsToReturn { get; set; }

    public double GetLeaderToneDurationMs(SstvModeDefinition mode) => LeaderToneDurationMsToReturn;

    public (VisHeaderKind Kind, int Value) VisHeaderInfoToReturn { get; set; } = (VisHeaderKind.Standard, 0);

    public (VisHeaderKind Kind, int Value) GetVisHeaderInfo(SstvModeDefinition mode) => VisHeaderInfoToReturn;

    public bool IsReceiving { get; set; }

    public bool IsTransmitting { get; set; }

    public bool IsRecording { get; set; }

    public bool IsAudioAutoSaveActive { get; set; }

    public bool IsAutoDetectPaused { get; set; }

    public int SetAutoDetectPausedCallCount { get; private set; }

    public void SetAutoDetectPaused(bool paused)
    {
        SetAutoDetectPausedCallCount++;
        IsAutoDetectPaused = paused;
    }

    public bool ThrowOnStartReceiving { get; set; }

    public bool ThrowOnStopReceiving { get; set; }

    /// <summary>fsk_cwid.md §A5: settable directly (unlike the real implementation's own
    /// <c>_decoder.ReceptionSequence</c> pass-through) so a test can simulate "a ModeDetected fired,
    /// bumping the reception identity" independently of <see cref="RaiseModeDetected"/> itself, the
    /// same shape <see cref="FakeReceivedImageBuffer.Generation"/> gave the guard this property
    /// replaces.</summary>
    public long CurrentReceptionSequence { get; set; }

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    public event Action<CwIdDecodedInfo>? CwIdDecoded;

    public event Action<TransmitProgressInfo>? TransmitProgressChanged;

    public event Action<bool>? CapturePausedForTransmitChanged;

    public string? OperatorCallsign { get; set; }

    /// <summary>When set, <see cref="GetOperatorCallsignAsync"/> awaits this instead of completing
    /// synchronously -- lets a test hold the call mid-flight (matching the real, uncached settings
    /// disk read it stands in for) to reproduce a generation-changes-while-awaiting race.</summary>
    public TaskCompletionSource<string?>? OperatorCallsignGate { get; set; }

    public Task<string?> GetOperatorCallsignAsync(CancellationToken ct = default) =>
        OperatorCallsignGate?.Task ?? Task.FromResult(OperatorCallsign);

    public string? OperatorGrid { get; set; }

    public Task<string?> GetOperatorGridAsync(CancellationToken ct = default) => Task.FromResult(OperatorGrid);

    public StationIdTransmitOptions StationIdTransmitOptionsToReturn { get; set; } = StationIdTransmitOptions.None;

    public Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default) => Task.FromResult(StationIdTransmitOptionsToReturn);

    /// <summary>Keyed by the exact path passed to <see cref="ValidateStationIdSoundFileAsync"/> --
    /// lets a test give different paths different outcomes in the same run. A path with no entry
    /// returns <see cref="SoundFileIdValidationResult.Fail"/>(<see cref="SoundFileIdValidationFailure.FileNotFound"/>),
    /// matching the real implementation's own behavior for a path that genuinely doesn't exist.</summary>
    public Dictionary<string, SoundFileIdValidationResult> SoundFileValidationResults { get; } = [];

    public List<string> ValidateStationIdSoundFileCalls { get; } = [];

    public Task<SoundFileIdValidationResult> ValidateStationIdSoundFileAsync(string path, CancellationToken ct = default)
    {
        ValidateStationIdSoundFileCalls.Add(path);
        return Task.FromResult(SoundFileValidationResults.TryGetValue(path, out var result)
            ? result
            : SoundFileIdValidationResult.Fail(SoundFileIdValidationFailure.FileNotFound));
    }

    public event Action? MaintenanceWarningRaised;

    public event Action? MaintenanceWarningCleared;

    public event Action? MaintenanceCriticalStopRaised;

    public event Action? DecoderInstanceReplaced;

    public event Action? ReconfigurationRejected;

    public int RequestReSyncCallCount { get; private set; }

    public void RequestReSync() => RequestReSyncCallCount++;

    public int RequestNotchCallCount { get; private set; }

    public bool LastNotchEnabled { get; private set; }

    public double? LastNotchFrequencyHz { get; private set; }

    public void RequestNotch(bool enabled, double? frequencyHz)
    {
        RequestNotchCallCount++;
        LastNotchEnabled = enabled;
        LastNotchFrequencyHz = frequencyHz;
    }

    public int RequestPllTuningCallCount { get; private set; }

    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz)
    {
        RequestPllTuningCallCount++;
    }

    public int RequestZeroCrossingTuningCallCount { get; private set; }

    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz)
    {
        RequestZeroCrossingTuningCallCount++;
    }

    public int ArmScopeCaptureCallCount { get; private set; }

    public int? LastScopeCaptureSize { get; private set; }

    public void ArmScopeCapture(int size)
    {
        ArmScopeCaptureCallCount++;
        LastScopeCaptureSize = size;
    }

    public double[]? ScopeCaptureChannel0ToReturn { get; set; }

    public double[]? TryGetScopeCaptureChannel0() => ScopeCaptureChannel0ToReturn;

    public int RequestSenseLevelCallCount { get; private set; }

    public int? LastRequestedSenseLevel { get; private set; }

    public void RequestSenseLevel(int level)
    {
        RequestSenseLevelCallCount++;
        LastRequestedSenseLevel = level;
        SenseLevel = level;
    }

    public int RequestAutoSyncEnabledCallCount { get; private set; }

    public bool? LastRequestedAutoSyncEnabled { get; private set; }

    public void RequestAutoSyncEnabled(bool enabled)
    {
        RequestAutoSyncEnabledCallCount++;
        LastRequestedAutoSyncEnabled = enabled;
    }

    public int RequestAutoStopEnabledCallCount { get; private set; }

    public bool? LastRequestedAutoStopEnabled { get; private set; }

    public void RequestAutoStopEnabled(bool enabled)
    {
        RequestAutoStopEnabledCallCount++;
        LastRequestedAutoStopEnabled = enabled;
    }

    public int RequestAutoSlantEnabledCallCount { get; private set; }

    public bool? LastRequestedAutoSlantEnabled { get; private set; }

    public void RequestAutoSlantEnabled(bool enabled)
    {
        RequestAutoSlantEnabledCallCount++;
        LastRequestedAutoSlantEnabled = enabled;
        AutoSlantEnabled = enabled;
    }

    public int RequestSyncRestartEnabledCallCount { get; private set; }

    public bool? LastRequestedSyncRestartEnabled { get; private set; }

    public void RequestSyncRestartEnabled(bool enabled)
    {
        RequestSyncRestartEnabledCallCount++;
        LastRequestedSyncRestartEnabled = enabled;
    }

    public int RequestReconfigurationCallCount { get; private set; }

    public (RxBpfPreset RxBpfPreset, DemodType DemodType, RxBufferMode RxBufferMode)? LastRequestedReconfiguration { get; private set; }

    public void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode)
    {
        RequestReconfigurationCallCount++;
        LastRequestedReconfiguration = (rxBpfPreset, demodType, rxBufferMode);
    }

    // Restart-required-settings backlog item 6 (2026-08-28): deliberately does NOT update the
    // settable RxBpfPreset property below -- same idle-gated shape RequestReconfiguration's own fake
    // implementation above already has (a request doesn't immediately change what's "applied"; a
    // test drives RxBpfPreset directly to simulate a swap actually landing, or calls
    // RaiseReconfigurationRejected to simulate one being dropped).
    public int RequestRxBpfPresetCallCount { get; private set; }

    public RxBpfPreset? LastRequestedRxBpfPreset { get; private set; }

    public void RequestRxBpfPreset(RxBpfPreset preset)
    {
        RequestRxBpfPresetCallCount++;
        LastRequestedRxBpfPreset = preset;
    }

    public int RequestSampleRateCallCount { get; private set; }

    public int? LastRequestedSampleRate { get; private set; }

    public SampleRateApplyResult SampleRateApplyResultToReturn { get; set; } = SampleRateApplyResult.Applied;

    public Task<SampleRateApplyResult> RequestSampleRateAsync(int sampleRate, CancellationToken ct = default)
    {
        RequestSampleRateCallCount++;
        LastRequestedSampleRate = sampleRate;
        return Task.FromResult(SampleRateApplyResultToReturn);
    }

    public int RequestCaptureDeviceCallCount { get; private set; }

    public (string? DeviceId, string? DeviceName)? LastRequestedCaptureDevice { get; private set; }

    public CaptureDeviceApplyResult CaptureDeviceApplyResultToReturn { get; set; } = CaptureDeviceApplyResult.Applied;

    /// <summary>When set, <see cref="RequestCaptureDeviceAsync"/> throws this instead of returning
    /// -- same shape as <see cref="PersistSenseLevelException"/> below, for a test to simulate a real
    /// failure (e.g. RequestCaptureDeviceAsync's own real _rxTransitionGate-timeout TimeoutException).</summary>
    public Exception? RequestCaptureDeviceException { get; set; }

    public Task<CaptureDeviceApplyResult> RequestCaptureDeviceAsync(string? deviceId, string? deviceName, CancellationToken ct = default)
    {
        RequestCaptureDeviceCallCount++;
        LastRequestedCaptureDevice = (deviceId, deviceName);
        return RequestCaptureDeviceException is { } ex ? Task.FromException<CaptureDeviceApplyResult>(ex) : Task.FromResult(CaptureDeviceApplyResultToReturn);
    }

    public int PersistSenseLevelCallCount { get; private set; }

    public int? LastPersistedSenseLevel { get; private set; }

    public Exception? PersistSenseLevelException { get; set; }

    public Task PersistSenseLevelAsync(int level, CancellationToken ct = default)
    {
        if (PersistSenseLevelException is { } ex)
        {
            throw ex;
        }

        PersistSenseLevelCallCount++;
        LastPersistedSenseLevel = level;
        return Task.CompletedTask;
    }

    public int PersistRxBpfPresetCallCount { get; private set; }

    public RxBpfPreset? LastPersistedRxBpfPreset { get; private set; }

    public Exception? PersistRxBpfPresetException { get; set; }

    public Task PersistRxBpfPresetAsync(RxBpfPreset preset, CancellationToken ct = default)
    {
        if (PersistRxBpfPresetException is { } ex)
        {
            throw ex;
        }

        PersistRxBpfPresetCallCount++;
        LastPersistedRxBpfPreset = preset;
        return Task.CompletedTask;
    }

    public double[]? ScopeCaptureChannel1ToReturn { get; set; }

    public double[]? TryGetScopeCaptureChannel1() => ScopeCaptureChannel1ToReturn;

    public int RequestCorrectSlantCallCount { get; private set; }

    public void RequestCorrectSlant() => RequestCorrectSlantCallCount++;

    public int AbortReceptionCallCount { get; private set; }

    public void AbortReception() => AbortReceptionCallCount++;

    public int ForceModeCallCount { get; private set; }

    public SstvModeDefinition? LastForcedMode { get; private set; }

    public void ForceMode(SstvModeDefinition mode)
    {
        ForceModeCallCount++;
        LastForcedMode = mode;
    }

    public int SetModeLockCallCount { get; private set; }

    public SstvModeDefinition? LastLockedMode { get; private set; }

    public void SetModeLock(SstvModeDefinition? mode)
    {
        SetModeLockCallCount++;
        LastLockedMode = mode;
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

    public List<string> StartRecordingCalls { get; } = [];

    public int StopRecordingCallCount { get; private set; }

    public bool IsRecordingForTests { get; private set; }

    public Exception? ThrowOnStartRecording { get; set; }

    public Exception? ThrowOnStopRecording { get; set; }

    public Task StartRecordingAsync(string path)
    {
        if (ThrowOnStartRecording is { } ex)
        {
            throw ex;
        }

        StartRecordingCalls.Add(path);
        IsRecordingForTests = true;
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync()
    {
        StopRecordingCallCount++;
        IsRecordingForTests = false;
        if (ThrowOnStopRecording is { } ex)
        {
            throw ex;
        }

        return Task.CompletedTask;
    }

    public event Action<long, int>? AudioSliceReady;

    public event Action? AudioCaptureReset;

    public void RaiseAudioSliceReady(long receptionId, int sampleRate) => AudioSliceReady?.Invoke(receptionId, sampleRate);

    public void RaiseAudioCaptureReset() => AudioCaptureReset?.Invoke();

    public List<(long ReceptionId, string Path)> TrySaveReceptionAudioCalls { get; } = [];

    public bool TrySaveReceptionAudioResult { get; set; } = true;

    public Task<bool> TrySaveReceptionAudioAsync(long receptionId, string path)
    {
        TrySaveReceptionAudioCalls.Add((receptionId, path));
        return Task.FromResult(TrySaveReceptionAudioResult);
    }

    public List<bool> SetAutoSaveAudioEnabledCalls { get; } = [];

    public void SetAutoSaveAudioEnabled(bool enabled) => SetAutoSaveAudioEnabledCalls.Add(enabled);

    public List<string?> SetAudioDirectoryCalls { get; } = [];

    public void SetAudioDirectory(string? directory) => SetAudioDirectoryCalls.Add(directory);

    public List<(SstvModeDefinition Mode, IImageSource Image)> RunLoopbackSelfTestCalls { get; } = [];

    public LoopbackSelfTestResult RunLoopbackSelfTestResult { get; set; } =
        new(new ArrayImageSource(1, 1, [new Rgb24(0, 0, 0)]), null, LoopbackSelfTestOutcome.Completed);

    public Exception? ThrowOnRunLoopbackSelfTest { get; set; }

    /// <summary>Code-review round-1 coverage gap: when set, <see cref="RunLoopbackSelfTestAsync"/>
    /// doesn't return until this completes -- lets a test hold a call open long enough to assert the
    /// mutual-exclusion CanExecute cross-wiring against <c>TransmitCommand</c> (same shape as this
    /// class's own <see cref="BlockUntilCancelled"/> for <see cref="TransmitAsync"/>).</summary>
    public TaskCompletionSource? RunLoopbackSelfTestGate { get; set; }

    public async Task<LoopbackSelfTestResult> RunLoopbackSelfTestAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        RunLoopbackSelfTestCalls.Add((mode, image));
        if (RunLoopbackSelfTestGate is { } gate)
        {
            await gate.Task;
        }

        if (ThrowOnRunLoopbackSelfTest is { } ex)
        {
            throw ex;
        }

        return RunLoopbackSelfTestResult;
    }

    public List<string> DecodeFromFileCalls { get; } = [];

    public Exception? ThrowOnDecodeFromFile { get; set; }

    public Task DecodeFromFileAsync(string path, CancellationToken ct = default)
    {
        DecodeFromFileCalls.Add(path);
        if (ThrowOnDecodeFromFile is { } ex)
        {
            throw ex;
        }

        return Task.CompletedTask;
    }

    /// <summary>When true, <see cref="TransmitAsync"/> doesn't return until <paramref name="ct"/> is
    /// cancelled (throwing <see cref="OperationCanceledException"/> then) -- lets a test simulate an
    /// in-flight transmission long enough to push several <c>RadioState</c> updates through
    /// <c>IRadioSessionService.StateChanges</c> and observe a Stop TX/SWR-cutoff actually cancel it,
    /// instead of the call completing instantly before any cutoff check could ever run.</summary>
    public bool BlockUntilCancelled { get; set; }

    /// <summary>TX history plan (2026-09-01): a general-purpose gate, set/completed directly by the
    /// test (success, cancel, OR fault) -- lets a test interleave a <see cref="RaiseTransmitProgress"/>
    /// call between TX start and completion for ANY outcome, unlike <see cref="BlockUntilCancelled"/>,
    /// which only supports the cancel case. Checked before <see cref="BlockUntilCancelled"/>; that
    /// field is untouched so existing callers keep working unchanged.</summary>
    public TaskCompletionSource? TransmitGate { get; set; }

    public Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        TransmitCalls.Add((mode, image));
        if (TransmitGate is not null)
        {
            return TransmitGate.Task;
        }

        if (BlockUntilCancelled)
        {
            var tcs = new TaskCompletionSource();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        return Task.CompletedTask;
    }

    public List<(double FrequencyHz, TimeSpan Duration, bool LeaveKeyedAfterTune)> TuneCalls { get; } = [];

    /// <summary>When set, <see cref="TuneAsync"/> suspends on this instead of completing immediately
    /// -- a dedicated gate for this one operation (this project's own "deterministic gates, not a
    /// shared race" convention), letting a test hold a tune "in flight" long enough to prove
    /// <c>RadioStatusViewModel.TuneCommand</c>'s Stop path is actually reachable through
    /// <c>CanExecute</c>, not just via a direct <c>ExecuteAsync</c> call that bypasses it.</summary>
    public TaskCompletionSource? TuneGate { get; set; }

    public async Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default)
    {
        TuneCalls.Add((frequencyHz, duration, leaveKeyedAfterTune));
        if (TuneGate is not null)
        {
            using (ct.Register(() => TuneGate.TrySetCanceled(ct)))
            {
                await TuneGate.Task;
            }
        }
    }

    public int TxVolumePercent { get; set; } = 100;

    public Task<int> GetTxVolumePercentAsync(CancellationToken ct = default) => Task.FromResult(TxVolumePercent);

    public Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        TxVolumePercent = percent;
        return Task.CompletedTask;
    }

    public bool TxIsMuted { get; set; }

    public Task<bool> GetTxDeviceMutedAsync(CancellationToken ct = default) => Task.FromResult(TxIsMuted);

    public bool IsPttLocked { get; private set; }

    public List<bool> PttLockCalls { get; } = [];

    public Task SetPttLockAsync(bool locked, CancellationToken ct = default)
    {
        PttLockCalls.Add(locked);
        IsPttLocked = locked;
        return Task.CompletedTask;
    }

    public string? ConfiguredPlaybackDeviceName { get; set; }

    public Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(ConfiguredPlaybackDeviceName);

    public string? ConfiguredCaptureDeviceName { get; set; }

    public Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(ConfiguredCaptureDeviceName);

    public double? SlantPpm { get; set; }

    public SstvSyncSource SyncSource { get; set; }

    public int? SyncOffsetSamples { get; set; }

    public double SignalPeakLevel { get; set; }

    public double RawInputPeakLevel { get; set; }

    public bool IsLevelOverdriven { get; set; }

    public bool AutoSlantEnabled { get; set; } = true;

    public int SenseLevel { get; set; } = 1;

    public RxBpfPreset RxBpfPreset { get; set; } = RxBpfPreset.Wide;

    public double? SyncFrequencyCorrectionHz { get; set; }

    public int BufferedSampleCount { get; set; }

    public int CaptureOverrunCount { get; set; }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseDecodeRestarted(SstvModeDefinition mode) => DecodeRestarted?.Invoke(mode);

    public void RaiseStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

    public void RaiseCwIdDecoded(CwIdDecodedInfo info) => CwIdDecoded?.Invoke(info);

    public void RaiseTransmitProgress(TransmitProgressInfo info) => TransmitProgressChanged?.Invoke(info);

    public void RaiseCapturePausedForTransmitChanged(bool paused) => CapturePausedForTransmitChanged?.Invoke(paused);

    public void RaiseMaintenanceWarningRaised() => MaintenanceWarningRaised?.Invoke();

    public void RaiseMaintenanceWarningCleared() => MaintenanceWarningCleared?.Invoke();

    public void RaiseMaintenanceCriticalStopRaised() => MaintenanceCriticalStopRaised?.Invoke();

    public void RaiseDecoderInstanceReplaced() => DecoderInstanceReplaced?.Invoke();

    public void RaiseReconfigurationRejected() => ReconfigurationRejected?.Invoke();
}

internal sealed class FakeRadioSessionService : IRadioSessionService, IDisposable
{
    private readonly Subject<RadioState> _stateChanges = new();
    private readonly Subject<RadioConnectionEvent> _connectionEvents = new();

    public RadioState? LastKnownState { get; set; }

    public RadioCapabilities Capabilities { get; set; } = RadioCapabilities.None;

    // ScanlineStudio.UI.Tests never constructs a real SstvSessionService (only FakeSstvSessionService),
    // so unlike the same-named fake in ScanlineStudio.Application.Tests, this value never gated
    // anything here -- present only to satisfy IRadioSessionService's interface contract. No longer
    // fully true: OptionsWindowViewModel.TestHamlibConnectionAsync/TestPttAsync now refuse to run
    // while RigId != "none" (a live connection is already active) -- tests exercising those two
    // commands must set this to "none" explicitly, since the default here stays "fake-radio" for
    // every OTHER existing test's sake.
    public string RigId { get; set; } = "fake-radio";

    public bool IsGenuinelyConnected { get; set; }

    public IObservable<RadioState> StateChanges => _stateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _connectionEvents;

    public int ConnectUsingSettingsCallCount { get; private set; }

    /// <summary>Scripts <see cref="ConnectUsingSettingsAsync"/> throwing -- <c>OptionsWindowViewModel.
    /// ToggleRadioConnectionAsync</c>'s own contract says a genuine factory-resolution failure there
    /// surfaces as an exception (a real connection/poll problem instead reaches <see cref="ConnectionEvents"/>,
    /// same as at app startup).</summary>
    public Exception? ConnectUsingSettingsExceptionToThrow { get; set; }

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default)
    {
        ConnectUsingSettingsCallCount++;
        return ConnectUsingSettingsExceptionToThrow is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }

    public RadioConnectionTestResult TestConnectionResultToReturn { get; set; } = new(true, "fake-rig", RadioCapabilities.None, null);

    public List<RadioConnectionSpec> TestConnectionCalls { get; } = [];

    // T1-11: lets tests confirm a real, bounded (CanBeCanceled == true) token reaches this call --
    // the regression the fix closes (default/CancellationToken.None has CanBeCanceled == false).
    public List<CancellationToken> TestConnectionTokens { get; } = [];

    public Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default)
    {
        TestConnectionCalls.Add(spec);
        TestConnectionTokens.Add(ct);
        return Task.FromResult(TestConnectionResultToReturn);
    }

    public RadioConnectionTestResult TestPttResultToReturn { get; set; } = new(true, "fake-rig", RadioCapabilities.PttControl, null);

    public List<(RadioConnectionSpec Spec, TimeSpan Duration)> TestPttCalls { get; } = [];

    /// <summary>Test-only hook: when set, <see cref="TestPttAsync"/> awaits this before returning --
    /// lets a test observe <c>IsTestingPtt</c> while a call is genuinely in flight, mirroring
    /// <c>FakeAudioDeviceEnumerator.Gate</c>'s shape.</summary>
    public Task? TestPttGate { get; set; }

    /// <summary>Code-review finding: set true if <paramref name="ct"/> (below) was actually observed
    /// as cancelled -- without this, a test asserting only "no second call started" can't
    /// distinguish the real Stop-cancels-the-in-flight-call fix from simply deleting the
    /// <c>_testPttCts?.Cancel()</c> call, since the re-entrancy guard alone already blocks a second
    /// call either way. Catches (rather than rethrows) the real
    /// <see cref="OperationCanceledException"/>, matching the REAL <c>RadioSessionService.TestPttAsync</c>'s
    /// own contract of never letting cancellation escape as an exception.</summary>
    public bool WasCancelled { get; private set; }

    public async Task<RadioConnectionTestResult> TestPttAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct = default)
    {
        TestPttCalls.Add((spec, duration));
        if (TestPttGate is not null)
        {
            try
            {
                await TestPttGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
            }
        }

        return TestPttResultToReturn;
    }

    public int RequestHamlibLibraryPathCallCount { get; private set; }

    public string? LastRequestedHamlibLibraryPath { get; private set; }

    public HamlibLibraryReloadResult? HamlibLibraryReloadResultToReturn { get; set; } = new(true, "/fake/libhamlib.so.4", "Hamlib 4.5.5", []);

    public Exception? HamlibLibraryReloadException { get; set; }

    public Task<HamlibLibraryReloadResult?> RequestHamlibLibraryPathAsync(string? overridePath, CancellationToken ct = default)
    {
        RequestHamlibLibraryPathCallCount++;
        LastRequestedHamlibLibraryPath = overridePath;
        if (HamlibLibraryReloadException is { } ex)
        {
            throw ex;
        }

        return Task.FromResult(HamlibLibraryReloadResultToReturn);
    }

    public int DisconnectCallCount { get; private set; }

    /// <summary>T1-8 (production_audit.md): the real RadioController.DisconnectAsync can now throw
    /// TimeoutException (its own internal lifecycle lock timed out) -- lets a test simulate that
    /// against OptionsWindowViewModel's Disconnect branch without a real wedged lock.</summary>
    public Exception? DisconnectException { get; set; }

    public Task DisconnectAsync()
    {
        DisconnectCallCount++;
        if (DisconnectException is { } ex)
        {
            throw ex;
        }

        return Task.CompletedTask;
    }

    public List<long> SetFrequencyCalls { get; } = [];

    public List<RadioMode> SetModeCalls { get; } = [];

    /// <summary>Simulates the real <c>RadioController.RequireProtocol</c> throw when no radio is
    /// connected -- a routine, common state that a real hands-on run once crashed the whole app on
    /// (an uncaught <see cref="InvalidOperationException"/> propagating out of an
    /// <c>AsyncRelayCommand</c>); guards against that regression.</summary>
    public bool ThrowOnSetFrequencyOrMode { get; set; }

    /// <summary>Test-only hook: when set, <see cref="SetFrequencyAsync"/> awaits this before
    /// returning -- lets a test simulate a slow CAT backend (e.g. flrig's own readback-verify poll
    /// loop) to exercise RadioStatusViewModel's stale-completion race guard. Separately controllable
    /// from <see cref="TestPttGate"/>-style gates elsewhere (deterministic-gates-not-shared-race
    /// convention): a test using this must not also depend on scheduling order against any other
    /// gate.</summary>
    public TaskCompletionSource? SetFrequencyGate { get; set; }

    /// <summary>Test-only hook: when set, each successive <see cref="SetFrequencyAsync"/> call awaits
    /// the NEXT gate in this queue instead of <see cref="SetFrequencyGate"/> -- lets a test stagger
    /// two concurrent calls (e.g. a double-Enter under CommunityToolkit's default
    /// AllowConcurrentExecutions=true) to distinct, independently-releasable completions, per this
    /// project's own "deterministic gates, not shared race" convention (one shared gate can't express
    /// "op A finishes before op B" -- releasing it resolves every waiter at once).</summary>
    public Queue<TaskCompletionSource>? SetFrequencyGateQueue { get; set; }

    public async Task SetFrequencyAsync(long hz, CancellationToken ct = default)
    {
        if (ThrowOnSetFrequencyOrMode)
        {
            throw new InvalidOperationException("No radio connected -- call ConnectAsync first.");
        }

        if (SetFrequencyGateQueue is { Count: > 0 } queue)
        {
            await queue.Dequeue().Task.ConfigureAwait(false);
        }
        else if (SetFrequencyGate is not null)
        {
            await SetFrequencyGate.Task.ConfigureAwait(false);
        }

        SetFrequencyCalls.Add(hz);
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

    public List<int?> SetBandwidthCalls { get; } = [];

    /// <summary>Simulates a backend (e.g. flrig) that throws when SetBandwidth isn't actually
    /// supported -- mirrors <see cref="ThrowOnSetFrequencyOrMode"/>'s shape but kept separate since a
    /// real caller is expected to check <see cref="Capabilities"/> before calling this one, unlike
    /// frequency/mode.</summary>
    public bool ThrowOnSetBandwidth { get; set; }

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct = default)
    {
        if (ThrowOnSetBandwidth)
        {
            throw new InvalidOperationException("This backend does not support setting bandwidth.");
        }

        SetBandwidthCalls.Add(bandwidthHz);
        return Task.CompletedTask;
    }

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

    public RadioSafetySpec SafetySpec { get; set; } = new(false, RadioSafetySpec.DefaultSwrCutoffThreshold);

    public Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default) => Task.FromResult(SafetySpec);

    public Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default)
    {
        SafetySpec = spec;
        SafetySettingsChanged?.Invoke(spec);
        return Task.CompletedTask;
    }

    public event Action<RadioSafetySpec>? SafetySettingsChanged;

    /// <summary>Test-only helper mirroring <see cref="SaveSafetySettingsAsync"/>'s own event raise --
    /// lets a test simulate a live Options-side edit WITHOUT going through the fake's own save path
    /// (e.g. to prove a subscriber picks up the change without a settings-file round trip involved).</summary>
    public void RaiseSafetySettingsChanged(RadioSafetySpec spec) => SafetySettingsChanged?.Invoke(spec);

    public void Push(RadioState state) => _stateChanges.OnNext(state);

    public void PushConnectionEvent(RadioConnectionEvent evt) => _connectionEvents.OnNext(evt);

    public void Dispose()
    {
        _stateChanges.Dispose();
        _connectionEvents.Dispose();
    }
}

internal sealed class FakeHamlibDiscoveryService : IHamlibDiscoveryService
{
    public HamlibProbeResult ResultToReturn { get; set; } = new(false, null, null, [], []);

    public Exception? ThrowOnProbe { get; set; }

    public List<string?> ProbedPaths { get; } = [];

    public Task<HamlibProbeResult> ProbeAsync(string? overridePath, CancellationToken cancellationToken = default)
    {
        ProbedPaths.Add(overridePath);
        return ThrowOnProbe is { } ex ? Task.FromException<HamlibProbeResult>(ex) : Task.FromResult(ResultToReturn);
    }
}

internal sealed class FakeSerialPortEnumerator : ISerialPortEnumerator
{
    public IReadOnlyList<string> PortNames { get; set; } = [];

    public IReadOnlyList<string> GetPortNames() => PortNames;
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

    public Exception? SaveExceptionToThrow { get; set; }

    /// <summary>Read-only peek at what a test's <c>templateId</c> currently maps to, without going
    /// through <see cref="LoadAsync"/> (which would need the document reconstructed into raw
    /// snapshots) -- lets a test assert a PRE-EXISTING template's document/name survived a failed
    /// overwrite-save untouched.</summary>
    public IReadOnlyDictionary<string, (string Name, DateTimeOffset SavedAt, PersistedTemplateDocument Document)> Templates => _templates;

    public string CreateTemplateId(string name) => $"{name}_{Guid.NewGuid():N}";

    public string GetAssetPath(string templateId, string assetFileName) => $"/fake/templates/{templateId}/assets/{assetFileName}";

    public Task SaveAsync(string templateId, string name, PersistedTemplateDocument document, CancellationToken ct = default)
    {
        if (SaveExceptionToThrow is { } ex)
        {
            throw ex;
        }

        _templates[templateId] = (name, DateTimeOffset.Now, document);
        return Task.CompletedTask;
    }

    /// <summary>When a gate exists for a given templateId, <see cref="LoadAsync"/> awaits it instead
    /// of resolving immediately -- lets a test hold one template's load open while another completes
    /// first, to reproduce an overlapping-load race deterministically.</summary>
    public Dictionary<string, TaskCompletionSource<PersistedTemplateDocument>> LoadGates { get; } = [];

    public Task<PersistedTemplateDocument> LoadAsync(string templateId, CancellationToken ct = default) =>
        LoadGates.TryGetValue(templateId, out var gate) ? gate.Task : Task.FromResult(_templates[templateId].Document);

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

    public Exception? ExportExceptionToThrow { get; set; }

    public List<(string TemplateId, string DestinationZipPath)> Exported { get; } = [];

    public Task ExportAsync(string templateId, string destinationZipPath, CancellationToken ct = default)
    {
        if (ExportExceptionToThrow is { } ex)
        {
            throw ex;
        }

        if (!_templates.ContainsKey(templateId))
        {
            throw new InvalidOperationException($"Template '{templateId}' does not exist.");
        }

        Exported.Add((templateId, destinationZipPath));
        return Task.CompletedTask;
    }

    public Exception? ImportExceptionToThrow { get; set; }

    /// <summary>What <see cref="ImportAsync"/> registers as the imported template's own content --
    /// a test configures this to whatever it wants <see cref="ReadyRackViewModel.RefreshAsync"/> to
    /// pick up afterward. Defaults to an empty document under a fixed name, matching this fake's own
    /// "don't require every test to configure everything" convention elsewhere.</summary>
    public (string Name, PersistedTemplateDocument Document) ImportResult { get; set; } = ("Imported Template", new PersistedTemplateDocument([]));

    public Task<string> ImportAsync(string sourceZipPath, CancellationToken ct = default)
    {
        if (ImportExceptionToThrow is { } ex)
        {
            throw ex;
        }

        var templateId = CreateTemplateId(ImportResult.Name);
        _templates[templateId] = (ImportResult.Name, DateTimeOffset.Now, ImportResult.Document);
        return Task.FromResult(templateId);
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

    /// <summary>Every path passed to <see cref="LoadAsync"/>/<see cref="LoadOriginalAsync"/>, in call
    /// order -- lets a drop-multiple-files test assert a capped/short-circuited request never even
    /// reached the loader for the paths past the cap, not just that the RESULT looks capped.</summary>
    public List<string> RequestedPaths { get; } = [];

    /// <summary>When a requested path is a key here, that call throws the mapped exception instead of
    /// returning <see cref="ResultToReturn"/> -- lets a multi-file drop test fail specific files
    /// while the rest succeed, matching <c>AddImagesFromDroppedFilesAsync</c>'s own per-file
    /// try/catch contract.</summary>
    public Dictionary<string, Exception> FailForPath { get; } = [];

    public Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
        => GatedOrImmediate(path);

    public Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default)
        => GatedOrImmediate(path);

    private Task<IImageSource> GatedOrImmediate(string path)
    {
        RequestedPaths.Add(path);
        if (FailForPath.TryGetValue(path, out var ex))
        {
            return Task.FromException<IImageSource>(ex);
        }

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

    public string? HamlibLibraryPathToReturn { get; set; } = "/usr/lib/libhamlib.so.4";

    /// <summary>Test-only: makes <see cref="PickHamlibLibraryFileAsync"/> genuinely resume on a
    /// threadpool thread (via <see cref="Task.Run(Func{string?})"/>) instead of completing
    /// synchronously like every other fake picker method here. A real file-picker dialog always
    /// does this (a real OS modal, real async gap); the default `Task.FromResult` shape used
    /// elsewhere in this fake does NOT, which is exactly why the original crash this flag exists to
    /// reproduce (<c>OptionsWindowViewModel.BrowseHamlibLibraryAsync</c> touching
    /// <c>IsProbingHamlib</c> off the UI thread) was invisible to every test written against the
    /// synchronous default.</summary>
    public bool CompletePickHamlibLibraryFileOnBackgroundThread { get; set; }

    public string? SaveAdifPathToReturn { get; set; } = "/tmp/fake.adi";

    public (string Path, ImageExportFormat Format)? SaveImagePathToReturn { get; set; } = ("/tmp/fake-export.png", ImageExportFormat.Png);

    public string? LastSuggestedFileName { get; private set; }

    public string? LastSuggestedImageFileName { get; private set; }

    public Exception? ThrowOnPickSaveImageFile { get; set; }

    public Exception? ThrowOnPickImageFile { get; set; }

    public Task<string?> PickImageFileAsync() =>
        ThrowOnPickImageFile is { } ex ? Task.FromException<string?>(ex) : Task.FromResult(PathToReturn);

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

    public string? SavePngPathToReturn { get; set; } = "/tmp/fake-sent-frame.png";

    public string? LastSuggestedPngFileName { get; private set; }

    public Task<string?> PickSavePngFileAsync(string suggestedFileName)
    {
        LastSuggestedPngFileName = suggestedFileName;
        return Task.FromResult(SavePngPathToReturn);
    }

    public Task<string?> PickHamlibLibraryFileAsync() =>
        CompletePickHamlibLibraryFileOnBackgroundThread
            ? Task.Run(() => HamlibLibraryPathToReturn)
            : Task.FromResult(HamlibLibraryPathToReturn);

    public string? FolderPathToReturn { get; set; }

    public string? LastSuggestedStartDirectory { get; private set; }

    public Task<string?> PickFolderAsync(string? suggestedStartDirectory)
    {
        LastSuggestedStartDirectory = suggestedStartDirectory;
        return Task.FromResult(FolderPathToReturn);
    }

    public string? OpenWavPathToReturn { get; set; } = "/tmp/fake-open.wav";

    public string? SaveWavPathToReturn { get; set; } = "/tmp/fake-record.wav";

    public string? LastSuggestedWavFileName { get; private set; }

    public Task<string?> PickOpenWavFileAsync() => Task.FromResult(OpenWavPathToReturn);

    public Task<string?> PickSaveWavFileAsync(string suggestedFileName)
    {
        LastSuggestedWavFileName = suggestedFileName;
        return Task.FromResult(SaveWavPathToReturn);
    }

    public string? MmvPathToReturn { get; set; } = "/tmp/fake.mmv";

    public Task<string?> PickMmvFileAsync() => Task.FromResult(MmvPathToReturn);

    public string? SaveTemplateBundlePathToReturn { get; set; } = "/tmp/fake.sstemplate";

    public string? LastSuggestedTemplateBundleFileName { get; private set; }

    public Task<string?> PickSaveTemplateBundleAsync(string suggestedFileName)
    {
        LastSuggestedTemplateBundleFileName = suggestedFileName;
        return Task.FromResult(SaveTemplateBundlePathToReturn);
    }

    public string? OpenTemplateBundlePathToReturn { get; set; } = "/tmp/fake-open.sstemplate";

    public Task<string?> PickOpenTemplateBundleAsync() => Task.FromResult(OpenTemplateBundlePathToReturn);
}

internal sealed class FakeUrlLauncher : IUrlLauncher
{
    public List<string> OpenedUrls { get; } = [];

    public void Open(string url) => OpenedUrls.Add(url);
}

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: minimal fake for
/// RxHistoryPaneViewModel's IRxAudioAutoSaver dependency -- only the event is functional, matching
/// this project's sibling-fake convention.</summary>
internal sealed class FakeRxAudioAutoSaver : IRxAudioAutoSaver
{
    public event Action<string, string>? AudioAttached;

    public void RaiseAudioAttached(string entryId, string path) => AudioAttached?.Invoke(entryId, path);
}

/// <summary>fsk_cwid.md A-P3b, same "minimal fake, only the event is functional" convention as
/// <see cref="FakeRxAudioAutoSaver"/> immediately above -- RxHistoryPaneViewModel's
/// IRxStationIdAttacher dependency.</summary>
internal sealed class FakeRxStationIdAttacher : IRxStationIdAttacher
{
    public event Action<StationIdAttachment>? StationIdAttached;

    // fsk_cwid.md B-P5, code-review nit: parameter order matches StationIdAttachment's own property
    // order exactly (EntryId, Callsign, CallsignSource, NrRst, CwId) -- the record itself exists to
    // remove a positional-transposition hazard; a differently-ordered helper here would silently
    // reintroduce it for any future positional (not named) call.
    public void RaiseStationIdAttached(string entryId, string? callsign, string? callsignSource = null, string? nrRst = null, string? cwId = null) =>
        StationIdAttached?.Invoke(new StationIdAttachment(entryId, callsign, callsignSource, nrRst, cwId));
}

internal sealed class FakeClipboardImageService : IClipboardImageService
{
    public List<Bitmap> CopiedImages { get; } = [];

    public bool ResultToReturn { get; set; } = true;

    /// <summary>T0-11: when set, <see cref="CopyImageAsync"/> parks on this until it completes --
    /// lets a test deterministically hold a copy "in flight" (same Gate convention as
    /// FakeSettingsStore.Gate elsewhere in this project) to exercise
    /// ImageViewerWindowViewModel's own in-flight-copy dispose guard.</summary>
    public Task? Gate { get; set; }

    public async Task<bool> CopyImageAsync(Bitmap bitmap)
    {
        CopiedImages.Add(bitmap);
        if (Gate is not null)
        {
            await Gate.ConfigureAwait(false);
        }

        return ResultToReturn;
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

    // TX workflow modernization plan, Phase 7 -- same "call-tracking, plausibly-shaped result, not
    // real pixel behavior" convention as every other method above. Flatten's own pixel-equivalence
    // tests use the REAL TransmitImagePreparer, not this fake -- a fake can't produce pixels to
    // compare against.
    public int CropPixelsCallCount { get; private set; }

    public IImageSource CropPixels(IImageSource source, int x, int y, int width, int height)
    {
        CropPixelsCallCount++;
        return new ArrayImageSource(width, height, new Rgb24[width * height]);
    }

    public int CompositeCallCount { get; private set; }

    public IImageSource Composite(IImageSource background, IImageSource overlay, int x, int y)
    {
        CompositeCallCount++;
        return new ArrayImageSource(background.Width, background.Height, new Rgb24[background.Width * background.Height]);
    }
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

    /// <summary>Keyed by <see cref="ReceiveHistoryEntry.Id"/> -- when set, that specific entry's
    /// <see cref="LoadThumbnailAsync"/> call doesn't resolve until the test completes the matching
    /// <see cref="TaskCompletionSource{TResult}"/> manually. Lets a test control which of two
    /// overlapping loads resolves first, to provoke a specific out-of-order interleaving (e.g.
    /// RxImagePaneViewModel's own PreviousFrames sort-on-insert regression coverage) -- entries with
    /// no gate configured resolve immediately via <see cref="ThumbnailToReturn"/>, same as before.</summary>
    public Dictionary<string, TaskCompletionSource<IImageSource>> ThumbnailLoadGates { get; } = [];

    public string ImagesDirectory { get; set; } = "/tmp/scanlinestudio-history";

    public bool AutoSaveAudioEnabled { get; set; }

    public string AudioDirectory { get; set; } = "/tmp/scanlinestudio-history-audio";

    public event Action<ReceiveHistoryEntry>? Recorded;
    public event Action<ReceiveHistoryEntry>? Deleted;

    public List<ReceiveHistoryEntry> DeletedEntries { get; } = [];

    public List<string> DeletedFilePaths { get; } = [];

    public Exception? ThrowOnDelete { get; set; }

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
        if (ThumbnailLoadGates.TryGetValue(entry.Id, out var gate))
        {
            return gate.Task;
        }

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

    /// <summary>Removes from <see cref="EntriesToReturn"/> (matched by Id, mirroring the real
    /// store's row delete) so a subsequent <see cref="QueryAsync"/>/refresh naturally excludes it --
    /// same "actually applies the effect" discipline <see cref="QueryAsync"/>'s own doc comment
    /// describes, not a bare stub. Returns <see langword="false"/> without raising
    /// <see cref="Deleted"/> if no matching Id was found, matching the real store's contract.
    /// </summary>
    public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        if (ThrowOnDelete is not null)
        {
            throw ThrowOnDelete;
        }

        var removed = EntriesToReturn.RemoveAll(e => e.Id == entry.Id) > 0;
        if (!removed)
        {
            return Task.FromResult(false);
        }

        DeletedEntries.Add(entry);
        DeletedFilePaths.Add(entry.FilePath);
        Deleted?.Invoke(entry);
        return Task.FromResult(true);
    }

    public bool ThrowOnGetImagesDirectory { get; set; }

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) =>
        ThrowOnGetImagesDirectory ? throw new IOException("Simulated storage-locations load failure.") : Task.FromResult(ImagesDirectory);

    public List<string?> SetImagesDirectoryCalls { get; } = [];

    public bool ThrowOnSetImagesDirectory { get; set; }

    public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        if (ThrowOnSetImagesDirectory)
        {
            throw new IOException("Simulated directory-creation failure.");
        }

        SetImagesDirectoryCalls.Add(directory);
        ImagesDirectory = string.IsNullOrWhiteSpace(directory) ? "/tmp/scanlinestudio-history" : directory;
        return Task.CompletedTask;
    }

    public List<(bool Enabled, string? Directory)> SetAudioSettingsCalls { get; } = [];

    public bool ThrowOnSetAudioSettings { get; set; }

    public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default) =>
        Task.FromResult(new AudioAutoSaveSettings(AutoSaveAudioEnabled, AudioDirectory));

    public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default)
    {
        if (ThrowOnSetAudioSettings)
        {
            throw new IOException("Simulated directory-creation failure.");
        }

        SetAudioSettingsCalls.Add((enabled, directory));
        AutoSaveAudioEnabled = enabled;
        AudioDirectory = string.IsNullOrWhiteSpace(directory) ? "/tmp/scanlinestudio-history-audio" : directory;
        return Task.CompletedTask;
    }

    public List<(string EntryId, string Path)> SetAudioFilePathCalls { get; } = [];

    public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default)
    {
        SetAudioFilePathCalls.Add((entryId, path));
        return Task.FromResult(TryUpdateEntry(entryId, e => e with { AudioFilePath = path }));
    }

    public List<(string EntryId, string? Callsign, string? NrRst)> SetDecodedStationIdCalls { get; } = [];

    public Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? callsignSource, string? nrRst, string? cwId, CancellationToken ct = default)
    {
        SetDecodedStationIdCalls.Add((entryId, callsign, nrRst));
        return Task.FromResult(TryUpdateEntry(entryId, e => e with { DecodedCallsign = callsign, DecodedCallsignSource = callsignSource, DecodedNrRst = nrRst, DecodedCwId = cwId }));
    }

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

    public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default)
    {
        var cleared = 0;
        for (var i = 0; i < EntriesToReturn.Count; i++)
        {
            if (EntriesToReturn[i].LinkedQsoId == qsoId)
            {
                EntriesToReturn[i] = EntriesToReturn[i] with { LinkedQsoId = null };
                cleared++;
            }
        }

        return Task.FromResult(cleared);
    }

    public int ReconcileCallCount { get; private set; }

    public int ReconcileResultCount { get; set; }

    public Exception? ThrowOnReconcile { get; set; }

    public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default)
    {
        ReconcileCallCount++;
        if (ThrowOnReconcile is not null)
        {
            throw ThrowOnReconcile;
        }

        return Task.FromResult(ReconcileResultCount);
    }

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

    public Exception? ThrowOnGetById { get; set; }

    public Task<QsoRecord?> GetQsoByIdAsync(string id, CancellationToken ct = default)
    {
        if (ThrowOnGetById is not null)
        {
            throw ThrowOnGetById;
        }

        return Task.FromResult(Records.FirstOrDefault(r => r.Id == id));
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

    public List<string> DeletedIds { get; } = [];

    public Exception? ThrowOnDelete { get; set; }

    /// <summary>Defaults to <see langword="true"/> (deleted) -- a test overrides this to
    /// <see langword="false"/> only when specifically exercising the "already gone" path.</summary>
    public bool DeleteResultToReturn { get; set; } = true;

    public Task<bool> DeleteQsoAsync(string id, CancellationToken ct = default)
    {
        if (ThrowOnDelete is not null)
        {
            throw ThrowOnDelete;
        }

        if (!DeleteResultToReturn)
        {
            return Task.FromResult(false);
        }

        Records.RemoveAll(r => r.Id == id);
        DeletedIds.Add(id);
        return Task.FromResult(true);
    }

    /// <summary>When non-null, the NEXT <see cref="FindLikelyDuplicateAsync"/> call returns this
    /// (then leaves it in place -- a test that wants "no duplicate on the second check" sets this to
    /// <see langword="null"/> itself between the two calls). Defaults to <see langword="null"/> (no
    /// duplicate) so every EXISTING Log/Update test that doesn't care about this feature keeps
    /// behaving as it did before it existed.</summary>
    public QsoRecord? DuplicateResultToReturn { get; set; }

    public Exception? ThrowOnFindLikelyDuplicate { get; set; }

    public List<(string Callsign, DateTimeOffset StartUtc, long? FrequencyHz, string? ExcludeId)> FindLikelyDuplicateCalls { get; } = [];

    public Task<QsoRecord?> FindLikelyDuplicateAsync(string callsign, DateTimeOffset startUtc, long? frequencyHz, string? excludeId, CancellationToken ct = default)
    {
        FindLikelyDuplicateCalls.Add((callsign, startUtc, frequencyHz, excludeId));
        if (ThrowOnFindLikelyDuplicate is not null)
        {
            throw ThrowOnFindLikelyDuplicate;
        }

        return Task.FromResult(DuplicateResultToReturn);
    }

    /// <summary>Real case-insensitive callsign filtering + count + max-`StartUtc` selection over
    /// <see cref="Records"/> -- unlike <see cref="SearchAsync"/> above (which ignores
    /// <c>query.Callsign</c> entirely), this must actually filter, since worked-before-indicator
    /// tests assert both "found" and "no match" outcomes. Deliberately does NOT call the real
    /// `AmateurBandLookup.BandFor` -- `ScanlineStudio.UI.Tests` doesn't reference
    /// `ScanlineStudio.Core.Logbook` (same layering wall `ScanlineStudio.UI` itself is gated behind),
    /// so <see cref="WorkedBeforeBandToReturn"/> stands in for it; real band-label correctness is
    /// `LogbookSessionService`'s own responsibility, tested in
    /// `ScanlineStudio.Application.Tests.LogbookSessionServiceTests` instead.</summary>
    public string? WorkedBeforeBandToReturn { get; set; }

    /// <summary>When set, <see cref="GetWorkedBeforeAsync"/> returns this outcome directly (with no
    /// <see cref="WorkedBeforeInfo"/>), bypassing the real filtering below -- models
    /// <see cref="WorkedBeforeOutcome.Failed"/> the way the real <c>LogbookSessionService</c> (and
    /// its interface contract) actually reports it, i.e. as a returned VALUE, never a thrown
    /// exception (code-review finding: an earlier version of this fake modeled "failed" as an actual
    /// thrown exception via a since-removed <c>ThrowOnGetWorkedBefore</c> field, which does not match
    /// the real, documented "never throws" contract -- a caller's real `catch` clause only handles
    /// <see cref="OperationCanceledException"/>, so a thrown non-cancellation exception here escaped
    /// uncaught as an unobserved fire-and-forget task fault instead of exercising the VM's own
    /// `Failed` handling, silently passing the test without ever writing the property under test).</summary>
    public WorkedBeforeOutcome? WorkedBeforeOutcomeOverride { get; set; }

    public List<string> GetWorkedBeforeCalls { get; } = [];

    public Task<WorkedBeforeLookup> GetWorkedBeforeAsync(string callsign, CancellationToken ct = default)
    {
        GetWorkedBeforeCalls.Add(callsign);
        if (WorkedBeforeOutcomeOverride is { } outcome)
        {
            return Task.FromResult(new WorkedBeforeLookup(outcome, null));
        }

        var matches = Records.Where(r => string.Equals(r.Callsign, callsign, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return Task.FromResult(new WorkedBeforeLookup(WorkedBeforeOutcome.NotFound, null));
        }

        var latest = matches.MaxBy(r => r.StartUtc)!;
        var info = new WorkedBeforeInfo(matches.Count, latest.StartUtc, WorkedBeforeBandToReturn);
        return Task.FromResult(new WorkedBeforeLookup(WorkedBeforeOutcome.Found, info));
    }

    public QrzCallsignLookupResult LookupResultToReturn { get; set; } = new(true, "Test Name", "Test QTH", "AA00", null);

    public Exception? ThrowOnLookup { get; set; }

    /// <summary>Defaults to configured (true) so every EXISTING test that doesn't care about this
    /// gate keeps behaving as it did before the gate was added.</summary>
    public bool IsQrzLookupConfiguredResult { get; set; } = true;

    public Exception? ThrowOnIsQrzLookupConfigured { get; set; }

    public Task<bool> IsQrzLookupConfiguredAsync(CancellationToken ct = default)
    {
        if (ThrowOnIsQrzLookupConfigured is not null)
        {
            throw ThrowOnIsQrzLookupConfigured;
        }

        return Task.FromResult(IsQrzLookupConfiguredResult);
    }

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

/// <summary>Backs <c>OptionsWindowViewModel</c>'s Storage-section Config/Database/Log rows in
/// tests -- in-memory only, no real file at any of the returned paths. Set the corresponding
/// <c>*ToThrow</c> to make the matching Set* call fail, mirroring the real
/// <c>AppLocationsService</c>'s own validation-failure/conflict shape (an exception, not a bool).</summary>
internal sealed class FakeAppLocationsService : IAppLocationsService
{
    // Restart-required-settings backlog item 3 (2026-08-27): applies live now, same immediate-apply
    // shape as LogDirectory/SetLogDirectoryAsync below -- no more PendingConfigDirectory/staging.
    public string ConfigDirectory { get; set; } = "/config/current";
    public Exception? ConfigDirectoryToThrow { get; set; }

    public string DatabaseDirectory { get; set; } = "/database/current";
    public string? PendingDatabaseDirectory { get; set; }
    public Exception? DatabaseDirectoryToThrow { get; set; }

    public string LogDirectory { get; set; } = "/logs/current";
    public Exception? LogDirectoryToThrow { get; set; }

    public Task<string> GetConfigDirectoryAsync(CancellationToken ct = default) => Task.FromResult(ConfigDirectory);

    public Task SetConfigDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        if (ConfigDirectoryToThrow is { } ex)
        {
            throw ex;
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            ConfigDirectory = directory;
        }

        return Task.CompletedTask;
    }

    public Task<string> GetDatabaseDirectoryAsync(CancellationToken ct = default) => Task.FromResult(DatabaseDirectory);

    public Task<string?> GetPendingDatabaseDirectoryAsync(CancellationToken ct = default) => Task.FromResult(PendingDatabaseDirectory);

    public Task SetDatabaseDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        if (DatabaseDirectoryToThrow is { } ex)
        {
            throw ex;
        }

        PendingDatabaseDirectory = string.IsNullOrWhiteSpace(directory) || directory == DatabaseDirectory ? null : directory;
        return Task.CompletedTask;
    }

    public Task<string> GetLogDirectoryAsync(CancellationToken ct = default) => Task.FromResult(LogDirectory);

    public Task SetLogDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        if (LogDirectoryToThrow is { } ex)
        {
            throw ex;
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            LogDirectory = directory;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Never actually spawns a process or sets a real flag's process-wide effect -- just
/// records what <c>OptionsWindowViewModel</c>'s Restart Now path asked for, so a test can assert
/// on it without touching <see cref="System.Diagnostics.Process"/>.</summary>
internal sealed class FakeApplicationRestarter : IApplicationRestarter
{
    public bool RestartRequested { get; set; }

    public bool StartNewInstanceCalled { get; private set; }

    public bool StartNewInstance()
    {
        StartNewInstanceCalled = true;
        return true;
    }
}
