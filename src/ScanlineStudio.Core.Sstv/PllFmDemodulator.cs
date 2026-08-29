namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CPLL</c> (`sstv.cpp`): a closed-loop FM discriminator — AGC on the
/// input (tracked per half-cycle, reset at zero-crossings) → loop IIR lowpass on the previous
/// phase error → drives a <see cref="Vco"/> → multiplying phase detector produces the next error
/// → an output IIR lowpass is the final demodulated value. Ported exactly (same filter orders/
/// cutoffs, same AGC formula) rather than an invented technique — see CLAUDE.md's "port first,
/// invent second" rule for DSP/codec math.
///
/// Used two separate ways in <c>AnalogFmSstvDecoder</c>: as a live, user-selectable main-picture
/// demodulator (`demodType == Pll`, `DemodulatedFrequencyAt`'s dispatch switch -- legacy's real
/// compiled-in DEFAULT is actually <see cref="HilbertFmDemodulator"/>, `m_Type=2`, not this class,
/// but PLL is a real, legacy-faithful alternative a user can select), and, entirely independently of
/// that setting, a SEPARATE dedicated instance always drives AVT training-lock detection
/// (<see cref="AvtTrainingLockStateMachine"/>), matching legacy's own real behavior of always using
/// PLL there regardless of which demodulator handles the picture stream
/// (`sstv.cpp:2129/2159/2169/2187/2222`, all outside the `m_Type`-dispatched switch) -- see the
/// demod-type runtime-dispatch subsystem's implementation plan for the explicit decision that the
/// main-picture instance and the AVT instance stay separate, not shared.
///
/// Legacy returns <c>outLPF.Do(m_out) * 32768 * vcogain</c> — an MMSSTV-internal scale. This port
/// instead converts the loop's normalized frequency-deviation output back to Hz
/// (<c>centerFrequencyHz - normalizedOutput * bandwidthHz</c>), since that's what this decoder's
/// pixel-mapping needs; the derivation is in the spec/06-sstv-dsp.md parity notes, not yet
/// cross-checked against a captured legacy golden vector.
///
/// Scale bridge (found by the holistic review of the VIS/preamble-lock system, same category as
/// <c>LevelAgc</c>'s own): <c>CPLL::Do</c>'s AGC (<c>sstv.cpp:316-343</c>) resets its own tracking
/// window to <c>m_Max=1.0; m_Min=-1.0</c> every half-cycle -- the *same* literal ±1.0 floor this
/// class uses (<see cref="_max"/>/<see cref="_min"/>). Legacy's real input is int16-scaled, so that
/// floor only ever binds during near-silence; a real full-scale signal always exceeds it and the
/// AGC adapts to the signal's *actual* peak-to-peak range, exactly like this port's. Callers MUST
/// feed int16-domain samples (scale by 32768.0 first, mirroring the exact bridge <c>LevelAgc</c>
/// already uses -- see that class's own doc comment), keeping every constant inside this class
/// itself (the ±1.0 floor, the `5.0` target) literally identical to legacy's. Tier A Batch 7 chunk
/// 7c independently confirmed every current call site honors this (the main-picture path via
/// <c>AnalogFmSstvDecoder</c>'s own `× 32768.0`, both AVT-training feeds via `AvtPllSampleAt`) --
/// there is no separate feed anywhere that stays below full scale and would hit the floor
/// unintentionally. This convention is unenforced by an assert; a future caller that feeds
/// quieter-than-int16-domain samples directly would silently lose AGC tracking (the floor would
/// pin `_agc` at a constant `5.0/2.0 = 2.5` regardless of true amplitude), so any new call site
/// must scale first, the same way the three existing ones do.
/// </summary>
internal sealed class PllFmDemodulator
{
    // sstv.h:441-442 -- CPLL::SetWidth's narrow band is a fixed compile-time constant, unlike the
    // wide band (this instance's own constructor low/high -- both current call sites already pass
    // legacy's real wide default, 1500-2300, but nothing here hardcodes that assumption).
    private const double NarrowLowHz = 2044.0;
    private const double NarrowHighHz = 2300.0;

    private readonly Vco _vco;
    private readonly IirFilter _loopFilter = new();
    private readonly IirFilter _outputFilter = new();
    private readonly double _wideLowHz;
    private readonly double _wideHighHz;
    private readonly double _sampleRate;
    private double _centerFrequencyHz;
    private double _bandwidthHz;
    private double _vcoGain;

    private double _err;
    private double _max = 1.0;
    private double _min = -1.0;
    private double _prevInput;
    private double _agc = 1.0;
    private double _agcPrev;

    public PllFmDemodulator(
        double sampleRate,
        double lowFrequencyHz,
        double highFrequencyHz,
        double vcoGain = 1.0,
        int loopOrder = 1,
        double loopCutoffHz = 1500,
        int outputOrder = 3,
        double outputCutoffHz = 900)
    {
        _wideLowHz = lowFrequencyHz;
        _wideHighHz = highFrequencyHz;
        _sampleRate = sampleRate;
        _vcoGain = vcoGain;

        _vco = new Vco(sampleRate, (lowFrequencyHz + highFrequencyHz) / 2.0);
        SetWidth(isNarrow: false);

        // Code-review round 1 finding: the Nyquist clamp used to live ONLY in SetTuning, not here --
        // this constructor's own loopCutoffHz/outputCutoffHz params are reachable, unclamped, from
        // EVERY production construction site (composition root, periodic decoder rebuild, a live
        // sample-rate change's own rebuild, a freshly-started AVT training attempt), not just the
        // live Options-Save push path SetTuning covers. A user-typed sub-Nyquist sample rate paired
        // with a stale-but-unclamped cutoff produced a divergent IIR filter (Math.Tan at/above
        // Nyquist) -> permanent NaN output, surviving every restart with no error. Shared helper, not
        // a second copy of the clamp math.
        _loopFilter.Design(ClampCutoffBelowNyquist(loopCutoffHz), sampleRate, ClampFilterOrder(loopOrder));
        _outputFilter.Design(ClampCutoffBelowNyquist(outputCutoffHz), sampleRate, ClampFilterOrder(outputOrder));
    }

    /// <summary>Code-review round 2 finding: this originally clamped the CEILING only
    /// (<c>Math.Min</c>) -- a zero or negative cutoff (reachable the exact same way the round-1
    /// blocker was: a hand-edited settings.json/preset file) hits the identical failure class round 1
    /// fixed. At a negative cutoff, <c>Math.Tan</c>'s argument goes negative, producing an
    /// unstable/divergent filter pole -> exponential blowup -> a permanently NaN decoder surviving
    /// every restart; at exactly 0, the filter goes dead (a legitimate value never reaches the
    /// output, silently). Legacy guards this at its own apply site too (`&gt; 0.0`,
    /// `Option.cpp:517-518,523-524`) -- this port's version guards it centrally instead.</summary>
    private double ClampCutoffBelowNyquist(double cutoffHz) => Math.Clamp(cutoffHz, 1.0, _sampleRate * 0.45);

    /// <summary>Code-review round 1 finding: <c>IirFilter.Design</c>'s own <c>new double[order*3]</c>
    /// has no validation at all -- a negative order throws <see cref="OverflowException"/> (an
    /// app-start or live-retune crash, not a graceful fallback) and order 0 silently disables the
    /// filter. <see cref="SstvDecoderSettings.Resolve"/> now clamps to legacy's own real range,
    /// (0,32] (`Option.cpp:515,521`), for the settings-driven path, but <see cref="SetTuning"/> is
    /// also directly reachable from a live push (<c>ISstvDecoder.RequestPllTuning</c>) that never
    /// goes through <c>Resolve</c> -- clamping HERE, right where the unsafe array allocation actually
    /// happens, protects every caller regardless of path.</summary>
    private static int ClampFilterOrder(int order) => Math.Clamp(order, 1, 32);

    /// <summary>Direct port of <c>CPLL::SetWidth</c> (`sstv.cpp:266-279`) -- retunes center
    /// frequency/bandwidth/VCO gain in place for a narrow-mode (MN/MC family) transition.
    /// Deliberately does NOT reset loop/output filter Z-state, <see cref="_err"/>, the AGC tracking
    /// window, or the VCO's own phase -- confirmed legacy's <c>SetWidth</c> touches only
    /// <c>SetFreeFreq</c>/<c>SetVcoGain</c> (`sstv.cpp:271,274,277`), neither of which is
    /// <c>MakeLoopLPF</c>/<c>MakeOutLPF</c> (the only two legacy calls that would reset filter
    /// state) -- so a narrow-mode transition mid-lock preserves loop continuity exactly like
    /// legacy's real soft retune, not a full re-acquisition. Re-applies the CURRENT
    /// <see cref="_vcoGain"/>, matching legacy's own <c>SetWidth</c> -&gt; <c>SetVcoGain(m_vcogain)</c>
    /// chain (`sstv.cpp:278`) -- a narrow-mode transition must not silently reset a user-tuned VcoGain
    /// back to 1.0.</summary>
    public void SetWidth(bool isNarrow)
    {
        var lowHz = isNarrow ? NarrowLowHz : _wideLowHz;
        var highHz = isNarrow ? NarrowHighHz : _wideHighHz;
        _centerFrequencyHz = (lowHz + highHz) / 2.0;
        _bandwidthHz = highHz - lowHz;

        _vco.SetFreeFrequency(_centerFrequencyHz);
        _vco.SetGain(-_bandwidthHz * _vcoGain);
    }

    /// <summary>Options-Advanced-tab PLL tuning (backlog item, `docs/plans/options-stub-item1-pll-tuning-plan.md`)
    /// -- direct port of legacy's own live-edit shape, <c>CPLL::SetVcoGain</c> (`sstv.cpp:281-286`,
    /// applies the gain to the VCO immediately) + <c>MakeLoopLPF</c>/<c>MakeOutLPF</c>
    /// (`Option.cpp:522-523`, rebuild both filters). Legacy's own live-edit does NOT reset filter
    /// Z-state (<c>CIIR::Clear</c> is a separate, never-called-here method) -- this port's
    /// <see cref="IirFilter.Design"/> DOES reset Z-state on redesign, a documented, accepted
    /// divergence (a brief loop-unlock on a live retune) rather than added state-preservation
    /// machinery for a rare, user-initiated action.
    ///
    /// <paramref name="loopCutoffHz"/>/<paramref name="outputCutoffHz"/> are clamped below this
    /// instance's own Nyquist frequency by <see cref="ClampCutoffBelowNyquist"/> -- legacy has no
    /// such ceiling (`Option.cpp:517-524`, only `&gt; 0.0`), a deliberate divergence: a user-typed
    /// sample rate (the Options Sample Rate field is an editable ComboBox) could otherwise pair with
    /// a stale/unclamped cutoff and produce an unstable filter (<c>Math.Tan</c> at/above Nyquist)
    /// LIVE, mid-decode. The SAME helper is also called from the constructor (code-review round 1
    /// finding -- this method alone did NOT cover every entry path; the constructor's own
    /// loopCutoffHz/outputCutoffHz are reachable, unclamped, from composition root/periodic
    /// rebuild/a live sample-rate change's own rebuild/a fresh AVT attempt, none of which go through
    /// this method at all).</summary>
    public void SetTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz)
    {
        _vcoGain = vcoGain;
        _vco.SetGain(-_bandwidthHz * _vcoGain);

        _loopFilter.Design(ClampCutoffBelowNyquist(loopCutoffHz), _sampleRate, ClampFilterOrder(loopOrder));
        _outputFilter.Design(ClampCutoffBelowNyquist(outputCutoffHz), _sampleRate, ClampFilterOrder(outputOrder));
    }

    /// <summary>Processes one input sample; returns the demodulated instantaneous frequency in Hz.</summary>
    public double ProcessSample(double input)
    {
        if (_max < input)
        {
            _max = input;
        }

        if (_min > input)
        {
            _min = input;
        }

        if (input >= 0 && _prevInput < 0)
        {
            var agcRange = _max - _min;
            var instantAgc = 5.0 / agcRange;
            _agc = (_agcPrev + instantAgc) * 0.5;
            _agcPrev = instantAgc;
            _max = 1.0;
            _min = -1.0;
        }

        _prevInput = input;
        var d = input * _agc;

        var loopOut = Math.Clamp(_loopFilter.Process(_err), -1.5, 1.5);
        var vcoOut = _vco.Process(loopOut);
        _err = vcoOut * d;

        var filteredOut = _outputFilter.Process(loopOut);
        return _centerFrequencyHz - filteredOut * _bandwidthHz * _vcoGain;
    }
}
