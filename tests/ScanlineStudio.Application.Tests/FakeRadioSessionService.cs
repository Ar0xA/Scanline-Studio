using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeRadioSessionService : IRadioSessionService
{
    public List<bool> PttCalls { get; } = [];

    public RadioState? LastKnownState => null;

    public RadioCapabilities Capabilities { get; set; } = RadioCapabilities.None;

    /// <summary>Defaults to a non-"none" value so every EXISTING test that constructs this fake
    /// with no explicit override keeps exercising PTT keying/unkeying exactly as before
    /// (spec/18-path-to-1.0.md Critical item 1 -- <c>SstvSessionService.PlayWithPttAsync</c>'s new
    /// guard skips keying specifically when this equals <c>"none"</c>). Set explicitly to
    /// <c>"none"</c> only in the new tests that exercise the no-radio-configured path.</summary>
    public string RigId { get; set; } = "fake-radio";

    public IObservable<RadioState> StateChanges { get; } = System.Reactive.Linq.Observable.Never<RadioState>();

    public IObservable<RadioConnectionEvent> ConnectionEvents { get; } = System.Reactive.Linq.Observable.Never<RadioConnectionEvent>();

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public RadioConnectionTestResult TestConnectionResultToReturn { get; set; } = new(true, "fake-rig", RadioCapabilities.None, null);

    public List<RadioConnectionSpec> TestConnectionCalls { get; } = [];

    public Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default)
    {
        TestConnectionCalls.Add(spec);
        return Task.FromResult(TestConnectionResultToReturn);
    }

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
        // Mirrors NoneRadioProtocol.SetPttAsync's real always-throws behavior when RigId == "none"
        // (spec/18-path-to-1.0.md Critical item 1, round-1 plan-review's own explicit "add a
        // ThrowOnSetPtt-shaped guard" recommendation) -- without this, the fake couldn't verify the
        // real end-to-end claim that PlayWithPttAsync's UNGUARDED cleanup un-key call is still safe
        // against the real null-object backend (it's wrapped in TryCleanupAsync, which needs
        // something to actually catch to prove anything).
        if (RigId == "none")
        {
            throw new InvalidOperationException("No radio is connected -- nothing to key PTT on.");
        }

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
