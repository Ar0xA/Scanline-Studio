using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Decoder-behavior toggles. See <see cref="AnalogFmSstvDecoder"/>'s own constructor doc
/// comment for each field's legacy basis.
///
/// <b>Nullable, not a plain <see cref="bool"/> with a <c>= true</c> initializer</b> -- System.Text.Json
/// does not honor property-initializer defaults for <c>init</c>-only properties absent from the JSON
/// payload (see <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.CaptureThreadPriority</c>'s doc
/// comment for the full explanation). The desired default for every field here is <see langword="true"/> (matches
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

    /// <summary>Options Advanced-tab PLL demodulator tuning (backlog item,
    /// `docs/plans/options-stub-item1-pll-tuning-plan.md`) -- port of legacy's real, user-editable
    /// <c>CPLL</c> fields (<c>m_vcogain</c>/<c>m_loopOrder</c>/<c>m_loopFC</c>/<c>m_outOrder</c>/
    /// <c>m_outFC</c>, `sstv.cpp:246-263`), persisted in legacy's own `[Define]` ini section
    /// (`Main.cpp:1955-1961`/`2442-2446`) unlike this doc comment's earlier confusion in an earlier
    /// plan-review round -- these ARE persisted, both in legacy and in this port. Desired defaults
    /// when absent are legacy's own real `CPLL` constructor values (1.0/1/1500.0/3/900.0), verified
    /// at 3 independent legacy sites (`sstv.cpp:246,251-254`; `sstv.cpp:1431-1436`;
    /// `Main.cpp:12211-12215`) -- NOT the Options-tab AXAML's own previously-hardcoded placeholder
    /// values (2600/1/200/1200), which were never real.</summary>
    public double? PllVcoGain { get; init; }

    public int? PllLoopOrder { get; init; }

    public double? PllLoopCutoffHz { get; init; }

    public int? PllOutputOrder { get; init; }

    public double? PllOutputCutoffHz { get; init; }

    /// <summary>Options Advanced-tab zero-crossing demodulator tuning (backlog item,
    /// `docs/plans/options-stub-item2-zerocrossing-tuning-plan.md`) -- port of legacy's real,
    /// user-editable <c>CFQC</c> fields (<c>m_Type</c>/<c>m_outOrder</c>/<c>m_outFC</c>/
    /// <c>m_SmoozFq</c>, `sstv.cpp:475-485`), persisted in legacy's own `[Define]` ini section
    /// (`Main.cpp:1938-1941`/`2437-2440`). Desired defaults when absent are legacy's own real
    /// <c>CFQC</c> constructor values (Iir/3/900.0/2200.0), verified at 3 independent legacy sites
    /// (`sstv.cpp:347-364`; `Main.cpp:1938-1941`; `Main.cpp:12252-12262`).</summary>
    public ZeroCrossingSmoothingMode? ZeroCrossingSmoothingMode { get; init; }

    public int? ZeroCrossingOutputOrder { get; init; }

    public double? ZeroCrossingOutputCutoffHz { get; init; }

    public double? ZeroCrossingSmoothingFrequencyHz { get; init; }

    /// <summary>Applies this record's documented absent-vs-out-of-range fallback rules, producing the
    /// concrete values an <see cref="AnalogFmSstvDecoder"/> constructor call needs. Single source of
    /// truth for that resolution -- previously duplicated independently in
    /// <c>ScanlineStudio.Host.Program.CreateSstvDecoder</c> and the Loopback self-test's own decoder
    /// construction; a third independent copy would risk silently drifting from the other two, which
    /// would make a self-test decode with different settings than live RX -- exactly the kind of bad
    /// demod-type/sense-level mismatch the self-test exists to catch.</summary>
    public ResolvedSstvDecoderSettings Resolve() => new(
        AfcEnabled: AfcEnabled ?? true,
        SyncRestartEnabled: SyncRestartEnabled ?? true,
        AutoSyncEnabled: AutoSyncEnabled ?? true,
        AutoStopEnabled: AutoStopEnabled ?? false,
        AutoSlantEnabled: AutoSlantEnabled ?? true,
        SenseLevel: SenseLevel ?? 1,
        // Fully qualified, not just `DemodType.Hilbert` -- this record has its own property named
        // DemodType, which shadows the type name within this member's scope.
        DemodType: DemodType is { } dt && Enum.IsDefined(dt) ? dt : Abstractions.Sstv.DemodType.Hilbert,
        RxBpfPreset: RxBpfPreset is { } bpf && Enum.IsDefined(bpf) ? bpf : Abstractions.Sstv.RxBpfPreset.Wide,
        RxBufferMode: RxBufferMode is { } rxb && Enum.IsDefined(rxb) ? rxb : Abstractions.Sstv.RxBufferMode.On,
        PllVcoGain: PllVcoGain ?? 1.0,
        // Code-review round 1 finding: unlike SenseLevel/DemodType/RxBpfPreset above, these two had
        // NO range guard at all -- legacy itself validates both to (0,32] on every edit
        // (Option.cpp:515,521). A hand-edited settings.json/preset file with order<=0 would either
        // silently disable the filter (IirFilter.Design's own pass-through at order 0) or throw
        // OverflowException from `new double[order*3]` at decoder construction for a negative value
        // (an app-start failure, not a graceful fallback) -- clamp to legacy's own real range here,
        // the single source of truth this whole method exists to be.
        PllLoopOrder: PllLoopOrder is { } lo ? Math.Clamp(lo, 1, 32) : 1,
        // Code-review round 2 finding: a LOWER bound is needed here too, same reasoning as the order
        // clamp immediately above -- legacy guards this `> 0.0` at its own apply site
        // (Option.cpp:517-518). No UPPER (Nyquist) bound here deliberately -- this method has no
        // sample-rate context (see PllFmDemodulator.ClampCutoffBelowNyquist's own doc comment for why
        // that half of the clamp lives decoder-side instead); only the "reject <= 0" half belongs
        // here.
        PllLoopCutoffHz: PllLoopCutoffHz is { } lc ? Math.Max(lc, 1.0) : 1500,
        PllOutputOrder: PllOutputOrder is { } oo ? Math.Clamp(oo, 1, 32) : 3,
        PllOutputCutoffHz: PllOutputCutoffHz is { } oc ? Math.Max(oc, 1.0) : 900,
        // Fully qualified, not just `ZeroCrossingSmoothingMode.Iir` -- this record has its own
        // property named ZeroCrossingSmoothingMode, which shadows the type name within this member's
        // scope (same reasoning as the DemodType/RxBpfPreset/RxBufferMode lines above). TWO different
        // fallbacks, not a copy of DemodType's single-fallback shape: ABSENT (null) -> Iir (CFQC's own
        // ctor default, `sstv.cpp:349`); PRESENT but out-of-range -> Off (legacy's real dispatch
        // default, `sstv.cpp:482`'s `default:` case) -- a hand-edited settings.json with an invalid
        // value must NOT silently become Iir, that would be a different (wrong) legacy behavior.
        ZeroCrossingSmoothingMode: ZeroCrossingSmoothingMode switch
        {
            null => Abstractions.Sstv.ZeroCrossingSmoothingMode.Iir,
            { } zm when Enum.IsDefined(zm) => zm,
            _ => Abstractions.Sstv.ZeroCrossingSmoothingMode.Off,
        },
        ZeroCrossingOutputOrder: ZeroCrossingOutputOrder is { } zo ? Math.Clamp(zo, 1, 32) : 3,
        ZeroCrossingOutputCutoffHz: ZeroCrossingOutputCutoffHz is { } zc ? Math.Max(zc, 1.0) : 900,
        // Both-direction clamp, applied from the start here (unlike PllLoopCutoffHz's own floor-only
        // shape above, discovered incrementally) -- legacy's real two-sided range is [500,8000]
        // (`Option.cpp:536-540`).
        ZeroCrossingSmoothingFrequencyHz: ZeroCrossingSmoothingFrequencyHz is { } zs ? Math.Clamp(zs, 500.0, 8000.0) : 2200);
}

/// <summary>Concrete, fully-resolved decoder-behavior values -- see <see cref="SstvDecoderSettings.Resolve"/>.</summary>
public sealed record ResolvedSstvDecoderSettings(
    bool AfcEnabled,
    bool SyncRestartEnabled,
    bool AutoSyncEnabled,
    bool AutoStopEnabled,
    bool AutoSlantEnabled,
    int SenseLevel,
    DemodType DemodType,
    RxBpfPreset RxBpfPreset,
    RxBufferMode RxBufferMode,
    double PllVcoGain,
    int PllLoopOrder,
    double PllLoopCutoffHz,
    int PllOutputOrder,
    double PllOutputCutoffHz,
    ZeroCrossingSmoothingMode ZeroCrossingSmoothingMode,
    int ZeroCrossingOutputOrder,
    double ZeroCrossingOutputCutoffHz,
    double ZeroCrossingSmoothingFrequencyHz);
