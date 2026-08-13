using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 5 -- decoder wiring for <see cref="RxLineStagingBuffer"/> capture. This
/// phase originally produced a decoder that behaves IDENTICALLY to before for every existing fixture
/// (capture is a pure side channel, nothing reads it back yet -- RX buffer subsystem Phase 6, not yet
/// built at the time) -- the full existing suite passing unchanged was that guarantee; the tests here
/// add the missing positive assertion that capture actually happens, plus the negative ones (Off/
/// Extended don't capture yet, AVT isn't captured, output is unaffected either way).
///
/// <b>RX buffer subsystem Phase 6d update: "output is unaffected" is no longer true for
/// <see cref="RxBufferMode.On"/> by default</b> -- Phase 6d wires <c>AnalogFmSstvDecoder.PerformReplay</c>
/// to fire AUTOMATICALLY during real decode (an Auto-Slant commit, or a once-per-image latch), and
/// replay retroactively REDRAWS already-decoded rows -- a real, intended behavior change, not a
/// regression. The byte-identical assertions below now use
/// <see cref="AnalogFmSstvDecoder.SuppressAutomaticReplayForTests"/> to isolate and re-verify the
/// ORIGINAL Phase 5 guarantee this file's own name promises -- capture BY ITSELF (with replay
/// suppressed) is still a pure side channel -- which remains true and worth its own regression
/// coverage; it does NOT (and no longer can) claim anything about <see cref="RxBufferMode.On"/>'s real,
/// default, replay-enabled behavior, which is exercised elsewhere (`ReplayEngineTests.cs`).
/// </summary>
public class RxBufferCaptureTests
{
    [Fact]
    public void RxBufferMode_On_ActuallyCapturesSamplesDuringARealDecode()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);

        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;
        const int chunkSize = 256;
        for (var offset = 0; offset < samples.Length && lineCount < 10; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(lineCount >= 10, "Never decoded enough lines -- test setup problem.");
        Assert.NotNull(decoder.RxLineStagingBufferForTests);
        Assert.True(decoder.RxLineStagingBufferForTests!.Count > 0, "Expected the staging buffer to have captured samples after 10 decoded lines.");

        // Ballpark check: after `lineCount` completed slant-lines with no mid-stream correction ever
        // committing (a clean signal, no rate mismatch), the captured sample total should track
        // `lineCount * effectiveSamplesPerLine` closely -- not exact (per-line rounding: the capture
        // hook's own flush boundary is effectively a per-line ceil, while `_consumedSamples` uses a
        // round -- these happen to coincide at lineCount=10 for this mode/rate, round-1 code-review
        // finding on an earlier, tighter version of this tolerance that was actually relying on that
        // coincidence, not a stated identity) -- widened to a full extra line of slack either way so
        // the test doesn't depend on rounding parity at a specific lineCount. Still tight enough to
        // catch the hook firing at the wrong granularity entirely (e.g. once per sample instead of once
        // per line, or never resetting the per-line accumulator), which would be off by orders of
        // magnitude, not a fraction of one line.
        var oneLine = mode.LineDurationMs / 1000.0 * 11025;
        var expectedApprox = lineCount * oneLine;
        Assert.InRange(decoder.RxLineStagingBufferForTests.Count, expectedApprox - oneLine, expectedApprox + oneLine);
    }

    [Theory]
    [InlineData(RxBufferMode.Off)]
    [InlineData(RxBufferMode.Extended)]
    public void RxBufferMode_OffAndExtended_DoNotCaptureYet(RxBufferMode mode)
    {
        // Extended's own disk-backed capture is a separate, still-unbuilt mechanism (RX buffer
        // subsystem Phase 7) -- this phase wires RAM-mode (On) capture only, so Extended must NOT
        // silently start capturing into a RAM buffer as a side effect of this phase landing.
        var sstvMode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(sstvMode);
        var decoder = new AnalogFmSstvDecoder(11025, rxBufferMode: mode);

        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;
        const int chunkSize = 256;
        for (var offset = 0; offset < samples.Length && lineCount < 10; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(lineCount >= 10, "Never decoded enough lines -- test setup problem.");
        Assert.Null(decoder.RxLineStagingBufferForTests);
    }

    [Fact]
    public void Capture_DoesNotChangeDecodeOutput_OnAndOffProduceByteIdenticalImages()
    {
        // The core Phase 5 guarantee: capture is a pure side channel. Decode the SAME signal with
        // capture on and off, and confirm the resulting images are pixel-exact, not just close --
        // this is a deterministic decode with no capture-dependent randomness, so exact equality is the
        // right bar, not a tolerance.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);

        AssertByteIdenticalDecode(mode, samples, afcEnabled: true);
    }

    [Fact]
    public void Capture_DoesNotChangeDecodeOutput_EvenWithAfcDisabled()
    {
        // Round-1 code-review finding: the capture hook's own DemodulatedFrequencyAt call is only
        // GUARANTEED to be a pure cache read (not the first computation of a sample) when
        // ApplyAfcCorrections has already covered this line's full extent -- which it does NOT do at
        // all when afcEnabled=false (a real, user-facing constructor setting). This is exactly the
        // configuration most likely to exercise the capture hook as a first-toucher of a line's tail
        // samples -- see AnalogFmSstvDecoder's own capture-hook doc comment for the full reasoning.
        // Even so, output must still be byte-identical On vs Off (DemodulatedFrequencyAt's own
        // strict-monotonic-first-touch contract holds regardless of WHICH caller touches an index
        // first).
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);

        AssertByteIdenticalDecode(mode, samples, afcEnabled: false);
    }

    [Fact]
    public void Capture_DoesNotChangeDecodeOutput_AcrossAMidStreamRestartBoundary()
    {
        // Round-1 code-review finding: the OTHER configuration where the capture hook can become a
        // first-toucher of a line's tail samples is the trailing lines of an image, near an
        // EndOfImage/mode-restart boundary (ApplyAfcCorrections' own bound can fall short there too).
        // Two back-to-back real transmissions in one stream exercises exactly that boundary, not just a
        // single clean image.
        AssertByteIdenticalDecodeAcrossTwoImages(afcEnabled: true);
    }

    [Fact]
    public void Capture_DoesNotChangeDecodeOutput_AfcDisabledAcrossAMidStreamRestartBoundary()
    {
        // Round-2 code-review finding: `afcEnabled: false` guarantees a non-empty tail gap on every
        // line (ApplyAfcCorrections never runs at all), and two back-to-back transmissions exercises an
        // EndOfImage boundary (which resets _bandpassLockedFromSample to int.MaxValue between images) --
        // together the combination previously untested by either arm alone. Round-3 correction of an
        // earlier version of this comment: the capture hook's own `useLocked` selection can NOT actually
        // diverge here -- ApplySlantTracking's own AgcSampleAt call (one line above the capture hook,
        // unconditional in every RxBufferMode) already settles BandpassFilteredSampleAt/useLocked for
        // each index before the capture hook's own DemodulatedFrequencyAt call ever reads it. The
        // narrower residual risk this arm can't cover is `isNarrow` specifically diverging on a
        // mid-image restart onto a NARROW mode -- not exercised here (Robot36 only), and outside this
        // port's existing narrow-mode H3/HBPFN coverage regardless (see AnalogFmSstvDecoder's own
        // capture-hook doc comment for the full, corrected reasoning). This arm is still real, useful
        // regression coverage for the tail-gap/restart-boundary combination itself, just not proof of a
        // `useLocked` divergence that turns out to be structurally impossible.
        AssertByteIdenticalDecodeAcrossTwoImages(afcEnabled: false);
    }

    private static void AssertByteIdenticalDecodeAcrossTwoImages(bool afcEnabled)
    {
        var mode = SstvModeRegistry.Robot36;
        var firstImageSamples = EncodeRealTransmission(mode);
        var secondImageSamples = EncodeRealTransmission(mode);
        var combined = new float[firstImageSamples.Length + secondImageSamples.Length];
        Array.Copy(firstImageSamples, combined, firstImageSamples.Length);
        Array.Copy(secondImageSamples, 0, combined, firstImageSamples.Length, secondImageSamples.Length);

        // Round-3 code-review nit: guard against this "restart boundary" arm silently degrading to a
        // single-image test if the second lock never actually happened (would pass vacuously otherwise,
        // same vacuity class the AVT test was already fixed for in round 1).
        var modeDetectedCount = 0;
        void CountModeDetected(SstvModeDefinition _) => modeDetectedCount++;

        AssertByteIdenticalDecode(mode, combined, afcEnabled, CountModeDetected);
        Assert.True(modeDetectedCount >= 2, "Never reached a second lock -- test setup problem, not proof the restart boundary is unaffected.");
    }

    private static void AssertByteIdenticalDecode(SstvModeDefinition mode, float[] samples, bool afcEnabled, Action<SstvModeDefinition>? onModeDetected = null)
    {
        // RX buffer subsystem Phase 6d: SuppressAutomaticReplayForTests isolates the ORIGINAL Phase 5
        // guarantee this test exists to check (capture alone, with no replay ever firing, changes
        // nothing observable) from Phase 6d's own real, intended behavior change (RxBufferMode.On's
        // real default DOES retroactively redraw rows) -- see this class's own updated doc comment.
        var onDecoder = new AnalogFmSstvDecoder(11025, afcEnabled: afcEnabled, rxBufferMode: RxBufferMode.On) { SuppressAutomaticReplayForTests = true };
        var offDecoder = new AnalogFmSstvDecoder(11025, afcEnabled: afcEnabled, rxBufferMode: RxBufferMode.Off);
        if (onModeDetected is not null)
        {
            onDecoder.ModeDetected += onModeDetected;
        }

        // Round-2 code-review finding: an earlier version of this helper kept only the LAST
        // LineDecoded image, which -- across two back-to-back transmissions -- compares ONLY the
        // second image; the first image's own trailing lines (the exact thing the restart-boundary
        // arms exist to exercise) were never actually compared. Snapshot (deep-copy, not the live
        // mutable-alias reference LineDecoded hands out) EVERY LineDecoded update instead, so every
        // line of every image in the stream gets compared, not just the final state.
        var onSnapshots = new List<Rgb24[]>();
        var offSnapshots = new List<Rgb24[]>();
        onDecoder.LineDecoded += u => onSnapshots.Add(CopyPixels(u.Image));
        offDecoder.LineDecoded += u => offSnapshots.Add(CopyPixels(u.Image));

        onDecoder.PushSamples(samples);
        offDecoder.PushSamples(samples);

        Assert.NotEmpty(onSnapshots);
        Assert.Equal(onSnapshots.Count, offSnapshots.Count);
        for (var i = 0; i < onSnapshots.Count; i++)
        {
            Assert.Equal(onSnapshots[i], offSnapshots[i]);
        }
    }

    private static Rgb24[] CopyPixels(IImageSource image)
    {
        var copy = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            image.GetScanline(y).CopyTo(copy.AsSpan(y * image.Width, image.Width));
        }

        return copy;
    }

    [Fact]
    public async Task Avt_IsNotCaptured_DocumentedScopeGap()
    {
        // ApplySlantTracking (the capture hook) no-ops entirely for AVT -- a documented, real scope gap
        // (see InitializeSlant's own doc comment), not a silent one. This test proves the gap is real
        // (Count stays 0), not that it's desirable.
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);
        var modeDetected = false;
        var lineDecoded = false;
        decoder.ModeDetected += _ => modeDetected = true;
        decoder.LineDecoded += _ => lineDecoded = true;
        decoder.PushSamples(samples.ToArray());

        // Round-1 code-review finding: an earlier version of this test asserted only Count==0, which
        // would pass vacuously if AVT detection itself regressed to "never locks" -- distinguish "AVT
        // decoded, but not captured" from "nothing decoded at all."
        Assert.True(modeDetected, "AVT never locked -- test setup problem, not a capture-gap proof.");
        Assert.True(lineDecoded, "AVT never decoded a line -- test setup problem, not a capture-gap proof.");
        Assert.NotNull(decoder.RxLineStagingBufferForTests);
        Assert.Equal(0, decoder.RxLineStagingBufferForTests!.Count);
    }

    [Fact]
    public void RxBufferMode_Extended_DisablesPeakPicking_ProducesDifferentOutputThanOn()
    {
        // Round-1 code-review finding: legacy's GetPictureLevel/GetPictureLevelDiff (Main.cpp:4058-4084)
        // gate the KSB peek-ahead comparison on `sys.m_UseRxBuff != 2` -- under Extended, EVERY mode's
        // picture-channel read is bare-only (PixelSampleReader's own `_neverPeakPicks`, already unit-
        // tested in isolation at the class level in PixelSampleReaderTests.cs). This test proves the
        // wiring actually reaches AnalogFmSstvDecoder's own PixelSampleReader construction, not just
        // that the class itself supports the flag. A gradient test image (real local contrast between
        // KSB-apart samples) on a non-Scottie-DX mode (which already never peak-picks, so wouldn't
        // distinguish this fix) must decode DIFFERENTLY under Extended than under On.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);

        var onDecoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);
        var extendedDecoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.Extended);

        IImageSource? onImage = null;
        IImageSource? extendedImage = null;
        onDecoder.LineDecoded += u => onImage = u.Image;
        extendedDecoder.LineDecoded += u => extendedImage = u.Image;

        onDecoder.PushSamples(samples);
        extendedDecoder.PushSamples(samples);

        Assert.NotNull(onImage);
        Assert.NotNull(extendedImage);
        var anyPixelDiffers = false;
        for (var y = 0; y < onImage!.Height && !anyPixelDiffers; y++)
        {
            var onLine = onImage.GetScanline(y);
            var extendedLine = extendedImage!.GetScanline(y);
            for (var x = 0; x < onImage.Width; x++)
            {
                if (!onLine[x].Equals(extendedLine[x]))
                {
                    anyPixelDiffers = true;
                    break;
                }
            }
        }

        Assert.True(anyPixelDiffers, "Expected RxBufferMode.Extended's disabled peak-picking to produce at least one different pixel vs. On -- if this fails, the Extended->neverPeakPicks wiring may have regressed.");
    }

    [Fact]
    public void FreshLock_ClearsAnyPriorCapture_NotUnboundedAccumulationAcrossImages()
    {
        // Two back-to-back real transmissions in one stream -- the second image's own lock
        // (InitializeSlant) must clear whatever the first image staged, matching legacy's own
        // `dp->m_wStgLine = 0` reset (Main.cpp:4958). Proven by observing Count actually DROP when the
        // second image's own capture starts, not just "stays high" (which unbounded accumulation would
        // also produce).
        var mode = SstvModeRegistry.Robot36;
        var firstImageSamples = EncodeRealTransmission(mode);
        var secondImageSamples = EncodeRealTransmission(mode);
        var combined = new float[firstImageSamples.Length + secondImageSamples.Length];
        Array.Copy(firstImageSamples, combined, firstImageSamples.Length);
        Array.Copy(secondImageSamples, 0, combined, firstImageSamples.Length, secondImageSamples.Length);

        var decoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);
        var modeDetectedCount = 0;
        var countAtSecondLock = -1;
        decoder.ModeDetected += _ =>
        {
            modeDetectedCount++;
            if (modeDetectedCount == 2)
            {
                countAtSecondLock = decoder.RxLineStagingBufferForTests?.Count ?? -1;
            }
        };

        const int chunkSize = 256;
        for (var offset = 0; offset < combined.Length && modeDetectedCount < 2; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, combined.Length - offset);
            decoder.PushSamples(combined.AsMemory(offset, length));
        }

        Assert.True(modeDetectedCount >= 2, "Never reached a second lock -- test setup problem.");
        // At the moment of the second lock (InitializeSlant just ran, right before this line's own
        // capture could add anything new), the buffer must already be empty or just barely started --
        // nowhere close to a whole first image's worth of samples.
        Assert.True(countAtSecondLock >= 0, "Second lock's capture count was never captured -- test setup problem.");
        Assert.True(countAtSecondLock < (int)(mode.LineDurationMs / 1000.0 * 11025 * 5), $"Expected the staging buffer to be freshly cleared at the second lock, not carrying over the first image's data -- count was {countAtSecondLock}.");
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode)
    {
        var width = mode.ImageWidth;
        var height = mode.ImageHeight;
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

        var image = new ArrayImageSource(width, height, pixels);
        var encoder = new AnalogFmSstvEncoder(11025);
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
}
