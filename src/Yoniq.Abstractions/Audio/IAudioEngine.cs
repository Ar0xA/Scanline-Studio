namespace Yoniq.Abstractions.Audio;

/// <summary>
/// See spec/05-audio-engine.md. Samples are always mono <see cref="float"/> in [-1, 1].
/// <see cref="SamplesCaptured"/> fires on the audio backend's own real-time thread — handlers must
/// not allocate or call into async machinery (see CLAUDE.md's concurrency/scheduler rule).
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default);

    Task StopCaptureAsync();

    Task StartPlaybackAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default);

    Task StopPlaybackAsync();

    event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    void EnqueuePlaybackSamples(ReadOnlyMemory<float> samples);
}
