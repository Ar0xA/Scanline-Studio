namespace ScanlineStudio.Abstractions.Radio;

/// <summary>Options-dialog-facing seam for locating a linked Hamlib install and listing the rig
/// models it supports (see spec/03-cat-layer.md's "Discovery order" -- tier 1, the user override
/// path, is what this drives). Lives here rather than only in the optional
/// <c>ScanlineStudio.Core.Radio.Hamlib</c> module so <c>ScanlineStudio.UI</c> can depend on it without
/// taking a direct reference to that module (the UI-layering rule enforced by
/// <c>UiLayeringArchitectureTests</c>) -- the real implementation is registered into DI from
/// <c>ScanlineStudio.Host</c>'s <c>Program.cs</c>, same pattern as <see cref="IRadioProtocolFactory"/>.</summary>
public interface IHamlibDiscoveryService
{
    /// <summary>Loads (or re-validates) a Hamlib library, then -- only if that succeeds -- also lists
    /// every rig model it supports, since both operations need the same freshly-loaded native handle.
    /// <paramref name="overridePath"/> null means "run auto-detection" (spec/03's tiers 2/3); a
    /// non-null path is tried exclusively (tier 1), matching <c>HamlibLibraryLocator</c>'s own
    /// contract. Never throws on a failed probe -- failure is reported via
    /// <see cref="HamlibProbeResult.IsAvailable"/>/<see cref="HamlibProbeResult.Attempts"/>, since a
    /// user browsing to the wrong file is an expected, not exceptional, outcome.</summary>
    Task<HamlibProbeResult> ProbeAsync(string? overridePath, CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="IHamlibDiscoveryService.ProbeAsync"/>. <see cref="RigModels"/> is
/// empty (never null) whenever <see cref="IsAvailable"/> is false, or when listing itself failed even
/// though the library loaded -- the Options dialog falls back to the existing manual numeric Model
/// entry in either case.</summary>
public sealed record HamlibProbeResult(
    bool IsAvailable,
    string? ResolvedPath,
    string? Version,
    IReadOnlyList<string> Attempts,
    IReadOnlyList<HamlibRigModelInfo> RigModels);

/// <summary>One entry from Hamlib's own compiled-in rig catalog -- <see cref="ModelId"/> is Hamlib's
/// <c>rig_model_t</c>, the same value <c>RadioConnectionSettings.HamlibModel</c>'s doc comment already
/// describes (this is that value's source, not a new Scanline-side registry).</summary>
public sealed record HamlibRigModelInfo(uint ModelId, string Manufacturer, string ModelName);
