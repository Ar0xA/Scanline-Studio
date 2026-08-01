namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct, literal port of legacy <c>CHILL</c> (`sstv.cpp:3005-3087`, `sstv.h:374-395`) -- a
/// Hilbert-transform-based instantaneous-phase FM discriminator, legacy's real compiled-in default
/// demodulator (<c>m_Type=2</c>, `sstv.cpp:1492`), replacing this port's prior PLL-only demodulation
/// for the main picture stream. Architecturally unrelated to <see cref="PllFmDemodulator"/>:
/// feedforward, not closed-loop -- a wideband Hilbert FIR produces a quadrature component, paired with
/// the delayed real signal to form an analytic signal; consecutive-sample phase difference (via
/// <c>atan2</c>) gives instantaneous frequency; a final order-3/1800Hz Butterworth IIR smooths the
/// result. No VCO, no loop filter, no lock-acquisition transient in the PLL sense -- motivated by
/// Piece 10's finding that <see cref="PllFmDemodulator"/>'s slow/undershooting settling causes real
/// (if small) golden-vector delta regressions on narrow-pitch modes, and a scoping pass
/// (spec/14-roadmap.md's "Hilbert demodulator scoping pass") that measured this class's transient as
/// bounded and roughly 2x faster.
///
/// Verified across two rounds of independent auditor plan-review, each re-deriving from source rather
/// than accepting the prior round's restatement -- see spec/14-roadmap.md for the full derivation
/// history. Key non-obvious details, all independently confirmed:
///
/// <list type="bullet">
/// <item><b>Buffer lengths are `tap+1`, not `tap`</b> (`sstv.h:380/381`'s `Z[HILLTAP+1]`/`H[HILLTAP+1]`,
/// both legacy loops use inclusive `&lt;=` bounds) -- 13 coefficients at tap=12 (11025Hz tier), 49 at
/// tap=48 (44100Hz tier). Getting this wrong misplaces the delayed-real-component read relative to the
/// true center of an odd-length window, silently breaking the group-delay match instead of failing
/// loudly.</item>
/// <item><b>The FIR's impulse response is the coefficient array in REVERSED order</b>: legacy's
/// `DoFIR` (`fir.cpp:29-37`) shifts the delay line toward index 0 and appends the newest sample at the
/// LAST index, so `H[0]` pairs with the OLDEST sample in the window, not the newest. This is
/// load-bearing, not cosmetic -- `H` is exactly antisymmetric about its center tap (the Hamming window
/// is symmetric about the center; the sinc-like `cos(x)/x` terms are odd), so reversing it flips its
/// effective sign, which is exactly what makes the discriminator's sign convention come out right
/// against the `+m_OFF` term below. Get the indexing direction backwards and it silently flips the
/// whole demodulator's sign -- the exact "encoder and decoder agree with each other while both are
/// wrong" failure mode CLAUDE.md's behavioral-parity rule exists to catch.</item>
/// <item><b>Output domain</b>: legacy's raw scaled value is `(1900-f)*32768/800` (confirmed against
/// `sstv.cpp:1685`'s AFC-constant comment, `CPLL`'s own `*32768` output convention, and `CFQC::Do`'s
/// literal `return -(m_out*16384)` -- same family, note the sign inversion). The final smoothing IIR
/// filters THIS scaled value directly (`sstv.cpp:3086`: `m_iir.Do(d * m_OUT)`), not Hz -- a fresh
/// filter's zero-state means "0" in whatever domain it's fed, and 0 in the scaled domain is 1900Hz
/// (center, at-rest), the correct cold-start value. Filtering in Hz first (converting, then smoothing)
/// would give the filter a false 0Hz cold-start instead, producing a large spurious transient every
/// time it (re)starts -- precisely the settling behavior this whole port exists to improve. This class
/// filters internally in the scaled domain and converts to Hz only on <see cref="ProcessSample"/>'s
/// return value.</item>
/// <item><b>Decimation tiering</b> (`SetWidth`, `sstv.cpp:3022-3051`): tap count and phase-diff lag
/// (`m_df`) tier on sample rate (`SampBase`, confirmed to mean the same thing as this port's own
/// `sampleRate` parameter -- both are "the actual DSP sample rate," `ComLib.cpp:48-192`). Lag = 2^df
/// (1/2/4 samples), derived by hand-simulating the `m_A[0..3]` shift-register switch across several
/// calls and independently re-confirmed by a second reviewer doing the same simulation fresh from
/// source. The per-tier `m_OFF`/`m_OUT` multipliers (x2/x0.5 at df=1, x4/x0.25 at df=2) exist
/// specifically to cancel this lag-dependent scaling, making the final Hz mapping lag-/rate-independent
/// -- confirmed as a real derivation (the multipliers exactly cancel `2^df`), not a coincidence. Only
/// the 12-tap/df0 (below 16kHz, covers this port's 11025Hz) and 48-tap/df2 (>=40kHz, covers 44100Hz)
/// tiers have real coverage from this port's actual supported rates; the middle (16-40kHz) tier is
/// implemented for completeness (cheap, just parameterized data) but untested until/unless a rate in
/// that range is used.</item>
/// <item><b>Fixed at legacy's non-narrow config always</b> (center 1900Hz, bandwidth 800Hz) -- no
/// per-mode narrow retune, matching <see cref="PllFmDemodulator"/>'s own existing, already-accepted
/// simplification (`CPLL::SetWidth`'s non-narrow branch matches `PllFmDemodulator`'s fixed
/// 1500-2300Hz config exactly, and legacy's `CSSTVDEM::SetWidth` retunes all three demodulators
/// together per mode -- a gap already logged elsewhere as pre-existing, not introduced here). Actually
/// a safer simplification for this class than for the PLL: normal-vs-narrow only changes the affine
/// `m_OFF`/`m_OUT` constants here, not the tap count or `m_df`, unlike the PLL where the VCO
/// center/gain genuinely alter closed-loop dynamics.</item>
/// <item><b><c>a == 0.0</c> is a real, reachable, meaningful state, not a stale-value edge case</b>:
/// if the delayed real sample is exactly zero (digital silence, an exact zero-crossing), legacy skips
/// `atan2` entirely and uses phase = 0.0 radians directly -- materially different from what
/// `atan2(quadrature, 0)` would compute (+/-PI/2). Ported literally.</item>
/// </list>
///
/// Explicitly NOT ported: the `N&lt;8` coefficient-normalization branch in <c>MakeHilbert</c>
/// (provably unreachable for every tap value this port ever constructs, 12/24/48, all >=8); the
/// `sys.m_bCQ100`-specific tap-tripling (no CQ100-equivalent hardware modeled anywhere in this port,
/// CLAUDE.md §4's no-Win32-hardware-specific-paths stance); `g_dblToneOffset` (a legacy global,
/// confirmed always 0.0, matching this port's existing precedent of omitting it elsewhere).
/// </summary>
internal sealed class HilbertFmDemodulator
{
    private const double NormalCenterHz = 1900.0;
    private const double NormalBandwidthHz = 800.0;

    private readonly int _tap;
    private readonly int _htap;
    private readonly int _df;
    private readonly double _off;
    private readonly double _out;
    private readonly double[] _h;
    private readonly double[] _z;
    private readonly double[] _a = new double[4];
    private readonly IirFilter _smoothingFilter = new();

    /// <summary>`m_htap` (`sstv.cpp:3013`, `m_tap/2`) -- half the FIR tap count, this demodulator's
    /// group delay in samples. Exposed for <see cref="SyncAnchorCorrector"/>'s Hilbert-specific
    /// sync-anchor correction term (`Main.cpp:3794`: `n -= dp->m_hill.m_htap/4`), which needs it
    /// directly rather than re-deriving the tap/sample-rate tiering table a second time.</summary>
    internal int HalfTap => _htap;

    public HilbertFmDemodulator(int sampleRate)
    {
        int tap;
        int df;
        if (sampleRate >= 40000)
        {
            tap = 48;
            df = 2;
        }
        else if (sampleRate >= 16000)
        {
            tap = 24;
            df = 1;
        }
        else
        {
            tap = 12;
            df = 0;
        }

        _tap = tap;
        _df = df;
        _htap = tap / 2;

        var tierMultiplier = df switch { 2 => 4.0, 1 => 2.0, _ => 1.0 };
        _off = 2 * Math.PI * NormalCenterHz / sampleRate * tierMultiplier;
        _out = 32768.0 * sampleRate / (2 * Math.PI * NormalBandwidthHz) / tierMultiplier;

        _h = MakeHilbert(tap, sampleRate, 100.0, sampleRate / 2.0 - 100.0);
        _z = new double[tap + 1];

        _smoothingFilter.Design(1800.0, sampleRate, 3);
    }

    /// <summary>Feeds one input sample; returns the demodulated instantaneous frequency in Hz.</summary>
    public double ProcessSample(double input)
    {
        var quadrature = DoFir(input);
        var real = _z[_htap];
        var phase = real != 0.0 ? Math.Atan2(quadrature, real) : 0.0;

        var diff = ComputePhaseDifference(phase, _a, _df);

        if (diff >= Math.PI)
        {
            diff -= 2 * Math.PI;
        }
        else if (diff <= -Math.PI)
        {
            diff += 2 * Math.PI;
        }

        diff += _off;

        var scaled = _smoothingFilter.Process(diff * _out);
        return NormalCenterHz - scaled * NormalBandwidthHz / 32768.0;
    }

    // sstv.cpp:3062-3078 -- `d = a - m_A[0]` reads the OLD m_A[0] (left over from the PREVIOUS call's
    // switch) before this call's switch updates state for the NEXT call. `df` selects the reference
    // lag: 0 (direct replace) = 1-sample lag; 1 (2-slot shift) = steady-state 2-sample lag; 2 (4-slot
    // shift) = steady-state 4-sample lag -- lag = 2^df, verified by hand-simulating this exact switch
    // across several calls from an all-zero `a`, independently re-confirmed by auditor review. Extracted
    // as its own testable unit (mirroring `DoFir`'s free-function shape) specifically so the warm-up
    // sample counts (2 calls at df=1, 4 at df=2) can be tested against this logic alone, not against
    // the whole class's settled Hz output, which also carries the FIR fill delay and IIR settling on
    // top -- asserting exact correctness at a specific early call number on the full pipeline would be
    // a test that's wrong by construction.
    internal static double ComputePhaseDifference(double phase, double[] a, int df)
    {
        var diff = phase - a[0];
        switch (df)
        {
            case 1:
                a[0] = a[1];
                a[1] = phase;
                break;
            case 2:
                a[0] = a[1];
                a[1] = a[2];
                a[2] = a[3];
                a[3] = phase;
                break;
            default:
                a[0] = phase;
                break;
        }

        return diff;
    }

    private double DoFir(double input) => DoFir(_h, _z, input, _tap);

    // fir.cpp:29-37 -- free function mirroring legacy's own DoFIR(hp, zp, d, tap) signature exactly
    // (rather than an instance method reaching into private fields) so it's directly unit-testable in
    // isolation: newest sample lands at the LAST index of `z` (mutated in place), so H[0] pairs with
    // the OLDEST sample in the window. See this class's own doc comment for why the direction of this
    // shift is load-bearing, not an arbitrary implementation choice.
    internal static double DoFir(double[] h, double[] z, double input, int tap)
    {
        Array.Copy(z, 1, z, 0, tap);
        z[tap] = input;

        var sum = 0.0;
        for (var i = 0; i <= tap; i++)
        {
            sum += z[i] * h[i];
        }

        return sum;
    }

    // fir.cpp:432-474 -- Hamming-windowed (0.54-0.46*cos(...), NOT Hann) sinc-difference Hilbert
    // transformer design. The final `else { x1=x2=1.0; }` branch in the legacy source is provably
    // unreachable (the preceding `n==L` check already excludes the only case that would reach it) --
    // not ported, matching this class's own documented "not ported" list.
    internal static double[] MakeHilbert(int tap, double sampleRate, double fc1, double fc2)
    {
        var h = new double[tap + 1];
        var l = tap / 2;
        var t = 1.0 / sampleRate;
        var w1 = 2 * Math.PI * fc1;
        var w2 = 2 * Math.PI * fc2;

        for (var n = 0; n <= tap; n++)
        {
            double x1;
            double x2;
            if (n == l)
            {
                x1 = 0.0;
                x2 = 0.0;
            }
            else
            {
                var arg1 = (n - l) * w1 * t;
                x1 = Math.Cos(arg1) / arg1;
                var arg2 = (n - l) * w2 * t;
                x2 = Math.Cos(arg2) / arg2;
            }

            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / tap);
            h[n] = -(2 * fc2 * t * x2 - 2 * fc1 * t * x1) * window;
        }

        return h;
    }
}
