using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>TT0-1/T0-2: regression tests for `ISettingsStore.UpdateAsync`'s atomic read-modify-write.
/// See `production_audit.md`'s T0-2 finding and `JsonSettingsStoreTests.UpdateAsync_ConcurrentCalls_SerializesAndPreservesBothSections`
/// for the same test against the real file-backed store.</summary>
public sealed class SettingsStoreConcurrencyTests
{
    [Fact]
    public async Task LoadThenSave_TwoCallersInterleaved_SecondCallerClobbersFirst()
    {
        // Characterization of the OLD race pattern (two independent LoadAsync/SaveAsync calls, not
        // UpdateAsync) -- demonstrates the bug this class exists to close still exists if code ever
        // reverts to the two-call API. Not gated on UpdateAsync existing; passes/fails independent of
        // it.
        var store = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "OLD" }, OperatorSettingsJsonContext.Default.OperatorSettings)
                .WithSection(ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "OLD" }, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings),
        };

        var loadedByA = await store.LoadAsync();
        var loadedByB = await store.LoadAsync();

        // B completes its whole load-mutate-save sequence first.
        await store.SaveAsync(loadedByB.WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "B" }, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings));

        // A resumes on its own now-stale snapshot (predates B's write) and saves.
        await store.SaveAsync(loadedByA.WithSection(
            OperatorSettings.SectionKey, new OperatorSettings { Callsign = "A" }, OperatorSettingsJsonContext.Default.OperatorSettings));

        // A's write survives (it wrote last), but B's write is silently reverted -- A's stale snapshot
        // still had ConfigurationPresetSettings="OLD", so A's save wiped B's "B" write back to "OLD".
        var finalPreset = store.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("OLD", finalPreset?.ActivePresetName);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentCalls_SerializesAndPreservesBothSections()
    {
        // The regression proof for the fix. Deliberately NOT a shared gate both calls park on (this
        // project's own `feedback_deterministic_gates_not_shared_race` -- a shared gate plus an
        // assumed ordering already burned this project once): SaveGate is one-shot, consumed by
        // whichever call reaches it first, and LockAcquiredSignal gives a genuine happens-before
        // instead of a timing assumption before starting the second call.
        var store = new FakeSettingsStore
        {
            Settings = new AppSettings(),
        };
        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveGate = new TaskCompletionSource();
        store.LockAcquiredSignal = lockAcquired;
        store.SaveGate = saveGate.Task;

        var taskA = store.UpdateAsync(s => s.WithSection(
            OperatorSettings.SectionKey, new OperatorSettings { Callsign = "A" }, OperatorSettingsJsonContext.Default.OperatorSettings));

        await lockAcquired.Task; // genuine happens-before: A has entered its critical section
        var taskB = store.UpdateAsync(s => s.WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "B" }, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings));

        // Secondary check only -- see the load-bearing final-state assertion below.
        Assert.False(taskB.IsCompleted);

        saveGate.SetResult();
        await taskA;
        await taskB;

        // The load-bearing assertion: BOTH sections survive. Without a real lock, B would read A's
        // pre-write snapshot and clobber A's section -- this fails deterministically on a broken
        // implementation, unlike the IsCompleted check above.
        var finalOperator = store.Settings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings);
        var finalPreset = store.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("A", finalOperator?.Callsign);
        Assert.Equal("B", finalPreset?.ActivePresetName);
    }

    [Fact]
    public async Task UpdateAsync_MutateReturnsSameReference_NoOpsWithoutSaving()
    {
        var store = new FakeSettingsStore { Settings = new AppSettings() };
        var saveCount = 0;
        store.Changes.Subscribe(_ => saveCount++);

        var result = await store.UpdateAsync(s => s);

        Assert.Same(store.Settings, result);
        Assert.Equal(0, saveCount);
    }
}
