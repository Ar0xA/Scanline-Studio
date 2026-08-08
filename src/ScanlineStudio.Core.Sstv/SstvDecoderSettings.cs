namespace ScanlineStudio.Core.Sstv;

/// <summary>Decoder-behavior toggles. See <see cref="AnalogFmSstvDecoder"/>'s own constructor doc
/// comment for each field's legacy basis.
///
/// <b>Nullable, not a plain <see cref="bool"/> with a <c>= true</c> initializer</b> -- System.Text.Json
/// does not honor property-initializer defaults for <c>init</c>-only properties absent from the JSON
/// payload (see <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>'s doc comment for
/// the full explanation). The desired default for every field here is <see langword="true"/> (matches
/// today's always-on behavior), which is NOT the CLR default for <see cref="bool"/>
/// (<see langword="false"/>) -- so a settings.json saved before a field existed must not silently
/// disable it for every existing install. Treat <see langword="null"/> as "unset -- apply the desired
/// <see langword="true"/> default" at the one read site (<c>ScanlineStudio.Host.Program</c>'s
/// <c>ISstvDecoder</c> registration), never re-add a non-null default value here.</summary>
public sealed record SstvDecoderSettings
{
    public const string SectionKey = "SstvDecoder";

    /// <summary>Legacy's AFC is always-on with no user-facing off switch of its own; this is a new,
    /// non-legacy-ported toggle.</summary>
    public bool? AfcEnabled { get; init; }

    /// <summary>Port of legacy's real, user-toggleable <c>m_SyncRestart</c> (default 1,
    /// <c>sstv.cpp:1486</c>, toggled via the "Lock" toolbar button, <c>Main.cpp:10907</c>/<c>11887</c>)
    /// -- gates whether a stronger/cleaner sync found mid-reception is allowed to abort and restart
    /// onto it. See <see cref="AnalogFmSstvDecoder"/>'s own doc comment at its mid-reception restart
    /// call site for the full citation trail.</summary>
    public bool? SyncRestartEnabled { get; init; }

    /// <summary>Port of legacy's real, user-toggleable <c>sys.m_AutoSync</c> (default 1,
    /// <c>Main.cpp:901</c>) -- gates whether a detected sync-position drift is allowed to
    /// automatically apply the same correction the manual ReSync button applies. Gates only the two
    /// trigger branches, not the underlying drift-detection bookkeeping, which runs unconditionally
    /// in this port (see <see cref="AnalogFmSstvDecoder"/>'s own Auto Sync doc comments for why).</summary>
    public bool? AutoSyncEnabled { get; init; }
}
