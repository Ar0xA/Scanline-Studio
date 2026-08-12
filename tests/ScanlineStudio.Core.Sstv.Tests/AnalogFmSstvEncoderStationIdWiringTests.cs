using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Phase 4 (CW-ID/FSK station-ID subsystem): <see cref="AnalogFmSstvEncoder"/>'s real TX pipeline
/// wiring -- the post-image FSK-ID/CW-ID append hook inside <c>GenerateFrequencySegments</c> and the
/// footer-tone-branch selection (<c>GenerateFooterSegments</c>'s <c>fskIdEnabled</c> parameter).
/// Uses the SAME real encode/decode entry points production code uses (<c>EncodeAsync</c> /
/// <see cref="AnalogFmSstvDecoder.PushSamples"/>), not a segment-level shortcut -- proves the full
/// image-then-station-ID sequence works end to end through the real per-mode segment generator,
/// which Phase 3's own station-ID tests (<c>StationIdDoesNotAbortScanTests</c>) deliberately did NOT
/// exercise (those built station-ID-only audio directly via <c>FskStationIdEncoder.Generate</c> +
/// <c>RenderSegments</c>, bypassing <c>GenerateFrequencySegments</c> entirely).
/// </summary>
public class AnalogFmSstvEncoderStationIdWiringTests
{
    private const int SampleRate = 11025;

    /// <summary>Pads a clip with trailing silence -- required for ANY post-<c>EndOfImage</c> narrow-
    /// FSK scan to find anything at all, not station-ID-specific: <c>EndOfImage</c> resets
    /// <c>_fixedWindowExhausted</c>/<c>_consumedSamples</c>/<c>_visLockProcessedUpTo</c>/
    /// <c>_syncBypassProcessedUpTo</c> to a fresh "just like a cold-start decoder" state
    /// (<c>resumeFrom</c> = end-of-image + 0.5s dead time), so <c>TryInterleavedHeaderScan</c>'s own
    /// one-shot race-safety gate (Band-1 S3 fix, see that method's doc comment) holds
    /// <c>TryNarrowFskScan</c>'s <c>scanBound</c> at <c>_consumedSamples</c> (i.e. finds nothing new
    /// at all) until <c>VisHeader.MaxSearchCeilingMs</c> (1305ms) worth of samples have arrived past
    /// that point -- confirmed by direct diagnosis (an earlier draft of these tests failed with zero
    /// station-ID events despite a correct image decode), same underlying property Phase 3's own
    /// <c>StationIdDoesNotAbortScanTests.PadPastFixedWindowCeiling</c> already documented for the
    /// pre-first-image case; this is that same gate re-arming after every <c>EndOfImage</c>, not a
    /// new or different mechanism.</summary>
    private static float[] PadPastFixedWindowCeiling(IEnumerable<float> samples)
    {
        var padded = samples.ToList();
        padded.AddRange(new float[SampleRate * 2]); // 2s trailing silence, comfortably > 0.5s dead time + 1305ms
        return padded.ToArray();
    }

    [Fact]
    public async Task EncodeAsync_NoStationIdOptions_ProducesIdenticalOutputToExplicitNone()
    {
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var withoutParam = await CollectAsync(encoder.EncodeAsync(mode, image));
        var withExplicitNone = await CollectAsync(encoder.EncodeAsync(mode, image, StationIdTransmitOptions.None));

        Assert.Equal(withoutParam, withExplicitNone);
    }

    [Fact]
    public async Task EncodeAsync_FskIdEnabled_ImageStillDecodesAndStationIdDecodesWithNormalizedCallsign()
    {
        // Deliberately messy raw callsign (lowercase, padded) to prove the settings-boundary
        // normalization (StationIdCallsignNormalizer.Normalize, Option.cpp:445-448)
        // actually runs on this real path, not just in a unit test of the helper in isolation.
        var mode = SstvModeRegistry.Mn73; // narrow -- also exercises the narrow-mode footer branch
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var stationId = new StationIdTransmitOptions { FskIdEnabled = true, Callsign = "  w1aw  " };

        var samples = PadPastFixedWindowCeiling(await CollectAsync(encoder.EncodeAsync(mode, image, stationId)));

        var decoder = new AnalogFmSstvDecoder(SampleRate) { StationIdDecodeEnabled = true };
        SstvModeDefinition? detectedMode = null;
        var stationIdEvents = new List<FskStationIdDecodedInfo>();
        decoder.ModeDetected += m => detectedMode = m;
        decoder.StationIdDecoded += info => stationIdEvents.Add(info);

        decoder.PushSamples(samples);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.Single(stationIdEvents);
        Assert.Equal("W1AW", stationIdEvents[0].Callsign);
    }

    [Fact]
    public async Task EncodeAsync_CallsignPathologicallyPadded_TruncatesBeforeTrimming_MatchingLegacyOrderExactly()
    {
        // Auditor-confirmed (round 1): StationIdCallsignNormalizer.Normalize's order is truncate(16) THEN
        // uppercase THEN trim -- a faithful port of Option.cpp:445-448's StrCopy(n=16) -> jstrupr ->
        // clipsp/SkipSpace order, NOT trim-then-truncate. This is the one input shape that actually
        // distinguishes the two orders: 20 leading spaces + "W1AW" (24 raw chars). Truncate-first
        // takes the first 16 chars -- all spaces, "W1AW" never survives -- trimming that down to an
        // EMPTY callsign, so no FSK-ID packet is sent at all (matches legacy exactly). Trim-first
        // would have produced "W1AW" instead -- a materially different transmission for the same
        // configured input, which is exactly the bug this ordering exists to avoid.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var stationId = new StationIdTransmitOptions { FskIdEnabled = true, Callsign = new string(' ', 20) + "W1AW" };

        var withPathologicalCallsign = await CollectAsync(encoder.EncodeAsync(mode, image, stationId));
        var withNoStationId = await CollectAsync(encoder.EncodeAsync(mode, image));

        // Footer-branch selection alone (FskIdEnabled=true) still changes the footer shape, so this
        // can't be a byte-identical comparison against "no station ID at all" -- but if the callsign
        // normalized to a non-empty string, real FSK-ID segments (guard tone, start-bit pulse, 6-bit
        // tone-per-byte) would follow the footer, making this MUCH longer than the plain-footer-only
        // case. Equal length here is strong evidence no packet was emitted (empty-callsign no-op).
        var withEmptyFskFooterOnly = await CollectAsync(encoder.EncodeAsync(mode, image, StationIdTransmitOptions.None with { FskIdEnabled = true }));
        Assert.Equal(withEmptyFskFooterOnly.Count, withPathologicalCallsign.Count);
    }

    [Fact]
    public async Task EncodeAsync_CwIdEnabled_AppendsAudibleTailAfterFooter_LeavesEverythingBeforeItUnchanged()
    {
        // A coarse but real assertion: CW-ID audio is really appended to the encoded stream when
        // enabled, adding real, non-silent duration -- not just a settings pass-through with no
        // actual effect. Precise Morse-shape fidelity is already covered by CwMorseGeneratorTests
        // (Phase 1); this only proves AnalogFmSstvEncoder actually calls it, and calls it AFTER
        // (not instead of) everything that came before -- footer-tone selection is independent of
        // CwEnabled (only FskIdEnabled affects it), so the image+footer prefix must be byte-identical.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var withoutCw = await CollectAsync(encoder.EncodeAsync(mode, image));
        var withCw = await CollectAsync(encoder.EncodeAsync(mode, image,
            new StationIdTransmitOptions { CwEnabled = true, CwResolvedText = "TEST", CwToneFrequencyHz = 1000, CwWpm = 28 }));

        Assert.True(withCw.Count > withoutCw.Count, "CW-ID enabled should add real samples after the footer.");
        Assert.Equal(withoutCw, withCw.Take(withoutCw.Count));

        var tail = withCw.Skip(withoutCw.Count).ToArray();
        Assert.Contains(tail, s => Math.Abs(s) > 0.01f);
    }

    [Fact]
    public async Task EncodeAsync_FskAndCwBothEnabled_BothDecodeIndependently()
    {
        // Main.cpp:7018-7025: OutputFSKID() called before OutputCWID() when both are configured --
        // not mutually exclusive. This only asserts the FSK-ID half decodes correctly with CW-ID
        // also enabled (proving the two don't interfere); CW-ID audio's own presence is covered by
        // EncodeAsync_CwIdEnabled_AppendsAudibleTailAfterFooter above.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            CwEnabled = true,
            CwResolvedText = "TEST",
            CwToneFrequencyHz = 1000,
            CwWpm = 28,
        };

        var samples = PadPastFixedWindowCeiling(await CollectAsync(encoder.EncodeAsync(mode, image, stationId)));

        var decoder = new AnalogFmSstvDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var stationIdEvents = new List<FskStationIdDecodedInfo>();
        decoder.StationIdDecoded += info => stationIdEvents.Add(info);
        decoder.PushSamples(samples);

        Assert.Single(stationIdEvents);
        Assert.Equal("W1AW", stationIdEvents[0].Callsign);
    }

    [Fact]
    public async Task EncodeAsync_NrRstTextLongerThanWireLimit_IsCappedNotSentUnbounded()
    {
        // FskStationIdWireFormat.MaxNrStringLength = 8; a raw NR/RST field longer than 3 (RST
        // digits) + 8 must be capped at the settings boundary
        // (AnalogFmSstvEncoder.CapNrRstTextForStationId), not silently produce a packet longer than
        // any compliant receiver (legacy or this port) can decode. "59912345678901234" filters down
        // to an all-digit remainder far longer than the wire format allows if left uncapped.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            NrRstText = "59912345678901234",
        };

        var samples = PadPastFixedWindowCeiling(await CollectAsync(encoder.EncodeAsync(mode, image, stationId)));

        var decoder = new AnalogFmSstvDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var stationIdEvents = new List<FskStationIdDecodedInfo>();
        decoder.StationIdDecoded += info => stationIdEvents.Add(info);
        decoder.PushSamples(samples);

        // Two-stage commit (matches NarrowFskHeaderDecoderStationIdTests' own established shape):
        // the callsign commits and fires first, the NR/RST sub-packet commits separately afterward
        // -- NOT one combined event. Both firing at all (rather than a resync/desync producing
        // fewer/malformed events) is what proves the cap worked -- if it didn't apply, a receiver
        // would see a string past its own 9-char abort threshold and resync, silently losing the
        // callsign too.
        Assert.Equal(2, stationIdEvents.Count);
        Assert.Equal("W1AW", stationIdEvents[0].Callsign);
        Assert.Null(stationIdEvents[1].CompactNr);
        Assert.Equal("12345678", stationIdEvents[1].NrText);
    }

    [Fact]
    public async Task EncodeAsync_NrRstTextWithSeparators_CapsAfterFilteringNotBeforeIt()
    {
        // Auditor code-review finding on Phase 4 (real bug, fixed): capping the RAW text's length
        // before filtering is only a SAFE bound, not an EXACT one -- separators (space, '-', '.',
        // '/') are all below '0' and get REMOVED by FskStationIdWireFormat.FilterNrRstChars, so raw
        // length is not a safe proxy for filtered length. "5 9 9 0 0 1 2" (13 raw chars) filters to
        // "5990012" -- skip the first 3 (RST digits) leaves remainder "0012" (l=4, d=12 <1000), which
        // legacy's own 5-condition predicate (Main.cpp:6940) sends as the STRING form. The OLD (buggy)
        // raw-length cap at 11 chars instead capped to "5 9 9 0 0 1" -> filtered "599001" -> remainder
        // "001" (l=3 <4) -> WRONGLY compact-eligible, sending compact NR=1 -- a different number on
        // the air than legacy would have sent for the same configured exchange.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            NrRstText = "5 9 9 0 0 1 2",
        };

        var samples = PadPastFixedWindowCeiling(await CollectAsync(encoder.EncodeAsync(mode, image, stationId)));

        var decoder = new AnalogFmSstvDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var stationIdEvents = new List<FskStationIdDecodedInfo>();
        decoder.StationIdDecoded += info => stationIdEvents.Add(info);
        decoder.PushSamples(samples);

        Assert.Equal(2, stationIdEvents.Count);
        Assert.Equal("W1AW", stationIdEvents[0].Callsign);
        Assert.Null(stationIdEvents[1].CompactNr);
        Assert.Equal("0012", stationIdEvents[1].NrText);
    }

    private static async Task<List<float>> CollectAsync(IAsyncEnumerable<float> samples)
    {
        var list = new List<float>();
        await foreach (var sample in samples)
        {
            list.Add(sample);
        }

        return list;
    }

    private static ArrayImageSource CreateSolidImage(int width, int height) =>
        new(width, height, Enumerable.Repeat(new Rgb24(128, 64, 200), width * height).ToArray());
}
