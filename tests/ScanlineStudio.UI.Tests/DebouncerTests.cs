using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

/// <summary>Polling wait shared by the debounce tests: pumps the UI dispatcher when called on it, and fails
/// explicitly after 10 s instead of hanging (same shape as <c>PaneViewModelTests.SettleGallerySearchAsync</c>).</summary>
internal static class TestWait
{
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs();
            }

            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out after 10 s waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }
}

public sealed class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);

    [AvaloniaFact]
    public async Task CapturedMode_DoesNotFireBeforeDelay_FiresAtDelay()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: true);
        var ran = 0;

        var run = debouncer.Schedule(_ => { ran++; return Task.CompletedTask; });
        clock.Advance(Delay - OneMs);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, ran);

        clock.Advance(OneMs);
        Dispatcher.UIThread.RunJobs();
        await run;
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task ThreeSchedulesInsideWindow_OnlyLastRuns()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        var ran = new List<int>();

        var first = debouncer.Schedule(_ => { ran.Add(1); return Task.CompletedTask; });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var second = debouncer.Schedule(_ => { ran.Add(2); return Task.CompletedTask; });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var third = debouncer.Schedule(_ => { ran.Add(3); return Task.CompletedTask; });
        clock.Advance(Delay);
        await Task.WhenAll(first, second, third);

        Assert.Equal([3], ran);
    }

    [Fact]
    public async Task SupersedeDuringAction_CancelsInFlightToken()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        var release = new TaskCompletionSource();
        CancellationToken inFlight = default;

        var first = debouncer.Schedule(async ct => { inFlight = ct; await release.Task; });
        await Task.Run(() => clock.Advance(Delay));
        Assert.True(inFlight.CanBeCanceled);
        Assert.False(inFlight.IsCancellationRequested);

        var second = debouncer.Schedule(_ => Task.CompletedTask);
        Assert.True(inFlight.IsCancellationRequested);

        release.SetResult();
        clock.Advance(Delay);
        await Task.WhenAll(first, second);
    }

    [AvaloniaFact]
    public async Task SupersedeInGapBetweenDelayAndAction_SkipsAction()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: true);
        var ran = new List<int>();

        var first = debouncer.Schedule(_ => { ran.Add(1); return Task.CompletedTask; });
        // Advancing on another thread while this UI thread blocks (no pumping) posts the delay's continuation to
        // the dispatcher without running it, so the supersede lands after the delay ended, before the action.
        var advancer = new Thread(() => clock.Advance(Delay));
        advancer.Start();
        advancer.Join();
        var second = debouncer.Schedule(_ => { ran.Add(2); return Task.CompletedTask; });
        Dispatcher.UIThread.RunJobs();
        await first;
        Assert.Empty(ran);

        clock.Advance(Delay);
        Dispatcher.UIThread.RunJobs();
        await second;
        Assert.Equal([2], ran);
    }

    [Fact]
    public async Task CancelBeforeDelay_ActionDoesNotRun()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        var ran = false;

        var run = debouncer.Schedule(_ => { ran = true; return Task.CompletedTask; });
        debouncer.Cancel();
        clock.Advance(Delay);
        await run;

        Assert.False(ran);
        Assert.False(debouncer.IsPending);
    }

    [Fact]
    public async Task CancelAfterCompletion_CancelsThatActionsToken()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        CancellationToken seen = default;

        var run = debouncer.Schedule(ct => { seen = ct; return Task.CompletedTask; });
        clock.Advance(Delay);
        await run;
        Assert.False(seen.IsCancellationRequested);

        debouncer.Cancel();
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelFromThreadPool_WhileScheduleInFlight_NeverThrowsObjectDisposed()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        using var start = new Barrier(2);

        for (var i = 0; i < 20000; i++)
        {
            // Schedule and Cancel are released together on two pool threads, so they genuinely overlap.
            Task run = Task.CompletedTask;
            var schedule = Task.Run(() =>
            {
                start.SignalAndWait();
                run = debouncer.Schedule(async ct =>
                {
                    await Task.Yield();
                    ct.ThrowIfCancellationRequested();
                });
            });
            var cancel = Task.Run(() =>
            {
                start.SignalAndWait();
                debouncer.Cancel();
            });
            await Task.WhenAll(schedule, cancel);

            // If Cancel won, the run completes without the action; otherwise the action runs here.
            clock.Advance(Delay);
            await run;
        }
    }

    [Fact]
    public async Task IsPending_TrueUntilDelayElapses_FalseAfterCancel()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        Assert.False(debouncer.IsPending);

        var run = debouncer.Schedule(_ => Task.CompletedTask);
        Assert.True(debouncer.IsPending);
        clock.Advance(Delay - OneMs);
        Assert.True(debouncer.IsPending);
        clock.Advance(OneMs);
        await run;
        Assert.False(debouncer.IsPending);

        _ = debouncer.Schedule(_ => Task.CompletedTask);
        Assert.True(debouncer.IsPending);
        debouncer.Cancel();
        Assert.False(debouncer.IsPending);
    }

    [Fact]
    public async Task SupersededAndCancelledSources_AreDisposed()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        CancellationToken first = default;
        CancellationToken second = default;

        var run = debouncer.Schedule(ct => { first = ct; return Task.CompletedTask; });
        clock.Advance(Delay);
        await run;
        Assert.NotNull(first.WaitHandle);

        run = debouncer.Schedule(ct => { second = ct; return Task.CompletedTask; });
        Assert.Throws<ObjectDisposedException>(() => first.WaitHandle);

        clock.Advance(Delay);
        await run;
        Assert.NotNull(second.WaitHandle);
        debouncer.Cancel();
        Assert.Throws<ObjectDisposedException>(() => second.WaitHandle);
    }

    [Fact]
    public async Task ActionException_FaultsReturnedTask()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);

        var run = debouncer.Schedule(_ => throw new InvalidOperationException("boom"));
        clock.Advance(Delay);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("boom", ex.Message);
    }

    [AvaloniaFact]
    public async Task CapturedMode_RunsActionOnDispatcher_EvenWhenTimerFiresOffThread()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: true);
        bool? onUiThread = null;

        var run = debouncer.Schedule(_ => { onUiThread = Dispatcher.UIThread.CheckAccess(); return Task.CompletedTask; });
        await Task.Run(() => clock.Advance(Delay));
        await TestWait.WaitUntilAsync(() => onUiThread is not null, "captured-context action");
        await run;

        Assert.True(onUiThread);
    }

    [AvaloniaFact]
    public async Task UncapturedMode_RunsActionOffDispatcher_WhenTimerFiresOffThread()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, clock, resumeOnCapturedContext: false);
        bool? onUiThread = null;

        var run = debouncer.Schedule(_ => { onUiThread = Dispatcher.UIThread.CheckAccess(); return Task.CompletedTask; });
        await Task.Run(() => clock.Advance(Delay));
        await run;

        Assert.False(onUiThread);
    }

    [Fact]
    public async Task Keyed_TwoKeysBothFire_CancelAffectsOnlyThatKey()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new KeyedDebouncer<string>(Delay, clock, resumeOnCapturedContext: false);
        var ran = new List<string>();

        var a = debouncer.Schedule("a", _ => { ran.Add("a"); return Task.CompletedTask; });
        var b = debouncer.Schedule("b", _ => { ran.Add("b"); return Task.CompletedTask; });
        clock.Advance(Delay);
        await Task.WhenAll(a, b);
        Assert.Equal(["a", "b"], ran.Order());

        ran.Clear();
        a = debouncer.Schedule("a", _ => { ran.Add("a"); return Task.CompletedTask; });
        b = debouncer.Schedule("b", _ => { ran.Add("b"); return Task.CompletedTask; });
        debouncer.Cancel("a");
        Assert.False(debouncer.IsPending("a"));
        Assert.True(debouncer.IsPending("b"));
        clock.Advance(Delay);
        await Task.WhenAll(a, b);
        Assert.Equal(["b"], ran);
    }

    [Fact]
    public async Task Keyed_SameKey_LastWins()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new KeyedDebouncer<string>(Delay, clock, resumeOnCapturedContext: false);
        var ran = new List<int>();

        var first = debouncer.Schedule("k", _ => { ran.Add(1); return Task.CompletedTask; });
        clock.Advance(Delay - OneMs);
        var second = debouncer.Schedule("k", _ => { ran.Add(2); return Task.CompletedTask; });
        clock.Advance(OneMs);
        await first;
        Assert.Empty(ran);

        clock.Advance(Delay);
        await second;
        Assert.Equal([2], ran);
    }

    [Fact]
    public async Task Keyed_SlotRemovedAndDisposedAfterRun()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new KeyedDebouncer<string>(Delay, clock, resumeOnCapturedContext: false);
        CancellationToken seen = default;

        var run = debouncer.Schedule("k", ct => { seen = ct; return Task.CompletedTask; });
        Assert.True(debouncer.IsPending("k"));
        clock.Advance(Delay);
        await run;

        Assert.False(debouncer.IsPending("k"));
        Assert.Throws<ObjectDisposedException>(() => seen.WaitHandle);
    }

    [Fact]
    public async Task Keyed_RunningAction_IsNotReachedByLaterScheduleOrCancel()
    {
        var clock = new FakeTimeProvider();
        var debouncer = new KeyedDebouncer<string>(Delay, clock, resumeOnCapturedContext: false);
        var release = new TaskCompletionSource();
        CancellationToken first = default;

        var run = debouncer.Schedule("k", async ct => { first = ct; await release.Task; });
        clock.Advance(Delay);
        Assert.False(debouncer.IsPending("k"));

        // The slot was already removed when the delay passed, so a new schedule does not reach the running action.
        var second = debouncer.Schedule("k", _ => Task.CompletedTask);
        Assert.False(first.IsCancellationRequested);
        debouncer.Cancel("k");
        Assert.False(first.IsCancellationRequested);

        release.SetResult();
        await Task.WhenAll(run, second);
        Assert.Throws<ObjectDisposedException>(() => first.WaitHandle);
    }
}
