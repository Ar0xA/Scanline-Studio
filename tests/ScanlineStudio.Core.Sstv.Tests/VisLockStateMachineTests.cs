using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

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

    [Fact]
    public void IsSearching_FalseOnceTooShortBlipEntersConfirmLock_TrueAgainAfterItResets()
    {
        // S12: IsSearching is the new gate AnalogFmSstvDecoder.TrySyncIntervalDetectionStep uses to
        // freeze m_sint2's SyncStart / m_sint3's whole block outside legacy's own case 0. Reuses the
        // exact blip shape TooShortBlip_NeverAdvancesPastConfirmLock already proves enters ConfirmLock
        // but can't complete its 15ms sustained-hold requirement (sstv.cpp:1952-1973, "ANY single
        // failing sample resets to Search immediately") -- so IsSearching must go false at some point
        // (ConfirmLock entered) and true again by the end (reset back to Search once the blip ends).
        var segments = new (double FrequencyHz, double DurationMs)[]
        {
            (1900.0, 100.0),
            (1200.0, 10.0),
            (1900.0, 500.0),
        };
        var samples = RenderSegments(segments, SampleRate);

        var machine = new VisLockStateMachine(SampleRate, AnalogFmSstvDecoder.SLvl, AnalogFmSstvDecoder.SLvl2);
        var agc = new LevelAgc(SampleRate);

        Assert.True(machine.IsSearching);
        var sawConfirmLock = false;
        (SstvModeDefinition Mode, int LineStartSample)? result = null;
        foreach (var sample in samples)
        {
            var scaled = sample * 32768.0;
            agc.Do(scaled);
            agc.Fix();
            var agcSample = Math.Clamp(agc.Agc(scaled) * 32.0, -16384.0, 16384.0);
            result = machine.ProcessSample(agcSample);
            if (!machine.IsSearching)
            {
                sawConfirmLock = true;
            }
        }

        Assert.Null(result); // matches TooShortBlip_NeverAdvancesPastConfirmLock -- never locks
        Assert.True(sawConfirmLock); // but did enter ConfirmLock at some point
        Assert.True(machine.IsSearching); // and reset back to Search once the blip ended
    }

    [Fact]
    public void IsAtOrBeforeConfirmLock_FalseWhileRealVisHeaderIsDecodingVisBits()
    {
        // S12: IsAtOrBeforeConfirmLock is the wider gate m_sint2's SyncMax continuation needs
        // (legacy keeps updating it through case 0 AND case 1, sstv.cpp:1953-1957, but never during
        // real VIS-bit decode/verify, cases 2/9/3). A clean, real VIS header passes through both
        // Search and ConfirmLock (IsAtOrBeforeConfirmLock true), then spends real time decoding VIS
        // bits with it false, before finally locking -- checked on the sample BEFORE the lock-
        // returning call, since a successful lock resets the internal state back to Search for the
        // next header in the same instant it returns (see ProcessSample's own Verify case).
        var mode = SstvModeRegistry.MartinM1;
        var samples = RenderSegments(VisHeader.GenerateSegments(mode.VisCode), SampleRate);

        var machine = new VisLockStateMachine(SampleRate, AnalogFmSstvDecoder.SLvl, AnalogFmSstvDecoder.SLvl2);
        var agc = new LevelAgc(SampleRate);

        var sawAtOrBeforeConfirmLock = false;
        var sawPastConfirmLock = false;
        (SstvModeDefinition Mode, int LineStartSample)? result = null;
        foreach (var sample in samples)
        {
            var scaled = sample * 32768.0;
            agc.Do(scaled);
            agc.Fix();
            var agcSample = Math.Clamp(agc.Agc(scaled) * 32.0, -16384.0, 16384.0);
            var stepResult = machine.ProcessSample(agcSample);
            if (stepResult is not null)
            {
                result = stepResult;
                break;
            }

            if (machine.IsAtOrBeforeConfirmLock)
            {
                sawAtOrBeforeConfirmLock = true;
            }
            else
            {
                sawPastConfirmLock = true;
            }
        }

        Assert.NotNull(result);
        Assert.Equal(mode.Id, result!.Value.Mode.Id);
        Assert.True(sawAtOrBeforeConfirmLock);
        Assert.True(sawPastConfirmLock);
    }

    private static (SstvModeDefinition Mode, int LineStartSample)? FeedUntilLocked(float[] samples)
    {
        // As of piece 7c, ProcessSample expects the shared AGC'd/scaled signal (see its own doc
        // comment), not a raw sample -- mirrors AnalogFmSstvDecoder.AgcSampleAt exactly (same scale
        // bridge, same LevelAgc.Do/Fix/Agc call order) so this isolated test still exercises the
        // real absolute-threshold conditions rather than silently reverting to the pre-7c relative-
        // only ones.
        var machine = new VisLockStateMachine(SampleRate, AnalogFmSstvDecoder.SLvl, AnalogFmSstvDecoder.SLvl2);
        var agc = new LevelAgc(SampleRate);
        foreach (var sample in samples)
        {
            var scaled = sample * 32768.0;
            agc.Do(scaled);
            agc.Fix();
            var agcSample = Math.Clamp(agc.Agc(scaled) * 32.0, -16384.0, 16384.0);

            var result = machine.ProcessSample(agcSample);
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
