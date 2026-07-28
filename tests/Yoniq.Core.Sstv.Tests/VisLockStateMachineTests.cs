using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="VisLockStateMachine"/>, driven directly with samples rendered from
/// <see cref="VisHeader"/>'s own segment generators -- no <see cref="AnalogFmSstvDecoder"/> involved,
/// per this project's established "verify each sub-piece before wiring" methodology.
/// </summary>
public class VisLockStateMachineTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void CleanNormalVisHeader_LocksTheRightMode()
    {
        var mode = SstvModeRegistry.MartinM1;
        var samples = RenderSegments(VisHeader.GenerateSegments(mode.VisCode), SampleRate);

        var result = FeedUntilLocked(samples);

        Assert.NotNull(result);
        Assert.Equal(mode.Id, result!.Value.Mode.Id);
    }

    [Fact]
    public void TooShortBlip_NeverAdvancesPastConfirmLock()
    {
        // A 10ms 1200Hz blip (like the real leader/break pulse, sstv.cpp's own break tone) is
        // shorter than ConfirmLock's 15ms sustained-hold requirement -- should never lock, even
        // with plenty of trailing samples for it to fail against.
        var segments = new (double FrequencyHz, double DurationMs)[]
        {
            (1900.0, 100.0),
            (1200.0, 10.0),
            (1900.0, 500.0),
        };
        var samples = RenderSegments(segments, SampleRate);

        var result = FeedUntilLocked(samples);

        Assert.Null(result);
    }

    [Fact]
    public void ExtendedVisHeader_LocksTheRightModeViaSecondByte()
    {
        var mode = SstvModeRegistry.Mr73;
        var samples = RenderSegments(VisHeader.GenerateExtendedSegments(mode.ExtendedVisCode!.Value), SampleRate);

        var result = FeedUntilLocked(samples);

        Assert.NotNull(result);
        Assert.Equal(mode.Id, result!.Value.Mode.Id);
    }

    [Fact]
    public void FlippedDataBit_NeverLocks()
    {
        // Robot36's real VIS code is 8 (0b0001000). Flip the bottom data bit (bit 0) to 9
        // (0b0001001) -- a byte no legacy mode owns -- while keeping correct parity for that
        // (now-wrong) code, so this exercises a genuinely wrong data byte, not a parity mismatch.
        var wrongVisCode = SstvModeRegistry.Robot36.VisCode ^ 0b0000001;
        var samples = RenderSegments(VisHeader.GenerateSegments(wrongVisCode), SampleRate);

        var result = FeedUntilLocked(samples);

        Assert.Null(result);
    }

    [Fact]
    public void FlippedParityBit_NeverLocks()
    {
        // Real VIS code with a deliberately wrong (forced) parity bit -- exercises
        // FindByFullVisByte's rejection of an otherwise-correct data byte with bad parity,
        // matching legacy's own `default:` rejection (sstv.cpp:2071-2073).
        var mode = SstvModeRegistry.Robot36;
        var correctParity = 0;
        for (var bitIndex = 0; bitIndex < 7; bitIndex++)
        {
            correctParity ^= (mode.VisCode >> bitIndex) & 1;
        }

        var samples = RenderSegments(VisHeader.GenerateSegments(mode.VisCode, forcedParityBit: correctParity ^ 1), SampleRate);

        var result = FeedUntilLocked(samples);

        Assert.Null(result);
    }

    [Fact]
    public void HeaderAfterSecondsOfSilence_StillLocksAtTheRightOffset()
    {
        // The actual new capability: a fixed-window decoder anchored at sample 0 could never find
        // this header, since it doesn't start there.
        var mode = SstvModeRegistry.MartinM1;
        var silenceSampleCount = 3 * SampleRate;
        var silence = new float[silenceSampleCount];
        var headerSamples = RenderSegments(VisHeader.GenerateSegments(mode.VisCode), SampleRate);
        var samples = silence.Concat(headerSamples).ToArray();

        var result = FeedUntilLocked(samples);

        Assert.NotNull(result);
        Assert.Equal(mode.Id, result!.Value.Mode.Id);

        // Measured, not assumed: the trigger only fires once the d12/d19 envelope detectors'
        // resonate-rectify-smooth filter chain has actually settled past the crossing point, a
        // real, bounded lag unique to this path (the fixed-window header path has no such lag,
        // since its window placement is fully analytic) -- same category of documented filter-delay
        // cost already recorded for SyncEnvelopeDetector's peak-position approximation elsewhere in
        // this port. Measured here: ~80 samples (~7.3ms) at 11025Hz for a clean synthetic tone with
        // no noise. 150-sample headroom catches a real anchor-formula regression while tolerating
        // this expected lag.
        var expectedLineStart = silenceSampleCount + (int)Math.Round(VisHeader.TotalDurationMs / 1000.0 * SampleRate);
        Assert.InRange(result!.Value.LineStartSample, expectedLineStart, expectedLineStart + 150);
    }

    private static (SstvModeDefinition Mode, int LineStartSample)? FeedUntilLocked(float[] samples)
    {
        var machine = new VisLockStateMachine(SampleRate);
        foreach (var sample in samples)
        {
            var result = machine.ProcessSample(sample);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static float[] RenderSegments(IEnumerable<(double FrequencyHz, double DurationMs)> segments, int sampleRate)
    {
        var samples = new List<float>();
        var phase = 0.0;
        var idealSamplesSoFar = 0.0;
        long emittedSamples = 0;

        foreach (var (frequencyHz, durationMs) in segments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * sampleRate;
            var targetEmitted = (long)Math.Round(idealSamplesSoFar);
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            var phaseIncrement = 2 * Math.PI * frequencyHz / sampleRate;
            for (var i = 0; i < samplesToEmit; i++)
            {
                phase += phaseIncrement;
                if (phase >= 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }

                samples.Add((float)Math.Sin(phase));
            }
        }

        // Small trailing pad so envelope-filter settling near the very end of the header has real
        // content to read, matching how real image data would follow in practice.
        for (var i = 0; i < sampleRate / 10; i++)
        {
            samples.Add(0f);
        }

        return samples.ToArray();
    }
}
