namespace ScanlineStudio.Abstractions.Audio;

/// <summary><paramref name="IsDefault"/> (spec/18-path-to-1.0.md Critical item 1 / item 8):
/// whether the backend reports this as the OS/audio-server default device. Optional trailing
/// parameter, not inserted mid-record -- keeps every existing positional
/// <c>new AudioDeviceInfo(...)</c> call site compiling unchanged; only
/// <c>MiniAudioDeviceEnumerator</c> passes the real value (surfaced from a native flag that
/// previously existed but was discarded). Used by <c>SstvSessionService.TryResolveDeviceAsync</c>
/// to fall back to a sane device when nothing is explicitly configured, instead of failing
/// outright on a fresh install.</summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    int MaxInputChannels,
    int MaxOutputChannels,
    IReadOnlyList<int> SupportedSampleRates,
    bool IsDefault = false);
