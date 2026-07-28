namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CSSTVDEM::SyncFreq</c> (`sstv.cpp:2339-2376`) — the AFC (automatic
/// frequency control) state machine. Fed a zero-crossing-based frequency reading
/// (<see cref="ZeroCrossingFrequencyCounter"/>) every sample once a mode is locked; watches for a
/// run of consecutive samples that plausibly reads as the mode's sync tone (1200Hz normal, 1900Hz
/// for the MN/MC narrow family, `NARROW_SYNC`), and once found, locks a persistent correction — the
/// difference between the expected sync frequency and what was actually measured — which the caller
/// adds to every subsequently demodulated sample (mirroring legacy's `if(m_Sync) d += m_AFCDiff`).
///
/// Kept in real Hz throughout (see <see cref="ZeroCrossingFrequencyCounter"/>'s doc comment for why
/// this is a faithful, not approximate, simplification of legacy's internal x16384/BWH-scaled
/// arithmetic) — every threshold below is legacy's own real-Hz constant, not a derived one, except
/// the small calibration nudge noted at its own declaration.
///
/// Not ported: legacy also calls <c>InitTone</c> on every lock update, re-tuning several *other*
/// fixed-frequency tone-detector IIR filters (VIS-bit/tick/FSK amplitude detectors,
/// `Main.cpp`/`sstv.cpp`'s `m_iir11`/`12`/`13`/`19`/`fsk`) so they track the same drift. This port
/// doesn't model that separate detector subsystem for any mode (VIS bits are decoded via direct
/// frequency thresholding on the continuous PLL output instead — see <c>AnalogFmSstvDecoder</c>) so
/// there is nothing for that side effect to retune here; only the correction this class returns is
/// relevant. Also not ported: the `m_lvl.m_CurMax > 16` signal-level gate at the call site — a
/// squelch/noise-immunity gate this port's noise-free synthetic round-trip pipeline has no
/// equivalent for, and a real "is there a signal at all" concept this port doesn't model for any
/// mode yet.
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
        // that scale is 128*BWH/16384 real Hz -- a small (~1-3Hz) fixed nudge, not independently
        // explained in source, ported faithfully rather than dropped as presumed-insignificant.
        _calibrationOffsetHz = 128.0 * bandwidthHalfHz / 16384.0;

        _shortAverage = new MovingAverage((int)(2.5 / 1000.0 * sampleRate)); // m_Avg, sstv.cpp:1475
    }

    /// <summary>Feeds one zero-crossing-based frequency reading (Hz); returns the current persistent
    /// correction (Hz, 0 until the first lock) to add to the main demodulator's output.</summary>
    public double ProcessSample(double measuredFrequencyHz)
    {
        var d = measuredFrequencyHz - _calibrationOffsetHz;

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
