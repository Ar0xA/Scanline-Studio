namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="NarrowFskHeaderDecoder"/>, driven directly with synthetic
/// mark(m)/space(s) integer pairs -- bypassing <see cref="SyncEnvelopeDetector"/>/AGC entirely, since
/// this class's own contract starts at already-int-truncated envelope values (see its own doc
/// comment). Exercises the state machine in isolation, before <c>AnalogFmSstvDecoder</c> wiring, per
/// this project's "chop DSP ports into pieces" methodology.
/// </summary>
public class NarrowFskHeaderDecoderTests
{
    private const int SampleRate = 11025;
    private const int Mark = 8192; // s=0 -> d=8192 >= 2048
    private const int Space = 8192; // m=0 -> d=8192 >= 2048

    private static int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * SampleRate);

    /// <summary>Feeds `count` samples of a constant (m,s) pair, discarding any result -- used for
    /// phases that are not expected to complete a packet on their own.</summary>
    private static void FeedConstant(NarrowFskHeaderDecoder decoder, int count, int m, int s)
    {
        for (var i = 0; i < count; i++)
        {
            decoder.ProcessSample(m, s);
        }
    }

    /// <summary>Feeds just the 24 data bits (STX/marker/modeCode/checksum) -- for a decoder already
    /// past the guard/start-bit phases (via <see cref="FeedGuardAndStartBit"/> or a hand-built
    /// boundary sequence). Mirrors <c>VisHeader.GenerateNarrowModeSegments</c>' bit order (LSB first)
    /// without depending on it directly, matching <c>DecodeFSK</c>'s own shift-accumulate
    /// reconstruction rather than that generator. Returns the sample's decoded mode code, if any.</summary>
    private static int? FeedDataBits(NarrowFskHeaderDecoder decoder, int modeCodeByte, int? stxByteOverride = null, int? checksumOverride = null)
    {
        var stxByte = stxByteOverride ?? VisHeader.NarrowStxByte;
        var checksum = checksumOverride ?? ((modeCodeByte ^ VisHeader.NarrowMarkerByte) & 0xFF);
        var bytes = new[] { stxByte, VisHeader.NarrowMarkerByte, modeCodeByte, checksum };

        int? locked = null;
        foreach (var value in bytes)
        {
            for (var bitIndex = 0; bitIndex < 6; bitIndex++)
            {
                var bit = (value >> bitIndex) & 1;
                var samplesInBit = MsToSamples(VisHeader.NarrowBitDurationMs);
                for (var i = 0; i < samplesInBit; i++)
                {
                    var result = bit == 1
                        ? decoder.ProcessSample(Mark, 0)
                        : decoder.ProcessSample(0, Space);
                    if (result is not null)
                    {
                        locked = result.Value.ModeCode;
                    }
                }
            }
        }

        return locked;
    }

    /// <summary>Feeds the guard tone (space-dominant, <paramref name="guardMs"/>) then the start-bit
    /// training pulse (mark-dominant, one bit-duration) -- everything before the 24 data bits.</summary>
    private static void FeedGuardAndStartBit(NarrowFskHeaderDecoder decoder, int guardMs, int markAmplitude = Mark, int spaceAmplitude = Space)
    {
        // Guard: space-dominant. 100ms matches real TX (Main.cpp:7397); only >=50ms is required to
        // complete mode 1's hold, but using the real duration keeps the happy-path test representative.
        FeedConstant(decoder, MsToSamples(guardMs), 0, spaceAmplitude);

        // Start-bit training pulse: mark-dominant, one bit-duration long (Main.cpp:7398).
        FeedConstant(decoder, MsToSamples(VisHeader.NarrowBitDurationMs), markAmplitude, 0);
    }

    /// <summary>Full happy-path packet: guard + start-bit + 24 data bits.</summary>
    private static int? FeedPacket(NarrowFskHeaderDecoder decoder, int modeCodeByte, int guardMs = 100, int? stxByteOverride = null, int? checksumOverride = null)
    {
        FeedGuardAndStartBit(decoder, guardMs);
        return FeedDataBits(decoder, modeCodeByte, stxByteOverride, checksumOverride);
    }

    public static readonly TheoryData<int, string> RegisteredModeCodes = new()
    {
        { 0x02, nameof(SstvModeRegistry.Mn73) },
        { 0x04, nameof(SstvModeRegistry.Mn110) },
        { 0x05, nameof(SstvModeRegistry.Mn140) },
        { 0x14, nameof(SstvModeRegistry.Mc110) },
        { 0x15, nameof(SstvModeRegistry.Mc140) },
        { 0x16, nameof(SstvModeRegistry.Mc180) },
    };

    [Theory]
    [MemberData(nameof(RegisteredModeCodes))]
    public void ValidPacket_Locks_WithCorrectModeCode(int modeCode, string modeName)
    {
        _ = modeName;
        var decoder = new NarrowFskHeaderDecoder(SampleRate);

        var locked = FeedPacket(decoder, modeCode);

        Assert.Equal(modeCode, locked);
    }

    [Fact]
    public void WrongStxByte_DoesNotLock()
    {
        var decoder = new NarrowFskHeaderDecoder(SampleRate);

        var locked = FeedPacket(decoder, 0x02, stxByteOverride: 0x00);

        Assert.Null(locked);
    }

    [Fact]
    public void UnregisteredModeCode_ChecksumStillMatches_ButDecoderJustReportsTheRawByte()
    {
        // NarrowFskHeaderDecoder itself only implements the checksum-verified byte protocol (mode
        // 4/16/17/18) -- mapping an unregistered code to "no mode" is SstvModeRegistry.FindByNarrowCode's
        // job at the AnalogFmSstvDecoder wiring layer, not this class's. A code with a self-consistent
        // checksum still locks here. 0x3F (not 0x7F) deliberately: only the low 6 bits of any byte
        // ever transmit/decode (WriteFSK, sstv.cpp:2942) -- a 7-bit input would silently truncate,
        // which is legacy-faithful but would make this test's own intent ambiguous.
        var decoder = new NarrowFskHeaderDecoder(SampleRate);

        var locked = FeedPacket(decoder, 0x3F);

        Assert.Equal(0x3F, locked);
    }

    [Fact]
    public void BadChecksum_DoesNotLock_ThenResumesScanning_AndLocksNextValidPacket()
    {
        var decoder = new NarrowFskHeaderDecoder(SampleRate);

        var firstAttempt = FeedPacket(decoder, 0x02, checksumOverride: 0x00);
        Assert.Null(firstAttempt);

        var secondAttempt = FeedPacket(decoder, 0x14);
        Assert.Equal(0x14, secondAttempt);
    }

    [Fact]
    public void StationIdCallsign_0x2a_NeverReturnsAModeCode_AndDoesNotLockAMode()
    {
        // Phase 3 (CW-ID/FSK station-ID subsystem): 0x2a is NO LONGER "just like any unrecognized
        // byte" -- it now starts the station-ID sub-machine (mode 5), a real, stateful path, not a
        // reset. A well-formed callsign packet correctly COMMITS a callsign (see
        // NarrowFskHeaderDecoderStationIdTests) but must never produce a ModeCode -- confirms that
        // side of the FskDecodeResult contract at this level too.
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };

        var result = FeedStationIdCallsignPacket(decoder, "AB");

        Assert.NotNull(result);
        Assert.Null(result!.Value.ModeCode);
        Assert.Equal("AB", result.Value.StationIdCallsign);
    }

    [Fact]
    public void StationIdCallsignThenClosingGuardTone_NaturallyResetsToMode0_ThenAValid0x2dPacketLocks()
    {
        // Legacy's real recovery mechanism after a callsign-only transmission (no NR sub-packet):
        // TX's closing guard tone (Main.cpp:6964, FSKSPACE/FSKGARD -- a sustained space-dominant
        // tone) decodes as a run of all-ZERO bits once mode 7 (waiting for the NR sub-packet) starts
        // sampling it -- byte value 0 matches none of mode 7's "keep going" conditions (EOT=0x01,
        // compact-marker=0x02, valid-NR-char>=0x10), so it falls through to the reset branch
        // (sstv.cpp:2522-2524), naturally returning the decoder to mode 0. There is no artificial
        // timeout; recovery is emergent from the guard tone's own bit pattern.
        //
        // Code-review correction: VisHeader.NarrowGuardDurationMs (100ms) alone dispatches only 5 of
        // the 6 bits mode 7 needs (100/22 = 4.5, truncated at each 22ms boundary) -- it does NOT, by
        // itself, complete the zero-byte dispatch that triggers the reset. This test's full recovery
        // genuinely depends on FeedPacket's OWN guard tone below supplying the 6th bit and completing
        // the sequence -- this is honest coupling, not a test artifact to eliminate: a real closing
        // guard tone is immediately followed by either more of itself (as here) or the next
        // transmission's own leader/guard, so a "fully isolated" 100ms-only test would not represent
        // anything that happens on real air. Asserting the END-TO-END outcome (the real packet still
        // locks) is the correct-strength claim; asserting mode 7 has already reset after exactly
        // 100ms would not be.
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };

        var callsignResult = FeedStationIdCallsignPacket(decoder, "A");
        Assert.NotNull(callsignResult);
        Assert.Equal("A", callsignResult!.Value.StationIdCallsign);

        FeedConstant(decoder, MsToSamples(VisHeader.NarrowGuardDurationMs), 0, Space);

        var realPacket = FeedPacket(decoder, 0x05);
        Assert.Equal(0x05, realPacket);
    }

    /// <summary>Feeds a complete, well-formed station-ID callsign packet (guard + start-bit + STX
    /// 0x2a + callsign chars, each offset -0x20 and XORed into a running checksum + EOT + correct
    /// checksum byte) and returns whatever <see cref="FskDecodeResult"/> the final checksum byte's
    /// dispatch produces, if any. Independently re-derives the wire bytes from
    /// <paramref name="callsign"/> rather than calling <c>FskStationIdEncoder</c> directly, so this
    /// doesn't just check the decoder against its own TX-side sibling's implementation.</summary>
    private static FskDecodeResult? FeedStationIdCallsignPacket(NarrowFskHeaderDecoder decoder, string callsign)
    {
        FeedGuardAndStartBit(decoder, guardMs: 100);

        var checksum = 0;
        var bytes = new List<int> { 0x2a }; // STX
        foreach (var ch in callsign)
        {
            var c = ch - 0x20;
            checksum ^= c;
            bytes.Add(c);
        }

        bytes.Add(0x01); // EOT
        bytes.Add(checksum);

        FskDecodeResult? result = null;
        var samplesInBit = MsToSamples(VisHeader.NarrowBitDurationMs);
        foreach (var value in bytes)
        {
            for (var bitIndex = 0; bitIndex < 6; bitIndex++)
            {
                var bit = (value >> bitIndex) & 1;
                for (var i = 0; i < samplesInBit; i++)
                {
                    var sample = bit == 1 ? decoder.ProcessSample(Mark, 0) : decoder.ProcessSample(0, Space);
                    if (sample is not null)
                    {
                        result = sample;
                    }
                }
            }
        }

        return result;
    }

    [Fact]
    public void AmplitudeGate_ExactlyAtThreshold_Accepts()
    {
        // d = |m-s| = 2048 exactly must still trigger the mode-0 guard detect (sstv.cpp:2384: d>=2048).
        var decoder = new NarrowFskHeaderDecoder(SampleRate);

        var result = decoder.ProcessSample(0, 2048);

        // Not locked yet (only one sample fed), but must not have silently rejected either -- confirm
        // indirectly via a full packet built on this exact boundary.
        Assert.Null(result);

        var boundaryDecoder = new NarrowFskHeaderDecoder(SampleRate);
        FeedGuardAndStartBit(boundaryDecoder, guardMs: 100, markAmplitude: 2048, spaceAmplitude: 2048);
        var locked = FeedDataBits(boundaryDecoder, 0x02);

        Assert.Equal(0x02, locked);
    }

    [Fact]
    public void AmplitudeGate_OneBelowThreshold_NeverTriggersGuardDetect()
    {
        // d = 2047 must never satisfy the mode-0 trigger (sstv.cpp:2384: d>=2048) -- feed a full
        // packet's worth of samples at this sub-threshold amplitude and confirm it never locks.
        var decoder = new NarrowFskHeaderDecoder(SampleRate);

        FeedConstant(decoder, MsToSamples(600), 0, 2047);
        var locked = FeedDataBits(decoder, 0x02);

        Assert.Null(locked);
    }

    [Fact]
    public void SamplesSinceBitClockOrigin_MatchesExactDataBitPhaseLength()
    {
        // S8 fix (spec/14-roadmap.md): pins the exact value AnalogFmSstvDecoder.TryNarrowFskScan's
        // anchor arithmetic depends on. Code-level auditor review correction: the "bit-clock origin"
        // (the mode-3->4 transition) is NOT the first sample FeedDataBits below feeds -- it lands
        // ~121 samples EARLIER, inside FeedGuardAndStartBit's own start-bit-training-pulse feed
        // (mode 2's trigger fires at start-bit onset, mode 3 passes its own single recheck 121
        // samples later, and mode 4 -- the origin itself -- begins the very next sample). The lock
        // also does NOT complete on the literal last sample FeedDataBits feeds -- it completes
        // whenever the decoder's own internal truncated-cumulative bit boundary is first reached
        // within the final bit's window, which this test's own samplesInBit (rounded, not truncated)
        // feed doesn't line up with exactly. Both are accounted for in the formula below, independently
        // re-derived by that review (exact at 11025Hz): asserting the resulting SamplesSinceBitClockOrigin
        // value directly, not re-deriving "origin sample" or "final sample" as independent facts.
        var decoder = new NarrowFskHeaderDecoder(SampleRate);
        FeedGuardAndStartBit(decoder, guardMs: 100);

        FskDecodeResult? locked = null;
        var stxByte = VisHeader.NarrowStxByte;
        var modeCodeByte = 0x02;
        var checksum = (modeCodeByte ^ VisHeader.NarrowMarkerByte) & 0xFF;
        var bytes = new[] { stxByte, VisHeader.NarrowMarkerByte, modeCodeByte, checksum };
        var samplesInBit = MsToSamples(VisHeader.NarrowBitDurationMs);
        foreach (var value in bytes)
        {
            for (var bitIndex = 0; bitIndex < 6; bitIndex++)
            {
                var bit = (value >> bitIndex) & 1;
                for (var i = 0; i < samplesInBit; i++)
                {
                    var result = bit == 1 ? decoder.ProcessSample(Mark, 0) : decoder.ProcessSample(0, Space);
                    if (result is not null)
                    {
                        locked = result;
                    }
                }
            }
        }

        Assert.NotNull(locked);
        Assert.Equal(0x02, locked!.Value.ModeCode);

        // NOT 24*samplesInBit-1: the decoder's own _nextBitBoundary is the TRUNCATED cumulative
        // exact-ms value at each step ((int)(k*22ms-worth-of-samples) for k=1..24, see ProcessSample's
        // default: case), not 24 independently-rounded per-bit windows -- this class's own real
        // drift-correction design (its class doc comment: "drift-corrects the 24-bit stream the way
        // naive repeated integer addition would not"). A single truncation of the 24-bit TOTAL happens
        // to equal this cumulative-truncation result exactly, since _nextBitBoundaryExact always holds
        // the exact running sum (k*22ms), never accumulating error from previous truncations.
        var expectedSamplesSinceOrigin = (int)(24 * VisHeader.NarrowBitDurationMs / 1000.0 * SampleRate) - 1;
        Assert.Equal(expectedSamplesSinceOrigin, locked.Value.SamplesSinceBitClockOrigin);
    }

    [Fact]
    public void GuardHold_49ms_DoesNotAdvance_50ms_DoesAdvance()
    {
        // Mode 1 requires the guard condition to hold for the FULL 50ms (FSKGARD/2) -- interrupting
        // it just 1ms early must reset to mode 0, not partially advance.
        var tooShort = new NarrowFskHeaderDecoder(SampleRate);
        FeedConstant(tooShort, MsToSamples(49), 0, Mark); // space-dominant guard, 49ms
        FeedConstant(tooShort, MsToSamples(2), Mark, 0); // interrupt with mark before 50ms hold completes
        FeedGuardAndStartBit(tooShort, guardMs: 100); // re-establish guard from scratch, then start-bit
        var recoveredLock = FeedDataBits(tooShort, 0x02);
        Assert.Equal(0x02, recoveredLock); // still recoverable -- confirms mode 0 reset, not a dead decoder

        // Exactly enough samples to complete the hold on the first try: mode 0's trigger consumes
        // one sample before mode 1's countdown even starts (sstv.cpp:2383-2387 sets m_fsktime but
        // doesn't decrement it that same sample), so completing a 50ms/551-sample hold needs
        // 551+1 = 552 total guard samples, not MsToSamples(50) itself -- one more than the nominal
        // duration would suggest. Real TX's 100ms guard swallows this off-by-one invisibly; this
        // test exists specifically to pin the exact boundary down.
        var exact = new NarrowFskHeaderDecoder(SampleRate);
        FeedConstant(exact, MsToSamples(50) + 1, 0, Space);
        FeedConstant(exact, MsToSamples(VisHeader.NarrowBitDurationMs), Mark, 0);
        var lockedOnFirstTry = FeedDataBits(exact, 0x02);
        Assert.Equal(0x02, lockedOnFirstTry);
    }
}
