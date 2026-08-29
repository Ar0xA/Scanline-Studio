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
internal sealed class FakeSstvDecoder : ISstvDecoder, ISstvDecoderMaintenance, ISstvDecoderReconfiguration, IDisposable
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

    public long ReceptionSequence { get; private set; }

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

    public int RequestPllTuningCallCount { get; private set; }

    public double LastPllVcoGain { get; private set; }

    public int LastPllLoopOrder { get; private set; }

    public double LastPllLoopCutoffHz { get; private set; }

    public int LastPllOutputOrder { get; private set; }

    public double LastPllOutputCutoffHz { get; private set; }

    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz)
    {
        RequestPllTuningCallCount++;
        LastPllVcoGain = vcoGain;
        LastPllLoopOrder = loopOrder;
        LastPllLoopCutoffHz = loopCutoffHz;
        LastPllOutputOrder = outputOrder;
        LastPllOutputCutoffHz = outputCutoffHz;
    }

    public int RequestZeroCrossingTuningCallCount { get; private set; }

    public ZeroCrossingSmoothingMode LastZeroCrossingSmoothingMode { get; private set; }

    public int LastZeroCrossingOutputOrder { get; private set; }

    public double LastZeroCrossingOutputCutoffHz { get; private set; }

    public double LastZeroCrossingSmoothingFrequencyHz { get; private set; }

    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz)
    {
        RequestZeroCrossingTuningCallCount++;
        LastZeroCrossingSmoothingMode = smoothingMode;
        LastZeroCrossingOutputOrder = outputOrder;
        LastZeroCrossingOutputCutoffHz = outputCutoffHz;
        LastZeroCrossingSmoothingFrequencyHz = smoothingFrequencyHz;
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

    public int SetModeLockCallCount { get; private set; }

    public SstvModeDefinition? LastLockedMode { get; private set; }

    public void SetModeLock(SstvModeDefinition? mode)
    {
        SetModeLockCallCount++;
        LastLockedMode = mode;
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

    public void RaiseModeDetected(SstvModeDefinition mode)
    {
        // ISstvDecoder.ReceptionSequence: bumped before the raise, matching the real
        // implementations' contract (see that property's own doc comment) -- first value is 1, 0
        // means "unset." A test that needs the minority-ordering shape (ModeDetected then
        // DecodeRestarted within the "same push") raises both from the same synchronous call site
        // it controls -- this fake has no separate push-epoch concept of its own (that machinery is
        // internal to SstvSessionService, not part of ISstvDecoder).
        ReceptionSequence++;
        ModeDetected?.Invoke(mode);
    }

    public void RaiseStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);

    public void RaiseRestartOverdue() => RestartOverdue?.Invoke();

    public void RaiseRestartCriticallyOverdue() => RestartCriticallyOverdue?.Invoke();

    public void RaiseRestarted() => Restarted?.Invoke();

    // Restart-required-settings backlog item 4 (2026-08-27): a lightweight, fully-controllable
    // ISstvDecoderReconfiguration -- lets SstvSessionServiceTests exercise RequestSampleRateAsync's
    // orchestration (stop/commit/restart ordering, Busy/Rejected handling) without needing a real
    // RestartableSstvDecoder's own idle-gating/threshold machinery.
    public event Action? ReconfigurationRejected;

    public int RequestReconfigurationCallCount { get; private set; }

    public (RxBpfPreset RxBpfPreset, DemodType DemodType, RxBufferMode RxBufferMode)? LastRequestedReconfiguration { get; private set; }

    public void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode)
    {
        RequestReconfigurationCallCount++;
        LastRequestedReconfiguration = (rxBpfPreset, demodType, rxBufferMode);
    }

    // Restart-required-settings backlog item 6 (2026-08-28): mirrors RequestReconfiguration's own
    // call-tracking shape immediately above.
    public int RequestRxBpfPresetCallCount { get; private set; }

    public RxBpfPreset? LastRequestedRxBpfPreset { get; private set; }

    public void RequestRxBpfPreset(RxBpfPreset rxBpfPreset)
    {
        RequestRxBpfPresetCallCount++;
        LastRequestedRxBpfPreset = rxBpfPreset;
    }

    public void RaiseReconfigurationRejected() => ReconfigurationRejected?.Invoke();

    public int RequestSampleRateCallCount { get; private set; }

    public int? PendingSampleRate { get; private set; }

    public void RequestSampleRate(int sampleRate)
    {
        RequestSampleRateCallCount++;
        PendingSampleRate = sampleRate == SampleRate ? null : sampleRate;
    }

    /// <summary>Configures what the next <see cref="ApplyPendingReconfigurationNow"/> call returns --
    /// default <see cref="SwapResult.Committed"/>, matching the common case.</summary>
    public SwapResult ApplyPendingReconfigurationNowResultToReturn { get; set; } = SwapResult.Committed;

    public int ApplyPendingReconfigurationNowCallCount { get; private set; }

    /// <summary>Lets a test observe state exactly at the commit point -- e.g. assert the audio engine
    /// is genuinely stopped and the waterfall hasn't been touched yet, proving the caller's own
    /// stop-then-commit-then-restart ordering, not just its final state.</summary>
    public Action? OnApplyPendingReconfigurationNow { get; set; }

    public SwapResult ApplyPendingReconfigurationNow()
    {
        ApplyPendingReconfigurationNowCallCount++;
        OnApplyPendingReconfigurationNow?.Invoke();

        // Code-review round-1 finding: Busy must be checked BEFORE touching any pending state,
        // matching the real RestartableSstvDecoder's own contract (its CAS guard trips before the
        // pending record is ever read) -- checking this after the PendingSampleRate-null branch
        // below made a test's "the stuck pending rate was cleared on Busy" assertion pass even if
        // the production code never cleared anything at all.
        if (ApplyPendingReconfigurationNowResultToReturn == SwapResult.Busy)
        {
            return SwapResult.Busy;
        }

        if (PendingSampleRate is null)
        {
            return SwapResult.NothingPending;
        }

        var result = ApplyPendingReconfigurationNowResultToReturn;
        if (result == SwapResult.Committed)
        {
            SampleRate = PendingSampleRate.Value;
        }

        if (result == SwapResult.Rejected)
        {
            ReconfigurationRejected?.Invoke();
        }

        PendingSampleRate = null;
        return result;
    }
}
