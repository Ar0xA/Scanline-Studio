using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeRadioSessionService : IRadioSessionService
{
    public List<bool> PttCalls { get; } = [];

    public RadioState? LastKnownState => null;

    public IObservable<RadioState> StateChanges { get; } = System.Reactive.Linq.Observable.Never<RadioState>();

    public IObservable<RadioConnectionEvent> ConnectionEvents { get; } = System.Reactive.Linq.Observable.Never<RadioConnectionEvent>();

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetPttAsync(bool tx, CancellationToken ct = default)
    {
        PttCalls.Add(tx);
        return Task.CompletedTask;
    }
}
