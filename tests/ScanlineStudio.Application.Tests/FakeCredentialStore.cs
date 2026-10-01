using ScanlineStudio.Abstractions.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>In-memory <see cref="ICredentialStore"/> that records every call (with its allowPrompt flag)
/// and exposes hooks for locked keyrings, failures, and interleavings.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Items { get; } = new();

    public List<(string Operation, bool AllowPrompt)> Calls { get; } = new();

    public bool IsSecure { get; init; } = true;

    public string BackendName => "fake";

    /// <summary>Without allowPrompt every Get of an existing item/Set/Delete returns Unavailable; a call
    /// with allowPrompt "shows the prompt" and unlocks.</summary>
    public bool Locked { get; set; }

    /// <summary>Returned by every Set instead of storing.</summary>
    public CredentialWrite? SetResult { get; set; }

    /// <summary>Returned by every Delete instead of deleting.</summary>
    public CredentialWrite? DeleteResult { get; set; }

    /// <summary>When set, Get returns this secret instead of the stored one (verify-mismatch simulation).</summary>
    public string? GetOverride { get; set; }

    /// <summary>Runs inside Set, after storing — lets a test change settings.json mid-sequence.</summary>
    public Action? OnSet { get; set; }

    /// <summary>When set, a Set/Delete with allowPrompt awaits this before completing (a prompt on screen).</summary>
    public Task? PromptGate { get; set; }

    public TaskCompletionSource PromptShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool AnyPromptAllowed => Calls.Any(c => c.AllowPrompt);

    public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default)
    {
        Calls.Add(("get", allowPrompt));
        if (!Items.TryGetValue(key, out var value))
        {
            return Task.FromResult(CredentialRead.Absent);
        }

        if (Locked)
        {
            if (!allowPrompt)
            {
                return Task.FromResult(CredentialRead.Unavailable("locked"));
            }

            Locked = false;
        }

        return Task.FromResult(CredentialRead.Found(GetOverride ?? value));
    }

    public async Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default)
    {
        Calls.Add(("set", allowPrompt));
        if (Locked)
        {
            if (!allowPrompt)
            {
                return CredentialWrite.Unavailable("locked");
            }

            await ShowPromptAsync(ct).ConfigureAwait(false);
            Locked = false;
        }

        if (SetResult is { } result)
        {
            return result;
        }

        Items[key] = secret;
        OnSet?.Invoke();
        return CredentialWrite.Success;
    }

    public async Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default)
    {
        Calls.Add(("delete", allowPrompt));
        if (Locked)
        {
            if (!allowPrompt)
            {
                return CredentialWrite.Unavailable("locked");
            }

            await ShowPromptAsync(ct).ConfigureAwait(false);
            Locked = false;
        }

        if (DeleteResult is { } result)
        {
            return result;
        }

        Items.Remove(key);
        return CredentialWrite.Success;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        Calls.Add(("exists", false));
        return Task.FromResult(Items.ContainsKey(key));
    }

    private async Task ShowPromptAsync(CancellationToken ct)
    {
        PromptShown.TrySetResult();
        if (PromptGate is { } gate)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
