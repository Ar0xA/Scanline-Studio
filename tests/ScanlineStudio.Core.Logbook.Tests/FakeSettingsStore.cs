using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = new();

    public IObservable<AppSettings> Changes => System.Reactive.Linq.Observable.Never<AppSettings>();

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), auditor-suggested (round 1
    /// code-review of the completed-path <c>receptionId</c> hoist): when set, <see cref="LoadAsync"/>
    /// awaits this before returning, letting a test suspend
    /// <c>ReceiveHistoryRecorder.ResolveImagesDirectoryAsync</c>'s first await mid-flight to fire a
    /// second reception while the first's write is still in flight.</summary>
    public TaskCompletionSource? LoadGate { get; set; }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (LoadGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        return Settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
