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

    public int SampleRate { get; init; } = 11025;

    /// <summary>TX output gain (0-100), applied as a linear multiplier on encoded PCM samples right
    /// before playback -- see <c>ScanlineStudio.Application.SstvSessionService</c>'s playback pump
    /// for the exact hook point. Lives here (not a UI-only settings section) because that service's
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
    /// heuristic alone" (see <c>native/yoniq_audio.c</c>'s own doc comment on
    /// <c>yoniq_audio_open_options</c>). A separate, lower-level knob from the fixed
    /// managed-side ring capacity <c>MiniAudioCaptureSession</c>/<c>MiniAudioPlaybackSession</c>
    /// already use internally -- this tunes the actual device buffer, not this shim's own ring.
    ///
    /// <b>Plain non-nullable <c>int</c>, NOT the nullable pattern</b> -- unlike
    /// <see cref="TxVolumePercent"/>/<see cref="CaptureThreadPriority"/> above, the desired "unset"
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
}
