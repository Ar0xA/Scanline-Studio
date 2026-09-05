using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 6c -- tests for <c>AnalogFmSstvDecoder.PerformReplay</c>, exercised via
/// <c>PerformReplayForTests</c> (a controlled, single-call entry point) with automatic replay
/// suppressed (<c>SuppressAutomaticReplayForTests</c>) unless a test explicitly wants the reverse.
/// Phase 6d's own automatic-trigger tests (does the commit trigger fire, does the once-per-image latch
/// fire and respect its own gates) live in the "automatic trigger" region near the bottom of this file.
/// </summary>
public class ReplayEngineTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void PerformReplay_NoModeLockedYet_IsASafeNoOp()
    {
        // Guard-clause coverage: _mode/_lineDecoder/_pixels/_slantTracker are all still null before
        // any lock -- must not throw.
        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On);
        decoder.PerformReplayForTests();
    }

    [Fact]
    public void PerformReplay_RxBufferModeOff_IsASafeNoOp_NoStagingBufferExists()
    {
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.Off);
        decoder.PushSamples(samples.AsMemory(0, samples.Length / 4));

        Assert.NotNull(decoder.ModeForTests); // sanity: actually locked before replay is attempted
        decoder.PerformReplayForTests(); // _rxLineStagingBuffer is null under Off -- must not throw
    }

    [Fact]
    public void PerformReplay_AfterPartialDecode_FiresLineDecodedForAlreadyDecodedRows_WithoutThrowing()
    {
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On);
        var liveLineCount = 0;
        decoder.LineDecoded += _ => liveLineCount++;

        // Push only enough for a handful of lines, not the whole image -- replay should still be able
        // to redraw whatever's been staged so far. Round-1 code-review fix: 12, not 20 -- once Phase 6d
        // wired the automatic once-per-image latch (fires unconditionally once 16 transmission lines
        // decode), staying below that threshold keeps this test's own manual PerformReplayForTests()
        // call the ONLY replay pass that runs, matching what the test actually wants to exercise.
        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && liveLineCount < 12)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(liveLineCount >= 12, "Test setup problem: never decoded 12 lines live before attempting replay.");
        Assert.True(decoder.RxLineStagingBufferForTests!.LineCount > 0, "Test setup problem: nothing staged yet.");

        var replayLineCount = 0;
        decoder.LineDecoded += _ => replayLineCount++;
        decoder.PerformReplayForTests();

        Assert.True(replayLineCount > 0, "PerformReplay never fired LineDecoded -- expected it to redraw at least the rows already staged.");
    }

    [Fact]
    public void PerformReplay_ThrowingSubscriber_IsDeferredUntilReplayTailAndLiveDecodeCanResume()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = Encode(mode, CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight), SampleRate);
        using var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On);
        decoder.SuppressAutomaticReplayForTests = true;

        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && decoder.NextLineForTests < 12)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(decoder.NextLineForTests >= 12, "Test setup problem: insufficient live decode before replay.");
        var stagedCountBeforeReplay = decoder.RxLineStagingBufferForTests!.Count;
        Assert.True(stagedCountBeforeReplay > 0);
        var replayPassCountBefore = decoder.ReplayPassCountForTests;
        var expected = new InvalidOperationException("Injected replay subscriber failure.");
        var replayRows = new List<int>();
        Action<DecodedImageUpdate> throwingHandler = _ => throw expected;
        decoder.LineDecoded += throwingHandler;
        decoder.LineDecoded += update => replayRows.Add(update.Line);

        var actual = Assert.Throws<InvalidOperationException>(decoder.PerformReplayForTests);

        Assert.Same(expected, actual);
        Assert.NotEmpty(replayRows);
        // Buffered-replay fix (§15 item 2): the staging buffer is never truncated anymore, so it stays
        // exactly as it was -- swapped from the old "must be cleared" assertion to "must be unchanged."
        Assert.Equal(stagedCountBeforeReplay, decoder.RxLineStagingBufferForTests.Count);
        Assert.True(decoder.ReplayPassCountForTests > replayPassCountBefore,
            "Replay must reach its cursor-reconciliation tail (which increments ReplayPassCountForTests) before surfacing the subscriber failure -- ExecuteWithDeferredSubscriberFailures defers the throw until after PerformReplay fully returns.");

        decoder.LineDecoded -= throwingHandler;
        var nextLineAfterReplay = decoder.NextLineForTests;
        decoder.PushSamples(samples.AsMemory(offset, Math.Min(chunkSize, samples.Length - offset)));
        Assert.True(decoder.NextLineForTests >= nextLineAfterReplay);
    }

    [Fact]
    public void PerformReplay_HasWriteFailed_IsASafeNoOp_NeverRedrawsFromCorruptedData()
    {
        // spec/18-path-to-1.0.md High item 6. PerformReplay has two write-failure checkpoints (see
        // its own doc comments): an entry guard, and a second one immediately after ComputeOrigin
        // returns (the pass's own first staging-buffer read -- the last point a NEW latch can surface
        // before the redraw loop runs, whether from that read itself or from the background writer
        // task latching HasWriteFailed independently, which can happen at any moment -- the flag is
        // volatile precisely because of that). This test exercises checkpoint 1 deterministically
        // (the failure is already latched before PerformReplayForTests is even called). Checkpoint 2
        // is not independently exercised by a dedicated test: doing so needs HasWriteFailed to read
        // false at checkpoint 1 and then true a few statements later at checkpoint 2, but there is no
        // test hook to force that exact interleaving deterministically (the buffer field this method
        // reads is decoder-private, no injection seam exists) -- see
        // /home/artien/.claude/plans/rx-buffer-write-failure-guard.md for why that gap is accepted,
        // not chased, rather than attempting a flaky test.
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        using var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.Extended);

        // Isolates the manual PerformReplayForTests() call below from any AUTOMATIC replay pass,
        // which would Clear() the staging buffer first -- making Count==0 (the pre-existing early
        // return) the reason for a no-op instead of the new guard under test here.
        decoder.SuppressAutomaticReplayForTests = true;

        var liveLineCount = 0;
        decoder.LineDecoded += _ => liveLineCount++;
        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && liveLineCount < 12)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(liveLineCount >= 12, "Test setup problem: never decoded 12 lines live before attempting replay.");
        var staging = (RxDiskLineStagingBuffer)decoder.RxLineStagingBufferForTests!;
        Assert.True(staging.Count > 0 && staging.LineCount > 0, "Test setup problem: nothing staged yet.");

        staging.CorruptWriteStreamForTests();

        // Deliberately not asserted either way -- same established race as
        // CorrectSlantTests.cs's sibling test; only staged so something is guaranteed to hit the
        // corrupted stream once the background consumer catches up.
        _ = staging.TryAppendLine([0.0], [0.0]);

        _ = staging.DemodulatedAt(0); // forces a drain -- guarantees HasWriteFailed is observed
        Assert.True(staging.HasWriteFailed, "Test setup problem: write failure never latched.");

        var replayLineCount = 0;
        decoder.LineDecoded += _ => replayLineCount++;
        decoder.PerformReplayForTests();

        Assert.Equal(0, replayLineCount);
    }

    [Theory]
    [InlineData(RxBufferMode.On)]
    [InlineData(RxBufferMode.Extended)]
    public void PerformReplay_AfterARealCorrectionCommits_LiveDecodeContinuesRowsWithoutGapOrRepeat(RxBufferMode rxBufferMode)
    {
        // Round-1 code-review strengthening: the original version of this test used a CLEAN (no clock
        // mismatch) signal, under which SlantTracker.ProcessLine never commits and
        // _effectiveSamplesPerLine never moves -- the auditor found this meant the whole save/restore
        // mechanism (and the _idealLineStartSample re-anchoring fix) could be deleted and this test
        // would still pass, since nothing ever actually exercised a real stride change. Reuses
        // SlantTests.cs's own proven-reliable 500ppm mismatch scenario (declaredSampleRate=44100,
        // trueSampleRate=44100*1.0005), which that file's own tests already confirm reliably commits a
        // correction during the decode -- so by the time replay fires here, _effectiveSamplesPerLine
        // genuinely differs from what it was when the earlier rows were drawn, making both the
        // accumulator save/restore AND the _idealLineStartSample re-anchor load-bearing, not inert.
        //
        // The independent check (not a re-derivation of the production reconciliation formula, per the
        // auditor's own "vacuous" finding on the test this replaces): capture the LAST row index
        // observed DURING the replay call itself, then assert the FIRST row index observed once live
        // decode resumes afterward continues EXACTLY one RowsPerTransmissionLine step forward -- no
        // gap (a wrong/too-small NextLine), no repeat (a wrong/too-large re-anchor), no backward jump.
        //
        // Parameterized over RxBufferMode (RX buffer subsystem Phase 7, sub-piece 7e): Extended's
        // disk-backed staging buffer must produce equivalent replay row-continuity and image quality
        // to RAM's -- this is this whole phase's actual payoff (Extended gets REAL replay too, not
        // just capture), and nothing about the reconciliation math above is RAM-specific, so the same
        // test body proves it for both backends via IRxLineStagingBuffer's shared contract.
        // Code-review correction: NOT literally "byte-identical" (an earlier version of this comment
        // claimed that) -- Extended deliberately disables peak-picking for every mode, in both the
        // live path and PerformReplay itself (AnalogFmSstvDecoder's own `_rxBufferMode ==
        // RxBufferMode.Extended` checks), a real, pinned divergence (RxBufferCaptureTests.cs's own
        // On-vs-Extended pixel-difference test) -- this test's own delta tolerance already accounts
        // for that, it just never compares On's output against Extended's directly.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        using var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: rxBufferMode);
        IImageSource? decodedImage = null;
        var replaying = false;
        var lastRowDuringReplay = -1;
        int? firstRowAfterReplay = null;
        decoder.LineDecoded += update =>
        {
            decodedImage = update.Image;
            if (replaying)
            {
                lastRowDuringReplay = update.Line;
            }
            else if (lastRowDuringReplay >= 0 && firstRowAfterReplay is null)
            {
                firstRowAfterReplay = update.Line;
            }
        };

        const int chunkSize = 256;
        var offset = 0;
        var replayed = false;
        while (offset < samples.Length)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;

            // Fire exactly one replay pass, once the tracker has actually committed a correction (not
            // just "once enough lines are staged") -- see this test's own doc comment on why a clean
            // signal doesn't exercise the thing under test.
            if (!replayed && decoder.SlantTrackerForTests?.DriftPpm is not (null or 0.0)
                && decoder.RxLineStagingBufferForTests is { LineCount: > 5 })
            {
                replaying = true;
                decoder.PerformReplayForTests();
                replaying = false;
                replayed = true;
            }
        }

        Assert.True(replayed, "Test setup problem: never observed a committed correction with staged lines available -- replay was never actually exercised against a real stride change.");
        Assert.True(lastRowDuringReplay >= 0, "Test setup problem: PerformReplay never fired LineDecoded.");
        // Code-review finding: Extended can silently stop capturing (a full channel or a drain
        // timeout both latch HasWriteFailed without throwing -- see RxDiskLineStagingBuffer's own
        // contract) while LineCount stays frozen but still > 5, so the gate above would still fire
        // the replay pass over a SHORTER staged extent than intended -- and neither the row-
        // continuity nor the delta assertion below would necessarily catch that, since both are
        // driven by decode-path bookkeeping, not by how much data actually made it to disk. Without
        // this assertion, Extended's own half of this Theory could silently prove less than it
        // claims to. Always false for On (RxLineStagingBuffer.HasWriteFailed is a hardcoded no-op).
        Assert.False(decoder.RxLineStagingBufferForTests!.HasWriteFailed, "Staging buffer silently stopped capturing -- this test would then prove nothing about replay over the real intended staged extent.");
        Assert.NotNull(firstRowAfterReplay);
        // Buffered-replay fix (§15 item 2): PerformReplay's sample-cursor re-anchor now snaps BACKWARD
        // to the corrected row start instead of jumping FORWARD past it, so no row is ever sacrificed --
        // the expected next row is a seamless continuation, exactly one transmission line
        // (RowsPerTransmissionLine == 1 for Robot36) after the last row this pass redrew, matching
        // legacy's own zero-gap continuity exactly.
        Assert.Equal(lastRowDuringReplay + 1, firstRowAfterReplay!.Value);

        Assert.NotNull(decodedImage);
        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);
        Assert.True(averageDelta <= 10.0, $"Average per-channel delta {averageDelta:F2} exceeded normal round-trip tolerance -- a mid-stream PerformReplay call across a real correction should still leave the live decode that continues after it self-consistent.");
    }

    [Fact]
    public void PerformReplay_CalledTwice_SecondPassRedrawsFromRowZeroAndStillMatchesSource()
    {
        // Buffered-replay fix (§15 item 2, robust-giggling-codd.md): this used to regression-test the
        // OLD forward-jump-then-truncate design's own buffer-splice bug (a second pass reading straight
        // across a truncation-created gap as if it were continuous audio). That design, and the bug
        // class it created, are both gone now -- Step B never truncates the staging buffer, so every
        // pass's redraw loop covers the WHOLE staged reception from row 0, not just what's staged since
        // the previous pass. This test now proves the actual FEATURE that removing truncation delivers:
        // pass 2 re-touches rows pass 1 already drew and they still match source -- structurally
        // impossible to prove this way under the old design, since pass 1's own raw audio was gone from
        // the buffer (truncated) by the time pass 2 ran.
        //
        // Also pins the batching change (§15 item 2 scope item 5): LineDecoded now fires exactly ONCE
        // per pass, carrying the LAST redrawn row -- this test's own `lastRowThisPass` capture relies on
        // that (it used to collect a whole HashSet of per-row events during a pass; batching makes that
        // collection always a single element now, so this test reads the image directly instead).
        //
        // A per-row solid, widely-separated color (NOT the smooth gradient other tests in this file
        // use) is deliberately used -- a slow gradient is a "useless discriminator" here, since a
        // one-row misassignment falls below its own row-to-row delta. R24
        // (YCbCrSequentialScanlineDecoder), not Robot36: Robot36 (RobotScanlineDecoder, the only decoder
        // with cross-line instance state -- caches the previous line's OTHER chroma channel across
        // DecodeLine calls, matching legacy's own m_D36[2][320] state) has real, by-design vertical
        // chroma subsampling that produces large per-row error against exactly this kind of image
        // REGARDLESS of replay (see PerformReplay's own doc comment for the closed investigation) -- an
        // invalid fidelity target for this specific per-row check.
        var mode = SstvModeRegistry.R24;
        var sourceImage = CreateRowIdentityTestImage(mode.ImageWidth, mode.ImageHeight);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On)
        {
            // This test drives exactly two replay passes, at moments IT controls -- an automatic replay
            // pass firing unpredictably during the same PushSamples calls (via the once-per-image latch
            // or a real Auto-Slant commit, both reachable under this test's own real 500ppm mismatch)
            // would interleave a THIRD, uncontrolled pass and corrupt this test's own bookkeeping.
            SuppressAutomaticReplayForTests = true,
        };
        IImageSource? decodedImage = null;
        var lastRowThisPass = -1;
        decoder.LineDecoded += update =>
        {
            decodedImage = update.Image;
            lastRowThisPass = update.Line;
        };

        var offset = 0;
        while (offset < samples.Length && (decoder.RxLineStagingBufferForTests?.LineCount ?? 0) <= 5)
        {
            var length = Math.Min(256, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True((decoder.RxLineStagingBufferForTests?.LineCount ?? 0) > 5, "Test setup problem: staging buffer never reached >5 lines before the first pass.");

        lastRowThisPass = -1;
        decoder.PerformReplayForTests();
        Assert.True(lastRowThisPass >= 0, "Test setup problem: pass 1 never fired LineDecoded.");
        var lastRowPass1 = lastRowThisPass;
        // Zero-gap guarantee (this fix's own core property): live decode resumes exactly one
        // transmission line after the last row this pass redrew (R24: RowsPerTransmissionLine == 1).
        Assert.Equal(lastRowPass1 + 1, decoder.NextLineForTests);

        var lineCountAfterPass1 = decoder.RxLineStagingBufferForTests!.LineCount;
        while (offset < samples.Length && decoder.RxLineStagingBufferForTests!.LineCount <= lineCountAfterPass1 + 5)
        {
            var length = Math.Min(256, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(decoder.RxLineStagingBufferForTests!.LineCount > lineCountAfterPass1 + 5, "Test setup problem: staging buffer never grew past pass 1's own extent before pass 2.");

        lastRowThisPass = -1;
        decoder.PerformReplayForTests();
        Assert.True(lastRowThisPass >= 0, "Test setup problem: pass 2 never fired LineDecoded.");
        var lastRowPass2 = lastRowThisPass;

        Assert.True(lastRowPass2 > lastRowPass1, "Test setup problem: pass 2 didn't redraw further than pass 1 -- staging buffer growth between passes was insufficient.");
        Assert.Equal(lastRowPass2 + 1, decoder.NextLineForTests);

        // The actual point of Step B: pass 2's own redraw loop covers the WHOLE staged reception from
        // row 0, including every row pass 1 already drew -- checked directly against source here.
        Assert.NotNull(decodedImage);
        for (var y = 0; y <= lastRowPass2; y++)
        {
            var delta = RowDelta(decodedImage!.GetScanline(y), sourceImage.GetScanline(y));
            Assert.True(delta <= 24.0, $"Row {y} delta {delta:F2} vs source exceeded tolerance after pass 2's own full-reception redraw.");
        }
    }

    [Fact]
    public void PerformReplay_PairedChannelMode_NextLineReconciliationAppliesRowsPerTransmissionLine()
    {
        // Round-1 code-review gap fix: every other test in this file uses Robot36
        // (RowsPerTransmissionLine == 1), so the unit-conversion multiply the round-2 plan-review flagged
        // (RX buffer plan's own `m_AY` note) was never actually exercised by anything in this file.
        // Mp73 is YCbCrLinePairedScanlineDecoder-backed (RowsPerTransmissionLine == 2) -- same
        // independent-observation technique as the sibling test above (continuity across the replay
        // boundary), which additionally pins the multiply: a dropped or double-applied
        // RowsPerTransmissionLine factor would make the post-replay row jump by 1 or 4 instead of the
        // correct 2.
        var mode = SstvModeRegistry.Mp73;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On);
        var replaying = false;
        var lastRowDuringReplay = -1;
        int? firstRowAfterReplay = null;
        decoder.LineDecoded += update =>
        {
            if (replaying)
            {
                lastRowDuringReplay = update.Line;
            }
            else if (lastRowDuringReplay >= 0 && firstRowAfterReplay is null)
            {
                firstRowAfterReplay = update.Line;
            }
        };

        const int chunkSize = 256;
        var offset = 0;
        var replayed = false;
        while (offset < samples.Length)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;

            if (!replayed && decoder.RxLineStagingBufferForTests is { LineCount: > 10 })
            {
                replaying = true;
                decoder.PerformReplayForTests();
                replaying = false;
                replayed = true;
            }

            if (replayed && firstRowAfterReplay is not null)
            {
                break;
            }
        }

        Assert.True(replayed, "Test setup problem: staging buffer never reached >10 lines.");
        Assert.True(lastRowDuringReplay >= 0, "Test setup problem: PerformReplay never fired LineDecoded.");
        Assert.NotNull(firstRowAfterReplay);
        Assert.Equal(0, lastRowDuringReplay % 2); // every replayed row index is a bitmap-row start for a paired mode
        // Buffered-replay fix (§15 item 2): same zero-gap reasoning as the sibling Robot36 test above,
        // scaled by RowsPerTransmissionLine == 2 (one transmission line == 2 bitmap rows) -- the exact
        // expected gap is +2, a seamless continuation, not a sacrificed-row +4.
        Assert.Equal(lastRowDuringReplay + 2, firstRowAfterReplay!.Value);
    }

    [Fact]
    public void PerformReplay_PositiveOrigin_ScottieFamilyDoesNotThrow()
    {
        // Round-1 code-review blocker regression test: ReplayOriginCalculator.AdjustPosition's own
        // Scottie-family wrap (`if(n<0) n += correctedLineWidthSamples`) forces a POSITIVE origin by
        // construction whenever the raw (pre-wrap) value is negative -- unlike every other mode family,
        // which can freely return a negative origin. A positive origin makes row 0's own
        // `lineStartStaged = 0 - origin` negative, which RxLineStagingBuffer.DemodulatedAt/SyncEnvelopeAt
        // (bare List<double> indexers) threw on before this blocker's fix. A clock mismatch is used so a
        // real correction (and therefore a real, non-degenerate origin computation) occurs before replay
        // runs, rather than relying on incidental noise in a clean signal to land the histogram argmax on
        // a value that happens to trigger the wrap.
        //
        // Round-2 code-review finding: DemodType.Hilbert (this decoder's own default) subtracts a
        // further `HalfTap/4` group-delay correction (ReplayOriginCalculator.ComputeOrigin's own Hilbert
        // branch) from AdjustPosition's already-non-negative Scottie result -- since that raw result
        // sits near zero by construction (the histogram argmax is meant to land near OFP), the
        // correction can push it back negative, making the origin's final sign under Hilbert a coin
        // flip this test can't control. DemodType.Pll is used instead specifically so
        // ReplayOriginCalculator.ComputeOrigin's own `if (demodType == DemodType.Hilbert)` branch never
        // fires, leaving AdjustPosition's own guaranteed-non-negative Scottie wrap as the final value --
        // and the explicit assertion below on LastReplayOriginForTests confirms this reasoning actually
        // held for this run, not just in theory.
        var mode = SstvModeRegistry.ScottieS1;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, demodType: DemodType.Pll, rxBufferMode: RxBufferMode.On);
        var replayLineCount = 0;
        var replaying = false;
        decoder.LineDecoded += _ =>
        {
            if (replaying)
            {
                replayLineCount++;
            }
        };

        const int chunkSize = 256;
        var offset = 0;
        var replayed = false;
        while (offset < samples.Length && !replayed)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;

            if (decoder.RxLineStagingBufferForTests is { LineCount: > 10 })
            {
                replaying = true;
                decoder.PerformReplayForTests(); // must not throw regardless of the sign ReplayOriginCalculator computed
                replaying = false;
                replayed = true;
            }
        }

        Assert.True(replayed, "Test setup problem: staging buffer never reached >10 lines.");
        Assert.True(replayLineCount > 0, "PerformReplay never fired LineDecoded -- doesn't actually prove replay drew anything (round-3 code-review nit: a bare live-line-count check couldn't distinguish this from replay silently doing nothing).");
        // Round-3 code-review nit: AdjustPosition's Scottie wrap only guarantees n >= 0 (argmax==(int)ofp
        // gives exactly 0), not n > 0 -- >= 0 is the real, non-flaky guarantee this test can assert.
        Assert.True(decoder.LastReplayOriginForTests >= 0, $"Test setup problem: origin was {decoder.LastReplayOriginForTests}, negative -- this test doesn't actually exercise the Scottie-wrap path it's named for.");
    }

    // ------------------------------------------------------------------------------------------------
    // RX buffer subsystem Phase 6d -- automatic trigger tests. Every test above this point suppresses
    // the automatic path (SuppressAutomaticReplayForTests) or predates Phase 6d entirely; per the
    // auditor's own round-1 finding, NOTHING previously exercised the automatic commit trigger or the
    // once-per-image latch actually firing on their own. These tests deliberately do NOT suppress it.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void PerformReplay_AutomaticCommitTrigger_FiresWithNoManualCallAnywhere()
    {
        // Proves the automatic commit trigger (ProcessSlantTrackingSample's own commit branch) actually
        // fires PerformReplay, with NO PerformReplayForTests() call anywhere in this test.
        // Buffered-replay fix (§15 item 2): _rxBufferBaseTransmissionLine now stays 0 forever (the
        // staging buffer is never truncated/re-based), so it can no longer serve as this test's oracle --
        // swapped to ReplayPassCountForTests, which counts a genuinely completed PerformReplay pass
        // regardless of the (now fixed) base transmission line.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);
        decoder.PushSamples(samples);

        Assert.True(decoder.ReplayPassCountForTests > 0, "The automatic commit trigger never fired a replay pass.");
    }

    [Fact]
    public void PerformReplay_AutomaticOnceLatch_NeverFiresWithoutARealCorrection()
    {
        // Round-2 code-review regression test for the explicit user decision (2026-08-13) narrowing the
        // once-per-image latch: it must NOT fire when no Auto-Slant correction has ever committed this
        // image, even past the 16-transmission-line threshold. A clean (matched-rate) signal never
        // commits a correction, and Robot36's own 240 lines are comfortably past 16 -- no replay pass
        // should ever run for the WHOLE image. Buffered-replay fix (§15 item 2): swapped from the old
        // "_rxBufferBaseTransmissionLine stays 0" oracle (now always true regardless of whether replay
        // ran, since the buffer is never truncated/re-based) to ReplayPassCountForTests.
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On);
        decoder.PushSamples(samples);

        Assert.Equal(0, decoder.ReplayPassCountForTests);
    }

    [Fact]
    public void PerformReplay_AutomaticOnceLatch_NeverFiresAcrossAManualReSyncHole()
    {
        // Round-2 code-review regression test for the auditor's own round-1 blocker: a manual ReSync
        // (or an Auto-Sync trigger) leaves a mid-buffer HOLE in RxLineStagingBuffer (DrainPendingSkip's
        // own skipped samples advance the live cursor without ever being staged) and sets
        // _slantCorrectionsDisabledForRestOfImage -- the once-per-image latch must respect that flag
        // too, or it would fire replay straight across the hole. Uses a real clock mismatch so a
        // correction commits FIRST (satisfying the OTHER gate this round added,
        // _anyCorrectionCommittedThisImage) -- proving this is genuinely the ReSync-hole gate holding
        // the latch back, not just the "no commit yet" gate from the sibling test above.
        //
        // Round-2 code-review correction (real bug in the test, not the production code): an earlier
        // version of this test set SuppressAutomaticReplayForTests = true, which made the FINAL
        // assertion unconditionally true regardless of whether the latch's own new gate worked at all --
        // the suppression isolated this test from the very thing it exists to prove. Automatic replay now
        // runs UNSUPPRESSED for this whole test; the discriminator is instead "the pass count stops
        // moving once the hole exists," not "it never moves at all" (the commit trigger legitimately
        // replays at least once, BEFORE the ReSync, which is expected and asserted below, not suppressed).
        // Buffered-replay fix (§15 item 2): swapped from the old "_rxBufferBaseTransmissionLine" oracle
        // (now always 0, since the staging buffer is never truncated/re-based) to ReplayPassCountForTests.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        // A severe (1%, not the sibling tests' own 500ppm) mismatch is used here specifically because
        // this test needs a commit to land well BEFORE line 16 -- SlantTests.cs's own established
        // characterization of this magnitude ("converges quickly") is exactly what a tight setup window
        // needs; 500ppm was measured (round-2 code review) to not reliably commit before line 16 for
        // this mode/rate.
        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);

        const int chunkSize = 256;
        var offset = 0;

        // Round-2 code-review correction: count TRANSMISSION LINES via NextLineForTests, not
        // LineDecoded events -- with automatic replay unsuppressed, LineDecoded fires once per REDRAWN
        // row too, which would inflate a plain event-count well past the real decode position. Robot36
        // has RowsPerTransmissionLine == 1, so this division is a no-op for THIS mode specifically, but
        // stated explicitly since the sibling tests' own event-counting convention would silently give
        // the wrong number here.
        int TransmissionLinesDecoded() => decoder.NextLineForTests; // RowsPerTransmissionLine == 1 for Robot36

        // Wait for a real commit (satisfies _anyCorrectionCommittedThisImage) AND a sync offset clearly
        // outside PerformReSync's own 5-sample deadband (Main.cpp:14006) -- the automatic commit
        // trigger's own replay pass re-centers the sync position, so requesting a ReSync immediately
        // after the FIRST commit alone is unreliable (round-2 code review: measured RequestReSync()
        // silently no-op under the deadband in that narrower window). Both well before line 16.
        while (offset < samples.Length
            && (decoder.SlantTrackerForTests?.DriftPpm is null or 0.0
                || decoder.SyncOffsetSamples is not int syncOffset || Math.Abs(syncOffset) < 5))
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(decoder.SlantTrackerForTests?.DriftPpm is not (null or 0.0), "Test setup problem: never observed a committed correction.");
        Assert.True(decoder.SyncOffsetSamples is int finalOffset && Math.Abs(finalOffset) >= 5, "Test setup problem: sync offset never cleared PerformReSync's own deadband -- the ReSync below would silently no-op.");
        Assert.True(TransmissionLinesDecoded() < 16, "Test setup problem: already past the once-per-image latch's own threshold before the manual ReSync below -- this test needs the hole to exist BEFORE the latch could otherwise fire.");
        // The commit trigger legitimately replays at least once already, before any ReSync -- proves the
        // hole the ReSync is about to punch lands in a NON-EMPTY staged buffer, i.e. a real interior gap,
        // not an edge case against an empty one.
        Assert.True(decoder.ReplayPassCountForTests > 0, "Test setup problem: the automatic commit trigger never replayed before the ReSync -- the hole below wouldn't land in a real interior gap.");

        // Round-3 code-review hardening: snapshot BEFORE RequestReSync(), not after the priming push --
        // ApplySyncCorrection (the ReSync's own shared tail) writes none of the RxBuffer-side fields, so
        // this value is identical either way in the common case, but capturing it here closes a
        // theoretical timing hole the auditor flagged: if the priming push below happened to cross the
        // 16-line latch threshold itself, capturing the snapshot AFTER that push could already reflect a
        // (reverted-gate) latch firing, silently making the test vacuous again. Captured here, that
        // can't happen -- the value used below is fixed before the ReSync (and its priming push) even run.
        var passCountAfterReSync = decoder.ReplayPassCountForTests;

        decoder.RequestReSync();
        // One more push to let the deferred ReSync actually apply (PerformReSync drains at the top of
        // the NEXT PushSamples call).
        var primeLength = Math.Min(chunkSize, samples.Length - offset);
        decoder.PushSamples(samples.AsMemory(offset, primeLength));
        offset += primeLength;
        Assert.True(decoder.SlantCorrectionsDisabledForRestOfImageForTests, "Test setup problem: RequestReSync() never actually applied (deadband?) -- the hole this test targets was never created.");

        // Decode well past the 16-transmission-line latch threshold.
        while (offset < samples.Length && TransmissionLinesDecoded() < 40)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(TransmissionLinesDecoded() >= 40, "Test setup problem: never decoded past the latch threshold after the ReSync.");
        Assert.True(decoder.SlantCorrectionsDisabledForRestOfImageForTests, "The disabled-for-rest-of-image flag should never clear mid-image -- see ApplySyncCorrection/ResetReSyncState's own scope.");
        // The real discriminator: the pass count must not have moved AGAIN since the ReSync -- proving
        // the once-per-image latch never fired across the hole. (It's allowed to be nonzero -- that's the
        // pre-ReSync commit-triggered pass captured above -- just unchanged since then.)
        Assert.Equal(passCountAfterReSync, decoder.ReplayPassCountForTests);
    }

    [Fact]
    public void RxBufferModeOn_DecodesMeasurablyBetterThanOff_UnderARealClockMismatch()
    {
        // Whole-subsystem review finding (2026-08-13): every OTHER test in this project verifies LOCAL
        // correctness of one RX buffer subsystem piece at a time (cursor bookkeeping stays consistent,
        // triggers fire/don't fire correctly, etc.) -- none of them had ever verified the actual POINT
        // of the whole feature: does a real drifting-clock reception decode BETTER with
        // RxBufferMode.On (capture + automatic replay, Phases 4-6) than with it Off? This test closes
        // that specific gap directly, end to end, through the real production automatic-trigger path
        // (no PerformReplayForTests, no suppression).
        //
        // MP73 (YCbCrLinePairedScanlineDecoder), not a Robot-family mode: deliberately sidesteps the
        // separate, already-known, already-deferred RobotScanlineDecoder cross-line chroma-cache finding
        // (see PerformReplay's own doc comment) -- this test isolates "does replay actually help" from
        // that unrelated, already-tracked defect. Also exercises the paired-channel
        // (RowsPerTransmissionLine == 2) path end-to-end, which the whole-subsystem review separately
        // noted had less end-to-end coverage than the sequential-channel modes.
        var mode = SstvModeRegistry.Mp73;
        var sourceImage = CreateRowIdentityTestImage(mode.ImageWidth, mode.ImageHeight); // a smooth gradient would hide the misalignment On is supposed to fix -- see this file's own earlier note on this

        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01); // severe, reliably-converging mismatch (SlantTests.cs's own established characterization)
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var onDecoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);
        var offDecoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.Off);

        IImageSource? onImage = null;
        IImageSource? offImage = null;
        onDecoder.LineDecoded += u => onImage = u.Image;
        offDecoder.LineDecoded += u => offImage = u.Image;

        onDecoder.PushSamples(samples);
        offDecoder.PushSamples(samples);

        Assert.NotNull(onImage);
        Assert.NotNull(offImage);
        Assert.True(onDecoder.ReplayPassCountForTests > 0, "Test setup problem: automatic replay never actually fired for the On decoder -- this test would prove nothing about the feature it's named for.");

        var onDelta = ComputeAveragePerChannelDelta(sourceImage, onImage!);
        var offDelta = ComputeAveragePerChannelDelta(sourceImage, offImage!);

        Assert.True(onDelta < offDelta, $"Expected RxBufferMode.On (delta {onDelta:F2}) to decode measurably better than Off (delta {offDelta:F2}) under a real clock mismatch -- if this fails, the RX buffer subsystem's own core value proposition isn't actually holding end to end, even though every individual piece tests correct in isolation.");
    }

    [Fact]
    public void PerformReplay_SetsSuppressionAndClearsStaleSyncPeak_AfterEveryPass()
    {
        // Buffered-replay fix (§15 item 2, robust-giggling-codd.md), round 3 risk #3: both existing
        // precedents that set _suppressNextSlantProcessLine = true (ApplySyncCorrection/
        // ApplyNotchDisableShift) pair it with _lastLineSyncPeakPosition = null -- PerformReplay's own
        // tail must do the same, or a manual ReSync landing before the first post-snap line completes
        // would read a stale peak position (the LAST REPLAYED line's own peak, written during the
        // re-feed walk) as if it were current. Unconditional on every completed pass, so no real
        // correction is needed to exercise it -- a clean signal suffices.
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On)
        {
            SuppressAutomaticReplayForTests = true,
        };

        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && (decoder.RxLineStagingBufferForTests?.LineCount ?? 0) <= 5)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True((decoder.RxLineStagingBufferForTests?.LineCount ?? 0) > 5, "Test setup problem: staging buffer never reached >5 lines.");

        decoder.PerformReplayForTests();

        Assert.True(decoder.SuppressNextSlantProcessLineForTests, "PerformReplay's own tail must suppress the first post-snap line's own Auto-Sync observation.");
        Assert.Null(decoder.LastLineSyncPeakPositionForTests);
    }

    [Fact]
    public void PerformReplay_BatchedLineDecodedEvent_NeverFiresBeforeTheImageHasAtLeast16LiveLines()
    {
        // Buffered-replay fix (§15 item 2), round 3/4 risk #4: three real consumers
        // (ReceiveHistoryRecorder, ReceivedImageBuffer, SstvSessionService's RX loopback self-test --
        // all in other projects, not directly reachable from this test) each derive their own step
        // size from only the FIRST TWO consecutive LineDecoded events, one-shot, never re-learned. A
        // batched replay event landing as an image's first or second event would poison that
        // derivation. Structurally impossible: both the automatic commit trigger and manual Correct
        // Slant require cumulative staged/decoded transmission lines >= 16 before a replay can even be
        // requested (see PerformReplay's own doc comment, Reachability paragraph) -- proven directly
        // here by chunking the push and confirming at least 16 live events already fired in some
        // earlier chunk before the chunk that first makes ReplayPassCountForTests move off 0.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);
        var eventCount = 0;
        decoder.LineDecoded += _ => eventCount++;

        const int chunkSize = 256;
        var offset = 0;
        var eventCountBeforeReplayChunk = -1;
        while (offset < samples.Length && decoder.ReplayPassCountForTests == 0)
        {
            eventCountBeforeReplayChunk = eventCount;
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(decoder.ReplayPassCountForTests > 0, "Test setup problem: automatic replay never fired.");
        // Conservative lower bound (events from the chunk that raises the batched event itself aren't
        // counted here, only events strictly before it) -- still enough to prove the invariant, since
        // the entry gate's own >= 16 requirement is on cumulative decoded/staged lines, not on this
        // chunk boundary.
        Assert.True(eventCountBeforeReplayChunk >= 15, $"Only {eventCountBeforeReplayChunk} live LineDecoded events fired before the replay-triggering chunk -- expected at least 15 (entry gate requires >= 16 cumulative lines before replay can even be requested).");
    }

    [Fact]
    public void DrainPendingSkip_InPostSnapWindow_DoesNotDoubleFeedDetectorOrAdvanceAnchor()
    {
        // Buffered-replay fix (§15 item 2, robust-giggling-codd.md), round 3/4 item 1: DrainPendingSkip's
        // 3-way guard (detector feed / _slantProcessedUpTo++ / _rxBufferAnchorSample++, all gated
        // together on _consumedSamples >= _slantProcessedUpTo) must suppress all three while
        // _consumedSamples sits behind the frozen _slantProcessedUpTo (the post-snap window a backward
        // snap opens) -- these samples were already staged/fed before the snap, so re-feeding the
        // detector or re-advancing either cursor would double-count them and corrupt the fixed
        // coordinate map every replay resumeDest depends on.
        //
        // A severe (1%) clock mismatch accumulates enough drift that even an early correction's own
        // backward-snap distance is comfortably larger than RequestNotch(enable)'s own fixed, precisely
        // known skip (NotchFilter.Tap/2 -- 96 taps at this test's 11025Hz rate, so a 48-sample skip) --
        // notch-enable reuses DrainPendingSkip verbatim (ApplyPendingNotchRequest's own doc comment), so
        // it exercises the exact same guard a manual ReSync would, with a skip size this test can assert
        // exactly rather than depending on PerformReSync's own data-dependent sync-offset measurement.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On)
        {
            SuppressAutomaticReplayForTests = true,
        };

        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length
            && (decoder.SlantTrackerForTests?.DriftPpm is null or 0.0
                || (decoder.RxLineStagingBufferForTests?.LineCount ?? 0) <= 20))
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(decoder.SlantTrackerForTests?.DriftPpm is not (null or 0.0), "Test setup problem: never observed a committed correction.");
        Assert.True((decoder.RxLineStagingBufferForTests?.LineCount ?? 0) > 20, "Test setup problem: staging buffer never reached >20 lines.");

        decoder.PerformReplayForTests();

        // NotchFilter.ComputeTap(11025) = (int)(96.0 * 11025 / 11025) = 96, already even, well under
        // NotchTapMax (256) -- computed here from the documented formula (NotchFilter.cs's own doc
        // comment) since the notch isn't constructed (and NotchTapForTests isn't readable) until
        // ApplyPendingNotchRequest actually runs, below. Self-checked against the real value once it is.
        const int expectedHalfTap = 48;
        var windowWidth = decoder.SlantProcessedUpToForTests - decoder.ConsumedSamplesForTests;
        Assert.True(windowWidth >= expectedHalfTap, $"Test setup problem: post-snap window ({windowWidth} samples) is narrower than the notch-enable skip ({expectedHalfTap} samples) -- this test needs the whole skip to land inside the window to prove the guard suppresses it completely.");

        var anchorBefore = decoder.RxBufferAnchorSampleForTests;
        var slantProcessedBefore = decoder.SlantProcessedUpToForTests;
        var detectorCallCountBefore = decoder.SyncEnvelopeDetectorForTests!.ProcessSampleCallCountForTests;

        decoder.RequestNotch(true, 1500.0); // in-passband frequency -- ComputeBandwidth's own 15Hz branch, irrelevant to Tap
        decoder.PushSamples(samples.AsMemory(offset, Math.Min(chunkSize, samples.Length - offset)));

        Assert.Equal(96, decoder.NotchTapForTests);
        var halfTap = decoder.NotchTapForTests / 2;
        Assert.Equal(expectedHalfTap, halfTap);
        Assert.Equal(0, decoder.PendingSkipSamplesForTests);

        // The anchor's own core proof: _rxBufferAnchorSample++ is ONLY reachable inside DrainPendingSkip's
        // guarded branch (ApplySlantTracking's own separate catch-up loop, below, never touches it) --
        // so it staying exactly where it was proves the guard suppressed every one of the skip's 48
        // samples, not just some of them. Without the guard this would have moved by +halfTap
        // permanently -- a real, uncorrected corruption of the fixed coordinate map every replay
        // resumeDest depends on (see _rxBufferAnchorSample's own doc comment).
        Assert.Equal(anchorBefore, decoder.RxBufferAnchorSampleForTests);

        // Coarse sanity check only, NOT a discriminator for this guard (code-review correction: an
        // earlier version of this comment overclaimed it was). _slantProcessedUpTo always converges
        // back to _consumedSamples regardless of the guard (ApplySlantTracking's own catch-up loop at
        // :6644 walks it there unconditionally once live decode passes wherever it's currently frozen),
        // and worked through by hand against the realistic regression (strip the `if` from
        // DrainPendingSkip's three guarded statements, keeping them as unconditional `++`, the natural
        // shape of "someone reverts just this guard"): the TOTAL detector call count since this pass
        // ended up EXACTLY THE SAME either way (`decoder.ConsumedSamplesForTests - slantProcessedBefore`
        // in both cases) -- what actually differs is WHICH raw-sample indices get fed (guarded: the
        // genuinely-new span once each; unguarded: the skip's own 48 already-fed indices get re-fed, and
        // an equal-sized span of genuinely-new indices right before the final _consumedSamples never
        // gets fed at all), which a call COUNT cannot see. This assertion still catches a totally
        // different regression (a gross off-by-some-other-amount in either cursor), so it stays, just
        // relabeled -- the anchor assertion above is this test's real, sole proof that the guard fires.
        var detectorCallDelta = decoder.SyncEnvelopeDetectorForTests!.ProcessSampleCallCountForTests - detectorCallCountBefore;
        var expectedDetectorCallDelta = decoder.ConsumedSamplesForTests - slantProcessedBefore;
        Assert.Equal(expectedDetectorCallDelta, detectorCallDelta);
    }

    [Fact]
    public void PerformReplay_RawFloorClampForward_TouchesOnlyTheLandingPoint()
    {
        // Buffered-replay fix (§15 item 2), code-review addition: the raw-floor clamp-forward branch is
        // the ONE path in PerformReplay's tail that reuses the retired forward-jump landing formula --
        // the plan flagged this twice as the exact spot a future edit could silently reintroduce the
        // un-staged-hole bug this whole piece removes (see PerformReplay's own doc comment, and this
        // branch's own inline comment: "do NOT set _slantProcessedUpTo = _consumedSamples here"). It
        // needs a real, deterministic test, not just a hand-derived proof.
        //
        // Naturally landing a backward snap far enough back to violate _bufferBase needs a manual
        // Correct Slant search whose own corrected rate differs enough from the accumulated drift --
        // empirically hard to land precisely (tried several real clock-mismatch magnitudes against a
        // full Robot36 image; each one either converged too close to land the target below the buffer's
        // real floor, or diverged enough to lose lock entirely before ever reaching TryCorrectSlant's own
        // entry gate). BufferBaseOverrideForTests exists for exactly this: forcing the ONE condition
        // (`backwardTargetConsumedSamples < _bufferBase`) this branch actually checks, deterministically,
        // without needing to fight the search's own real convergence dynamics -- the branch's own logic
        // (arithmetic on cursors already loaded before the check runs) doesn't care why the condition is
        // true, only that it is.
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = Encode(mode, sourceImage, SampleRate);

        var decoder = new AnalogFmSstvDecoder(SampleRate, rxBufferMode: RxBufferMode.On)
        {
            SuppressAutomaticReplayForTests = true,
        };

        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && (decoder.RxLineStagingBufferForTests?.LineCount ?? 0) <= 5)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True((decoder.RxLineStagingBufferForTests?.LineCount ?? 0) > 5, "Test setup problem: staging buffer never reached >5 lines.");

        var anchorBefore = decoder.RxBufferAnchorSampleForTests;
        var slantProcessedBefore = decoder.SlantProcessedUpToForTests;
        var stagedCountBefore = decoder.RxLineStagingBufferForTests!.Count;
        var consumedBefore = decoder.ConsumedSamplesForTests;

        // Forces the branch: a floor set to (current live cursor + 1) can never be satisfied by ANY
        // backward target (which is always <= the live cursor's own destination-coordinate position).
        decoder.BufferBaseOverrideForTests = consumedBefore + 1;
        try
        {
            decoder.PerformReplayForTests();
        }
        finally
        {
            decoder.BufferBaseOverrideForTests = null;
        }

        Assert.Equal(1, decoder.RawFloorClampForwardCountForTests);
        // The branch's own core contract: touches ONLY the landing point. Everything else the normal
        // backward branch also leaves alone stays exactly as it was.
        Assert.Equal(anchorBefore, decoder.RxBufferAnchorSampleForTests);
        Assert.Equal(slantProcessedBefore, decoder.SlantProcessedUpToForTests);
        Assert.Equal(stagedCountBefore, decoder.RxLineStagingBufferForTests!.Count);
        // The landing point itself must be a genuinely FORWARD move (strictly past where the live
        // cursor already was before this call, never at-or-behind it) -- `forward - consumedBefore =
        // ceil((resumeLine+1)*stride) - resumeDest` is unconditionally > 0, not just >= 0.
        Assert.True(decoder.ConsumedSamplesForTests > consumedBefore);
    }

    [Fact]
    public void PerformReplay_AfterRejectionAtCapacity_ResumesFromTheLiveCursorNotTheStagedExtent()
    {
        // Functional-audit fix (D3+D8+D9 coupled round 2, correcting a round-1 regression): a
        // rejected (buffer-full) TryAppendLine call must NOT move _rxBufferAnchorSample -- see that
        // field's own doc comment for the full corrected reasoning (it defines a FIXED coordinate
        // map, not a running "how much has been staged" tally; a rejected line was still decoded and
        // stamped into the image, only STAGING for a future replay failed, so the map is unchanged).
        // Round 1 got this backwards and advanced the anchor on every rejection, which pins
        // PerformReplay's own resumeDest to the STAGED extent instead of the live cursor's true
        // position -- confirmed via a worked numeric trace during round 2 review to silently rewind
        // _nextLine after a replay pass, re-stamping already-decoded rows with stale audio. This test
        // proves the actual consequence directly, not a bookkeeping proxy for it: after real decode
        // runs well past RAM capacity (every line from that point on rejected), a real PerformReplay
        // pass must resume from the TRUE live position (NextLineForTests staying at/near where live
        // decode actually was), not collapse back down to roughly how many lines were staged before
        // capacity was hit.
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var transmissionSamples = Encode(mode, sourceImage, SampleRate);

        // autoSyncEnabled:false -- isolates this test to the rejection behavior alone. A real
        // Auto-Sync correction (TryAutoSync -> ApplySyncCorrection) sets _pendingSkipSamples plus
        // _slantCorrectionsDisabledForRestOfImage/an Auto-Sync cooldown, all side effects unrelated
        // to the rejection behavior this test targets -- disabling Auto-Sync entirely avoids all of
        // them moving NextLineForTests for reasons unrelated to the fix under test. (D0-audit
        // round-7 correction: this comment previously justified the disable by DrainPendingSkip
        // having only one call site, at the top of PushSamplesCore -- since superseded by a D0
        // round-4 fix adding a second call site inside the per-line loop, so a skip no longer goes
        // un-drained within a single bulk push either way; the disable is still the right call, for
        // the broader reason stated above.)
        using var decoder = new AnalogFmSstvDecoder(SampleRate, autoSyncEnabled: false, rxBufferMode: RxBufferMode.On);
        var locked = false;
        decoder.ModeDetected += _ => locked = true;

        // Small chunks so we stop soon after lock (Commit/InitializeSlant, which constructs the real
        // staging buffer and sets the initial anchor) -- header detection itself can need buffered
        // lookahead beyond the header's own nominal boundary, so some real lines may already be
        // captured by the time ModeDetected fires within whichever chunk's PushSamples call actually
        // triggers it; the pre-fill below reads the buffer's own real Count rather than assuming 0.
        const int chunkSize = 256;
        var offset = 0;
        while (offset < transmissionSamples.Length && !locked)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Length - offset);
            decoder.PushSamples(transmissionSamples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(locked, "Test setup problem -- decoder never locked onto the header.");
        var buffer = Assert.IsType<RxLineStagingBuffer>(decoder.RxLineStagingBufferForTests);

        var fillerLength = buffer.CapacitySamples - 1 - buffer.Count;
        Assert.True(fillerLength > 0, $"Test setup problem -- real decode already staged {buffer.Count} samples before this test could pre-fill the rest, leaving no room; buffer.CapacitySamples={buffer.CapacitySamples}.");
        var filler = new double[fillerLength];
        Assert.True(buffer.TryAppendLine(filler, filler), "Test setup problem -- pre-fill append was rejected; CapacitySamples may have changed.");

        // Automatic replay stays suppressed while decoding the rest -- the synthetic filler above is
        // all-zero garbage, not a real demodulated/sync-envelope stream, so an automatic replay
        // reading it mid-decode (Auto-Slant is on by default) would confound the rest of this test
        // with unrelated pixel/state churn. This test drives PerformReplay itself, once, explicitly,
        // via PerformReplayForTests, after enough real (rejected) decode has happened.
        decoder.SuppressAutomaticReplayForTests = true;

        // Decode roughly half of what's left -- comfortably mid-transmission, not the trailing
        // footer/EndOfImage, so _mode/_lineDecoder/_pixels are still live when PerformReplayForTests
        // runs below (EndOfImage nulls them once the image completes, which would make PerformReplay
        // a no-op and defeat this test). Every real line decoded from here on is rejected -- the
        // buffer admission test is `Count + length >= Capacity`, so at Count == Capacity - 1 (this
        // test's pre-fill target) even a 1-sample line is rejected -- zero usable headroom, not 1.
        var remaining = transmissionSamples.Length - offset;
        decoder.PushSamples(transmissionSamples.AsMemory(offset, remaining / 2));

        var lineBeforeReplay = decoder.NextLineForTests;
        Assert.True(lineBeforeReplay > 10, $"Test setup problem -- expected many real (rejected) lines decoded by now, only got to line {lineBeforeReplay}.");

        decoder.PerformReplayForTests();

        // PerformReplay only ever redraws ALREADY-decoded rows from staged content and then computes
        // where LIVE decode should resume from -- it never consumes new audio itself, so the correct
        // post-replay NextLineForTests should land at/near lineBeforeReplay (within a row or two of
        // rounding slack at the destination-coordinate line boundary), not drift far in EITHER
        // direction. Round 1's regression would have collapsed this back down to roughly however many
        // lines were staged before the pre-fill (a handful -- far below lineBeforeReplay here, since
        // this test decodes dozens of real lines past that point) -- the lower bound below is what
        // that regression actually violates; the upper bound pins the same value against an
        // unexpected overshoot in the other direction.
        Assert.InRange(decoder.NextLineForTests, lineBeforeReplay - 1, lineBeforeReplay + 2);
    }

    private static float[] Encode(SstvModeDefinition mode, IImageSource sourceImage, int sampleRate)
    {
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, sourceImage).GetAsyncEnumerator();
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

    private static double ComputeAveragePerChannelDelta(IImageSource expected, IImageSource actual)
    {
        double totalDelta = 0;
        var sampleCount = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            for (var x = 0; x < expected.Width; x++)
            {
                totalDelta += Math.Abs(expectedLine[x].R - actualLine[x].R);
                totalDelta += Math.Abs(expectedLine[x].G - actualLine[x].G);
                totalDelta += Math.Abs(expectedLine[x].B - actualLine[x].B);
                sampleCount += 3;
            }
        }

        return totalDelta / sampleCount;
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    // Round-4 code-review addition: a per-row SOLID, widely-separated color (not a smooth gradient) --
    // the auditor's own round-4 finding was that CreateGradientTestImage's slow row-to-row ramp is a
    // "useless discriminator" for a one-row misassignment, since the delta between neighboring rows
    // falls below normal round-trip noise. Large multipliers with mod-256 wraparound spread adjacent
    // rows far apart in RGB space, so a row pulled from the wrong position stands out clearly.
    private static ArrayImageSource CreateRowIdentityTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            var color = new Rgb24(
                R: (byte)((y * 97 + 31) % 256),
                G: (byte)((y * 53 + 137) % 256),
                B: (byte)((y * 211 + 71) % 256));
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = color;
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static double RowDelta(ReadOnlySpan<Rgb24> a, ReadOnlySpan<Rgb24> b)
    {
        double total = 0;
        for (var x = 0; x < a.Length; x++)
        {
            total += Math.Abs(a[x].R - b[x].R) + Math.Abs(a[x].G - b[x].G) + Math.Abs(a[x].B - b[x].B);
        }

        return total / (a.Length * 3);
    }
}
