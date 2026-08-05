using System.Reactive.Subjects;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeRadioController : IRadioController, IDisposable
{
    private readonly Subject<RadioState> _stateChanges = new();
    private readonly Subject<RadioConnectionEvent> _connectionEvents = new();

    public List<RadioConnectionSpec> ConnectCalls { get; } = [];

    public List<bool> PttCalls { get; } = [];

    public RadioState? LastKnownState { get; private set; }

    public RadioCapabilities Capabilities => RadioCapabilities.SetFrequency | RadioCapabilities.SetMode | RadioCapabilities.PttControl;

    public IObservable<RadioState> StateChanges => _stateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _connectionEvents;

    public Task ConnectAsync(RadioConnectionSpec spec, CancellationToken ct)
    {
        ConnectCalls.Add(spec);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SetFrequencyAsync(long hz, CancellationToken ct) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct) => Task.CompletedTask;

    public Task SetPttAsync(bool tx, CancellationToken ct)
    {
        PttCalls.Add(tx);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stateChanges.Dispose();
        _connectionEvents.Dispose();
    }
}
