namespace ScanlineStudio.Core.Radio;

/// <summary>Wraps an observable so that ONE subscriber throwing from its handler cannot affect any
/// other subscriber, and cannot silently unsubscribe itself.
///
/// <para><b>The defect this exists to fix</b> (measured, not inferred -- two subscribers per stream,
/// the first throws, three values published):</para>
/// <list type="bullet">
/// <item><c>StateChanges</c> is a composed <c>Where</c>/<c>Select</c>. Rx wraps a composed
/// observable's subscriber in its own <c>AutoDetachObserver</c>, which DISPOSES the subscription when
/// the handler throws. The thrower saw only the first value and was gone. Subscribers registered
/// after it missed that one value, then recovered.</item>
/// <item><c>ConnectionEvents</c> is a raw <c>Subject&lt;T&gt;</c>, which has no such wrapper.
/// <c>OnNext</c> rethrows out of its fan-out loop at the first thrower, so it never reaches the
/// subscribers after it -- on that publish and on every publish afterwards, permanently. The thrower
/// itself stayed subscribed and kept throwing.</item>
/// </list>
///
/// <para>Both were undocumented, neither was intended, and they were asymmetric with each other. It
/// was reachable: the main window subscribes to <c>ConnectionEvents</c> at construction and the
/// Options dialog subscribes later, and every one of those handlers begins with
/// <c>Dispatcher.UIThread.Post</c>, which can throw once the dispatcher is shutting down. One throw
/// from the earlier subscriber permanently blinded the later one for the life of the process, with
/// nothing visible to the user.</para>
///
/// <para><b>Why a hand-written <see cref="IObservable{T}"/> and not <c>Observable.Create</c>.</b>
/// <c>Observable.Create</c> returns an <c>ObservableBase</c>, so the observer handed to its lambda IS
/// the <c>AutoDetachObserver</c> -- it disposes the subscription first and rethrows into the guard
/// second, leaving the defect in place. Measured, same source, both variants: with
/// <c>Observable.Create</c> the thrower saw <c>[1]</c> and was detached; with this shape it saw
/// <c>[1,2,3]</c> and stayed subscribed. A plain <see cref="IObservable{T}"/> inserts no such wrapper,
/// so the guard sits between Rx and the real handler, which is the whole point.</para>
///
/// <para><b>This must stay OUTERMOST.</b> <c>Guarded(Select(Where(subject)))</c> works.
/// <c>Select(Where(Guarded(subject)))</c> reads as equivalent and is not -- the outer operator's own
/// <c>AutoDetachObserver</c> would sit back between the guard and the handler.</para>
///
/// <para><b>A throwing subscriber stays subscribed</b> rather than being detached. The reachability
/// case decides it: a shutdown-time <c>Dispatcher.UIThread.Post</c> throw is transient, and a detach
/// policy turns a transient throw into permanent blindness for the rest of the process.
/// <c>MiniAudioEngine</c> already made the same choice for the same problem. The deliberate cost: a
/// subscriber that throws persistently is now invoked forever, so the publisher pays a throw and a
/// catch every publish. Microseconds against a 250ms poll.</para>
///
/// <para><b>What this does NOT do.</b> It contains THROWS, not misbehaviour generally. It adds no
/// serialization, so handlers can still run concurrently on two threads. It does not help a subscriber
/// that BLOCKS -- that still stalls the publisher. It does not help a subscriber that synchronously
/// calls back into a lifecycle method, which still deadlocks exactly as
/// <c>IRadioController.ConnectAsync</c> describes.</para>
///
/// <para><b>The protection ends at the subscription boundary.</b> It covers the observer handed to
/// <see cref="Subscribe"/>. Compose ANY Rx operator downstream --
/// <c>StateChanges.Where(...).Subscribe(h)</c>, <c>ObserveOn</c>, anything -- and that operator
/// inserts its own <c>AutoDetachObserver</c> BETWEEN this guard and <c>h</c>, so <c>h</c> is
/// auto-detached again on its first throw. Other subscribers stay protected either way. Subscribe
/// directly to keep the guarantee.</para></summary>
/// <param name="source">The stream to wrap. Pass the fully composed observable — see the
/// outermost rule above.</param>
/// <param name="onFirstThrow">Logs the first throw from a given subscription, in full.</param>
/// <param name="onRepeatedThrows">Logs a periodic summary carrying the running count. Called at
/// <see cref="SummaryEveryNThrows"/>, never on every throw.</param>
internal sealed class GuardedObservable<T>(
    IObservable<T> source,
    Action<Exception> onFirstThrow,
    Action<Exception, int> onRepeatedThrows) : IObservable<T>
{
    /// <summary>Log the first throw in full, then one summary line per this many. Matches
    /// <c>MiniAudioCaptureSession</c>'s own cadence. Logging every throw is forbidden outright on this
    /// path (docs/logging-guidelines.md names the radio poll loop specifically) because
    /// <c>FileLogger</c> does a synchronous lock-serialized disk write per call -- so an unthrottled
    /// log would itself become the slow-publisher stall this whole change exists to avoid.</summary>
    private const int SummaryEveryNThrows = 100;

    public IDisposable Subscribe(IObserver<T> observer) =>
        source.Subscribe(new GuardedObserver(observer, onFirstThrow, onRepeatedThrows));

    /// <summary>One per subscription, which is what makes the throw counter meaningful. A single
    /// shared counter on the controller would let the first throwing subscriber consume the
    /// first-occurrence branch, so every other subscriber's first throw would be absorbed into a
    /// summary naming the wrong count -- a worse diagnostic than none.</summary>
    private sealed class GuardedObserver(
        IObserver<T> inner,
        Action<Exception> onFirstThrow,
        Action<Exception, int> onRepeatedThrows) : IObserver<T>
    {
        private int _throwCount;

        public void OnNext(T value) => Guard(() => inner.OnNext(value));

        public void OnCompleted() => Guard(inner.OnCompleted);

        /// <summary>Defensive only. Nothing in <c>RadioController</c> ever calls <c>OnError</c> —
        /// the subjects only ever see <c>OnNext</c>, <c>OnCompleted</c> and <c>Dispose</c>.</summary>
        public void OnError(Exception error) => Guard(() => inner.OnError(error));

        /// <summary>Catches <see cref="Exception"/> unconditionally, with NO <c>when</c> filter. A
        /// filter such as <c>when (ex is not OperationCanceledException)</c> would silently restore
        /// the entire defect for that one type, and would not be caught by a test that throws a plain
        /// exception. That is not a hypothetical shape here: the reachability case is
        /// <c>Dispatcher.UIThread.Post</c> during shutdown, which is exactly where cancellation-shaped
        /// exceptions surface.</summary>
        private void Guard(Action notify)
        {
            try
            {
                notify();
            }
            catch (Exception ex)
            {
                // Interlocked, not ++: per-subscriber does NOT mean single-threaded. The poll loop
                // publishes with no lock held while a caller thread can publish from its own
                // disconnect path, so this same observer can be notified on two threads at once. A
                // plain increment would lose counts and could skip the first-occurrence branch
                // entirely.
                var count = Interlocked.Increment(ref _throwCount);
                try
                {
                    if (count == 1)
                    {
                        onFirstThrow(ex);
                    }
                    else if (count % SummaryEveryNThrows == 0)
                    {
                        onRepeatedThrows(ex, count);
                    }
                }
                catch
                {
                    // The log callbacks are the one path that could still escape this guard, which
                    // would restore the whole defect behind a throwing ILogger -- and on the
                    // OnCompleted path would re-open the DisposeAsync teardown abort. A throwing
                    // ILogger is treated as reachable elsewhere in this class, so it is guarded here
                    // too. Nothing to report if reporting itself is what failed.
                }
            }
        }
    }
}
