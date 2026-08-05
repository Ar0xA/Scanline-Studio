using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio;

/// <summary>
/// Test double for <see cref="IAudioEngine"/> — see spec/05-audio-engine.md. Lets a test
/// synchronously "play" a sample fixture through <see cref="SamplesCaptured"/> via
/// <see cref="PushCapturedSamples"/>, and inspect whatever was enqueued for playback via
/// <see cref="PlaybackSamples"/>. No real device, no threads, no native dependency.
///
/// Round-2-engine-review correction: round 1 left this deliberately permissive (not enforcing
/// <see cref="IAudioEngine"/>'s own documented Lifecycle-error contract) specifically because
/// `FakeAudioEngineRoundTripTests` called <see cref="EnqueuePlaybackSamples"/> before
/// <see cref="StartPlaybackAsync"/>. On reflection that reasoning was backwards: this fake is the
/// only <see cref="IAudioEngine"/> a future `ScanlineStudio.Application` TX pump will be unit-tested
/// against, and a test double more permissive than the real contract can't catch the exact
/// contract violations that contract exists to catch -- code that passes its tests against a
/// permissive fake and then throws on real hardware defeats the point of writing the contract down
/// at all. Fixed properly instead: this now enforces the same contract
/// `ScanlineStudio.Core.Audio.MiniAudio.MiniAudioEngine` does, and the one test that relied on the old
/// permissive behavior was updated to call <see cref="StartPlaybackAsync"/> first (a one-line
/// change, not a rewrite).
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    private readonly List<float> _playbackSamples = [];
    private bool _disposed;

    public bool IsCapturing { get; private set; }

    public bool IsPlaying { get; private set; }

    public event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    public IReadOnlyList<float> PlaybackSamples => _playbackSamples;

    public Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing)
        {
            throw new InvalidOperationException("Capture is already started -- call StopCaptureAsync first.");
        }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPlaying)
        {
            throw new InvalidOperationException("Playback is already started -- call StopPlaybackAsync first.");
        }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPlaying)
        {
            throw new InvalidOperationException("Playback is not started -- call StartPlaybackAsync first.");
        }

        _playbackSamples.AddRange(samples.Span);
        return samples.Length;
    }

    /// <summary>Test-only: simulates a capture callback firing with the given samples.</summary>
    public void PushCapturedSamples(ReadOnlyMemory<float> samples) => SamplesCaptured?.Invoke(samples);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
