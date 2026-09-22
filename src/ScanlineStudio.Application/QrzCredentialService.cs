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
/// credential store under <see cref="CredentialKey"/>. <see cref="QrzLookupSettings.PasswordInCredentialStore"/>
/// records that the store holds it.
///
/// Concurrency invariant: <see cref="_gate"/> serializes every write/verify/compare-and-clear sequence
/// (Options save vs. startup migration) but is NEVER held while a keyring prompt is on screen — a
/// prompting store call runs outside the gate, and the verify + compare-and-clear that follows re-enters
/// it. Every settings.json change is a compare-and-clear inside <see cref="ISettingsStore.UpdateAsync"/>,
/// so a value changed by anyone else in between is never cleared.</summary>
public sealed partial class QrzCredentialService : IDisposable
{
    public const string CredentialKey = "qrz-lookup-password";

    private readonly ISettingsStore _settingsStore;
    private readonly CredentialStoreResolver _storeResolver;
    private readonly ILogger<QrzCredentialService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QrzCredentialService(ISettingsStore settingsStore, CredentialStoreResolver storeResolver, ILogger<QrzCredentialService> logger)
    {
        _settingsStore = settingsStore;
        _storeResolver = storeResolver;
        _logger = logger;
    }

    /// <summary>Whether this session stores the password in a real OS keyring.</summary>
    public async Task<bool> IsSecureAsync(CancellationToken ct = default) =>
        (await _storeResolver.GetAsync(ct).ConfigureAwait(false)).IsSecure;

    /// <summary>Never throws except for cancellation. Only a user-started action may pass
    /// <paramref name="allowPrompt"/> = <see langword="true"/>.</summary>
    public async Task<CredentialRead> ReadAsync(bool allowPrompt, CancellationToken ct = default)
    {
        try
        {
            var section = await LoadSectionAsync(ct).ConfigureAwait(false);
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

            var read = await store.GetAsync(CredentialKey, allowPrompt, ct).ConfigureAwait(false);
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
            var section = await LoadSectionAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(section.Password))
            {
                return true;
            }

            var store = await _storeResolver.GetAsync(ct).ConfigureAwait(false);
            return store.IsSecure && await store.ExistsAsync(CredentialKey, ct).ConfigureAwait(false);
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
    /// With a keyring present a failure is returned, never downgraded to a plaintext write.</summary>
    public async Task<CredentialWrite> WriteAsync(string? newPassword, string? replacedPassword, CancellationToken ct = default)
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
            return CredentialWrite.Failed(ex.Message);
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
                var section = await LoadSectionAsync(ct).ConfigureAwait(false);
                var plaintext = section.Password;
                if (string.IsNullOrEmpty(plaintext))
                {
                    return;
                }

                var set = await store.SetAsync(CredentialKey, plaintext, allowPrompt: false, ct).ConfigureAwait(false);
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
                    var now = GetSection(current);
                    if (!string.Equals(now.Password, plaintext, StringComparison.Ordinal))
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

    private async Task<CredentialWrite> WritePlaintextAsync(string? newPassword, CancellationToken ct)
    {
        var value = string.IsNullOrEmpty(newPassword) ? null : newPassword;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settingsStore.UpdateAsync(current =>
            {
                var now = GetSection(current);
                return string.Equals(now.Password, value, StringComparison.Ordinal)
                    ? current
                    : WithSection(current, now with { Password = value });
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return CredentialWrite.Success;
    }

    private async Task<CredentialWrite> SetSecureAsync(ICredentialStore store, string newPassword, string? replacedPassword, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var set = await store.SetAsync(CredentialKey, newPassword, allowPrompt: false, ct).ConfigureAwait(false);
            if (set.IsSuccess)
            {
                return await FinishSetAsync(store, newPassword, replacedPassword, ct).ConfigureAwait(false);
            }

            if (set.Status != CredentialWriteStatus.Unavailable)
            {
                Log.StoreWriteFailed(_logger, store.BackendName, set.Status, set.Reason);
                return set;
            }
        }
        finally
        {
            _gate.Release();
        }

        // Keyring locked: prompt outside the gate, then re-enter it to verify and clear.
        var prompted = await store.SetAsync(CredentialKey, newPassword, allowPrompt: true, ct).ConfigureAwait(false);
        if (!prompted.IsSuccess)
        {
            Log.StoreWriteFailed(_logger, store.BackendName, prompted.Status, prompted.Reason);
            return prompted;
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
    private async Task<CredentialWrite> FinishSetAsync(ICredentialStore store, string newPassword, string? replacedPassword, CancellationToken ct)
    {
        if (!await VerifyAsync(store, newPassword, ct).ConfigureAwait(false))
        {
            Log.WriteVerifyFailed(_logger, store.BackendName);
            return CredentialWrite.Failed("The keyring did not return the password that was just stored.");
        }

        await _settingsStore.UpdateAsync(current =>
        {
            var now = GetSection(current);
            var clearPlaintext = !string.IsNullOrEmpty(now.Password)
                && (string.Equals(now.Password, replacedPassword, StringComparison.Ordinal) || string.Equals(now.Password, newPassword, StringComparison.Ordinal));
            var updated = now with
            {
                Password = clearPlaintext ? null : now.Password,
                PasswordInCredentialStore = true,
            };
            return updated == now ? current : WithSection(current, updated);
        }, ct).ConfigureAwait(false);

        Log.Stored(_logger, store.BackendName);
        return CredentialWrite.Success;
    }

    private async Task<CredentialWrite> DeleteSecureAsync(ICredentialStore store, string? replacedPassword, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var deleted = await store.DeleteAsync(CredentialKey, allowPrompt: false, ct).ConfigureAwait(false);
            if (deleted.IsSuccess)
            {
                return await FinishDeleteAsync(store, replacedPassword, ct).ConfigureAwait(false);
            }

            if (deleted.Status != CredentialWriteStatus.Unavailable)
            {
                Log.StoreDeleteFailed(_logger, store.BackendName, deleted.Status, deleted.Reason);
                return deleted;
            }
        }
        finally
        {
            _gate.Release();
        }

        var prompted = await store.DeleteAsync(CredentialKey, allowPrompt: true, ct).ConfigureAwait(false);
        if (!prompted.IsSuccess)
        {
            Log.StoreDeleteFailed(_logger, store.BackendName, prompted.Status, prompted.Reason);
            return prompted;
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
    private async Task<CredentialWrite> FinishDeleteAsync(ICredentialStore store, string? replacedPassword, CancellationToken ct)
    {
        await _settingsStore.UpdateAsync(current =>
        {
            var now = GetSection(current);
            var clearPlaintext = !string.IsNullOrEmpty(now.Password) && string.Equals(now.Password, replacedPassword, StringComparison.Ordinal);
            var updated = now with
            {
                Password = clearPlaintext ? null : now.Password,
                PasswordInCredentialStore = false,
            };
            return updated == now ? current : WithSection(current, updated);
        }, ct).ConfigureAwait(false);

        Log.Deleted(_logger, store.BackendName);
        return CredentialWrite.Success;
    }

    private static async Task<bool> VerifyAsync(ICredentialStore store, string expected, CancellationToken ct)
    {
        var read = await store.GetAsync(CredentialKey, allowPrompt: false, ct).ConfigureAwait(false);
        return read.Status == CredentialReadStatus.Found && string.Equals(read.Secret, expected, StringComparison.Ordinal);
    }

    private async Task<QrzLookupSettings> LoadSectionAsync(CancellationToken ct) =>
        GetSection(await _settingsStore.LoadAsync(ct).ConfigureAwait(false));

    private static QrzLookupSettings GetSection(AppSettings settings) =>
        settings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();

    private static AppSettings WithSection(AppSettings settings, QrzLookupSettings section) =>
        settings.WithSection(QrzLookupSettings.SectionKey, section, QrzLookupSettingsJsonContext.Default.QrzLookupSettings);

    public void Dispose() => _gate.Dispose();

    private static partial class Log
    {
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
    }
}
