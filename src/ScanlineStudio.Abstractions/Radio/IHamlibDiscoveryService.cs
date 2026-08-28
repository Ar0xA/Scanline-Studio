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

/// <summary>Options-dialog-facing seam for applying a NEW Hamlib library path LIVE, without an app
/// restart (restart-required-settings backlog item 5, 2026-08-28). Implemented by
/// <c>ScanlineStudio.Core.Radio.Hamlib.HamlibProtocolFactory</c> only -- same "optional side-channel,
/// `is`-test, no-op if absent" convention this backlog already established for
/// <c>ISstvDecoderReconfiguration</c>/<c>ISstvEncoderReconfiguration</c>/
/// <c>IWaterfallSourceReconfiguration</c>. Lives here (not <c>ScanlineStudio.Core.Radio.Hamlib</c>)
/// for the same reason <see cref="IHamlibDiscoveryService"/> does -- so <c>ScanlineStudio.UI</c>/
/// <c>ScanlineStudio.Application</c> can reference it without a direct dependency on the optional
/// Hamlib module.
///
/// Deliberately NOT the same operation as <see cref="IHamlibDiscoveryService.ProbeAsync"/>: a probe
/// builds a throwaway <c>HamlibRuntime</c> purely to report back a <see cref="HamlibProbeResult"/>,
/// never touching the real, already-connected singleton. <see cref="ReloadLibraryAsync"/> installs
/// its result into that real singleton -- every FUTURE connection attempt uses it, but an ALREADY-
/// open Hamlib connection is unaffected (it captured its own native function pointers at
/// construction, with no live reference back to whichever runtime is currently installed) and keeps
/// running on the previous library until it naturally reconnects. Each call performs a fresh native
/// load that is NEVER unloaded (matching <see cref="IHamlibDiscoveryService.ProbeAsync"/>'s own
/// established, already-shipped behavior) -- only call this when the configured path has actually
/// changed, not unconditionally on every Save, or every Save leaks another loaded copy.</summary>
public interface IHamlibLibraryReconfiguration
{
    /// <summary>Builds a fresh Hamlib runtime at <paramref name="overridePath"/> (<see langword="null"/>
    /// re-runs auto-detection, matching <c>HamlibLibraryLocator</c>'s own tier-1-vs-tiers-2/3
    /// contract) and installs it if -- and only if -- it resolves to something usable. On failure,
    /// whatever was previously installed (a working library, OR an already-unavailable state) is left
    /// untouched. Never throws for a bad/unavailable path -- that's reported via
    /// <see cref="HamlibLibraryReloadResult.Applied"/>/<see cref="HamlibLibraryReloadResult.Attempts"/>,
    /// same as <see cref="IHamlibDiscoveryService.ProbeAsync"/>'s own contract. CAN throw
    /// <see cref="TimeoutException"/> if a concurrent reload is still in progress past this call's own
    /// bounded wait -- a caller must treat that as a real failure, not swallow it silently.</summary>
    Task<HamlibLibraryReloadResult> ReloadLibraryAsync(string? overridePath, CancellationToken ct = default);
}

/// <summary>Result of <see cref="IHamlibLibraryReconfiguration.ReloadLibraryAsync"/>.</summary>
public sealed record HamlibLibraryReloadResult(
    bool Applied,
    string? ResolvedPath,
    string? Version,
    IReadOnlyList<string> Attempts);
