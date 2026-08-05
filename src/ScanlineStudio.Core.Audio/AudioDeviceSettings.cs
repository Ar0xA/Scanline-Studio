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
}
