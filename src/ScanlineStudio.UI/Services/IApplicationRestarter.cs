namespace ScanlineStudio.UI.Services;

/// <summary>Backs Options &gt; General's Config/Database "Restart Now" action -- deliberately NOT
/// shaped like <see cref="IUrlLauncher"/>'s void/swallow-and-log convenience-action shape: a
/// failed relaunch here must be visible to the caller (see <see cref="StartNewInstance"/>), not
/// swallowed, since a silent failure would leave the user with no running app and no clue why.
///
/// <see cref="RestartRequested"/> is a plain flag, not a command: setting it does NOT spawn a new
/// process immediately -- <c>Program.cs</c>'s own shutdown sequence spawns the new instance only
/// after its existing teardown (<c>DisposeAsync</c>, <c>SqliteConnection.ClearAllPools()</c>) has
/// fully completed, so the new process's own startup-time file relocation never races a still-open
/// handle in this one. Implementations must NOT be <see cref="IDisposable"/>/
/// <see cref="IAsyncDisposable"/> -- the DI container would otherwise capture and tear this down
/// as part of that same teardown, before it's ever used.</summary>
public interface IApplicationRestarter
{
    bool RestartRequested { get; set; }

    /// <summary>Spawns a fresh instance of this app with the same executable/arguments this
    /// process was launched with. Returns whether the spawn actually succeeded -- callers must
    /// surface a <c>false</c> result, not treat it as fire-and-forget.</summary>
    bool StartNewInstance();
}
