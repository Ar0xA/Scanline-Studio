namespace Yoniq.Settings;

/// <summary>See spec/12-settings.md. Migration-chain versioning is not implemented yet — Phase 0 scope.</summary>
public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    IObservable<AppSettings> Changes { get; }
}
