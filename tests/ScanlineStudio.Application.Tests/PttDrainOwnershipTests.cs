using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>Shutdown-ownership regressions for the PTT-test drain. The host calls the drain
/// directly and the DI container then disposes the same singleton a second time, so two properties
/// have to hold together: shutdown must refuse a new PTT test, and the cached task must never
/// fault.</summary>
public sealed class PttDrainOwnershipTests
{
    private static RadioSessionService CreateService(FakeRadioProtocol protocol, ILogger<RadioSessionService>? logger = null) =>
        new(new FakeRadioController(), new FakeSettingsStore(), [new FakeRadioProtocolFactory(protocol)],
            logger ?? NullLogger<RadioSessionService>.Instance);

    private static RigctldConnectionSpec Spec => new("localhost", 4532);

    [Fact]
    public async Task DrainPttTestsAsync_ThenTestPttAsync_IsRefusedBeforeAnyProtocolIsOpened()
    {
        // The admission gate has to key on a flag the DRAIN sets, because the host's exit path calls
        // the drain rather than DisposeAsync. Asserting on the result alone would prove nothing: the
        // drain also cancels the shutdown token, and every test links to it, so an ADMITTED test
        // would fail and never key either. What only a closed gate gives is refusal before the
        // backend is opened at all -- no factory call, so no connection and no protocol to dispose.
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        var factory = new FakeRadioProtocolFactory(protocol);
        var service = new RadioSessionService(new FakeRadioController(), new FakeSettingsStore(),
            [factory], NullLogger<RadioSessionService>.Instance);

        Assert.True(await service.DrainPttTestsAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        var result = await service.TestPttAsync(Spec, TimeSpan.Zero);
        Assert.False(result.Success);
        Assert.Empty(factory.CreateCalls);
        Assert.Empty(protocol.SetPttCalls);
    }

    [Fact]
    public async Task DrainPttTestsAsync_RepeatedCalls_ShareOneTaskWithDisposeAsync()
    {
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl };
        var service = CreateService(protocol);

        var drain = service.DrainPttTestsAsync();
        Assert.Same(drain, service.DrainPttTestsAsync());
        Assert.Same(drain, service.DisposeAsync().AsTask());
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DrainPttTestsAsync_BudgetExpires_ReportsFalseWithoutFaulting()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl, PollGate = gate.Task };
        var service = CreateService(protocol);
        var testing = service.TestPttAsync(Spec, TimeSpan.Zero);

        var drain = service.DrainPttTestsAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(await drain.WaitAsync(TimeSpan.FromSeconds(5)));

        // The whole point: an expired budget reports false, it does not hand the caller a faulted
        // task. A second dispose observes that same completed task rather than rethrowing.
        Assert.Same(drain, service.DisposeAsync().AsTask());
        await service.DisposeAsync();

        gate.SetResult();
        await testing;
    }

    [Fact]
    public async Task DrainPttTestsAsync_ThrowingLogger_ReportsFalseWithoutFaulting()
    {
        // Microsoft.Extensions.Logging aggregates provider exceptions and rethrows them, so an
        // unguarded log call inside the drain would fault the cached task. Bounded by WaitAsync so
        // an unset completion fails the test instead of hanging the suite.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl, PollGate = gate.Task };
        var service = CreateService(protocol, new ThrowingLogger<RadioSessionService>());
        var testing = service.TestPttAsync(Spec, TimeSpan.Zero);

        var drain = service.DrainPttTestsAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(await drain.WaitAsync(TimeSpan.FromSeconds(5)));

        gate.SetResult();
        await testing;
    }

    [Fact]
    public async Task ContainerDispose_AfterExpiredDrain_StillDisposesTheRestOfTheChain()
    {
        // This is 00d's actual consequence, not just "no exception": the container abandons every
        // disposal it has not reached yet once one IAsyncDisposable faults. RadioSessionService takes
        // the tracker as a constructor dependency, so the tracker is created first and therefore
        // disposed last -- exactly the position the live radio connection occupies in production.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new FakeRadioProtocol { Capabilities = RadioCapabilities.PttControl, PollGate = gate.Task };
        var services = new ServiceCollection();
        // Registered by TYPE, not as a pre-built instance: the container only disposes what it
        // created itself, so an instance registration would never be disposed and the assertion
        // below would prove nothing.
        services.AddSingleton<DisposalTracker>();
        services.AddSingleton<IRadioSessionService>(sp =>
        {
            _ = sp.GetRequiredService<DisposalTracker>();
            return new RadioSessionService(new FakeRadioController(), new FakeSettingsStore(),
                [new FakeRadioProtocolFactory(protocol)], NullLogger<RadioSessionService>.Instance);
        });

        var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<DisposalTracker>();
        var service = (IPttTestDrain)provider.GetRequiredService<IRadioSessionService>();
        // Order matters: the test must already be in flight and wedged on its poll, so the drain's
        // budget genuinely expires. Draining first would find nothing to wait for and succeed.
        var pending = ((IRadioSessionService)service).TestPttAsync(Spec, TimeSpan.Zero);
        var drain = service.DrainPttTestsAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(await drain.WaitAsync(TimeSpan.FromSeconds(5)));

        await provider.DisposeAsync();

        Assert.True(tracker.Disposed);
        gate.SetResult();
        await pending;
    }

    private sealed class DisposalTracker : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    /// <summary>Throws only for the drain's own messages. A blanket throwing logger would also derail
    /// the PTT test path's unguarded logging, which is a different concern and would make the
    /// assertion here prove nothing about the drain.</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("shutdown", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("logger provider failed");
            }
        }
    }
}
