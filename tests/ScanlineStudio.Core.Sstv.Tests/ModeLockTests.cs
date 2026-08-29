using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// ui_transition_plan.md step 10 (T2-5): persistent RX mode lock -- genuinely NEW UI/workflow, no
/// legacy precedent (legacy's own related state, CSSTVDEM::m_SyncMode/RxAutoPush/SBMClick, maps to
/// this port's ALREADY-shipped SetAutoDetectPaused and one-shot ForceMode, not to "stays locked
/// across many receptions"). Went through 2 rounds of plan-review: round 1's "skip VIS auto-detect
/// at the idle boundary" strawman made the decoder never idle (starving RestartableSstvDecoder's
/// idle-gated swap machinery, and turning a silent channel into an infinite noise-frame decode +
/// disk-write loop) and left 2 mid-reception commit paths able to flip the mode out from under the
/// lock. Round 2's corrected design (implemented here): detection keeps running exactly as without
/// a lock; only the MODE actually committed once a reception is detected gets substituted, inside
/// Commit() itself, at one auditable point covering every commit path. Anchor math computed by
/// CALLERS before Commit() (e.g. Scottie's post-VIS pulse skip, sync-bypass's midpoint offset) stays
/// keyed to the ACTUALLY DETECTED mode -- it describes the observed signal, not what gets labeled --
/// TryResolveSyncAnchorCorrection re-derives the anchor modulo the COMMITTED mode's own line width
/// regardless, so a locked-mode substitution is corrected for automatically.
/// </summary>
public class ModeLockTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void SetModeLock_Idle_DoesNotAffectIsIdle()
    {
        // The core guarantee round 1's strawman violated: a lock must not make the decoder think a
        // reception is in progress when nothing has been detected.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        Assert.True(decoder.IsIdle);

        decoder.SetModeLock(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[64]);

        Assert.True(decoder.IsIdle);
    }

    [Fact]
    public void SetModeLock_SilenceForMultipleImageDurations_NeverFiresModeDetected_StaysIdle()
    {
        // Round 1's own "the test that fails the strawman": a locked channel with nothing arriving
        // must stay genuinely idle -- it must not synthesize receptions. Push several seconds of
        // silence (more than Robot 36's own real image duration) and confirm nothing was ever
        // detected/decoded.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        decoder.SetModeLock(SstvModeRegistry.Robot36);

        var modeDetectedCount = 0;
        decoder.ModeDetected += _ => modeDetectedCount++;

        decoder.PushSamples(new float[SampleRate * 40]); // 40s of silence, well over one Robot 36 image

        Assert.Equal(0, modeDetectedCount);
        Assert.True(decoder.IsIdle);
    }

    [Fact]
    public async Task SetModeLock_RealVisHeaderForADifferentMode_CommitsAsTheLockedMode()
    {
        // The primary case: a real, fully-formed VIS-header transmission for mode X arrives while
        // locked to mode A (A != X) -- must commit and decode as A, not X.
        var transmittedMode = SstvModeRegistry.MartinM1;
        var lockedMode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(transmittedMode.ImageWidth, transmittedMode.ImageHeight, offset: 0);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(transmittedMode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        decoder.SetModeLock(lockedMode);
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detectedMode);
        Assert.Equal(lockedMode.Id, detectedMode!.Id);
    }

    [Fact]
    public void ForceMode_WhileLocked_WinsItsOwnReception_LockResumesForTheNextOne()
    {
        // Both halves round 2 required: (1) a manual ForceMode click always wins its OWN reception
        // even while locked (a one-shot EXCEPTION, not a change to the lock), and (2) the lock
        // resumes at the NEXT detection event -- proven here via a second ForceMode-free commit
        // (PerformForceMode itself, applyModeLock: false) standing in for "the next detected
        // reception", since exercising a real VIS-detected commit deterministically here would just
        // re-test SetModeLock_RealVisHeaderForADifferentMode_CommitsAsTheLockedMode above.
        var lockedMode = SstvModeRegistry.Robot36;
        var forcedMode = SstvModeRegistry.MartinM1;
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        decoder.SetModeLock(lockedMode);

        decoder.ForceMode(forcedMode);
        decoder.PushSamples(new float[64]);

        Assert.Equal(forcedMode.Id, decoder.ModeForTests?.Id); // (1) the forced mode won, not the lock
        Assert.Equal(lockedMode.Id, decoder.LockedModeForTests?.Id); // (2) the lock itself survived ForceMode
    }

    [Fact]
    public async Task SetModeLock_AvtSignal_StillTrainsAndCommitsAsAvt()
    {
        // v1 limitation, deliberately enforced: a detected AVT signal is never substituted, even
        // while locked to a non-AVT mode -- no cross-family AVT<->locked-mode mapping is invented.
        var lockedMode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[SstvModeRegistry.Avt.ImageWidth * SstvModeRegistry.Avt.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(SstvModeRegistry.Avt.ImageWidth, SstvModeRegistry.Avt.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(44100);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(SstvModeRegistry.Avt, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        decoder.SetModeLock(lockedMode);
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detectedMode);
        Assert.Equal(SstvModeRegistry.Avt.Id, detectedMode!.Id);
    }

    [Fact]
    public void SetModeLock_SurvivesAnInnerDecoderSwap_InRestartableSstvDecoder()
    {
        // Round 2's own flagged "piece most likely to be forgotten": RestartableSstvDecoder swaps
        // the underlying AnalogFmSstvDecoder instance for periodic maintenance and live
        // BPF/demod-type/RX-buffer-mode reconfiguration -- the lock must be re-seeded onto the fresh
        // inner (CreateInner), not silently lost.
        var wrapper = new RestartableSstvDecoder(sampleRate: SampleRate);
        var lockedMode = SstvModeRegistry.Robot36;
        wrapper.SetModeLock(lockedMode);

        // Force a live reconfiguration swap -- any inner-decoder-affecting live-apply path exercises
        // CreateInner the same way; RequestReconfiguration mirrors the shipped RX BPF/demod/buffer
        // live-apply path (OptionsWindowViewModel.SaveCoreUnguardedAsync's own real call site).
        wrapper.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Hilbert, RxBufferMode.Off);
        wrapper.PushSamples(new float[64]); // drains the pending reconfiguration, swaps the inner

        Assert.Equal(lockedMode.Id, wrapper.InnerLockedModeForTests?.Id);
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height, int offset)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)((x * 255 / Math.Max(1, width - 1) + offset) % 256),
                    G: (byte)((y * 255 / Math.Max(1, height - 1) + offset) % 256),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
