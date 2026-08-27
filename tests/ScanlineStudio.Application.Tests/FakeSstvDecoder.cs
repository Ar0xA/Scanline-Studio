using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Application.Tests;

// Implements ISstvDecoderMaintenance (unlike the two other FakeSstvDecoders in Core.Imaging.Tests/
// Core.Logbook.Tests, which have no need for it) so SstvSessionServiceTests can drive the ultracode
// audit finding #34 maintenance-signal wiring without a real RestartableSstvDecoder.
//
// IDisposable (RX buffer subsystem Phase 7, disposal-chain sub-piece): ISstvDecoder itself does NOT
// extend IDisposable (SstvSessionService.DisposeAsync duck-types `_decoder is IDisposable`, matching
// the real production RestartableSstvDecoder, which implements it conditionally for the same reason)
// -- implemented here too so SstvSessionServiceTests can assert that duck-typed dispose path actually
// fires, without needing a real RestartableSstvDecoder/AnalogFmSstvDecoder.
internal sealed class FakeSstvDecoder : ISstvDecoder, ISstvDecoderMaintenance, IDisposable
{
    public int SampleRate { get; set; } = SstvSampleRate.Default;

    public bool DisposedForTests { get; private set; }

    public void Dispose() => DisposedForTests = true;

    public List<ReadOnlyMemory<float>> PushedSamples { get; } = [];

    public Exception? ThrowOnPush { get; set; }

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public event Action? RestartOverdue;

    public event Action? RestartCriticallyOverdue;

    public event Action? Restarted;

    public int ResetAgcCallCount { get; private set; }

    public void ResetAgc() => ResetAgcCallCount++;

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

    public int ArmScopeCaptureCallCount { get; private set; }

    public int? LastScopeCaptureSize { get; private set; }

    public void ArmScopeCapture(int size)
    {
        ArmScopeCaptureCallCount++;
        LastScopeCaptureSize = size;
    }

    public double[]? ScopeCaptureChannel0ToReturn { get; set; }

    public double[]? TryGetScopeCaptureChannel0() => ScopeCaptureChannel0ToReturn;

    public double[]? ScopeCaptureChannel1ToReturn { get; set; }

    public double[]? TryGetScopeCaptureChannel1() => ScopeCaptureChannel1ToReturn;

    public int ForceModeCallCount { get; private set; }

    public SstvModeDefinition? LastForcedMode { get; private set; }

    public void ForceMode(SstvModeDefinition mode)
    {
        ForceModeCallCount++;
        LastForcedMode = mode;
    }

    public int RequestAbandonReceptionCallCount { get; private set; }

    public void RequestAbandonReception() => RequestAbandonReceptionCallCount++;

    public int RequestCorrectSlantCallCount { get; private set; }

    public void RequestCorrectSlant() => RequestCorrectSlantCallCount++;

    public double? SlantPpm { get; set; }

    public SstvSyncSource SyncSource { get; set; }

    public int? SyncOffsetSamples { get; set; }

    public double SignalPeakLevel { get; set; }

    public bool IsLevelOverdriven { get; set; }

    public double? SyncFrequencyCorrectionHz { get; set; }

    public int BufferedSampleCount { get; set; }

    public bool AutoSlantEnabled { get; set; } = true;

    public bool AutoSyncEnabled { get; set; } = true;

    public bool AutoStopEnabled { get; set; }

    public bool SyncRestartEnabled { get; set; } = true;

    public int SenseLevel { get; set; } = 1;

    public RxBpfPreset RxBpfPreset { get; set; } = RxBpfPreset.Wide;

    public bool StationIdDecodeEnabled { get; set; }

    /// <summary>Piece C2 test hook: lets a test synchronously observe/block inside a PushSamples call
    /// (e.g. to hold DecodeFromFileAsync's background-thread chunk loop open long enough to assert
    /// single-flight/cross-guard behavior against a concurrent call) -- same shape as
    /// <see cref="ThrowOnPush"/>, just a callback instead of an exception.</summary>
    public Action<ReadOnlyMemory<float>>? OnPushSamples { get; set; }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        PushedSamples.Add(samples);
        OnPushSamples?.Invoke(samples);
        if (ThrowOnPush is not null)
        {
            throw ThrowOnPush;
        }
    }

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);

    public void RaiseRestartOverdue() => RestartOverdue?.Invoke();

    public void RaiseRestartCriticallyOverdue() => RestartCriticallyOverdue?.Invoke();

    public void RaiseRestarted() => Restarted?.Invoke();
}
