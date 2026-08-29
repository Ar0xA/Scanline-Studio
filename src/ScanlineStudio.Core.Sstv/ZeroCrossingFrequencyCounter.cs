using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CFQC</c> (`sstv.cpp:347-489`) — a zero-crossing-interval frequency
/// discriminator. Used in two roles, matching legacy's single <c>m_fqc</c>: AFC's frequency-
/// measurement input (<c>CSSTVDEM::SyncFreq</c>'s <c>m_fqc.Do(m_lvl.m_Cur)</c>, `m_Type==0`,
/// `sstv.cpp:2258`, see <see cref="AfcTracker"/>) and, entirely independently, the main picture
/// demodulator when the user selects <c>DemodType.ZeroCrossing</c> (`m_Type==1`, `sstv.cpp:2262`)
/// -- split into two separate instances here, see <c>AnalogFmSstvDecoder</c>'s own field comment.
/// Operates on the raw incoming audio sample directly (matching legacy's <c>m_lvl.m_Cur</c>), since
/// zero-crossing interval timing is amplitude-independent — no AGC needed, unlike the PLL path.
///
/// Kept in real Hz throughout rather than legacy's internal <c>(freq-Center)/BWH</c>-then-x16384
/// scale. <see cref="IirFilter.Process"/> is a linear biquad cascade with a DC gain of exactly 1,
/// so ONCE THE OUTPUT FILTER HAS SETTLED, filtering <c>(freq-Center)/BWH</c> and denormalizing
/// afterward is algebraically identical to filtering <c>freq</c> directly. It is NOT identical
/// transiently: legacy's filter Z-state is stored in normalized units, so a <see cref="SetWidth"/>
/// implicitly re-scales that state too (legacy's reported Hz jumps ~272Hz instantly at a wide-to-
/// narrow flip, then holds), whereas this port's Hz-domain state is left alone and glides toward
/// the new value instead. Accepted, bounded divergence: &lt;=~272Hz decaying over the 900Hz/order-3
/// output filter's settling time (~1.5ms, ~17 samples at 11025Hz), only at a narrow-mode
/// transition -- Tier A Batch 7 chunk 7c independently re-derived this from <see cref="IirFilter"/>'s
/// own DC-gain algebra rather than trusting the port's original "mathematically identical" claim,
/// which was true only at steady state, not unconditionally. See <see cref="AfcTracker"/> for
/// where the corresponding real-Hz threshold values come from.
///
/// The FIR moving-average branch (<see cref="MovingAverage"/>, Options stub backlog item 2) has the
/// SAME accepted-divergence class at a <see cref="SetWidth"/> transition, code-review round 1 finding
/// -- bounded even more tightly, since the window at the smoothing-frequency floor (500Hz) holds at
/// most `sampleRate/500` samples (&lt;=88 at 11025Hz), not the IIR filter's settling time. The Off
/// branch has no accepted divergence at all -- it holds no filter state to rescale.
/// </summary>
internal sealed class ZeroCrossingFrequencyCounter
{
    private readonly IirFilter _outputFilter = new();
    private readonly MovingAverage _movingAverage = new(1);
    private readonly double _sampleRate;
    private readonly double _halfSampleRate;
    private ZeroCrossingSmoothingMode _smoothingMode;
    private double _centerFrequencyHz;
    private double _highClampHz;
    private double _lowClampHz;

    // sstv.h:397 -- ZEROFQ is a FIXED normalized (freq-Center)/BWH value, not recomputed per width.
    // Legacy stores m_fq normalized specifically so a stale reset value stays meaningful in Hz
    // regardless of a LATER width change (denormalized at read time using whatever center/BWH is
    // then-current) -- this port works in real Hz throughout instead, so SetWidth below recomputes
    // the real-Hz equivalent directly whenever the width changes, rather than storing the normalized
    // form and converting at read time.
    private const double ZeroFqNormalized = -1900.0 / 400.0;

    private double _prevSample;
    private double _fractionalCrossingSampleIndex;
    private double _sampleIndex;
    private double _currentFrequencyHz;
    private double _clearedFrequencyHz;
    private double _bwh;
    private bool _widthInitialized;

    public ZeroCrossingFrequencyCounter(
        double sampleRate,
        ZeroCrossingSmoothingMode smoothingMode = ZeroCrossingSmoothingMode.Iir,
        int outputOrder = 3,
        double outputCutoffHz = 900,
        double smoothingFrequencyHz = 2200)
    {
        _sampleRate = sampleRate;
        _halfSampleRate = sampleRate * 0.5;
        SetTuning(smoothingMode, outputOrder, outputCutoffHz, smoothingFrequencyHz);
        SetWidth(isNarrow: false);
    }

    /// <summary><c>CFQC::CalcLPF</c> (`sstv.cpp:403-408`) -- (re)designs the IIR output filter,
    /// resizes/clears the FIR moving-average window, and stores which smoothing stage
    /// <see cref="ProcessSample"/> should apply. Both-direction clamps applied here AND at
    /// construction (a lesson from the PLL-tuning item's own 2 code-review rounds, applied from the
    /// start here instead of discovered incrementally): order `[1,32]` (`Option.cpp:529-531`), IIR
    /// cutoff `[1.0, sampleRate*0.45]` (legacy's own dialog check is floor-only, `Option.cpp:532-534`
    /// -- this port keeps the stricter Nyquist ceiling too, same accepted divergence as PLL tuning),
    /// smoothing frequency `[500,8000]` (`Option.cpp:536-540`, legacy's real two-sided range -- legacy
    /// rejects-and-keeps an out-of-range entry, this port clamps instead). A NaN input is replaced
    /// with the field's own legacy default before clamping (`Math.Clamp(NaN, ...)` returns NaN
    /// unchanged, which would otherwise silently reach filter design/window-size math).
    /// <see cref="MovingAverage.SetCount"/> is called unconditionally every time, matching legacy's
    /// own "reset even when the window size is unchanged" behavior (`sstv.h:125-127`'s `else`
    /// branch) -- never skip it as an apparent no-op optimization.</summary>
    public void SetTuning(
        ZeroCrossingSmoothingMode smoothingMode,
        int outputOrder,
        double outputCutoffHz,
        double smoothingFrequencyHz)
    {
        _smoothingMode = Enum.IsDefined(smoothingMode) ? smoothingMode : ZeroCrossingSmoothingMode.Off;

        if (double.IsNaN(outputCutoffHz))
        {
            outputCutoffHz = 900;
        }

        if (double.IsNaN(smoothingFrequencyHz))
        {
            smoothingFrequencyHz = 2200;
        }

        var clampedOrder = Math.Clamp(outputOrder, 1, 32);
        var clampedCutoffHz = Math.Clamp(outputCutoffHz, 1.0, _sampleRate * 0.45);
        var clampedSmoothingHz = Math.Clamp(smoothingFrequencyHz, 500.0, 8000.0);

        _outputFilter.Design(clampedCutoffHz, _sampleRate, clampedOrder);
        _movingAverage.SetCount((int)(_sampleRate / clampedSmoothingHz));
    }

    /// <summary><c>CFQC::SetWidth</c> (`sstv.cpp:367-383`) — the wide sanity-clamp bounds (not the
    /// tight AFC acceptance band, which lives in <see cref="AfcTracker"/>) and center frequency
    /// differ for the MN/MC "narrow" family. Also recomputes <see cref="_clearedFrequencyHz"/> --
    /// see that field's own doc comment and <see cref="Clear"/>.
    ///
    /// Round-2 code-review finding, fixed here: legacy stores <c>m_fq</c> NORMALIZED, so ANY width
    /// change re-denormalizes whatever is CURRENTLY HELD (a real measurement, not just a `Clear()`
    /// reset value) through the new center/BWH at the next read -- an earlier version of this method
    /// only recomputed <see cref="_clearedFrequencyHz"/>, leaving a live held estimate frozen at its
    /// old real-Hz value across a mid-stream width flip until the next zero crossing corrected it
    /// (a small, bounded divergence -- at most one half-cycle -- but a real one, not present in
    /// legacy). Rescale <see cref="_currentFrequencyHz"/> the same way, skipped on the very first
    /// call (from the constructor), where there is no prior width to rescale FROM.</summary>
    public void SetWidth(bool isNarrow)
    {
        var newCenter = isNarrow ? 2172.0 : 1900.0; // NARROW_CENTER=(2300+2044)/2 vs. normal 1900
        var newBwh = isNarrow ? 128.0 : 400.0; // NARROW_BWH (sstv.h:445, =NARROW_BW/2=256/2) vs. wide m_BWH (sstv.cpp:375)

        if (_widthInitialized)
        {
            _currentFrequencyHz = newCenter + (_currentFrequencyHz - _centerFrequencyHz) / _bwh * newBwh;
        }

        _centerFrequencyHz = newCenter;
        _highClampHz = 2400.0; // same for both
        _lowClampHz = isNarrow ? 1800.0 : 1000.0; // NARROW_AFCLOW vs. normal
        _bwh = newBwh;
        _clearedFrequencyHz = _centerFrequencyHz + ZeroFqNormalized * _bwh;
        _widthInitialized = true;
    }

    /// <summary>Direct port of <c>CFQC::Clear</c> (`sstv.cpp:385-394`) -- legacy calls
    /// <c>m_fqc.Clear()</c> on every <c>Start()</c>/<c>Stop()</c> (`sstv.cpp:1722,1751,1781`), so a
    /// stale interval estimate from a previous transmission never leaks into the next. Resets the
    /// running frequency estimate and internal zero-crossing counters only -- does NOT touch
    /// <see cref="_outputFilter"/>'s Z-state or the width fields (<see cref="SetWidth"/> is a
    /// separate, independent legacy call). <c>_currentFrequencyHz</c> resets to
    /// <see cref="_clearedFrequencyHz"/> -- this port's real-Hz equivalent of legacy's normalized
    /// <c>ZEROFQ</c> reset value (`sstv.h:397`), which is 0Hz at the wide width but ~1564Hz at the
    /// narrow width (round-1 code-review finding: an earlier version of this method hardcoded 0.0
    /// unconditionally, correct only for the wide case -- legacy's own
    /// <c>CSSTVDEM::Start</c> calls <c>SetWidth</c> BEFORE <c>Clear()</c>, `sstv.cpp:1719,1722`, so
    /// the narrow case is genuinely reachable, not a theoretical corner).</summary>
    public void Clear()
    {
        _prevSample = 0.0;
        _fractionalCrossingSampleIndex = 0.0;
        _sampleIndex = 0.0;
        _currentFrequencyHz = _clearedFrequencyHz;
    }

    /// <summary>Processes one raw audio sample; returns the smoothed frequency estimate in Hz
    /// (sample-and-hold between zero crossings, matching legacy's own <c>m_fq</c> behavior).</summary>
    public double ProcessSample(double input)
    {
        if ((input >= 0 && _prevSample < 0) || (input < 0 && _prevSample >= 0))
        {
            // Linear interpolation of the exact (sub-sample) crossing time, CFQC::Do:
            // offset = d/(d-m_d); m_ACount = m_Count - offset; count = (m_Count-m_ACount_prev) - offset.
            var offset = input / (input - _prevSample);
            var count = _sampleIndex - _fractionalCrossingSampleIndex - offset;
            _fractionalCrossingSampleIndex = _sampleIndex - offset;

            if (count >= 1.0)
            {
                var measuredFrequencyHz = _halfSampleRate / count; // half-period per zero crossing
                _currentFrequencyHz = Math.Clamp(measuredFrequencyHz, _lowClampHz, _highClampHz);
            }
        }

        _prevSample = input;
        _sampleIndex += 1;

        // CFQC::Do's m_Type switch (`sstv.cpp:475-485`): IIR -> m_iir.Do, FIR -> m_fir.Avg,
        // default (OFF) -> raw m_fq passthrough, unsmoothed.
        return _smoothingMode switch
        {
            ZeroCrossingSmoothingMode.Iir => _outputFilter.Process(_currentFrequencyHz),
            ZeroCrossingSmoothingMode.Fir => _movingAverage.Add(_currentFrequencyHz),
            _ => _currentFrequencyHz,
        };
    }

    /// <summary>Test-only visibility into the running frequency estimate <see cref="Clear"/> resets
    /// -- production code has no need to read this back (only <see cref="ProcessSample"/>'s smoothed
    /// return value is consumed). Round-1 D0-audit finding: lets a test prove <see cref="Clear"/> was
    /// actually called at a given teardown point, not just that the call compiles.</summary>
    internal double CurrentFrequencyHzForTests => _currentFrequencyHz;

    /// <summary>Test-only visibility into the CURRENTLY APPLIED smoothing mode -- code-review round 1
    /// finding (Options stub backlog item 2): without this, a test asserting
    /// <see cref="AnalogFmSstvDecoder.RequestZeroCrossingTuning"/> reaches BOTH counter instances could
    /// only check the decoder's own (non-load-bearing) tracking fields, not the instances themselves --
    /// the exact gap that let a dropped drain call slip past the whole suite.</summary>
    internal ZeroCrossingSmoothingMode SmoothingModeForTests => _smoothingMode;
}
