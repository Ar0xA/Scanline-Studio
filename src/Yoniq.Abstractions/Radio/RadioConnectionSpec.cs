namespace Yoniq.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. Describes *how* to reach a rig — a discriminated union via an
/// open abstract record base with one sealed subtype per backend (spec/03-cat-layer.md). Only
/// <see cref="NoneConnectionSpec"/> and <see cref="RigctldConnectionSpec"/> exist yet; future backends
/// (linked Hamlib, flrig, OmniRig-as-client) each add their own sealed subtype here without requiring
/// any existing code to change — <see cref="IRadioController"/> never switches on the concrete subtype
/// itself, only each registered <see cref="IRadioProtocolFactory"/>'s own <c>CanHandle</c> does (see
/// that interface's doc comment), so there is no exhaustiveness surface for a new subtype to silently
/// fall through.
///
/// <b>Not (yet) JSON-serializable as this polymorphic hierarchy</b> — noted here so a future settings-
/// persistence implementation (Phase 3/4, out of scope for the current rigctld-client work) doesn't
/// design around this record shape and hit the wall: <c>System.Text.Json</c> polymorphic
/// (de)serialization needs a closed <c>[JsonDerivedType]</c> list declared on the base type, but
/// <c>Yoniq.Abstractions</c> must never reference optional backend assemblies (linked-Hamlib/OmniRig
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
/// process — the daemon's own rig identity/model is opaque to Yoniq (see spec/02-radio-layer.md's "Rig
/// identification").</summary>
public sealed record RigctldConnectionSpec(string Host, int Port) : RadioConnectionSpec
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
