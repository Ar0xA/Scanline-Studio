using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Application.Tests;

/// <summary>Auditor-suggested (step 12, round 1 code-review of RxAudioAutoSaver): a minimal
/// <see cref="ISstvSessionService"/> fake for correlation-only tests that need precise, arbitrary
/// control over <see cref="AudioSliceReady"/>'s <c>receptionId</c> values -- something a REAL
/// <see cref="SstvSessionService"/> can't give (its ids are always sequential from its own decoder,
/// starting at 1). Only <see cref="AudioSliceReady"/>/<see cref="TrySaveReceptionAudioAsync"/> are
/// functional; every other member throws, matching this project's sibling-fake convention for
/// members "not exercised by" the test suite that owns the fake.</summary>
internal sealed class FakeSstvSessionServiceForCorrelation : ISstvSessionService
{
    private static NotSupportedException NotExercised() => new("Not exercised by RxAudioAutoSaverTests's eviction/race coverage.");

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

    public void SetAutoSaveAudioEnabled(bool enabled)
    {
    }

    public void SetAudioDirectory(string? directory)
    {
    }

    public bool IsAudioAutoSaveActive => throw NotExercised();

    public event Action<SstvModeDefinition>? ModeDetected { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action<SstvModeDefinition>? DecodeRestarted { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action<TransmitProgressInfo>? TransmitProgressChanged { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action<bool>? CapturePausedForTransmitChanged { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action? MaintenanceWarningRaised { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action? MaintenanceWarningCleared { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action? MaintenanceCriticalStopRaised { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action? DecoderInstanceReplaced { add => throw NotExercised(); remove => throw NotExercised(); }

    public event Action? ReconfigurationRejected { add => throw NotExercised(); remove => throw NotExercised(); }

    public IWaterfallSource Waterfall => throw NotExercised();

    public IReceivedImageBuffer ReceivedImage => throw NotExercised();

    public IReadOnlyList<SstvModeDefinition> AvailableModes => throw NotExercised();

    public bool IsReceiving => throw NotExercised();

    public bool IsTransmitting => throw NotExercised();

    public bool IsRecording => throw NotExercised();

    public bool IsAutoDetectPaused => throw NotExercised();

    public bool IsPttLocked => throw NotExercised();

    public double? SlantPpm => throw NotExercised();

    public SstvSyncSource SyncSource => throw NotExercised();

    public int? SyncOffsetSamples => throw NotExercised();

    public double SignalPeakLevel => throw NotExercised();

    public double RawInputPeakLevel => throw NotExercised();

    public bool IsLevelOverdriven => throw NotExercised();

    public bool AutoSlantEnabled { get => throw NotExercised(); set => throw NotExercised(); }

    public int SenseLevel { get => throw NotExercised(); set => throw NotExercised(); }

    public RxBpfPreset RxBpfPreset => throw NotExercised();

    public double? SyncFrequencyCorrectionHz => throw NotExercised();

    public int BufferedSampleCount => throw NotExercised();

    public int CaptureOverrunCount => throw NotExercised();

    public double GetLeaderToneDurationMs(SstvModeDefinition mode) => throw NotExercised();

    public (VisHeaderKind Kind, int Value) GetVisHeaderInfo(SstvModeDefinition mode) => throw NotExercised();

    public void SetAutoDetectPaused(bool paused) => throw NotExercised();

    public Task<string?> GetOperatorCallsignAsync(CancellationToken ct = default) => throw NotExercised();

    public Task<string?> GetOperatorGridAsync(CancellationToken ct = default) => throw NotExercised();

    public Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default) => throw NotExercised();

    public Task<SoundFileIdValidationResult> ValidateStationIdSoundFileAsync(string path, CancellationToken ct = default) => throw NotExercised();

    public void RequestReSync() => throw NotExercised();

    public void RequestNotch(bool enabled, double? frequencyHz) => throw NotExercised();

    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz) => throw NotExercised();

    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz) => throw NotExercised();

    public void ArmScopeCapture(int size) => throw NotExercised();

    public double[]? TryGetScopeCaptureChannel0() => throw NotExercised();

    public double[]? TryGetScopeCaptureChannel1() => throw NotExercised();

    public void RequestSenseLevel(int level) => throw NotExercised();

    public void RequestAutoSyncEnabled(bool enabled) => throw NotExercised();

    public void RequestAutoStopEnabled(bool enabled) => throw NotExercised();

    public void RequestAutoSlantEnabled(bool enabled) => throw NotExercised();

    public void RequestSyncRestartEnabled(bool enabled) => throw NotExercised();

    public void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode) => throw NotExercised();

    public void RequestRxBpfPreset(RxBpfPreset preset) => throw NotExercised();

    public Task<SampleRateApplyResult> RequestSampleRateAsync(int sampleRate, CancellationToken ct = default) => throw NotExercised();

    public Task<CaptureDeviceApplyResult> RequestCaptureDeviceAsync(string? deviceId, string? deviceName, CancellationToken ct = default) => throw NotExercised();

    public Task PersistSenseLevelAsync(int level, CancellationToken ct = default) => throw NotExercised();

    public Task PersistRxBpfPresetAsync(RxBpfPreset preset, CancellationToken ct = default) => throw NotExercised();

    public void RequestCorrectSlant() => throw NotExercised();

    public void AbortReception() => throw NotExercised();

    public void ForceMode(SstvModeDefinition mode) => throw NotExercised();

    public void SetModeLock(SstvModeDefinition? mode) => throw NotExercised();

    public Task StartReceivingAsync(CancellationToken ct = default) => throw NotExercised();

    public Task StopReceivingAsync() => throw NotExercised();

    public Task StartRecordingAsync(string path) => throw NotExercised();

    public Task StopRecordingAsync() => throw NotExercised();

    public Task<LoopbackSelfTestResult> RunLoopbackSelfTestAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default) => throw NotExercised();

    public Task DecodeFromFileAsync(string path, CancellationToken ct = default) => throw NotExercised();

    public Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default) => throw NotExercised();

    public Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default) => throw NotExercised();

    public Task<int> GetTxVolumePercentAsync(CancellationToken ct = default) => throw NotExercised();

    public Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default) => throw NotExercised();

    public Task<bool> GetTxDeviceMutedAsync(CancellationToken ct = default) => throw NotExercised();

    public Task SetPttLockAsync(bool locked, CancellationToken ct = default) => throw NotExercised();

    public Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default) => throw NotExercised();

    public Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default) => throw NotExercised();

    public ValueTask DisposeAsync() => throw NotExercised();
}
