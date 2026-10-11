namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Per-transmission TX tone state: VCO phase, the running sample-count accumulator, the TX LPF
/// moving average and the TX output bandpass filter. <see cref="AnalogFmSstvEncoder"/>'s production
/// loop and its <c>RenderSegments</c> test seam both synthesize through this one class.
///
/// Holds mutable filter state, so create one per transmission and never share it: the encoder is
/// a DI singleton, and legacy resets the BPF per transmission (<c>InitTXBuf</c> -&gt;
/// <c>m_BPF.Clear()</c>). Per-sample methods only — the caller is an async iterator, where Span
/// locals are not allowed (CS4012).
/// </summary>
internal sealed class TxToneSynthesizer
{
    private readonly double _effectiveSampleRate;
    private readonly double _carrierOffsetHz;
    private readonly bool _bpfEnabled;
    private readonly bool _lpfEnabled;
    private readonly TxOutputBandpassFilter _bandpassFilter;
    private readonly MovingAverage _lpfAverage;

    private double _phase;

    // Running accumulator, not "round(durationMs -> samples) per segment": with ~245,000
    // individual per-pixel segments in a full image, independently rounding each one's sample
    // count biases every pixel the same direction and the error accumulates linearly (over a
    // second of drift by the end of the image). Tracking ideal elapsed samples as a running
    // total and taking the difference keeps rounding error bounded. This port TRUNCATES (see
    // AdvanceSegment), which bounds the error to [0,1) samples -- a one-sided lag, not a
    // "+/-0.5 sample" round-to-nearest figure.
    private double _idealSamplesSoFar;
    private long _emittedSamples;

    /// <param name="nominalSampleRate">Builds the BPF and sizes the LPF window.</param>
    /// <param name="effectiveSampleRate">Drives the sample-count accumulator and the VCO phase step.</param>
    /// <param name="carrierOffsetHz">Receiver tuning error added to every tone after LPF smoothing.</param>
    public TxToneSynthesizer(
        double nominalSampleRate,
        double effectiveSampleRate,
        double carrierOffsetHz,
        bool bpfEnabled,
        int bpfTapCount,
        bool lpfEnabled,
        double lpfFrequencyHz)
    {
        _effectiveSampleRate = effectiveSampleRate;
        _carrierOffsetHz = carrierOffsetHz;
        _bpfEnabled = bpfEnabled;
        _lpfEnabled = lpfEnabled;

        // ultracode audit finding #26: legacy's TX output bandpass filter is applied to EVERY emitted
        // sample, as the last step of CSSTVMOD::Do(). See TxOutputBandpassFilter's own doc comment for
        // why this can't just reuse SearchBandpassFilter. Deliberately the NOMINAL sample rate, not the
        // effective one -- legacy builds this same filter from the nominal rate too, never the
        // TX-offset-corrected one (sstv.cpp:2768/2771/2923/2926). The on/off gate (legacy's m_bpf)
        // lives at the ProcessSample call sites below, not inside the filter class -- matches legacy's
        // own `if(m_bpf) d = m_BPF.Do(d);` shape (sstv.cpp:2914) exactly, the delay line is never
        // advanced at all when off, not merely bypassed post-construction.
        _bandpassFilter = new TxOutputBandpassFilter(nominalSampleRate, bpfTapCount);

        // Legacy's TX LPF (CSSTVMOD::avgLPF, a CSmooz moving average -- the exact same class already
        // ported as MovingAverage) smooths the discrete per-segment target FREQUENCY, applied PRE-VCO,
        // once per audio sample (sstv.cpp:2869, inside the m_Cnt-active/f>0 branch only). Fresh per
        // transmission here, NOT matching legacy's own InitTXBuf-clears-m_BPF-only precedent (avgLPF
        // genuinely PERSISTS across legacy transmissions, only SetCount clears it,
        // sstv.cpp:2827,2929) -- a deliberate, documented DIVERGENCE (simpler, deterministic,
        // practically indistinguishable after one window's worth of samples), not a faithfully-ported
        // behavior. SetCount uses the NOMINAL sample rate (matching CalcFilter's own bare SampFreq,
        // sstv.cpp:2929, and legacy's own separate VCO-rate re-pointing at the offset-corrected rate,
        // Main.cpp:959/7948) -- rounds via +0.5 before truncation, NOT CFQC::CalcLPF's
        // bare-truncation formula.
        LpfWindowSize = AnalogFmSstvEncoder.ResolveLpfWindowSize(nominalSampleRate, lpfFrequencyHz);
        _lpfAverage = new MovingAverage(LpfWindowSize);
    }

    internal int LpfWindowSize { get; }

    /// <summary>Adds one segment's duration to the running accumulator and returns how many samples
    /// to emit for it. The expression and operand order must stay identical to
    /// <see cref="AnalogFmSstvEncoder.EstimateSampleCount"/>'s own copy.</summary>
    public long AdvanceSegment(double durationMs)
    {
        _idealSamplesSoFar += durationMs / 1000.0 * _effectiveSampleRate;

        // ultracode audit finding #25: legacy's CSSTVMOD::Do uses `for (; m_iPos < int(m_dPos); ...)`
        // on an equivalent running accumulator -- floor/truncation, not round-to-nearest. Both are
        // bounded (non-cumulative, +/-1 sample per segment boundary) and Math.Round is arguably
        // more accurate (zero-mean vs legacy's 0.5-sample lag). Sample-exact legacy diffing is not
        // reachable through this choice alone: legacy's own accumulator uses a DIFFERENT operand
        // order (`(tim*SampFreq)/1000.0`, multiply-then-divide, vs this port's
        // divide-then-multiply), and legacy's VCO is a quantized sine LOOKUP TABLE plus integer
        // gain/mask stages (`sstv.cpp:77-148,2866-2901`), not this port's exact `Math.Sin`.
        // Keep truncation anyway -- it is the real ported behavior, not an invented approximation.
        // Comment left here so a future reader doesn't "fix" this back to Round without reason.
        var targetEmitted = (long)_idealSamplesSoFar;
        var samplesToEmit = targetEmitted - _emittedSamples;
        _emittedSamples = targetEmitted;
        return samplesToEmit;
    }

    /// <summary>Next output sample of a tone segment at <paramref name="frequencyHz"/>; a value
    /// &lt;= 0 is silence.</summary>
    public float NextToneSample(double frequencyHz)
    {
        double sample;
        if (frequencyHz <= 0)
        {
            // CW-ID's inter-element/inter-letter/inter-word gaps are real silence, not a 0Hz tone.
            // Matches legacy CSSTVMOD::Do (sstv.cpp:2865-2875) exactly: emit 0 amplitude and do NOT
            // advance the VCO phase (`m_vco.Do()` is skipped entirely when the buffered value is
            // <= 0), so a tone immediately after a gap resumes in-phase rather than restarting from
            // phase 0. The output bandpass filter still runs over the zero samples below
            // (sstv.cpp:2914), same as every other segment -- do not special-case it out of the
            // filter call. The TX LPF moving average is ALSO skipped here (sstv.cpp:2867's `if(f>0)`
            // gates both the VCO advance AND avgLPF.Avg together) -- its history holds through the
            // gap and resumes smoothing, not resets, once a real tone follows, matching legacy
            // exactly.
            sample = 0.0;
        }
        else
        {
            // Legacy re-reads and re-smooths this SAME per-segment-constant frequencyHz on EVERY
            // audio sample (m_TXBuf holds one discrete frequency code per requested duration, Do()
            // reads+smooths it once per call, sstv.cpp:2866-2870) -- avgLPF converges toward each new
            // segment's target like a glide filter rather than an instant jump. The phase increment
            // is therefore derived PER SAMPLE from the (optionally smoothed) frequency. Smoothing the
            // raw Hz value directly (not legacy's normalized (f-1100)/1200) is algebraically
            // identical, not a divergence -- CSmooz/MovingAverage is a linear operator
            // (mean(a*x+b) = a*mean(x)+b) and legacy's VCO conversion is itself affine, so a boxcar
            // mean commutes through it exactly.
            var smoothedFrequencyHz = _lpfEnabled ? _lpfAverage.Add(frequencyHz) : frequencyHz;

            // The carrier offset is added AFTER the transmit smoothing on purpose: it models a
            // RECEIVER tuning error, which is downstream of the transmitter's audio path. It is also
            // deliberately absent from the silence branch above -- there is no tone there to shift,
            // and shifting nothing would only advance the phase.
            var phaseIncrement = 2 * Math.PI * (smoothedFrequencyHz + _carrierOffsetHz) / _effectiveSampleRate;

            _phase += phaseIncrement;
            if (_phase >= 2 * Math.PI)
            {
                _phase -= 2 * Math.PI;
            }

            sample = Math.Sin(_phase);
        }

        // Filtered in double, narrowed to float only here -- legacy's whole chain (CSSTVMOD::Do's
        // `d`) is double; narrowing before filtering would lose precision the filter itself doesn't
        // need to lose. Tones must never be routed through FilterRaw(float) for the same reason.
        return (float)(_bpfEnabled ? _bandpassFilter.ProcessSample(sample) : sample);
    }

    /// <summary>Sound-file row-playback tail only: runs an already-float sample through the output
    /// BPF with no VCO advance and no LPF smoothing. Legacy's row-playback branch is outside the
    /// <c>f&gt;0</c> tone branch (sstv.cpp:2867-2870) and shares only the <c>d=m_BPF.Do(d)</c> stage
    /// (sstv.cpp:2914) with tone generation.</summary>
    public float FilterRaw(float raw) => (float)(_bpfEnabled ? _bandpassFilter.ProcessSample(raw) : raw);
}
