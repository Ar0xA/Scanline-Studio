namespace ScanlineStudio.Abstractions.Audio;

/// <summary>
/// Real OS mute state for a capture or playback device -- deliberately device-scoped, not
/// session-scoped like <see cref="IAudioEngine"/> (whose every member requires an open capture or
/// playback lifecycle). The OS's own per-device mute flag is a property of the device itself,
/// queryable whether or not this process currently has any session open on it, the same shape as
/// <see cref="IAudioDeviceEnumerator"/> -- not something that belongs bolted onto
/// <see cref="IAudioEngine"/>'s already session-lifecycle-scoped contract.
///
/// Read-only by design: this app has no mute SETTER anywhere, on RX or TX -- it only observes the
/// OS's own mute state so a UI can show a muted indicator, never mutes a device other software/the
/// OS itself controls. (This interface briefly also carried real OS-mixer VOLUME get/set --
/// removed in the same user-directed reversal noted on <c>ISstvSessionService.GetTxVolumePercentAsync</c>
/// that put TX Pwr back on app-internal gain; mute is the one piece of that design that stayed,
/// since it's a real OS device fact independent of whichever gain approach TX uses.)
/// </summary>
public interface IAudioDeviceMuteQuery
{
    /// <summary>Whether <paramref name="device"/> is currently muted at the OS level, or <see
    /// langword="null"/> if this backend/device doesn't support a mute query (e.g. JACK, or an ALSA
    /// device exposing no suitable mixer switch). Not an error -- callers should treat <see
    /// langword="null"/> as "unsupported here," not "failed."</summary>
    Task<bool?> IsDeviceMutedAsync(AudioDeviceInfo device, bool isCapture, CancellationToken ct = default);
}
