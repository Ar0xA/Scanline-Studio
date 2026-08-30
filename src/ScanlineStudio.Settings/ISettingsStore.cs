namespace ScanlineStudio.Settings;

/// <summary>See spec/12-settings.md. Migration-chain versioning is not implemented yet — Phase 0 scope.</summary>
public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>T0-2: atomic read-modify-write. Loads the current settings, applies
    /// <paramref name="mutate"/>, and saves the result -- all under a single lock acquisition, so a
    /// concurrent caller's own <see cref="UpdateAsync"/>/<see cref="SaveAsync"/> can never observe or
    /// clobber a stale snapshot mid-sequence the way two independent <see cref="LoadAsync"/>+
    /// <see cref="SaveAsync"/> calls could. Returns the settings that were actually persisted.
    ///
    /// <b><paramref name="mutate"/> contract, strict:</b>
    /// <list type="bullet">
    /// <item>Synchronous, pure, and fast -- no I/O, no <c>await</c>, no UI-thread-affine reads (a
    /// live control's current value must be captured in a local BEFORE calling this method, never
    /// read inside the lambda). This may run synchronously on the CALLER'S OWN THREAD on any path,
    /// not only a fresh-install/no-file path -- never assume a background thread.</item>
    /// <item>Must not call back into this interface (<c>LoadAsync</c>/<c>SaveAsync</c>/
    /// <c>UpdateAsync</c>) -- the underlying lock is not reentrant and a re-entrant call self-deadlocks.</item>
    /// <item>Must not mutate its input <see cref="AppSettings"/> in place (e.g. writing directly into
    /// its <c>Sections</c> dictionary and returning the same reference) -- return a new instance for
    /// every real change. An implementation may skip the write entirely when <paramref name="mutate"/>
    /// returns the SAME reference it was given, as a no-op optimization; an in-place mutation would
    /// make that skip silently drop a real change.</item>
    /// </list>
    /// If <paramref name="mutate"/> throws, nothing is written and the exception propagates to the
    /// caller unchanged.</summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default);

    IObservable<AppSettings> Changes { get; }
}
