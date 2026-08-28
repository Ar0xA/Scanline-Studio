namespace ScanlineStudio.Application;

/// <summary>Outcome of <see cref="IConfigurationPresetService.SwitchToPresetAsync"/> --
/// Configurations-preset backlog, Phase 3 (2026-08-28).</summary>
public enum ConfigurationPresetSwitchOutcome
{
    /// <summary>The preset's own content was merged into the live settings.json and every changed,
    /// Application-layer-reachable section was pushed live. See
    /// <see cref="ConfigurationPresetSwitchResult.RxAudioDeferred"/> and
    /// <see cref="ConfigurationPresetSwitchResult.PartiallyApplied"/> for the partial-application
    /// cases this can still carry -- settings.json and the active-preset marker are ALWAYS fully
    /// switched by the time this outcome is returned; only the LIVE in-process push of one or more
    /// sections may not have fully landed.</summary>
    Applied,

    /// <summary>No preset with the requested name exists (<see cref="IConfigurationPresetStore.LoadPresetAsync"/>
    /// returned <see langword="null"/>) -- nothing was touched.</summary>
    PresetNotFound,

    /// <summary>Rejected outright, before touching anything -- a transmission is currently in flight
    /// (<see cref="ISstvSessionService.IsTransmitting"/>). No existing precedent silently aborts a
    /// live transmission (unlike RX, which sample-rate/BPF/device changes already abort routinely);
    /// PTT is safety-critical enough that an explicit, up-front reject beats a partial or deferred
    /// switch.</summary>
    RejectedTransmitting,

    /// <summary>Rejected outright, before touching anything -- a recording is currently in progress
    /// (<see cref="ISstvSessionService.IsRecording"/>). Checked UP FRONT (not discovered partway
    /// through, the way a single setting's own deferral works) so "the whole switch either applies or
    /// doesn't" stays a real promise rather than one that becomes unachievable once settings are
    /// already partway overwritten.</summary>
    RejectedRecording,

    /// <summary>Rejected outright, before touching anything -- another <see cref="IConfigurationPresetService.SwitchToPresetAsync"/>
    /// call is already in progress on this instance. Round-1 code-review finding: without this,
    /// two concurrent switches (a UI double-click, or a future command with no <c>CanExecute</c> gate)
    /// interleave freely -- both snapshot the same "previous" settings, both merge, both push, and the
    /// live subsystems can end up a mix of both presets while the active-preset marker names only one
    /// of them.</summary>
    RejectedConcurrentSwitch,
}

/// <summary>See <see cref="ConfigurationPresetSwitchOutcome"/> for the overall result -- this record
/// carries what the CALLER (a UI-layer command handler, already on the UI thread) must still do
/// after `await`ing <see cref="IConfigurationPresetService.SwitchToPresetAsync"/>, since neither
/// action is safe to perform from inside the orchestrator itself: <c>ILocalizationService.SetCultureAsync</c>'s
/// own <c>CultureChanged</c> event fires synchronously on whatever thread calls it (documented as
/// "expected to always be the UI thread" -- the orchestrator itself runs on a background/pool
/// thread), and the ~8-readout UI refresh this app's own Options-Closed flow already does lives in
/// <c>ScanlineStudio.UI</c>, which <c>ScanlineStudio.Application</c> has no project reference to at
/// all.
///
/// <para>Round-1 code-review fix: the caller must check <paramref name="CultureChanged"/>, never
/// <c>CultureToApply != null</c> -- a preset whose Localization section explicitly carries
/// <see langword="null"/> (e.g. one saved from an Options reset-to-defaults) is a REAL culture
/// change when the live culture is non-null, and <paramref name="CultureToApply"/> is
/// <see langword="null"/> in that case too (the target culture genuinely IS null/default).
/// <paramref name="CultureChanged"/> is the only reliable "did it change" signal;
/// <paramref name="CultureToApply"/> only tells you WHAT to apply once you already know it
/// changed.</para>
///
/// <para>Round-1 code-review fix: <paramref name="PartiallyApplied"/> is <see langword="true"/> if
/// any live push (audio/decoder/radio-safety/radio-connection) threw or came back as an explicit
/// rejection (e.g. the preset's own capture device is no longer plugged in, or its sample rate was
/// rejected by the decoder). settings.json and the active-preset marker are STILL fully switched
/// either way -- this only means one or more of the live, in-process pushes did not fully land, and
/// the caller should surface a generic "some settings could not be applied live" warning rather than
/// treating this the same as a fully clean <see cref="ConfigurationPresetSwitchOutcome.Applied"/>.</para></summary>
public sealed record ConfigurationPresetSwitchResult(
    ConfigurationPresetSwitchOutcome Outcome,
    bool CultureChanged,
    string? CultureToApply,
    bool RxAudioDeferred,
    bool PartiallyApplied)
{
    public static readonly ConfigurationPresetSwitchResult NotFound = new(ConfigurationPresetSwitchOutcome.PresetNotFound, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);

    public static readonly ConfigurationPresetSwitchResult RejectedTransmitting = new(ConfigurationPresetSwitchOutcome.RejectedTransmitting, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);

    public static readonly ConfigurationPresetSwitchResult RejectedRecording = new(ConfigurationPresetSwitchOutcome.RejectedRecording, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);

    public static readonly ConfigurationPresetSwitchResult RejectedConcurrentSwitch = new(ConfigurationPresetSwitchOutcome.RejectedConcurrentSwitch, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);
}
