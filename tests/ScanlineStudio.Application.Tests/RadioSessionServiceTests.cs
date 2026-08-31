using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

public sealed class RadioSessionServiceTests
{
    [Fact]
    public async Task ConnectUsingSettingsAsync_NoSectionConfigured_ConnectsWithNoneConnectionSpec()
    {
        var controller = new FakeRadioController();
        var settingsStore = new FakeSettingsStore();
        var service = new RadioSessionService(controller, settingsStore, [], NullLogger<RadioSessionService>.Instance);

        await service.ConnectUsingSettingsAsync();

        var spec = Assert.Single(controller.ConnectCalls);
        Assert.IsType<NoneConnectionSpec>(spec);
    }

    [Fact]
    public async Task ConnectUsingSettingsAsync_RigctldConfigured_ConnectsWithRigctldConnectionSpec()
    {
        var controller = new FakeRadioController();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RadioConnectionSettings.SectionKey,
                new RadioConnectionSettings { BackendId = "rigctld", Host = "127.0.0.1", Port = 4532 },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var service = new RadioSessionService(controller, settingsStore, [], NullLogger<RadioSessionService>.Instance);

        await service.ConnectUsingSettingsAsync();

        var spec = Assert.IsType<RigctldConnectionSpec>(Assert.Single(controller.ConnectCalls));
        Assert.Equal("127.0.0.1", spec.Host);
        Assert.Equal(4532, spec.Port);
    }

    [Fact]
    public async Task SetPttAsync_DelegatesToController()
    {
        var controller = new FakeRadioController();
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [], NullLogger<RadioSessionService>.Instance);

        await service.SetPttAsync(true);
        await service.SetPttAsync(false);

        Assert.Equal([true, false], controller.PttCalls);
    }

    [Fact]
    public void RigId_DelegatesToController()
    {
        var controller = new FakeRadioController { RigId = "elecraft-k3" };
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [], NullLogger<RadioSessionService>.Instance);

        Assert.Equal("elecraft-k3", service.RigId);
    }

    // Auditor usability review follow-up (2026-08-18): a settings-dialog "Test Connection" button.
    // Deliberately resolves/polls a FRESH, disposable IRadioProtocol, never the real controller
    // session -- ConnectCalls must stay empty across every one of these, that's the whole point.

    [Fact]
    public async Task TestConnectionAsync_Succeeds_ReturnsRigIdAndCapabilitiesAndDisposesTheProtocol()
    {
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { RigId = "rigctld-client", Capabilities = RadioCapabilities.ReadFrequency | RadioCapabilities.PttControl };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestConnectionAsync(spec);

        Assert.True(result.Success);
        Assert.Equal("rigctld-client", result.RigId);
        Assert.Equal(RadioCapabilities.ReadFrequency | RadioCapabilities.PttControl, result.Capabilities);
        Assert.Null(result.ErrorMessage);
        Assert.True(protocol.Disposed);
        Assert.Empty(controller.ConnectCalls);
    }

    [Fact]
    public async Task TestConnectionAsync_PollThrows_ReturnsFailureWithMessageAndStillDisposesTheProtocol()
    {
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { PollExceptionToThrow = new InvalidOperationException("connection refused") };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestConnectionAsync(spec);

        Assert.False(result.Success);
        Assert.Null(result.RigId);
        Assert.Equal("connection refused", result.ErrorMessage);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestConnectionAsync_PollThrowsAndDisposeAlsoThrows_ReportsTheOriginalPollFailure()
    {
        // Closes a coverage gap flagged by Tier A Batch 10 chunk 10c (docs/functional-audit-playbook.md):
        // a throwing DisposeAsync inside the finally block used to REPLACE whatever the try/catch
        // above had already decided to return -- masking the real poll failure behind an unrelated
        // teardown error. The double-fault case is the only thing that actually distinguishes "the
        // finally block's exception propagates" from "it's caught and logged, the original result
        // stands" -- a single-fault test (poll only) can't tell the two apart.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol
        {
            PollExceptionToThrow = new InvalidOperationException("connection refused"),
            DisposeExceptionToThrow = new IOException("handle already closed"),
        };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestConnectionAsync(spec);

        Assert.False(result.Success);
        Assert.Null(result.RigId);
        Assert.Equal("connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_FactoryCreateThrows_ReturnsFailure_DoesNotPropagate()
    {
        // Closes a coverage gap flagged by Tier A Batch 10 chunk 10c: matches[0].Create(spec) used
        // to run OUTSIDE the try/catch -- a throwing factory leaked past this method's own interface
        // contract (IRadioSessionService.TestConnectionAsync's doc comment says it always returns a
        // result, never throws).
        var controller = new FakeRadioController();
        var factory = new FakeRadioProtocolFactory(new FakeRadioProtocol())
        {
            CreateExceptionToThrow = new InvalidOperationException("factory misconfigured"),
        };
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestConnectionAsync(spec);

        Assert.False(result.Success);
        Assert.Null(result.RigId);
        Assert.Equal("factory misconfigured", result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_NoFactoryRegisteredForSpec_ReturnsFailure()
    {
        var controller = new FakeRadioController();
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestConnectionAsync(spec);

        Assert.False(result.Success);
        Assert.Contains("No backend registered", result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_AmbiguousFactoryRegistration_ReturnsFailure()
    {
        var controller = new FakeRadioController();
        var factoryA = new FakeRadioProtocolFactory(new FakeRadioProtocol());
        var factoryB = new FakeRadioProtocolFactory(new FakeRadioProtocol());
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factoryA, factoryB], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestConnectionAsync(spec);

        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.ErrorMessage);
    }

    [Fact]
    public async Task GetSafetySettingsAsync_NoSectionConfigured_ReturnsDefaults()
    {
        // Tier B audit finding: zero test coverage existed for the safety-settings round-trip
        // (GetSafetySettingsAsync/SaveSafetySettingsAsync) before this -- correct by inspection, but
        // nothing guarded a future regression (e.g. adding a third field, wired only one direction).
        var controller = new FakeRadioController();
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [], NullLogger<RadioSessionService>.Instance);

        var spec = await service.GetSafetySettingsAsync();

        Assert.False(spec.SwrCutoffEnabled);
        Assert.Equal(RadioSafetySpec.DefaultSwrCutoffThreshold, spec.SwrCutoffThreshold);
    }

    [Fact]
    public async Task SaveSafetySettingsAsync_ThenGet_RoundTripsBothFields()
    {
        var controller = new FakeRadioController();
        var settingsStore = new FakeSettingsStore();
        var service = new RadioSessionService(controller, settingsStore, [], NullLogger<RadioSessionService>.Instance);

        await service.SaveSafetySettingsAsync(new RadioSafetySpec(true, 2.5));
        var spec = await service.GetSafetySettingsAsync();

        Assert.True(spec.SwrCutoffEnabled);
        Assert.Equal(2.5, spec.SwrCutoffThreshold);
    }

    [Fact]
    public async Task SaveSafetySettingsAsync_RaisesSafetySettingsChanged_WithTheSavedValue()
    {
        // Code-review finding: TxControlsPaneViewModel's own live-propagation coverage
        // (SafetySettingsChanged_FiredDuringActiveTransmit...) only drives the TEST FAKE's raise --
        // without this test, deleting the real SafetySettingsChanged?.Invoke(spec) line in
        // RadioSessionService.SaveSafetySettingsAsync would leave the whole suite green while live
        // propagation silently died in production.
        var controller = new FakeRadioController();
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [], NullLogger<RadioSessionService>.Instance);
        RadioSafetySpec? raised = null;
        service.SafetySettingsChanged += spec => raised = spec;

        await service.SaveSafetySettingsAsync(new RadioSafetySpec(true, 4.2));

        Assert.NotNull(raised);
        Assert.True(raised!.SwrCutoffEnabled);
        Assert.Equal(4.2, raised.SwrCutoffThreshold);
    }

    [Fact]
    public async Task SaveSafetySettingsAsync_ThrowingSubscriber_DoesNotPropagate_AndSettingsAlreadyPersisted()
    {
        // T1-7 (production_audit.md): the disk write completes before SafetySettingsChanged is
        // raised -- a throwing subscriber (a real one exists, TxControlsPaneViewModel.OnSafetySettingsChanged)
        // used to propagate straight out of this method, which OptionsWindowViewModel's own caller
        // wraps in one big multi-section Save/Apply try/catch, reporting the WHOLE save as failed
        // even though this specific write already landed.
        var controller = new FakeRadioController();
        var settingsStore = new FakeSettingsStore();
        var service = new RadioSessionService(controller, settingsStore, [], NullLogger<RadioSessionService>.Instance);
        service.SafetySettingsChanged += _ => throw new InvalidOperationException("subscriber exploded");

        await service.SaveSafetySettingsAsync(new RadioSafetySpec(true, 3.1));
        var spec = await service.GetSafetySettingsAsync();

        Assert.True(spec.SwrCutoffEnabled);
        Assert.Equal(3.1, spec.SwrCutoffThreshold);
    }

    // Options-dialog "Test PTT" button. Radio-safety-sensitive: every one of these proves the rig
    // ends up un-keyed (or the operator is told it might not be), never silently left keyed.

    [Fact]
    public async Task TestPttAsync_Succeeds_KeysWaitsThenUnkeysAndDisposes()
    {
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { RigId = "rigctld-client", Capabilities = RadioCapabilities.PttControl };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        Assert.True(result.Success);
        Assert.Equal("rigctld-client", result.RigId);
        Assert.Null(result.ErrorMessage);
        Assert.Equal([true, false], protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
        Assert.Empty(controller.ConnectCalls);
    }

    [Fact]
    public async Task TestPttAsync_CancelledDuringWait_StillUnkeysAndReportsSuccess()
    {
        // A Stop click (or the dialog closing) cancels the wait early -- normal control flow, same
        // philosophy as OptionsWindowViewModel.TuneAsync's own Stop handling, not a failure. The
        // un-key must still run regardless.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromSeconds(30), new CancellationToken(canceled: true));

        Assert.True(result.Success);
        Assert.Equal([true, false], protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestPttAsync_KeyingThrows_StillAttemptsUnkeyThenDisposes()
    {
        // Code-review finding: rig_set_ptt can return a non-OK code AFTER the rig has already
        // physically keyed (confirmed against hamlib/src/rig.c -- a VFO-revert call after the real
        // PTT-on command can fail and become the returned error), so a thrown SetPttAsync(true,...)
        // does NOT mean the rig was never keyed. The un-key attempt must still run -- skipping it
        // (this test's original, wrong expectation) is exactly the stuck-transmitter hazard this
        // whole method exists to close.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        protocol.SetPttExceptionsToThrow.Enqueue(new InvalidOperationException("key failed"));
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        // The un-key attempt itself succeeds (nothing scripted to fail it), so the ORIGINAL key
        // failure is what's reported -- the un-key-failed message only overrides this when the
        // un-key attempt(s) ALSO fail (see TestPttAsync_UnkeyFailsEveryRetry_... below).
        Assert.False(result.Success);
        Assert.Equal("key failed", result.ErrorMessage);
        Assert.Equal([true, false], protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestPttAsync_UnkeyFailsEveryRetry_ReturnsFailureWithUnkeyMessage()
    {
        // rig_close (Hamlib) doesn't rescue CAT PTT on dispose -- if the explicit un-key call itself
        // fails every retry, this must be reported to the operator, never silently swallowed.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        protocol.SetPttExceptionsToThrow.Enqueue(null); // key succeeds
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 1 failed"));
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 2 failed"));
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 3 failed"));
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        Assert.False(result.Success);
        Assert.Contains("check your rig", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([true, false, false, false], protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestPttAsync_UnkeyAttempt1Hangs_Attempts2And3StillRunWithinABound()
    {
        // TT0-2/T0-1: the retry loop's actual pre-fix failure mode was a HANG (Hamlib's semaphore
        // wait had no timeout), not a throw -- SetPttExceptionsToThrow can only script a throw, so
        // this test needs HangOnCallNumber instead. Call 1 = the initial key. Call 2 = un-key
        // attempt 1, which hangs forever. Call 3 = un-key attempt 2, scripted to fail (proving it
        // genuinely ran, not just short-circuited past the hung call). Call 4 = un-key attempt 3,
        // left to succeed on the now-empty queue.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        protocol.SetPttExceptionsToThrow.Enqueue(null); // key succeeds
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey retry 2 failed"));
        protocol.HangOnCallNumber = 2;
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(
            controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance,
            unkeyAttemptWaitTimeoutForTests: TimeSpan.FromMilliseconds(50));
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        // The hung call (attempt 1) never reaches CompleteSetPtt, so it's never recorded -- only the
        // key plus the two attempts that actually ran (one scripted failure, one success) are.
        Assert.Equal([true, false, false], protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestPttAsync_KeyingThrowsAndEveryUnkeyRetryAlsoFails_ReturnsUnkeyMessageNotKeyMessage()
    {
        // The exact scenario code review flagged: SetPttAsync(true) throws (rig.c confirms this can
        // happen AFTER the rig is already physically keyed), and the rescue un-key attempt ALSO
        // fails every retry -- the un-key failure must win over the original key error, since a
        // possibly-still-transmitting rig is the more urgent fact to surface.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        protocol.SetPttExceptionsToThrow.Enqueue(new InvalidOperationException("key failed"));
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 1 failed"));
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 2 failed"));
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 3 failed"));
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        Assert.False(result.Success);
        Assert.Contains("check your rig", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([true, false, false, false], protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestPttAsync_UnkeyFailsThenSucceeds_ReturnsSuccess()
    {
        // Proves the retry loop genuinely retries, not just attempts once.
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        protocol.SetPttExceptionsToThrow.Enqueue(null); // key succeeds
        protocol.SetPttExceptionsToThrow.Enqueue(new IOException("unkey 1 failed"));
        protocol.SetPttExceptionsToThrow.Enqueue(null); // unkey 2 succeeds
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        Assert.True(result.Success);
        Assert.Equal([true, false, false], protocol.SetPttCalls);
    }

    [Fact]
    public async Task TestPttAsync_NoPttCapability_ReturnsFailureWithoutKeying()
    {
        var controller = new FakeRadioController();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.ReadFrequency };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        Assert.False(result.Success);
        Assert.Contains("no PTT control", result.ErrorMessage);
        Assert.Empty(protocol.SetPttCalls);
        Assert.True(protocol.Disposed);
    }

    [Fact]
    public async Task TestPttAsync_NoFactoryRegisteredForSpec_ReturnsFailure()
    {
        var controller = new FakeRadioController();
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var result = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        Assert.False(result.Success);
        Assert.Contains("No backend registered", result.ErrorMessage);
    }

    [Fact]
    public async Task TestPttAsync_ConcurrentCalls_SecondCallIsRejectedBySingleFlightGuard()
    {
        var controller = new FakeRadioController();
        var gate = new TaskCompletionSource();
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl, PollGate = gate.Task };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [factory], NullLogger<RadioSessionService>.Instance);
        var spec = new RigctldConnectionSpec("127.0.0.1", 4532);

        var firstCall = service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));

        var secondResult = await service.TestPttAsync(spec, TimeSpan.FromMilliseconds(10));
        Assert.False(secondResult.Success);
        Assert.Contains("already in progress", secondResult.ErrorMessage);

        gate.SetResult();
        var firstResult = await firstCall;
        Assert.True(firstResult.Success);
    }

    // Restart-required-settings backlog item 5 (2026-08-28): code-review round-1 finding --
    // RequestHamlibLibraryPathAsync's own OfType<IHamlibLibraryReconfiguration>().FirstOrDefault()
    // resolution across the registered factories was previously untested.

    [Fact]
    public async Task RequestHamlibLibraryPathAsync_ReconfigurableFactoryRegistered_ForwardsTheCall()
    {
        var controller = new FakeRadioController();
        var rigctldFactory = new FakeRadioProtocolFactory(new FakeRadioProtocol()); // does NOT implement the reconfiguration side-channel
        var hamlibFactory = new FakeHamlibReconfigurableProtocolFactory
        {
            ResultToReturn = new HamlibLibraryReloadResult(true, "/opt/homebrew/lib/libhamlib.4.dylib", "Hamlib 4.6.0", []),
        };
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [rigctldFactory, hamlibFactory], NullLogger<RadioSessionService>.Instance);

        var result = await service.RequestHamlibLibraryPathAsync("/opt/homebrew/lib/libhamlib.4.dylib");

        Assert.NotNull(result);
        Assert.True(result.Applied);
        Assert.Equal(1, hamlibFactory.ReloadLibraryCallCount);
        Assert.Equal("/opt/homebrew/lib/libhamlib.4.dylib", hamlibFactory.LastRequestedOverridePath);
    }

    [Fact]
    public async Task RequestHamlibLibraryPathAsync_NoReconfigurableFactoryRegistered_ReturnsNull()
    {
        var controller = new FakeRadioController();
        var rigctldFactory = new FakeRadioProtocolFactory(new FakeRadioProtocol());
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [rigctldFactory], NullLogger<RadioSessionService>.Instance);

        var result = await service.RequestHamlibLibraryPathAsync("/some/path.so");

        Assert.Null(result); // matches every other optional-side-channel convention in this backlog
    }

    // Code-review round-2 finding: RequestHamlibLibraryPathAsync's Applied:false/rejected arm
    // (ReloadHamlibLibraryAsync's own `else` branch, RadioSessionService.cs:313-316) and its
    // FirstOrDefault-among-many resolution were untested at this layer.

    [Fact]
    public async Task RequestHamlibLibraryPathAsync_ReloadRejected_ReturnsAppliedFalseResult()
    {
        var controller = new FakeRadioController();
        var hamlibFactory = new FakeHamlibReconfigurableProtocolFactory
        {
            ResultToReturn = new HamlibLibraryReloadResult(false, "/bad/path.so", null, ["candidate not found"]),
        };
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [hamlibFactory], NullLogger<RadioSessionService>.Instance);

        var result = await service.RequestHamlibLibraryPathAsync("/bad/path.so");

        // Proves the else branch (the rejected-logging arm) runs to completion and surfaces the
        // rejected result unchanged, rather than throwing or silently dropping the failure detail.
        Assert.NotNull(result);
        Assert.False(result.Applied);
        Assert.Equal("/bad/path.so", result.ResolvedPath);
        Assert.Contains("candidate not found", result.Attempts);
    }

    [Fact]
    public async Task RequestHamlibLibraryPathAsync_MultipleReconfigurableFactoriesRegistered_UsesOnlyTheFirst()
    {
        var controller = new FakeRadioController();
        var firstFactory = new FakeHamlibReconfigurableProtocolFactory
        {
            ResultToReturn = new HamlibLibraryReloadResult(true, "/first/libhamlib.so", "Hamlib 4.5.5", []),
        };
        var secondFactory = new FakeHamlibReconfigurableProtocolFactory
        {
            ResultToReturn = new HamlibLibraryReloadResult(true, "/second/libhamlib.so", "Hamlib 4.6.0", []),
        };
        var service = new RadioSessionService(controller, new FakeSettingsStore(), [firstFactory, secondFactory], NullLogger<RadioSessionService>.Instance);

        var result = await service.RequestHamlibLibraryPathAsync("/some/path.so");

        // FirstOrDefault picks registration order -- only the first-registered reconfigurable
        // factory is ever called, never both.
        Assert.NotNull(result);
        Assert.Equal("/first/libhamlib.so", result.ResolvedPath);
        Assert.Equal(1, firstFactory.ReloadLibraryCallCount);
        Assert.Equal(0, secondFactory.ReloadLibraryCallCount);
    }
}
