using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application;

/// <summary>Orchestrates <c>ScanlineStudio.Abstractions.Audio.IAudioEngine</c> + <see cref="ISstvEncoder"/>/
/// <see cref="ISstvDecoder"/> + <see cref="IWaterfallSource"/> + <see cref="IReceivedImageBuffer"/> for
/// the UI — see the Phase-3 plan's decision #9. Fans a single capture stream out to the decoder and
/// the waterfall in isolation (decision #3: neither can stall the other), and interlocks TX/RX
/// (decision #10): <see cref="TransmitAsync"/> pauses capture for the duration of the transmission (so
/// an audio-cable-looped demo doesn't decode its own TX) and keys PTT around playback.
///
/// <b>Concurrency</b>: <see cref="ModeDetected"/> and whatever <see cref="IReceivedImageBuffer.Updated"/>
/// the caller subscribes to both fire synchronously from the audio drain thread — same contract as
/// <see cref="IWaterfallSource.Frames"/> and <c>ScanlineStudio.Abstractions.Radio.IRadioController.StateChanges</c>
/// already established in this codebase. UI subscribers marshal to their own scheduler themselves;
/// this service does not depend on Avalonia (spec/01-architecture.md's layering rule) and so cannot do
/// that marshaling itself.</summary>
public interface ISstvSessionService : IAsyncDisposable
{
    IWaterfallSource Waterfall { get; }

    IReceivedImageBuffer ReceivedImage { get; }

    /// <summary>Every mode <see cref="TransmitAsync"/> can encode — exposed here (not
    /// <c>ScanlineStudio.Core.Sstv.SstvModeRegistry.All</c> directly) so <c>ScanlineStudio.UI</c>'s TX pane can list
    /// modes without referencing a <c>ScanlineStudio.Core.*</c> concrete assembly (spec/09-ui.md's layering
    /// rule, enforced by the architecture test).</summary>
    IReadOnlyList<SstvModeDefinition> AvailableModes { get; }

    /// <summary>Whether capture is currently running -- reflects the same internal state
    /// <see cref="StartReceivingAsync"/>/<see cref="StopReceivingAsync"/> already track, so it's
    /// accurate for the header's Receiving/Halt toggle including the case where a startup
    /// auto-start silently failed (no audio device) or <see cref="TransmitAsync"/> is transiently
    /// pausing capture for the duration of a transmission.</summary>
    bool IsReceiving { get; }

    event Action<SstvModeDefinition>? ModeDetected;

    /// <summary>Ultracode audit finding #34's automatic-restart mechanism (see
    /// <c>ScanlineStudio.Core.Sstv.RestartableSstvDecoder</c>) has gone past its warning threshold
    /// without an opportunity to swap yet -- fires at most once per restart cycle, cleared by
    /// <see cref="MaintenanceWarningCleared"/>. Fires synchronously on the audio drain thread, same
    /// contract as <see cref="ModeDetected"/>.</summary>
    event Action? MaintenanceWarningRaised;

    /// <summary>The condition <see cref="MaintenanceWarningRaised"/> warned about has resolved (a
    /// restart happened before the critical threshold was reached).</summary>
    event Action? MaintenanceWarningCleared;

    /// <summary>The critical threshold was reached -- the decoder has already force-restarted
    /// unconditionally (this is a notification, not a request), and by the time this fires RX has
    /// already been stopped via <see cref="StopReceivingAsync"/>. The user must manually call
    /// <see cref="StartReceivingAsync"/> again.</summary>
    event Action? MaintenanceCriticalStopRaised;

    Task StartReceivingAsync(CancellationToken ct = default);

    Task StopReceivingAsync();

    /// <summary>Encodes and transmits <paramref name="image"/> as <paramref name="mode"/>. Pauses
    /// capture/decode for the duration (restored afterward only if RX was already running) and keys
    /// PTT via the injected <c>IRadioSessionService</c> around playback.</summary>
    Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default);

    /// <summary>Keys PTT, plays a steady sine tone at <paramref name="frequencyHz"/> for
    /// <paramref name="duration"/> (WSJT-X/legacy-Tune-style AFC-lock aid), then un-keys PTT --
    /// same pause-RX/key-PTT/resume-RX guarantee shape as <see cref="TransmitAsync"/>.</summary>
    /// <param name="leaveKeyedAfterTune"><b>Unverified against legacy YONIQ source</b> -- a direct
    /// citation for the exact post-tune-tone transition (e.g. legacy's own Tune button/`CtrBtn.cpp`)
    /// was not found; this parameter name deliberately does not claim legacy parity. When
    /// <see langword="true"/>, PTT is left keyed once the tone finishes (skipping the normal
    /// un-key/resume-RX step) instead of returning to RX -- intended for a "tune into satellite
    /// pass-through" workflow where the operator wants to go straight from a tune tone into
    /// transmitting. A subsequent <see cref="TransmitAsync"/> or <see cref="TuneAsync"/> call still
    /// keys PTT itself as normal (this flag only affects what happens at the END of THIS call, not
    /// any later one) -- callers wanting PTT held across multiple calls should use
    /// <see cref="SetPttLockAsync"/> instead, which is a distinct, independently-toggled
    /// mechanism.</param>
    Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default);

    /// <summary>Current post-encode playback gain (0-100), read fresh from
    /// <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>.</summary>
    Task<int> GetTxVolumePercentAsync(CancellationToken ct = default);

    Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default);

    /// <summary>Whether <see cref="SetPttLockAsync"/>'s lock is currently engaged.</summary>
    bool IsPttLocked { get; }

    /// <summary>Manual-keying diagnostic aid: holds PTT keyed independent of any
    /// <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call, until unlocked. Idempotent both
    /// ways. See the implementation's own doc comment for the exact failure-handling and
    /// concurrency contract -- in short: a failure here is NOT swallowed (unlike this service's
    /// internal best-effort cleanup paths), and <see cref="IsPttLocked"/> only changes after
    /// <c>IRadioSessionService.SetPttAsync</c> actually confirms the requested state.</summary>
    Task SetPttLockAsync(bool locked, CancellationToken ct = default);
}
