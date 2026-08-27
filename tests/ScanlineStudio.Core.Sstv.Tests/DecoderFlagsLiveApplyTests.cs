using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Restart-required-settings backlog item 1 (2026-08-27): AutoSyncEnabled/AutoStopEnabled/
/// AutoSlantEnabled/SyncRestartEnabled are now live-settable, same class of change as the earlier
/// Squelch level (SenseLevel) feature -- see this project's own 3-round plan-review for the two
/// real defects found and fixed before this shipped: a reachable decode-thread crash on
/// SyncRestartEnabled's false-&gt;true edge while a mode is locked (fixed by re-anchoring
/// VisLockStateMachine, gated on <c>_mode is not null</c>), and an unsafe plain-write property
/// shape (fixed by the CAS-merged <c>_desiredFlags</c>/<c>_appliedFlags</c> deferred latch).
/// </summary>
public class DecoderFlagsLiveApplyTests
{
    [Fact]
    public void AutoSyncEnabled_LiveSetterAppliesOnNextPushSamples()
    {
        var decoder = new AnalogFmSstvDecoder(autoSyncEnabled: true);
        Assert.True(decoder.AutoSyncEnabled);

        decoder.AutoSyncEnabled = false;
        Assert.True(decoder.AutoSyncEnabled, "Deferred -- must not apply until the next PushSamples call drains it.");

        decoder.PushSamples(new float[8]);
        Assert.False(decoder.AutoSyncEnabled);
    }

    [Fact]
    public void AutoStopEnabled_LiveSetterAppliesOnNextPushSamples()
    {
        var decoder = new AnalogFmSstvDecoder(autoStopEnabled: false);
        Assert.False(decoder.AutoStopEnabled);

        decoder.AutoStopEnabled = true;
        decoder.PushSamples(new float[8]);
        Assert.True(decoder.AutoStopEnabled);
    }

    [Fact]
    public void AutoSlantEnabled_LiveSetterAppliesOnNextPushSamples()
    {
        var decoder = new AnalogFmSstvDecoder(autoSlantEnabled: true);
        Assert.True(decoder.AutoSlantEnabled);

        decoder.AutoSlantEnabled = false;
        decoder.PushSamples(new float[8]);
        Assert.False(decoder.AutoSlantEnabled);
    }

    [Fact]
    public void SyncRestartEnabled_LiveSetterAppliesOnNextPushSamples()
    {
        var decoder = new AnalogFmSstvDecoder(syncRestartEnabled: true);
        Assert.True(decoder.SyncRestartEnabled);

        decoder.SyncRestartEnabled = false;
        decoder.PushSamples(new float[8]);
        Assert.False(decoder.SyncRestartEnabled);
    }

    [Fact]
    public void DecoderFlags_ConstructorValues_SurviveAPeriodicSwap()
    {
        // Same non-vacuous proof requirement as SenseLevel's own swap-survival tests
        // (RestartableSstvDecoderTests.cs) -- InnerXForTests reads the LIVE inner decoder's own
        // state post-swap, not just the wrapper's stored field.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000,
            autoSyncEnabled: false, autoStopEnabled: true, autoSlantEnabled: false, syncRestartEnabled: false);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.False(decoder.InnerAutoSyncEnabledForTests);
        Assert.True(decoder.InnerAutoStopEnabledForTests);
        Assert.False(decoder.InnerAutoSlantEnabledForTests);
        Assert.False(decoder.InnerSyncRestartEnabledForTests);
    }

    [Fact]
    public void DecoderFlags_SetLiveAfterConstruction_AlsoSurviveAPeriodicSwap()
    {
        // Same reasoning as SenseLevel_SetLiveAfterConstruction_AlsoSurvivesAPeriodicSwap -- proves
        // each setter updates the WRAPPER's stored field, not just the current inner instance
        // directly (the only way a later swap could know to re-apply it).
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000,
            autoSyncEnabled: true, autoStopEnabled: false, autoSlantEnabled: true, syncRestartEnabled: true);

        decoder.AutoSyncEnabled = false;
        decoder.AutoStopEnabled = true;
        decoder.AutoSlantEnabled = false;
        decoder.SyncRestartEnabled = false;

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // 1st call also drains every deferred inner request; 3rd crosses warningThresholdSamples=100
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.False(decoder.AutoSyncEnabled);
        Assert.True(decoder.AutoStopEnabled);
        Assert.False(decoder.AutoSlantEnabled);
        Assert.False(decoder.SyncRestartEnabled);
        Assert.False(decoder.InnerAutoSyncEnabledForTests);
        Assert.True(decoder.InnerAutoStopEnabledForTests);
        Assert.False(decoder.InnerAutoSlantEnabledForTests);
        Assert.False(decoder.InnerSyncRestartEnabledForTests);
    }

    [Fact]
    public void DecoderFlags_SequentialSetAndDrain_BothUpdatesTakeEffect()
    {
        // Plan-review round 2's own suggested regression check: set one flag, drain (a PushSamples
        // call), set a DIFFERENT flag, drain again -- both must end up applied, not just the second.
        // NOTE on scope: this is a deterministic, single-threaded proof that the CAS-merge mechanism
        // is wired correctly in the ordinary sequential case; it does NOT (and cannot, without a real
        // multi-threaded stress harness) reproduce the specific race round 2 found, which required a
        // UI-thread setter and a decode-thread drain genuinely interleaving. That race was fixed by
        // construction (the CAS baseline is now read from _desiredFlags, a field the drain never
        // writes -- see that field's own doc comment) and verified by code-level reasoning across 3
        // plan-review rounds, not by a flaky timing-dependent test. Deliberately not attempted here,
        // consistent with this project's own low tolerance for flaky tests.
        var decoder = new AnalogFmSstvDecoder(autoSyncEnabled: true, autoStopEnabled: false);

        decoder.AutoSyncEnabled = false;
        decoder.PushSamples(new float[8]);
        Assert.False(decoder.AutoSyncEnabled);

        decoder.AutoStopEnabled = true;
        decoder.PushSamples(new float[8]);

        Assert.False(decoder.AutoSyncEnabled); // the FIRST change must not have been lost
        Assert.True(decoder.AutoStopEnabled);
    }

    [Fact]
    public void SearchBandpassFilter_UpdateSyncRestart_ChangesH1Coefficients()
    {
        var filter = new SearchBandpassFilter(11025, RxBpfPreset.Wide, syncRestartEnabled: false);
        var h1WhenOff = (double[])filter.H1ForTests.Clone();

        filter.UpdateSyncRestart(true);
        var h1WhenOn = filter.H1ForTests;

        Assert.NotEqual(h1WhenOff, h1WhenOn); // fcl moved from 1200Hz to 1100Hz -- the coefficients must actually differ
        Assert.Equal(h1WhenOff.Length, h1WhenOn.Length); // tap count is invariant to this flag
    }

    [Fact]
    public void AnalogFmSstvDecoder_SyncRestartEnabled_LiveSetterRebuildsH1_NullSafeUnderRxBpfOff()
    {
        // Null-safety nit from plan-review round 3: RxBpfPreset.Off has no SearchBandpassFilter at
        // all (AnalogFmSstvDecoder's own bypass decision) -- the live setter's H1 rebuild must no-op,
        // not throw, in that case.
        var decoder = new AnalogFmSstvDecoder(rxBpfPreset: RxBpfPreset.Off, syncRestartEnabled: false);
        Assert.Null(decoder.SearchBandpassFilterForTests);

        decoder.SyncRestartEnabled = true;
        var ex = Record.Exception(() => decoder.PushSamples(new float[8]));

        Assert.Null(ex);
        Assert.True(decoder.SyncRestartEnabled);
    }

    [Fact]
    public void AnalogFmSstvDecoder_SyncRestartEnabled_LiveSetterRebuildsH1_WhenRxBpfIsActive()
    {
        var decoder = new AnalogFmSstvDecoder(rxBpfPreset: RxBpfPreset.Wide, syncRestartEnabled: false);
        var filter = decoder.SearchBandpassFilterForTests;
        Assert.NotNull(filter);
        var h1Before = (double[])filter!.H1ForTests.Clone();

        decoder.SyncRestartEnabled = true;
        decoder.PushSamples(new float[8]); // drains the deferred request, which rebuilds H1

        Assert.NotEqual(h1Before, filter.H1ForTests);
    }

    [Fact]
    public async Task SyncRestartEnabled_FalseToTrueMidLockedImage_DoesNotThrow_NoSpuriousRestart()
    {
        // Plan-review round 1 blocker: flipping SyncRestartEnabled false->true while a reception is
        // already LOCKED used to be reachable to a crash -- TrimBuffers freezes _visLockProcessedUpTo
        // behind the trim watermark while the flag is off (excluded from it, AnalogFmSstvDecoder.cs's
        // own TrimBuffers comment), so flipping it on resumes reading from a cursor that may already
        // have been trimmed past, throwing InvalidOperationException from Rel(). Fixed by re-anchoring
        // the cursor (Commit()'s own established triple) on exactly this edge, gated on the reception
        // actually being locked.
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var transmissionSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            transmissionSamples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(sampleRate, syncRestartEnabled: false);
        var restartCount = 0;
        decoder.DecodeRestarted += _ => restartCount++;
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        const int chunkSize = 500;
        // MinTrimSamples is 44100 (~4s @11025Hz) -- feed well past lock, plus a comfortable margin
        // past that, so TrimBuffers is guaranteed to have actually run at least once with the flag
        // off before the flip below.
        const int lockedSamplesNeededBeforeFlip = sampleRate * 6;
        var lockedAtSample = -1;
        var fed = 0;
        for (; fed < transmissionSamples.Count; fed += chunkSize)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Count - fed);
            decoder.PushSamples(transmissionSamples.GetRange(fed, length).ToArray());

            if (detectedMode is not null && lockedAtSample < 0)
            {
                lockedAtSample = fed + length;
            }

            if (lockedAtSample >= 0 && fed + length - lockedAtSample >= lockedSamplesNeededBeforeFlip)
            {
                fed += length;
                break;
            }
        }

        Assert.NotNull(detectedMode); // sanity: lock actually happened before this test ran out of transmission to feed
        Assert.Equal(mode.Id, detectedMode!.Id);

        decoder.SyncRestartEnabled = true;
        decoder.PushSamples(new float[8]); // drains the request -- applies the H1 rebuild + re-anchor

        // Continue feeding a few more real seconds -- this is where the pre-fix bug threw.
        var ex = Record.Exception(() =>
        {
            var continueUntil = Math.Min(transmissionSamples.Count, fed + (sampleRate * 5));
            for (var offset = fed; offset < continueUntil; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, continueUntil - offset);
                decoder.PushSamples(transmissionSamples.GetRange(offset, length).ToArray());
            }
        });

        Assert.Null(ex);
        Assert.Equal(0, restartCount); // the flip itself must not spuriously abort/restart the in-progress image
    }

    [Fact]
    public async Task SyncRestartEnabled_FlippedDuringPreLockHeaderScan_StillDetectsModeCorrectly()
    {
        // Plan-review round 2/3: the re-anchor's _mode is not null gate exists specifically because
        // VisLockStateMachine.Reset() is NOT a no-op pre-lock -- it would discard in-flight VIS
        // header detection state. This proves the gate actually protects a real in-progress header
        // scan: flipping the flag partway through the header must not prevent (or corrupt) detection.
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var transmissionSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            transmissionSamples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(sampleRate, syncRestartEnabled: false);
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        const int chunkSize = 50; // small chunks -- deliberately lands the flip mid-header, not at a convenient boundary
        // VIS headers run under ~300ms even at the extended-VIS worst case -- feed roughly half a
        // second in tiny pieces, flip mid-way, then feed the rest of the transmission normally.
        var halfSecondSamples = sampleRate / 2;
        var flipped = false;
        var offset = 0;
        for (; offset < Math.Min(halfSecondSamples, transmissionSamples.Count); offset += chunkSize)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Count - offset);
            decoder.PushSamples(transmissionSamples.GetRange(offset, length).ToArray());

            // Code-review correction: setting true then false back-to-back with no drain between
            // them coalesces at the CAS latch -- the next drain would only ever see the LAST value
            // (false), so the false->true edge (the one the re-anchor/H1-rebuild logic actually
            // branches on) would never fire, making this test vacuous for its own stated claim. A
            // PushSamples call between the two flips forces each one through its own drain.
            if (!flipped && offset >= halfSecondSamples / 2)
            {
                flipped = true;
                decoder.SyncRestartEnabled = true; // false->true edge
                decoder.PushSamples(new float[8]); // drains it -- still pre-lock, so no re-anchor/Reset() fires
                decoder.SyncRestartEnabled = false; // true->false edge
                decoder.PushSamples(new float[8]); // drains it too
            }
        }

        Assert.True(flipped, "Test setup problem -- never reached the flip point before the pre-lock feed window ended.");
        Assert.Null(detectedMode); // sanity: still pre-lock at this point -- otherwise this test isn't exercising the pre-lock gate at all

        for (; offset < transmissionSamples.Count; offset += 500)
        {
            var length = Math.Min(500, transmissionSamples.Count - offset);
            decoder.PushSamples(transmissionSamples.GetRange(offset, length).ToArray());
            if (detectedMode is not null)
            {
                break;
            }
        }

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
    }

    // AutoStop's own live-toggle divergence test (counter pre-elevated while all three flags are
    // off, then AutoStopEnabled turned on) lives in AutoStopTests.cs instead of here --
    // AutoStopEnabled_LiveEnableAfterCounterPreElevated_TriggersMuchSoonerThanAFreshClimb -- reusing
    // that file's own existing noisy-transmission helpers rather than duplicating them.

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
