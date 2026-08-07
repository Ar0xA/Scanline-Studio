using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Manual ReSync — the port of legacy's real "ReSync" button (<c>TMmsstv::KRFSClick</c>,
/// <c>Main.cpp:14004-14020</c>), not <c>ReSyncSSTV</c>. See <see cref="AnalogFmSstvDecoder"/>'s own
/// doc comments on <c>PerformReSync</c>/<c>DrainPendingSkip</c> and <c>ApplySlantTracking</c>'s
/// two-flag suppression scheme for the full design.
/// </summary>
public class RequestReSyncTests
{
    [Fact]
    public void RequestReSync_BeforeAnyLockEverHappens_IsNoOpAndDoesNotThrow()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestReSync();
        decoder.PushSamples(new float[64]); // never matches any header -- stays idle

        Assert.Equal(0, decoder.ConsumedSamplesForTests);
        Assert.Null(decoder.LastLineSyncPeakPositionForTests);
    }

    // No "locked, but zero lines decoded yet" test for a non-AVT mode: confirmed unreachable via a
    // real decode. TryResolveSyncAnchorCorrection (AnalogFmSstvDecoder.cs, feeding
    // _pendingAnchorCorrectionMode) requires several transmission lines' worth of audio already
    // buffered before it can even resolve the anchor and let FinalizeAnchorAndStartDecoding raise
    // ModeDetected -- confirmed empirically too: even feeding one sample per PushSamples call,
    // LastLineSyncPeakPositionForTests already held a real value (120.75) the instant ModeDetected
    // first fired for Robot36. The `!_lastLineSyncPeakPosition.HasValue` guard in PerformReSync is
    // still real defensive code (AVT's own no-op test exercises the same null-check path via a
    // different precondition -- _slantTracker is null there), just not one this port's real decode
    // pipeline can ever observe standing alone for a non-AVT mode.

    [Fact]
    public void RequestReSync_Avt_IsNoOpForTheWholeImage()
    {
        // AVT has no Auto-Slant tracker at all (InitializeSlant returns early for it) -- confirmed
        // real legacy behavior (AutoStopJob's own m_ASDis-gated branches are keyed off modes AVT
        // isn't part of), matching this port's already-established AFC exclusion for the same mode.
        var mode = SstvModeRegistry.Avt;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025);

        // Decode a real prefix -- comfortably past lock, but AVT transmissions are long, so this
        // stays well short of EndOfImage (which would null _mode again and make the guard moot).
        var prefixLength = Math.Min(samples.Length, samples.Length / 10);
        decoder.PushSamples(samples.AsMemory(0, prefixLength));

        decoder.RequestReSync();
        var beforeConsumed = decoder.ConsumedSamplesForTests;

        decoder.PushSamples(samples.AsMemory(prefixLength, Math.Min(64, samples.Length - prefixLength)));

        Assert.True(decoder.ConsumedSamplesForTests - beforeConsumed <= 64,
            "AVT must never apply a ReSync jump -- it has no Auto-Slant tracker to read a peak position from.");
    }

    [Fact]
    public void RequestReSync_PeakWithinDeadband_IsNoOp_BothEarlyAndLate()
    {
        // A clean, self-consistent round trip (same encode/decode rate, no deliberate mistune) keeps
        // the measured peak within a sample or two of `ofp` for many lines -- comfortably inside the
        // 5-sample deadband on both sides, without needing to engineer an exact +4/-4 offset.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025);

        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;

        const int chunkSize = 256;
        var offset = 0;
        for (; offset < samples.Length && lineCount < 3; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(lineCount >= 3, "Never decoded enough lines -- test setup problem.");

        decoder.RequestReSync();
        var beforeConsumed = decoder.ConsumedSamplesForTests;

        decoder.PushSamples(samples.AsMemory(offset, Math.Min(64, samples.Length - offset)));

        Assert.True(decoder.ConsumedSamplesForTests - beforeConsumed <= 64,
            "A within-deadband peak must not trigger a jump.");
    }

    [Fact]
    public void RequestReSync_RealCorrection_ShiftsConsumedSamplesByExactlyTheHandComputedSkip()
    {
        // Deliberately large sample-clock mismatch (1%, matching SlantTests.cs's own
        // SlantTracker_ConsistentDrift... simulation) so the RAW measured peak drifts comfortably past
        // the 5-sample deadband within the first couple of lines, well before Auto Slant's own
        // correction ladder (which needs >=5 lines observed, then >=3 more past a baseline) has any
        // chance to reduce it -- avoids a borderline/flaky threshold crossing.
        var mode = SstvModeRegistry.Robot36;
        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);
        var samples = EncodeRealTransmissionAtRate(mode, trueSampleRate, out _);
        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);

        // Small chunks throughout, deliberately: TryProcessBuffer only decodes a line once a FULL
        // line's worth of buffered-but-undecoded audio ("backlog") has accumulated. Larger chunks let
        // that backlog grow close to a full line between decode events, and DrainPendingSkip's own loop
        // bound (_consumedSamples < TotalSamplesReceived) means a large enough backlog can absorb the
        // ENTIRE skip within a single call -- worse, that same call's own TryProcessBuffer can then
        // ALSO advance _consumedSamples further via ordinary decode against whatever backlog remains,
        // contaminating the exact-equality diff below with no way to observe the boundary between "skip
        // applied" and "ordinary decode resumed" from outside a single synchronous PushSamples call.
        // Keeping every chunk small keeps the backlog small too, so the drain reliably spans multiple
        // calls with nothing left over for TryProcessBuffer to additionally consume in the same call
        // where PendingSkipSamplesForTests first reaches 0.
        const int chunkSize = 32;
        var offset = 0;
        double? capturedPeak = null;
        int? capturedOfp = null;
        var reSyncRequested = false;
        var consumedBeforeDrain = 0;
        for (; offset < samples.Length && !reSyncRequested; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));

            if (decoder.LastLineSyncPeakPositionForTests is { } peak
                && decoder.SyncPeakOffsetSamplesForTests is { } ofpNow
                && Math.Abs(peak - ofpNow) >= 5)
            {
                capturedPeak = peak;
                capturedOfp = ofpNow;
                // Snapshot BEFORE RequestReSync() -- PerformReSync itself hasn't run yet (it's deferred
                // to the top of the NEXT PushSamples call), so _consumedSamples is still whatever
                // ordinary decode left it at.
                consumedBeforeDrain = decoder.ConsumedSamplesForTests;
                decoder.RequestReSync();
                reSyncRequested = true;
            }
        }

        Assert.True(reSyncRequested, "Never observed a peak past the deadband -- test setup problem, not what this test means to check.");
        // `offset` already points past the chunk that fired the request -- the for loop's own
        // increment ran once more before its condition (now false) stopped it.

        // Not a diff against a second decoder: a control decoder's OWN Auto-Slant corrections keep
        // adjusting its EffectiveSamplesPerLineForTests for as long as it keeps decoding (confirmed by
        // the negative control in the widened-suppression test below), which would silently contaminate
        // any consumed-samples diff taken more than a line or so after the click. Instead, bracket the
        // drain tightly on this ONE decoder: feed one sample at a time until PendingSkipSamplesForTests
        // reaches exactly 0 (a small chunk size above keeps the backlog small enough that this
        // genuinely takes more than one push, matching the drain-crash regression test's own approach).
        var drainIterations = 0;
        while (decoder.PendingSkipSamplesForTests is not 0 || drainIterations == 0)
        {
            Assert.True(offset < samples.Length && drainIterations < 5000,
                "Drain never reached 0 -- test setup problem.");
            decoder.PushSamples(samples.AsMemory(offset, 1));
            offset++;
            drainIterations++;
        }

        // Hand-computed from the peak/ofp captured just before RequestReSync() was called -- an
        // independent check, not a re-derivation of PerformReSync's internal formula
        // (SyncPeakOffsetSamplesForTests reuses that formula's own implementation, but the
        // deadband/wrap arithmetic here is written out fresh).
        var syncPos = (int)capturedPeak!.Value;
        var expectedSkip = syncPos - capturedOfp!.Value;
        if (expectedSkip < 0)
        {
            expectedSkip += (int)decoder.EffectiveSamplesPerLineForTests;
        }

        Assert.Equal(expectedSkip, decoder.ConsumedSamplesForTests - consumedBeforeDrain);

        // Distinguishes the two suppression scopes from the outside (auditor-flagged gap): the
        // one-line gate (_suppressNextSlantProcessLine) must contribute NOTHING to SlantTracker's
        // history -- not even via ProcessLineHistoryOnly -- matching legacy's m_SyncPos != -1 check
        // gating AutoStopJob() out entirely for that one line (Main.cpp:4190), as opposed to the
        // whole-image gate (_slantCorrectionsDisabledForRestOfImage), which keeps pushing history via
        // ProcessLineHistoryOnly for every line after that.
        var tracker = decoder.SlantTrackerForTests!;
        var linesObservedAfterDrain = tracker.TotalLinesObservedForTests;
        var lineWidth = (int)decoder.EffectiveSamplesPerLineForTests;

        // Push through the one suppressed line (the first to complete once the drain above finished).
        var consumedBeforeSuppressedLine = decoder.ConsumedSamplesForTests;
        while (decoder.ConsumedSamplesForTests - consumedBeforeSuppressedLine < lineWidth)
        {
            Assert.True(offset < samples.Length, "Ran out of samples before the suppressed line completed -- test setup problem.");
            decoder.PushSamples(samples.AsMemory(offset, 1));
            offset++;
        }

        Assert.Equal(linesObservedAfterDrain, tracker.TotalLinesObservedForTests); // the suppressed line: zero new history entries

        // Push through the next (first non-suppressed, whole-image-gated) line.
        var consumedBeforeNextLine = decoder.ConsumedSamplesForTests;
        while (decoder.ConsumedSamplesForTests - consumedBeforeNextLine < lineWidth)
        {
            Assert.True(offset < samples.Length, "Ran out of samples before the next line completed -- test setup problem.");
            decoder.PushSamples(samples.AsMemory(offset, 1));
            offset++;
        }

        Assert.Equal(linesObservedAfterDrain + 1, tracker.TotalLinesObservedForTests); // exactly one new entry, via ProcessLineHistoryOnly
    }

    [Fact]
    public void RequestReSync_Convergence_SecondClickAfterACompletedLineIsANoOp()
    {
        // A much smaller mistune than the other tests here, deliberately: with a constant clock
        // mismatch, corrections are disabled for the rest of the image, so the raw peak keeps drifting
        // AWAY from ofp every subsequent line at the same underlying rate, regardless of the earlier
        // click. At 1% (as used elsewhere in this file) that rate is >5 samples/line all by itself,
        // so a second click checked even one line later would legitimately find a fresh
        // deadband-exceeding peak and apply a fresh, correct skip -- not a bug, just incompatible with
        // an "idempotent second click" scenario. 0.04% keeps the per-line drift small enough that a
        // couple of lines' gap plausibly stays inside the deadband, verified dynamically below rather
        // than assumed from this ppm figure alone (0.08% measured ~3 samples/line empirically here --
        // enough to make 2 lines' worth of drift alone exceed the deadband).
        var mode = SstvModeRegistry.Robot36;
        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0004);
        var samples = EncodeRealTransmissionAtRate(mode, trueSampleRate, out _);
        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);

        // Polling LastLineSyncPeakPositionForTests directly after each push, deliberately, rather than
        // via the LineDecoded event: LineDecoded fires BEFORE ApplySlantTracking catches up for that
        // same line (AnalogFmSstvDecoder.cs -- LineDecoded?.Invoke precedes the "let slant tracking
        // catch up" step), so a LineDecoded handler always observes a one-line-STALE capture. Checking
        // directly in this outer loop, after PushSamples has already returned, sees the fully
        // caught-up state instead.
        const int chunkSize = 32; // keep backlog small -- see RealCorrection test's own comment for why
        var offset = 0;
        var reSyncRequested = false;
        var realCapturesSinceReSync = 0;
        double? lastObservedPeak = null;
        for (; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));

            if (!reSyncRequested
                && decoder.LastLineSyncPeakPositionForTests is { } peak
                && decoder.SyncPeakOffsetSamplesForTests is { } ofp
                && Math.Abs(peak - ofp) >= 5)
            {
                decoder.RequestReSync();
                reSyncRequested = true;
            }
            else if (reSyncRequested
                && decoder.LastLineSyncPeakPositionForTests is { } current
                && current != lastObservedPeak)
            {
                // The one suppressed line leaves this null; the first DISTINCT non-null value
                // afterward is a genuinely new capture (a repeated non-null across several small
                // pushes, with no new line completing in between, must not be double-counted). Require
                // two such real captures, so the idempotence check below isn't exercised on the very
                // first line straddling the jump -- matches the plan's own "at least one more line
                // completes" spec.
                lastObservedPeak = current;
                realCapturesSinceReSync++;
                if (realCapturesSinceReSync >= 2)
                {
                    break;
                }
            }
        }

        Assert.True(reSyncRequested, "Never observed a peak past the deadband -- test setup problem.");
        Assert.True(realCapturesSinceReSync >= 2, "Never let enough real (non-suppressed) lines complete after the correction -- test setup problem.");

        // Verify the scenario this test actually means to exercise, rather than assuming the ppm
        // figure above guarantees it: the peak must genuinely still be within deadband right now, or
        // the "no-op" assertion below would be trivially wrong to expect (pick a smaller mistune if
        // this ever fails).
        Assert.True(
            decoder.LastLineSyncPeakPositionForTests is { } settledPeak
            && decoder.SyncPeakOffsetSamplesForTests is { } settledOfp
            && Math.Abs(settledPeak - settledOfp) < 5,
            $"Peak already exceeds the deadband again before the second click. peak={decoder.LastLineSyncPeakPositionForTests} ofp={decoder.SyncPeakOffsetSamplesForTests}");

        var beforeSecondClick = decoder.ConsumedSamplesForTests;
        decoder.RequestReSync();
        decoder.PushSamples(samples.AsMemory(offset, Math.Min(64, samples.Length - offset)));

        Assert.Equal(beforeSecondClick, decoder.ConsumedSamplesForTests);
    }

    [Fact]
    public void RequestReSync_DrainSpanningMultiplePushSamplesCalls_DoesNotThrow_AndPinsThePartiallyDrainedState()
    {
        // The actual crash regression test: a bulk push would never present a chunk boundary mid-skip,
        // so this specifically uses chunks much smaller than a computed skip can be.
        var mode = SstvModeRegistry.Robot36;
        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);
        var samples = EncodeRealTransmissionAtRate(mode, trueSampleRate, out _);
        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);

        const int chunkSize = 32; // deliberately smaller than any realistic skip
        var offset = 0;
        var reSyncRequested = false;
        for (; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length)); // must never throw

            if (!reSyncRequested
                && decoder.LastLineSyncPeakPositionForTests is { } peak
                && decoder.SyncPeakOffsetSamplesForTests is { } ofp
                && Math.Abs(peak - ofp) >= 5)
            {
                decoder.RequestReSync();
                reSyncRequested = true;
                continue;
            }

            if (reSyncRequested)
            {
                break; // stop right after the request -- we want to observe the drain still in progress
            }
        }

        Assert.True(reSyncRequested, "Never observed a peak past the deadband -- test setup problem.");

        // Immediately after the request, the drain has almost certainly not finished in a single
        // 32-sample chunk (a skip is typically tens to low hundreds of samples) -- pin that it
        // genuinely takes more than one further chunk to fully apply, not just that it doesn't throw.
        var consumedRightAfterRequest = decoder.ConsumedSamplesForTests;
        var drainedFully = false;
        for (var i = 0; i < 50 && offset < samples.Length; i++, offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));

            if (decoder.ConsumedSamplesForTests > consumedRightAfterRequest + chunkSize)
            {
                drainedFully = true;
                break;
            }
        }

        Assert.True(drainedFully, "Drain never appeared to make progress across multiple small PushSamples calls.");
    }

    [Fact]
    public void RequestReSync_DisablesSlantCorrections_ForTheRestOfTheImage_WithANegativeControl()
    {
        var mode = SstvModeRegistry.Robot36;
        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);
        var samplesWith = EncodeRealTransmissionAtRate(mode, trueSampleRate, out _);
        var samplesControl = samplesWith; // same source samples -- only whether RequestReSync() is called differs

        var withReSync = new AnalogFmSstvDecoder(declaredSampleRate);
        var control = new AnalogFmSstvDecoder(declaredSampleRate);

        const int chunkSize = 256;
        var offset = 0;
        var reSyncRequested = false;
        for (; offset < samplesWith.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samplesWith.Length - offset);
            withReSync.PushSamples(samplesWith.AsMemory(offset, length));
            control.PushSamples(samplesControl.AsMemory(offset, length));

            if (!reSyncRequested
                && withReSync.LastLineSyncPeakPositionForTests is { } peak
                && withReSync.SyncPeakOffsetSamplesForTests is { } ofp
                && Math.Abs(peak - ofp) >= 5)
            {
                withReSync.RequestReSync();
                reSyncRequested = true;
            }
        }

        Assert.True(reSyncRequested, "Never observed a peak past the deadband -- test setup problem.");

        // Negative control: the SAME mistuned clock, decoded without ever calling RequestReSync(),
        // must eventually trigger a real Auto-Slant correction -- otherwise the positive assertion
        // below would be vacuously true because the mistune was too small to matter at all.
        Assert.NotEqual(mode.LineDurationMs / 1000.0 * declaredSampleRate, control.EffectiveSamplesPerLineForTests);

        // Positive assertion: once corrections are disabled, they stay disabled for the rest of the
        // image, even though the underlying clock is still just as mistuned.
        Assert.Equal(mode.LineDurationMs / 1000.0 * declaredSampleRate, withReSync.EffectiveSamplesPerLineForTests);
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode, out ArrayImageSource sourceImage)
        => EncodeRealTransmissionAtRate(mode, 11025, out sourceImage);

    private static float[] EncodeRealTransmissionAtRate(SstvModeDefinition mode, int encodeSampleRate, out ArrayImageSource sourceImage)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        sourceImage = image;

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
}
