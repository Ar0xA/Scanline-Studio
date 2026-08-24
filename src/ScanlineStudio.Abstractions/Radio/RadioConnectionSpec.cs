namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. Describes *how* to reach a rig — a discriminated union via an
/// open abstract record base with one sealed subtype per backend (spec/03-cat-layer.md).
/// <see cref="NoneConnectionSpec"/>, <see cref="RigctldConnectionSpec"/>, <see cref="HamlibConnectionSpec"/>,
/// and <see cref="FlrigConnectionSpec"/> exist; a future OmniRig-as-client backend adds its own sealed
/// subtype here without requiring any existing code to change — <see cref="IRadioController"/> never switches on the concrete subtype
/// itself, only each registered <see cref="IRadioProtocolFactory"/>'s own <c>CanHandle</c> does (see
/// that interface's doc comment), so there is no exhaustiveness surface for a new subtype to silently
/// fall through.
///
/// <b>Not (yet) JSON-serializable as this polymorphic hierarchy</b> — noted here so a future settings-
/// persistence implementation (Phase 3/4, out of scope for the current rigctld-client work) doesn't
/// design around this record shape and hit the wall: <c>System.Text.Json</c> polymorphic
/// (de)serialization needs a closed <c>[JsonDerivedType]</c> list declared on the base type, but
/// <c>ScanlineStudio.Abstractions</c> must never reference optional backend assemblies (linked-Hamlib/OmniRig
/// modules, per CLAUDE.md's Win32/COM/P-Invoke isolation rule) to declare them. Persistence should use
/// a separate flat DTO instead (e.g. <c>RadioConnectionSettings { string BackendId; string? Host; int?
/// Port; ... }</c>) that each backend's own factory knows how to turn into its <see cref="RadioConnectionSpec"/>
/// subtype, per spec/12-settings.md's "each module's typed settings section is defined in that module's
/// own project" convention.</summary>
public abstract record RadioConnectionSpec
{
    public PollingStrategy Strategy { get; init; } = PollingStrategy.Continuous;

    /// <summary>Default matches legacy <c>PollInterval</c> (see spec/02-radio-layer.md's "Polling"
    /// section).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>"No radio" — a first-class, fully supported spec (see spec/02-radio-layer.md). SSTV and
/// logging must work with zero radios connected; <see cref="IRadioController"/> handles this via a
/// null-object <see cref="IRadioProtocol"/> resolved through the normal
/// <see cref="IRadioProtocolFactory"/> path, not a special case in the controller itself.</summary>
public sealed record NoneConnectionSpec : RadioConnectionSpec;

/// <summary>See spec/04-rigctld.md. Connects to an already-running <c>rigctld</c> (or compatible)
/// process — the daemon's own rig identity/model is opaque to ScanlineStudio (see spec/02-radio-layer.md's "Rig
/// identification").</summary>
public sealed record RigctldConnectionSpec(string Host, int Port) : RadioConnectionSpec
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>See spec/03-cat-layer.md's "Linked Hamlib: bring-your-own-libhamlib". Connects to a
/// system-installed <c>libhamlib</c> via P/Invoke — <paramref name="Model"/> is Hamlib's own
/// <c>rig_model_t</c> (there is no ScanlineStudio-side rig registry, see spec/02-radio-layer.md's "Rig
/// identification"). <see cref="BaudRate"/>/<see cref="PttType"/> are passed through to Hamlib's own
/// <c>rig_set_conf</c> config (spec/02-radio-layer.md's "PTT usage" — ScanlineStudio does not implement RTS/DTR
/// itself). The manual library-path override (spec/03's discovery-order tier 3) is deliberately
/// <i>not</i> part of this spec — it's a one-time app-level setting, not per-connection identity —
/// and is supplied to whatever constructs the Hamlib backend's DI registration instead.</summary>
public sealed record HamlibConnectionSpec(uint Model) : RadioConnectionSpec
{
    public string? SerialPort { get; init; }
    public int? BaudRate { get; init; }
    public string? PttType { get; init; }

    /// <summary>Hamlib's own <c>ptt_pathname</c> config token -- a PTT-only serial device, separate
    /// from <see cref="SerialPort"/> (the CAT port). Only meaningful when <see cref="PttType"/> is
    /// <c>"RTS"</c>/<c>"DTR"</c> (CAT/VOX/none key over the CAT link itself or not at all) -- passed
    /// through unconditionally regardless, since Hamlib's own config layer ignores it for those
    /// other PTT types (verified against <c>hamlib/src/rig.c</c>'s PTT-open path).</summary>
    public string? PttPort { get; init; }
}

/// <summary>See spec/03-cat-layer.md's flrig section. Connects to a running flrig instance's XML-RPC
/// server (default <c>http://Host:Port/RPC2</c>, default port 12345 -- flrig's own default,
/// <c>flrig/src/support/status.cxx</c>). No <c>ConnectTimeout</c> property here unlike
/// <see cref="RigctldConnectionSpec"/> -- deliberate: the HTTP client backing this connection is built
/// once at DI-registration time via a named <c>IHttpClientFactory</c> client, so a per-spec timeout
/// value has no way to reach it. Timeout is instead a single fixed value configured on that named
/// client, covering connect+read together.</summary>
public sealed record FlrigConnectionSpec(string Host, int Port) : RadioConnectionSpec;
