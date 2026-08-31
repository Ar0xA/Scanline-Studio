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

    [Fact]
    public void WhenDisposeAsyncCapturesTheCallingSynchronizationContext_StillCompletes_NotBlockedByIt()
    {
        // T0-5 fix landed: HandleLifetimeExit now wraps host.DisposeAsync() in Task.Run, which does
        // not flow the ambient SynchronizationContext into its delegate -- the delegate always runs
        // on a thread-pool thread with SynchronizationContext.Current == null. FakeAsyncDisposableHost's
        // DisposeAsync still awaits its gate WITHOUT ConfigureAwait(false) (deliberately, unchanged --
        // that's what proves the fix doesn't depend on well-behaved awaits downstream), but its
        // continuation now resumes on the pool thread instead of posting to capturingContext, so
        // disposal genuinely completes instead of hanging until disposeTimeout. This is the
        // regression gate promised by this test's own prior name/comment ("becomes the actual
        // regression gate once T0-5's separate Task.Run fix lands").
        var previousContext = SynchronizationContext.Current;
        var capturingContext = new NonPumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(capturingContext);
        try
        {
            var disposeGate = new TaskCompletionSource();
            var host = new FakeAsyncDisposableHost { DisposeGate = disposeGate.Task };
            var restarter = new FakeApplicationRestarter { RestartRequested = false };

            // Completes the gate from a background thread, after this thread has already reached
            // HandleLifetimeExit's blocking wait below -- proves completion is driven by the gate
            // actually resolving, not a coincidence of ordering.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                disposeGate.SetResult();
            });

            var start = DateTime.UtcNow;
            Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null,
                disposeTimeout: TimeSpan.FromMilliseconds(200));
            var elapsed = DateTime.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(2),
                $"HandleLifetimeExit should return promptly once DisposeAsync completes, took {elapsed}.");
            Assert.True(host.DisposeAsyncCompleted,
                "DisposeAsync's continuation should run to completion on Task.Run's own pool thread " +
                "-- if this is false, the fix regressed and the UI-thread deadlock is back.");
            // Code-review nit: Assert.Equal (not Assert.True(x == 0, "...")) so a failure's message
            // reports the actual count, not just "false" -- nothing in the DisposeAsync chain should
            // ever touch the calling thread's SynchronizationContext once it's wrapped in Task.Run.
            Assert.Equal(0, capturingContext.PostedCallbackCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    /// <summary>Reproduces a UI-thread-style <see cref="SynchronizationContext"/> that only runs
    /// posted work when its owning thread pumps a message loop -- deliberately never invokes the
    /// callback, since this test's calling thread (blocked synchronously inside
    /// <see cref="Program.HandleLifetimeExit"/>) never does either. The base
    /// <see cref="SynchronizationContext"/>'s own default <c>Post</c> implementation queues to the
    /// thread pool instead, which would NOT reproduce the deadlock -- this override is the whole
    /// point of the test above.</summary>
    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public int PostedCallbackCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => PostedCallbackCount++;
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

    [Fact]
    public void WhenRestartRequested_ReleasesTheSingleInstanceMutexBeforeSpawning()
    {
        // Code-review finding: StartNewInstance's own child process re-launches without
        // --allow-multiple-instances, and this process is still alive (Main hasn't returned) when
        // the spawn happens -- if the mutex weren't released first, the child would race this
        // process's own real exit for the same named lock and could lose, silently vanishing
        // instead of restarting. Same "assert inside OnStartNewInstance, before the real spawn
        // runs" technique as WhenRestartRequested_DisposesTheFileLoggerProviderBeforeSpawning above.
        var mutexName = Guid.NewGuid().ToString("N");
        var singleInstanceMutex = new Mutex(initiallyOwned: false, name: mutexName, out var createdNew);
        Assert.True(createdNew); // sanity: this test's own setup actually holds the name

        var host = new FakeAsyncDisposableHost();
        var restarter = new FakeApplicationRestarter { RestartRequested = true, NextStartResult = true };
        restarter.OnStartNewInstance = () =>
        {
            using var reacquired = new Mutex(initiallyOwned: false, name: mutexName, out var stillFree);
            Assert.True(stillFree, "the single-instance mutex must be released before the restart spawn, or the child can lose the race and silently fail to start");
        };

        Program.HandleLifetimeExit(NullLogger.Instance, host, restarter, fileLoggerProvider: null, singleInstanceMutex: singleInstanceMutex);

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
