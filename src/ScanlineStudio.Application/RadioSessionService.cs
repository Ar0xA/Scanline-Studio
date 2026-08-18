using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class RadioSessionService : IRadioSessionService
{
    private readonly IRadioController _controller;
    private readonly ISettingsStore _settingsStore;
    private readonly IReadOnlyList<IRadioProtocolFactory> _protocolFactories;
    private readonly ILogger<RadioSessionService> _logger;

    public RadioSessionService(
        IRadioController controller, ISettingsStore settingsStore, IEnumerable<IRadioProtocolFactory> protocolFactories,
        ILogger<RadioSessionService> logger)
    {
        _controller = controller;
        _settingsStore = settingsStore;
        _protocolFactories = protocolFactories.ToList();
        _logger = logger;
    }

    public RadioState? LastKnownState => _controller.LastKnownState;

    public RadioCapabilities Capabilities => _controller.Capabilities;

    public string RigId => _controller.RigId;

    public IObservable<RadioState> StateChanges => _controller.StateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents => _controller.ConnectionEvents;

    public async Task ConnectUsingSettingsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var radioSettings = appSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings)
            ?? new RadioConnectionSettings();
        var spec = radioSettings.ToConnectionSpec();
        Log.ResolvedSpecFromSettings(_logger, radioSettings.BackendId);
        await _controller.ConnectAsync(spec, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="IRadioSessionService.TestConnectionAsync"/>'s own doc comment -- a
    /// fresh, disposable <see cref="IRadioProtocol"/>, never the real <see cref="_controller"/>
    /// session. Same "exactly one factory match" resolution <see cref="IRadioController"/> itself
    /// uses internally (mirrored here rather than shared, since that resolution lives on the
    /// concrete <c>RadioController</c> class, not the <see cref="IRadioController"/> interface this
    /// service is scoped to depend on).</summary>
    public async Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default)
    {
        var matches = _protocolFactories.Where(f => f.CanHandle(spec)).ToList();
        if (matches.Count != 1)
        {
            var message = matches.Count == 0
                ? $"No backend registered for {spec.GetType().Name}."
                : $"{matches.Count} backends all claim {spec.GetType().Name} -- registration is ambiguous.";
            Log.TestConnectionResolutionFailed(_logger, spec.GetType().Name, matches.Count);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, message);
        }

        var protocol = matches[0].Create(spec);
        try
        {
            await protocol.PollAsync(ct).ConfigureAwait(false);
            Log.TestConnectionSucceeded(_logger, protocol.RigId);
            return new RadioConnectionTestResult(true, protocol.RigId, protocol.Capabilities, null);
        }
        catch (Exception ex)
        {
            Log.TestConnectionFailed(_logger, spec.GetType().Name, ex);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
        }
        finally
        {
            await protocol.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task DisconnectAsync() => _controller.DisconnectAsync();

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => _controller.SetFrequencyAsync(hz, ct);

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => _controller.SetModeAsync(mode, ct);

    public Task SetPttAsync(bool tx, CancellationToken ct = default) => _controller.SetPttAsync(tx, ct);

    public async Task<IReadOnlyList<FrequencyPreset>> GetFrequencyPresetsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = appSettings.GetSection(FrequencyPresetsSettings.SectionKey, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings)
            ?? new FrequencyPresetsSettings();
        return section.Presets;
    }

    public async Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var updated = appSettings.WithSection(
            FrequencyPresetsSettings.SectionKey,
            new FrequencyPresetsSettings { Presets = presets },
            FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings);
        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = appSettings.GetSection(RadioSafetySettings.SectionKey, RadioSafetySettingsJsonContext.Default.RadioSafetySettings)
            ?? new RadioSafetySettings();
        return new RadioSafetySpec(section.SwrCutoffEnabled, section.SwrCutoffThreshold);
    }

    public async Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var updated = appSettings.WithSection(
            RadioSafetySettings.SectionKey,
            new RadioSafetySettings { SwrCutoffEnabled = spec.SwrCutoffEnabled, SwrCutoffThreshold = spec.SwrCutoffThreshold },
            RadioSafetySettingsJsonContext.Default.RadioSafetySettings);
        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Resolved radio spec from settings: backend={BackendId}")]
        public static partial void ResolvedSpecFromSettings(ILogger logger, string backendId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestConnectionAsync: factory resolution failed for {SpecType}: {MatchCount} match(es)")]
        public static partial void TestConnectionResolutionFailed(ILogger logger, string specType, int matchCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "TestConnectionAsync succeeded: rigId={RigId}")]
        public static partial void TestConnectionSucceeded(ILogger logger, string rigId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestConnectionAsync failed for {SpecType}")]
        public static partial void TestConnectionFailed(ILogger logger, string specType, Exception exception);
    }
}
