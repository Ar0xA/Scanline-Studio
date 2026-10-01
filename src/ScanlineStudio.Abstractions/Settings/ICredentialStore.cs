namespace ScanlineStudio.Abstractions.Settings;

/// <summary>An OS credential store (Linux Secret Service, Windows Credential Manager) or the
/// plaintext settings-file fallback. Keys are short app-scoped identifiers such as
/// <c>qrz-lookup-password</c>; the backend namespaces them itself.
///
/// <paramref name="allowPrompt"/> on every method that could need one: only a user-started action may
/// pass <see langword="true"/>. A background caller passes <see langword="false"/> and gets
/// <see cref="CredentialReadStatus.Unavailable"/>/<see cref="CredentialWriteStatus.Unavailable"/> instead of a
/// keyring dialog. Implementations never throw for an expected backend failure (locked, timeout, bus
/// error) — they return Unavailable/Failed; only cancellation propagates.</summary>
public interface ICredentialStore
{
    /// <summary><see langword="true"/> for a real OS credential store, <see langword="false"/> for the
    /// plaintext settings-file fallback.</summary>
    bool IsSecure { get; }

    /// <summary>Short, log-safe backend name ("Secret Service", "Windows Credential Manager", "settings.json").</summary>
    string BackendName { get; }

    Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default);

    Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default);

    /// <summary>Deleting a key that does not exist is <see cref="CredentialWriteStatus.Success"/>.</summary>
    Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default);

    /// <summary>Never prompts: reports a stored item even while it is locked. Any backend error → <see langword="false"/>.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

public enum CredentialReadStatus
{
    Found,
    Absent,

    /// <summary>Locked without permission to prompt, prompt dismissed/timed out, or a backend error.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="ICredentialStore.GetAsync"/>. <see cref="ToString"/> never includes the secret.</summary>
public readonly record struct CredentialRead(CredentialReadStatus Status, string? Secret, string? Reason)
{
    public static CredentialRead Found(string secret) => new(CredentialReadStatus.Found, secret, null);

    public static CredentialRead Absent { get; } = new(CredentialReadStatus.Absent, null, null);

    public static CredentialRead Unavailable(string reason) => new(CredentialReadStatus.Unavailable, null, reason);

    public override string ToString() => Status == CredentialReadStatus.Unavailable ? $"Unavailable({Reason})" : Status.ToString();
}

public enum CredentialWriteStatus
{
    Success,

    /// <summary>Locked without permission to prompt, prompt dismissed/timed out, or no usable collection.</summary>
    Unavailable,

    Failed,
}

/// <summary>Shared <see cref="CredentialRead.Reason"/>/<see cref="CredentialWrite.Reason"/> values callers compare on.</summary>
public static class CredentialReasons
{
    /// <summary>The store or its caller gave up waiting; the backend may still complete the call later.</summary>
    public const string TimedOut = "keyring call timed out";
}

/// <summary>Result of <see cref="ICredentialStore.SetAsync"/>/<see cref="ICredentialStore.DeleteAsync"/>.</summary>
public readonly record struct CredentialWrite(CredentialWriteStatus Status, string? Reason)
{
    public static CredentialWrite Success { get; } = new(CredentialWriteStatus.Success, null);

    public static CredentialWrite Unavailable(string reason) => new(CredentialWriteStatus.Unavailable, reason);

    public static CredentialWrite Failed(string reason) => new(CredentialWriteStatus.Failed, reason);

    public bool IsSuccess => Status == CredentialWriteStatus.Success;
}
