using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Piece 9: <see cref="AnalogFmSstvDecoder.TryDecodeVisHeader"/>'s VIS-bit decode now races
/// dedicated 1080Hz/1320Hz envelope detectors (<see cref="VisBitDecision"/>) instead of reading the
/// shared PLL's demodulated-frequency stream -- see that method's own doc comment and
/// spec/14-roadmap.md's "Piece 9" entry. This file regression-guards the two real bugs the switch
/// surfaced (both caught by full-suite/golden-vector runs, not anticipated up front):
///
/// 1. A first version had no precondition before racing the tone detectors at all, so it could
///    (and did, on <c>GoldenVectorTests</c>' real martin-m1 capture's mic-noise lead-in) decode a
///    registered VIS byte from pure noise -- a false lock. Legacy never runs the tone race without
///    first requiring a genuine, sustained 1200Hz dominant tone (case 0 trigger + case 1's 15ms
///    hold, <c>sstv.cpp:1946-1973</c>).
/// 2. Fixing #1 by checking that hold at a fixed, analytically-idealized 610ms offset then broke
///    genuine signal (AVT's own round-trip tests): a real filtered tone transition isn't
///    instantaneous, so requiring dominance starting at the exact idealized instant is too strict.
///    The fix searches for the trigger dynamically instead of assuming a fixed offset.
/// </summary>
public class VisToneRaceHeaderTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void PureNoise_NeverProducesASpuriousLock()
    {
        // Regression test for bug #1 above, without depending on the real golden-vector fixture
        // files: several seconds of deterministic pseudo-random noise (no VIS content at all)
        // should never lock onto any mode -- if it does, some registered VIS byte was raced out of
        // noise the way sc2-120's was from martin-m1's real mic-noise lead-in.
        var random = new Random(Seed: 12345);
        var noise = new float[8 * SampleRate];
        for (var i = 0; i < noise.Length; i++)
        {
            // Comparable RMS to the real martin-m1 fixture's own measured lead-in noise (~0.044
            // RMS, ~0.15 peak) -- not an arbitrarily quiet or arbitrarily loud synthetic choice.
            noise[i] = (float)((random.NextDouble() * 2.0 - 1.0) * 0.15);
        }

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected = m;

        decoder.PushSamples(noise);

        Assert.Null(detected);
    }

    [Theory]
    [InlineData("mr73")] // extended VIS (2-byte code)
    [InlineData("martin-m1")] // normal VIS (1-byte code)
    [InlineData("avt")] // full 8-bit VIS byte match (parity bit included), longest header
    public async Task GenuineHeader_StillDecodesCorrectly_ImmediatelyAtStreamStart(string modeId)
    {
        // A direct check that the new tone-race mechanism (not just the old PLL-average proxy it
        // replaced) still recovers the right VIS byte with zero lead-in silence -- the case the
        // dynamic-trigger-search fix (bug #2 above) had to keep working while fixing bug #1.
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var sourceImage = new Yoniq.Core.Imaging.ArrayImageSource(
            mode.ImageWidth, mode.ImageHeight, new Yoniq.Abstractions.Imaging.Rgb24[mode.ImageWidth * mode.ImageHeight]);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected ??= m;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detected);
        Assert.Equal(mode.Id, detected!.Id);
    }

    [Theory]
    [InlineData("martin-m1")]
    [InlineData("mr73")]
    public void FixedWindowPathAndVisLockStateMachine_AgreeOnTheSameHeader(string modeId)
    {
        // Round-2-code-review-recommended pinning test: TryDecodeVisHeader (fixed-window, exercised
        // via the full decoder with a header starting exactly at sample 0) and VisLockStateMachine
        // (fed directly, sample-by-sample) now share the same VisBitDecision predicate but keep
        // fully independent detector instances and timing -- this asserts they still land on the
        // same decoded mode for the same real header, rather than trusting the shared-predicate
        // refactor without a direct cross-check. AVT excluded: VisLockStateMachine deliberately never
        // reports it (see that class's own doc comment), so it can't be part of this comparison.
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var segments = mode.ExtendedVisCode is not null
            ? VisHeader.GenerateExtendedSegments(mode.ExtendedVisCode.Value)
            : VisHeader.GenerateSegments(mode.VisCode);
        // Trailing silence margin: `ModeDetected` only fires once TryResolveSyncAnchorCorrection's
        // own piece-8 sync-anchor fold completes, which needs 3-4 full transmission lines' worth of
        // trailing content buffered (mode-dependent, e.g. ~1.8s for martin-m1 at 11025Hz) -- a bare
        // header with no trailing "image" content never reaches that point, even though the header
        // itself decodes correctly and `_mode` is already set internally. 3s covers every mode.
        var samples = RenderSegments(segments.Append((0.0, 3000.0)), SampleRate);

        var fixedWindowDecoder = new AnalogFmSstvDecoder(SampleRate);
        SstvModeDefinition? fixedWindowResult = null;
        fixedWindowDecoder.ModeDetected += m => fixedWindowResult ??= m;
        fixedWindowDecoder.PushSamples(samples);

        var machine = new VisLockStateMachine(SampleRate, AnalogFmSstvDecoder.SLvl, AnalogFmSstvDecoder.SLvl2);
        var agc = new LevelAgc(SampleRate);
        SstvModeDefinition? visLockResult = null;
        foreach (var sample in samples)
        {
            var scaled = sample * 32768.0;
            agc.Do(scaled);
            agc.Fix();
            var agcSample = Math.Clamp(agc.Agc(scaled) * 32.0, -16384.0, 16384.0);
            var result = machine.ProcessSample(agcSample);
            if (result is not null)
            {
                visLockResult = result.Value.Mode;
                break;
            }
        }

        Assert.NotNull(fixedWindowResult);
        Assert.NotNull(visLockResult);
        Assert.Equal(fixedWindowResult!.Id, visLockResult!.Id);
    }

    [Fact]
    public void SpuriousTriggerOnTheBreakTone_StillRecoversTheRealHeader()
    {
        // Code-review finding (Piece 9, "Risk 1"): legacy's real receiver never permanently gives up
        // on a rejected bit-decode attempt -- it just resets to case 0 and re-triggers on the very
        // next qualifying sample (sstv.cpp:1957/1972/1983). A first version of TryDecodeVisDataBits
        // aborted the whole attempt on any reject instead, which could permanently kill detection if
        // an early, shorter-than-normal trigger candidate (the header's own 1200Hz break tone,
        // normally only 10ms -- too short to complete the 15ms hold on a clean signal) ever DID
        // complete a hold, e.g. under real filter-settling/noise conditions the auditor flagged as
        // plausible but unconfirmed. This test forces that exact scenario directly (an artificially
        // widened 30ms break, long enough to reliably complete the hold on its own) rather than
        // relying on it happening to occur in noisy real audio, and asserts the decoder still
        // recovers the real header afterward instead of getting stuck.
        var mode = SstvModeRegistry.MartinM1;
        var segments = VisHeader.GenerateSegments(mode.VisCode).ToList();
        segments[1] = (segments[1].FrequencyHz, 30.0); // 10ms break -> 30ms, matching the real
                                                         // start-bit tone's own duration
        segments.Add((0.0, 3000.0)); // trailing margin -- see FixedWindowPathAndVisLockStateMachine_
                                      // AgreeOnTheSameHeader's own comment for why this is needed
        var samples = RenderSegments(segments, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected ??= m;

        decoder.PushSamples(samples);

        Assert.NotNull(detected);
        Assert.Equal(mode.Id, detected!.Id);
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

        return samples.ToArray();
    }
}
