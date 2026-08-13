using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Decoder-behavior toggles. See <see cref="AnalogFmSstvDecoder"/>'s own constructor doc
/// comment for each field's legacy basis.
///
/// <b>Nullable, not a plain <see cref="bool"/> with a <c>= true</c> initializer</b> -- System.Text.Json
/// does not honor property-initializer defaults for <c>init</c>-only properties absent from the JSON
/// payload (see <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>'s doc comment for
/// the full explanation). The desired default for every field here is <see langword="true"/> (matches
/// today's always-on behavior) -- <b>except <see cref="AutoStopEnabled"/></b>, whose own desired default
/// is <see langword="false"/> (legacy's real fresh-startup default too, <c>sys.m_AutoStop = 0</c>,
/// <c>Main.cpp:900</c>) -- deliberately not the CLR default for every other <see cref="bool"/> field, so
/// a settings.json saved before a field existed must not silently disable an on-by-default feature for
/// every existing install. Treat <see langword="null"/> as "unset -- apply that field's own desired
/// default" at the one read site (<c>ScanlineStudio.Host.Program</c>'s <c>ISstvDecoder</c> registration),
/// never re-add a non-null default value here.
///
/// <see cref="SenseLevel"/> is the one field here that isn't a bool: its desired default when absent
/// is <c>1</c> (legacy's real ctor default, <c>m_SenseLvl = 1</c>, <c>sstv.cpp:1489</c>) -- but a
/// PRESENT, out-of-range value (e.g. a hand-edited <c>7</c>) is deliberately mapped to <c>0</c>
/// instead, matching legacy's own <c>SetSenseLvl</c> switch <c>default:</c> branch
/// (<c>sstv.cpp:1812-1814</c>) for anything outside 1-3 -- these two fallbacks are intentionally
/// different values, not a copy-paste of each other. See <c>AnalogFmSstvDecoder.SenseLevelPresets</c>
/// for the full 4-preset table.</summary>
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

    /// <summary>Port of legacy's real, user-toggleable <c>sys.m_AutoStop</c> (fresh default OFF,
    /// <c>Main.cpp:900</c> -- unlike the other three fields here) -- gates whether erratic/weak-signal
    /// detection is allowed to stop reception and re-arm auto-detection (legacy's
    /// <c>RxAutoPush(TRUE)</c>). Gates only the trigger itself, not the underlying detection
    /// bookkeeping, which runs unconditionally in this port (see <see cref="AnalogFmSstvDecoder"/>'s
    /// own <c>TryAutoSync</c> doc comment). Caveat: legacy's "Lock" toolbar button
    /// (<c>SBLKClick</c>, <c>Main.cpp:10898-10907</c>) ties this to the same button as
    /// <see cref="SyncRestartEnabled"/>/<see cref="AutoSyncEnabled"/> -- Lock engaged means all three
    /// are off, Lock disengaged means all three are on, so this port's own default combination
    /// (the other three <see langword="true"/>, this one <see langword="false"/>) is not a state
    /// legacy's Lock button can itself produce -- it matches legacy's fresh/unmodified default only.</summary>
    public bool? AutoStopEnabled { get; init; }

    /// <summary>Port of legacy's real, user-toggleable <c>KRSA-&gt;Checked</c> (<c>Main.cpp:1863</c>'s
    /// <c>Define/AutoSlant</c> .ini key) -- gates whether <see cref="AnalogFmSstvDecoder"/>'s
    /// slant-correction commits are ever applied (the underlying drift-detection bookkeeping still
    /// runs unconditionally, same reasoning as <see cref="AutoSyncEnabled"/>/<see cref="AutoStopEnabled"/>
    /// above). Default-desired <see langword="true"/> here preserves this port's own current
    /// always-on shipped behavior -- NOT a confirmed legacy fresh-install default: the shipped
    /// <c>.ini</c>s disagree (<c>Mmsstv.ini</c>/<c>MmsstvV.ini</c> say <c>1</c>, but
    /// <c>Mmsstv English.ini</c>/<c>Mmsstv Japanese.ini</c> both say <c>0</c>), and legacy's real
    /// design-time default lives in the binary <c>Main.vlb</c>, unreadable.</summary>
    public bool? AutoSlantEnabled { get; init; }

    /// <summary>Port of legacy's real, user-selectable <c>m_SenseLvl</c> (squelch/sense level,
    /// <c>Option.dfm</c>'s <c>RGSLvl</c> radio group, 4 presets "Very low".."Very high") -- see this
    /// record's own class-level doc comment for the absent-vs-out-of-range fallback distinction, and
    /// <c>AnalogFmSstvDecoder.SenseLevelPresets</c> for the preset table itself.</summary>
    public int? SenseLevel { get; init; }

    /// <summary>Port of legacy's real, user-selectable <c>CSSTVDEM::m_Type</c> (main-picture FM
    /// demodulator algorithm, <c>Option.dfm</c>'s <c>RGDemType</c> radio group,
    /// <c>sstv.cpp:2256-2269</c>) -- another absent-vs-out-of-range case like <see cref="SenseLevel"/>
    /// above, but with matching (not different) fallback values this time: absent means "apply
    /// <see cref="DemodType.Hilbert"/>" (legacy's real compiled-in default, <c>sstv.cpp:1492</c>), and
    /// a PRESENT-but-out-of-range value (e.g. a hand-edited settings.json with an enum member this
    /// build doesn't know) ALSO clamps to <see cref="DemodType.Hilbert"/> -- a deliberate, sane
    /// divergence from legacy's own real inconsistency here (an out-of-range legacy <c>DemType</c>
    /// gets Hilbert demodulation via the switch's <c>default:</c> arm, `sstv.cpp:2265-2268`, but NOT
    /// the `m_Type==2`-gated sync-anchor correction, `Main.cpp:3794` -- this port's own clamp
    /// deliberately gets both, see the demod-type runtime-dispatch subsystem's implementation plan
    /// for the full reasoning). Not the same "different fallback" pattern <see cref="SenseLevel"/>
    /// uses -- don't copy that shape here.</summary>
    public DemodType? DemodType { get; init; }

    /// <summary>Port of legacy's real, user-selectable <c>CSSTVDEM::m_bpf</c> (RX bandpass-filter
    /// sharpness, <c>Option.dfm</c>'s <c>RGRxBPF</c> radio group, <c>sstv.cpp:1522-1550</c>'s
    /// <c>CalcBPF</c>) -- same <see cref="DemodType"/> shape, matching (not different) fallback
    /// values: absent means "apply <see cref="RxBpfPreset.Wide"/>" (legacy's real compiled-in
    /// default, <c>sstv.cpp:1416</c>, <c>m_bpf=1</c>), and a PRESENT-but-out-of-range value (e.g. a
    /// hand-edited settings.json with an enum member this build doesn't know) ALSO clamps to
    /// <see cref="RxBpfPreset.Wide"/> -- a deliberate divergence from legacy's own real behavior
    /// here, NOT a replicated bug: an out-of-range legacy <c>DEMBPF</c> reaches <c>CalcBPF</c>'s
    /// <c>default:</c> arm (<c>sstv.cpp:1546-1548</c>), which sets <c>bpftap=0</c> while <c>m_bpf</c>
    /// itself stays nonzero -- so the <c>if(m_bpf)</c> gate (<c>sstv.cpp:1826</c>) still passes and
    /// <c>m_BPF.Do</c> runs against a zero-length/stale coefficient table, a real legacy bug, not a
    /// behavior worth preserving. This port's clamp-to-Wide sidesteps it entirely, same reasoning as
    /// <see cref="DemodType"/>'s own clamp-not-replicate-the-bug decision above.</summary>
    public RxBpfPreset? RxBpfPreset { get; init; }

    /// <summary>Port of legacy's real, user-selectable <c>sys.m_UseRxBuff</c> (RX buffer mode,
    /// `Option.dfm`'s <c>RGRBuf</c> radio group, `sstv.cpp:1626-1644`'s <c>OpenCloseRxBuff</c>) --
    /// same <see cref="DemodType"/>/<see cref="RxBpfPreset"/> shape, matching (not different) fallback
    /// values: absent means "apply <see cref="Sstv.RxBufferMode.On"/>" (legacy's real compiled-in
    /// default, `Main.cpp:899`, `sys.m_UseRxBuff=1`), and a PRESENT-but-out-of-range value ALSO clamps
    /// to <see cref="Sstv.RxBufferMode.On"/> -- legacy itself has no validation here (an out-of-range
    /// <c>UseRxBuff</c> reaches most gating sites as a plain nonzero-or-not C-style int test, but
    /// `sstv.cpp:1630`'s <c>OpenCloseRxBuff</c> tests `== 1` specifically -- so an out-of-range legacy
    /// value is internally INCONSISTENT, not just unvalidated: it reads as "buffer present" at the
    /// nonzero-test sites while allocating no RAM buffer at all, `default:`-falling to
    /// <c>FreeRxBuff()</c>. Not a behavior worth preserving; this port's clamp-to-On sidesteps the
    /// inconsistency entirely, same reasoning as <see cref="RxBpfPreset"/>'s own documented `CalcBPF`
    /// bug and clamp-not-replicate decision.</summary>
    public RxBufferMode? RxBufferMode { get; init; }
}
