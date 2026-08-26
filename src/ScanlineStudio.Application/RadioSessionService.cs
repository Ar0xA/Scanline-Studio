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

    public bool IsGenuinelyConnected => _controller.IsGenuinelyConnected;

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

        // Round-1 code-review finding (Tier A Batch 10 chunk 10c, real risk fixed): `Create` used to
        // run OUTSIDE this try -- a throwing factory leaked out of a method whose own interface
        // contract (IRadioSessionService.TestConnectionAsync's doc comment) says it always returns a
        // result, never throws.
        try
        {
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
                // Round-1 code-review finding: a throwing DisposeAsync here used to REPLACE whatever
                // the try/catch above already decided to return, masking the real poll failure behind
                // an unrelated teardown error -- the exact hazard RadioController.cs's own
                // DisconnectAsync already guards against for the identical `protocol.DisposeAsync()`
                // call ("a broken backend's own DisposeAsync must not abort the rest of the
                // teardown"). Contained here the same way, not propagated.
                try
                {
                    await protocol.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.TestConnectionDisposeFailed(_logger, spec.GetType().Name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.TestConnectionFailed(_logger, spec.GetType().Name, ex);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
        }
    }

    private int _testPttInFlight;

    /// <summary>See <see cref="IRadioSessionService.TestPttAsync"/>'s own doc comment for the safety
    /// contract. Structured as a linear "compute a result, then always un-key/dispose" sequence
    /// rather than nested try/finally, specifically so the un-key-retry-exhausted case can override
    /// an already-computed success result before it's returned -- a `finally` block can't cleanly
    /// replace a value a `return` inside its `try` already committed to.</summary>
    public async Task<RadioConnectionTestResult> TestPttAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _testPttInFlight, 1, 0) != 0)
        {
            Log.TestPttAlreadyInFlight(_logger, spec.GetType().Name);
            return new RadioConnectionTestResult(false, null, RadioCapabilities.None, "A PTT test is already in progress.");
        }

        try
        {
            var matches = _protocolFactories.Where(f => f.CanHandle(spec)).ToList();
            if (matches.Count != 1)
            {
                var message = matches.Count == 0
                    ? $"No backend registered for {spec.GetType().Name}."
                    : $"{matches.Count} backends all claim {spec.GetType().Name} -- registration is ambiguous.";
                Log.TestPttResolutionFailed(_logger, spec.GetType().Name, matches.Count);
                return new RadioConnectionTestResult(false, null, RadioCapabilities.None, message);
            }

            try
            {
                var protocol = matches[0].Create(spec);
                // Round-review finding (radio-safety-critical): must be set BEFORE the SetPttAsync
                // call, not after it returns -- rig_set_ptt can return a non-OK code AFTER the rig
                // has already physically keyed (confirmed against hamlib/src/rig.c: a VFO-revert
                // call after the real PTT-on command can fail and become the returned error). Setting
                // this only on a successful return skipped the un-key retry on exactly that path,
                // the one case blocker #2 exists to cover. Cost of the false-positive (SetPttAsync
                // throws before ever reaching the rig) is one harmless extra PTT-OFF attempt; cost of
                // the false-negative this replaces was a stuck-keyed transmitter.
                var keyAttempted = false;
                RadioConnectionTestResult result;
                try
                {
                    await protocol.PollAsync(ct).ConfigureAwait(false);

                    if (!protocol.Capabilities.HasFlag(RadioCapabilities.PttControl))
                    {
                        Log.TestPttNoCapability(_logger, protocol.RigId);
                        result = new RadioConnectionTestResult(false, protocol.RigId, protocol.Capabilities, "This rig model has no PTT control.");
                    }
                    else
                    {
                        keyAttempted = true;
                        await protocol.SetPttAsync(true, ct).ConfigureAwait(false);

                        try
                        {
                            await Task.Delay(duration, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected -- the operator clicked Stop, or the dialog closed. Un-keying
                            // below still runs regardless.
                        }

                        Log.TestPttSucceeded(_logger, protocol.RigId);
                        result = new RadioConnectionTestResult(true, protocol.RigId, protocol.Capabilities, null);
                    }
                }
                catch (Exception ex)
                {
                    Log.TestPttFailed(_logger, spec.GetType().Name, ex);
                    result = new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
                }

                if (keyAttempted)
                {
                    var unkeyed = await TryUnkeyWithRetryAsync(protocol).ConfigureAwait(false);
                    if (!unkeyed)
                    {
                        result = result with
                        {
                            Success = false,
                            ErrorMessage = "PTT test finished, but turning PTT back off failed -- check your rig; it may still be transmitting.",
                        };
                    }
                }

                try
                {
                    await protocol.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.TestPttDisposeFailed(_logger, spec.GetType().Name, ex);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.TestPttFailed(_logger, spec.GetType().Name, ex);
                return new RadioConnectionTestResult(false, null, RadioCapabilities.None, ex.Message);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _testPttInFlight, 0);
        }
    }

    /// <summary>Retries the un-key call up to 3 times, always on <see cref="CancellationToken.None"/>
    /// so neither the caller's own cancellation nor a Stop click can abort it -- see
    /// <see cref="TestPttAsync"/>'s own doc comment for why CAT ("RIG") PTT specifically has no
    /// dispose-time rescue path.</summary>
    private async Task<bool> TryUnkeyWithRetryAsync(IRadioProtocol protocol)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await protocol.SetPttAsync(false, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Log.TestPttUnkeyFailed(_logger, protocol.RigId, ex);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        return false;
    }

    public Task DisconnectAsync() => _controller.DisconnectAsync();

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => _controller.SetFrequencyAsync(hz, ct);

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => _controller.SetModeAsync(mode, ct);

    public Task SetPttAsync(bool tx, CancellationToken ct = default) => _controller.SetPttAsync(tx, ct);

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct = default) => _controller.SetBandwidthAsync(bandwidthHz, ct);

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestConnectionAsync: disposing the throwaway test protocol for {SpecType} failed; the poll result above still stands")]
        public static partial void TestConnectionDisposeFailed(ILogger logger, string specType, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: rejected for {SpecType} -- a PTT test is already in progress")]
        public static partial void TestPttAlreadyInFlight(ILogger logger, string specType);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: factory resolution failed for {SpecType}: {MatchCount} match(es)")]
        public static partial void TestPttResolutionFailed(ILogger logger, string specType, int matchCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: rig {RigId} reports no PTT control capability -- not attempting to key")]
        public static partial void TestPttNoCapability(ILogger logger, string rigId);

        [LoggerMessage(Level = LogLevel.Information, Message = "TestPttAsync succeeded: rigId={RigId}")]
        public static partial void TestPttSucceeded(ILogger logger, string rigId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync failed for {SpecType}")]
        public static partial void TestPttFailed(ILogger logger, string specType, Exception exception);

        [LoggerMessage(Level = LogLevel.Critical, Message = "TestPttAsync: un-keying rig {RigId} failed after every retry -- it may still be physically transmitting")]
        public static partial void TestPttUnkeyFailed(ILogger logger, string rigId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPttAsync: disposing the throwaway test protocol for {SpecType} failed; the test result above still stands")]
        public static partial void TestPttDisposeFailed(ILogger logger, string specType, Exception exception);
    }
}
