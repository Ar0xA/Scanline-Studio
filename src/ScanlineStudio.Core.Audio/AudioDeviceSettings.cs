using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio;

/// <summary>Persisted capture/playback device selection — <see cref="AudioDeviceInfo.Id"/> is opaque
/// per its own backend (MiniAudio device ID today); a <c>null</c> field means "no device selected
/// yet," resolved by whatever composition-root/session-service code picks a default (spec/05 leaves
/// default-device selection to the application layer, not the engine itself).</summary>
public sealed record AudioDeviceSettings
{
    public const string SectionKey = "Audio";

    public string? CaptureDeviceId { get; init; }

    public string? PlaybackDeviceId { get; init; }

    /// <summary>User-reported fix (2026-08-23): <see cref="AudioDeviceInfo.Id"/> is NOT a stable,
    /// durable identifier in practice on any backend -- observed live on this dev machine when a
    /// PipeWire/PulseAudio USB capture node was re-created with a numeric suffix appended to its own
    /// id after a mute toggle (same physical device, same friendly name, new id) -- WASAPI/CoreAudio
    /// can churn their own device ids across a reconnect/driver-reset event for the identical reason.
    /// Persisted purely as a recovery aid: when <see cref="CaptureDeviceId"/> no longer matches any
    /// currently enumerated device, <c>SstvSessionService.TryResolveDeviceAsync</c> and
    /// <c>OptionsWindowViewModel</c>'s own device-selection restore fall back to matching THIS name
    /// among currently enumerated devices before giving up -- recovering the same device under
    /// whatever new id the backend assigned, instead of surfacing a spurious "device not found."
    /// Not itself a primary key (two identical USB mics could share a name) -- only ever a fallback
    /// after an exact <see cref="CaptureDeviceId"/> match has already failed. <see langword="null"/>
    /// means "no device explicitly selected yet," same as <see cref="CaptureDeviceId"/>.</summary>
    public string? CaptureDeviceName { get; init; }

    /// <summary>See <see cref="CaptureDeviceName"/>'s own doc comment -- identical reasoning, for
    /// <see cref="PlaybackDeviceId"/>.</summary>
    public string? PlaybackDeviceName { get; init; }

    public int SampleRate { get; init; } = 11025;

    /// <summary>"Pwr" TX output gain (0-100), applied as a linear multiplier on encoded PCM samples
    /// right before playback -- see <c>ScanlineStudio.Application.SstvSessionService</c>'s playback
    /// pump for the exact hook point. Pure app-internal gain, same shape WSJT-X's/fldigi's own
    /// Pwr-style controls use (never touches the OS mixer) -- a user-directed reversal of an earlier
    /// same-session design that briefly made this a real OS device volume query instead; see
    /// <c>IAudioDeviceMuteQuery.IsDeviceMutedAsync</c>/<c>SstvSessionService.GetTxDeviceMutedAsync</c>
    /// for the one piece of that design that stayed (mute is still a real OS device query,
    /// independent of this gain). Lives here (not a UI-only settings section) because that service's
    /// own playback pipeline needs to read it directly, and it cannot reference a
    /// <c>ScanlineStudio.UI</c>-owned type.
    ///
    /// <b>Nullable, not a plain <c>int</c> with a <c>= 100</c> initializer</b> -- System.Text.Json
    /// does not honor property-initializer defaults for <c>init</c>-only properties absent from the
    /// JSON payload (a real, confirmed STJ limitation, not a hypothetical): any settings.json saved
    /// before this field existed deserializes it as the CLR default <c>0</c>, not <c>100</c>, silently
    /// muting TX audio for every existing install. Treat <c>null</c> as "unset -- apply the desired
    /// 100 default" at every read site (see <c>SstvSessionService.GetTxVolumePercentAsync</c>), never
    /// re-add a non-null default value here.</summary>
    public int? TxVolumePercent { get; init; }

    /// <summary>OS scheduling priority for the capture drain thread (see
    /// <see cref="ScanlineStudio.Abstractions.Audio.IAudioEngine.StartCaptureAsync"/>'s own doc
    /// comment) -- does NOT affect the real-time native audio callback thread, which has no
    /// managed-settable priority at all.
    ///
    /// <b>Nullable</b> for the same STJ reason as <see cref="TxVolumePercent"/> above: the desired
    /// "leave it at the runtime default" behavior is "don't pass a value at all," which is exactly
    /// what <c>null</c> already means at the one read site (<c>SstvSessionService.StartReceivingAsync</c>)
    /// -- never re-add a non-null default value here.</summary>
    public System.Threading.ThreadPriority? CaptureThreadPriority { get; init; }

    /// <summary>Requested native hardware/backend buffer period size, in frames, for BOTH capture
    /// and playback sessions -- <c>0</c> means "leave miniaudio's own default period/backend
    /// heuristic alone" (see <c>native/scanline_audio.c</c>'s own doc comment on
    /// <c>scanline_audio_open_options</c>). A separate, lower-level knob from the fixed
    /// managed-side ring capacity <c>MiniAudioCaptureSession</c>/<c>MiniAudioPlaybackSession</c>
    /// already use internally -- this tunes the actual device buffer, not this shim's own ring.
    ///
    /// <b>Plain non-nullable <c>int</c>, NOT the nullable pattern</b> -- unlike
    /// <see cref="CaptureThreadPriority"/> above, the desired "unset"
    /// default here (<c>0</c>, "don't touch it") IS the CLR default for <c>int</c>, so the
    /// nullable-with-read-site-fallback pattern would be unnecessary ceremony (see
    /// <c>RadioSafetySettings.SwrCutoffEnabled</c> for the established precedent of when a plain
    /// default is correct). Do not "fix" this to nullable.</summary>
    public int PeriodSizeInFrames { get; init; }

    /// <summary>Requested native period count, for BOTH capture and playback sessions -- see
    /// <see cref="PeriodSizeInFrames"/>'s own doc comment for the full reasoning (same
    /// CLR-default-safe, plain-non-nullable shape, same <c>0</c> = "backend default"
    /// meaning).</summary>
    public int Periods { get; init; }

    /// <summary>Which channel of a stereo-capable capture device to use as the mono RX source --
    /// <b>not a confirmed legacy port</b> (see <see cref="AudioChannelSource"/>'s own doc comment).
    /// Plain non-nullable, same CLR-default-safe reasoning as <see cref="PeriodSizeInFrames"/>
    /// above -- <see cref="AudioChannelSource.Mono"/> (value <c>0</c>) is both the desired
    /// "unset" default and the enum's own CLR default.</summary>
    public AudioChannelSource CaptureChannelSource { get; init; }

    /// <summary>Duplicates the TX signal to both output channels instead of a mono device open --
    /// <b>not a confirmed legacy port</b>. Plain non-nullable, same CLR-default-safe reasoning as
    /// <see cref="PeriodSizeInFrames"/> above -- <see langword="false"/> is both the desired
    /// "unset" default and <see cref="bool"/>'s own CLR default.</summary>
    public bool StereoTxEnabled { get; init; }

    /// <summary>Manual TX sample-clock correction, in Hz -- port of legacy's <c>sys.m_TxSampOff</c>
    /// (<c>[SoundCard] TxSampOffset</c>, <c>Main.cpp:1637/2200</c>, default <c>0.0</c>), the
    /// Options dialog's own manual spinner/text-field for correcting a soundcard's real clock drift
    /// from nominal (legacy's own auto-measure convenience on top of this, reachable only via real
    /// full-duplex loopback hardware, is a separate, not-yet-ported piece — stub survey Tier 3).
    /// Applied to the TX encoder's tone-generation math only (<c>AnalogFmSstvEncoder</c>'s
    /// <c>sampleRateOffsetHz</c> parameter) — the nominal <see cref="SampleRate"/> above stays
    /// unchanged for the playback device rate, WAV headers, and the TX output bandpass filter, all
    /// three of which legacy itself keeps at the nominal rate too (<c>sstv.cpp:2768/2771</c>).
    /// Plain non-nullable <c>double</c>, same CLR-default-safe reasoning as
    /// <see cref="PeriodSizeInFrames"/> above — <c>0.0</c> (no correction) is both the desired
    /// "unset" default and <see cref="double"/>'s own CLR default, so no STJ nullable-read-site
    /// fallback is needed. Settings-boundary validation (non-finite or outside legacy's own
    /// accepted ±1500 Hz range falls back to <c>0.0</c>) lives at the read site
    /// (<c>SstvSessionService</c>'s transmit-settings resolution), matching this codebase's own
    /// <c>CwToneFrequencyHz</c> precedent.</summary>
    public double TxSampleRateOffsetHz { get; init; }
}
