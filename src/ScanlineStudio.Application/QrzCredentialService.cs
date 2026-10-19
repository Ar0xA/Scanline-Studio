using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>The ONLY code that reads or writes the QRZ.com lookup password. Everything else (Options,
/// lookups, is-configured checks, startup migration) goes through here.
///
/// Where the password lives: a non-empty <see cref="QrzLookupSettings.Password"/> in settings.json always
/// wins (pre-migration value, or the plaintext fallback when no keyring exists); otherwise the
/// credential store under <see cref="QrzCredentialServiceOptions.CredentialKey"/>.
/// <see cref="QrzLookupSettings.PasswordInCredentialStore"/> records that the store holds it.
///
/// Concurrency invariant: <see cref="_gate"/> serializes every write/verify/compare-and-clear sequence
/// (Options save vs. startup migration) but is NEVER held while a keyring prompt is on screen — a
/// prompting store call runs outside the gate, and the verify + compare-and-clear that follows re-enters
/// it. Every store call made while holding the gate is non-prompting and bounded by
/// <see cref="QrzCredentialServiceOptions.StoreCallTimeout"/>, so a hung keyring daemon cannot hold the gate
/// (and with it Options Save and migration) forever; prompting calls are bounded by
/// <see cref="QrzCredentialServiceOptions.PromptCallTimeout"/>. Every settings.json change is a
/// compare-and-clear inside <see cref="ISettingsStore.UpdateAsync"/>, so a value changed by anyone else in
/// between is never cleared. An unreadable section is never compared or cleared: a write sets it aside
/// as <c>QrzLookup.unreadable</c> and starts from defaults. Every user password write (set, clear, plaintext)
/// also removes any "Password" property from that copy: the user's explicit value replaces it.</summary>
public sealed partial class QrzCredentialService : IDisposable
{
    public const string CredentialKey = "qrz-lookup-password";

    private readonly ISettingsStore _settingsStore;
    private readonly CredentialStoreResolver _storeResolver;
    private readonly ILogger<QrzCredentialService> _logger;
    private readonly QrzCredentialServiceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QrzCredentialService(ISettingsStore settingsStore, CredentialStoreResolver storeResolver, ILogger<QrzCredentialService> logger, QrzCredentialServiceOptions? options = null)
    {
        _settingsStore = settingsStore;
        _storeResolver = storeResolver;
        _logger = logger;
        _options = options ?? new QrzCredentialServiceOptions();
    }

    private string Key => _options.CredentialKey;

    /// <summary>Whether this session stores the password in a real OS keyring.</summary>
    public async Task<bool> IsSecureAsync(CancellationToken ct = default) =>
        (await _storeResolver.GetAsync(ct).ConfigureAwait(false)).IsSecure;

    /// <summary>Never throws except for cancellation. Only a user-started action may pass
    /// <paramref name="allowPrompt"/> = <see langword="true"/>. An unreadable settings section counts as
    /// "no settings.json password, flag unset", so the keyring is still read.</summary>
    public async Task<CredentialRead> ReadAsync(bool allowPrompt, CancellationToken ct = default)
    {
        try
        {
            var section = await LoadSectionForReadAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(section.Password))
            {
                return CredentialRead.Found(section.Password);
            }

            var store = await _storeResolver.GetAsync(ct).ConfigureAwait(false);
            if (!store.IsSecure)
            {
                // Written to a keyring in an earlier session that this session cannot reach: not "no password".
                return (section.PasswordInCredentialStore ?? false)
                    ? CredentialRead.Unavailable("The system keyring holding the QRZ password is not available in this session.")
                    : CredentialRead.Absent;
            }

            var read = await BoundedAsync(store.GetAsync(Key, allowPrompt, ct), allowPrompt, CredentialRead.Unavailable(CredentialReasons.TimedOut), "read", ct).ConfigureAwait(false);
            if (read.Status == CredentialReadStatus.Unavailable)
            {
                Log.StoreReadUnavailable(_logger, store.BackendName, read.Reason);
            }

            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ReadFailed(_logger, ex);
            return CredentialRead.Unavailable(ex.Message);
        }
    }

    /// <summary>"Is a password configured" without ever prompting (a locked keyring item still counts).
    /// Never throws except for cancellation.</summary>
    public async Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        try
        {
            var section = await LoadSectionForReadAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(section.Password))
            {
                return true;
            }

            var store = await _storeResolver.GetAsync(ct).ConfigureAwait(false);
            return store.IsSecure && await BoundedAsync(store.ExistsAsync(Key, ct), allowPrompt: false, false, "exists", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ReadFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>User-started write from Options Save. <paramref name="replacedPassword"/> is the value the
    /// dialog loaded (what this write replaces); settings.json's plaintext copy is cleared only if it
    /// still holds that value or the one just stored. Empty/null <paramref name="newPassword"/> deletes.
    /// With a keyring present a failure is returned, never downgraded to a plaintext write. Backend
    /// reasons are logged here, not returned: callers show their own localized text.</summary>
    public async Task<QrzPasswordWriteOutcome> WriteAsync(string? newPassword, string? replacedPassword, CancellationToken ct = default)
    {
        try
        {
            var store = await _storeResolver.GetAsync(ct).ConfigureAwait(false);
            if (!store.IsSecure)
            {
                return await WritePlaintextAsync(newPassword, ct).ConfigureAwait(false);
            }

            return string.IsNullOrEmpty(newPassword)
                ? await DeleteSecureAsync(store, replacedPassword, ct).ConfigureAwait(false)
                : await SetSecureAsync(store, newPassword, replacedPassword, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.WriteFailed(_logger, ex);
            return QrzPasswordWriteOutcome.KeyringFailed;
        }
    }

    /// <summary>Startup, background, never prompts: moves a plaintext settings.json password into the
    /// keyring. Any failure leaves settings.json untouched and is retried on the next start.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        try
        {
            var store = await _storeResolver.GetAsync(ct).ConfigureAwait(false);
            if (!store.IsSecure)
            {
                return;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                if (!settings.TryReadSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings, out var read, out _))
                {
                    Log.MigrationSkippedUnreadable(_logger);
                    return;
                }

                var section = read ?? new QrzLookupSettings();
                var plaintext = section.Password;
                if (string.IsNullOrEmpty(plaintext))
                {
                    if (read is not null && section.PasswordInCredentialStore is null)
                    {
                        await RepairCredentialStoreFlagAsync(store, ct).ConfigureAwait(false);
                    }

                    return;
                }

                if (AbandonedMutationStillRunning)
                {
                    Log.MutationBlockedByAbandonedCall(_logger, store.BackendName);
                    return;
                }

                var set = await BoundedAsync(store.SetAsync(Key, plaintext, allowPrompt: false, ct), allowPrompt: false, TimedOutWrite, "migrate", ct, mutating: true).ConfigureAwait(false);
                if (!set.IsSuccess)
                {
                    Log.MigrationSkipped(_logger, store.BackendName, set.Status, set.Reason);
                    return;
                }

                if (!await VerifyAsync(store, plaintext, ct).ConfigureAwait(false))
                {
                    Log.MigrationVerifyFailed(_logger, store.BackendName);
                    return;
                }

                var cleared = false;
                await _settingsStore.UpdateAsync(current =>
                {
                    // A section that turned unreadable since the load is never cleared (see the class doc).
                    if (!current.TryReadSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings, out var now, out _)
                        || now is null || !string.Equals(now.Password, plaintext, StringComparison.Ordinal))
                    {
                        return current;
                    }

                    cleared = true;
                    return WithSection(current, now with { Password = null, PasswordInCredentialStore = true });
                }, ct).ConfigureAwait(false);

                if (cleared)
                {
                    Log.Migrated(_logger, store.BackendName);
                }
                else
                {
                    Log.MigrationRaced(_logger);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.MigrationFailed(_logger, ex);
        }
    }

    /// <summary>A section rebuilt from defaults (Options Save or a preset over an unreadable one) lost the
    /// "in the keyring" flag. Only a null flag is repaired: false means the user deleted the password.
    /// Caller holds <see cref="_gate"/>.</summary>
    private async Task RepairCredentialStoreFlagAsync(ICredentialStore store, CancellationToken ct)
    {
        // A false Exists can also be a backend error, so it only skips the repair.
        if (!await BoundedAsync(store.ExistsAsync(Key, ct), allowPrompt: false, false, "exists", ct).ConfigureAwait(false))
        {
            return;
        }

        var repaired = false;
        await _settingsStore.UpdateAsync(current =>
        {
            if (!current.TryReadSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings, out var now, out _)
                || now is null || !string.IsNullOrEmpty(now.Password) || now.PasswordInCredentialStore is not null)
            {
                return current;
            }

            repaired = true;
            return WithSection(current, now with { PasswordInCredentialStore = true });
        }, ct).ConfigureAwait(false);

        if (repaired)
        {
            Log.CredentialStoreFlagRepaired(_logger, store.BackendName);
        }
    }

    /// <summary><paramref name="settings"/> with every "Password" property removed from a
    /// <c>QrzLookup.unreadable</c> object (the key is removed if nothing else is left); the same instance
    /// when there is nothing to remove.</summary>
    private AppSettings WithoutAsidePassword(AppSettings settings)
    {
        const string asideKey = QrzLookupSettings.SectionKey + AppSettingsSectionExtensions.UnreadableSuffix;
        if (!settings.Sections.TryGetValue(asideKey, out var aside)
            || aside.ValueKind != System.Text.Json.JsonValueKind.Object
            || !aside.EnumerateObject().Any(p => p.NameEquals(nameof(QrzLookupSettings.Password))))
        {
            return settings;
        }

        var sections = new Dictionary<string, System.Text.Json.JsonElement>(settings.Sections);
        var kept = aside.EnumerateObject().Where(p => !p.NameEquals(nameof(QrzLookupSettings.Password))).ToList();
        if (kept.Count == 0)
        {
            sections.Remove(asideKey);
        }
        else
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in kept)
                {
                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            using var document = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
            sections[asideKey] = document.RootElement.Clone();
        }

        Log.AsidePasswordRemoved(_logger);
        return settings with { Sections = sections };
    }

    private static CredentialWrite TimedOutWrite { get; } = CredentialWrite.Unavailable(CredentialReasons.TimedOut);

    // A timed-out keyring write/delete can still complete later and overwrite a newer save, so no new
    // mutation starts until it has finished. Written from any thread, hence Volatile.
    private Task? _abandonedMutation;

    private bool AbandonedMutationStillRunning => Volatile.Read(ref _abandonedMutation) is { IsCompleted: false };

    private QrzPasswordWriteOutcome RefuseWhileAbandonedMutationRuns(string backend)
    {
        Log.MutationBlockedByAbandonedCall(_logger, backend);
        return QrzPasswordWriteOutcome.KeyringUnavailable;
    }

    private async Task<QrzPasswordWriteOutcome> WritePlaintextAsync(string? newPassword, CancellationToken ct)
    {
        var value = string.IsNullOrEmpty(newPassword) ? null : newPassword;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settingsStore.UpdateAsync(current =>
            {
                var now = ReadForWrite(current, out var baseSettings);
                return WithoutAsidePassword(string.Equals(now.Password, value, StringComparison.Ordinal)
                    ? current
                    : WithSection(baseSettings, now with { Password = value }));
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return QrzPasswordWriteOutcome.Saved;
    }

    private async Task<QrzPasswordWriteOutcome> SetSecureAsync(ICredentialStore store, string newPassword, string? replacedPassword, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (AbandonedMutationStillRunning)
            {
                return RefuseWhileAbandonedMutationRuns(store.BackendName);
            }

            var set = await BoundedAsync(store.SetAsync(Key, newPassword, allowPrompt: false, ct), allowPrompt: false, TimedOutWrite, "write", ct, mutating: true).ConfigureAwait(false);
            if (set.IsSuccess)
            {
                return await FinishSetAsync(store, newPassword, replacedPassword, ct).ConfigureAwait(false);
            }

            // Only "locked" is worth a prompt; a timeout means the daemon is not answering at all.
            if (set.Status != CredentialWriteStatus.Unavailable || set == TimedOutWrite)
            {
                Log.StoreWriteFailed(_logger, store.BackendName, set.Status, set.Reason);
                return ToOutcome(set);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Keyring locked: prompt outside the gate, then re-enter it to verify and clear.
        if (AbandonedMutationStillRunning)
        {
            return RefuseWhileAbandonedMutationRuns(store.BackendName);
        }

        var prompted = await BoundedAsync(store.SetAsync(Key, newPassword, allowPrompt: true, ct), allowPrompt: true, TimedOutWrite, "write", ct, mutating: true).ConfigureAwait(false);
        if (!prompted.IsSuccess)
        {
            Log.StoreWriteFailed(_logger, store.BackendName, prompted.Status, prompted.Reason);
            return ToOutcome(prompted);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FinishSetAsync(store, newPassword, replacedPassword, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private async Task<QrzPasswordWriteOutcome> FinishSetAsync(ICredentialStore store, string newPassword, string? replacedPassword, CancellationToken ct)
    {
        if (!await VerifyAsync(store, newPassword, ct).ConfigureAwait(false))
        {
            Log.WriteVerifyFailed(_logger, store.BackendName);
            return QrzPasswordWriteOutcome.KeyringVerifyFailed;
        }

        await _settingsStore.UpdateAsync(current =>
        {
            var now = ReadForWrite(current, out var baseSettings);
            var clearPlaintext = !string.IsNullOrEmpty(now.Password)
                && (string.Equals(now.Password, replacedPassword, StringComparison.Ordinal) || string.Equals(now.Password, newPassword, StringComparison.Ordinal));
            var updated = now with
            {
                Password = clearPlaintext ? null : now.Password,
                PasswordInCredentialStore = true,
            };
            return WithoutAsidePassword(updated == now ? current : WithSection(baseSettings, updated));
        }, ct).ConfigureAwait(false);

        Log.Stored(_logger, store.BackendName);
        return QrzPasswordWriteOutcome.Saved;
    }

    private async Task<QrzPasswordWriteOutcome> DeleteSecureAsync(ICredentialStore store, string? replacedPassword, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (AbandonedMutationStillRunning)
            {
                return RefuseWhileAbandonedMutationRuns(store.BackendName);
            }

            var deleted = await BoundedAsync(store.DeleteAsync(Key, allowPrompt: false, ct), allowPrompt: false, TimedOutWrite, "delete", ct, mutating: true).ConfigureAwait(false);
            if (deleted.IsSuccess)
            {
                return await FinishDeleteAsync(store, replacedPassword, ct).ConfigureAwait(false);
            }

            if (deleted.Status != CredentialWriteStatus.Unavailable || deleted == TimedOutWrite)
            {
                Log.StoreDeleteFailed(_logger, store.BackendName, deleted.Status, deleted.Reason);
                return ToOutcome(deleted);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (AbandonedMutationStillRunning)
        {
            return RefuseWhileAbandonedMutationRuns(store.BackendName);
        }

        var prompted = await BoundedAsync(store.DeleteAsync(Key, allowPrompt: true, ct), allowPrompt: true, TimedOutWrite, "delete", ct, mutating: true).ConfigureAwait(false);
        if (!prompted.IsSuccess)
        {
            Log.StoreDeleteFailed(_logger, store.BackendName, prompted.Status, prompted.Reason);
            return ToOutcome(prompted);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FinishDeleteAsync(store, replacedPassword, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate. The JSON copy must go too, or the JSON-first read resurrects the deleted password.
    private async Task<QrzPasswordWriteOutcome> FinishDeleteAsync(ICredentialStore store, string? replacedPassword, CancellationToken ct)
    {
        await _settingsStore.UpdateAsync(current =>
        {
            var now = ReadForWrite(current, out var baseSettings);
            var clearPlaintext = !string.IsNullOrEmpty(now.Password) && string.Equals(now.Password, replacedPassword, StringComparison.Ordinal);
            var updated = now with
            {
                Password = clearPlaintext ? null : now.Password,
                PasswordInCredentialStore = false,
            };
            return WithoutAsidePassword(updated == now ? current : WithSection(baseSettings, updated));
        }, ct).ConfigureAwait(false);

        Log.Deleted(_logger, store.BackendName);
        return QrzPasswordWriteOutcome.Saved;
    }

    private async Task<bool> VerifyAsync(ICredentialStore store, string expected, CancellationToken ct)
    {
        var read = await BoundedAsync(store.GetAsync(Key, allowPrompt: false, ct), allowPrompt: false, CredentialRead.Unavailable(CredentialReasons.TimedOut), "verify", ct).ConfigureAwait(false);
        return read.Status == CredentialReadStatus.Found && string.Equals(read.Secret, expected, StringComparison.Ordinal);
    }

    /// <summary>Awaits a store call for at most the configured timeout; on timeout returns
    /// <paramref name="onTimeout"/>. The abandoned call keeps running but no longer holds anyone up.</summary>
    private async Task<T> BoundedAsync<T>(Task<T> call, bool allowPrompt, T onTimeout, string operation, CancellationToken ct, bool mutating = false)
    {
        var timeout = allowPrompt ? _options.PromptCallTimeout : _options.StoreCallTimeout;
        try
        {
            return await call.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.StoreCallTimedOut(_logger, operation, timeout.TotalSeconds);
            if (mutating)
            {
                Volatile.Write(ref _abandonedMutation, call);
            }

            return onTimeout;
        }
    }

    private static QrzPasswordWriteOutcome ToOutcome(CredentialWrite write) => write.Status switch
    {
        CredentialWriteStatus.Success => QrzPasswordWriteOutcome.Saved,
        CredentialWriteStatus.Unavailable => QrzPasswordWriteOutcome.KeyringUnavailable,
        _ => QrzPasswordWriteOutcome.KeyringFailed,
    };

    /// <summary>For the read paths only: an unreadable section yields defaults (logged), so a keyring
    /// password is still found and a locked keyring is still reported as locked.</summary>
    private async Task<QrzLookupSettings> LoadSectionForReadAsync(CancellationToken ct)
    {
        var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        if (settings.TryReadSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings, out var section, out var error))
        {
            return section ?? new QrzLookupSettings();
        }

        Log.SectionUnreadableOnRead(_logger, error);
        return new QrzLookupSettings();
    }

    /// <summary>The section a password write starts from. An unreadable one yields defaults and
    /// <paramref name="baseSettings"/> has it set aside, so the write keeps the raw text.</summary>
    private QrzLookupSettings ReadForWrite(AppSettings current, out AppSettings baseSettings)
    {
        if (current.TryReadSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings, out var section, out var error))
        {
            baseSettings = current;
            return section ?? new QrzLookupSettings();
        }

        Log.SectionSetAside(_logger, current.Sections.ContainsKey(QrzLookupSettings.SectionKey + AppSettingsSectionExtensions.UnreadableSuffix), error);
        baseSettings = current.SetSectionAside(QrzLookupSettings.SectionKey);
        return new QrzLookupSettings();
    }

    private static AppSettings WithSection(AppSettings settings, QrzLookupSettings section) =>
        settings.WithSection(QrzLookupSettings.SectionKey, section, QrzLookupSettingsJsonContext.Default.QrzLookupSettings);

    public void Dispose() => _gate.Dispose();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Unreadable QrzLookup settings section kept as QrzLookup.unreadable (replaced an earlier copy: {ReplacedEarlier}) before saving the QRZ password state")]
        public static partial void SectionSetAside(ILogger logger, bool replacedEarlier, Exception? ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "QrzLookup settings section is unreadable; QRZ password migration skipped")]
        public static partial void MigrationSkippedUnreadable(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ password is in {Backend} but its settings flag was missing; flag restored")]
        public static partial void CredentialStoreFlagRepaired(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Information, Message = "Old QRZ password removed from QrzLookup.unreadable after a new password write")]
        public static partial void AsidePasswordRemoved(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ password stored in {Backend}")]
        public static partial void Stored(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ password deleted from {Backend}")]
        public static partial void Deleted(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ password migrated from settings.json to {Backend}")]
        public static partial void Migrated(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ password migration to {Backend} skipped ({Status}: {Reason}); will retry next start")]
        public static partial void MigrationSkipped(ILogger logger, string backend, CredentialWriteStatus status, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password migration to {Backend} failed verification; settings.json left untouched")]
        public static partial void MigrationVerifyFailed(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ password in settings.json changed during migration; plaintext copy left untouched")]
        public static partial void MigrationRaced(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password migration failed; settings.json left untouched")]
        public static partial void MigrationFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password read from {Backend} unavailable: {Reason}")]
        public static partial void StoreReadUnavailable(ILogger logger, string backend, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QrzLookup settings section is unreadable; its password and keyring flag are ignored for this read")]
        public static partial void SectionUnreadableOnRead(ILogger logger, Exception? ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password read failed")]
        public static partial void ReadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password write to {Backend} did not succeed ({Status}: {Reason})")]
        public static partial void StoreWriteFailed(ILogger logger, string backend, CredentialWriteStatus status, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password write to {Backend} failed verification")]
        public static partial void WriteVerifyFailed(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password delete from {Backend} did not succeed ({Status}: {Reason})")]
        public static partial void StoreDeleteFailed(ILogger logger, string backend, CredentialWriteStatus status, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password write failed")]
        public static partial void WriteFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password change refused: an earlier timed-out {Backend} write/delete is still running")]
        public static partial void MutationBlockedByAbandonedCall(ILogger logger, string backend);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ credential store {Operation} timed out after {Seconds} s")]
        public static partial void StoreCallTimedOut(ILogger logger, string operation, double seconds);
    }
}

/// <summary>Result of <see cref="QrzCredentialService.WriteAsync"/>. Backend detail is logged, not carried.</summary>
public enum QrzPasswordWriteOutcome
{
    Saved,

    /// <summary>Keyring locked, prompt dismissed/timed out, or the daemon did not answer. Nothing changed.</summary>
    KeyringUnavailable,

    /// <summary>The keyring rejected the write/delete. Nothing changed.</summary>
    KeyringFailed,

    /// <summary>The keyring accepted the write but did not return the same value when read back;
    /// settings.json was not touched.</summary>
    KeyringVerifyFailed,
}

/// <summary>Tuning for <see cref="QrzCredentialService"/>; defaults are the production values.</summary>
public sealed record QrzCredentialServiceOptions
{
    /// <summary>Credential-store key. Only tests against a real keyring use anything else.</summary>
    public string CredentialKey { get; init; } = QrzCredentialService.CredentialKey;

    /// <summary>Bound on one non-prompting store call; 25 s matches libdbus's default method-call timeout.</summary>
    public TimeSpan StoreCallTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Bound on a prompting store call: the store's own 60 s prompt wait plus its D-Bus calls.</summary>
    public TimeSpan PromptCallTimeout { get; init; } = TimeSpan.FromMinutes(3);
}
