using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 3 -- regression tests for the two decode-path <c>sys.m_UseRxBuff</c>
/// gating sites this port previously hardcoded as always-true (harmless only because no
/// <see cref="RxBufferMode"/> toggle existed yet): <c>TryAutoSync</c>'s branch 1/branch 2 conditions
/// (<c>Main.cpp:3907</c>/<c>:3945</c>) and <c>TryResolveSyncAnchorCorrection</c>'s averaging-depth
/// selection (<c>Main.cpp:3760</c>). Neither site depends on the staging buffer or replay mechanism
/// itself (both still unbuilt, RX buffer subsystem Phase 4+) -- these are real decode-behavior changes
/// reachable the moment <see cref="RxBufferMode.Off"/> is selectable, independent of everything else
/// in that subsystem.
/// </summary>
public class RxBufferModeGatingTests
{
    [Fact]
    public void SyncAnchorCorrection_RequiresOneExtraLineOfSamples_WhenRxBufferModeIsOff()
    {
        // Main.cpp:3760's e=3-vs-e=4 selection: PD240's own LineDurationMs is exactly 1000.0ms (the
        // >= 1000.0 boundary), so under RxBufferMode.On/Extended it needs only 3 pages of buffered
        // samples before TryResolveSyncAnchorCorrection succeeds, but 4 under RxBufferMode.Off -- one
        // full extra transmission-line's worth of samples before the first LineDecoded can possibly
        // fire. Chosen because it's a pure integer/threshold effect (WHEN decoding can start), not a
        // DSP-magnitude effect -- deterministic and independent of any splice/bias engineering. Not a
        // coincidental boundary: legacy's own GetTiming(smPD240) is an exact literal 1000.00
        // (sstv.cpp:1221-1222), so legacy itself takes e=3 at this exact mode -- this port's own
        // LineDurationMs (SstvModeDefinition.cs's LineSegments.Sum, 20.0+2.08+4*244.48) must therefore
        // round to >= 1000.0 for this test's own premise to hold, which it does (this test would fail
        // loudly, not pass vacuously, otherwise).
        var mode = SstvModeRegistry.Pd240;
        const int sampleRate = 11025;
        var pageWidthSamples = (int)(mode.LineDurationMs / 1000.0 * sampleRate);
        // Header + porch + enough pages for either arm, plus slack for VIS/anchor search overhead.
        var maxSamples = 6 * pageWidthSamples + 20000;
        var samples = EncodePartialTransmission(mode, sampleRate, maxSamples);

        var onOffset = FirstLineDecodedOffset(samples, new AnalogFmSstvDecoder(sampleRate, rxBufferMode: RxBufferMode.On));
        var offOffset = FirstLineDecodedOffset(samples, new AnalogFmSstvDecoder(sampleRate, rxBufferMode: RxBufferMode.Off));
        var extendedOffset = FirstLineDecodedOffset(samples, new AnalogFmSstvDecoder(sampleRate, rxBufferMode: RxBufferMode.Extended));

        Assert.True(onOffset.HasValue, "RxBufferMode.On decoder never decoded a line -- test setup problem (increase maxSamples).");
        Assert.True(offOffset.HasValue, "RxBufferMode.Off decoder never decoded a line -- test setup problem (increase maxSamples).");
        Assert.True(extendedOffset.HasValue, "RxBufferMode.Extended decoder never decoded a line -- test setup problem.");

        // Off must need strictly more samples than On (4 pages vs 3), by roughly one page's worth --
        // some slack for the anchor-correction fold's own commit-point shift between a 3-page and a
        // 4-page search.
        Assert.True(offOffset > onOffset,
            $"Expected RxBufferMode.Off to require MORE samples before the first line decodes than On (On={onOffset}, Off={offOffset}).");
        var delta = offOffset!.Value - onOffset!.Value;
        Assert.InRange(delta, pageWidthSamples / 2, pageWidthSamples * 2);

        // Extended is "buffer present" for this gating purpose too (never == On specifically) --
        // must match On exactly, not fall back to Off's 4-page requirement.
        Assert.Equal(onOffset, extendedOffset);
    }

    [Fact]
    public void TryAutoSync_Branch2_UnlocksOnTheFirstAnomalyOfAnImage_OnlyWhenRxBufferModeIsOff()
    {
        // Main.cpp:3945's `(m_AutoSyncCount || !sys.m_UseRxBuff)` term: branch 2 (the absolute-
        // position-vs-zero test) can normally only fire AFTER a prior correction has already committed
        // this image (_slantCorrectionsDisabledForRestOfImage). RxBufferMode.Off additionally unlocks
        // it from the very first anomaly, with no prior correction required -- independent of branch 1,
        // which Main.cpp:3907's own trailing `&& sys.m_UseRxBuff` term makes UNREACHABLE at all once
        // Off is selected.
        //
        // A sustained small-magnitude sample-rate mismatch (not a sudden splice) is used deliberately:
        // AutoSyncTests.cs's own SuddenPositionJump test already established that a genuine JUMP
        // reliably fires Auto Sync (via branch 1, since it's the first anomaly of a clean image) --
        // exactly the scenario this fix makes unreachable under Off, so trigger COUNT alone can't
        // distinguish "branch 1 correctly blocked" from "the fix did nothing" (branch 2 could
        // independently fire on the very same jump once unlocked). A small, CONSTANT declared-vs-true
        // sample-rate mismatch instead isolates branch 2 specifically: empirically confirmed (swept
        // 1.002-1.01, every point behaves identically) that the resulting position reading climbs
        // smoothly rather than jumping, so it never satisfies branch 1's own previous-vs-current "real
        // jump" test (On: 0 triggers, every ratio tested) -- but DOES eventually clear branch 2's
        // absolute-position-vs-zero threshold, which only Off can reach without a prior correction
        // (Off: >=1 triggers, every ratio tested). 1.005/30 lines is comfortably inside the confirmed
        // range, not a boundary value.
        var mode = SstvModeRegistry.Robot36;
        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.005);
        var samples = EncodeRealTransmissionAtRate(mode, trueSampleRate);

        var onDecoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);
        var offDecoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.Off);

        const int chunkSize = 256;
        var onLineCount = 0;
        var offLineCount = 0;
        onDecoder.LineDecoded += _ => onLineCount++;
        offDecoder.LineDecoded += _ => offLineCount++;
        // Round-1 auditor finding: this was `&&`, which exits the loop (and stops pushing samples to
        // BOTH decoders) as soon as EITHER counter reaches 30 -- if a mid-stream correction ever makes
        // one decoder fall behind the other (e.g. a skip applied by TriggerAutoSync), the lagging
        // decoder silently gets fewer than 30 lines pushed, and the `>= 30` assertion below would only
        // catch it if it fell behind badly enough to still be under 30 -- a latent flake, not silent on
        // that specific case, but fragile. `||` runs until BOTH have reached 30 (or input exhausts).
        for (var offset = 0; offset < samples.Length && (onLineCount < 30 || offLineCount < 30); offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            onDecoder.PushSamples(samples.AsMemory(offset, length));
            offDecoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(onLineCount >= 30 && offLineCount >= 30, "Never decoded enough lines -- test setup problem.");
        // On: branch 1 is the only reachable trigger this early (no prior correction yet), and the
        // smoothly-climbing position never satisfies its previous-vs-current "real jump" test (per
        // AutoSyncTests.cs's own PerfectlySyncedSignal reasoning) -- so On shows 0 triggers here.
        Assert.Equal(0, onDecoder.AutoSyncTriggerCountForTests);
        // Off: branch 2 is additionally reachable from the very first anomaly (no prior correction
        // required), and catches the same climbing position via its absolute-position-vs-zero test.
        Assert.True(offDecoder.AutoSyncTriggerCountForTests > 0,
            "Expected RxBufferMode.Off to unlock branch 2 and trigger on a climbing position On never triggers on.");
    }

    [Fact]
    public void TryAutoSync_Branch2_UnlocksOnASmallSplice_ExtendedMatchesOnNotOff()
    {
        // Round-2 auditor correction of an earlier version of this test/comment, which WRONGLY claimed
        // to isolate branch 1's own gate (Main.cpp:3907's trailing `&& sys.m_UseRxBuff`,
        // AnalogFmSstvDecoder.cs's `_rxBufferMode != RxBufferMode.Off` term). Re-derived: at this
        // splice size, On's 0-trigger result is NOT caused by branch 1's own 25-sample threshold
        // (LastBranch1ThresholdForTests) failing -- it's caused by CountAutoSyncCluster's own cluster
        // threshold (14*mult=70 once >=16 observations exist, Main.cpp:3892-3899) staying satisfied
        // (n>=4) for a 50-sample jump, which routes execution into the `else` arm (Main.cpp:3941,
        // AnalogFmSstvDecoder.cs's own n>=4 branch) instead of ever reaching branch 1's own `if(n<4)`
        // body at all -- branch 1 is never EVALUATED here, not evaluated-and-failed. (AutoSyncTests.cs's
        // own SuddenPositionJump test already documents this exact mechanism: "a 60-sample splice
        // measured well under that threshold and never triggered.")
        //
        // Round-3 auditor correction: no test in this suite isolates branch 1's own `!= Off` term
        // (Main.cpp:3907), and an earlier version of this comment overclaimed that no such test could
        // exist -- that's not established either. Branch 2's own step test (`|cur-prev| <=
        // _autoSyncDiff=15`) is STRICTER than branch 1's (`<= branch1Threshold=25`), not looser -- only
        // branch 2's magnitude test (`|cur| >= 15` vs branch 1's `|cur-ref| >= 25`) is looser. So there
        // is an on-paper window -- sustained per-line drift in (3*mult, 5*mult] = (15, 25] samples/line
        // for this mode/rate -- where branch 2's own step test fails every line (drift too big) while
        // branch 1's own step/jump pair could still pass, IF the other preconditions (n landing in
        // [2,4), a stable reference, _autoSyncCooldown==0) happen to line up -- not yet confirmed to
        // occur on a real encoded signal. Branch 1's own `!= Off` term is verified today by direct
        // source correspondence against Main.cpp:3907 (confirmed independently across three auditor
        // rounds) -- documented here so a future reader knows the coverage gap is real and where the
        // plausible (but unconfirmed) test signal would have to land, not that it's been proven
        // impossible.
        //
        // What THIS test actually demonstrates: branch 2's unlock (Main.cpp:3945) reproduces on a
        // splice-shaped signal too (not just the sample-rate-mismatch signal the test above uses), and
        // Extended matches On (not Off) at this same site -- a second, differently-shaped confirmation
        // of the branch-2 test above, plus this phase's only Extended-vs-On check for the TryAutoSync
        // sites specifically (the test above only checks On vs Off).
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmissionAtRate(mode, 11025);
        var spliceIndex = samples.Length * 15 / 100;
        const int spliceSamples = 50;
        var spliced = new float[samples.Length + spliceSamples];
        Array.Copy(samples, 0, spliced, 0, spliceIndex);
        Array.Copy(samples, spliceIndex, spliced, spliceIndex + spliceSamples, samples.Length - spliceIndex);

        var onDecoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);
        var offDecoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.Off);
        var extendedDecoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.Extended);

        onDecoder.PushSamples(spliced);
        offDecoder.PushSamples(spliced);
        extendedDecoder.PushSamples(spliced);

        // On: the jump stays inside the cluster threshold (n>=4), so neither branch is reachable here.
        Assert.Equal(0, onDecoder.AutoSyncTriggerCountForTests);
        // Off: branch 2 is unlocked from the first anomaly and catches this same splice via its own
        // smaller absolute-position threshold.
        Assert.True(offDecoder.AutoSyncTriggerCountForTests > 0,
            "Expected RxBufferMode.Off to trigger via branch 2 on a splice On never triggers on.");
        // Extended is "buffer present" for this gating purpose too (never == On specifically) -- must
        // match On's 0-trigger result, not fall back to Off's unlocked behavior.
        Assert.Equal(0, extendedDecoder.AutoSyncTriggerCountForTests);
    }

    private static float[] EncodeRealTransmissionAtRate(SstvModeDefinition mode, int encodeSampleRate)
    {
        var width = mode.ImageWidth;
        var height = mode.ImageHeight;
        var pixels = new ScanlineStudio.Abstractions.Imaging.Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new ScanlineStudio.Abstractions.Imaging.Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        var image = new ScanlineStudio.Core.Imaging.ArrayImageSource(width, height, pixels);
        var encoder = new AnalogFmSstvEncoder(encodeSampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, image).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }

    /// <summary>Consumes only up to <paramref name="maxSamples"/> from the encoder's lazy async
    /// iterator, rather than draining a full (potentially multi-hundred-second) transmission --
    /// PD240's own full image would otherwise take ~250s of synthesized audio for a test that only
    /// needs the first handful of transmission lines.</summary>
    private static float[] EncodePartialTransmission(SstvModeDefinition mode, int sampleRate, int maxSamples)
    {
        var width = mode.ImageWidth;
        var height = mode.ImageHeight;
        var pixels = new ScanlineStudio.Abstractions.Imaging.Rgb24[width * height];
        Array.Fill(pixels, new ScanlineStudio.Abstractions.Imaging.Rgb24(180, 90, 40));
        var image = new ScanlineStudio.Core.Imaging.ArrayImageSource(width, height, pixels);

        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>(maxSamples);
        var enumerator = encoder.EncodeAsync(mode, image).GetAsyncEnumerator();
        try
        {
            while (samples.Count < maxSamples && enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }

    /// <summary>Pushes <paramref name="samples"/> into <paramref name="decoder"/> in small chunks,
    /// returning the total sample offset (as of the START of the chunk whose <c>PushSamples</c> call
    /// triggered it -- <c>totalPushed</c> is incremented AFTER the push, so the event fires mid-push
    /// against the pre-increment value) at which <see cref="ISstvDecoder.LineDecoded"/> first fires, or
    /// <see langword="null"/> if it never does before the input is exhausted. Both decoders under
    /// comparison are measured identically (same chunk size, same quantization), so this doesn't affect
    /// either assertion's validity.</summary>
    private static int? FirstLineDecodedOffset(float[] samples, AnalogFmSstvDecoder decoder)
    {
        const int chunkSize = 256;
        int? firstOffset = null;
        var totalPushed = 0;
        decoder.LineDecoded += _ => firstOffset ??= totalPushed;
        for (var offset = 0; offset < samples.Length && firstOffset is null; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            totalPushed += length;
        }

        return firstOffset;
    }
}
