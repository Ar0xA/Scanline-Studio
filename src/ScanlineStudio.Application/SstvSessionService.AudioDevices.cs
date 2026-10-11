using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService
{
    /// <summary>User-reported fix, round 3 (2026-08-23): a configured-but-missing device (unplugged,
    /// uninstalled, or churned with no name match either) now falls all the way through to
    /// <see cref="TryResolveDeviceAsync"/>'s own backend-default lookup instead of returning
    /// <see langword="null"/> for that case specifically -- see that method's own doc comment. This
    /// means <see langword="null"/> ONLY ever means "no device available at all," a single, simpler
    /// error case (was two, "nothing configured + no default" vs "configured device not found" --
    /// both now collapse into this one, since TryResolveDeviceAsync already tried everything before
    /// giving up).</summary>
    private async Task<AudioDeviceInfo> ResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var device = await TryResolveDeviceAsync(forCapture, ct, persistIfResolvedIndirectly: true).ConfigureAwait(false);
        if (device is null)
        {
            var kind = forCapture ? "capture" : "playback";
            // Tier B audit finding: SafeLog-wrapped, same as every other logging call in this file's
            // cleanup/device-resolution paths (see SafeLog's own doc comment) -- a throwing logging
            // provider here must not substitute a generic logging exception for the actionable
            // InvalidOperationException message this method is about to throw anyway.
            SafeLog(() => Log.NoDeviceConfigured(_logger, kind));
            throw new InvalidOperationException($"No {kind} audio device configured, and no default {kind} device is available -- connect one, or set one in Options before starting a session.");
        }

        return device;
    }

    /// <summary>Non-throwing counterpart to <see cref="ResolveDeviceAsync"/>, extracted from it (not
    /// duplicated) so <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s passive readout use and
    /// <see cref="ResolveDeviceAsync"/>'s action-that-should-fail-loudly use share one lookup.
    /// <see langword="null"/> means "no device available at all" -- neither an exact id match, a
    /// same-name recovery match, nor a backend-reported default exists (spec/18-path-to-1.0.md
    /// Critical item 1 / item 8's genuinely rare case, e.g. a headless machine with no audio
    /// hardware). Every other case recovers to SOME real device rather than surfacing "not found."
    /// <para>User-reported fix (2026-08-23): an id mismatch first tries recovering the SAME device by
    /// its last-known <see cref="AudioDeviceSettings.CaptureDeviceName"/>/
    /// <see cref="AudioDeviceSettings.PlaybackDeviceName"/> -- see that field's own doc comment for
    /// the real, OS-agnostic device-id-churn scenario this closes (observed live: a PipeWire USB
    /// capture node re-created under a new id after a mute toggle, same physical device, same
    /// name).</para>
    /// <para>User-reported fix, round 3, same day (explicit product decision, overriding this
    /// method's own prior "surface a missing configured device, don't silently substitute"
    /// stance): "if in the file there is an RX/TX device that is not currently attached to the
    /// computer, just set it to the OS defaults." A configured device that matches NEITHER by id NOR
    /// by name (genuinely unplugged/uninstalled, not just churned) now falls through to the same
    /// backend-default lookup "nothing configured" already used, instead of returning
    /// <see langword="null"/> and making <see cref="ResolveDeviceAsync"/> throw.</para>
    /// <para>User-reported fix, round 2 (2026-08-23): <paramref name="persistIfResolvedIndirectly"/>
    /// -- "load from the file; if nothing in the file, use the OS default; SAVE it to the file; use
    /// it in the program" (verbatim user spec). Before this, a "nothing configured" resolution
    /// re-ran the SAME backend-default lookup on every single call with nothing ever written back,
    /// so a later OS-level default change (a different mic becoming the system default) silently
    /// changed what this app used too, with no durable record of what was actually selected. Passed
    /// <see langword="true"/> only from <see cref="ResolveDeviceAsync"/> (the action paths --
    /// <see cref="StartReceivingAsync"/>/<see cref="TransmitAsync"/>/<see cref="TuneAsync"/> --
    /// where actually committing to a device is appropriate); left <see langword="false"/> (its
    /// default) for <see cref="GetConfiguredCaptureDeviceNameAsync"/>/
    /// <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s passive display-only reads, which must
    /// stay read-only and not write to disk just because the Options dialog (or this app's own
    /// header chip) happened to ask "what would be used right now."</para></summary>
    private async Task<AudioDeviceInfo?> TryResolveDeviceAsync(bool forCapture, CancellationToken ct, bool persistIfResolvedIndirectly = false)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;
        var deviceName = forCapture ? settings.CaptureDeviceName : settings.PlaybackDeviceName;

        await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
        var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;

        if (deviceId is not null)
        {
            var exactMatch = devices.FirstOrDefault(d => d.Id == deviceId);
            if (exactMatch is not null)
            {
                return exactMatch;
            }

            if (deviceName is not null)
            {
                var nameMatch = devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.Ordinal));
                if (nameMatch is not null)
                {
                    SafeLog(() => Log.RecoveredDeviceByName(_logger, forCapture ? "capture" : "playback", deviceId, nameMatch.Id, nameMatch.Name));
                    if (persistIfResolvedIndirectly)
                    {
                        await PersistResolvedDeviceAsync(forCapture, nameMatch, ct).ConfigureAwait(false);
                    }

                    return nameMatch;
                }
            }

            SafeLog(() => Log.ConfiguredDeviceNotFound(_logger, forCapture ? "capture" : "playback", deviceId, devices.Count));
        }

        var fallback = devices.FirstOrDefault(d => d.IsDefault);
        if (fallback is not null)
        {
            // Tier B audit finding: this sat unwrapped on the SUCCESS path -- a throwing logging
            // provider here (this file's own stated threat model, see SafeLog's own doc comment)
            // would throw out of TryResolveDeviceAsync AFTER a device was already successfully
            // resolved into `fallback`, taking down every caller: StartReceivingAsync (RX dead),
            // TransmitAsync/TuneAsync (TX dead), and both GetConfigured*DeviceNameAsync readouts.
            // Same shape round 22 already fixed for Log.RxStarted/Log.RxStopped ("a throwing
            // provider still propagated out of this method after capture had genuinely started",
            // see that round's own comment near RxStarted's call site) -- the identical
            // "one method has the guard, a near-identical sibling doesn't" pattern, just on the
            // device-resolution path instead of the RX-start path.
            SafeLog(() => Log.UsingDefaultDevice(_logger, forCapture ? "capture" : "playback", fallback.Name));
            if (persistIfResolvedIndirectly)
            {
                await PersistResolvedDeviceAsync(forCapture, fallback, ct).ConfigureAwait(false);
            }
        }

        return fallback;
    }

    /// <summary>Writes the just-resolved device's id+name back into <see cref="AudioDeviceSettings"/>
    /// -- see <see cref="TryResolveDeviceAsync"/>'s own <c>persistIfResolvedIndirectly</c> doc
    /// comment for when/why this runs. T0-2: read-modify-write via <see cref="ISettingsStore.UpdateAsync"/>,
    /// atomic against a concurrent Options Save writing a different section -- no longer just
    /// "reload right before writing" to minimize the window, the window is closed. Swallow-and-log
    /// on failure, never propagate: a failed opportunistic persist must not turn an
    /// otherwise-successful device resolution into a failed <see cref="StartReceivingAsync"/>/
    /// <see cref="TransmitAsync"/> call.</summary>
    private async Task PersistResolvedDeviceAsync(bool forCapture, AudioDeviceInfo device, CancellationToken ct)
    {
        try
        {
            await _settingsStore.UpdateAsync(appSettings =>
            {
                var previousAudio = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
                var updatedAudio = forCapture
                    ? previousAudio with { CaptureDeviceId = device.Id, CaptureDeviceName = device.Name }
                    : previousAudio with { PlaybackDeviceId = device.Id, PlaybackDeviceName = device.Name };
                return appSettings.WithSection(AudioDeviceSettings.SectionKey, updatedAudio, AudioSettingsJsonContext.Default.AudioDeviceSettings);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.PersistResolvedDeviceFailed(_logger, forCapture ? "capture" : "playback", device.Id, ex));
        }
    }

    public async Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
        return device?.Name;
    }

    /// <summary>See <see cref="ISstvSessionService.GetConfiguredCaptureDeviceNameAsync"/>.</summary>
    public async Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: true, ct).ConfigureAwait(false);
        return device?.Name;
    }

    private async Task<AudioDeviceSettings> LoadAudioSettingsAsync(CancellationToken ct)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
    }
}
