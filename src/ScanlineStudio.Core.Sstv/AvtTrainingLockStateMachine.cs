namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy's AVT training-sequence lock (`sstv.cpp`'s <c>CSSTVDEM::Do</c>,
/// <c>m_SyncMode</c> cases 4/5/6/7, `sstv.cpp:2155-2233`) -- decodes the 32-block shift-register
/// counter AVT's own header transmits (<see cref="VisHeader.GenerateAvtSegments"/>) using
/// <see cref="PllFmDemodulator"/> at legacy's own real 1500-2300Hz band (piece 9 narrowed it from an
/// earlier, wider 1100-2300Hz once VIS-bit decode stopped depending on this same demodulator, see
/// spec/14-roadmap.md's "Piece 9" entry) -- confirmed by direct measurement that PLL settles cleanly
/// within the tight acceptance bands cases 4/5/6 need, even within a single 9.7646ms bit window:
/// steady-state ripple is ~9.0Hz for the bit-1 tone (1600Hz) and ~2.3Hz for bit-0 (2200Hz), both
/// landing with a solid ~100Hz margin against case 6's threshold (`BitOneMaxHz`/`BitZeroMinHz`,
/// +/-8000/40.96 either side of 1900Hz) -- comfortably wider than the ripple either way, and
/// <see cref="AvtTrainingLockStateMachineTests.FullTrainingSequence_CompletesWithinExpectedBudget"/>
/// confirms a full 32-block sequence still locks correctly end to end at this band.
///
/// **Dedicated `PllFmDemodulator` instance, not shared with the main picture-decode path** (a real
/// wiring change from an earlier version of this class, made when the Hilbert demodulator piece
/// landed): legacy's AVT lock calls (`sstv.cpp:2129/2159/2169/2187/2222`) always use PLL directly,
/// independent of `CSSTVDEM::m_Type` -- true even now that <see cref="AnalogFmSstvDecoder"/>'s main
/// picture path uses <see cref="HilbertFmDemodulator"/>. See <see cref="ProcessSample"/>'s own doc
/// comment for the source citations and the caller's construction of this dedicated instance.
///
/// Surprising finding from reading cases 4-8 in full, not assumed from a partial read: legacy's
/// own case 8 (`sstv.cpp:2234-2239`) is dead code -- <c>m_SyncMode</c> is 8 on entry, so
/// <c>m_SyncMode--</c> makes it 7, never 0, so <c>Start()</c> there is unreachable. This means the
/// training sequence *never* completes via a clean "all 32 blocks decoded" path; <c>Start()</c> is
/// always reached via the overall timeout (<c>m_SyncTime</c>, set once in case 3 to `9 + 2xVIS-block
/// + full-training-sequence` ms, `sstv.cpp:2140`) expiring in case 4, 5, or 6. What makes the port
/// worthwhile despite that: case 6 *recalculates* that overall timeout, smaller, after every
/// successfully-decoded block (`sstv.cpp:2200-2201`, from the block's own position in the 32-block
/// sequence, encoded in its `h` byte) -- so a signal with real, sustained lock finishes close to
/// the actual content end, while a signal that never locks waits out the full nominal duration
/// (exactly what this port's existing fixed-duration skip, <see cref="VisHeader.AvtExtraHeaderDurationMs"/>,
/// already does). This class is therefore a refinement of that existing, already-working path --
/// more accurate completion timing for real captured audio with clock drift between encoder and
/// decoder -- not a replacement, and not a new capability the way <see cref="VisLockStateMachine"/> was.
///
/// Case 7 (<c>WaitNextMarker</c>) does not decrement the overall timeout at all (confirmed: no
/// `m_SyncTime--` anywhere in `sstv.cpp:2221-2233`) -- the budget is frozen while waiting between
/// blocks, only ticking in states 4/5/6. Case 8 is folded directly into case 6's <c>h==0x40</c>
/// (last real block) transition, going straight to <c>WaitNextMarker</c> with case 8's real
/// eventual destination state -- a documented simplification with no functional effect, since real
/// case 8 is a single-sample pass-through (~0.09ms at 11025Hz) that never calls <c>Start()</c> either way.
///
/// This class's own instance is only ever constructed once <see cref="AnalogFmSstvDecoder"/> has
/// already skipped past all 3 VIS repeats (see its <c>TryStartAvtTraining</c>) -- i.e. starting at
/// the training sequence's own first marker, not legacy's real case-3-exit point (which is right
/// after the *first* VIS repeat, ~1835ms earlier, with cases 4-8 then also processing the 2nd/3rd
/// repeats' own audio as failed marker-search noise before ever reaching real training content).
/// The initial overall-timeout budget below is therefore scoped to just the training sequence's own
/// nominal duration (<see cref="VisHeader.AvtTrainingSequenceDurationMs"/>), not legacy's full
/// `9 + 2xVIS-block + training` (`sstv.cpp:2140`) -- using the full legacy figure here would double-
/// count the 2 VIS-repeat durations already skipped by the caller. This was a real bug caught by
/// independent review: an earlier version used the full legacy figure, which (harmlessly, since
/// <c>AnalogFmSstvDecoder</c>'s own separately-computed fallback deadline always fires first at the
/// decoder level) meant this class's isolated internal timeout didn't reflect its own real starting
/// point -- see <c>AvtTrainingLockStateMachineTests.NoTrainingSignalAtAll_CompletesAtTheFullNominalBudget</c>.
/// </summary>
internal sealed class AvtTrainingLockStateMachine
{
    private const double MarkerBandHalfWidthHz = 1000.0 / 40.96; // sstv.cpp:2160's |d|<=1000, converted per PllFmDemodulator's doc comment
    private const double ConfirmBandHalfWidthHz = 800.0 / 40.96; // sstv.cpp:2170's |d|<=800
    private const double BitOneMaxHz = 1900.0 - 8000.0 / 40.96; // sstv.cpp:2190's d>=8000 => bit 1
    private const double BitZeroMinHz = 1900.0 + 8000.0 / 40.96; // sstv.cpp:2190's d<-8000 => bit 0

    private const double MarkerConfirmHoldMs = VisHeader.AvtTrainingBitDurationMs * 0.5; // sstv.cpp:2162/2225
    private const double BitWindowMs = VisHeader.AvtTrainingBitDurationMs; // sstv.cpp:2174
    private const double NextBlockWaitMs = VisHeader.AvtTrainingBitDurationMs * 0.7; // sstv.cpp:2200, h!=0x40 branch
    private const double LastBlockWaitMs = VisHeader.AvtTrainingBitDurationMs * 0.5 - 0.8; // sstv.cpp:2206, h==0x40 branch
    private const double BlockDurationMs = 165.9982; // sstv.cpp:2201's literal -- equals 17*9.7646 (1 marker + 16 bits), kept literal per source
    private const double OverallTimeoutMarginMs = 9.0; // sstv.cpp:2140's leading "9 +"

    private enum LockState { MarkerSearch, MarkerConfirm, DecodeBits, WaitNextMarker }

    private readonly double _sampleRate;

    private LockState _state = LockState.MarkerSearch;
    private int _sampleCounter;
    private int _overallTimeoutCounter; // m_SyncTime -- only ticks in MarkerSearch/MarkerConfirm/DecodeBits
    private int _phaseCounter; // m_SyncATime
    private int _visData;
    private int _visCount;

    public AvtTrainingLockStateMachine(double sampleRate)
    {
        _sampleRate = sampleRate;
        _overallTimeoutCounter = MsToSamples(OverallTimeoutMarginMs + VisHeader.AvtTrainingSequenceDurationMs);
    }

    /// <summary>Feeds one already-demodulated frequency (Hz) sample -- caller-supplied, deliberately
    /// NOT reused from <see cref="AnalogFmSstvDecoder"/>'s main picture-decode demodulator output.
    /// Legacy's AVT lock state machine (`sstv.cpp:2129/2159/2169/2187/2222`) always calls
    /// <c>m_pll.Do(ad)</c> directly, regardless of <c>CSSTVDEM::m_Type</c> -- every one of those call
    /// sites sits outside the <c>m_Type</c>-dispatched switch the main picture demodulation goes
    /// through (`sstv.cpp:2255-2269`). Legacy always uses PLL for AVT lock detection even when
    /// Hilbert (or zero-crossing) is the active picture demodulator. Before the Hilbert demodulator
    /// piece, this class's own caller reused the shared main-path stream on the (then-true, now-false)
    /// premise that both were literally the same demodulator; the caller now feeds this method from a
    /// dedicated <see cref="PllFmDemodulator"/> instance instead, matching legacy's real
    /// dual-demodulator structure. Returns the sample index (relative to the very first sample passed
    /// to this instance) at which the overall timeout expired -- i.e. where line 0 begins -- or null
    /// if not yet done.</summary>
    public int? ProcessSample(double demodulatedHz)
    {
        var currentSample = _sampleCounter++;

        switch (_state)
        {
            case LockState.MarkerSearch: // sstv.cpp:2155-2164
                if (--_overallTimeoutCounter == 0)
                {
                    return currentSample;
                }

                if (Math.Abs(demodulatedHz - 1900.0) <= MarkerBandHalfWidthHz)
                {
                    _state = LockState.MarkerConfirm;
                    _phaseCounter = MsToSamples(MarkerConfirmHoldMs);
                }

                break;

            case LockState.MarkerConfirm: // sstv.cpp:2165-2182
                if (--_overallTimeoutCounter == 0)
                {
                    return currentSample;
                }

                if (Math.Abs(demodulatedHz - 1900.0) <= ConfirmBandHalfWidthHz)
                {
                    if (--_phaseCounter == 0)
                    {
                        _state = LockState.DecodeBits;
                        _phaseCounter = MsToSamples(BitWindowMs);
                        _visData = 0;
                        _visCount = 16;
                    }
                }
                else
                {
                    _state = LockState.MarkerSearch;
                }

                break;

            case LockState.DecodeBits: // sstv.cpp:2183-2220
                if (--_overallTimeoutCounter == 0)
                {
                    return currentSample;
                }

                if (--_phaseCounter == 0)
                {
                    if (demodulatedHz > BitOneMaxHz && demodulatedHz <= BitZeroMinHz)
                    {
                        // Neither confidently bit-1 nor bit-0 -- too close to center, lost lock.
                        _state = LockState.MarkerSearch;
                        break;
                    }

                    _phaseCounter = MsToSamples(BitWindowMs);
                    _visData <<= 1; // sstv.cpp:2192, MSB-first (opposite of VisLockStateMachine's LSB-first shift)
                    if (demodulatedHz <= BitOneMaxHz)
                    {
                        _visData |= 1;
                    }

                    if (--_visCount != 0)
                    {
                        break;
                    }

                    var l = _visData & 0xff;
                    var h = (_visData >> 8) & 0xff;
                    if ((l + h) == 0xff && l is >= 0xa0 and <= 0xbf && h is >= 0x40 and <= 0x5f) // sstv.cpp:2198
                    {
                        _state = LockState.WaitNextMarker;
                        if (h != 0x40)
                        {
                            _phaseCounter = MsToSamples(NextBlockWaitMs);
                            _overallTimeoutCounter = MsToSamples((h - 0x40) * BlockDurationMs - 0.8); // sstv.cpp:2201
                        }
                        else
                        {
                            // sstv.cpp:2204-2209. Case 8's own single-sample pass-through is folded
                            // in here (see class doc comment) -- both branches end up waiting in
                            // WaitNextMarker either way, with no marker left to actually find.
                            //
                            // _phaseCounter is deliberately NOT touched here, matching legacy exactly:
                            // sstv.cpp:2204-2209 only ever reassigns m_SyncTime, never m_SyncATime, in
                            // this branch -- it's already at a full bit-window's worth from the
                            // unconditional assignment at line 131 above (sstv.cpp:2191's own
                            // unconditional `m_SyncATime = 9.7646*SampFreq/1000`, which runs for every
                            // bit including this block's last one, before this validity check is ever
                            // reached). An earlier version of this method overwrote it with
                            // LastBlockWaitMs here -- a real bug (not part of the case-8-folding
                            // decision at all), caught by independent review: it made this class
                            // complete ~63 samples (~5.7ms at 11025Hz) earlier than legacy after a
                            // clean 32-block lock, invisible to AvtTrainingLockStateMachineTests'
                            // wide (+/-300ms) completion-time tolerance.
                            if (_overallTimeoutCounter == 0 || _overallTimeoutCounter >= MsToSamples(BitWindowMs))
                            {
                                _overallTimeoutCounter = MsToSamples(LastBlockWaitMs);
                            }
                        }
                    }
                    else
                    {
                        _state = LockState.MarkerSearch;
                    }
                }

                break;

            case LockState.WaitNextMarker: // sstv.cpp:2221-2233 -- overall timeout frozen here
                if (Math.Abs(demodulatedHz - 1900.0) <= MarkerBandHalfWidthHz)
                {
                    _state = LockState.MarkerConfirm;
                    _phaseCounter = MsToSamples(MarkerConfirmHoldMs);
                }
                else if (--_phaseCounter == 0)
                {
                    _state = LockState.MarkerSearch;
                }

                break;
        }

        return null;
    }

    // ultracode audit finding #11 -- see VisLockStateMachine.MsToSamples' doc comment for the full
    // rationale: legacy's int assignment truncates (C narrowing), not rounds; kept as truncation here
    // purely for golden-vector parity, not because it's more "correct" than rounding.
    private int MsToSamples(double ms) => (int)(ms / 1000.0 * _sampleRate);

    /// <summary>Test-only visibility into <see cref="MsToSamples"/> (ultracode audit finding #11).</summary>
    internal int MsToSamplesForTests(double ms) => MsToSamples(ms);
}
