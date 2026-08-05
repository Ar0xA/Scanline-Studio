using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>The persisted, flat settings-section counterpart to <see cref="RadioConnectionSpec"/> —
/// see that type's own doc comment for why persistence needs a separate DTO (the polymorphic
/// <c>RadioConnectionSpec</c> hierarchy isn't JSON-source-gen-friendly across optional backend
/// assemblies). Only <c>"none"</c>/<c>"rigctld"</c> are meaningful today — see spec/14-roadmap.md's
/// Phase 3 scope (linked Hamlib is wired later; adding its own settings fields here is a small,
/// additive change when that happens, not a redesign).</summary>
public sealed record RadioConnectionSettings
{
    public const string SectionKey = "Radio";

    public string BackendId { get; init; } = "none";

    public string? Host { get; init; }

    public int? Port { get; init; }
}

public static class RadioConnectionSettingsExtensions
{
    /// <summary>Never throws on an incomplete/unknown selection — falls back to
    /// <see cref="NoneConnectionSpec"/>, matching "no radio" being a first-class, always-valid state
    /// (spec/02-radio-layer.md) rather than a configuration error.</summary>
    public static RadioConnectionSpec ToConnectionSpec(this RadioConnectionSettings settings) =>
        settings.BackendId switch
        {
            "rigctld" when settings.Host is { Length: > 0 } host && settings.Port is int port
                => new RigctldConnectionSpec(host, port),
            _ => new NoneConnectionSpec(),
        };
}
