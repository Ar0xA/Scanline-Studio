using Yoniq.Abstractions.Audio;

namespace Yoniq.Core.Audio;

/// <summary>
/// Test double for <see cref="IAudioEngine"/> — see spec/05-audio-engine.md. Lets a test
/// synchronously "play" a sample fixture through <see cref="SamplesCaptured"/> via
/// <see cref="PushCapturedSamples"/>, and inspect whatever was enqueued for playback via
/// <see cref="PlaybackSamples"/>. No real device, no threads, no native dependency.
///
/// Round-1-engine-review note: deliberately does NOT enforce the Lifecycle-error contract
/// <see cref="IAudioEngine"/>'s own doc comment documents (double-Start throwing, Enqueue-before-
/// Start throwing, post-dispose throwing) -- <see cref="MiniAudioEngine"/> in
/// `Yoniq.Core.Audio.MiniAudio` enforces all of that for real. Kept permissive here on purpose:
/// `FakeAudioEngineRoundTripTests` (`Yoniq.Core.Audio.Tests`) already calls
/// <see cref="EnqueuePlaybackSamples"/> before ever calling <see cref="StartPlaybackAsync"/>, since
/// that test only cares about exercising the DSP round trip through the interface's data-shape
/// contract (chunking, memory lifetime), not engine lifecycle discipline. Tightening this fake to
/// match would break that pre-existing, unrelated test for no benefit -- a caller that needs to
/// verify lifecycle-error behavior should test against the real engine (see
/// `Yoniq.Core.Audio.MiniAudio.Tests.MiniAudioEngineTests`), not this one.
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

    /// <summary>Unbounded in-memory buffer -- always accepts everything, matching a real backend's
    /// contract of returning the accepted count (see <see cref="IAudioEngine.EnqueuePlaybackSamples"/>)
    /// even though this fake never actually applies back-pressure.</summary>
    public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples)
    {
        _playbackSamples.AddRange(samples.Span);
        return samples.Length;
    }

    /// <summary>Test-only: simulates a capture callback firing with the given samples.</summary>
    public void PushCapturedSamples(ReadOnlyMemory<float> samples) => SamplesCaptured?.Invoke(samples);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
