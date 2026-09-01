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

    /// <summary>Configurations-preset backlog, Phase 1 (2026-08-28): lets a test assert WHICH device
    /// was actually opened, same shape as <see cref="LastRequestedCaptureSampleRate"/> above -- added
    /// alongside the new live capture-device-swap feature, which needed a way to distinguish "opened
    /// device A" from "opened device B" that this fake didn't previously expose.</summary>
    public AudioDeviceInfo? LastRequestedCaptureDevice { get; private set; }

    /// <summary>Configurations-preset backlog, Phase 1 code-review round-1 finding: when set, the
    /// NEXT <see cref="StartCaptureAsync"/> call throws this instead of succeeding, then this is
    /// cleared back to <see langword="null"/> -- ONE-SHOT, not a permanent failure, so a test can
    /// simulate a single real native-open failure (e.g. <see cref="AudioDeviceUnavailableException"/>,
    /// what <c>MiniAudioEngine</c> actually throws for a device that enumerates but won't open --
    /// exclusive use, unsupported rate, unplugged mid-swap) without also breaking a SUBSEQUENT
    /// rollback restart attempt in the same test.</summary>
    public Exception? StartCaptureExceptionToThrowOnce { get; set; }

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

        if (StartCaptureExceptionToThrowOnce is { } ex)
        {
            StartCaptureExceptionToThrowOnce = null;
            throw ex;
        }

        LastRequestedDrainThreadPriority = drainThreadPriority;
        LastRequestedCaptureSampleRate = sampleRate;
        LastRequestedCaptureDevice = device;
        LastRequestedCapturePeriodSizeInFrames = periodSizeInFrames;
        LastRequestedCapturePeriods = periods;
        LastRequestedChannelSource = channelSource;
        _captureOverrunCount = 0;
        IsCapturing = true;
        return Task.CompletedTask;
    }

    /// <summary>Configurations-preset backlog, Phase 1 (2026-08-28): same deterministic-gate idiom as
    /// <see cref="OnPlaybackChunkEnqueued"/> above -- fired synchronously, right as capture stops,
    /// letting a test act at the exact window a live capture-device (or sample-rate) change's own
    /// TOCTOU guard exists to close (e.g. attempting to start a recording while a swap is mid-flight).</summary>
    public Func<Task>? OnStopCaptureAsync { get; set; }

    public async Task StopCaptureAsync()
    {
        IsCapturing = false;
        if (OnStopCaptureAsync is not null)
        {
            await OnStopCaptureAsync().ConfigureAwait(false);
        }
    }

    public int? LastRequestedPeriodSizeInFrames { get; private set; }

    public int? LastRequestedPeriods { get; private set; }

    public bool? LastRequestedStereoTx { get; private set; }

    /// <summary>Same shape as <see cref="LastRequestedCaptureDevice"/> -- lets a test assert WHICH
    /// playback device was actually opened for a given TX, e.g. to prove the playback path already
    /// resolves the device fresh from settings on every call (unlike capture, which holds a
    /// long-lived open stream and needs an explicit live-apply request to pick up a change).</summary>
    public AudioDeviceInfo? LastRequestedPlaybackDevice { get; private set; }

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
        LastRequestedPlaybackDevice = device;
        IsPlaying = true;
        return Task.CompletedTask;
    }

    public Task StopPlaybackAsync()
    {
        IsPlaying = false;
        return Task.CompletedTask;
    }

    /// <summary>Test-only deterministic gate (not a race/timing hook -- see this project's own
    /// "deterministic gates, not a shared race" rule): awaited synchronously, on the caller's own
    /// thread, right after each chunk lands in <see cref="_playbackSamples"/> and before
    /// <see cref="EnqueuePlaybackSamples"/> returns. Lets a test act at the exact moment a real
    /// mid-transmission event (e.g. a Pwr-slider change while <c>SstvSessionService.PumpToPlaybackAsync</c>
    /// is mid-flight) would matter, with no polling and no wall-clock wait. <c>Func&lt;Task&gt;</c>,
    /// not a plain <c>Action</c>, so a test can drive an async call (e.g.
    /// <c>SstvSessionService.SetTxVolumePercentAsync</c>) -- the blocking `.GetAwaiter().GetResult()`
    /// this needs belongs here, in fake test-INFRASTRUCTURE code (this class ships in
    /// <c>ScanlineStudio.Core.Audio</c>, not a test project, so xunit's own "no blocking task ops in a
    /// test method" analyzer doesn't apply), not inside an actual `[Fact]`.</summary>
    public Func<Task>? OnPlaybackChunkEnqueued { get; set; }

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
        OnPlaybackChunkEnqueued?.Invoke().GetAwaiter().GetResult();
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
