using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// End-to-end proof of piece 6d: the actual mid-reception abandon-and-restart scenario piece 6c
/// wires up (legacy's case-0 trigger, `sstv.cpp:1946-1950`, carries no `!m_Sync` guard on the
/// transition itself -- keeps running even while already locked, at legacy's own shipped defaults;
/// see <see cref="AnalogFmSstvDecoder"/>'s piece-6c comment for the full `m_SyncRestart` nuance,
/// found by round-2 review). A first transmission's audio is truncated partway through its own image body
/// (simulating a real station cutting out, or a second station overriding it), immediately followed
/// by a second, complete transmission -- no footer, no gap, just a hard cut mid-image into a new
/// header. This is the scenario <see cref="EndOfImageResetTests"/> does not cover: that test's two
/// transmissions are both complete, so <see cref="AnalogFmSstvDecoder.EndOfImage"/> (piece 6a) is
/// what resolves it, not the mid-reception restart path (piece 6c) this test targets specifically.
/// </summary>
public class MidReceptionRestartTests
{
    [Fact]
    public async Task TruncatedFirstTransmission_AbandonsAndDecodesTheSecond()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage1 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 0);
        var sourceImage2 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 64);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples1 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage1))
        {
            samples1.Add(sample);
        }

        // Truncate well past the header (~910ms) but well before the image completes -- lands
        // partway through the image body, matching a real cut/override mid-reception.
        var truncatedSamples1 = samples1.Take(samples1.Count * 3 / 10).ToArray();

        var samples2 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage2))
        {
            samples2.Add(sample);
        }

        var combined = truncatedSamples1.Concat(samples2).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        var decodedImages = new List<IImageSource>();
        var restartCount = 0;
        decoder.ModeDetected += m =>
        {
            detectedModes.Add(m);
            decodedImages.Add(null!);
        };
        decoder.DecodeRestarted += _ => restartCount++;
        decoder.LineDecoded += update => decodedImages[^1] = update.Image;

        decoder.PushSamples(combined);

        Assert.Equal(2, detectedModes.Count);
        Assert.Equal(mode.Id, detectedModes[0].Id);
        Assert.Equal(mode.Id, detectedModes[1].Id);
        Assert.Equal(1, restartCount);

        // Measured, not assumed: 11.84 (re-measured after piece 7c; was 9.81 before it). Resolved via
        // VisLockStateMachine's own anchor -- as of 7c, that anchor's own tolerance grew from 10.0 to
        // 13.0 (see VisLockStateMachineDecoderTests' doc comment for why: CLVL's AGC now correctly
        // hard-clips a full-amplitude tone, adding real, legacy-faithful noise to the exact trigger
        // sample). 14.0 gives the same kind of headroom here, not a new imprecision introduced by
        // piece 6c/6d specifically.
        var delta = MeasureDelta(sourceImage2, decodedImages[1]);
        Assert.True(delta <= 14.0, $"Second (real) transmission average per-channel delta {delta:F2} exceeded tolerance.");
    }

    [Fact]
    public async Task TruncatedFirstTransmission_WithSyncRestartDisabled_NeverAbandonsOrRestarts()
    {
        // Same scenario as TruncatedFirstTransmission_AbandonsAndDecodesTheSecond above, but with the
        // new SyncRestart toggle (port of legacy's real m_SyncRestart, sstv.cpp:1486, default 1) set
        // to false -- the mid-reception restart trigger this test's sibling exercises (TryVisLockState
        // Machine's own MID-reception, still-locked scan) must not run at all. The first (truncated)
        // image is instead decoded straight through as (corrupted) garbage past the real truncation
        // point until its own ImageHeight is reached normally -- no abandonment, so DecodeRestarted
        // must never fire. Once that first image completes and _mode goes null again, ordinary
        // pre-lock header detection is free to find the second transmission's still-undetected real
        // header in whatever buffer remains -- so a second ModeDetected is still expected; the
        // discriminating assertion is restartCount, not how many ModeDetected events fire.
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage1 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 0);
        var sourceImage2 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 64);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples1 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage1))
        {
            samples1.Add(sample);
        }

        var truncatedSamples1 = samples1.Take(samples1.Count * 3 / 10).ToArray();

        var samples2 = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage2))
        {
            samples2.Add(sample);
        }

        var combined = truncatedSamples1.Concat(samples2).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate, syncRestartEnabled: false);
        var detectedModeCount = 0;
        var restartCount = 0;
        decoder.ModeDetected += _ => detectedModeCount++;
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(combined);

        Assert.Equal(0, restartCount); // the discriminating assertion -- see this test's own doc comment
        Assert.True(detectedModeCount >= 1, "Never even detected the first transmission -- test setup problem.");
    }

    // Shared by the two mid-reception station-ID tests below. MUST stay a WIDE mode (MartinM1):
    // BandpassFilteredSampleAt's `useLocked` gate is forced false whenever `_mode.NarrowModeCode is
    // not null` (narrow modes always run H2/search), so a narrow mode would silently stop exercising
    // the H1 (locked bandpass) path these tests exist to cover.
    private static async Task<(bool LockedBeforeBurst, int ImageHeight, List<string> Callsigns,
        List<uint?> CompactNrs, List<string?> NrTexts, List<int> EventLines, List<SstvModeDefinition> ModeDetected,
        List<SstvModeDefinition> Restarts)> RunMidReceptionStationIdScenarioAsync(
        bool syncRestartEnabled, double splitFraction, string? nrRst)
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 0);

        var encoder = new AnalogFmSstvEncoder(11025);
        var imageSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            imageSamples.Add(sample);
        }

        var splitPoint = (int)(imageSamples.Count * splitFraction);
        var before = imageSamples.Take(splitPoint).ToArray();
        var after = imageSamples.Skip(splitPoint).ToArray();

        var fskSegments = FskStationIdEncoder.Generate("W1AW", nrRst);
        var fskBurst = AnalogFmSstvEncoder.RenderSegments(fskSegments, 11025).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate, syncRestartEnabled: syncRestartEnabled) { StationIdDecodeEnabled = true };
        var callsigns = new List<string>();
        var compactNrs = new List<uint?>();
        var nrTexts = new List<string?>();
        var eventLines = new List<int>();
        var modeDetected = new List<SstvModeDefinition>();
        var restarts = new List<SstvModeDefinition>();
        var lastLine = -1;

        decoder.LineDecoded += update => lastLine = update.Line;
        decoder.StationIdDecoded += info =>
        {
            callsigns.Add(info.Callsign ?? "(null)");
            compactNrs.Add(info.CompactNr);
            nrTexts.Add(info.NrText);
            eventLines.Add(lastLine);
        };
        decoder.ModeDetected += m => modeDetected.Add(m);
        decoder.DecodeRestarted += m => restarts.Add(m);

        const int chunkSize = 512;
        void Feed(float[] samples)
        {
            for (var i = 0; i < samples.Length; i += chunkSize)
            {
                var len = Math.Min(chunkSize, samples.Length - i);
                decoder.PushSamples(samples.AsSpan(i, len).ToArray());
            }
        }

        Feed(before);
        var lockedBeforeBurst = decoder.FirstLockedBandpassIndex is not null;
        Feed(fskBurst);
        Feed(after);
        Feed(new float[11025 * 2]); // trailing silence so any in-flight scan can finish

        return (lockedBeforeBurst, mode.ImageHeight, callsigns, compactNrs, nrTexts, eventLines, modeDetected, restarts);
    }

    // Burst duration is ~1.15s (100ms guard + 22ms start bit + ~7 bytes x 6 bits x 22ms + 100ms
    // guard) -- roughly 8 MartinM1 transmission lines. A genuinely mid-reception delivery must land
    // within a handful of lines of the splice point, not at/near ImageHeight-1 (which is exactly what
    // a regressed "only the post-EndOfImage scan still delivers it" case would produce, since
    // LineDecoded's own last-observed line is never reset by this harness).
    private static int MaxExpectedStationIdEventLine(int imageHeight, double splitFraction) =>
        (int)(imageHeight * splitFraction) + 30;

    [Theory]
    [InlineData(false, 0.10)]
    [InlineData(false, 0.50)]
    [InlineData(true, 0.10)]
    [InlineData(true, 0.50)]
    public async Task CallsignOnlyBurst_FiresStationIdDecoded_GenuinelyMidImage_NeverRestarts(bool syncRestartEnabled, double splitFraction)
    {
        var result = await RunMidReceptionStationIdScenarioAsync(syncRestartEnabled, splitFraction, nrRst: null);
        var maxExpectedLine = MaxExpectedStationIdEventLine(result.ImageHeight, splitFraction);

        Assert.True(result.LockedBeforeBurst, "Setup problem: never locked before splicing the burst in.");
        Assert.Single(result.Callsigns);
        Assert.Equal("W1AW", result.Callsigns[0]);
        Assert.InRange(result.EventLines[0], 0, maxExpectedLine);
        Assert.Empty(result.Restarts);
        Assert.Single(result.ModeDetected); // exactly the original lock, no spurious re-lock
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NrRstSubPacketBurst_FiresBothEvents_GenuinelyMidImage(bool syncRestartEnabled)
    {
        const double splitFraction = 0.50;
        var result = await RunMidReceptionStationIdScenarioAsync(syncRestartEnabled, splitFraction, nrRst: "599123");
        var maxExpectedLine = MaxExpectedStationIdEventLine(result.ImageHeight, splitFraction);

        Assert.True(result.LockedBeforeBurst);
        // Matches AnalogFmSstvEncoderStationIdWiringTests' own established shape: two SEPARATE
        // commits (callsign, then NR), not one combined event.
        Assert.Equal(2, result.Callsigns.Count);
        Assert.Equal("W1AW", result.Callsigns[0]);
        Assert.Null(result.CompactNrs[0]);
        Assert.Equal("(null)", result.Callsigns[1]);
        Assert.Equal(123u, result.CompactNrs[1]);
        Assert.Null(result.NrTexts[1]);
        Assert.All(result.EventLines, line => Assert.InRange(line, 0, maxExpectedLine));
        Assert.Empty(result.Restarts);
        Assert.Single(result.ModeDetected);
    }

    // D0-audit round-13, 2026-08-26: round 11/12's finding above (narrow-FSK "never delivers a
    // StationIdDecoded result while genuinely locked mid-reception... in either [syncRestartEnabled]
    // setting") does NOT reproduce. Re-investigated from scratch: a station-ID FSK burst spliced
    // into a locked MartinM1 reception (both callsign-only and the NR/RST two-event sub-packet form,
    // both syncRestartEnabled settings, splice points at 10% and 50% through the image body) fires
    // StationIdDecoded correctly every time -- see CallsignOnlyBurst_FiresStationIdDecoded_
    // GenuinelyMidImage_NeverRestarts/NrRstSubPacketBurst_FiresBothEvents_GenuinelyMidImage below,
    // added as this investigation's own permanent regression coverage (the ORIGINAL gap round 12
    // named -- "this exact scenario has no test coverage anywhere in the suite" -- was real and is
    // now closed, independent of whether the decode failure itself ever was). Both tests assert a
    // TIGHT, splice-proportional upper bound on which decoded image line the event lands on (not a
    // trivial 0..ImageHeight-1 range, which a regressed "only the post-EndOfImage scan still
    // delivers it" case would still satisfy) -- proving genuine mid-image delivery, not just "an
    // event eventually fired somewhere in the pushed buffer."
    //
    // Cross-checked against legacy ground truth, not assumed: `yoniq-old/YONIQ-main/Sound.cpp:356,364`
    // (`SSTVDEM.Do(*lp)`, the real-time audio callback) feeds every sample through the decoder
    // unconditionally, image-in-progress or not; `sstv.cpp:1858` (`DecodeFSK(int(d19), int(dsp))`)
    // runs unconditionally on that same per-sample call, BEFORE the `!m_Sync||...` sync-state branch
    // even starts; the station-ID commit at `sstv.cpp:2483-2495` has no `m_Sync`/`m_SyncRestart` gate
    // at all (unlike the mode-announce commit at `:2592`). So legacy requires mid-reception FSK-ID
    // delivery to work, and this port's own call graph (`AnalogFmSstvDecoder.TryNarrowFskScan`,
    // hoisted unconditional per decoded line since round 11; the station-ID branch inside it fires
    // via the event with no `Commit()`/lock-state gate at all, only the separate mode-announce match
    // is gated) already matches that -- confirmed correct by direct code trace, not inferred from the
    // tests passing. Numerically verified too: H1 (locked) and H2 (search) bandpass filters have
    // near-identical gain at both FSK tone frequencies (1900/2100Hz, both ~0dB via the real
    // `SearchBandpassFilter.MakeFilter` coefficients), ruling out round 12's own filter-passband
    // theory as the (never real) mechanism.
    //
    // Most likely explanation for round 12's original false negative (unverifiable -- that round's
    // failing test was written and removed in the same commit, never itself committed): a decoder
    // constructed without `StationIdDecodeEnabled = true` would silently produce zero StationIdDecoded
    // events in EITHER syncRestartEnabled setting, matching the reported symptom exactly --
    // `StationIdDoesNotAbortScanTests.StationIdDecodeDisabled_TransmissionProducesNoEventAtAll`
    // already documents this exact trap existing elsewhere in this test suite. A hypothesis, not a
    // confirmed root cause -- flagged as such, not asserted as fact.
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

    private static double MeasureDelta(IImageSource expected, IImageSource actual)
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
}
