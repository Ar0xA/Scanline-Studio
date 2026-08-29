namespace ScanlineStudio.Abstractions.Radio;

/// <summary>ui_transition_plan.md step 6 (T2-4): a narrow read-only view of <see cref="IRadioController"/>
/// for a consumer that only ever needs "what is the radio doing right now" — never connect/disconnect/
/// set-frequency/etc. Exists because <c>ScanlineStudio.Core.Logbook</c> (specifically
/// <c>ReceiveHistoryRecorder</c>, which needs to snapshot the radio's frequency/mode at the moment
/// each reception completes) references only <c>Abstractions</c> and <c>Settings</c> — it cannot
/// reference <c>ScanlineStudio.Application</c> (where <c>IRadioSessionService</c> lives) without
/// inverting the intended dependency direction. Auditor plan-review (2026-08-29) explicitly rejected
/// implementing this as a cast of the DI-resolved <c>IRadioSessionService</c> (fragile — breaks the
/// moment a different implementation is ever registered) in favor of a real adapter over
/// <see cref="IRadioController"/>, which already lives in this same namespace and already exposes
/// exactly this data.</summary>
public interface IRadioStateProvider
{
    /// <summary>Same value/null-means-"nothing yet or genuinely no radio" contract as
    /// <see cref="IRadioController.LastKnownState"/> — implementations must also swallow an
    /// <see cref="ObjectDisposedException"/> from a torn-down controller (a real, reachable state
    /// during app shutdown while a decode is still draining) and return <see langword="null"/>
    /// rather than let it propagate onto a caller's own thread.</summary>
    RadioState? Current { get; }
}
