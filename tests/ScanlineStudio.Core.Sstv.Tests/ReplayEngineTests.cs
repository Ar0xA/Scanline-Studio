using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 6c -- tests for <c>AnalogFmSstvDecoder.PerformReplay</c> (exercised via
/// <c>PerformReplayForTests</c>, since Phase 6d, the automatic deferred-trigger wiring, is not yet
/// built -- this phase's replay engine is not reachable from any production code path yet).
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
        // to redraw whatever's been staged so far.
        const int chunkSize = 256;
        var offset = 0;
        while (offset < samples.Length && liveLineCount < 20)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True(liveLineCount >= 20, "Test setup problem: never decoded 20 lines live before attempting replay.");
        Assert.True(decoder.RxLineStagingBufferForTests!.LineCount > 0, "Test setup problem: nothing staged yet.");

        var replayLineCount = 0;
        decoder.LineDecoded += _ => replayLineCount++;
        decoder.PerformReplayForTests();

        Assert.True(replayLineCount > 0, "PerformReplay never fired LineDecoded -- expected it to redraw at least the rows already staged.");
    }

    [Fact]
    public void PerformReplay_AfterARealCorrectionCommits_LiveDecodeContinuesRowsWithoutGapOrRepeat()
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
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);
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
        Assert.NotNull(firstRowAfterReplay);
        // Round-3 code-review correction: PerformReplay's own sample-cursor re-anchor ALWAYS sacrifices
        // exactly one transmission line's own row (the row covering whatever raw samples were consumed
        // live but not yet reflected in the staged sample count when this pass ran -- see the method's
        // own doc comment) -- not "at most one, sometimes zero" as an earlier version of this test
        // assumed. In the steady state (live decode's own sample-consumption and slant-tracking
        // accumulation advance together) that gap is un-skippable by construction, so the expected next
        // row is deterministically `lastRowDuringReplay + 2` (one sacrificed row + the next one),
        // Robot36's own RowsPerTransmissionLine == 1.
        Assert.Equal(lastRowDuringReplay + 2, firstRowAfterReplay!.Value);

        Assert.NotNull(decodedImage);
        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);
        Assert.True(averageDelta <= 10.0, $"Average per-channel delta {averageDelta:F2} exceeded normal round-trip tolerance -- a mid-stream PerformReplay call across a real correction should still leave the live decode that continues after it self-consistent.");
    }

    [Fact]
    public void PerformReplay_CalledTwice_SecondPassStaysCorrectDespiteFirstPassesCursorJump()
    {
        // Round-3 code-review regression test for the stale-anchor blocker, STRENGTHENED in round 4
        // after the auditor's own two-pass trace found round 3's fix (_rxBufferAnchorSample advanced
        // additively) kept the sample-COUNT bookkeeping correct while leaving the staging buffer
        // PHYSICALLY discontinuous -- a second pass would read straight across that gap as if it were
        // continuous audio, silently misaligning PIXELS without the row-index gap (gap1/gap2 below)
        // ever showing anything wrong. Round 4's fix truncates the staging buffer at every jump (see
        // PerformReplay's own doc comment) and tracks WHERE in the image that truncation point sits
        // (_rxBufferBaseTransmissionLine). This test now checks what the auditor's own round-4 report
        // said the row-index-only checks couldn't: PIXEL CONTENT.
        //
        // A per-row solid, widely-separated color (NOT the smooth gradient other tests in this file
        // use) is deliberately used -- the auditor's own round-4 finding was that a slow gradient is a
        // "useless discriminator" here, since a one-row misassignment falls below its own row-to-row
        // delta. Large per-row jumps make a wrong row assignment obvious.
        //
        // R24 (YCbCrSequentialScanlineDecoder), not Robot36, deliberately: while diagnosing this test's
        // own failures during round-4 implementation, Robot36 (RobotScanlineDecoder) surfaced a REAL,
        // SEPARATE finding -- that decoder caches the previous line's OTHER chroma channel across
        // DecodeLine calls (an instance field, matching legacy's own m_D36[2][320] cross-line state),
        // which the "sacrifice one row + occasionally redraw an already-decoded row" replay design
        // (this method's own doc comment) does not currently preserve correctly: a sacrificed row's own
        // missing chroma contribution, and a redrawn row's own re-run of the R-Y/B-Y alternation, both
        // desynchronize that cache from what an unbroken legacy decode would produce. Confirmed by
        // running this exact scenario against Robot36 first -- it failed with a REAL cross-row color
        // bleed (not a test-harness artifact) that this same test, run against R24 (a genuinely
        // stateless decoder -- fresh Y/R-Y/B-Y arrays every call, verified by reading its source), does
        // not exhibit. Logged as a known, deferred limitation (PerformReplay's own doc comment), not
        // silently dropped -- fixing it is real, separate work (likely needs the replay engine to
        // either never sacrifice/redraw a row for a stateful decoder family, or reset that decoder's
        // own cross-line cache at a truncation boundary), out of scope for this round.
        var mode = SstvModeRegistry.R24;
        var sourceImage = CreateRowIdentityTestImage(mode.ImageWidth, mode.ImageHeight);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On);
        var replaying = false;
        var lastRowDuringReplay = -1;
        int? firstRowAfterReplay = null;
        IImageSource? decodedImage = null;
        var rowsTouchedThisPass = new HashSet<int>();
        decoder.LineDecoded += update =>
        {
            decodedImage = update.Image;
            if (replaying)
            {
                lastRowDuringReplay = update.Line;
                rowsTouchedThisPass.Add(update.Line);
            }
            else if (lastRowDuringReplay >= 0 && firstRowAfterReplay is null)
            {
                firstRowAfterReplay = update.Line;
            }
        };

        (int gap, Rgb24[] snapshotBeforeThisPass, int decodedRowBoundaryAtSnapshot, HashSet<int> rowsTouched) RunOneReplayPassAndReturnGap(ref int offset)
        {
            lastRowDuringReplay = -1;
            firstRowAfterReplay = null;
            rowsTouchedThisPass = [];
            var startingLineCount = decoder.RxLineStagingBufferForTests!.LineCount;
            var snapshot = decodedImage is null ? [] : SnapshotRows(decodedImage);
            // Only rows [0, decodedRowBoundaryAtSnapshot) actually have real (non-default) content at
            // snapshot time -- rows at or past this boundary haven't been decoded by ANYTHING yet, and
            // ordinary live decode (unrelated to replay) legitimately fills them in as more samples
            // arrive, including during this very pass's own priming loop below. The "untouched rows
            // stay identical" check further down must only apply below this boundary, or it flags
            // completely normal live-decode progress as a false regression.
            var decodedRowBoundaryAtSnapshot = decoder.NextLineForTests;

            while (offset < samples.Length)
            {
                var length = Math.Min(256, samples.Length - offset);
                decoder.PushSamples(samples.AsMemory(offset, length));
                offset += length;

                if (lastRowDuringReplay < 0 && decoder.RxLineStagingBufferForTests!.LineCount > startingLineCount + 5)
                {
                    replaying = true;
                    decoder.PerformReplayForTests();
                    replaying = false;

                    // Buffer-state invariants, checked immediately after every truncation (round-4 fix):
                    // the staging buffer must be genuinely empty, and _rxBufferAnchorSample/
                    // _rxBufferBaseTransmissionLine must both track the fresh anchor exactly, or the
                    // NEXT pass's own resumeDest/row-loop math silently drifts.
                    Assert.Equal(0, decoder.RxLineStagingBufferForTests!.Count);
                    Assert.Equal(0, decoder.RxLineStagingBufferForTests!.LineCount);
                    Assert.Equal(decoder.ConsumedSamplesForTests, decoder.RxBufferAnchorSampleForTests);
                    // R24: RowsPerTransmissionLine == 1, so base and NextLine coincide numerically here --
                    // round-5 code-review nit: this specific assertion is common-mode-blind (it would
                    // have passed even with the exact "both fields got the un-based local value" bug an
                    // earlier version of this fix had, since BOTH fields would be wrong the SAME way).
                    // The gap2 assertion at the bottom of this test is what actually caught that bug --
                    // this check stays for basic sanity, not as the primary regression guard.
                    Assert.Equal(decoder.RxBufferBaseTransmissionLineForTests, decoder.NextLineForTests);
                }

                if (lastRowDuringReplay >= 0 && firstRowAfterReplay is not null)
                {
                    break;
                }
            }

            Assert.True(lastRowDuringReplay >= 0, "Test setup problem: PerformReplay never fired LineDecoded for this pass.");
            Assert.NotNull(firstRowAfterReplay);
            return (firstRowAfterReplay!.Value - lastRowDuringReplay, snapshot, decodedRowBoundaryAtSnapshot, rowsTouchedThisPass);
        }

        var offset = 0;
        // Prime the staging buffer before the first pass, same threshold the sibling single-replay test
        // uses.
        while (offset < samples.Length && (decoder.RxLineStagingBufferForTests?.LineCount ?? 0) <= 5)
        {
            var length = Math.Min(256, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;
        }

        Assert.True((decoder.RxLineStagingBufferForTests?.LineCount ?? 0) > 5, "Test setup problem: staging buffer never reached >5 lines before the first pass.");

        var (gap1, _, _, _) = RunOneReplayPassAndReturnGap(ref offset);
        var (gap2, snapshotBeforePass2, decodedRowBoundaryBeforePass2, rowsTouchedByPass2) = RunOneReplayPassAndReturnGap(ref offset);

        // The pixel-content check the auditor's own round-4 report said the row-index-only checks (gap1/
        // gap2 alone) could not catch: every row PASS 2 redrew must match the SOURCE image's own row for
        // that index (proving the second pass reads the RIGHT audio, not audio shifted by the
        // first pass's own jump) -- and every row pass 2 did NOT redraw must be byte-identical to its
        // OWN pre-pass-2 snapshot (proving pass 2 never stamps content into rows that belong to pass 1's
        // own already-correct redraw, the base-transmission-line regression this round's fix targets).
        Assert.NotNull(decodedImage);
        for (var y = 0; y < mode.ImageHeight; y++)
        {
            var actualRow = decodedImage!.GetScanline(y);
            if (rowsTouchedByPass2.Contains(y))
            {
                var delta = RowDelta(actualRow, sourceImage.GetScanline(y));
                Assert.True(delta <= 24.0, $"Row {y} (redrawn by pass 2) delta {delta:F2} vs source exceeded tolerance -- pass 2 likely read audio from the wrong (jump-shifted) position.");
            }
            else if (y < decodedRowBoundaryBeforePass2)
            {
                // Only rows that were ALREADY decoded (by pass 1's own replay or by ordinary live
                // decode) before pass 2 started are checked for "must stay identical" -- a row past
                // that boundary was still default/undecoded at snapshot time, and ordinary live decode
                // legitimately filling it in during pass 2's own priming loop is normal progress, not a
                // regression.
                var beforeRow = snapshotBeforePass2.AsSpan(y * mode.ImageWidth, mode.ImageWidth);
                var delta = RowDelta(actualRow, beforeRow);
                Assert.True(delta <= 1.0, $"Row {y} (NOT redrawn by pass 2) changed by {delta:F2} -- pass 2 overwrote a row it shouldn't have touched (a base-transmission-line regression).");
            }
        }

        // R24: RowsPerTransmissionLine == 1 -- both passes sacrifice exactly one row, deterministically.
        // gap2 is the value this test exists to pin -- both round-3's stale-anchor bug and round-4's
        // own local/absolute base-offset bug would leave gap2 wrong (differently: stale-anchor gives a
        // gap larger than 2, the base-offset bug gave a large NEGATIVE gap) while leaving gap1 correct.
        Assert.Equal(2, gap1);
        Assert.Equal(2, gap2);
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
        // Round-3 code-review correction: same deterministic "always sacrifice exactly one transmission
        // line" reasoning as the sibling Robot36 test above, scaled by RowsPerTransmissionLine == 2 (one
        // sacrificed transmission line == 2 bitmap rows) -- the exact expected gap is +4, not a range.
        Assert.Equal(lastRowDuringReplay + 4, firstRowAfterReplay!.Value);
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

    private static Rgb24[] SnapshotRows(IImageSource image)
    {
        var snapshot = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            image.GetScanline(y).CopyTo(snapshot.AsSpan(y * image.Width, image.Width));
        }

        return snapshot;
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
