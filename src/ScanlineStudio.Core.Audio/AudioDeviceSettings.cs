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
}
