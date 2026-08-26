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

    public bool IsGenuinelyConnected { get; set; }

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

    public RadioConnectionTestResult TestPttResultToReturn { get; set; } = new(true, "fake-rig", RadioCapabilities.PttControl, null);

    public List<(RadioConnectionSpec Spec, TimeSpan Duration)> TestPttCalls { get; } = [];

    public Task<RadioConnectionTestResult> TestPttAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct = default)
    {
        TestPttCalls.Add((spec, duration));
        return Task.FromResult(TestPttResultToReturn);
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => Task.CompletedTask;

    public List<int?> SetBandwidthCalls { get; } = [];

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct = default)
    {
        SetBandwidthCalls.Add(bandwidthHz);
        return Task.CompletedTask;
    }

    /// <summary>Unlike the other members here, this one honors <paramref name="ct"/> (throwing if
    /// already cancelled) -- matching the real <c>RigctldClientProtocol</c>/<c>HamlibRadioProtocol</c>
    /// contract (both throw immediately from their own <c>_requestLock.WaitAsync(ct)</c> on an
    /// already-cancelled token) closely enough to actually distinguish
    /// <c>SstvSessionService.PlayWithPttAsync</c>'s cleanup path passing a fresh token from one that
    /// (incorrectly) reuses the possibly-cancelled transmit token -- see
    /// <c>SstvSessionServiceTests.TransmitAsync_TokenCancelledMidTransmit_StillUnkeysPttAndRestartsCapture</c>.</summary>
    /// <summary>Tier B audit finding (test-fidelity gap): when true, a RigId=="none" call throws
    /// SYNCHRONOUSLY before this method's own async state machine ever starts -- matching the
    /// real production chain's actual shape (RadioSessionService -> RadioController ->
    /// NoneRadioProtocol, none of which is `async`, so the throw happens before the caller's own
    /// `pttCommand` local is ever assigned). The plain async version below always returns SOME
    /// Task object to the caller, even for the immediate-throw case (a faulted one) -- so a test
    /// against the default (false) shape cannot distinguish "the command was dispatched and then
    /// failed" from "the command was never dispatched at all," the exact distinction
    /// SstvSessionService.SetPttLockAsync's own pttCommand-is-not-null guard depends on. Defaults
    /// to false so every EXISTING test (relying on the original async-fault shape) is unaffected.</summary>
    public bool ThrowSynchronouslyOnNoneRig { get; set; }

    public Task SetPttAsync(bool tx, CancellationToken ct = default)
    {
        if (ThrowSynchronouslyOnNoneRig && RigId == "none")
        {
            throw new InvalidOperationException("No radio is connected -- nothing to key PTT on.");
        }

        return SetPttAsyncCore(tx, ct);
    }

    private async Task SetPttAsyncCore(bool tx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var callNumber = Interlocked.Increment(ref _callCount);
        OnCallStarted?.Invoke(callNumber);
        if (HangOnCallNumber == callNumber)
        {
            // Round-14 test hook: hangs forever, never returning -- unlike Gate (below), this only
            // affects ONE specific call (1-based, across the whole fake's lifetime), so a test can
            // target exactly the key command while still observing a LATER cleanup un-key call
            // succeed normally. Gate cannot express this: it is one shared Task, so hanging it hangs
            // every call equally, including the cleanup un-key a hung-key-command test needs to prove
            // still gets through.
            await _neverCompletes.Task.ConfigureAwait(false);
        }

        if (Gate is not null && (GateOnCallNumber is null || GateOnCallNumber == callNumber))
        {
            // Round-18: respects `ct` (via Task.WaitAsync, not a plain await), matching
            // FakeAudioDeviceEnumerator.Gate's own round-10 upgrade -- needed so a test can
            // distinguish a caller that cancels this specific command from one that (correctly, per
            // round 18's own fix) never does. A no-op for every existing test passing
            // CancellationToken.None, which never cancels.
            await Gate.WaitAsync(ct).ConfigureAwait(false);
        }

        if (Gate2 is not null && GateOnCallNumber2 == callNumber)
        {
            // Round-30 test hook: a SECOND, independent gate -- needed when a test must control TWO
            // different calls' own release timing separately (e.g. an abandoned first call and a
            // second call it indirectly triggers, both of which need to be released at DIFFERENT,
            // deterministic points rather than simultaneously). Gate/GateOnCallNumber alone can't
            // express this: it is one shared Task, so both calls would always release together.
            await Gate2.WaitAsync(ct).ConfigureAwait(false);
        }

        BeforeSetPtt?.Invoke(tx);
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
    }

    /// <summary>Test-only hook, invoked BEFORE the RigId check below (and, round-9 nit: BEFORE this
    /// point, after <see cref="Gate"/> if that's also set -- not literally "the very start of
    /// <see cref="SetPttAsync"/>" once a test parks the call there first), so a test can mutate
    /// <see cref="RigId"/> from inside the call. That is exactly how the real blocker-2 race works
    /// (Tier A Batch 3 chunk 3a): <c>RadioController.DisconnectAsync</c>/<c>DisposeAsync</c> reset
    /// <c>_rigId</c> to <c>"none"</c> WITHOUT un-keying, so <see cref="RigId"/> can read <c>"none"</c>
    /// at the precise moment a genuinely-keyed rig's un-key fails.</summary>
    public Action<bool>? BeforeSetPtt { get; set; }

    /// <summary>Test-only hook (Tier A Batch 3 chunk 3a round 8): when set, <see cref="SetPttAsync"/>
    /// genuinely `await`s this before doing anything else -- unlike <see cref="BeforeSetPtt"/> (a
    /// synchronous callback that can't yield control back to the caller), this lets a test park a real
    /// in-flight <c>SetPttLockAsync</c>/<c>PlayWithPttAsync</c> call mid-command without blocking the
    /// calling thread, the same shape <c>FakeAudioDeviceEnumerator.Gate</c> already provides for device
    /// resolution.</summary>
    public Task? Gate { get; set; }

    /// <summary>Test-only hook (round 18): when set together with <see cref="Gate"/>, restricts the
    /// gate to that one 1-based call number only, leaving every other call unaffected -- unlike
    /// <see cref="HangOnCallNumber"/> (a gate that can never be resolved), this one CAN be completed
    /// later by a test, letting it prove a call that was gated past its own timeout still eventually
    /// reaches completion rather than being abandoned/cancelled. `null` (the default) keeps
    /// <see cref="Gate"/>'s original behavior of applying to every call.</summary>
    public int? GateOnCallNumber { get; set; }

    /// <summary>Test-only hook (round 30): a SECOND, independent gate -- see <see cref="SetPttAsync"/>'s
    /// own comment for why this exists separately from <see cref="Gate"/>. Unlike <see cref="Gate"/>,
    /// this one requires <see cref="GateOnCallNumber2"/> to be set (no "applies to every call"
    /// default) -- it exists specifically to target one OTHER call independently of whatever
    /// <see cref="Gate"/>/<see cref="GateOnCallNumber"/> is already targeting.</summary>
    public Task? Gate2 { get; set; }

    /// <summary>Test-only hook (round 30): the 1-based call number <see cref="Gate2"/> applies to. See
    /// <see cref="Gate2"/>'s own comment.</summary>
    public int? GateOnCallNumber2 { get; set; }

    /// <summary>Test-only hook (Tier A Batch 3 chunk 3a round 14): 1-based call number of
    /// <see cref="SetPttAsync"/> that should hang forever instead of completing. See
    /// <see cref="SetPttAsync"/>'s own comment for why this exists separately from
    /// <see cref="Gate"/>.</summary>
    public int? HangOnCallNumber { get; set; }

    /// <summary>Test-only hook (round 28): invoked with the 1-based call number the INSTANT
    /// <see cref="SetPttAsync"/> is entered, before any <see cref="HangOnCallNumber"/>/<see cref="Gate"/>
    /// check. Needed because this fake has no real-time pacing -- a test racing a SECOND, independent
    /// call (e.g. <c>SetPttLockAsync</c>) against a GATED first call cannot otherwise tell "the first
    /// call has genuinely reached and is now parked at its own gate" from external polling alone, the
    /// same problem <c>SignalingGatedStopPlaybackAudioEngine</c> (round 25) solves for the audio engine.
    /// Gives a deterministic happens-before point to synchronize on instead.</summary>
    public Action<int>? OnCallStarted { get; set; }

    private int _callCount;
    private readonly TaskCompletionSource _neverCompletes = new();

    public IReadOnlyList<FrequencyPreset> Presets { get; set; } = [];

    public Task<IReadOnlyList<FrequencyPreset>> GetFrequencyPresetsAsync(CancellationToken ct = default) => Task.FromResult(Presets);

    public Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default)
    {
        Presets = presets;
        return Task.CompletedTask;
    }

    public RadioSafetySpec SafetySpec { get; set; } = new(false, RadioSafetySpec.DefaultSwrCutoffThreshold);

    public Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default) => Task.FromResult(SafetySpec);

    public Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default)
    {
        SafetySpec = spec;
        SafetySettingsChanged?.Invoke(spec);
        return Task.CompletedTask;
    }

    public event Action<RadioSafetySpec>? SafetySettingsChanged;

    /// <summary>Test-only helper mirroring <see cref="SaveSafetySettingsAsync"/>'s own event raise --
    /// lets a test simulate a live Options-side edit WITHOUT going through the fake's own save path
    /// (e.g. to prove a subscriber picks up the change without a settings-file round trip involved).</summary>
    public void RaiseSafetySettingsChanged(RadioSafetySpec spec) => SafetySettingsChanged?.Invoke(spec);
}
