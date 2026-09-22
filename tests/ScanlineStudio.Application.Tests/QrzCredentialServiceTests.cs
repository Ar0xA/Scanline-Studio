using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

public sealed class QrzCredentialServiceTests
{
    private const string Key = QrzCredentialService.CredentialKey;

    private static FakeSettingsStore StoreWith(QrzLookupSettings section) =>
        new() { Settings = new AppSettings().WithSection(QrzLookupSettings.SectionKey, section, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) };

    private static QrzLookupSettings Section(FakeSettingsStore store) =>
        store.Settings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();

    private static QrzCredentialService Create(FakeSettingsStore settings, ICredentialStore store) =>
        new(settings, CredentialStoreResolver.ForStore(store), NullLogger<QrzCredentialService>.Instance);

    [Fact]
    public async Task ReadAsync_NonEmptyJsonPassword_WinsOverTheStore()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "json" });
        var store = new FakeCredentialStore();
        store.Items[Key] = "keyring";

        var read = await Create(settings, store).ReadAsync(allowPrompt: false);

        Assert.Equal(CredentialReadStatus.Found, read.Status);
        Assert.Equal("json", read.Secret);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ReadAsync_EmptyJsonPassword_ReadsTheStore()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "" });
        var store = new FakeCredentialStore();
        store.Items[Key] = "keyring";

        var read = await Create(settings, store).ReadAsync(allowPrompt: false);

        Assert.Equal("keyring", read.Secret);
    }

    [Fact]
    public async Task ReadAsync_FallbackSessionWithCredentialStoreFlag_ReportsUnavailableNotAbsent()
    {
        var settings = StoreWith(new QrzLookupSettings { PasswordInCredentialStore = true });

        var read = await Create(settings, new SettingsFileCredentialStore()).ReadAsync(allowPrompt: false);

        Assert.Equal(CredentialReadStatus.Unavailable, read.Status);
    }

    [Fact]
    public async Task ReadAsync_FallbackSessionWithoutFlag_ReportsAbsent()
    {
        var read = await Create(new FakeSettingsStore(), new SettingsFileCredentialStore()).ReadAsync(allowPrompt: false);

        Assert.Equal(CredentialReadStatus.Absent, read.Status);
    }

    [Fact]
    public async Task ReadAsync_LockedKeyringWithoutPrompt_ReportsUnavailable()
    {
        var store = new FakeCredentialStore { Locked = true };
        store.Items[Key] = "keyring";

        var read = await Create(new FakeSettingsStore(), store).ReadAsync(allowPrompt: false);

        Assert.Equal(CredentialReadStatus.Unavailable, read.Status);
        Assert.False(store.AnyPromptAllowed);
    }

    [Fact]
    public async Task FallbackStore_RoundTripsThroughSettingsJson()
    {
        var settings = new FakeSettingsStore();
        var service = Create(settings, new SettingsFileCredentialStore());

        Assert.Equal(QrzPasswordWriteOutcome.Saved, await service.WriteAsync("pass", null));
        Assert.Equal("pass", Section(settings).Password);
        Assert.Equal("pass", (await service.ReadAsync(allowPrompt: false)).Secret);
        Assert.True(await service.IsPresentAsync());

        Assert.Equal(QrzPasswordWriteOutcome.Saved, await service.WriteAsync(null, "pass"));
        Assert.Null(Section(settings).Password);
        Assert.Equal(CredentialReadStatus.Absent, (await service.ReadAsync(allowPrompt: false)).Status);
    }

    [Fact]
    public async Task WriteAsync_SecureMode_StoresInKeyringAndSetsFlag_NotInJson()
    {
        var settings = new FakeSettingsStore();
        var store = new FakeCredentialStore();

        var result = await Create(settings, store).WriteAsync("secret", null);

        Assert.Equal(QrzPasswordWriteOutcome.Saved, result);
        Assert.Equal("secret", store.Items[Key]);
        Assert.Null(Section(settings).Password);
        Assert.True(Section(settings).PasswordInCredentialStore);
    }

    // B2(a)
    [Fact]
    public async Task WriteAsync_SecureMode_ClearsStaleJsonPlaintext_AndNextStartDoesNotMigrateTheOldValueBack()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "old" });
        var store = new FakeCredentialStore();
        var service = Create(settings, store);

        var result = await service.WriteAsync("new", replacedPassword: "old");
        await service.MigrateAsync();

        Assert.Equal(QrzPasswordWriteOutcome.Saved, result);
        Assert.Null(Section(settings).Password);
        Assert.Equal("new", store.Items[Key]);
        Assert.Equal("new", (await service.ReadAsync(allowPrompt: false)).Secret);
    }

    [Fact]
    public async Task WriteAsync_JsonChangedByAnotherWriter_IsNotCleared()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "someone-else" });
        var store = new FakeCredentialStore();

        await Create(settings, store).WriteAsync("new", replacedPassword: "old");

        Assert.Equal("someone-else", Section(settings).Password);
    }

    // R2
    [Fact]
    public async Task WriteAsync_SecureDelete_AlsoClearsTheJsonCopy_SoItIsNotResurrected()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "old", PasswordInCredentialStore = true });
        var store = new FakeCredentialStore();
        store.Items[Key] = "old";
        var service = Create(settings, store);

        var result = await service.WriteAsync(null, replacedPassword: "old");

        Assert.Equal(QrzPasswordWriteOutcome.Saved, result);
        Assert.False(store.Items.ContainsKey(Key));
        Assert.Null(Section(settings).Password);
        Assert.False(Section(settings).PasswordInCredentialStore);
        Assert.Equal(CredentialReadStatus.Absent, (await service.ReadAsync(allowPrompt: false)).Status);
    }

    // R1 (service half)
    [Fact]
    public async Task WriteAsync_KeyringWriteFails_ReturnsFailure_AndNeverFallsBackToPlaintext()
    {
        var settings = StoreWith(new QrzLookupSettings { Username = "user" });
        var store = new FakeCredentialStore { SetResult = CredentialWrite.Failed("bus error") };

        var result = await Create(settings, store).WriteAsync("secret", null);

        Assert.Equal(QrzPasswordWriteOutcome.KeyringFailed, result);
        Assert.Null(Section(settings).Password);
        Assert.Null(Section(settings).PasswordInCredentialStore);
    }

    [Fact]
    public async Task WriteAsync_VerifyMismatch_ReturnsFailure_AndLeavesJsonUntouched()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "old" });
        var store = new FakeCredentialStore { GetOverride = "garbled" };

        var result = await Create(settings, store).WriteAsync("new", replacedPassword: "old");

        Assert.Equal(QrzPasswordWriteOutcome.KeyringVerifyFailed, result);
        Assert.Equal("old", Section(settings).Password);
    }

    [Fact]
    public async Task WriteAsync_LockedKeyring_PromptsOnlyOnTheRetry_AndDoesNotHoldTheGateWhilePrompting()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "old" });
        var promptGate = new TaskCompletionSource();
        var store = new FakeCredentialStore { Locked = true, PromptGate = promptGate.Task };
        var service = Create(settings, store);

        var write = service.WriteAsync("new", replacedPassword: "old");
        await store.PromptShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Migration needs the gate; it must finish while the prompt is still on screen.
        var migrate = service.MigrateAsync();
        var finished = await Task.WhenAny(migrate, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(migrate, finished);

        promptGate.SetResult();
        var result = await write;

        Assert.Equal(QrzPasswordWriteOutcome.Saved, result);
        Assert.Equal(("set", false), store.Calls[0]);
        Assert.Contains(("set", true), store.Calls);
        Assert.Equal("new", store.Items[Key]);
    }

    [Fact]
    public async Task MigrateAsync_HappyPath_MovesJsonPasswordIntoKeyring()
    {
        var settings = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user", Password = "plain" });
        var store = new FakeCredentialStore();

        await Create(settings, store).MigrateAsync();

        Assert.Equal("plain", store.Items[Key]);
        Assert.Null(Section(settings).Password);
        Assert.True(Section(settings).PasswordInCredentialStore);
        Assert.Equal("user", Section(settings).Username);
    }

    [Theory]
    [InlineData(CredentialWriteStatus.Unavailable)]
    [InlineData(CredentialWriteStatus.Failed)]
    public async Task MigrateAsync_StoreFailure_LeavesJsonUntouched(CredentialWriteStatus status)
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });
        var store = new FakeCredentialStore { SetResult = new CredentialWrite(status, "nope") };

        await Create(settings, store).MigrateAsync();

        Assert.Equal("plain", Section(settings).Password);
        Assert.Null(Section(settings).PasswordInCredentialStore);
    }

    [Fact]
    public async Task MigrateAsync_VerifyMismatch_LeavesJsonUntouched()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });
        var store = new FakeCredentialStore { GetOverride = "different" };

        await Create(settings, store).MigrateAsync();

        Assert.Equal("plain", Section(settings).Password);
    }

    [Fact]
    public async Task MigrateAsync_LockedKeyring_SkipsWithoutPrompting()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });
        var store = new FakeCredentialStore { Locked = true };

        await Create(settings, store).MigrateAsync();

        Assert.Equal("plain", Section(settings).Password);
        Assert.False(store.AnyPromptAllowed);
    }

    // B2(b)
    [Fact]
    public async Task MigrateAsync_JsonValueChangedDuringMigration_IsNotCleared()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });
        var store = new FakeCredentialStore();
        store.OnSet = () => settings.Settings = settings.Settings.WithSection(
            QrzLookupSettings.SectionKey, new QrzLookupSettings { Password = "edited" }, QrzLookupSettingsJsonContext.Default.QrzLookupSettings);

        await Create(settings, store).MigrateAsync();

        Assert.Equal("edited", Section(settings).Password);
    }

    [Fact]
    public async Task MigrateAsync_FallbackStore_DoesNothing()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });

        await Create(settings, new SettingsFileCredentialStore()).MigrateAsync();

        Assert.Equal("plain", Section(settings).Password);
    }

    // D1
    [Fact]
    public async Task IsPresentAndMigrate_NeverAllowAPrompt()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });
        var store = new FakeCredentialStore { Locked = true };
        store.Items[Key] = "other";
        var service = Create(settings, store);

        await service.MigrateAsync();
        settings.Settings = new AppSettings();
        var present = await service.IsPresentAsync();

        Assert.True(present);
        Assert.NotEmpty(store.Calls);
        Assert.False(store.AnyPromptAllowed);
    }

    [Fact]
    public async Task IsPresentAsync_FallbackSessionWithFlagOnly_IsFalse()
    {
        var settings = StoreWith(new QrzLookupSettings { PasswordInCredentialStore = true });

        Assert.False(await Create(settings, new SettingsFileCredentialStore()).IsPresentAsync());
    }

    [Fact]
    public async Task HungStore_EveryPathTimesOutAsUnavailable_AndTheGateIsReleased()
    {
        var settings = StoreWith(new QrzLookupSettings { Password = "plain" });
        var options = new QrzCredentialServiceOptions { StoreCallTimeout = TimeSpan.FromMilliseconds(100), PromptCallTimeout = TimeSpan.FromMilliseconds(100) };
        var service = new QrzCredentialService(settings, CredentialStoreResolver.ForStore(new NeverCompletingCredentialStore()), NullLogger<QrzCredentialService>.Instance, options);
        var bound = TimeSpan.FromSeconds(10);

        await service.MigrateAsync().WaitAsync(bound);
        Assert.Equal("plain", Section(settings).Password);

        Assert.Equal(QrzPasswordWriteOutcome.KeyringUnavailable, await service.WriteAsync("new", "plain").WaitAsync(bound));
        Assert.Equal(QrzPasswordWriteOutcome.KeyringUnavailable, await service.WriteAsync(null, "plain").WaitAsync(bound));
        Assert.Equal("plain", Section(settings).Password);

        settings.Settings = new AppSettings();
        Assert.Equal(CredentialReadStatus.Unavailable, (await service.ReadAsync(allowPrompt: false).WaitAsync(bound)).Status);
        Assert.False(await service.IsPresentAsync().WaitAsync(bound));

        // A second gated operation completing proves the timed-out ones released the gate.
        settings.Settings = StoreWith(new QrzLookupSettings { Password = "plain" }).Settings;
        await service.MigrateAsync().WaitAsync(bound);
    }

    [Fact]
    public async Task Resolver_ProbeTimeout_FallsBackWithoutBlockingTheCaller()
    {
        using var release = new ManualResetEventSlim(false);
        var resolver = new CredentialStoreResolver(
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); // a probe that blocks its thread synchronously, ignoring cancellation
                return Task.FromResult<ICredentialStore?>(new FakeCredentialStore());
            },
            TimeSpan.FromMilliseconds(200),
            NullLogger<CredentialStoreResolver>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var pending = resolver.GetAsync();
        var returnedAfter = stopwatch.Elapsed;
        var store = await pending;
        release.Set();

        Assert.True(returnedAfter < TimeSpan.FromMilliseconds(150), $"GetAsync blocked the caller for {returnedAfter}");
        Assert.IsType<SettingsFileCredentialStore>(store);
    }

    [Fact]
    public async Task Resolver_ProbeReturnsNullOrThrows_FallsBack()
    {
        var none = new CredentialStoreResolver(_ => Task.FromResult<ICredentialStore?>(null), TimeSpan.FromSeconds(3), NullLogger<CredentialStoreResolver>.Instance);
        var throws = new CredentialStoreResolver(_ => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(3), NullLogger<CredentialStoreResolver>.Instance);

        Assert.IsType<SettingsFileCredentialStore>(await none.GetAsync());
        Assert.IsType<SettingsFileCredentialStore>(await throws.GetAsync());
    }

    [Fact]
    public async Task Resolver_ProbeSucceeds_ReturnsTheSameStoreEveryTime()
    {
        var calls = 0;
        var fake = new FakeCredentialStore();
        var resolver = new CredentialStoreResolver(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<ICredentialStore?>(fake);
            },
            TimeSpan.FromSeconds(3),
            NullLogger<CredentialStoreResolver>.Instance);

        Assert.Same(fake, await resolver.GetAsync());
        Assert.Same(fake, await resolver.GetAsync());
        Assert.Equal(1, calls);
    }

    /// <summary>A keyring daemon that accepted the call and never answers.</summary>
    [Fact]
    public async Task TimedOutWrite_StillRunning_BlocksLaterWrites_AndIsNeverRetriedWithAPrompt()
    {
        var settings = new FakeSettingsStore { Settings = new AppSettings() };
        var store = new GatedCredentialStore();
        var options = new QrzCredentialServiceOptions { StoreCallTimeout = TimeSpan.FromMilliseconds(100), PromptCallTimeout = TimeSpan.FromMilliseconds(100) };
        var service = new QrzCredentialService(settings, CredentialStoreResolver.ForStore(store), NullLogger<QrzCredentialService>.Instance, options);
        var bound = TimeSpan.FromSeconds(10);

        Assert.Equal(QrzPasswordWriteOutcome.KeyringUnavailable, await service.WriteAsync("p1", null).WaitAsync(bound));
        Assert.Equal(QrzPasswordWriteOutcome.KeyringUnavailable, await service.WriteAsync("p2", null).WaitAsync(bound));
        Assert.Equal(QrzPasswordWriteOutcome.KeyringUnavailable, await service.WriteAsync(null, "p1").WaitAsync(bound));

        // While the abandoned write runs, nothing else reaches the store, and nothing retries with a prompt.
        Assert.Equal(1, store.SetCalls);
        Assert.Equal(0, store.DeleteCalls);
        Assert.False(store.AnyPromptingCall);

        // The abandoned write lands late; only after it has finished may a new save start — and it wins.
        store.Release();
        var deadline = DateTime.UtcNow + bound;
        while (store.Stored != "p1" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal("p1", store.Stored);
        await Task.Delay(50);
        Assert.Equal(QrzPasswordWriteOutcome.Saved, await service.WriteAsync("p2", null).WaitAsync(bound));
        Assert.Equal("p2", store.Stored);
    }

    /// <summary>The first SetAsync blocks until <see cref="Release"/>, then every call completes at once.</summary>
    private sealed class GatedCredentialStore : ICredentialStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _setCalls;

        public bool IsSecure => true;

        public string BackendName => "gated";

        public string? Stored { get; private set; }

        public int SetCalls => Volatile.Read(ref _setCalls);

        public int DeleteCalls { get; private set; }

        public bool AnyPromptingCall { get; private set; }

        public void Release() => _gate.TrySetResult();

        public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
            Task.FromResult(Stored is null ? CredentialRead.Absent : CredentialRead.Found(Stored));

        public async Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default)
        {
            AnyPromptingCall |= allowPrompt;
            Interlocked.Increment(ref _setCalls);
            await _gate.Task.ConfigureAwait(false);
            Stored = secret;
            return CredentialWrite.Success;
        }

        public Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default)
        {
            AnyPromptingCall |= allowPrompt;
            DeleteCalls++;
            Stored = null;
            return Task.FromResult(CredentialWrite.Success);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(Stored is not null);
    }

    private sealed class NeverCompletingCredentialStore : ICredentialStore
    {
        private readonly TaskCompletionSource _never = new();

        public bool IsSecure => true;

        public string BackendName => "hung";

        public async Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default)
        {
            await _never.Task.ConfigureAwait(false);
            return CredentialRead.Absent;
        }

        public async Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default)
        {
            await _never.Task.ConfigureAwait(false);
            return CredentialWrite.Success;
        }

        public async Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default)
        {
            await _never.Task.ConfigureAwait(false);
            return CredentialWrite.Success;
        }

        public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        {
            await _never.Task.ConfigureAwait(false);
            return true;
        }
    }
}
