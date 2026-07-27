using Yoniq.Abstractions.Audio;

namespace Yoniq.Core.Audio;

/// <summary>
/// Test double for <see cref="IAudioEngine"/> — see spec/05-audio-engine.md. Lets a test
/// synchronously "play" a sample fixture through <see cref="SamplesCaptured"/> via
/// <see cref="PushCapturedSamples"/>, and inspect whatever was enqueued for playback via
/// <see cref="PlaybackSamples"/>. No real device, no threads, no native dependency.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    private readonly List<float> _playbackSamples = [];

    public bool IsCapturing { get; private set; }

    public bool IsPlaying { get; private set; }

    public event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    public IReadOnlyList<float> PlaybackSamples => _playbackSamples;

    public Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default)
    {
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync()
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public Task StartPlaybackAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default)
    {
        IsPlaying = true;
        return Task.CompletedTask;
    }

    public Task StopPlaybackAsync()
    {
        IsPlaying = false;
        return Task.CompletedTask;
    }

    public void EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => _playbackSamples.AddRange(samples.Span);

    /// <summary>Test-only: simulates a capture callback firing with the given samples.</summary>
    public void PushCapturedSamples(ReadOnlyMemory<float> samples) => SamplesCaptured?.Invoke(samples);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
