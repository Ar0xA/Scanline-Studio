using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>The persisted, flat settings-section counterpart to <see cref="RadioConnectionSpec"/> —
/// see that type's own doc comment for why persistence needs a separate DTO (the polymorphic
/// <c>RadioConnectionSpec</c> hierarchy isn't JSON-source-gen-friendly across optional backend
/// assemblies). <c>"none"</c>/<c>"rigctld"</c>/<c>"hamlib"</c> are meaningful — the Hamlib fields
/// mirror <see cref="HamlibConnectionSpec"/>'s own shape exactly.</summary>
public sealed record RadioConnectionSettings
{
    public const string SectionKey = "Radio";

    public string BackendId { get; init; } = "none";

    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary>Hamlib's own <c>rig_model_t</c> — see <see cref="HamlibConnectionSpec"/>'s doc
    /// comment for why there is no ScanlineStudio-side rig registry to resolve this from a friendly
    /// name instead.</summary>
    public uint? HamlibModel { get; init; }

    public string? SerialPort { get; init; }

    public int? BaudRate { get; init; }

    public string? PttType { get; init; }
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
                },
            _ => new NoneConnectionSpec(),
        };
}
