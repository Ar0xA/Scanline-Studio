using System.Reactive.Subjects;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeSettingsStore : ISettingsStore, IDisposable
{
    private readonly Subject<AppSettings> _changes = new();

    public AppSettings Settings { get; set; } = new();

    public IObservable<AppSettings> Changes => _changes;

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _changes.OnNext(settings);
        return Task.CompletedTask;
    }

    public void Dispose() => _changes.Dispose();
}
