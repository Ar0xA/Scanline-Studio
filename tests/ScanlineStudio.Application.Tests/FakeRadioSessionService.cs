using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeRadioSessionService : IRadioSessionService
{
    public List<bool> PttCalls { get; } = [];

    public RadioState? LastKnownState => null;

    public RadioCapabilities Capabilities { get; set; } = RadioCapabilities.None;

    public IObservable<RadioState> StateChanges { get; } = System.Reactive.Linq.Observable.Never<RadioState>();

    public IObservable<RadioConnectionEvent> ConnectionEvents { get; } = System.Reactive.Linq.Observable.Never<RadioConnectionEvent>();

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Unlike the other members here, this one honors <paramref name="ct"/> (throwing if
    /// already cancelled) -- matching the real <c>RigctldClientProtocol</c>/<c>HamlibRadioProtocol</c>
    /// contract (both throw immediately from their own <c>_requestLock.WaitAsync(ct)</c> on an
    /// already-cancelled token) closely enough to actually distinguish
    /// <c>SstvSessionService.PlayWithPttAsync</c>'s cleanup path passing a fresh token from one that
    /// (incorrectly) reuses the possibly-cancelled transmit token -- see
    /// <c>SstvSessionServiceTests.TransmitAsync_TokenCancelledMidTransmit_StillUnkeysPttAndRestartsCapture</c>.</summary>
    public Task SetPttAsync(bool tx, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PttCalls.Add(tx);
        return Task.CompletedTask;
    }

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
}
