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

    public int ForceModeCallCount { get; private set; }

    public SstvModeDefinition? LastForcedMode { get; private set; }

    public void ForceMode(SstvModeDefinition mode)
    {
        ForceModeCallCount++;
        LastForcedMode = mode;
    }

    public int RequestCorrectSlantCallCount { get; private set; }

    public void RequestCorrectSlant() => RequestCorrectSlantCallCount++;

    public double? SlantPpm { get; set; }

    public int? SyncOffsetSamples { get; set; }

    public double SignalPeakLevel { get; set; }

    public bool IsLevelOverdriven { get; set; }

    public double? SyncFrequencyCorrectionHz { get; set; }

    public int BufferedSampleCount { get; set; }

    public bool AutoSlantEnabled { get; set; } = true;

    public bool StationIdDecodeEnabled { get; set; }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        PushedSamples.Add(samples);
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
