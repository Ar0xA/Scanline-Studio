namespace ScanlineStudio.Core.Sstv;

/// <summary>Decoder-behavior toggles -- currently just AFC. See
/// <see cref="AnalogFmSstvDecoder"/>'s own constructor doc comment: legacy's AFC is always-on with
/// no user-facing off switch of its own; this is a new, non-legacy-ported toggle.
///
/// <b>Nullable, not a plain <see cref="bool"/> with a <c>= true</c> initializer</b> -- System.Text.Json
/// does not honor property-initializer defaults for <c>init</c>-only properties absent from the JSON
/// payload (see <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>'s doc comment for
/// the full explanation). The desired default here is <see langword="true"/> (matches today's
/// always-on behavior), which is NOT the CLR default for <see cref="bool"/> (<see langword="false"/>)
/// -- so a settings.json saved before this field existed must not silently disable AFC for every
/// existing install. Treat <see langword="null"/> as "unset -- apply the desired <see langword="true"/>
/// default" at the one read site (<c>ScanlineStudio.Host.Program</c>'s <c>ISstvDecoder</c>
/// registration), never re-add a non-null default value here.</summary>
public sealed record SstvDecoderSettings
{
    public const string SectionKey = "SstvDecoder";

    public bool? AfcEnabled { get; init; }
}
