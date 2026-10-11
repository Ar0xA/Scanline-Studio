namespace ScanlineStudio.UI.Services;

/// <summary>
/// Last-wins debounce on an injectable clock: each <see cref="Schedule"/> cancels the previous one, and only an
/// action whose delay elapses without being superseded or cancelled runs. Nothing is flushed on teardown.
/// <para>Concurrency: every <see cref="CancellationTokenSource"/> has exactly one owner. Only the swap of the
/// current source happens under the lock; whoever swaps a source out cancels and disposes it outside the lock.
/// That makes <see cref="Cancel"/> safe from any thread, including while a run resumes on the thread pool.
/// The token is captured under the lock, because <c>CancellationTokenSource.Token</c> throws once disposed.</para>
/// <para>The last source stays current after its action completes, so a later <see cref="Cancel"/> still
/// cancels the token that action received. It has no timer, so leaving it undisposed is harmless.</para>
/// <para>Ordering between successive actions is not this type's job; a caller that needs ordered writes
/// chains them itself.</para>
/// </summary>
public sealed class Debouncer(TimeSpan delay, TimeProvider time, bool resumeOnCapturedContext)
{
    private readonly ConfigureAwaitOptions _context = resumeOnCapturedContext
        ? ConfigureAwaitOptions.ContinueOnCapturedContext
        : ConfigureAwaitOptions.None;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _isPending;

    /// <summary>True from <see cref="Schedule"/> until its delay elapses unsuperseded; false after <see cref="Cancel"/>.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _isPending;
            }
        }
    }

    /// <summary>Replaces any pending or running action with <paramref name="action"/>, run after the delay with a
    /// token that a later <see cref="Schedule"/> or <see cref="Cancel"/> cancels. The returned task completes
    /// without running the action if it is superseded or cancelled first, and faults if the action throws.
    /// Fire-and-forget callers discard it, so an action fault reaches the unobserved-task logger.</summary>
    public Task Schedule(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var next = new CancellationTokenSource();
        CancellationTokenSource? previous;
        CancellationToken token;
        lock (_gate)
        {
            previous = _cts;
            _cts = next;
            token = next.Token;
            _isPending = true;
        }

        previous?.Cancel();
        previous?.Dispose();
        return RunAsync(next, action, token);
    }

    /// <summary>Cancels the pending action, or the token of the running or last-run action. Safe from any thread.</summary>
    public void Cancel()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _cts;
            _cts = null;
            _isPending = false;
        }

        previous?.Cancel();
        previous?.Dispose();
    }

    private async Task RunAsync(CancellationTokenSource cts, Func<CancellationToken, Task> action, CancellationToken token)
    {
        await Task.Delay(delay, time, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | _context);
        lock (_gate)
        {
            // Also covers a supersede that lands between the delay ending and this check.
            if (token.IsCancellationRequested || !ReferenceEquals(_cts, cts))
            {
                return;
            }

            _isPending = false;
        }

        await action(token).ConfigureAwait(_context);
    }
}

/// <summary>
/// <see cref="Debouncer"/> with one independent last-wins slot per key, under the same swap and single-owner
/// rules. When a slot's delay elapses it is removed, so a later <see cref="Schedule"/> or <see cref="Cancel"/>
/// for that key does not reach the action that is then running; the run that removed the slot owns its source
/// and disposes it once the action completes. Ordering of the actions themselves is left to the caller.
/// </summary>
public sealed class KeyedDebouncer<TKey>(TimeSpan delay, TimeProvider time, bool resumeOnCapturedContext)
    where TKey : notnull
{
    private readonly ConfigureAwaitOptions _context = resumeOnCapturedContext
        ? ConfigureAwaitOptions.ContinueOnCapturedContext
        : ConfigureAwaitOptions.None;

    private readonly object _gate = new();
    private readonly Dictionary<TKey, CancellationTokenSource> _slots = [];

    /// <summary>True while <paramref name="key"/> has an action waiting out its delay.</summary>
    public bool IsPending(TKey key)
    {
        lock (_gate)
        {
            return _slots.ContainsKey(key);
        }
    }

    /// <summary>Replaces <paramref name="key"/>'s pending action. Same returned-task contract as
    /// <see cref="Debouncer.Schedule"/>.</summary>
    public Task Schedule(TKey key, Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var next = new CancellationTokenSource();
        CancellationTokenSource? previous;
        CancellationToken token;
        lock (_gate)
        {
            _slots.TryGetValue(key, out previous);
            _slots[key] = next;
            token = next.Token;
        }

        previous?.Cancel();
        previous?.Dispose();
        return RunAsync(key, next, action, token);
    }

    /// <summary>Cancels <paramref name="key"/>'s pending action, if any.</summary>
    public void Cancel(TKey key)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (!_slots.Remove(key, out previous))
            {
                return;
            }
        }

        previous.Cancel();
        previous.Dispose();
    }

    private async Task RunAsync(TKey key, CancellationTokenSource cts, Func<CancellationToken, Task> action, CancellationToken token)
    {
        await Task.Delay(delay, time, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | _context);
        lock (_gate)
        {
            if (token.IsCancellationRequested
                || !_slots.TryGetValue(key, out var current)
                || !ReferenceEquals(current, cts))
            {
                return;
            }

            _slots.Remove(key);
        }

        // This run removed the slot, so it is the source's only owner.
        try
        {
            await action(token).ConfigureAwait(_context);
        }
        finally
        {
            cts.Dispose();
        }
    }
}
