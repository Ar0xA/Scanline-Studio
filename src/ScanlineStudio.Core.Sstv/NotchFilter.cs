namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy's RX notch filter (<c>CNotch</c>, `fir.h:123-137`, `fir.cpp:263-300`) -- a
/// band-elimination FIR (`ffBEF`) used to suppress a single interfering tone in the RX audio.
///
/// <b>Always the unwindowed branch, never Kaiser.</b> `CNotch::SetNotchFreq` always calls
/// `Create(tap, ffBEF, SampFreq, fl, fh, att: 10, gain: 1.0)` (`fir.cpp:292`) -- `att=10 &lt; 21`,
/// so the shared <see cref="FirDesign.MakeFilter"/> never takes its Kaiser-window branch
/// (`fir.cpp:373-383`) for this filter.
///
/// <b>Tap count</b> (`CNotch::CNotch`, `fir.cpp:264-272`): `tap = (int)(96.0 * sampleRate /
/// 11025.0)` -- C++ integer TRUNCATION, not rounding, then bumped up to even if odd, capped at 256
/// (`NOTCHTAPMAX`, `fir.h:120`). Plan-review correction: an earlier draft of this port used
/// ceiling-to-even instead of truncation, which disagrees with legacy at several real sample rates
/// (e.g. 6000 Hz: truncation gives 52, ceiling-to-even gives 54). Legacy technically derives this
/// from `SampBase` (raw card rate), separately from the filter's own design `fs` (`SampFreq`,
/// clock-calibrated) -- this port has one configured rate, so both collapse to the same value.
///
/// <b>Bandwidth</b> (`CNotch::SetNotchFreq`, `fir.cpp:279-292`): center frequency `fq`; outside the
/// voice passband (`fq &lt; 1050 || fq &gt; 2350`), half-bandwidth is 30Hz; inside it, 15Hz.
///
/// <b>BEF coefficient transform</b> (`fir.cpp:415-419`, the non-BPF branch under the
/// frequency-transform fork), `w0 = pi*(fcl+fch)/sampleRate`: `h[0] = 1.0 - 2.0*h[0]`, and for
/// `j=1..tap/2`: `h[j] *= -2.0*cos(j*w0)` -- the sign-flipped, DC-inverted counterpart to the BPF
/// transform (`*= +2.0*cos(j*w0)` for ALL j including 0). Both transforms, and everything around them
/// (prototype cutoff, impulse response, DC-sum normalization, two-loop mirror), live in the shared
/// <see cref="FirDesign.MakeFilter"/>, which <see cref="SearchBandpassFilter"/> and
/// <see cref="TxOutputBandpassFilter"/> also use.
///
/// <b>Retuning must not reset the delay line.</b> `CFIR2::Create` only reallocates/zeroes the delay
/// buffer when the TAP COUNT changes; `SetNotchFreq` always passes the same tap for a given sample
/// rate, so retuning recomputes coefficients in place and the delay line survives.
/// <see cref="SetFrequency"/> mutates only the coefficient array, never <c>_z</c> -- the UI's
/// click-and-drag retune calls this on every pointer-move, and resetting state there would
/// audibly click/pop on each frame.
///
/// <b>`ProcessSample` is a plain causal FIR convolution</b> (`CNotch::Do`-&gt;`CFIR2::Do`,
/// `fir.cpp:296-300,1115-1129`), no dry/wet mix -- same copy-shift delay-line shape already used by
/// <see cref="SearchBandpassFilter.ProcessSample"/>/<see cref="TxOutputBandpassFilter.ProcessSample"/>,
/// `H[0]` pairing with the newest sample.
/// </summary>
internal sealed class NotchFilter
{
    private const int NotchTapMax = 256; // NOTCHTAPMAX, fir.h:120
    private const double NotchAttenuationDb = 10.0; // CNotch::SetNotchFreq, fir.cpp:292

    private readonly int _tap;
    private readonly double _sampleRate;
    private readonly double[] _z; // delay line, length tap+1. _z[0]=newest sample, _z[tap]=oldest.
    private double[] _h;

    public NotchFilter(int sampleRate, double initialFrequencyHz = 2400.0)
    {
        _sampleRate = sampleRate;
        _tap = ComputeTap(sampleRate);
        _z = new double[_tap + 1]; // zero-init, matches CFIR2::Create's own zero-memset delay line
        _h = [];
        SetFrequency(initialFrequencyHz);
    }

    public double Frequency { get; private set; }

    internal int Tap => _tap;

    /// <summary>Recomputes the filter coefficients for a new center frequency, WITHOUT touching the
    /// delay line -- see class doc comment for why this matters for a click-and-drag retune.</summary>
    public void SetFrequency(double hz)
    {
        Frequency = hz;
        ComputeBandwidth(hz, out var fcl, out var fch);
        _h = MakeFilter(_tap, _sampleRate, fcl, fch);
    }

    public double ProcessSample(double input)
    {
        Array.Copy(_z, 0, _z, 1, _tap);
        _z[0] = input;

        var sum = 0.0;
        for (var i = 0; i <= _tap; i++)
        {
            sum += _z[i] * _h[i];
        }

        return sum;
    }

    /// <summary>`CNotch::CNotch` (`fir.cpp:264-272`): `m_tap = 96 * SampBase / 11025.0;` into an
    /// `int` field -- C++ truncates toward zero on that implicit double-to-int conversion, THEN
    /// `if(m_tap &amp; 1) m_tap++;`, THEN capped at <see cref="NotchTapMax"/>.</summary>
    private static int ComputeTap(double sampleRate)
    {
        var tap = (int)(96.0 * sampleRate / 11025.0);
        if ((tap & 1) != 0)
        {
            tap++;
        }

        return Math.Min(tap, NotchTapMax);
    }

    /// <summary>`CNotch::SetNotchFreq` (`fir.cpp:279-292`).</summary>
    private static void ComputeBandwidth(double centerHz, out double fcl, out double fch)
    {
        var halfBandwidth = centerHz is < 1050.0 or > 2350.0 ? 30.0 : 15.0;
        fcl = centerHz - halfBandwidth;
        fch = centerHz + halfBandwidth;
    }

    /// <summary>`CNotch::SetNotchFreq`'s `MakeFilter` call (`fir.cpp:292`) via the shared
    /// <see cref="FirDesign.MakeFilter"/>: `ffBEF`, att <see cref="NotchAttenuationDb"/> (unwindowed),
    /// gain 1.0.</summary>
    internal static double[] MakeFilter(int tap, double sampleRate, double fcl, double fch) =>
        FirDesign.MakeFilter(FirKind.Bef, tap, sampleRate, fcl, fch, NotchAttenuationDb);
}
