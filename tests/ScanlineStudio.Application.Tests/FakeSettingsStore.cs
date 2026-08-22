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

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _changes.OnNext(settings);
        return Task.CompletedTask;
    }

    public void Dispose() => _changes.Dispose();
}
