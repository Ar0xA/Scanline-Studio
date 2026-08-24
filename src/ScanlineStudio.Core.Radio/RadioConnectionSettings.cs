using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>The persisted, flat settings-section counterpart to <see cref="RadioConnectionSpec"/> —
/// see that type's own doc comment for why persistence needs a separate DTO (the polymorphic
/// <c>RadioConnectionSpec</c> hierarchy isn't JSON-source-gen-friendly across optional backend
/// assemblies). <c>"none"</c>/<c>"rigctld"</c>/<c>"hamlib"</c>/<c>"flrig"</c> are meaningful — the
/// Hamlib fields mirror <see cref="HamlibConnectionSpec"/>'s own shape exactly. flrig gets its own
/// <see cref="FlrigHost"/>/<see cref="FlrigPort"/> fields rather than reusing <see cref="Host"/>/
/// <see cref="Port"/> — different service, different default port, and sharing would let editing one
/// backend's panel spuriously invalidate the other's test state.</summary>
public sealed record RadioConnectionSettings
{
    public const string SectionKey = "Radio";

    public string BackendId { get; init; } = "none";

    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary>Defaults to the loopback address -- flrig conventionally runs on the same machine as
    /// its XML-RPC client in a typical single-computer shack setup.</summary>
    public string? FlrigHost { get; init; } = "127.0.0.1";

    /// <summary>flrig's own default XML-RPC port, 12345 (verified against a local flrig source clone,
    /// <c>src/support/status.cxx</c>).</summary>
    public int? FlrigPort { get; init; } = 12345;

    /// <summary>Hamlib's own <c>rig_model_t</c> — see <see cref="HamlibConnectionSpec"/>'s doc
    /// comment for why there is no ScanlineStudio-side rig registry to resolve this from a friendly
    /// name instead.</summary>
    public uint? HamlibModel { get; init; }

    /// <summary>Discovery-order tier-1 user override path (spec/03-cat-layer.md's "Discovery order") --
    /// a specific <c>libhamlib</c> file the user browsed to or an auto-detected path they accepted.
    /// <see langword="null"/> means "run auto-detection" (tiers 2/3), matching
    /// <c>HamlibLibraryLocator</c>'s own null-means-auto-detect contract exactly.</summary>
    public string? HamlibLibraryPath { get; init; }

    public string? SerialPort { get; init; }

    public int? BaudRate { get; init; }

    public string? PttType { get; init; }

    public string? PttPort { get; init; }
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
            "hamlib" when settings.HamlibModel is uint model
                => new HamlibConnectionSpec(model)
                {
                    SerialPort = settings.SerialPort,
                    BaudRate = settings.BaudRate,
                    PttType = settings.PttType,
                    PttPort = settings.PttPort,
                },
            "flrig" when settings.FlrigHost is { Length: > 0 } flrigHost && settings.FlrigPort is int flrigPort
                => new FlrigConnectionSpec(flrigHost, flrigPort),
            _ => new NoneConnectionSpec(),
        };
}
