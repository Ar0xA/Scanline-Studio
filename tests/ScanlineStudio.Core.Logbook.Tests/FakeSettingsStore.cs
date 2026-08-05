using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

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
}
