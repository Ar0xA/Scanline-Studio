using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Golden-vector tests per spec/13-testing.md's "Golden-vector testing (legacy parity)" section:
/// reference input/output pairs captured from a real, running legacy YONIQ/MMSSTV install (not
/// re-derived from source a second time), see Fixtures/GoldenVectors/README.md for exact capture
/// provenance. Every other test in this suite (<see cref="SstvRoundTripTests"/> and friends) is
/// self-consistency only -- this file is the first to check the C# port's decoder against actual
/// legacy binary *output*, closing the gap <see cref="SstvRoundTripTests"/>'s own doc comment names.
///
/// Deliberately test-only: nothing here changes <c>AnalogFmSstvDecoder</c>/<c>AnalogFmSstvEncoder</c>.
///
/// Deferred (not attempted here, per an Opus plan-review before implementation): a true
/// signal-domain check of the C# encoder's output against the real `.mmv` audio itself (e.g.
/// comparing instantaneous-frequency-over-time curves). Two reasons: (1) the `.mmv` capture is not
/// a clean tap of the modulator -- `Sound.cpp:334/370-395` shows the recording buffer is delayed by
/// exactly one audio buffer during TX and the final buffer is never written (also: outside the TX
/// window the file contains real recorded sound-card/mic input, not silence -- confirmed directly
/// against source, correcting an earlier, wrong "no soundcard loopback" assumption), so there's no
/// clean reference signal to align against sample-for-sample; (2) building an aligned
/// frequency-curve comparison would need access to decoder-internal state
/// (<c>_consumedSamples</c>/<c>_visLockOriginSample</c>) that's private today -- exposing it would
/// turn this "test-only" effort into a production-code change, which is explicitly out of scope for
/// this pass. <see cref="MmvFixture_TxRegionDuration_MatchesExpectedTransmissionTiming"/> and
/// <see cref="EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode"/> capture most of the
/// realistic value of an encoder check without either problem -- for martin-m1. Round-2-review
/// correction: this summary used to make that claim unqualified for both fixture modes, which
/// <see cref="EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode"/>'s own per-test comment
/// already contradicts for robot-36 (its corruption-floor finding: that test's robot-36 arm is a
/// regression tripwire only, not meaningfully discriminating, until the Robot-36-at-11025Hz DSP gap
/// documented on <see cref="Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource"/> is fixed) --
/// this summary should have said so too instead of leaving an unqualified claim standing at the
/// file's most-read location.
/// </summary>
public class GoldenVectorTests
{
    private const string FixtureDir = "Fixtures/GoldenVectors";

    public static readonly TheoryData<string, string, string, int> Fixtures = new()
    {
        // (mode id, source bmp, legacy RX bmp, picture height -- Robot 36's RX save is the full
        // 320x256 shared canvas per GetBitmapSize/GetPictureSize, sstv.cpp:607-653, confirmed
        // directly against the fixture: rows 0-239 are real content, 240-255 are white margin).
        { "robot-36", "robot36.bmp", "robot36_RX.bmp", 240 },
        { "martin-m1", "martin-m1.bmp", "martin-m1_RX.bmp", 256 },
        { "scottie-s1", "scottie-s1.bmp", "scottie-s1_RX.bmp", 256 },
        { "robot-72", "robot72.bmp", "robot72_RX.bmp", 240 },
        { "pd90", "pd90.bmp", "pd90_RX.bmp", 256 },
        { "rm8", "rm8.bmp", "rm8_RX.bmp", 240 },
        { "mn110", "mn110.bmp", "mn110_RX.bmp", 256 },
        { "avt", "avt.bmp", "avt_RX.bmp", 240 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void LegacyOwnDecode_MatchesSourceImage_EstablishesBaselineDelta(
        string modeId, string sourceBmp, string rxBmp, int pictureHeight)
    {
        // Zero C# DSP involved -- this measures legacy's OWN encode+decode round-trip on its own
        // hardware/software, which is the honest reference bar every other tolerance in this file
        // should be judged against, replacing an invented number. spec/14-roadmap.md never
        // previously answered "what delta does legacy's own decoder actually achieve on this input,"
        // per its own Phase-1-isn't-done note -- this is that answer, measured directly.
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var rx = CropToTop(BmpFile.Read(Path.Combine(FixtureDir, rxBmp)), pictureHeight);

        var delta = MeasureAveragePerChannelDelta(source, rx, pictureHeight);

        // Measured directly (not assumed): robot-36 = 6.99, martin-m1 = 1.57 -- both real, nonzero,
        // legacy's own decode is visibly imperfect even on a synthetic gradient with no analog
        // noise source (spot-checked manually: robot-36's left edge shows a genuine multi-pixel
        // transient in legacy's own decode, B ramping 23/76/95/116 into the true 128, not a step).
        // 15.0 is a generous upper bound (~2x the larger measured value) that still fails loudly on
        // a corrupted capture or a wrong crop, without pretending either measured number is a
        // pre-known target.
        Assert.True(delta < 15.0, $"[{modeId}] legacy-own-decode baseline delta {delta:F2} unexpectedly large -- check fixture/crop.");
    }

    private const string TxCaptureDir = "Fixtures/GoldenVectors/TxCapture";

    public static readonly TheoryData<string, string, string, int, bool> TxFixtures = new()
    {
        // (mode id, source bmp, legacy TX_RX bmp, picture height, row-doubled) -- the reverse
        // direction of Fixtures above: THIS PORT'S OWN encoder output, played back through a real
        // legacy install and decoded there (Fixtures/GoldenVectors/TxCapture/README.md). This is the
        // "TX-side verification tests" the milestone audit's own "Explicit prerequisite before
        // Phase 3" note (spec/14-roadmap.md) required before the chain/integration audit can run --
        // until now, TX only had internal round-trip coverage (this port's encoder decoded by this
        // port's own decoder agreeing with itself), exactly the failure shape CLAUDE.md's own Scottie
        // incident warns about (both halves can be wrong the same way).
        //
        // Source bmp names/picture heights reused verbatim from Fixtures/DecoderFixtures above for
        // the 8 modes that already had RX fixtures; scottie-dx/mr73/r24 are the 3 modes the milestone
        // audit flagged as having zero coverage anywhere (TxCapture/README.md's own "New source
        // images" note).
        { "robot-36", "robot36.bmp", "robot-36_TX_RX.bmp", 240, false },
        { "martin-m1", "martin-m1.bmp", "martin-m1_TX_RX.bmp", 256, false },
        { "scottie-s1", "scottie-s1.bmp", "scottie-s1_TX_RX.bmp", 256, false },
        { "robot-72", "robot72.bmp", "robot-72_TX_RX.bmp", 240, false },
        { "pd90", "pd90.bmp", "pd90_TX_RX.bmp", 256, false },
        { "rm8", "rm8.bmp", "rm8_TX_RX.bmp", 240, false },
        { "mn110", "mn110.bmp", "mn110_TX_RX.bmp", 256, false },
        { "avt", "avt.bmp", "avt_TX_RX.bmp", 240, false },
        { "scottie-dx", "scottie-dx.bmp", "scottie-dx_TX_RX.bmp", 256, false },
        { "mr73", "mr73.bmp", "mr73_TX_RX.bmp", 256, false },
        // R24 is genuinely 120 real transmitted rows, nearest-neighbor row-doubled by legacy's own
        // RX display (SstvModeRegistry.R24's own doc comment, Main.cpp:4160-4168 `R=y*2`) -- its
        // source bmp is 120 rows tall, but legacy's saved TX_RX bmp is the usual 256-row canvas with
        // each real row duplicated into 2 consecutive display rows. A plain CropToTop would compare
        // doubled rows against undoubled source rows and misalign by 2x -- rowDoubled=true selects
        // the even-indexed rows of the top 240 (0, 2, 4, ..., 238) instead, undoing the doubling
        // before comparing.
        { "r24", "r24.bmp", "r24_TX_RX.bmp", 120, true },
    };

    [Theory]
    [MemberData(nameof(TxFixtures))]
    public void LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage(
        string modeId, string sourceBmp, string txRxBmp, int pictureHeight, bool rowDoubled)
    {
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var rawRx = BmpFile.Read(Path.Combine(TxCaptureDir, txRxBmp));
        var rx = rowDoubled
            ? CropToTopEvenRows(rawRx, pictureHeight)
            : CropToTop(rawRx, pictureHeight);

        var delta = MeasureAveragePerChannelDelta(source, rx, pictureHeight);

        // Measured directly (not assumed), via the same temporary-zero-tolerance technique used
        // throughout this session: robot-36 3.30, martin-m1 1.53, scottie-s1 1.05, robot-72 3.21,
        // pd90 2.40, rm8 5.15, mn110 2.60, avt 0.86, scottie-dx 1.03, mr73 3.22, r24 3.70. All 11
        // restart-free, correct mode detected, and comfortably BELOW the RX-direction
        // LegacyOwnDecode_MatchesSourceImage_EstablishesBaselineDelta numbers for the same modes
        // (robot-36 6.99, martin-m1 1.57) despite going through this port's own encoder first -- real
        // evidence this port's TX output is a valid, accurately decodable transmission to a real
        // legacy receiver, not just internally self-consistent (the exact gap
        // spec/14-roadmap.md's "Explicit prerequisite before Phase 3" note flagged). Each tolerance
        // below is ~2x its own measured value (same margin style as the RX baseline's 15.0), still
        // comfortably under the ~42.67 corruption floor this gradient-image metric measures
        // elsewhere in this file.
        // Re-measured after MUST 4 (spec/14-roadmap.md's Phase 3 fix, the RX per-line cursor-rounding
        // fix): every value here is UNCHANGED from before that fix, exactly as predicted -- this test
        // exercises the TX-encode-then-REAL-legacy-decode path, and MUST 4 only touched this port's own
        // RX decoder's per-line cursor, not its encoder.
        var toleranceByModeId = new Dictionary<string, double>
        {
            ["robot-36"] = 8.0,
            ["martin-m1"] = 5.0,
            ["scottie-s1"] = 4.0,
            ["robot-72"] = 8.0,
            ["pd90"] = 6.0,
            ["rm8"] = 12.0,
            ["mn110"] = 7.0,
            ["avt"] = 4.0,
            ["scottie-dx"] = 4.0,
            ["mr73"] = 8.0,
            ["r24"] = 10.0,
        };
        var tolerance = toleranceByModeId[modeId];

        Assert.True(
            delta < tolerance,
            $"[{modeId}] real-legacy-decode-of-this-ports-TX delta {delta:F2} exceeded tolerance {tolerance}.");
    }

    public static readonly TheoryData<string, string, string, int> DecoderFixtures = new()
    {
        { "robot-36", "robot36.mmv", "robot36.bmp", 240 },
        { "martin-m1", "martin-m1.mmv", "martin-m1.bmp", 256 },
        { "scottie-s1", "scottie-s1.mmv", "scottie-s1.bmp", 256 },
        { "robot-72", "robot72.mmv", "robot72.bmp", 240 },
        { "pd90", "pd90.mmv", "pd90.bmp", 256 },
        { "rm8", "rm8.mmv", "rm8.bmp", 240 },
        { "mn110", "mn110.mmv", "mn110.bmp", 256 },
        // S31 fix (spec/14-roadmap.md): AVT now included here -- see this file's own
        // Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource tolerance comment for the root
        // cause and fix. Previously excluded because this port's decoder never locked onto avt.mmv
        // at all (0 ModeDetected events across the whole ~100s file), even though the raw audio
        // traced correctly against legacy's own expected AVT header sequence by hand.
        { "avt", "avt.mmv", "avt.bmp", 240 },
    };

    [Theory]
    [MemberData(nameof(DecoderFixtures))]
    public void Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource(
        string modeId, string mmvFile, string sourceBmp, int pictureHeight)
    {
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var (decoded, mode, restartCount) = DecodeMmvFixture(modeId, mmvFile);

        var actual = CropToTop(decoded, pictureHeight);
        var delta = MeasureAveragePerChannelDelta(source, actual, pictureHeight);

        // Re-measured after Piece 9 (spec/14-roadmap.md) narrowed PllFmDemodulator from 1100-2300Hz
        // to legacy's real 1500-2300Hz image-decode band, restarts=0 and the correct mode detected
        // first-try for both:
        //   martin-m1: 1.22 -- was 11.78 pre-piece-9 (already close to SstvRoundTripTests' own 10.0
        //     synthetic-self-round-trip tolerance); the tighter PLL band's better-settled per-pixel
        //     reads brought real-capture accuracy roughly in line with synthetic self-consistency.
        //   robot-36:  16.995 -- was 68.06 pre-piece-9, a ~4x improvement. The narrower-band-PLL
        //     hypothesis from spec/14-roadmap.md's re-verification plan (VCO gain change affecting
        //     line-start settling) turned out not to dominate here -- EVERY mode's round-trip delta
        //     improved after narrowing (measured directly, not assumed; see spec/14-roadmap.md's
        //     Piece 9 entry for the full per-mode table), consistent with the narrower band's real
        //     benefit (less out-of-band content bleeding into the tracked frequency range) winning
        //     out over the slower-reacquisition risk in practice, at least for these two fixtures'
        //     actual content and rates. This closes out the "5x-worse-than-synthetic" divergence a
        //     previous revision of this comment flagged as needing its own follow-up investigation --
        //     the PLL bandwidth mismatch WAS that investigation's target all along (Piece 9's own
        //     starting diagnosis), not a coincidence.
        //     Previous revision's "not meaningfully discriminating" framing for robot-36's tolerance
        //     no longer applies: the corruption-floor measurement it relied on (~42.67 for this exact
        //     source image under this exact metric -- a flat gray image, a horizontal mirror, a
        //     vertical flip, and an R<->B channel swap all score around there) is now comfortably
        //     ABOVE robot-36's real measured delta (16.995), not below it. Tightened from 75.0 to
        //     25.0 -- real margin over the measured value (comparable proportionally to martin-m1's
        //     own margin below), while staying safely under the corruption floor, so this bound is a
        //     genuine discriminating check again, not just a regression tripwire.
        // Re-measured again after the m_KSS/m_KS2S pixel-pitch fix (spec/14-roadmap.md's item 3,
        // GetPixelPitchTrimFactor): martin-m1 1.22 -> 1.438 (still well inside tolerance), robot-36
        // 16.995 -> 14.809 (improved). Both fixtures are group E (239/240 trim on both axes), so the
        // ~0.4% horizontal correction moved each fixture a small, opposite-signed amount -- expected
        // given it's a scale fix, not a settling-time fix like Piece 9's was.
        // Re-measured again after the Hilbert demodulator port (spec/14-roadmap.md, replacing
        // PllFmDemodulator with HilbertFmDemodulator as this port's main picture demodulator, matching
        // legacy's real compiled-in default): martin-m1 1.438 -> 1.312, robot-36 14.809 -> 14.307 --
        // both improved, consistent with Hilbert's faster/bounded settling transient vs. PLL's
        // undershoot-then-recover tail (measured directly, not assumed; see spec/14-roadmap.md's
        // Hilbert scoping-pass and implementation entries for the full derivation). Neither tolerance
        // needed to change.
        // Re-measured again after Piece A (spec/14-roadmap.md, legacy's always-on 2-tap
        // moving-average pre-filter, `d=(s+m_ad)*0.5`, `sstv.cpp:1824-1825`): martin-m1 1.312 ->
        // 1.263 (improved), robot-36 14.307 -> 14.476 (worsened slightly) -- both changes tiny (<0.2)
        // and expected: these fixtures are already-documented clean/low-noise real captures, so a
        // smoothing filter has little noise to remove here -- its real motivation is noise robustness
        // for the noisier real-world reception these fixtures don't exercise (see spec/14-roadmap.md's
        // bandpass-filter-chain scoping discussion). Neither tolerance needed to change.
        //
        // Task #7 (spec/14-roadmap.md): five new real-legacy-capture fixtures added, all decode with
        // restarts=0 and the correct mode detected first-try. Measured directly (not assumed):
        // scottie-s1 2.74, robot-72 13.46, pd90 1.99, rm8 13.76, mn110 12.79. Tolerances below give
        // each mode real headroom (proportionally similar margin to martin-m1/robot-36's own bounds
        // above) while staying well under the ~42.67 corruption floor this exact gradient-formula
        // source measures at (see robot-36/martin-m1's own corruption-floor note further down in this
        // file -- not independently re-measured per new mode, since all six fixtures share the same
        // gradient construction and comparable dimensions).
        //
        // Re-measured again after the milestone-audit MUST fix 3 (spec/14-roadmap.md, "Milestone
        // audit, Phase 1+2" -- the pixel-pitch trim accumulator drift affecting
        // RgbSequentialScanlineDecoder/RobotScanlineDecoder/YCbCrSequentialScanlineDecoder/
        // YCbCrLinePairedScanlineDecoder's own scan-segment boundary placement) together with MUST
        // fix 2 (TryNarrowFskScan interleaving, which can shift narrow-mode anchor precision):
        // martin-m1 1.263 -> 0.44 (big improvement), robot-36 14.476 -> 16.19 (worsened slightly,
        // same accepted-tradeoff category as Piece A's own robot-36 note above -- a real, honestly
        // recorded consequence of a genuine correctness fix, not chased to zero), scottie-s1 2.74 ->
        // 1.92 (improved), robot-72 13.46 -> 14.57 (worsened slightly, same category), pd90 1.99 ->
        // 0.96 (improved), rm8 13.76 -> 13.76 (UNCHANGED, exactly as predicted -- MonoAveragedPaired's
        // single scan segment per line is structurally immune to this fix, confirmed not just
        // assumed), mn110 12.79 -> 2.39 (big improvement -- NOT expected from fix 3 alone, since
        // MN110 is a "group C" mode with trim factor exactly 1.0, making fix 3 a numeric no-op for
        // it; plausibly attributable to fix 2's own improved narrow-FSK anchor precision instead, not
        // independently isolated), avt 5.78 (was 5.79, unchanged within measurement noise). No
        // tolerance changes needed -- every measured value stays comfortably within its existing
        // bound.
        // Re-measured after MUST 4 (spec/14-roadmap.md's Phase 3 fix: the RX per-line cursor no
        // longer accumulates rounding error line-to-line -- see AnalogFmSstvDecoder's
        // _idealLineStartSample doc comment). Biggest improvements land exactly on the modes
        // predicted to have the largest per-line rounding error at 11025Hz (confirming the fix, not
        // just passing): robot-36 16.19 -> 5.04, robot-72 14.57 -> 4.41, rm8 13.76 -> 4.17 (rm8 was
        // UNAFFECTED by MUST fix 3, since MonoAveragedPaired has only one scan segment per line -- but
        // MUST 4 lives in the shared per-line loop, not any per-family decoder, so it improves rm8
        // just as much as the others, confirming these are two genuinely different bugs). Smaller/
        // mixed changes on the modes with tiny predicted per-line error, an accepted tradeoff same
        // category as prior fixes' own cross-mode effects: martin-m1 0.44 -> 0.77 (worsened
        // slightly), scottie-s1 1.92 -> 0.45 (improved), pd90 0.96 -> 0.93 (improved), mn110 2.39 ->
        // 1.97 (improved), avt 5.78 -> 6.74 (worsened slightly). Tolerances tightened to ~2x each new
        // measured value (same margin style as before), still comfortably under the ~42.67 corruption
        // floor.
        var toleranceByModeId = new Dictionary<string, double>
        {
            ["martin-m1"] = 3.0,
            ["robot-36"] = 10.0,
            ["scottie-s1"] = 3.0,
            ["robot-72"] = 9.0,
            ["pd90"] = 3.0,
            ["rm8"] = 9.0,
            ["mn110"] = 5.0,
            ["avt"] = 14.0,
        };
        var tolerance = toleranceByModeId[modeId];

        Assert.Equal(0, restartCount);
        Assert.True(
            delta < tolerance,
            $"[{modeId}] decoder-vs-source delta {delta:F2} exceeded regression-guard tolerance {tolerance}. " +
            $"restarts={restartCount}, detected mode=[{mode.Id}]");
    }

    // Piece 2c-i: an independent, non-tautological timing check -- it verifies this port's timing
    // table against a real legacy BINARY's actual output, not against source a human read (unlike
    // SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming, which cross-checks against
    // CSSTVSET::GetTiming's *source*). This class's own doc comment (above) explains why a full
    // signal-domain comparison was deferred instead -- this timing check is deliberately coarse
    // (envelope amplitude only, not frequency content), which is exactly why it stays in-scope for
    // test-only code: no decoder/encoder internals needed.
    public static readonly TheoryData<string, string> MmvFiles = new()
    {
        { "robot-36", "robot36.mmv" },
        { "martin-m1", "martin-m1.mmv" },
        { "scottie-s1", "scottie-s1.mmv" },
        { "robot-72", "robot72.mmv" },
        { "pd90", "pd90.mmv" },
        { "rm8", "rm8.mmv" },
        { "mn110", "mn110.mmv" },
        { "avt", "avt.mmv" },
    };

    [Theory]
    [MemberData(nameof(MmvFiles))]
    public void MmvFixture_TxRegionDuration_MatchesExpectedTransmissionTiming(string modeId, string mmvFile)
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, mmvFile));
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        var (txStartSeconds, txEndSeconds) = MeasureTxRegion(samples, sampleRate);
        var measuredDurationSeconds = txEndSeconds - txStartSeconds;

        // VisHeader.PrefixDurationMs + NormalTailDurationMs = 910ms for a normal (non-narrow,
        // non-AVT) VIS header -- robot-36/martin-m1/robot-72/pd90/rm8 all use this. Scottie also
        // emits an extra 9ms/1200Hz pulse right after the VIS stop bit (Main.cpp:7576-7578,
        // VisHeader.ScottiePostVisPulseDurationMs). AVT repeats the whole VIS block 3x then appends
        // its own long training sequence (Main.cpp:7429/7563-7575, VisHeader.AvtVisRepeatCount/
        // AvtVisBlockDurationMs/AvtTrainingSequenceDurationMs). MN110 (narrow) replaces the VIS
        // header entirely with the FSK mode-announce packet (VisHeader.NarrowHeaderTotalDurationMs).
        // Task #7 (spec/14-roadmap.md): generalized from the original two-fixture version, which
        // only ever needed the plain 910ms case.
        var expectedHeaderSeconds = modeId switch
        {
            "scottie-s1" => (VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs + VisHeader.ScottiePostVisPulseDurationMs) / 1000.0,
            "mn110" => VisHeader.NarrowHeaderTotalDurationMs / 1000.0,
            "avt" => ((VisHeader.AvtVisRepeatCount * VisHeader.AvtVisBlockDurationMs) + VisHeader.AvtTrainingSequenceDurationMs) / 1000.0,
            _ => (VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs) / 1000.0,
        };

        // TMmsstv::OutHEAD (Main.cpp:7270-7292) writes 8x100ms leader tones (800ms) for every mode
        // except narrow, which writes only 4x100ms (400ms) -- confirmed directly against source
        // (both branches read at Main.cpp:7277-7282/7284-7292), not assumed from the original
        // two-fixture derivation (which never needed the narrow branch).
        var headSeconds = mode.NarrowModeCode is not null ? 0.4 : 0.8;

        // PD90/MN110 use YCbCrLinePaired's Y1-RY-BY-Y2 shape: one LineDurationMs "transmission
        // unit" covers 2 image rows (RowsPerTransmissionLine = 2), so the naive
        // LineDurationMs * ImageHeight (the original two-fixture formula, both RowsPerTransmissionLine
        // = 1 families) double-counts for these -- confirmed against AnalogFmSstvEncoder's own TX
        // loop (`y += lineEncoder.RowsPerTransmissionLine`), not re-derived independently here.
        var rowsPerTransmissionLine = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding).RowsPerTransmissionLine;
        var expectedBodySeconds = mode.LineDurationMs * (mode.ImageHeight / rowsPerTransmissionLine) / 1000.0;

        // Round-1-review finding: an earlier version of this test omitted two real, fixed-duration
        // legacy TX segments and misattributed the resulting ~1.7s gap to "envelope-detection
        // slop" -- wrong, and caught by re-deriving the envelope by hand (both edges are a single
        // 50ms window wide, not a ramp, so slop is bounded at about +-0.1s per edge, not 1.7s).
        // Confirmed directly against source instead: TMmsstv::OutHEAD (Main.cpp:7270-7292) writes
        // 8x100ms leader tones (800ms) unconditionally before the VIS header at legacy's shipped
        // defaults (sys.m_VOX==0, non-narrow) -- called from SendSSTV at Main.cpp:7393, right
        // before the VIS header itself. TMmsstv::SendSSTV's own footer (Main.cpp:6996-7009, same
        // shipped defaults, sys.m_TXFSKID==0 per Main.cpp:903) writes
        // WriteC(1500, min(SSTVSET.m_TW, SampFreq/2)) + 4x100ms (400ms).
        //
        // Round-2-review correction: the first version of this fix read m_TW as "one line's
        // duration in samples" for the TRANSMITTED mode -- wrong, caught by tracing SSTVSET.m_TW's
        // actual assignment. m_TW is set by CSSTVSET::SetSampFreq (sstv.cpp:655-1109, the RECEIVE
        // side), as `GetTiming(m_Mode) * m_SampFreq / 1000.0` -- m_Mode is the demodulator's
        // currently-selected mode, not the mode being transmitted. The TX-side equivalent is a
        // genuinely separate field, CSSTVSET::SetTxSampFreq's `m_TTW = GetTiming(m_TxMode) *
        // m_TxSampFreq / 1000.0` (sstv.cpp:1280-1285) -- SendSSTV's footer does not use it. So this
        // footer segment's length depends on whatever mode the RX side happens to be sitting on at
        // the moment TX finishes, not on the mode actually being sent -- confirmed empirically: both
        // fixtures measure the same ~425ms WriteC portion despite transmitting different modes
        // (150ms vs 446ms LineDurationMs), consistent with both captures' RX side sitting at
        // GetTiming's own `default:` case (smSCT1, 428.22ms, sstv.cpp:1275) during capture.
        //
        // Round-3-review correction: an earlier version of this comment called that RX-side state
        // "the demodulator's default/never-changed mode on a fresh run" -- not quite right, and not
        // a general invariant. SSTVSET.m_Mode is NOT constructor-fixed: on every ini load,
        // Main.cpp:1872-1873 reads the *persisted* TX mode (`Define/SSTVMode`) and immediately calls
        // `SSTVSET.SetMode()` with it, seeding the RX side from whatever TX mode was last saved --
        // which happens to have been Scottie 1 (smSCT1, matching CSSTVSET's own constructor default
        // at sstv.cpp:570) for these two specific captures, not because the value can never change.
        // Re-capturing after a session that saved a different persisted TX mode would change this
        // constant. Documented here as a fact about these two committed fixtures, not a property of
        // the .mmv format or of legacy in general -- modeled as a named constant rather than derived
        // from the transmitted mode, since it demonstrably isn't derived from that mode either way.
        const double receiveSideModeLineDurationMsForTheseFixtures = 428.22; // GetTiming's default: case (smSCT1)

        // Main.cpp:6994-7013 (SendSSTV's footer, confirmed directly against source this session):
        // `if(!sys.m_VOX && !SSTVSET.m_fTxNarrow) WriteC(1500,...)+4x100ms; else WriteC(1900,...)` --
        // the narrow branch has NO alternating-tone tail, only the trailing carrier. Both branches'
        // trailing-carrier length is min(SSTVSET.m_TW, SampFreq/2) -- SSTVSET.m_TW is the RECEIVE
        // side's currently-selected mode, not the transmitted one (see the round-2/3-review history
        // above), so it isn't derivable from the mode under test; only measurable per capture.
        //
        // Task #7 (spec/14-roadmap.md): measured directly for all five new normal/AVT fixtures --
        // scottie-s1 0.766s, robot-72 0.806s, pd90 0.709s, avt 0.813s, rm8 0.908s. All cluster
        // around the same ~428ms-RX-default hypothesis (0.828s) within envelope-window slop, same
        // as the original two fixtures, so kept on that shared formula rather than a fifth
        // independent constant per mode.
        //
        // mn110 is the one real exception, not folded into the formula: its measured residual is
        // only ~0.076s, and a direct look at the raw envelope (50ms windows over the file's last 3s)
        // shows a SHARP cutoff from full amplitude straight to noise floor with no extended trailing
        // tone at all -- i.e. this specific real capture appears to carry no measurable footer
        // carrier, not a slow/quiet one this test's window size just missed. Genuinely unexplained
        // (RX-side m_TW state during this one capture is unknown and unrecoverable after the fact) --
        // flagged here rather than silently forced to fit the same formula; see this row's own
        // tolerance below.
        var footerSeconds = modeId == "mn110"
            ? 0.0
            : (Math.Min(receiveSideModeLineDurationMsForTheseFixtures, 500.0) / 1000.0) + 0.4;
        var expectedTotalSeconds = headSeconds + expectedHeaderSeconds + expectedBodySeconds + footerSeconds;

        // Measured directly (not assumed), after also fixing MeasureTxRegion's own window-duration
        // unit mismatch (see that method's comment): robot-36 expected 38.5382s, measured 38.5325s
        // (delta 0.0057s); martin-m1 expected 116.8284s, measured 116.7970s (delta 0.0314s) -- both
        // sub-50ms residuals (envelope-window granularity), a very different picture from the
        // pre-review 1.7s-per-mode gap this test used to carry. Round-3-review note: these exact
        // numbers (and their sign) shift slightly whenever the underlying .mmv files are
        // re-trimmed/re-captured, since trimming re-phases the fixed-size envelope-window grid
        // against the TX region -- re-measure rather than assume these stay exact after any future
        // fixture change. 0.2s is comfortably above the larger residual either way: generous enough
        // to absorb real envelope-window granularity, while still tight enough to catch a real gross
        // timing error (wrong sample rate, wrong mode duration table entry, badly misdetected TX
        // region) -- a 25x tighter bound than the original 5.0s, which was wide enough to accept
        // anything from ~129ms to ~171ms as "correct" for robot-36's own 150ms line duration and so
        // couldn't actually catch the error class it claimed to guard.
        var deltaSeconds = Math.Abs(measuredDurationSeconds - expectedTotalSeconds);
        Assert.True(
            deltaSeconds < 0.2,
            $"[{modeId}] TX-region duration {measuredDurationSeconds:F2}s vs expected {expectedTotalSeconds:F2}s, " +
            $"delta {deltaSeconds:F2}s exceeded tolerance.");
    }

    private static (double StartSeconds, double EndSeconds) MeasureTxRegion(float[] samples, int sampleRate)
    {
        const double nominalWindowSeconds = 0.05;
        const float threshold = 15000f / 32768f;

        var windowSize = (int)(nominalWindowSeconds * sampleRate);
        // Round-2-review fix: windowSize is an integer sample count, e.g. (int)(0.05*11025) = 551
        // samples = 49.977ms, not exactly the nominal 50ms used to compute it -- converting window
        // INDICES back to seconds using the nominal constant instead of this actual window duration
        // introduced a systematic +0.045% scale error, proportional to elapsed time (~17ms on
        // robot-36's ~38s capture, ~45ms on martin-m1's ~117s one -- both confirmed by an
        // independent re-implementation of this same algorithm). Deriving the conversion from the
        // actual windowSize/sampleRate instead removes that error rather than just tolerating it.
        var actualWindowSeconds = windowSize / (double)sampleRate;

        var windowPeaks = new List<float>();
        for (var i = 0; i < samples.Length; i += windowSize)
        {
            var end = Math.Min(i + windowSize, samples.Length);
            float peak = 0;
            for (var j = i; j < end; j++)
            {
                peak = Math.Max(peak, Math.Abs(samples[j]));
            }

            windowPeaks.Add(peak);
        }

        var firstActive = windowPeaks.FindIndex(p => p > threshold);
        var lastActive = windowPeaks.FindLastIndex(p => p > threshold);
        Assert.True(firstActive >= 0, "No TX region found above the amplitude threshold.");

        var startSeconds = firstActive * actualWindowSeconds;
        var endSeconds = (lastActive + 1) * actualWindowSeconds;
        return (startSeconds, endSeconds);
    }

    // Piece 2c-ii: an image-domain encoder cross-check that avoids the signal-domain alignment
    // problems a raw PCM/frequency-curve comparison against the real .mmv would hit (see this
    // class's own doc comment, above, for the deferred-piece-3 reasoning -- the .mmv capture is
    // offset by one audio buffer during TX and its final buffer is dropped, per
    // Fixtures/GoldenVectors/README.md, so it isn't even a clean copy of the modulator stream to
    // compare against sample-for-sample). Instead: encode the same source image with THIS port's
    // own encoder, decode that with THIS port's own decoder, and compare the result to what THIS
    // port's decoder produced from the REAL captured legacy audio (already measured in
    // Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource). If the C# encoder had a structural
    // bug like the Scottie channel-order/sync-placement issue CLAUDE.md's TX/RX-split rule warns
    // about, the two decodes would diverge from each other, not just from the source.
    [Theory]
    [MemberData(nameof(DecoderFixtures))]
    public async Task EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode(
        string modeId, string mmvFile, string sourceBmp, int pictureHeight)
    {
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var (realAudioDecoded, mode, _) = DecodeMmvFixture(modeId, mmvFile);

        var encoder = new AnalogFmSstvEncoder(11025);
        var encodedSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, source))
        {
            encodedSamples.Add(sample);
        }

        var selfDecoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? selfDetectedMode = null;
        IImageSource? selfDecoded = null;
        // Deliberately simpler than DecodeMmvFixture's "pin the first lock" logic: encodedSamples
        // is exactly one clean encode with no trailing audio, so unlike the real .mmv captures
        // there is no extra content for a second, spurious lock to ever occur against -- keeping
        // the last (== only) ModeDetected/LineDecoded update is equivalent here, not an
        // inconsistency with the sibling helper.
        selfDecoder.ModeDetected += m => selfDetectedMode = m;
        // Round-1-review fix: snapshot immediately rather than aliasing the decoder's own live
        // pixel buffer -- see DecodeMmvFixture's own comment on Snapshot for why this matters even
        // though, for this single-lock case, the live reference happens to be safe to read after
        // PushSamples returns too.
        selfDecoder.LineDecoded += update => selfDecoded = Snapshot(update.Image);

        selfDecoder.PushSamples(encodedSamples.ToArray());

        Assert.NotNull(selfDetectedMode);
        Assert.Equal(mode.Id, selfDetectedMode!.Id);
        Assert.NotNull(selfDecoded);

        var croppedSelf = CropToTop(selfDecoded!, pictureHeight);
        var croppedReal = CropToTop(realAudioDecoded, pictureHeight);
        var delta = MeasureAveragePerChannelDelta(croppedSelf, croppedReal, pictureHeight);

        // Re-measured after MUST 4 (spec/14-roadmap.md's Phase 3 fix) -- this comment's own prior
        // numbers predate MUST fixes 1-3 (they were never re-measured through those, unlike the
        // sibling test above) so no precise before/after comparison is claimed here; these are the
        // current, fresh measured values: martin-m1 1.29, robot-36 4.79, scottie-s1 0.48, robot-72
        // 4.64, pd90 1.58, rm8 3.43, mn110 1.10, avt 9.65. All close to or below their own sibling
        // decode-vs-source delta above -- genuine encoder/decoder agreement, not a loose tolerance
        // happening to pass -- and comfortably under the ~42.67 corruption floor documented on that
        // sibling test. Tolerances set to ~2x each measured value, same margin style used throughout
        // this file.
        //
        // Re-measured again after SHOULD item 4 (spec/14-roadmap.md, the TX color-to-frequency
        // integer-truncation fix -- YCbCr.FromRgb/ColorToFreq now model legacy's real two-truncation
        // chain): martin-m1 1.29 -> 1.34, robot-36 4.79 -> 4.16, scottie-s1 0.48 -> 0.37, robot-72
        // 4.64 -> 4.13, pd90 1.58 -> 0.18, rm8 3.43 -> 3.80, mn110 1.10 -> 0.14, avt 9.65 -> 9.87.
        // Mostly improved (this test compares THIS PORT'S OWN self-decode of its now-more-legacy-
        // faithful TX output against a REAL legacy decode, so closer truncation fidelity plausibly
        // improves agreement), a few worsened slightly (rm8, avt, martin-m1) -- same accepted-
        // tradeoff category as every other fix's own cross-mode effects this session. Every value
        // still comfortably inside its EXISTING tolerance below -- none needed to change. The sibling
        // RX-only test (`Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource`) was also re-measured
        // and found EXACTLY UNCHANGED -- expected, this fix touches only the encoder.
        //
        // Round-1-review correction: an earlier version of this comment also claimed
        // `LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` (the TX-direction real-legacy-
        // decode test) was re-measured and found unchanged "because a sub-3Hz shift is below one
        // quantization level" -- WRONG, caught by code-level review. That test reads two CHECKED-IN
        // files from disk (the source bmp and the stored `*_TX_RX.bmp`) -- it never invokes the
        // encoder at all, so it could not possibly have changed regardless of this fix; "unchanged"
        // there is a tautology, not evidence. The `*_TX.mmv`/`*_TX_RX.bmp` fixtures
        // (`Fixtures/GoldenVectors/TxCapture/`) were captured from the PRE-fix encoder and are now
        // STALE with respect to this fix -- `TxCaptureFixturesTests.cs` decodes the same stale
        // `.mmv` and doesn't cover it either. THIS test (`EncoderOutput_DecodesSimilarlyTo_
        // RealLegacyAudioDecode`, the one whose numbers moved above) is the only test in this file
        // that actually live-encodes with this port's own (now-fixed) encoder, so it's the only
        // real evidence this fix has today. Real TX-vs-real-legacy validation of this specific fix
        // does not yet exist -- needs a fresh `TxCapture/` re-capture (the user's own real legacy
        // install, same process as the original TX-side golden-vector work), out of scope for this
        // pass. Tracked as a real, open follow-up, not silently left unstated.
        //
        // Re-measured again after SHOULD item 5 (spec/14-roadmap.md, the OutHEAD pre-VIS leader-tone
        // burst port): martin-m1 1.34 -> 0.35, robot-36 4.16 -> 4.13, scottie-s1 0.37 -> 0.24,
        // robot-72 4.13 -> 4.11, pd90 0.18 -> 0.17, rm8 3.80 -> 3.72, mn110 0.14 -> 0.11, avt
        // 9.87 -> 4.68 (avt's own improvement is the largest here, but round-1-review flagged the
        // "AGC settling" explanation this comment used to give as weakly supported -- AVT already
        // carries ~10s of its own preamble before line 0, so its AGC is long converged either way;
        // see SstvRoundTripTests.cs's own AVT tolerance comment for the full correction and the
        // (unconfirmed) alternative mechanism the review raised instead. The improvement itself is
        // real and measured, just not confidently explained). Every value improved or held steady,
        // none worsened; all comfortably inside existing tolerances, none
        // needed to change. This is the same live-encoding test as the truncation fix's own
        // re-measurement above, and the same caveat applies: `LegacyDecode_OfThisPortsEncoderOutput_
        // MatchesSourceImage` (the TX-direction real-legacy-decode test) does not exercise this fix
        // either, for the identical reason (stale checked-in fixtures, no live encode) -- same real,
        // open follow-up, not re-stated a second time here.
        var toleranceByModeId = new Dictionary<string, double>
        {
            ["martin-m1"] = 4.0,
            ["robot-36"] = 10.0,
            ["scottie-s1"] = 3.0,
            ["robot-72"] = 10.0,
            ["pd90"] = 4.0,
            ["rm8"] = 7.0,
            ["mn110"] = 4.0,
            ["avt"] = 20.0,
        };
        var tolerance = toleranceByModeId[modeId];

        Assert.True(
            delta < tolerance,
            $"[{modeId}] self-encoded-vs-real-audio-decode delta {delta:F2} exceeded tolerance {tolerance}.");
    }

    // Round-1-review fix: AnalogFmSstvDecoder.LineDecoded hands out a MutableImageSource wrapping
    // the decoder's own LIVE _pixels array by reference, not a copy (confirmed directly against
    // AnalogFmSstvDecoder.cs: Commit() allocates a fresh _pixels array on every lock, and the only
    // writer, DecodeLine, re-reads _pixels from the field at the top of every outer-loop
    // iteration). Capturing that reference and reading it later works TODAY only because of that
    // "always a fresh array" invariant -- if Commit() were ever changed to reuse a same-sized
    // buffer instead of reallocating (a plausible future allocation optimization), a captured
    // reference would silently keep mutating after being "frozen," with no visible symptom. Copying
    // into a real snapshot here removes the dependency on that invariant instead of relying on it,
    // for the two capture sites in THIS file specifically. Round-2-review note: this same pattern
    // (bare `update.Image` capture, no snapshot) is used at roughly 15 other LineDecoded sites
    // across this test project (SstvRoundTripTests, PllScaleBridgeTests, SlantTests,
    // MidReceptionRestartTests, EndOfImageResetTests, and others) -- this fix does not protect any
    // of those, and fixing it here should not be read as having addressed the pattern project-wide.
    // Left as-is rather than touched here: this pass is scoped to the golden-vector harness, and a
    // repo-wide sweep is a separate, explicitly-scoped follow-up (or, more robustly, enforcing the
    // "always allocate fresh" invariant at the decoder itself, with a doc comment or test asserting
    // it, rather than every caller having to defensively copy).
    private static ArrayImageSource Snapshot(IImageSource image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, image.Height, pixels);
    }

    private static (IImageSource Decoded, SstvModeDefinition Mode, int RestartCount) DecodeMmvFixture(string modeId, string mmvFile)
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, mmvFile));

        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var detectedModesInOrder = new List<SstvModeDefinition>();
        var restartCount = 0;
        IImageSource? lastImageBeforeSecondLock = null;
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        decoder.ModeDetected += m => detectedModesInOrder.Add(m);
        decoder.DecodeRestarted += _ => restartCount++;
        decoder.LineDecoded += update =>
        {
            // These captures originally carried real sound-card audio recorded before/after the
            // transmission itself (confirmed during scoping: legacy's "Rec" only taps the modulator
            // while transmitting -- Wave.InClose() during TX means the pre/post-TX portions are live
            // mic input, not silence); now trimmed to the TX region plus a 1.0s margin (see
            // Fixtures/GoldenVectors/README.md), so the hazard this logic guards against is much
            // smaller in practice than when it was written, but the decoder still legitimately keeps
            // scanning past the end of the real transmission regardless of how much trailing audio
            // remains, so the defensive logic stays worth keeping. Snapshotting (see
            // Snapshot's own comment for why a bare reference isn't safe to rely on) on the last
            // LineDecoded update seen while still on the FIRST detected mode -- rather than assuming
            // the last LineDecoded event overall is the real image -- means a later spurious lock
            // can't silently overwrite the image we actually want to compare. See
            // Fixtures/GoldenVectors/README.md for the measured TX-region timing.
            if (detectedModesInOrder.Count == 1)
            {
                lastImageBeforeSecondLock = Snapshot(update.Image);
            }
        };

        decoder.PushSamples(samples);

        Assert.NotEmpty(detectedModesInOrder);
        Assert.Equal(mode.Id, detectedModesInOrder[0].Id);
        Assert.NotNull(lastImageBeforeSecondLock);

        return (lastImageBeforeSecondLock!, mode, restartCount);
    }

    private static IImageSource CropToTop(IImageSource image, int pictureHeight)
    {
        if (pictureHeight == image.Height)
        {
            return image;
        }

        var pixels = new Rgb24[image.Width * pictureHeight];
        for (var y = 0; y < pictureHeight; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, pictureHeight, pixels);
    }

    // R24-only: undoes legacy's own display-side row-doubling (SstvModeRegistry.R24's doc comment)
    // by taking every other row of the top `sourceHeight * 2` rows, rather than a plain top-N crop.
    private static IImageSource CropToTopEvenRows(IImageSource image, int sourceHeight)
    {
        var pixels = new Rgb24[image.Width * sourceHeight];
        for (var y = 0; y < sourceHeight; y++)
        {
            var line = image.GetScanline(y * 2);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, sourceHeight, pixels);
    }

    private static double MeasureAveragePerChannelDelta(IImageSource expected, IImageSource actual, int height)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(height, actual.Height);

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < height; y++)
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
