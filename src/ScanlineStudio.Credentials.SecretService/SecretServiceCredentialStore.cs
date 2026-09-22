using System.Text;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Settings;
using Tmds.DBus.Protocol;

namespace ScanlineStudio.Credentials.SecretService;

/// <summary><see cref="ICredentialStore"/> over the freedesktop Secret Service D-Bus API (gnome-keyring,
/// KWallet's secret-service bridge, KeePassXC). Items live in the <c>default</c> collection, labelled
/// "Scanline Studio — …" and keyed by the attributes <c>{application: ScanlineStudio, key: &lt;key&gt;}</c>.
///
/// Uses the <c>plain</c> (unencrypted) transfer session: the secret crosses the user's own session bus,
/// which only processes running as the same user can reach — the same threat model as the plaintext
/// settings.json this replaces, so a DH-encrypted session would add code without adding protection.
///
/// Prompts: <c>Unlock</c>/<c>CreateItem</c>/<c>Delete</c> may hand back a prompt object. With
/// <c>allowPrompt: false</c> that is reported as Unavailable and no UI is shown. With
/// <c>allowPrompt: true</c> the prompt is shown and awaited for at most <see cref="PromptTimeout"/>,
/// then dismissed. A prompt that is not going to be shown, or whose wait is cancelled, is dismissed too.
/// Every other D-Bus call is bounded by <see cref="CallTimeout"/>; a timeout reads as Unavailable.
///
/// Each operation opens its own bus connection and session, so no connection state outlives a call.</summary>
public sealed partial class SecretServiceCredentialStore : ICredentialStore
{
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Bound on every non-prompting D-Bus call, matching libdbus's default method-call timeout, so a
    /// hung daemon surfaces as Unavailable instead of blocking the caller forever.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);

    internal const string ApplicationAttributeValue = "ScanlineStudio";

    private const string ServiceName = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string SessionInterface = "org.freedesktop.Secret.Session";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IsLockedError = "org.freedesktop.Secret.Error.IsLocked";
    private const string NoPrompt = "/";
    private const string ContentType = "text/plain; charset=utf8";

    private readonly string _busAddress;
    private readonly ILogger _logger;

    private SecretServiceCredentialStore(string busAddress, ILogger logger)
    {
        _busAddress = busAddress;
        _logger = logger;
    }

    public bool IsSecure => true;

    public string BackendName => "Secret Service";

    /// <summary>Returns a store only when a session bus exists, <c>org.freedesktop.secrets</c> is running
    /// or activatable, a <c>plain</c> session opens, and a default collection exists. Never prompts.</summary>
    public static async Task<SecretServiceCredentialStore?> TryCreateAsync(ILogger logger, CancellationToken ct = default)
    {
        var address = Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            Log.ProbeNoSessionBus(logger);
            return null;
        }

        try
        {
            using var connection = new Connection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            var running = await connection.ListServicesAsync().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            if (!running.Contains(ServiceName, StringComparer.Ordinal))
            {
                var activatable = await connection.ListActivatableServicesAsync().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
                if (!activatable.Contains(ServiceName, StringComparer.Ordinal))
                {
                    Log.ProbeNoService(logger);
                    return null;
                }
            }

            var sessionPath = await OpenSessionAsync(connection, ct).ConfigureAwait(false);
            try
            {
                var collection = await ReadDefaultCollectionAsync(connection, ct).ConfigureAwait(false);
                if (collection is null)
                {
                    Log.ProbeNoDefaultCollection(logger);
                    return null;
                }
            }
            finally
            {
                await CloseSessionAsync(connection, sessionPath).ConfigureAwait(false);
            }

            return new SecretServiceCredentialStore(address, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ProbeFailed(logger, ex);
            return null;
        }
    }

    public async Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default)
    {
        try
        {
            await using var session = await SecretSession.OpenAsync(_busAddress, ct).ConfigureAwait(false);
            var (unlocked, locked) = await SearchAsync(session.Connection, key, ct).ConfigureAwait(false);
            var item = await NewestAsync(session.Connection, unlocked.Concat(locked), ct).ConfigureAwait(false);
            if (item is null)
            {
                return CredentialRead.Absent;
            }

            if (locked.Contains(item, StringComparer.Ordinal))
            {
                if (!allowPrompt)
                {
                    return CredentialRead.Unavailable("keyring item is locked");
                }

                if (!await UnlockAsync(session.Connection, [item], ct).ConfigureAwait(false))
                {
                    return CredentialRead.Unavailable("keyring unlock was dismissed or timed out");
                }
            }

            var secret = await GetSecretAsync(session.Connection, item, session.Path, ct).ConfigureAwait(false);
            return CredentialRead.Found(secret);
        }
        catch (DBusException ex) when (ex.ErrorName == IsLockedError)
        {
            return CredentialRead.Unavailable("keyring is locked");
        }
        catch (TimeoutException)
        {
            Log.CallTimedOut(_logger, "read", CallTimeout.TotalSeconds);
            return CredentialRead.Unavailable("D-Bus call timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(_logger, "read", ex);
            return CredentialRead.Unavailable(ex.Message);
        }
    }

    public async Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default)
    {
        try
        {
            await using var session = await SecretSession.OpenAsync(_busAddress, ct).ConfigureAwait(false);
            var connection = session.Connection;
            var collection = await ReadDefaultCollectionAsync(connection, ct).ConfigureAwait(false);
            if (collection is null)
            {
                return CredentialWrite.Unavailable("no default keyring collection");
            }

            if (await GetBoolPropertyAsync(connection, collection, CollectionInterface, "Locked", ct).ConfigureAwait(false))
            {
                if (!allowPrompt)
                {
                    return CredentialWrite.Unavailable("keyring is locked");
                }

                if (!await UnlockAsync(connection, [collection], ct).ConfigureAwait(false))
                {
                    return CredentialWrite.Unavailable("keyring unlock was dismissed or timed out");
                }
            }

            var secretBytes = Encoding.UTF8.GetBytes(secret);
            string prompt;
            try
            {
                prompt = await connection.CallMethodAsync(
                    BuildCreateItem(connection, collection, session.Path, key, secretBytes),
                    static (m, _) => ReadObjectPathPair(m).Second,
                    null).WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(secretBytes);
            }

            if (prompt != NoPrompt)
            {
                if (!allowPrompt)
                {
                    await DismissQuietlyAsync(connection, prompt).ConfigureAwait(false);
                    return CredentialWrite.Unavailable("keyring requires a prompt");
                }

                if (!await RunPromptAsync(connection, prompt, ct).ConfigureAwait(false))
                {
                    return CredentialWrite.Unavailable("keyring prompt was dismissed or timed out");
                }
            }

            await DeleteDuplicatesAsync(connection, key, ct).ConfigureAwait(false);
            return CredentialWrite.Success;
        }
        catch (DBusException ex) when (ex.ErrorName == IsLockedError)
        {
            return CredentialWrite.Unavailable("keyring is locked");
        }
        catch (TimeoutException)
        {
            Log.CallTimedOut(_logger, "write", CallTimeout.TotalSeconds);
            return CredentialWrite.Unavailable("D-Bus call timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(_logger, "write", ex);
            return CredentialWrite.Failed(ex.Message);
        }
    }

    public async Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default)
    {
        try
        {
            await using var session = await SecretSession.OpenAsync(_busAddress, ct).ConfigureAwait(false);
            var connection = session.Connection;
            var (unlocked, locked) = await SearchAsync(connection, key, ct).ConfigureAwait(false);
            if (unlocked.Length == 0 && locked.Length == 0)
            {
                return CredentialWrite.Success;
            }

            if (locked.Length > 0)
            {
                if (!allowPrompt)
                {
                    return CredentialWrite.Unavailable("keyring item is locked");
                }

                if (!await UnlockAsync(connection, locked, ct).ConfigureAwait(false))
                {
                    return CredentialWrite.Unavailable("keyring unlock was dismissed or timed out");
                }
            }

            foreach (var item in unlocked.Concat(locked))
            {
                var prompt = await connection.CallMethodAsync(BuildItemDelete(connection, item), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null)
                    .WaitAsync(CallTimeout, ct).ConfigureAwait(false);
                if (prompt == NoPrompt)
                {
                    continue;
                }

                if (!allowPrompt)
                {
                    await DismissQuietlyAsync(connection, prompt).ConfigureAwait(false);
                    return CredentialWrite.Unavailable("keyring requires a prompt");
                }

                if (!await RunPromptAsync(connection, prompt, ct).ConfigureAwait(false))
                {
                    return CredentialWrite.Unavailable("keyring prompt was dismissed or timed out");
                }
            }

            return CredentialWrite.Success;
        }
        catch (DBusException ex) when (ex.ErrorName == IsLockedError)
        {
            return CredentialWrite.Unavailable("keyring is locked");
        }
        catch (TimeoutException)
        {
            Log.CallTimedOut(_logger, "delete", CallTimeout.TotalSeconds);
            return CredentialWrite.Unavailable("D-Bus call timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(_logger, "delete", ex);
            return CredentialWrite.Failed(ex.Message);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var connection = new Connection(_busAddress);
            await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            var (unlocked, locked) = await SearchAsync(connection, key, ct).ConfigureAwait(false);
            return unlocked.Length > 0 || locked.Length > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(_logger, "exists", ex);
            return false;
        }
    }

    /// <summary>Test support: <see langword="null"/> when there is no default collection, else its Locked
    /// property. Never unlocks.</summary>
    internal async Task<bool?> IsDefaultCollectionLockedAsync(CancellationToken ct = default)
    {
        using var connection = new Connection(_busAddress);
        await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        var collection = await ReadDefaultCollectionAsync(connection, ct).ConfigureAwait(false);
        return collection is null ? null : await GetBoolPropertyAsync(connection, collection, CollectionInterface, "Locked", ct).ConfigureAwait(false);
    }

    // Keeps the newest item; older duplicates (e.g. in another collection) are removed without prompting.
    private async Task DeleteDuplicatesAsync(Connection connection, string key, CancellationToken ct)
    {
        var (unlocked, locked) = await SearchAsync(connection, key, ct).ConfigureAwait(false);
        if (unlocked.Length + locked.Length < 2)
        {
            return;
        }

        var newest = await NewestAsync(connection, unlocked.Concat(locked), ct).ConfigureAwait(false);
        foreach (var item in unlocked.Where(i => !string.Equals(i, newest, StringComparison.Ordinal)))
        {
            try
            {
                await connection.CallMethodAsync(BuildItemDelete(connection, item), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null)
                    .WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            }
            catch (DBusException ex)
            {
                Log.DuplicateDeleteFailed(_logger, ex);
            }
        }
    }

    private static async Task<string?> NewestAsync(Connection connection, IEnumerable<string> items, CancellationToken ct)
    {
        string? newest = null;
        ulong newestStamp = 0;
        foreach (var item in items)
        {
            // Modified tracks replace=true rewrites; Created is the fallback for a backend that lacks it.
            var stamp = await ReadTimestampAsync(connection, item, "Modified", ct).ConfigureAwait(false);
            if (stamp == 0)
            {
                stamp = await ReadTimestampAsync(connection, item, "Created", ct).ConfigureAwait(false);
            }

            if (newest is null || stamp > newestStamp)
            {
                newest = item;
                newestStamp = stamp;
            }
        }

        return newest;
    }

    private static async Task<ulong> ReadTimestampAsync(Connection connection, string item, string property, CancellationToken ct)
    {
        try
        {
            return await connection.CallMethodAsync(
                BuildGetProperty(connection, item, ItemInterface, property),
                static (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt64(),
                null).WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        }
        catch (DBusException)
        {
            return 0;
        }
    }

    private async Task<bool> UnlockAsync(Connection connection, string[] objects, CancellationToken ct)
    {
        var prompt = await connection.CallMethodAsync(BuildUnlock(connection, objects), static (m, _) => ReadUnlockReply(m), null)
            .WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        return prompt == NoPrompt || await RunPromptAsync(connection, prompt, ct).ConfigureAwait(false);
    }

    /// <summary>Shows the prompt and waits for <c>Completed</c>. False when dismissed, timed out, or failed.</summary>
    private async Task<bool> RunPromptAsync(Connection connection, string promptPath, CancellationToken ct)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = ServiceName,
            Path = promptPath,
            Interface = PromptInterface,
            Member = "Completed",
        };
        using var subscription = await connection.AddMatchAsync(
            rule,
            static (m, _) => m.GetBodyReader().ReadBool(),
            static (ex, dismissed, _, handlerState) =>
            {
                var tcs = (TaskCompletionSource<bool>)handlerState!;
                if (ex is not null)
                {
                    tcs.TrySetException(ex);
                }
                else
                {
                    tcs.TrySetResult(dismissed);
                }
            },
            ObserverFlags.None,
            readerState: null,
            handlerState: completed,
            emitOnCapturedContext: false).AsTask().WaitAsync(CallTimeout, ct).ConfigureAwait(false);

        Log.PromptShown(_logger);
        try
        {
            await connection.CallMethodAsync(BuildPromptCall(connection, promptPath, "Prompt", withWindowId: true)).WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            var dismissed = await completed.Task.WaitAsync(PromptTimeout, ct).ConfigureAwait(false);
            return !dismissed;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await DismissQuietlyAsync(connection, promptPath).ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            Log.PromptTimedOut(_logger, PromptTimeout.TotalSeconds);
            return false;
        }
    }

    private async Task DismissQuietlyAsync(Connection connection, string promptPath)
    {
        try
        {
            await connection.CallMethodAsync(BuildPromptCall(connection, promptPath, "Dismiss", withWindowId: false)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PromptDismissFailed(_logger, ex);
        }
    }

    private static Task<(string[] Unlocked, string[] Locked)> SearchAsync(Connection connection, string key, CancellationToken ct) =>
        connection.CallMethodAsync(BuildSearchItems(connection, key), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            var unlocked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
            var locked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
            return (unlocked, locked);
        }, null).WaitAsync(CallTimeout, ct);

    private static async Task<string?> ReadDefaultCollectionAsync(Connection connection, CancellationToken ct)
    {
        var path = await connection.CallMethodAsync(BuildReadAlias(connection), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        return path == NoPrompt ? null : path;
    }

    private static Task<bool> GetBoolPropertyAsync(Connection connection, string path, string @interface, string name, CancellationToken ct) =>
        connection.CallMethodAsync(BuildGetProperty(connection, path, @interface, name), static (m, _) => m.GetBodyReader().ReadVariantValue().GetBool(), null)
            .WaitAsync(CallTimeout, ct);

    private static Task<string> GetSecretAsync(Connection connection, string item, string sessionPath, CancellationToken ct) =>
        connection.CallMethodAsync(BuildGetSecret(connection, item, sessionPath), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.AlignStruct();
            reader.ReadObjectPathAsString();
            reader.ReadArrayOfByte();
            var value = reader.ReadArrayOfByte();
            try
            {
                return Encoding.UTF8.GetString(value);
            }
            finally
            {
                Array.Clear(value);
            }
        }, null).WaitAsync(CallTimeout, ct);

    private static async Task<string> OpenSessionAsync(Connection connection, CancellationToken ct) =>
        await connection.CallMethodAsync(BuildOpenSession(connection), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }, null).WaitAsync(CallTimeout, ct).ConfigureAwait(false);

    private static async Task CloseSessionAsync(Connection connection, string sessionPath)
    {
        try
        {
            await connection.CallMethodAsync(BuildSessionClose(connection, sessionPath)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException or DisconnectedException)
        {
            // Closing the connection releases the session anyway.
        }
    }

    private static (string First, string Second) ReadObjectPathPair(Message message)
    {
        var reader = message.GetBodyReader();
        var first = reader.ReadObjectPathAsString();
        var second = reader.ReadObjectPathAsString();
        return (first, second);
    }

    private static string ReadUnlockReply(Message message)
    {
        var reader = message.GetBodyReader();
        reader.ReadArrayOfObjectPath();
        return reader.ReadObjectPathAsString();
    }

    // Message builders stay synchronous (MessageWriter is a ref struct); CreateMessage takes ownership of the pooled buffer.
    private static MessageBuffer BuildOpenSession(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "OpenSession", "sv", MessageFlags.None);
        writer.WriteString("plain");
        writer.WriteVariantString(string.Empty);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSessionClose(Connection connection, string sessionPath)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, sessionPath, SessionInterface, "Close", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildReadAlias(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "ReadAlias", "s", MessageFlags.None);
        writer.WriteString("default");
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSearchItems(Connection connection, string key)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "SearchItems", "a{ss}", MessageFlags.None);
        WriteAttributes(ref writer, key);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildUnlock(Connection connection, string[] objects)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "Unlock", "ao", MessageFlags.None);
        writer.WriteArray(objects.Select(o => new ObjectPath(o)).ToArray());
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetProperty(Connection connection, string path, string @interface, string name)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, path, PropertiesInterface, "Get", "ss", MessageFlags.None);
        writer.WriteString(@interface);
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetSecret(Connection connection, string item, string sessionPath)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, item, ItemInterface, "GetSecret", "o", MessageFlags.None);
        writer.WriteObjectPath(sessionPath);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildItemDelete(Connection connection, string item)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, item, ItemInterface, "Delete", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildCreateItem(Connection connection, string collection, string sessionPath, string key, byte[] secret)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b", MessageFlags.None);
        var properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString($"Scanline Studio — {LabelFor(key)}");
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        // A variant on the wire is its signature followed by the value.
        writer.WriteSignature("a{ss}");
        WriteAttributes(ref writer, key);
        writer.WriteDictionaryEnd(properties);
        writer.WriteStructureStart();
        writer.WriteObjectPath(sessionPath);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(secret);
        writer.WriteString(ContentType);
        writer.WriteBool(true);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildPromptCall(Connection connection, string promptPath, string member, bool withWindowId)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, promptPath, PromptInterface, member, withWindowId ? "s" : null, MessageFlags.None);
        if (withWindowId)
        {
            writer.WriteString(string.Empty);
        }

        return writer.CreateMessage();
    }

    private static void WriteAttributes(ref MessageWriter writer, string key)
    {
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("application");
        writer.WriteString(ApplicationAttributeValue);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("key");
        writer.WriteString(key);
        writer.WriteDictionaryEnd(attributes);
    }

    internal static string LabelFor(string key) => key switch
    {
        "qrz-lookup-password" => "QRZ.com password",
        _ => key,
    };

    /// <summary>One bus connection plus one open <c>plain</c> session, closed together.</summary>
    private sealed class SecretSession : IAsyncDisposable
    {
        private SecretSession(Connection connection, string path)
        {
            Connection = connection;
            Path = path;
        }

        public Connection Connection { get; }

        public string Path { get; }

        public static async Task<SecretSession> OpenAsync(string busAddress, CancellationToken ct)
        {
            var connection = new Connection(busAddress);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
                var path = await OpenSessionAsync(connection, ct).ConfigureAwait(false);
                return new SecretSession(connection, path);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseSessionAsync(Connection, Path).ConfigureAwait(false);
            Connection.Dispose();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Secret Service probe: no D-Bus session bus")]
        public static partial void ProbeNoSessionBus(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Secret Service probe: org.freedesktop.secrets is neither running nor activatable")]
        public static partial void ProbeNoService(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Secret Service probe: no default collection")]
        public static partial void ProbeNoDefaultCollection(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Secret Service probe failed")]
        public static partial void ProbeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Secret Service {Operation} failed")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Secret Service duplicate item delete failed")]
        public static partial void DuplicateDeleteFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Secret Service {Operation} timed out after {Seconds} s")]
        public static partial void CallTimedOut(ILogger logger, string operation, double seconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "Secret Service prompt shown")]
        public static partial void PromptShown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Secret Service prompt timed out after {Seconds} s; dismissed")]
        public static partial void PromptTimedOut(ILogger logger, double seconds);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Secret Service prompt dismiss failed")]
        public static partial void PromptDismissFailed(ILogger logger, Exception ex);
    }
}
