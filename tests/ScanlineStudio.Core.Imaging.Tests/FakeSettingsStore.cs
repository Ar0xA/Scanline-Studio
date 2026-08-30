using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Imaging.Tests;

internal sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = new();

    public IObservable<AppSettings> Changes => System.Reactive.Linq.Observable.Never<AppSettings>();

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        var current = await LoadAsync(ct).ConfigureAwait(false);
        var updated = mutate(current);
        await SaveAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }
}
