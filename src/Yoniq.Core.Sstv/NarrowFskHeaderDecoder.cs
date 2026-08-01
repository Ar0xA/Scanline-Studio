namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct, literal port of <c>CSSTVDEM::DecodeFSK(int m, int s)</c> (<c>sstv.cpp:2378-2606</c>) --
/// the real per-sample state machine legacy uses to decode the MN/MC narrow-mode-announce packet
/// (<see cref="VisHeader.GenerateNarrowModeSegments"/>), replacing a prior proxy in
/// <see cref="AnalogFmSstvDecoder"/> that averaged the shared PLL's demodulated-frequency stream over
/// a fixed window instead -- the same bug shape Piece 9 already fixed for VIS-bit decode.
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
/// <item>Byte dispatch (every 6th bit, still inside the mode-4+ default case): mode 4 expects STX
///   0x2d (anything else, including the unrelated 0x2a callsign-ID packet this class does not
///   implement, resets to mode 0 exactly like any other unrecognized byte -- see
///   docs/removed-features.md); mode 16 expects marker 0x15; mode 17 stores the raw mode-code byte;
///   mode 18 checks it against the running XOR checksum and, on match, resolves the mode code. Mode
///   ALWAYS resets to 0 after mode 18's dispatch, success or failure -- legacy never permanently gives
///   up on a bad checksum or unrecognized mode code, it just resumes scanning on the very next sample
///   (matching the same "resume, don't abort" shape already fixed once on the VIS-bit path).</item>
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
    private int _runningXor;
    private int _modeCodeByte;

    public NarrowFskHeaderDecoder(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <summary>Feeds one sample's mark(1900Hz)/space(2100Hz) envelope pair through the state
    /// machine. Returns the decoded mode code once a full packet locks with a valid checksum;
    /// otherwise null (still searching -- every legacy failure path resumes scanning internally,
    /// so there is no separate "aborted" outcome to report).</summary>
    public int? ProcessSample(int m, int s)
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
                        _mode = 4;
                    }
                    else
                    {
                        _mode = 0;
                    }
                }
                break;

            default: // sstv.cpp:2430-2603 -- bit sampling + byte dispatch (modes 4, 16, 17, 18)
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
                            var lockedCode = DispatchByte(_bitAccumulator);
                            _bitAccumulator = 0;
                            if (lockedCode is not null)
                            {
                                return lockedCode;
                            }
                        }
                    }
                }
                break;
        }

        return null;
    }

    // sstv.cpp:2445-2598 -- fires once per completed 6-bit byte, dispatched on the CURRENT mode
    // (still 4/16/17/18 at this point, distinct from the outer default case covering all of 4+).
    private int? DispatchByte(int fskc)
    {
        switch (_mode)
        {
            case 4: // First SYNC -- sstv.cpp:2446-2464
                if (fskc == VisHeader.NarrowStxByte) // 0x2d
                {
                    _runningXor = 0;
                    _modeCodeByte = 0;
                    _mode = 16;
                }
                else
                {
                    // 0x2a (the unimplemented FSK callsign-ID packet, sstv.cpp:2447) and every other
                    // value are treated identically to legacy's own `else` branch, sstv.cpp:2461-2463.
                    _mode = 0;
                }
                break;

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
                    return _modeCodeByte;
                }
                break;
        }

        return null;
    }

    private int MsToSamples(double ms) => (int)(ms / 1000.0 * _sampleRate);

    private double MsToSamplesExact(double ms) => ms / 1000.0 * _sampleRate;
}
