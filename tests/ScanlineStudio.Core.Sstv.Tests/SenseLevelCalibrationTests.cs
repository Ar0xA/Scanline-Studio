namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Measured, not assumed: piece 7c reintroduced legacy's absolute-amplitude thresholds
/// (<see cref="AnalogFmSstvDecoder.SLvl"/>/<see cref="AnalogFmSstvDecoder.SLvl2"/>/
/// <see cref="AnalogFmSstvDecoder.SLvl3"/>) into <c>TrySyncIntervalDetectionStep</c> and
/// <see cref="VisLockStateMachine"/>. Those thresholds are only meaningful if a real full-amplitude
/// tone, pushed through this port's own <see cref="LevelAgc"/> + <see cref="SyncEnvelopeDetector"/>
/// pipeline (the same math <c>AnalogFmSstvDecoder.AgcSampleAt</c> uses), actually produces an
/// envelope comfortably above them -- otherwise every trigger condition this piece just wired in
/// would be permanently unreachable, silently disabling m_sint1/m_sint2/m_sint3/VisLockStateMachine
/// entirely rather than gating them as intended. This is a calibration sanity check on the constants
/// themselves, not a round-trip decode test.
///
/// Extended (Sense level wiring) to cover all 4 <see cref="AnalogFmSstvDecoder.SenseLevelPresets"/>
/// rows, not just the shipped default (preset 1) -- exposing the other 3 via
/// <c>Options.Decode.SenseLevel</c> is pointless if their thresholds are calibration-unreachable.
/// </summary>
public class SenseLevelCalibrationTests
{
    private const int SampleRate = 11025;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullScale1200HzTone_D12ClearsSLvlWithRealMargin_D19StaysWellBelow(int presetIndex)
    {
        var (slvl, _, _) = AnalogFmSstvDecoder.SenseLevelPresets[presetIndex];
        var (onTone, offTone) = MeasureSteadyStateEnvelopes(toneHz: 1200.0, onDetectorHz: 1200.0, offDetectorHz: 1900.0);

        // d12 must clear SLvl -- the trigger this gates (sstv.cpp:1946/1958) must actually be
        // reachable by a real full-amplitude tone once CLVL's AGC has warmed up, for EVERY preset a
        // user can select via Options.Decode.SenseLevel, not just the shipped default.
        Assert.True(onTone > slvl, $"preset {presetIndex}: d12 {onTone:F0} did not clear SLvl {slvl}.");

        // The difference gate ((d12-d19)>=SLvl) must also be satisfiable: the off-frequency detector
        // has to stay low enough, not just "lower than d12".
        Assert.True(onTone - offTone >= slvl,
            $"preset {presetIndex}: d12-d19 {onTone - offTone:F0} did not clear the SLvl {slvl} difference gate.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullScale1900HzTone_D19ClearsTightestSLvl3WithRealMargin_D12StaysWellBelow(int presetIndex)
    {
        var (_, _, slvl3) = AnalogFmSstvDecoder.SenseLevelPresets[presetIndex];
        var (onTone, offTone) = MeasureSteadyStateEnvelopes(toneHz: 1900.0, onDetectorHz: 1900.0, offDetectorHz: 1200.0);

        // SLvl3 is the tightest margin of the three thresholds in every preset row (Opus review
        // finding D, originally against preset 1's 5700) -- confirm it's still clearable for every
        // preset, not just the loosest one. Preset 3 ("Very high", SLvl3=8000) is this port's own
        // measured ceiling: if this assertion ever fails here, per this project's process the fix is
        // to re-verify this port's AGC scale against legacy's CLVL first (do NOT loosen this
        // assertion or the SenseLevelPresets constant to make it pass) -- as of writing, preset 3
        // clears with real margin, confirming this port's AGC scale is faithful enough that "Very
        // high" is a real, reachable (if near-mute) squelch setting, not a broken one.
        Assert.True(onTone > slvl3, $"preset {presetIndex}: d19 {onTone:F0} did not clear SLvl3 {slvl3}.");

        // m_sint3's difference gate uses SLvl3 against d12 (sstv.cpp:1926's (d19-d12)>=m_SLvl3).
        Assert.True(onTone - offTone >= slvl3,
            $"preset {presetIndex}: d19-d12 {onTone - offTone:F0} did not clear the SLvl3 {slvl3} difference gate.");
    }

    /// <summary>Transcription-accuracy check, independent of the calibration tests above: confirms
    /// <see cref="AnalogFmSstvDecoder.SenseLevelPresets"/> matches <c>CSSTVDEM::SetSenseLvl</c>
    /// (<c>sstv.cpp:1793-1817</c>) exactly, row for row -- catches a hand-transcription slip (e.g. a
    /// preset's SLvl2 accidentally colliding with a different preset's SLvl) that a calibration
    /// margin check alone wouldn't necessarily catch.</summary>
    [Fact]
    public void SenseLevelPresets_MatchLegacySetSenseLvlTable()
    {
        (double SLvl, double SLvl2, double SLvl3)[] expected =
        [
            (2400.0, 1200.0, 5000.0), // 0 -- switch default: (sstv.cpp:1812-1814)
            (3500.0, 1750.0, 5700.0), // 1 -- case 1, the real shipped default (sstv.cpp:1794-1797)
            (4800.0, 2400.0, 6800.0), // 2 -- case 2 (sstv.cpp:1799-1802)
            (6000.0, 3000.0, 8000.0), // 3 -- case 3 (sstv.cpp:1804-1807)
        ];

        Assert.Equal(expected, AnalogFmSstvDecoder.SenseLevelPresets);
    }

    /// <summary>Auditor-caught gap: every test above only checks the preset TABLE's values, not that
    /// a given <c>senseLevel</c> constructor argument actually reaches the running decoder -- a
    /// dropped/ignored parameter would pass all of them. Asserts directly against the decoder's own
    /// internal <c>_slvl</c>/<c>_slvl2</c>/<c>_slvl3</c> fields (internal, not private, specifically
    /// for this) rather than an amplitude-based behavioral probe: <see cref="LevelAgc"/> is a true
    /// AGC that normalizes toward a target level once settled, so scaling a synthetic tone's input
    /// amplitude down does not reliably produce a proportionally scaled steady-state envelope to
    /// assert threshold-crossing against -- a direct field check is both simpler and non-flaky.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SenseLevelConstructorArgument_ActuallyReachesTheDecodersThresholdFields(int presetIndex)
    {
        var decoder = new AnalogFmSstvDecoder(sampleRate: SampleRate, senseLevel: presetIndex);
        var expected = AnalogFmSstvDecoder.SenseLevelPresets[presetIndex];

        Assert.Equal(expected.SLvl, decoder._slvl);
        Assert.Equal(expected.SLvl2, decoder._slvl2);
        Assert.Equal(expected.SLvl3, decoder._slvl3);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(7)]
    public void SenseLevelConstructorArgument_OutOfRange_FallsBackToPreset0_NotAnException(int outOfRangeSenseLevel)
    {
        var decoder = new AnalogFmSstvDecoder(sampleRate: SampleRate, senseLevel: outOfRangeSenseLevel);
        var preset0 = AnalogFmSstvDecoder.SenseLevelPresets[0];

        Assert.Equal(preset0.SLvl, decoder._slvl);
        Assert.Equal(preset0.SLvl2, decoder._slvl2);
        Assert.Equal(preset0.SLvl3, decoder._slvl3);
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
