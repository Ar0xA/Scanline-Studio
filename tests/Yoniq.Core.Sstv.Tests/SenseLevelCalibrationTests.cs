namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Measured, not assumed: piece 7c reintroduced legacy's absolute-amplitude thresholds
/// (<see cref="AnalogFmSstvDecoder.SLvl"/>/<see cref="AnalogFmSstvDecoder.SLvl2"/>/
/// <see cref="AnalogFmSstvDecoder.SLvl3"/>) into <c>TrySyncIntervalDetection</c> and
/// <see cref="VisLockStateMachine"/>. Those thresholds are only meaningful if a real full-amplitude
/// tone, pushed through this port's own <see cref="LevelAgc"/> + <see cref="SyncEnvelopeDetector"/>
/// pipeline (the same math <c>AnalogFmSstvDecoder.AgcSampleAt</c> uses), actually produces an
/// envelope comfortably above them -- otherwise every trigger condition this piece just wired in
/// would be permanently unreachable, silently disabling m_sint1/m_sint2/m_sint3/VisLockStateMachine
/// entirely rather than gating them as intended. This is a calibration sanity check on the constants
/// themselves, not a round-trip decode test.
/// </summary>
public class SenseLevelCalibrationTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void FullScale1200HzTone_D12ClearsSLvlWithRealMargin_D19StaysWellBelow()
    {
        var (onTone, offTone) = MeasureSteadyStateEnvelopes(toneHz: 1200.0, onDetectorHz: 1200.0, offDetectorHz: 1900.0);

        // d12 must clear SLvl (3500) -- the trigger this gates (sstv.cpp:1946/1958) must actually be
        // reachable by a real full-amplitude tone once CLVL's AGC has warmed up.
        Assert.True(onTone > AnalogFmSstvDecoder.SLvl, $"d12 {onTone:F0} did not clear SLvl {AnalogFmSstvDecoder.SLvl}.");

        // The difference gate ((d12-d19)>=SLvl) must also be satisfiable: the off-frequency detector
        // has to stay low enough, not just "lower than d12".
        Assert.True(onTone - offTone >= AnalogFmSstvDecoder.SLvl,
            $"d12-d19 {onTone - offTone:F0} did not clear the SLvl {AnalogFmSstvDecoder.SLvl} difference gate.");
    }

    [Fact]
    public void FullScale1900HzTone_D19ClearsTightestSLvl3WithRealMargin_D12StaysWellBelow()
    {
        var (onTone, offTone) = MeasureSteadyStateEnvelopes(toneHz: 1900.0, onDetectorHz: 1900.0, offDetectorHz: 1200.0);

        // SLvl3 (5700) is the tightest margin of the three thresholds (Opus review finding D) --
        // confirm it's still clearable, not just SLvl/SLvl2's looser ones.
        Assert.True(onTone > AnalogFmSstvDecoder.SLvl3, $"d19 {onTone:F0} did not clear SLvl3 {AnalogFmSstvDecoder.SLvl3}.");

        // m_sint3's difference gate uses SLvl3 against d12 (sstv.cpp:1926's (d19-d12)>=m_SLvl3).
        Assert.True(onTone - offTone >= AnalogFmSstvDecoder.SLvl3,
            $"d19-d12 {onTone - offTone:F0} did not clear the SLvl3 {AnalogFmSstvDecoder.SLvl3} difference gate.");
    }

    /// <summary>Mirrors <c>AnalogFmSstvDecoder.AgcSampleAt</c>'s exact math (LevelAgc.Do/Fix/Agc, the
    /// 32768x scale bridge, the *32 gain and ±16384 clamp) rather than calling into the decoder
    /// itself -- an isolated calibration measurement, not a round-trip decode.</summary>
    private static (double OnToneEnvelope, double OffToneEnvelope) MeasureSteadyStateEnvelopes(double toneHz, double onDetectorHz, double offDetectorHz)
    {
        var agc = new LevelAgc(SampleRate);
        var onDetector = new SyncEnvelopeDetector(SampleRate, onDetectorHz);
        var offDetector = new SyncEnvelopeDetector(SampleRate, offDetectorHz);

        var phase = 0.0;
        var phaseIncrement = 2 * Math.PI * toneHz / SampleRate;
        double lastOn = 0, lastOff = 0;

        // 1 second -- comfortably past LevelAgc's warm-up (Fix() only recomputes m_agc once >=100ms
        // of samples have accumulated) and past the envelope detectors' own filter settling time.
        var sampleCount = SampleRate;
        for (var i = 0; i < sampleCount; i++)
        {
            phase += phaseIncrement;
            if (phase >= 2 * Math.PI)
            {
                phase -= 2 * Math.PI;
            }

            var raw = Math.Sin(phase);
            var scaled = raw * 32768.0;
            agc.Do(scaled);
            agc.Fix();
            var agcSample = Math.Clamp(agc.Agc(scaled) * 32.0, -16384.0, 16384.0);

            lastOn = onDetector.ProcessSample(agcSample);
            lastOff = offDetector.ProcessSample(agcSample);
        }

        return (lastOn, lastOff);
    }
}
