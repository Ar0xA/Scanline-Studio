using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>fsk_cwid.md §8.2/§10: <see cref="ISstvDecoder.AnchorLagSamples"/>'s contract, isolated
/// from B-P2's own capture/arm design per this project's own "chop into pieces, test each piece
/// before wiring" convention (CLAUDE.md §4) -- these tests exercise only the property itself, not
/// the CW-ID window that will be built on top of it.
///
/// No exact expected value is asserted for the real-audio cases -- the plan's own tolerance note
/// (§10: "tolerance = the SyncAnchorCorrector delta, sub-line") describes a small, mode-dependent
/// slant-correction residual, not a fixed constant this test could hardcode without duplicating
/// production logic. Instead: (1) a CHUNK-INVARIANCE check on the underlying corrected anchor
/// (<c>TotalSamplesReceived - AnchorLagSamples</c>, which recovers that anchor by construction --
/// the real thing under test is whether the ANCHOR ITSELF, not this arithmetic, is chunk-size
/// independent, the same property <c>BandpassCacheChunkInvarianceTests</c> already pins for the
/// lock anchor directly, extended here through this property's own getter/field-write path); and
/// (2) a SMALL-LAG bound -- the deferred anchor correction is stated as 3-4 transmission lines
/// (§8.2), so against a REALISTIC (production-chunk-sized) push, the lag must be a small multiple
/// of one line's own sample count, never comparable to the whole image. That "realistic" qualifier
/// is load-bearing, not incidental: a bulk single-call push (itself a legitimate, separately
/// supported mode, per <c>BandpassCacheChunkInvarianceTests</c>' own <c>int.MaxValue</c> case)
/// makes <c>TotalSamplesReceived</c> reflect the ENTIRE input the instant it's appended, regardless
/// of how far <c>TryProcessBuffer</c> has actually decoded within that same call -- so
/// <c>AnchorLagSamples</c> legitimately reports nearly the whole image as "lag" for a bulk push,
/// which is why the small-lag tests below push in <c>DecodeFromFileAsync</c>'s own real
/// production chunk size instead of one shot.</summary>
public class AnchorLagSamplesTests
{
    [Fact]
    public void AnalogFmSstvDecoder_FreshInstance_AnchorLagSamplesIsZero()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        Assert.Equal(0, decoder.AnchorLagSamples);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_ScottieOne_DerivedImageStartIsChunkInvariant()
    {
        var mode = SstvModeRegistry.ScottieS1;
        var samples = await EncodeFlatImageAsync(mode, 11025);

        var derivedImageStartWholeChunk = PushAndCaptureDerivedImageStart(samples, chunkSize: samples.Length);
        var derivedImageStartSmallChunks = PushAndCaptureDerivedImageStart(samples, chunkSize: 512);

        Assert.Equal(derivedImageStartWholeChunk, derivedImageStartSmallChunks);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_PdFamily_AnchorLagIsASmallMultipleOfOneLine_NotComparableToWholeImage()
    {
        // PD290 specifically (§8.2's own worked example): 308 transmission lines of ~940ms each --
        // an anchor-relative-origin bug would put the lag at ~1.7-3.75s (thousands of lines' worth
        // relative to ONE line), so this bound (10 lines, generous vs the stated 3-4) is exactly the
        // property that distinguishes a correct implementation from that already-diagnosed bug class.
        //
        // Pushed in SstvSessionService.DecodeFromFileAsync's own real ChunkSize (4096,
        // SstvSessionService.cs:2419), NOT one bulk PushSamples call: TotalSamplesReceived reflects
        // every sample appended to the raw buffer so far, REGARDLESS of decode progress within that
        // call (PushSamplesCore's own append loop runs to completion before TryProcessBuffer ever
        // starts) -- a bulk push (a legitimate, separately-tested mode elsewhere, e.g.
        // BandpassCacheChunkInvarianceTests) would make AnchorLagSamples report almost the WHOLE
        // image as "lag" purely because everything was already received, not because decode is
        // actually behind. The "few lines" claim only holds relative to how production actually
        // feeds this decoder.
        var mode = SstvModeRegistry.Pd290;
        var samples = await EncodeFlatImageAsync(mode, 11025);
        var oneLineSamples = mode.LineDurationMs / 1000.0 * 11025;

        var decoder = new AnalogFmSstvDecoder(11025);
        int? anchorLag = null;
        decoder.ModeDetected += _ => anchorLag = decoder.AnchorLagSamples;

        PushInProductionSizedChunks(decoder, samples);

        Assert.NotNull(anchorLag);
        Assert.InRange(anchorLag!.Value, 0, (int)(oneLineSamples * 10));
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_Avt_AnchorLagIsASmallMultipleOfOneLine()
    {
        // AVT's own Commit() finalizes immediately (no deferred TryResolveSyncAnchorCorrection step,
        // §8.2) -- still expected to report a small lag (bounded below by the assert's own 0 floor,
        // not asserted strictly positive) from header/sync alone, comparable to a few lines, never
        // the whole image. Same production-chunked push as the PD290 case above, for the same reason.
        var mode = SstvModeRegistry.Avt;
        var samples = await EncodeFlatImageAsync(mode, 11025);
        var oneLineSamples = mode.LineDurationMs / 1000.0 * 11025;

        var decoder = new AnalogFmSstvDecoder(11025);
        int? anchorLag = null;
        decoder.ModeDetected += _ => anchorLag = decoder.AnchorLagSamples;

        PushInProductionSizedChunks(decoder, samples);

        Assert.NotNull(anchorLag);
        Assert.InRange(anchorLag!.Value, 0, (int)(oneLineSamples * 10));
    }

    // Matches SstvSessionService.DecodeFromFileAsync's own real ChunkSize (SstvSessionService.cs:2419)
    // exactly, not an arbitrary test value -- see the PD290 test's own comment for why the chunk size
    // is load-bearing here, unlike the chunk-invariance test above.
    private const int ProductionFileDecodeChunkSize = 4096;

    private static void PushInProductionSizedChunks(AnalogFmSstvDecoder decoder, float[] samples)
    {
        for (var offset = 0; offset < samples.Length; offset += ProductionFileDecodeChunkSize)
        {
            var length = Math.Min(ProductionFileDecodeChunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }
    }

    [Fact]
    public void RestartableSstvDecoder_FreshInstance_AnchorLagSamplesIsZero()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true);
        Assert.Equal(0, decoder.AnchorLagSamples);
    }

    [Fact]
    public async Task RestartableSstvDecoder_ForwardsTheLiveInnersAnchorLagSamples_AcrossASwap()
    {
        // Same swap-forcing technique as ReceptionSequenceTests' own swap test -- proves this
        // property is forwarded from whichever inner is CURRENT (unlike ReceptionSequence, which is
        // the wrapper's own counter), not stuck at a value from a since-replaced inner.
        //
        // Deliberately uses TWO DIFFERENT modes (Robot36 then MartinM1, different VIS header and
        // LineDurationMs) rather than the same mode twice: reusing the same mode for both receptions
        // makes the two real anchor-lag values coincidentally equal, which would make a mutation like
        // "always return the FIRST inner's cached value" pass this test undetected. The independent
        // reference decoder below is the actual assertion; the not-equal check is only a coarse
        // sanity backstop.
        //
        // Reads AnchorLagSamples directly after each PushSamples call returns, NOT via a subscribed
        // ModeDetected handler left in place across both receptions -- PushSamples is synchronous, so
        // the value is identical to what a callback would have seen, and this avoids a real mistake
        // found while authoring this test: a handler subscribed for the first reception and never
        // unsubscribed also fires (and overwrites its own captured variable) on the SECOND
        // reception's ModeDetected, making the "first" and "second" values spuriously identical
        // regardless of whether forwarding is actually correct.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 500_000, criticalThresholdSamples: 5_000_000);
        var firstMode = SstvModeRegistry.Robot36;
        var firstSamples = await EncodeFlatImageAsync(firstMode, RestartableSstvDecoder.ProductionSampleRate);

        decoder.PushSamples(firstSamples);
        var firstAnchorLag = decoder.AnchorLagSamples;

        decoder.PushSamples(new float[150_000]);
        decoder.PushSamples(new float[1]);
        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap actually happened

        var secondMode = SstvModeRegistry.MartinM1;
        var secondSamples = await EncodeFlatImageAsync(secondMode, RestartableSstvDecoder.ProductionSampleRate);

        decoder.PushSamples(secondSamples);
        var secondAnchorLagViaWrapper = decoder.AnchorLagSamples;

        // Independent reference: the SAME second-reception audio, pushed the SAME way (one call),
        // through a FRESH raw AnalogFmSstvDecoder with no wrapper involved at all.
        var referenceDecoder = new AnalogFmSstvDecoder(RestartableSstvDecoder.ProductionSampleRate);
        referenceDecoder.PushSamples(secondSamples);
        var referenceAnchorLag = referenceDecoder.AnchorLagSamples;

        Assert.NotEqual(firstAnchorLag, secondAnchorLagViaWrapper);
        Assert.Equal(referenceAnchorLag, secondAnchorLagViaWrapper);
    }

    /// <summary>Pushes <paramref name="samples"/> in fixed-size chunks and returns
    /// <c>TotalSamplesReceived - AnchorLagSamples</c> as read inside <c>ModeDetected</c> -- the
    /// absolute pushed-sample index of this reception's true image start, in this decoder
    /// instance's own coordinate space.</summary>
    private static int PushAndCaptureDerivedImageStart(float[] samples, int chunkSize)
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        int? derivedImageStart = null;
        decoder.ModeDetected += _ => derivedImageStart = decoder.TotalSamplesReceived - decoder.AnchorLagSamples;

        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.NotNull(derivedImageStart);
        return derivedImageStart!.Value;
    }

    private static async Task<float[]> EncodeFlatImageAsync(SstvModeDefinition mode, int sampleRate)
    {
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, CreateFlatImage(mode)))
        {
            samples.Add(sample);
        }

        return [.. samples];
    }

    private static ArrayImageSource CreateFlatImage(SstvModeDefinition mode)
    {
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        return new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
    }
}
