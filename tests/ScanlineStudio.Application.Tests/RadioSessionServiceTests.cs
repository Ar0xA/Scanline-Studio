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
}
