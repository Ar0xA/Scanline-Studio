using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Optional side-channel implemented by <see cref="RestartableSstvDecoder"/> only -- same
/// reasoning as <see cref="ISstvDecoderMaintenance"/>'s own doc comment: a real setter on
/// <see cref="AnalogFmSstvDecoder"/> itself would be actively misleading, since setting
/// <c>RxBpfPreset</c>/<c>DemodType</c>/<c>RxBufferMode</c> on an already-constructed instance does
/// nothing there (all three are read once, at construction, by that class alone). Consumers that
/// care (currently only <c>ScanlineStudio.Application.SstvSessionService</c>) test for this via
/// <c>is</c> on their injected <see cref="ISstvDecoder"/>, matching
/// <see cref="ISstvDecoderMaintenance"/>'s own established pattern.</summary>
public interface ISstvDecoderReconfiguration
{
    /// <summary>Requests that <see cref="RxBpfPreset"/>, <see cref="DemodType"/>, and
    /// <see cref="RxBufferMode"/> apply the next time the decoder is idle -- not immediately.
    /// Restart-required-settings backlog item 2 (2026-08-27): all three are constructor-only on
    /// <see cref="AnalogFmSstvDecoder"/>, so this reuses <see cref="RestartableSstvDecoder"/>'s
    /// existing periodic-maintenance whole-instance-swap machinery instead of any in-place field
    /// mutation (see that class's own <c>RequestReconfiguration</c> doc comment for the full
    /// idle-gating and failure-handling contract). A no-op request (all three values already equal
    /// to what's currently applied) clears any previously-queued-but-not-yet-applied request rather
    /// than queuing a pointless swap. Safe to call from any thread.</summary>
    void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode);

    /// <summary>Fires when a queued <see cref="RequestReconfiguration"/> request could NOT be
    /// applied because <c>CreateInner</c> threw while draining it -- the request was rolled back and
    /// dropped, not retried (see <see cref="RequestReconfiguration"/>'s own doc comment for why one
    /// attempt only). Can fire alongside <see cref="ISstvDecoderMaintenance.Restarted"/> on the SAME
    /// swap (a mandatory overflow/critical swap that happened to have a pending, but failing,
    /// reconfiguration queued still completes -- using the previous, known-good settings -- and
    /// reports the drop here). Same threading/ordering contract as
    /// <see cref="ISstvDecoderMaintenance"/>'s own events: fires synchronously on whichever thread
    /// called <c>PushSamples</c>, strictly after the swap lock is released.</summary>
    event Action? ReconfigurationRejected;
}
