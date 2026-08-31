namespace ScanlineStudio.Host.Tests;

/// <summary>Missing-feature sweep (2026-08-31): legacy YONIQ refuses a second launch outright
/// (<c>Mmsstv.cpp</c>'s <c>WinMain</c>). Each test uses its own unique mutex name (a GUID) so it
/// never collides with a genuinely running Scanline Studio instance or with another test running
/// concurrently.</summary>
public sealed class TryAcquireSingleInstanceLockTests
{
    [Fact]
    public void FirstCaller_AcquiresTheLock()
    {
        var mutexName = Guid.NewGuid().ToString("N");
        var errorWriter = new StringWriter();

        var acquired = Program.TryAcquireSingleInstanceLock([], errorWriter, out var mutex, mutexName);

        try
        {
            Assert.True(acquired);
            Assert.NotNull(mutex);
            Assert.Equal(string.Empty, errorWriter.ToString());
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    [Fact]
    public void SecondCaller_FailsToAcquire_AndReportsToErrorWriter()
    {
        var mutexName = Guid.NewGuid().ToString("N");
        using var firstMutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        Assert.True(createdNew); // sanity: this test's own setup actually holds the lock

        var errorWriter = new StringWriter();
        var acquired = Program.TryAcquireSingleInstanceLock([], errorWriter, out var mutex, mutexName);

        Assert.False(acquired);
        Assert.Null(mutex);
        Assert.Contains("already running", errorWriter.ToString());
        Assert.Contains("--allow-multiple-instances", errorWriter.ToString());
    }

    [Fact]
    public void AllowMultipleInstancesFlag_BypassesTheCheck_EvenWhenAnotherInstanceHoldsTheLock()
    {
        var mutexName = Guid.NewGuid().ToString("N");
        using var firstMutex = new Mutex(initiallyOwned: true, name: mutexName, out _);

        var errorWriter = new StringWriter();
        var acquired = Program.TryAcquireSingleInstanceLock(["--allow-multiple-instances"], errorWriter, out var mutex, mutexName);

        Assert.True(acquired);
        Assert.Null(mutex); // nothing acquired -- matches "nothing to hold, nothing to release"
        Assert.Equal(string.Empty, errorWriter.ToString());
    }

    [Fact]
    public void SecondCallerAfterFirstReleases_CanAcquireTheLock()
    {
        // Confirms the failed second attempt doesn't leak a handle that would wedge a later,
        // genuinely-first attempt against the same name.
        var mutexName = Guid.NewGuid().ToString("N");
        using (var firstMutex = new Mutex(initiallyOwned: true, name: mutexName, out _))
        {
            var blockedAttempt = Program.TryAcquireSingleInstanceLock([], new StringWriter(), out var blockedMutex, mutexName);
            Assert.False(blockedAttempt);
            Assert.Null(blockedMutex);
        }

        var acquired = Program.TryAcquireSingleInstanceLock([], new StringWriter(), out var mutex, mutexName);

        try
        {
            Assert.True(acquired);
            Assert.NotNull(mutex);
        }
        finally
        {
            mutex?.Dispose();
        }
    }
}
