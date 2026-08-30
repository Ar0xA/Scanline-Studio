using System.Reactive.Subjects;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeSettingsStore : ISettingsStore, IDisposable
{
    private readonly Subject<AppSettings> _changes = new();

    public AppSettings Settings { get; set; } = new();

    public IObservable<AppSettings> Changes => _changes;

    /// <summary>Test-only hook (Tier A Batch 3 chunk 3a round 15): when set, <see cref="LoadAsync"/>
    /// parks on this until it completes -- lets a test simulate a settings read hanging (e.g. a
    /// config file on a wedged network mount), the failure mode round-15 finding 3's
    /// ResolveDeviceAsync/GetTxVolumePercentAsync/LoadAudioSettingsAsync WaitAsync bounds exist to
    /// close. Respects `ct` via Task.WaitAsync, matching FakeAudioDeviceEnumerator.Gate's own
    /// round-10 pattern.</summary>
    public Task? Gate { get; set; }

    /// <summary>When set, <see cref="LoadAsync"/> throws this instead of returning
    /// <see cref="Settings"/> -- lets a test simulate a corrupt/unreadable settings file.</summary>
    public Exception? LoadAsyncException { get; set; }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (Gate is not null)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
        }

        if (LoadAsyncException is { } ex)
        {
            throw ex;
        }

        return Settings;
    }

    /// <summary>Configurations-preset backlog, Phase 1 code-review round-1 finding: when set, the
    /// NEXT <see cref="SaveAsync"/> call throws this instead of persisting, then this is cleared
    /// back to <see langword="null"/> -- ONE-SHOT, mirroring <see cref="LoadAsyncException"/>'s own
    /// shape -- lets a test simulate a disk-full/permission-denied write without breaking a
    /// SUBSEQUENT save in the same test (e.g. a rollback's own restore write).</summary>
    public Exception? SaveAsyncExceptionOnce { get; set; }

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (SaveAsyncExceptionOnce is { } ex)
        {
            SaveAsyncExceptionOnce = null;
            throw ex;
        }

        Settings = settings;
        _changes.OnNext(settings);
        return Task.CompletedTask;
    }

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>T0-2 test-only hook: when set, <see cref="UpdateAsync"/>'s write phase (after
    /// <c>mutate</c>, before the delegated <see cref="SaveAsync"/> call) awaits this -- lets a test
    /// hold ONE <see cref="UpdateAsync"/> call's write phase open to prove a concurrent second call
    /// genuinely blocks on <see cref="_lock"/> rather than racing past it. One-shot: cleared the
    /// moment a call reaches it, so it only gates the FIRST call to arrive, never a later one in the
    /// same test.</summary>
    public Task? SaveGate { get; set; }

    /// <summary>T0-2 test-only hook: signaled right after <see cref="UpdateAsync"/> acquires
    /// <see cref="_lock"/> -- gives a test a genuine "the first call has entered its critical section"
    /// happens-before to await, instead of a timing assumption, before starting a second concurrent
    /// call.</summary>
    public TaskCompletionSource? LockAcquiredSignal { get; set; }

    /// <summary>T0-2: real mutual exclusion via <see cref="_lock"/> -- the one fake that must prove
    /// genuine serialization, not just forward to <see cref="LoadAsync"/>/<see cref="SaveAsync"/> like
    /// every other <see cref="ISettingsStore"/> test double in this codebase. Delegates the actual
    /// write to this class's own <see cref="SaveAsync"/> (not a duplicated throw/assign) so
    /// <see cref="SaveAsyncExceptionOnce"/>'s existing throw-before-assign ordering, depended on by
    /// <c>SstvSessionServiceCaptureDeviceLiveApplyTests</c>, is preserved automatically.</summary>
    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LockAcquiredSignal?.TrySetResult();
            // Auditor code-review finding: must go through this class's own LoadAsync, not a direct
            // `Settings` read, so Gate/LoadAsyncException (used to gate e.g.
            // ConfigurationPresetServiceTests' single-flight test) still apply once a caller migrates
            // from the old LoadAsync+SaveAsync pair onto UpdateAsync.
            var current = await LoadAsync(ct).ConfigureAwait(false);
            var updated = mutate(current);
            if (ReferenceEquals(updated, current))
            {
                return updated;
            }

            if (SaveGate is { } gate)
            {
                SaveGate = null;
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }

            await SaveAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _changes.Dispose();
        _lock.Dispose();
    }
}
