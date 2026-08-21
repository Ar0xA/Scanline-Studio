namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct, literal port of legacy's always-on TX output bandpass filter (<c>CSSTVMOD</c>'s
/// <c>m_BPF</c>, `sstv.cpp:2759-2772,2914`): <c>if(m_bpf) d = m_BPF.Do(d);</c> is the unconditional
/// LAST statement of <c>CSSTVMOD::Do()</c> — it filters every emitted TX sample (leader tone,
/// VIS/extended-VIS/narrow-FSK/AVT header, the Scottie post-VIS pulse, image lines, the footer, and
/// the idle 1500Hz carrier alike). Ultracode audit finding #26: this port had no equivalent stage at
/// all, meaning its TX output carries real out-of-band spectral splatter from the hard frequency
/// steps at every pixel/segment boundary that legacy always suppresses by default.
///
/// This is the standard 700-2800Hz SSB transmit-audio passband, not a cosmetic detail — every SSTV
/// tone (1100-2300Hz normal, 1900-2300Hz narrow) sits comfortably inside it with unity passband gain
/// (<c>MakeFilter</c>'s own DC-normalization), so the filter removes essentially only the
/// step-transition energy at segment boundaries. This matters beyond golden-vector matching: it's a
/// real transmit spectral-purity/RF-hygiene property a ham-radio product's output is expected to
/// have, not just an internal test-fidelity concern.
///
/// <b>Not a reuse of <see cref="SearchBandpassFilter"/>, though its own <c>MakeFilter</c> is now
/// operation-for-operation identical at this file's own parameters</b> — Tier A Batch 7 chunk 7d
/// round 1 found and corrected a stale claim here: this doc comment previously said reusing
/// <see cref="SearchBandpassFilter.MakeFilter"/> at <c>att=40</c> would "silently produce a
/// rectangular-window filter," on the premise that class's Kaiser branch was provably unreachable.
/// That premise stopped being true once Band-1 item 4b added H1 at <c>att=40/50</c> (Narrow/
/// VeryNarrow) to <see cref="SearchBandpassFilter"/> — its <c>MakeFilter</c> fully implements Kaiser
/// and takes <c>tap</c> as a parameter, so at <c>tap=24, att=40</c> the two implementations are
/// bit-identical, verified operation-by-operation. This class still keeps its own independent copy —
/// not a functional necessity, just avoiding a cross-class dependency between an RX-search-path type
/// and the TX encoder for two small, stable, already-duplicated static functions.
///
/// <b>Constructed locally per encode, not held as a field</b>: <c>AnalogFmSstvEncoder</c> is
/// registered as a DI singleton and is otherwise fully stateless. A ctor-field filter here would leak
/// mutable per-transmission delay-line state across sequential/concurrent <c>EncodeAsync</c> calls —
/// legacy's own <c>InitTXBuf</c> calls <c>m_BPF.Clear()</c> at the start of every transmission
/// (`sstv.cpp:2827`), so a fresh (zeroed) instance per call is the actually-faithful choice, not
/// merely a safety precaution.
///
/// Legacy's <c>lfq &lt; 100 -&gt; ffLPF</c> fallback (`sstv.cpp:2767-2769`, low-pass instead of
/// band-pass when the low edge would go non-positive) is not ported: it depends on
/// <c>g_dblToneOffset</c>, which this port doesn't model, so the fallback's trigger condition is
/// structurally unreachable here.
///
/// <b>Two legacy user settings are dropped, not just defaulted</b>: legacy's <c>m_bpf</c> checkbox
/// (`CBTXBPF`, persisted as `TXBPF`, `Option.cpp:266-289,450`) lets the user disable this filter
/// entirely, and <c>m_bpftap</c> (`TxBpfTap`/`TXBPFTAP`) is user-editable, rebuilt via
/// <c>CalcFilter</c> (`sstv.cpp:2918-2928`) — this port always applies the filter at a fixed 24 taps,
/// matching legacy's shipped defaults exactly but with no user override. See
/// `docs/removed-features.md`'s "TX output bandpass filter toggle/tap setting" entry.
/// </summary>
internal sealed class TxOutputBandpassFilter
{
    // sstv.cpp:2766-2772: MakeFilter(H, m_bpftap, ffBPF, SampFreq, 700.0+g_dblToneOffset,
    // 2800.0+g_dblToneOffset, 40.0, 1.0). g_dblToneOffset is 0 for every case this port models (no
    // CQ100 support -- see SearchBandpassFilter's own doc comment for the same confirmation).
    private const double LowCutoffHz = 700.0;
    private const double HighCutoffHz = 2800.0;
    private const double AttenuationDb = 40.0;
    private const int Tap = 24; // sstv.cpp:2764 -- fixed, NOT scaled by sample rate

    private readonly double[] _h;
    private readonly double[] _z; // delay line, length Tap+1. _z[0]=newest sample, _z[Tap]=oldest.

    public TxOutputBandpassFilter(double sampleRate)
    {
        _h = MakeFilter(sampleRate);
        _z = new double[Tap + 1]; // zero-init -- matches CFIR2::Create's zero-memset delay line and
                                   // legacy's per-transmission InitTXBuf -> m_BPF.Clear() reset.
    }

    /// <summary>Streaming, one-sample-at-a-time convolution -- a persistent delay line, matching
    /// <c>CFIR2::Do</c>'s real internal structure (`fir.cpp:1131-1144`). CAUSAL, not centered:
    /// <c>_z[0]</c> (this call's newest sample) pairs with <c>H[0]</c>, giving a constant
    /// <c>Tap/2</c> = 12-sample group delay at every sample rate (see <see cref="SearchBandpassFilter"/>'s
    /// own doc comment for why this causal-vs-centered distinction is load-bearing, not cosmetic).</summary>
    public double ProcessSample(double input)
    {
        Array.Copy(_z, 0, _z, 1, Tap);
        _z[0] = input;

        var sum = 0.0;
        for (var i = 0; i <= Tap; i++)
        {
            sum += _z[i] * _h[i];
        }

        return sum;
    }

    // Literal port of MakeFilter's att>=21 Kaiser/Bessel branch (fir.cpp:346-427) -- this filter's
    // att=40 always takes the Kaiser branch, so that's the branch exercised here (SearchBandpassFilter
    // .MakeFilter implements this same branch too, as of Band-1 item 4b -- see class doc comment).
    // att>=50's alternate alpha formula (fir.cpp:361-363) is unreachable at att=40 -- omitted, matching
    // SearchBandpassFilter's own precedent for documenting (not silently dropping) an unreachable branch.
    internal static double[] MakeFilter(double sampleRate)
    {
        const int half = Tap / 2;
        var fc = (HighCutoffHz - LowCutoffHz) / 2.0;
        var sumArg = 2.0 * Math.PI * fc / sampleRate;

        // fir.cpp:365: alpha = 0.5842*pow(att-21,0.4) + 0.07886*(att-21), the 21<=att<50 branch.
        var alpha = 0.5842 * Math.Pow(AttenuationDb - 21.0, 0.4) + 0.07886 * (AttenuationDb - 21.0);
        var i0Alpha = I0(alpha);

        var prototype = new double[half + 1];
        for (var j = 0; j <= half; j++)
        {
            if (j == 0)
            {
                prototype[j] = fc * 2.0 / sampleRate;
                continue;
            }

            var fm = (2.0 * j) / Tap;
            var win = I0(alpha * Math.Sqrt(1.0 - fm * fm)) / i0Alpha;
            prototype[j] = Math.Sin(j * sumArg) / (Math.PI * j) * win;
        }

        var dcSum = prototype[0];
        for (var j = 1; j <= half; j++)
        {
            dcSum += 2.0 * prototype[j];
        }

        if (dcSum > 0.0)
        {
            for (var j = 0; j <= half; j++)
            {
                prototype[j] /= dcSum;
            }
        }

        var w0 = Math.PI * (LowCutoffHz + HighCutoffHz) / sampleRate;
        for (var j = 0; j <= half; j++)
        {
            prototype[j] *= 2.0 * Math.Cos(j * w0);
        }

        var h = new double[Tap + 1];
        var outIndex = 0;
        for (var j = half; j >= 0; j--)
        {
            h[outIndex++] = prototype[j];
        }

        for (var j = 1; j <= half; j++)
        {
            h[outIndex++] = prototype[j];
        }

        return h;
    }

    // Literal port of fir.cpp:310-325's I0 -- a TRUNCATED series with its own convergence break
    // (`(1e-8*sum) - xj*xj > 0`), not an exact modified-Bessel-function-of-the-first-kind
    // implementation. Ported verbatim, including the truncation, because independently-computed
    // reference coefficients in this class's own test file are generated with this exact series --
    // substituting an exact I0 would silently fail those fixtures at their stated tolerance for
    // reasons unrelated to correctness (see this class's test file for the exact figure).
    private static double I0(double x)
    {
        var sum = 1.0;
        var xj = 1.0;
        var j = 1;
        while (true)
        {
            xj *= (0.5 * x) / j;
            sum += xj * xj;
            j++;
            if (1e-8 * sum - xj * xj > 0)
            {
                break;
            }
        }

        return sum;
    }
}
