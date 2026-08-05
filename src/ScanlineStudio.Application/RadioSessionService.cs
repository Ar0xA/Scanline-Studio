using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed class RadioSessionService : IRadioSessionService
{
    private readonly IRadioController _controller;
    private readonly ISettingsStore _settingsStore;

    public RadioSessionService(IRadioController controller, ISettingsStore settingsStore)
    {
        _controller = controller;
        _settingsStore = settingsStore;
    }

    public RadioState? LastKnownState => _controller.LastKnownState;

    public IObservable<RadioState> StateChanges => _controller.StateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _controller.ConnectionEvents;

    public async Task ConnectUsingSettingsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var radioSettings = appSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings)
            ?? new RadioConnectionSettings();
        var spec = radioSettings.ToConnectionSpec();
        await _controller.ConnectAsync(spec, ct).ConfigureAwait(false);
    }

    public Task DisconnectAsync() => _controller.DisconnectAsync();

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => _controller.SetFrequencyAsync(hz, ct);

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => _controller.SetModeAsync(mode, ct);

    public Task SetPttAsync(bool tx, CancellationToken ct = default) => _controller.SetPttAsync(tx, ct);
}
