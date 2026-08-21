namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CSSTVDEM::SyncFreq</c> (`sstv.cpp:2339-2376`) — the AFC (automatic
/// frequency control) state machine. Fed a zero-crossing-based frequency reading
/// (<see cref="ZeroCrossingFrequencyCounter"/>) every sample once a mode is locked; watches for a
/// run of consecutive samples that plausibly reads as the mode's sync tone (1200Hz normal, 1900Hz
/// for the MN/MC narrow family, `NARROW_SYNC`), and once found, locks a persistent correction — the
/// difference between the expected sync frequency and what was actually measured — which the caller
/// adds to every subsequently demodulated sample (mirroring legacy's `if(m_Sync) d += m_AFCDiff`).
///
/// Kept in real Hz throughout (see <see cref="ZeroCrossingFrequencyCounter"/>'s doc comment for the
/// faithful-at-steady-state, bounded-and-accepted-during-a-narrow-mode-transition qualification on
/// this simplification of legacy's internal x16384/BWH-scaled arithmetic) — every threshold below
/// is legacy's own real-Hz constant, not a derived one, except the small calibration nudge noted at
/// its own declaration.
///
/// Correction (ultracode audit findings #1/#4): two earlier claims in this comment were wrong.
/// (1) legacy's <c>InitTone</c> retune IS modeled — <see cref="SyncEnvelopeDetector.Retune"/>, called
/// from <c>AnalogFmSstvDecoder</c> using <see cref="CorrectionHz"/> below, mirrors it exactly for the
/// Auto-Slant sync-envelope detector (the one legacy path whose loss was actually measurable; the
/// port never modeled the *other* four VIS-bit/FSK tone detectors <c>InitTone</c> also retunes, since
/// VIS bits are decoded via direct frequency thresholding on the continuous demodulator output
/// instead, which has no equivalent fixed-frequency resonator to retune). (2) the `m_CurMax > 16`
/// gate IS ported, at the <see cref="CorrectionHz"/> call site in <c>AnalogFmSstvDecoder</c> — it
/// gates the call to <see cref="ProcessSample"/> (matching legacy's `SyncFreq(d)` update), while the
/// standing correction itself is applied to every sample unconditionally (matching legacy's separate,
/// unconditional `d += m_AFCDiff`, `sstv.cpp:2270`).
/// </summary>
internal sealed class AfcTracker
{
    private readonly double _syncTargetHz;
    private readonly double _bandLowHz;
    private readonly double _bandHighHz;
    private readonly int _afcBeginSamples;
    private readonly int _afcEndSamples;
    private readonly int _cooldownSamples;
    private readonly double _calibrationOffsetHz;

    private readonly MovingAverage _shortAverage;
    private readonly MovingAverage _lockAverage = new(15); // m_AFCAVG, sstv.cpp:1476

    private int _consecutiveInBandCount;
    private int _disabledSamplesRemaining;
    private int _gardRemaining = 10; // m_AFCGard, InitAFC (sstv.cpp:1659)
    private double _lockedFrequencyHz;
    private double _correctionHz;

    /// <param name="sampleRate">This decoder's sample rate.</param>
    /// <param name="syncTargetHz">Expected sync-tone frequency: 1200Hz normal, 1900Hz (`NARROW_SYNC`)
    /// for the narrow family.</param>
    /// <param name="bandLowHz">Low edge of the plausible-sync-tone acceptance band: 1000Hz normal,
    /// 1800Hz (`NARROW_AFCLOW`) narrow.</param>
    /// <param name="bandHighHz">High edge: 1325Hz normal, 1950Hz (`NARROW_AFCHIGH`) narrow.</param>
    /// <param name="afcBeginMs">`m_AFCB`: how far into a stable run to wait before trusting it (skips
    /// the pulse's noisy leading edge) — 1.5ms normal, 1.0ms for the Martin/SC2/MC group
    /// (`sstv.cpp:1162-1177`).</param>
    /// <param name="afcWidthMs">`m_AFCW`: how much further to average before locking — 3.0ms normal,
    /// 2.0ms for that same group.</param>
    /// <param name="bandwidthHalfHz">`BWH` for this mode's frequency range (400Hz normal,
    /// `NARROW_BWH`=128Hz narrow) — only used for the tiny calibration nudge below.</param>
    public AfcTracker(
        double sampleRate,
        double syncTargetHz,
        double bandLowHz,
        double bandHighHz,
        double afcBeginMs,
        double afcWidthMs,
        double bandwidthHalfHz)
    {
        _syncTargetHz = syncTargetHz;
        _bandLowHz = bandLowHz;
        _bandHighHz = bandHighHz;
        _afcBeginSamples = (int)(afcBeginMs / 1000.0 * sampleRate);
        _afcEndSamples = _afcBeginSamples + (int)(afcWidthMs / 1000.0 * sampleRate);
        _cooldownSamples = (int)(100.0 / 1000.0 * sampleRate); // m_AFCInt = 100ms, sstv.cpp:1478

        // SyncFreq's `d -= 128` (sstv.cpp:2347), translated out of legacy's x16384/BWH scale: 128 in
        // that scale is 128*BWH/16384 real Hz. Confirmed deliberate, not incidental (ultracode audit
        // finding #2): 128 scaled units is *exactly* one luma level in both wide (400/3.125Hz) and
        // narrow (128/1.0Hz) bandwidth modes -- too dimensionally precise across two unrelated
        // constants to be accidental. In legacy's own inverted/scaled domain, `d -= 128` corresponds
        // to `f_eff = f + 128*BWH/16384`, i.e. the offset must be ADDED to the measured frequency
        // here, not subtracted (an earlier version of this line had the sign backwards, net error
        // +6.25Hz wide / +2.0Hz narrow vs legacy).
        _calibrationOffsetHz = 128.0 * bandwidthHalfHz / 16384.0;

        _shortAverage = new MovingAverage((int)(2.5 / 1000.0 * sampleRate)); // m_Avg, sstv.cpp:1475

        // InitAFC pre-seeds m_AFCLock/m_AFCData to the nominal sync-tone-equivalent value
        // (sstv.cpp:1662/1665) before the lock average has ever filled -- without this, the 15-tap
        // lock average's guard-timeout reset path (see the `_gardRemaining == 0` branch below) mixes
        // a real measured frequency with 14 slots of an unseeded default, producing a grossly wrong
        // correction (ultracode audit finding #3: ~1120Hz/~1773Hz wrong vs legacy's ~0Hz on that path).
        _lockedFrequencyHz = syncTargetHz;
    }

    /// <summary>The current persistent correction (Hz, 0 until the first lock) -- mirrors legacy's
    /// standing <c>m_AFCDiff</c>, which is applied to every sample unconditionally once synced
    /// (`sstv.cpp:2270`), independent of whatever gated <see cref="ProcessSample"/> most recently.</summary>
    public double CorrectionHz => _correctionHz;

    /// <summary>Feeds one zero-crossing-based frequency reading (Hz); returns the current persistent
    /// correction (Hz, 0 until the first lock) to add to the main demodulator's output. Callers that
    /// need the correction applied to every sample (not just gated ones) should read
    /// <see cref="CorrectionHz"/> separately -- see <c>AnalogFmSstvDecoder.ApplyAfcCorrections</c>.</summary>
    public double ProcessSample(double measuredFrequencyHz)
    {
        var d = measuredFrequencyHz + _calibrationOffsetHz;

        if (d >= _bandLowHz && d <= _bandHighHz)
        {
            if (_disabledSamplesRemaining == 0 && _consecutiveInBandCount >= _afcBeginSamples && _consecutiveInBandCount <= _afcEndSamples)
            {
                var averaged = _shortAverage.Add(d);
                if (_consecutiveInBandCount == _afcEndSamples)
                {
                    _lockedFrequencyHz = _gardRemaining > 0 ? _lockAverage.Reset(averaged) : _lockAverage.Add(averaged);
                    _gardRemaining = 0;

                    _correctionHz = _syncTargetHz - _lockedFrequencyHz;
                    _disabledSamplesRemaining = _cooldownSamples;
                }
            }

            _consecutiveInBandCount++;
        }
        else
        {
            if (_consecutiveInBandCount >= _afcBeginSamples && _gardRemaining > 0)
            {
                _gardRemaining--;
                if (_gardRemaining == 0)
                {
                    _lockAverage.Reset(_lockedFrequencyHz);
                }
            }

            _consecutiveInBandCount = 0;
            if (_disabledSamplesRemaining > 0)
            {
                _disabledSamplesRemaining--;
            }
        }

        return _correctionHz;
    }
}
