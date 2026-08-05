namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="SyncEnvelopeDetector"/>'s <c>bandwidthHz</c> parameter, added to
/// support the VIS-bit tone-race detectors (1080Hz/1320Hz at 80Hz bandwidth, `sstv.cpp:1446/1448`)
/// this port's <c>VisLockStateMachine</c> piece needs -- every existing instance (AFC, Slant,
/// <c>m_sint2</c>/<c>m_sint3</c>) stays on the default 100Hz bandwidth (`sstv.cpp:1447/1449/1450`),
/// so the default-value behavior must not change.
/// </summary>
public class SyncEnvelopeDetectorTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void NarrowBandwidthDetector_RespondsMoreStronglyToOnFrequencyTone()
    {
        var detector1080 = new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0);
        var detector1320 = new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0);

        var onFrequencyEnvelope = FeedTone(detector1080, 1080.0, durationMs: 30);
        var offFrequencyEnvelope = FeedTone(detector1320, 1320.0, durationMs: 30);

        Assert.True(onFrequencyEnvelope > offFrequencyEnvelope,
            $"On-frequency envelope {onFrequencyEnvelope:F4} should exceed off-frequency envelope {offFrequencyEnvelope:F4}.");
    }

    [Fact]
    public void ToneRaceDetectors_DiscriminateBetween1080And1320Hz()
    {
        var detector1080 = new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0);
        var detector1320 = new SyncEnvelopeDetector(SampleRate, 1320.0, bandwidthHz: 80.0);

        var d11 = FeedTone(detector1080, 1080.0, durationMs: 30);
        var d13 = FeedTone(detector1320, 1080.0, durationMs: 30);

        Assert.True(d11 > d13, $"1080Hz detector ({d11:F4}) should read higher than 1320Hz detector ({d13:F4}) for a 1080Hz tone.");
    }

    [Fact]
    public void DefaultBandwidth_MatchesExplicit100HzConstruction()
    {
        var implicitDetector = new SyncEnvelopeDetector(SampleRate, 1200.0);
        var explicitDetector = new SyncEnvelopeDetector(SampleRate, 1200.0, bandwidthHz: 100.0);

        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var sample = random.NextDouble() * 2 - 1;
            Assert.Equal(explicitDetector.ProcessSample(sample), implicitDetector.ProcessSample(sample), precision: 12);
        }
    }

    private static double FeedTone(SyncEnvelopeDetector detector, double frequencyHz, double durationMs)
    {
        var sampleCount = (int)(durationMs / 1000.0 * SampleRate);
        double envelope = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate);
            envelope = detector.ProcessSample(sample);
        }

        return envelope;
    }
}
