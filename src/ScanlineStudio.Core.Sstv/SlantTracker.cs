namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy's "Auto Slant" (`Main.cpp`'s <c>AutoStopJob</c>, the <c>KRSA-&gt;Checked</c>
/// branch only, `Main.cpp:3968-4018`, plus the shared position-history bookkeeping every call does,
/// `3964-3966`, and <c>GetSqerrPos</c>, `Main.cpp:3867-3880`) — automatic sample-clock-rate drift
/// correction, on by default in the shipped legacy config (<c>Mmsstv.ini</c>'s <c>AutoSlant=1</c>).
/// Each line, the caller reports where the sync-tone envelope peaked relative to that mode's
/// expected sync position (see <see cref="SyncEnvelopeDetector"/>); this class fits a trend line
/// through the last 5 readings, and once it has enough lines of consistent trend, computes a
/// corrected samples-per-line value and reports it once a staged confidence-threshold ladder
/// decides the drift is real (not noise).
///
/// Deliberately NOT ported (out of scope for "slant" specifically, per user direction): legacy
/// bundles this together with two *different* features in the same function --
/// "Auto Stop" (<c>sys.m_AutoStop</c>, stops the recording when the sync position looks stable,
/// i.e. the image looks finished) and "Auto Sync" (<c>sys.m_AutoSync</c>, snaps the read pointer to
/// recover from a sync glitch/jump) -- both left out here as separate concerns, not slant
/// correction. Also not ported: the <c>!SBTX-&gt;Down</c>/echo/TX-sample-offset gate, trivially
/// satisfied for a pure RX decoder with no simultaneous-TX concept (this port's decoder never has
/// <c>SBTX-&gt;Down</c> true), so it's correctly omitted rather than silently dropped.
/// <c>m_AutoSyncCount</c>, the third condition on that same line (<c>Main.cpp:3968</c>), IS modeled --
/// not in this class but in its caller: a manual ReSync sets it (<c>Main.cpp:14017</c>) and it stays
/// set until the next reception starts (<c>Main.cpp:4994</c>), which the caller ports by calling
/// <see cref="ProcessLineHistoryOnly"/> instead of <see cref="ProcessLine"/> for every subsequent
/// line of that image. An earlier version of this comment called that guard "trivially satisfied,"
/// which was true only before manual ReSync existed.
///
/// Scope note on what "applying" the correction means here, UPDATED by RX buffer subsystem Phase 6c
/// (this paragraph previously said this port applies a committed correction going forward only --
/// no longer true): legacy retroactively re-decodes the *entire* image received so far at the
/// corrected rate once a correction commits (<c>RedrawSampFreq</c> replaying a staging buffer,
/// <c>Main.cpp:5586-5629</c>). This port now does the same, when <c>RxBufferMode.On</c> is
/// selected -- <c>AnalogFmSstvDecoder.PerformReplay</c> retroactively redraws every already-decoded
/// row from <c>RxLineStagingBuffer</c>'s staged history using the corrected origin/stride, reusing
/// this class's own <see cref="ProcessLineSuppressed"/>/<see cref="ResetBaseline"/> for the re-feed.
/// Under <c>RxBufferMode.Off</c> (no staging buffer exists) this port still only applies a
/// committed correction going forward, exactly as this paragraph originally described -- a real,
/// bounded, mode-dependent scoping choice now, not a blanket one.
/// </summary>
internal sealed class SlantTracker
{
    private const int HistorySize = 16;
    private const int FitPoints = 5; // GetSqerrPos is only ever called with n=5 in legacy

    private readonly int[] _history = new int[HistorySize];
    private readonly double[] _limitsHz;
    private readonly int[] _thresholdLinePositions;
    private readonly double _lineDurationMs;
    private readonly double _sampleRate;
    private readonly int _mult;
    private readonly MovingAverage _correctionAverage = new(HistorySize);

    // Main.cpp:3994's `SSTVSET.m_SampFreq`/`SSTVSET.m_TW` are BOTH the evolving, already-corrected
    // values, kept in sync by SetSampFreq() recomputing m_TW = GetTiming(mode)*m_SampFreq/1000 after
    // every commit -- not the fixed hardware rate. _sampleRate above stays fixed (it matches the
    // *different*, genuinely-fixed `SampFreq` used for the m_ASLmt/clamp constants, Main.cpp:1645
    // and 4012-4014). These two mutable fields track the evolving pair; keeping them in sync is
    // exactly the bug an earlier version of this class had -- _nominalSamplesPerLine was frozen at
    // its construction-time value forever, so after the first correction the drift formula's
    // numerator (fixed _sampleRate) and denominator (stale _nominalSamplesPerLine) silently drifted
    // out of the proportional relationship legacy maintains, corrupting every correction after the
    // first. Caught by an end-to-end mistuned-rate test, not by the isolated unit tests above (which
    // never exercised more than one correction in sequence).
    private double _currentSampleRate;
    private double _nominalSamplesPerLine;

    private int _totalLinesObserved;
    private int _linesSinceBaseline;
    private int _bitMask;
    // Legacy's `int m_ASBgnPos` with its literal 0x7fffffff "no baseline yet" sentinel
    // (Main.h:1360, Main.cpp:3807/3989). _hasBaseline carries the actual decision; the sentinel is
    // kept for type/value fidelity with legacy.
    private int _baselinePosition = int.MaxValue;
    private bool _hasBaseline;

    /// <param name="sampleRate">This decoder's sample rate.</param>
    /// <param name="nominalSamplesPerLine">The mode's nominal (uncorrected) samples-per-line, i.e.
    /// <c>mode.LineDurationMs/1000*sampleRate</c> -- legacy's <c>SSTVSET.m_TW</c>.</param>
    /// <param name="thresholdLinePositions">Mode-dependent <c>m_ASPos[0..3]</c> (`Main.cpp:3812-3856`)
    /// -- line counts at which progressively smaller drift thresholds become active. See
    /// <see cref="SstvModeRegistry"/>'s per-mode grouping.</param>
    public SlantTracker(double sampleRate, double nominalSamplesPerLine, int[] thresholdLinePositions)
    {
        _sampleRate = sampleRate;
        _currentSampleRate = sampleRate;
        _nominalSamplesPerLine = nominalSamplesPerLine;
        _lineDurationMs = nominalSamplesPerLine / sampleRate * 1000.0;
        _thresholdLinePositions = thresholdLinePositions;
        _mult = Math.Max(1, (int)(nominalSamplesPerLine / 320.0)); // Main.cpp:3860, literal 320 regardless of actual image width

        // m_ASLmt[0..6] = {25,10,2,0.5,0.2,0.2,0.08} * SampFreq/11025 (Main.cpp:1645-1651).
        double[] literalHz = [25.0, 10.0, 2.0, 0.5, 0.2, 0.2, 0.08];
        _limitsHz = Array.ConvertAll(literalHz, v => v * sampleRate / 11025.0);
    }

    /// <summary>Reports one line's sync-envelope peak position, in samples, relative to where this
    /// mode's sync segment is expected to start (already wrapped to the representation closest to
    /// zero — see <c>AnalogFmSstvDecoder</c>). Returns a corrected samples-per-line value once a
    /// drift correction commits, else null.</summary>
    /// <param name="hasStagingBuffer">Legacy's <c>UpdateSampFreq</c> guard,
    /// <c>if( (dp-&gt;m_StgBuf != NULL) || WaveStg.IsOpen() )</c> (`Main.cpp:5597`) -- the caller's
    /// <c>_rxLineStagingBuffer is not null</c>. Gates ONLY the post-commit
    /// <see cref="Reset"/> (legacy's <c>InitAutoStop()</c>, `Main.cpp:5600`, INSIDE that guard), never
    /// the rate write (`Main.cpp:4015-4016`, and `:5596`'s bare <c>SetSampFreq()</c>, both OUTSIDE it).
    /// Deliberately has NO default value: a defaulted commit-semantics flag is exactly the silent
    /// mis-pass hazard <see cref="RestoreRateWithoutReset"/>'s own doc comment warns about.</param>
    public double? ProcessLine(int relativePositionSamples, bool hasStagingBuffer)
        => ProcessLineCore(relativePositionSamples, suppressCommit: false, hasStagingBuffer);

    /// <summary>RX buffer subsystem Phase 6b -- the third code path round-1 plan-review found missing:
    /// legacy's own suppressed-replay re-feed (`Main.cpp:3989-4017`, `m_ASDis=1` bracketed) runs the
    /// SAME fit/baseline/average/bitmask-latching logic <see cref="ProcessLine"/> does -- only the
    /// FINAL `SSTVSET.m_SampFreq` write (`:4011-4017`) is gated on `!m_ASDis`, everything upstream of
    /// it, including the bitmask latches at `:4006-4010`, is NOT. Neither of this class's other two
    /// public methods matches that shape: <see cref="ProcessLine"/> commits too much (would advance
    /// <see cref="_currentSampleRate"/>/<see cref="_nominalSamplesPerLine"/> and call
    /// <see cref="Reset"/>, corrupting the very history a replay pass is supposed to be rebuilding, not
    /// mutating going-forward state); <see cref="ProcessLineHistoryOnly"/> skips the fit/baseline/
    /// average/bitmask entirely (that method's own real purpose, the `m_AutoSyncCount`-disabled-for-
    /// rest-of-image case, is a DIFFERENT legacy gate than replay suppression). Discards the
    /// would-be-corrected rate rather than returning it (unlike <see cref="ProcessLine"/>) --
    /// deliberately: nothing should ever act on this quantity during a suppressed pass, and exposing it
    /// would invite a future caller to "notice" and apply it, exactly what legacy's own `m_ASDis`
    /// exists to prevent.</summary>
    public void ProcessLineSuppressed(int relativePositionSamples)
        // `hasStagingBuffer: true` is both structurally unreachable AND factually correct here, so it
        // stays correct either way: unreachable because TryComputeCorrection returns at its own
        // `if (suppressCommit)` before the flag is ever read (legacy's `!m_ASDis` gate,
        // Main.cpp:4011); factually correct because a suppressed pass only exists during replay, which
        // only exists when a staging buffer does (PerformReplay's own `_rxLineStagingBuffer is null`
        // bail). Passed as `true` rather than `false` so it survives a future restructuring of that
        // early return without silently changing behaviour.
        => ProcessLineCore(relativePositionSamples, suppressCommit: true, hasStagingBuffer: true);

    private double? ProcessLineCore(int relativePositionSamples, bool suppressCommit, bool hasStagingBuffer)
    {
        // Main.cpp:3964-3966: unconditional history shift + push, every call. `int[]` matches
        // legacy's `int m_AutoStopAPos[16]` (Main.h:1351) -- the caller has already applied
        // legacy's own truncation chain (see AnalogFmSstvDecoder's `relative`).
        Array.Copy(_history, 1, _history, 0, HistorySize - 1);
        _history[HistorySize - 1] = relativePositionSamples;
        _totalLinesObserved++;

        double? result = null;

        if (_totalLinesObserved >= FitPoints)
        {
            // Main.cpp:3971-3981: jitter gate over the last 5 consecutive deltas (indices 15 down to
            // 10 inclusive -- 5 deltas, one further back than the 5-point fit window below).
            // ultracode audit finding #7: the loop bound used to be `i > HistorySize - FitPoints`
            // (11), an off-by-one that checked only 4 deltas (indices 15..12) instead of legacy's 5,
            // making this gate a strict superset-permissive subset of legacy's real check.
            // Main.cpp:3971-3981 -- `int df = ABS(int - int)` against the int `8*m_Mult`. With an
            // int history this comparison is now exact, with no float-boundary ambiguity at
            // df == 8*mult (previously a fractional maxDelta of 159.9999... vs 160 could flip it).
            var maxDelta = 0;
            for (var i = HistorySize - 1; i > HistorySize - FitPoints - 1; i--)
            {
                maxDelta = Math.Max(maxDelta, Math.Abs(_history[i] - _history[i - 1]));
            }

            if (maxDelta < 8 * _mult)
            {
                var fittedPosition = GetSqerrPos();

                if (!_hasBaseline)
                {
                    _baselinePosition = fittedPosition;
                    _linesSinceBaseline = 0;
                    _hasBaseline = true;
                }
                else if (_linesSinceBaseline >= 3)
                {
                    result = TryComputeCorrection(fittedPosition, suppressCommit, hasStagingBuffer);
                }
            }
        }

        _linesSinceBaseline++;
        return result;
    }

    /// <summary>Records one line's peak position into the history WITHOUT running the jitter gate, the
    /// fit, the baseline capture, or the correction ladder — the caller's port of legacy's
    /// <c>m_AutoSyncCount</c> gate (<c>Main.cpp:3968</c>: <c>if( KRSA-&gt;Checked &amp;&amp;
    /// !m_AutoSyncCount &amp;&amp; ... )</c>), which a manual ReSync sets and which stays set for the
    /// rest of the reception. While that gate is closed legacy still runs <c>AutoStopJob</c>'s own
    /// unconditional bookkeeping — the history shift/push and <c>m_AutoStopACnt++</c>
    /// (<c>Main.cpp:3964-3966</c>) plus <c>m_ASCurY++</c> (<c>Main.cpp:4032</c>, verified by brace
    /// count to sit OUTSIDE the <c>:3968</c> gate but inside <c>:3886</c>'s outer one) — and skips
    /// everything from <c>:3968</c> through <c>:4031</c>.
    ///
    /// Calling <see cref="ProcessLine"/> and discarding its return value is NOT equivalent to this:
    /// that would still set <see cref="_baselinePosition"/>/<see cref="_hasBaseline"/>, feed
    /// <see cref="_correctionAverage"/>, latch <see cref="_bitMask"/>, advance
    /// <see cref="_currentSampleRate"/>/<see cref="_nominalSamplesPerLine"/>, and — via
    /// <see cref="Reset"/> on any committed correction — <c>Array.Clear</c> the very history it is
    /// supposed to be preserving.</summary>
    public void ProcessLineHistoryOnly(int relativePositionSamples)
    {
        // Main.cpp:3964-3966 -- byte-for-byte ProcessLine's own first three statements.
        Array.Copy(_history, 1, _history, 0, HistorySize - 1);
        _history[HistorySize - 1] = relativePositionSamples;
        _totalLinesObserved++;

        // Main.cpp:4032's m_ASCurY++ -- ProcessLine's own trailing `_linesSinceBaseline++`, which
        // legacy also runs regardless of the :3968 gate.
        _linesSinceBaseline++;
    }

    /// <summary><c>GetSqerrPos(5)</c> (`Main.cpp:3867-3881`) — least-squares linear fit's value at
    /// i=0 (the most recent sample) over the last 5 history entries. Legacy accumulates in
    /// <c>double</c> but is declared <c>int __fastcall GetSqerrPos(int n)</c> (Main.h:1342) and
    /// returns a <c>double l0</c>, so the fit result is TRUNCATED TOWARD ZERO on return -- not
    /// rounded, and specifically not banker's-rounded. That truncated value is what becomes
    /// <c>m_ASBgnPos</c> and what the drift formula differences against, so it is observable, not
    /// cosmetic.</summary>
    private int GetSqerrPos()
    {
        double t = 0, l = 0, tt = 0, tl = 0;
        for (var i = 0; i < FitPoints; i++)
        {
            t += i;
            var value = _history[HistorySize - 1 - i];
            l += value;
            tt += (double)i * i;
            tl += i * value; // legacy's `TL += i * l` -- int*int, both operands small and bounded
        }

        return (int)((l * tt - t * tl) / (FitPoints * tt - t * t));
    }

    /// <summary>Main.cpp:3994-4017 -- the drift calculation and staged threshold ladder.
    /// <paramref name="suppressCommit"/> (RX buffer subsystem Phase 6b) mirrors legacy's own
    /// `if(!m_ASDis){ SSTVSET.m_SampFreq = ...; }` (`Main.cpp:4011-4017`) exactly: the bitmask
    /// latching below (`:4006-4010`) is NOT gated by it (matches legacy -- those bits latch during a
    /// suppressed replay pass too) -- only the trailing `_currentSampleRate`/`_nominalSamplesPerLine`
    /// write and <see cref="Reset"/> call are skipped when <see langword="true"/>.</summary>
    private double? TryComputeCorrection(int fittedPosition, bool suppressCommit, bool hasStagingBuffer)
    {
        var d = (_baselinePosition - fittedPosition) * _currentSampleRate / _nominalSamplesPerLine / _linesSinceBaseline;
        var candidateSampleRate = _correctionAverage.Add(_currentSampleRate - d);
        d = _currentSampleRate - candidateSampleRate;

        var triggered =
            ((_bitMask & 1) == 0 && Math.Abs(d) >= _limitsHz[0])
            || ((_bitMask & 1) == 0 && _linesSinceBaseline >= 16 && Math.Abs(d) >= _limitsHz[1])
            || ((_bitMask & 1) == 0 && _linesSinceBaseline >= 32 && Math.Abs(d) >= _limitsHz[2])
            || ((_bitMask & 2) == 0 && _linesSinceBaseline >= _thresholdLinePositions[0] && Math.Abs(d) >= _limitsHz[3])
            || ((_bitMask & 4) == 0 && _linesSinceBaseline >= _thresholdLinePositions[1] && Math.Abs(d) >= _limitsHz[4])
            || ((_bitMask & 8) == 0 && _linesSinceBaseline >= _thresholdLinePositions[2] && Math.Abs(d) >= _limitsHz[5])
            || ((_bitMask & 16) == 0 && _linesSinceBaseline >= _thresholdLinePositions[3] && Math.Abs(d) >= _limitsHz[6]);

        if (!triggered)
        {
            return null;
        }

        if (_linesSinceBaseline >= 32)
        {
            _bitMask |= 1;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[0])
        {
            _bitMask |= 2;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[1])
        {
            _bitMask |= 4;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[2])
        {
            _bitMask |= 8;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[3])
        {
            _bitMask |= 16;
        }

        var correctedRate = ClampAndNormalizeAutoSlantRate(candidateSampleRate, _sampleRate);

        // RX buffer subsystem Phase 6b: legacy's own `if(!m_ASDis){...}` gate (Main.cpp:4011) wraps
        // exactly this block (the SetSampFreq-consistency comment below and the InitAutoStop-after-
        // commit reset it describes) -- a suppressed replay pass computes everything above (the
        // bitmask latches included) but must not advance state here.
        if (suppressCommit)
        {
            return correctedRate;
        }

        // Main.cpp:5596's SSTVSET.SetSampFreq() recomputing m_TW from the just-corrected m_SampFreq,
        // reached after every commit via m_ReqSampChg -> RedrawSampFreq -> UpdateSampFreq -- keeps the
        // next correction's drift formula self-consistent instead of dividing by a stale denominator.
        // The paired InitAutoStop wipe (Main.cpp:5600) is NOT unconditional -- see AdoptCorrectedRate,
        // which owns that split so this call site and any future commit call site can never disagree
        // about which fields a "the rate is now X" commit actually updates.
        AdoptCorrectedRate(correctedRate, hasStagingBuffer);

        return correctedRate;
    }

    /// <summary>Applies a corrected rate as this class's own <see cref="TryComputeCorrection"/> would
    /// on the AUTOMATIC commit path. Also fixes <see cref="DriftPpm"/> (and therefore
    /// <c>ISstvDecoder.SlantPpm</c>) reflecting whatever last corrected the rate, matching legacy's
    /// own <c>DrawSlantInfo</c> (`Main.cpp:5537`), which reads the same shared <c>m_SampFreq</c>
    /// regardless of which mechanism last corrected it. The manual Correct Slant path (both its
    /// forward commit and its revert) uses <see cref="RestoreRateWithoutReset"/> instead -- see that
    /// method's own doc comment for why the two must not share a code path.</summary>
    /// <param name="hasStagingBuffer">See <see cref="ProcessLine"/>'s own doc comment for the full
    /// legacy citation. Gates only <see cref="Reset"/> -- <see cref="SetRatePair"/> is unconditional
    /// in legacy (`Main.cpp:4015-4016`/`:5596`) and stays unconditional here.</param>
    internal void AdoptCorrectedRate(double correctedSampleRate, bool hasStagingBuffer)
    {
        SetRatePair(correctedSampleRate);

        // Batch 2 chunk 2b round-2 fix: legacy's post-commit InitAutoStop (Main.cpp:3801-3810) is
        // reached ONLY through UpdateSampFreq's staging-buffer guard
        // (`if( (dp->m_StgBuf != NULL) || WaveStg.IsOpen() )`, Main.cpp:5597 wrapping the :5600 call).
        // With no staging buffer (this port's RxBufferMode.Off; legacy's sys.m_UseRxBuff == 0, still
        // reachable there -- Main.cpp:11903 only DISABLES the Auto Slant menu item, it never clears
        // KRSA->Checked, and :3968 reads only Checked) legacy keeps m_ASBgnPos / m_AutoStopAPos /
        // m_AutoStopACnt / m_ASAvg / m_ASBitMask across every automatic commit: m_ASCurY keeps growing
        // so the drift term shrinks as 1/m_ASCurY, and the latched tier bits keep the coarse m_ASLmt[0]
        // (+/-25Hz * SampFreq/11025) permanently disabled -- corrections tighten monotonically instead
        // of restarting from an unlatched bitmask and an 8-line re-arm (5 lines to refill the fit
        // window + 3 more before _linesSinceBaseline >= 3 is satisfied again).
        if (hasStagingBuffer)
        {
            Reset();
        }
    }

    /// <summary>Legacy's bare <c>SSTVSET.m_SampFreq = ...; SSTVSET.SetSampFreq();</c> with NO
    /// <c>InitAutoStop</c> -- the rate half of a commit only. Used by the manual Correct Slant path
    /// (both its forward commit and its revert), because in legacy <c>InitAutoStop</c> is reached
    /// only from INSIDE <c>UpdateSampFreq</c> (<c>Main.cpp:5600</c>), i.e. only on the success arm
    /// (<c>Main.cpp:5418</c>'s <c>RedrawSampFreq(FALSE)</c> -> <c>:5585</c> -> <c>:5600</c>). The
    /// revert arm (<c>Main.cpp:5420-5423</c>) is a bare <c>m_SampFreq = StartSamp; SetSampFreq();</c>
    /// -- baseline, 16-entry history, moving average and bitmask ALL survive a reverted manual
    /// correction in legacy. This port's <c>PerformReplay</c> supplies the success-arm reset at its
    /// own <see cref="ResetBaseline"/> call, which sits after every one of its early-exit
    /// <c>return false</c>s and before every <c>return true</c> -- the same boundary legacy draws.
    /// Deliberately a separate method rather than a bool parameter on
    /// <see cref="AdoptCorrectedRate"/>: a mis-passed flag on a commit API is silent.</summary>
    internal void RestoreRateWithoutReset(double sampleRate) => SetRatePair(sampleRate);

    private void SetRatePair(double sampleRate)
    {
        _currentSampleRate = sampleRate;
        _nominalSamplesPerLine = _lineDurationMs / 1000.0 * sampleRate;
    }

    /// <summary>RX buffer subsystem Phase 6b -- public exposure of <see cref="Reset"/> for a replay
    /// pass to call before it starts (mirroring legacy's own `InitAutoStop`-before-every-replay shape,
    /// `Main.cpp:5600`/`:5679`/`:5741`, immediately before `m_ASDis=1`) -- a thin, deliberately
    /// non-duplicated wrapper around the SAME method <see cref="TryComputeCorrection"/> already calls
    /// after every commit, so the two call sites can never drift apart on which fields get cleared.</summary>
    internal void ResetBaseline() => Reset();

    /// <summary>Resets all per-baseline state to legacy's <c>InitAutoStop</c> defaults
    /// (`Main.cpp:3801-3810`), called immediately after every correction commits (see
    /// <see cref="TryComputeCorrection"/>) -- ultracode audit finding #9. Deliberately does NOT
    /// touch <see cref="_currentSampleRate"/>/<see cref="_nominalSamplesPerLine"/>: those are the
    /// evolving corrected rate itself, not per-baseline bookkeeping.</summary>
    private void Reset()
    {
        Array.Clear(_history);
        _totalLinesObserved = 0;
        _linesSinceBaseline = 0;
        _hasBaseline = false;
        _baselinePosition = int.MaxValue;
        _bitMask = 0;
        _correctionAverage.Clear();
    }

    /// <summary><c>NormalSampFreq</c> (`ComLib.cpp:203-207`) -- rounds to the nearest 1/m fraction.</summary>
    private static double NormalSampleRate(double value, double precision) => (int)(value * precision + 0.5) / precision;

    /// <summary>Live Auto Slant clamp and normalization from <c>Main.cpp:4012-4015</c>. The clamp
    /// uses the fixed declared rate, not the evolving corrected rate (matching legacy's bare
    /// <c>SampFreq</c> there).</summary>
    internal static double ClampAndNormalizeAutoSlantRate(double candidateSampleRate, double declaredSampleRate)
    {
        var clampedRate = Math.Min(candidateSampleRate, declaredSampleRate * 1100.0 / 1060.0);
        return NormalSampleRate(clampedRate, 50);
    }

    /// <summary>Current drift, in parts-per-million relative to the fixed declared sample rate --
    /// legacy's own <c>DrawSlantInfo</c> ppm readout formula (`Main.cpp:5537`:
    /// <c>(SSTVSET.m_SampFreq - sys.m_SampFreq) * 1e6 / sys.m_SampFreq</c>), with this class's
    /// <see cref="_currentSampleRate"/>/<see cref="_sampleRate"/> standing in for
    /// <c>SSTVSET.m_SampFreq</c>/<c>sys.m_SampFreq</c> respectively (see their own field doc
    /// comments). Zero until the first correction commits, matching legacy's own
    /// <c>SSTVSET.m_SampFreq == sys.m_SampFreq</c> until then.</summary>
    internal double DriftPpm => (_currentSampleRate - _sampleRate) * 1_000_000.0 / _sampleRate;

    /// <summary>Test-only visibility into whether a baseline has been established yet -- lets a test
    /// directly observe the jitter gate's pass/fail outcome (ultracode audit finding #7) without
    /// waiting the further 3 lines a resulting correction would need.</summary>
    internal bool HasBaselineForTests => _hasBaseline;

    /// <summary>Test-only visibility into the confidence-tier latch bits (Main.cpp's <c>m_ASBitMask</c>)
    /// -- RX buffer subsystem Phase 6b: lets a test confirm <see cref="ProcessLineSuppressed"/> latches
    /// these the same way a real commit does (Main.cpp:4006-4010, outside the `!m_ASDis` gate), not
    /// just that it withholds the final rate write.</summary>
    internal int BitMaskForTests => _bitMask;

    /// <summary>Test-only visibility into how many lines have been recorded via <see cref="ProcessLine"/>
    /// or <see cref="ProcessLineHistoryOnly"/> combined -- lets a test confirm history recording
    /// actually happened via <see cref="ProcessLineHistoryOnly"/> without relying on
    /// <see cref="HasBaselineForTests"/>, which stays false in that mode by design and so can't
    /// distinguish "history recorded, corrections disabled" from "nothing happened at all".</summary>
    internal int TotalLinesObservedForTests => _totalLinesObserved;

    /// <summary>Test-only visibility into the raw recorded-position history buffer -- a defensive
    /// copy, since the real field is mutated in place by every subsequent call.</summary>
    internal int[] HistoryForTests => (int[])_history.Clone();

    /// <summary>Test-only visibility into <c>m_ASBgnPos</c> (Main.h:1360) -- the captured baseline is
    /// the direct, unaveraged output of <see cref="GetSqerrPos"/>, so this is the only place a test
    /// can observe that fit's TRUNCATION rule (toward zero, per legacy's `int GetSqerrPos`) without
    /// it being laundered through the moving average and the NormalSampFreq quantizer.</summary>
    internal int BaselinePositionForTests => _baselinePosition;
}
