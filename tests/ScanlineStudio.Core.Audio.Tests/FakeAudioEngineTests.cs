using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.Tests;

/// <summary>
/// Regression tests for a real bug an auditor caught (batch-5 wiring, round 1):
/// <see cref="FakeAudioEngine.CaptureOverrunCount"/> was a bare settable property, not enforcing
/// <see cref="IAudioEngine.CaptureOverrunCount"/>'s own documented contract ("0 when capture isn't
/// running, reset by a fresh StartCaptureAsync") the way this fake's own round-2 fix already enforces
/// for the Lifecycle-error contract elsewhere -- a test against this fake could see a nonzero reading
/// in a state the real <c>MiniAudioEngine</c> can never produce.
/// </summary>
public sealed class FakeAudioEngineTests
{
    private static readonly AudioDeviceInfo FakeDevice =
        new("fake", "Fake Device", MaxInputChannels: 1, MaxOutputChannels: 0, SupportedSampleRates: [11025]);

    [Fact]
    public void CaptureOverrunCount_SetWhileNotCapturing_ReadsZero()
    {
        var engine = new FakeAudioEngine { CaptureOverrunCount = 5 };

        Assert.Equal(0, engine.CaptureOverrunCount);
    }

    [Fact]
    public async Task CaptureOverrunCount_SetWhileCapturing_ReadsTheSetValue()
    {
        await using var engine = new FakeAudioEngine();
        await engine.StartCaptureAsync(FakeDevice, sampleRate: 11025);

        engine.CaptureOverrunCount = 5;

        Assert.Equal(5, engine.CaptureOverrunCount);
    }

    [Fact]
    public async Task CaptureOverrunCount_ResetsOnAFreshStartCaptureAsync()
    {
        await using var engine = new FakeAudioEngine();
        await engine.StartCaptureAsync(FakeDevice, sampleRate: 11025);
        engine.CaptureOverrunCount = 5;
        await engine.StopCaptureAsync();

        await engine.StartCaptureAsync(FakeDevice, sampleRate: 11025);

        Assert.Equal(0, engine.CaptureOverrunCount);
    }
}
