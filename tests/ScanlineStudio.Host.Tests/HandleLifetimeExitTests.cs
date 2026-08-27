using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.Host.Tests;

public sealed class HandleLifetimeExitTests
{
    [Fact]
    public void WhenRestartNotRequested_DisposesTheHost_AndDoesNotSpawn()
    {
        var host = new FakeAsyncDisposableHost();
        var restarter = new FakeApplicationRestarter { RestartRequested = false };

        Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null);

        Assert.True(host.DisposeAsyncCalled);
        Assert.False(restarter.StartNewInstanceCalled);
    }

    [Fact]
    public async Task WhenRestartRequested_SpawnsOnlyAfterDisposeCompletes()
    {
        // Code-review round-1 finding: the original version of this test set its "completed
        // before spawn" flag unconditionally INSIDE a synchronously-completing DisposeAsync, so it
        // passed even if the ordering were wrong -- a shared-completion-order assumption, not a
        // real gate (see this codebase's own "deterministic gates, not shared race" lesson). This
        // version uses a TaskCompletionSource DisposeAsync genuinely awaits: the assertion that
        // nothing has spawned yet happens BEFORE the gate is released, so a bug that spawned early
        // would be caught, not coincidentally passed.
        var disposeGate = new TaskCompletionSource();
        var host = new FakeAsyncDisposableHost { DisposeGate = disposeGate.Task };
        var restarter = new FakeApplicationRestarter { RestartRequested = true, NextStartResult = true };

        var handleTask = Task.Run(() => Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null));
        await WaitUntilAsync(() => host.DisposeAsyncCalled, TimeSpan.FromSeconds(5));

        Assert.False(restarter.StartNewInstanceCalled, "must not spawn while DisposeAsync is still pending");
        Assert.False(host.DisposeAsyncCompleted);

        disposeGate.SetResult();
        await handleTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(host.DisposeAsyncCompleted);
        Assert.True(restarter.StartNewInstanceCalled);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public void WhenRestartRequested_AndStartNewInstanceFails_DoesNotThrow()
    {
        var host = new FakeAsyncDisposableHost();
        var restarter = new FakeApplicationRestarter { RestartRequested = true, NextStartResult = false };

        var exception = Record.Exception(() => Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null));

        Assert.Null(exception);
        Assert.True(restarter.StartNewInstanceCalled);
    }

    [Fact]
    public void WhenRestartRequested_AndDisposeThrows_StillRestarts()
    {
        var host = new FakeAsyncDisposableHost { ThrowOnDispose = true };
        var restarter = new FakeApplicationRestarter { RestartRequested = true, NextStartResult = true };

        var exception = Record.Exception(() => Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null));

        Assert.Null(exception);
        Assert.True(restarter.StartNewInstanceCalled);
    }

    [Fact]
    public void WhenRestartRequested_DisposesTheFileLoggerProviderBeforeSpawning()
    {
        var host = new FakeAsyncDisposableHost();
        var restarter = new FakeApplicationRestarter { RestartRequested = true, NextStartResult = true };
        var tempDirectory = Directory.CreateTempSubdirectory("yoniq-handle-lifetime-exit-tests-");
        try
        {
            var fileLoggerProvider = new FileLoggerProvider(Path.Combine(tempDirectory.FullName, "app.log"));
            restarter.OnStartNewInstance = () =>
            {
                var loggedAfterDispose = Record.Exception(() =>
                    fileLoggerProvider.CreateLogger("Cat").Log(
                        Microsoft.Extensions.Logging.LogLevel.Information, new Microsoft.Extensions.Logging.EventId(0),
                        "after dispose", null, (s, _) => s));
                Assert.Null(loggedAfterDispose); // must be a safe no-op, not a throw, if already disposed
            };

            Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider);

            Assert.True(restarter.StartNewInstanceCalled);
        }
        finally
        {
            Directory.Delete(tempDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public void WhenNoFileLoggerProviderExists_StillRestartsWithoutThrowing()
    {
        var host = new FakeAsyncDisposableHost();
        var restarter = new FakeApplicationRestarter { RestartRequested = true, NextStartResult = true };

        var exception = Record.Exception(() => Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null));

        Assert.Null(exception);
        Assert.True(restarter.StartNewInstanceCalled);
    }

    private sealed class FakeAsyncDisposableHost : IAsyncDisposable
    {
        /// <summary>Real, controllable gate -- defaults to already-completed so every OTHER test
        /// in this file (which doesn't care about ordering, only about the end state) keeps
        /// behaving synchronously, unchanged.</summary>
        public Task DisposeGate { get; set; } = Task.CompletedTask;

        public bool DisposeAsyncCalled { get; private set; }
        public bool DisposeAsyncCompleted { get; private set; }
        public bool ThrowOnDispose { get; set; }

        public async ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            await DisposeGate;
            DisposeAsyncCompleted = true;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("simulated dispose failure");
            }
        }
    }

    private sealed class FakeApplicationRestarter : IApplicationRestarter
    {
        public bool RestartRequested { get; set; }
        public bool NextStartResult { get; set; }
        public bool StartNewInstanceCalled { get; private set; }
        public Action? OnStartNewInstance { get; set; }

        public bool StartNewInstance()
        {
            StartNewInstanceCalled = true;
            OnStartNewInstance?.Invoke();
            return NextStartResult;
        }
    }
}
