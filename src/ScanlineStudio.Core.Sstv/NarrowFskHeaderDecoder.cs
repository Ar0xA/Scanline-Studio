namespace ScanlineStudio.Core.Sstv;

/// <summary>Discriminated result of <see cref="NarrowFskHeaderDecoder.ProcessSample"/> -- at most one
/// of <see cref="ModeCode"/>/<see cref="StationIdCallsign"/>/<see cref="StationIdCompactNr"/>/
/// <see cref="StationIdNrText"/> is set per instance, mirroring legacy's distinct successful-decode
/// outcomes from the ONE shared state machine (mode-announce lock vs. station-ID callsign commit vs.
/// station-ID NR/RST commit, itself two sub-forms -- <c>sstv.cpp:2586-2598</c>, <c>:2485-2494</c>,
/// <c>:2544-2551</c>, <c>:2526-2534</c> respectively). <see cref="StationIdCompactNr"/> (from the
/// compact numeric sub-form, legacy mode 10) and <see cref="StationIdNrText"/> (from the
/// alphanumeric string sub-form, legacy mode 8) are DELIBERATELY separate, not one field with a
/// try-parse: the string sub-form exists precisely for NR/RST values that don't fit the compact
/// numeric form, so a value that fails to parse as a number is a legitimate, real result, not
/// evidence of a decode error -- collapsing them would silently drop it. Callers must NOT treat a
/// <see cref="StationIdCallsign"/>/<see cref="StationIdCompactNr"/>/<see cref="StationIdNrText"/>
/// result the same way as a <see cref="ModeCode"/> result: only a mode-announce lock represents
/// "found the packet this scan exists to find, stop and commit a mode" -- a station-ID commit is a
/// side-channel informational event that must let the enclosing scan keep running (see
/// <see cref="AnalogFmSstvDecoder"/>'s own <c>TryNarrowFskScan</c> for the consumer side of this
/// contract, and its doc comment for why getting this wrong would silently break AVT
/// training/header scanning).</summary>
internal readonly record struct FskDecodeResult(
    int SamplesSinceBitClockOrigin,
    int? ModeCode = null,
    string? StationIdCallsign = null,
    uint? StationIdCompactNr = null,
    string? StationIdNrText = null)
{
    public static FskDecodeResult ForModeCode(int modeCode, int samplesSinceBitClockOrigin) =>
        new(samplesSinceBitClockOrigin, ModeCode: modeCode);

    public static FskDecodeResult ForStationIdCallsign(string callsign, int samplesSinceBitClockOrigin) =>
        new(samplesSinceBitClockOrigin, StationIdCallsign: callsign);

    public static FskDecodeResult ForStationIdCompactNr(uint nr, int samplesSinceBitClockOrigin) =>
        new(samplesSinceBitClockOrigin, StationIdCompactNr: nr);

    public static FskDecodeResult ForStationIdNrText(string nrText, int samplesSinceBitClockOrigin) =>
        new(samplesSinceBitClockOrigin, StationIdNrText: nrText);
}

/// <summary>
/// Direct, literal port of <c>CSSTVDEM::DecodeFSK(int m, int s)</c> (<c>sstv.cpp:2378-2606</c>) --
/// the real per-sample state machine legacy uses to decode the MN/MC narrow-mode-announce packet
/// (<see cref="VisHeader.GenerateNarrowModeSegments"/>), replacing a prior proxy in
/// <see cref="AnalogFmSstvDecoder"/> that averaged the shared PLL's demodulated-frequency stream over
/// a fixed window instead -- the same bug shape Piece 9 already fixed for VIS-bit decode.
///
/// Also decodes the FSK station-ID packet (<c>STX 0x2a</c>, legacy modes 5-10, `Main.cpp:2465-2551`)
/// -- the SAME shared state machine legacy uses, diverging from the mode-announce path only after
/// the sync byte (mode 4's dispatch). Legacy reuses ONE checksum field (`m_fsks`) and ONE length
/// counter (`m_fskcnt`) across BOTH packet types (never active simultaneously, since only one path
/// is reachable per packet) -- this port reuses <see cref="_runningXor"/> and
/// <see cref="_stationIdSubPacketCount"/> the same way, not separate fields per path.
///
/// <c>m</c>/<c>s</c> are <c>int(d19)</c>/<c>int(dsp)</c> in legacy: the 1900Hz mark and 2100Hz
/// (<see cref="VisHeader.NarrowSpaceFrequencyHz"/>) space envelope detectors
/// (<see cref="SyncEnvelopeDetector"/>, both 100Hz bandwidth, <c>sstv.cpp:1449-1450</c>), fed the same
/// AGC'd/scaled signal (<c>d = ad*32</c> clipped +-16384, <c>sstv.cpp:1834-1839</c>) as every other
/// sync/tone envelope detector in this class -- i.e. this port's <c>AgcSampleAt</c>. Callers must
/// truncate each detector's output to <c>int</c> before calling <see cref="ProcessSample"/>, matching
/// legacy's own <c>int()</c> truncation at the call site (<c>sstv.cpp:1858</c>).
///
/// State machine (verified line-by-line against <c>sstv.cpp:2378-2606</c> and <c>sstv.h:710-717</c>
/// across two rounds of independent auditor review -- round 1 caught real transcription errors in an
/// earlier draft of this same summary, so this is re-derived from source, not paraphrased from memory):
/// <list type="bullet">
/// <item>mode 0 (trigger): <c>s&gt;m &amp;&amp; |m-s|&gt;=2048</c> -&gt; arm a 50ms
///   (<c>FSKGARD/2</c>) countdown, mode 1. Else stay.</item>
/// <item>mode 1 (guard hold -- the actual debounce): re-tests the same condition every sample,
///   decrementing only while it holds; ANY failing sample resets straight to mode 0 (no re-arm).
///   Reaching 0 (50ms held) reloads a 100ms (<c>FSKGARD</c>) countdown and advances to mode 2.</item>
/// <item>mode 2 (start-bit search -- a 100ms TIMEOUT window, not a second hold): decrements
///   unconditionally every sample; hitting 0 is a pure timeout back to mode 0 with NO condition check
///   on that same sample. Only when not timing out does it test <c>m&gt;s &amp;&amp; |m-s|&gt;=2048</c>;
///   on success, arms an 11ms (<c>FSKINTVAL/2</c>) countdown and advances to mode 3.</item>
/// <item>mode 3 (single recheck, not a hold): decrements every sample with no condition check until
///   it hits 0; at that ONE sample, tests <c>m&gt;s &amp;&amp; |m-s|&gt;=2048</c> once -- pass sets up
///   the bit clock (see below) and advances to mode 4, fail resets to mode 0.</item>
/// <item>mode 4+ (bit sampling): a free-running sample counter advances every sample from mode-4
///   entry; only when it reaches the next bit boundary is <c>|m-s|</c> tested (once per 22ms bit
///   instant, never in between) -- below 2048 resets to mode 0, otherwise the bit
///   (<c>m&gt;s</c> =&gt; 1) is shifted into a 6-bit accumulator LSB-first and the boundary advances by
///   a FRACTIONAL (double) 22ms increment that is truncated to int only for the next comparison --
///   this drift-corrects the 24-bit stream the way naive repeated integer addition would not.</item>
/// <item>Byte dispatch (every 6th bit, still inside the mode-4+ default case): mode 4 branches on
///   STX -- 0x2d advances to mode 16 (mode-announce path, below), 0x2a advances to mode 5
///   (station-ID path, `Main.cpp:2465-2551` -- see this class's own second doc-comment paragraph),
///   anything else resets to mode 0. Mode-announce: mode 16 expects marker 0x15; mode 17 stores the
///   raw mode-code byte; mode 18 checks it against the running XOR checksum and, on match, resolves
///   the mode code. Mode ALWAYS resets to 0 after mode 18's dispatch, success or failure -- legacy
///   never permanently gives up on a bad checksum or unrecognized mode code, it just resumes
///   scanning on the very next sample (matching the same "resume, don't abort" shape already fixed
///   once on the VIS-bit path). Station-ID modes 5-10 follow the identical always-resume
///   discipline.</item>
/// </list>
///
/// Each <see cref="ProcessSample"/> call reads the current mode once and runs exactly one case's
/// logic, mirroring legacy's <c>switch</c>+<c>break</c>-per-<c>Do()</c>-call structure -- a mode
/// change made during sample N's call only takes effect starting sample N+1's call. This is what
/// makes mode 2's "timeout sample gets no condition check" and mode 1/3's exact boundaries correct;
/// an implementation that re-dispatches the new state within the same call would silently break all
/// three.
/// </summary>
internal sealed class NarrowFskHeaderDecoder
{
    private const int AmplitudeThreshold = 2048;

    private readonly int _sampleRate;

    private int _mode;
    private int _time;
    private double _nextBitBoundaryExact;
    private int _nextBitBoundary;
    private int _bitAccumulator;
    private int _bitCount;
    private int _runningXor; // shared checksum accumulator (legacy m_fsks), both packet types
    private int _modeCodeByte;

    // Station-ID (STX 0x2a) state, legacy modes 5-10. `_stationIdSubPacketCount` mirrors legacy's
    // `m_fskcnt` reuse: counts callsign chars during mode 5, reset to 0 by mode 6, then reused
    // to count NR/RST string chars during mode 7 -- one shared counter, not two, matching source.
    private int _stationIdSubPacketCount;
    private readonly System.Text.StringBuilder _stationIdCallsignBuffer = new();
    private readonly System.Text.StringBuilder _stationIdNrStringBuffer = new();
    private int _stationIdNr; // legacy m_fskNR, compact-form 12-bit accumulator (2x 6-bit halves)

    // S8 fix (spec/14-roadmap.md): tracks samples elapsed since the mode-3->4 transition ("bit-clock
    // origin" -- the instant bit sampling begins), NOT an absolute sample index. Deliberately relative:
    // this class is now fed continuously for the whole decoder's lifetime by a caller whose own cursor
    // bookkeeping (EndOfImage/Commit fast-forwards) this class must stay completely ignorant of --
    // auditor plan-review flagged an earlier draft's "this instance's own sample-0 is absolute sample
    // 0" premise as unsafe (nothing enforces every feed advancing by exactly 1 from a true zero; a
    // future caller-side skip would silently produce a wrong anchor, not a crash). The caller computes
    // the true origin as (its own absolute index for this call) - SamplesSinceBitClockOrigin.
    // Auditor plan-review also corrected the anchor's own reference point: the mode-0 guard-tone
    // TRIGGER (an earlier draft's choice) has real, unbounded-in-practice jitter (envelope-settling lag
    // on the rising/falling s/m tones, plus mode 1's own 50ms hold tolerating a trigger up to ~50ms
    // late and still succeeding) -- the mode-3 recheck is tightly pinned instead (a single pass/fail
    // sample, not a hold, so it can only succeed within the 22ms start-bit window it's checking against).
    private int _samplesSinceBitClockOrigin;

    public NarrowFskHeaderDecoder(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <summary>Legacy <c>m_fskdecode</c> (`sstv.h:708`, ini key <c>RXFSKID</c>) -- gates whether a
    /// station-ID callsign/NR commit is accepted (modes 6/8; mode 10's compact-NR commit does NOT
    /// check this, a legacy asymmetry confirmed unreachable in practice since mode 10 is only
    /// reachable through mode 6, which already requires this flag -- see `Main.cpp:2485`/`:2544`).
    /// Does NOT gate the mode-announce path at all (legacy has no such gate there). Defaults to
    /// legacy's own real default (off, zero-initialized, `Main.cpp:1880`'s `ReadInteger` falls back
    /// to whatever this field already was) -- wired to the live user setting by
    /// <c>SstvSessionService.StartReceivingAsync</c> via <see cref="AnalogFmSstvDecoder.StationIdDecodeEnabled"/>
    /// (see that property's own doc comment for the full wiring chain).</summary>
    public bool StationIdDecodeEnabled { get; set; }

    /// <summary>Feeds one sample's mark(1900Hz)/space(2100Hz) envelope pair through the state
    /// machine. Returns the decoded mode code and how many samples ago this instance's own internal
    /// "bit-clock origin" (the mode-3->4 transition, see <see cref="_samplesSinceBitClockOrigin"/>'s
    /// own doc comment) occurred, once a full packet locks with a valid checksum; otherwise null
    /// (still searching -- every legacy failure path resumes scanning internally, so there is no
    /// separate "aborted" outcome to report). See <see cref="FskDecodeResult"/>'s own doc comment for
    /// the station-ID (callsign/NR) outcomes this can also return, and why a caller must not treat
    /// them the same way as a mode-code lock.
    ///
    /// Code-level auditor review note (S8 fix, spec/14-roadmap.md): assumes CONTIGUOUS feeding since
    /// whatever sample last produced the current "bit-clock origin" -- i.e. every sample in between
    /// must have been fed too, none skipped. The caller (<c>AnalogFmSstvDecoder.Commit</c>) can fast-
    /// forward ITS OWN bookkeeping cursor past a stretch of samples this instance was never fed (when
    /// a DIFFERENT detection path commits a match while this instance happens to be mid-packet,
    /// mode&gt;=4) -- if that ever happens, <see cref="_samplesSinceBitClockOrigin"/> would undercount
    /// the true elapsed time once feeding resumes, biasing a subsequent lock's anchor late by the
    /// skipped span. Not fixed: reachable only via an already-improbable false-positive continuation
    /// (any real gap of more than about one bit period, ~22ms, already desyncs the bit clock and
    /// resets this state machine to mode 0 on its own, via the existing `d &lt; AmplitudeThreshold`
    /// check), and no test has ever reached it.</summary>
    public FskDecodeResult? ProcessSample(int m, int s)
    {
        var d = Math.Abs(m - s);

        switch (_mode)
        {
            case 0: // sstv.cpp:2382-2388 -- guard-tone trigger
                if (s > m && d >= AmplitudeThreshold)
                {
                    _time = MsToSamples(VisHeader.NarrowGuardDurationMs / 2);
                    _mode = 1;
                }
                break;

            case 1: // sstv.cpp:2389-2401 -- guard-tone hold (the real 50ms debounce)
                if (s > m && d >= AmplitudeThreshold)
                {
                    _time--;
                    if (_time == 0)
                    {
                        _time = MsToSamples(VisHeader.NarrowGuardDurationMs);
                        _mode = 2;
                    }
                }
                else
                {
                    _mode = 0;
                }
                break;

            case 2: // sstv.cpp:2402-2412 -- start-bit search, a timeout window, not a second hold
                _time--;
                if (_time == 0)
                {
                    _mode = 0; // pure timeout -- no condition check on this same sample
                }
                else if (m > s && d >= AmplitudeThreshold)
                {
                    _time = MsToSamples(VisHeader.NarrowBitDurationMs / 2);
                    _mode = 3;
                }
                break;

            case 3: // sstv.cpp:2413-2429 -- single recheck at the 11ms midpoint, not a hold
                _time--;
                if (_time == 0)
                {
                    if (m > s && d >= AmplitudeThreshold)
                    {
                        _time = 0;
                        _nextBitBoundaryExact = MsToSamplesExact(VisHeader.NarrowBitDurationMs);
                        _nextBitBoundary = (int)_nextBitBoundaryExact;
                        _bitCount = 0;
                        _bitAccumulator = 0;
                        // -1 here, not 0: the mode-3->4 transition itself is not yet a `default:`
                        // case call (matches this class's own "a mode change made during sample N's
                        // call only takes effect starting sample N+1's call" convention, see class
                        // doc comment) -- the first `default:` call below increments this to 0,
                        // representing that NEXT sample as the origin itself (0 samples elapsed).
                        _samplesSinceBitClockOrigin = -1;
                        _mode = 4;
                    }
                    else
                    {
                        _mode = 0;
                    }
                }
                break;

            default: // sstv.cpp:2430-2603 -- bit sampling + byte dispatch (modes 4, 16, 17, 18)
                _samplesSinceBitClockOrigin++;
                _time++;
                if (_time >= _nextBitBoundary)
                {
                    if (d < AmplitudeThreshold)
                    {
                        _mode = 0;
                    }
                    else
                    {
                        _nextBitBoundaryExact += MsToSamplesExact(VisHeader.NarrowBitDurationMs);
                        _nextBitBoundary = (int)_nextBitBoundaryExact;

                        _bitAccumulator >>= 1;
                        if (m > s)
                        {
                            _bitAccumulator |= 0x20;
                        }

                        _bitCount++;
                        if (_bitCount >= 6)
                        {
                            _bitCount = 0;
                            var result = DispatchByte(_bitAccumulator);
                            _bitAccumulator = 0;
                            if (result is not null)
                            {
                                return result;
                            }
                        }
                    }
                }
                break;
        }

        return null;
    }

    private const int StationIdStxByte = 0x2a;
    private const int StationIdEotByte = 0x01;
    private const int StationIdCompactNrMarkerByte = 0x02;
    private const int MaxCallsignLength = 16; // FskStationIdWireFormat.MaxCallsignLength, kept in
                                               // sync manually (that class isn't a dependency of
                                               // this one, matching TX/RX not sharing code directly).
    private const int MaxNrStringLength = 8;

    // sstv.cpp:2445-2598 -- fires once per completed 6-bit byte, dispatched on the CURRENT mode
    // (still 4/16/17/18/5/6/7/8/9/10 at this point, distinct from the outer default case covering
    // all of 4+).
    private FskDecodeResult? DispatchByte(int fskc)
    {
        switch (_mode)
        {
            case 4: // First SYNC -- sstv.cpp:2446-2464
                if (fskc == StationIdStxByte) // 0x2a
                {
                    _stationIdSubPacketCount = 0;
                    _runningXor = 0;
                    _stationIdCallsignBuffer.Clear();
                    _mode = 5;
                }
                else if (fskc == VisHeader.NarrowStxByte) // 0x2d
                {
                    // Code-review finding: legacy resets m_fskcnt on BOTH branches (sstv.cpp:2448 and
                    // :2455) -- an earlier version of this code only reset _stationIdSubPacketCount on
                    // the 0x2a branch. Provably harmless either way (nothing reads this counter again
                    // before mode 5 or mode 9 next resets it themselves), but matched here for exact
                    // fidelity rather than relying on that argument.
                    _stationIdSubPacketCount = 0;
                    _runningXor = 0;
                    _modeCodeByte = 0;
                    _mode = 16;
                }
                else
                {
                    _mode = 0;
                }
                break;

            case 5: // station-ID: store callsign data -- sstv.cpp:2465-2482
                if (fskc == StationIdEotByte)
                {
                    _mode = _stationIdSubPacketCount >= 1 ? 6 : 0;
                }
                else
                {
                    _runningXor ^= fskc;
                    _stationIdCallsignBuffer.Append((char)(fskc + 0x20));
                    _stationIdSubPacketCount++;
                    if (_stationIdSubPacketCount >= MaxCallsignLength + 1)
                    {
                        _mode = 0;
                    }
                }
                break;

            case 6: // station-ID: check callsign XOR -- sstv.cpp:2483-2499
            {
                _runningXor &= 0x3f;
                FskDecodeResult? committed = null;
                if (fskc == _runningXor && StationIdDecodeEnabled)
                {
                    // sstv.cpp:2487-2488: SkipSpace(leading) + StrCopy(...,16) + clipsp(trailing) --
                    // .Trim() covers the combined leading+trailing whitespace trim; the 16-char cap
                    // is a redundant safety net here (case 5's own MaxCallsignLength+1 abort already
                    // bounds any surviving, non-aborted callsign to <= 16 chars), kept for fidelity.
                    var callsign = _stationIdCallsignBuffer.ToString().Trim();
                    if (callsign.Length > MaxCallsignLength)
                    {
                        callsign = callsign[..MaxCallsignLength];
                    }

                    committed = FskDecodeResult.ForStationIdCallsign(callsign, _samplesSinceBitClockOrigin);
                    _stationIdSubPacketCount = 0;
                    _runningXor = 0;
                    _stationIdNrStringBuffer.Clear();
                    _mode = 7;
                }
                else
                {
                    _mode = 0;
                }

                return committed;
            }

            case 7: // station-ID: store NR/RST data -- sstv.cpp:2500-2525
                if (fskc == StationIdEotByte)
                {
                    _mode = _stationIdSubPacketCount >= 1 ? 8 : 0;
                }
                else if (fskc == StationIdCompactNrMarkerByte)
                {
                    // Code-review finding: legacy does NOT reset m_fskcnt here (sstv.cpp:2509-2513) --
                    // an earlier version of this code reset _stationIdSubPacketCount, reasoning mode 9
                    // "uses it purely as a 2-halves counter." That reasoning is wrong for corrupt/
                    // malformed input specifically: if NR-string chars were already accumulated before
                    // this marker byte arrived (fskcnt>0), legacy enters mode 9 with that nonzero count
                    // and only consumes ONE more 6-bit half before checking XOR (mode 9's own `>=2`
                    // threshold), not two. Matching legacy exactly here, including that behavior, since
                    // "the safer choice" isn't this port's call to make on a byte-exact wire protocol.
                    _runningXor = StationIdCompactNrMarkerByte;
                    _stationIdNr = 0;
                    _mode = 9;
                }
                else if (fskc >= 0x10)
                {
                    _runningXor ^= fskc;
                    _stationIdNrStringBuffer.Append((char)(fskc + 0x20));
                    _stationIdSubPacketCount++;
                    if (_stationIdSubPacketCount >= MaxNrStringLength + 1)
                    {
                        _mode = 0;
                    }
                }
                else
                {
                    _mode = 0;
                }
                break;

            case 8: // station-ID: check NR/RST-string XOR -- sstv.cpp:2526-2534
            {
                _runningXor &= 0x3f;
                FskDecodeResult? committed = null;
                if (fskc == _runningXor && StationIdDecodeEnabled)
                {
                    // sstv.cpp:2530: clipsp (trailing-whitespace trim only, no leading-space skip
                    // unlike the callsign path's SkipSpace+clipsp pair) -- .Trim() trims both ends,
                    // a slightly broader (harmless) match rather than a narrower, wrong one.
                    var nrText = _stationIdNrStringBuffer.ToString().Trim();
                    committed = FskDecodeResult.ForStationIdNrText(nrText, _samplesSinceBitClockOrigin);
                }

                _mode = 0; // legacy ALWAYS resets here, unlike mode 6 -- sstv.cpp:2533
                return committed;
            }

            case 9: // station-ID: compact NR bit accumulation -- sstv.cpp:2535-2543
                _runningXor ^= fskc;
                _stationIdNr = (_stationIdNr << 6) + fskc;
                _stationIdSubPacketCount++;
                if (_stationIdSubPacketCount >= 2)
                {
                    _mode = 10;
                }

                break;

            case 10: // station-ID: check compact-NR XOR -- sstv.cpp:2544-2551. NOTE: unlike modes 6/8,
                      // legacy does NOT check m_fskdecode here (a confirmed-unreachable-in-practice
                      // asymmetry, see StationIdDecodeEnabled's own doc comment -- mode 10 is only
                      // reachable via mode 9, only reachable via mode 7, only reachable via mode 6,
                      // which already gated on the flag).
            {
                _runningXor &= 0x3f;
                FskDecodeResult? committed = null;
                if (fskc == _runningXor)
                {
                    committed = FskDecodeResult.ForStationIdCompactNr((uint)_stationIdNr, _samplesSinceBitClockOrigin);
                }

                _mode = 0;
                return committed;
            }

            case 16: // marker byte -- sstv.cpp:2552-2560
                _runningXor ^= fskc;
                _mode = fskc == VisHeader.NarrowMarkerByte ? 17 : 0; // 0x15
                break;

            case 17: // mode-code byte -- sstv.cpp:2561-2565
                _runningXor ^= fskc;
                _modeCodeByte = fskc;
                _mode = 18;
                break;

            case 18: // checksum -- sstv.cpp:2566-2598
                _runningXor &= 0x3f;
                _mode = 0; // legacy always resets here, checksum pass or fail (sstv.cpp:2597)
                if (fskc == _runningXor)
                {
                    return FskDecodeResult.ForModeCode(_modeCodeByte, _samplesSinceBitClockOrigin);
                }

                break;
        }

        return null;
    }

    private int MsToSamples(double ms) => (int)(ms / 1000.0 * _sampleRate);

    private double MsToSamplesExact(double ms) => ms / 1000.0 * _sampleRate;
}
