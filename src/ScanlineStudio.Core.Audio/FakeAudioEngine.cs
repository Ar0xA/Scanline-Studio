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

    private int _captureOverrunCount;

    public bool IsCapturing { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Test-settable -- this fake has no real overrun mechanism of its own to drive it, so a
    /// test that needs a specific value sets it directly rather than provoking a real drop. Auditor
    /// round 1 (batch-5 wiring) caught this NOT actually enforcing <see cref="IAudioEngine.CaptureOverrunCount"/>'s
    /// own documented contract ("0 when capture isn't running, reset by a fresh StartCaptureAsync")
    /// the way this file's own round-2 fix already enforces for the Lifecycle-error contract
    /// elsewhere -- a test against this fake could see a nonzero reading in a state the real engine
    /// can never produce. Fixed the same way: <see cref="StartCaptureAsync"/> resets the backing
    /// field, and the getter forces <c>0</c> whenever <see cref="IsCapturing"/> is
    /// <see langword="false"/>, matching <c>MiniAudioEngine.CaptureOverrunCount</c>'s own real
    /// behavior (its capture session is null/unpublished in exactly that state).</summary>
    public int CaptureOverrunCount
    {
        get => IsCapturing ? _captureOverrunCount : 0;
        set => _captureOverrunCount = value;
    }

    public event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    public IReadOnlyList<float> PlaybackSamples => _playbackSamples;

    /// <summary>Records <paramref name="drainThreadPriority"/> in <see cref="LastRequestedDrainThreadPriority"/>
    /// for test assertions -- this fake has no real drain thread of its own to apply it to.</summary>
    public ThreadPriority? LastRequestedDrainThreadPriority { get; private set; }

    public int? LastRequestedCapturePeriodSizeInFrames { get; private set; }

    public int? LastRequestedCapturePeriods { get; private set; }

    public AudioChannelSource? LastRequestedChannelSource { get; private set; }

    public int? LastRequestedCaptureSampleRate { get; private set; }

    public Task StartCaptureAsync(
        AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
        int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing)
        {
            throw new InvalidOperationException("Capture is already started -- call StopCaptureAsync first.");
        }

        LastRequestedDrainThreadPriority = drainThreadPriority;
        LastRequestedCaptureSampleRate = sampleRate;
        LastRequestedCapturePeriodSizeInFrames = periodSizeInFrames;
        LastRequestedCapturePeriods = periods;
        LastRequestedChannelSource = channelSource;
        _captureOverrunCount = 0;
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync()
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public int? LastRequestedPeriodSizeInFrames { get; private set; }

    public int? LastRequestedPeriods { get; private set; }

    public bool? LastRequestedStereoTx { get; private set; }

    public Task StartPlaybackAsync(
        AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
        bool stereoTx = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPlaying)
        {
            throw new InvalidOperationException("Playback is already started -- call StopPlaybackAsync first.");
        }

        LastRequestedPeriodSizeInFrames = periodSizeInFrames;
        LastRequestedPeriods = periods;
        LastRequestedStereoTx = stereoTx;
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
