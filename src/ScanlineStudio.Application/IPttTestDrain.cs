namespace ScanlineStudio.Application;

/// <summary>Shutdown-only drain for an in-flight Options PTT test, kept off
/// <see cref="IRadioSessionService"/> on purpose: that is the interface <c>ScanlineStudio.UI</c>
/// talks to (spec/01-architecture.md's layering rule), and a teardown method has no business on it.
/// The host casts to this at its exit path, the same way it already casts for
/// <see cref="System.IAsyncDisposable"/>.</summary>
public interface IPttTestDrain
{
    /// <summary>Stops admitting new PTT tests, then cancels and waits for any test already in
    /// flight, including its bounded un-key retries and protocol disposal. <see langword="false"/>
    /// means the budget expired or cleanup failed; a native call that outlives the budget stays a
    /// logged hardware limitation. Repeated calls share the same drain and return the identical task
    /// instance, and the returned task never faults.</summary>
    /// <param name="timeout">Test seam only. Production passes nothing and gets the 75-second budget
    /// the host's exit path is sized around.</param>
    Task<bool> DrainPttTestsAsync(TimeSpan? timeout = null);
}
