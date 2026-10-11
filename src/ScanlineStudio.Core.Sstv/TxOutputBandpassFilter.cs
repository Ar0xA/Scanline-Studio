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
/// <b>Coefficients come from the shared <see cref="FirDesign.MakeFilter"/></b>, the same designer the
/// RX <see cref="SearchBandpassFilter"/> and <see cref="NotchFilter"/> use, called with legacy's TX
/// arguments (`ffBPF`, 700-2800Hz, att 40, gain 1.0), so it always takes the Kaiser branch.
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
/// <b>Tap count is now user-editable (Options stub backlog item 3,
/// `docs/plans/options-stub-item3-tx-bpf-lpf-plan.md`)</b>: legacy's <c>m_bpftap</c>
/// (`TxBpfTap`/`TXBPFTAP`), rebuilt via <c>CalcFilter</c> (`sstv.cpp:2918-2928`), is now a real
/// constructor parameter here, clamped to legacy's own real Save-handler range `[2,512]`
/// (`Option.cpp:456-459`, `TAPMAX`, `fir.h:27`) and rounded to the nearest EVEN value — legacy's
/// `MakeFilter` only ever writes `2*(n/2)+1` coefficients, exactly filling a `tap+1`-sized allocation
/// for an EVEN `tap` (verified: 24 -> 13+12=25=24+1) but leaving one slot uninitialized for an ODD
/// `tap` (verified: 25 -> 13+12=25 writes into a 26-element array, `fir.cpp:421-426,1102,1123`) — this
/// port sidesteps that legacy UB entirely rather than replicating memory-garbage-dependent behavior.
/// <b>On/off (legacy's <c>m_bpf</c>/`CBTXBPF`/`TXBPF`) is gated at the CALL SITE, not here</b> —
/// mirrors legacy's own `if(m_bpf) d = m_BPF.Do(d);` shape (`sstv.cpp:2914`) exactly: when off, the
/// delay line is never advanced at all, not merely bypassed post-construction. See
/// <see cref="AnalogFmSstvEncoder.EncodeBatchedAsyncCore"/>'s own call site for the gate.
/// </summary>
internal sealed class TxOutputBandpassFilter
{
    // sstv.cpp:2766-2772: MakeFilter(H, m_bpftap, ffBPF, SampFreq, 700.0+g_dblToneOffset,
    // 2800.0+g_dblToneOffset, 40.0, 1.0). g_dblToneOffset is 0 for every case this port models (no
    // CQ100 support -- see SearchBandpassFilter's own doc comment for the same confirmation).
    private const double LowCutoffHz = 700.0;
    private const double HighCutoffHz = 2800.0;
    private const double AttenuationDb = 40.0;
    internal const int DefaultTapCount = 24; // sstv.cpp:2764 -- legacy's real shipped default.

    private readonly int _tap;
    private readonly double[] _h;
    private readonly FirDelayLine _delay; // logical index 0 = newest sample, pairs with _h[0]

    public TxOutputBandpassFilter(double sampleRate, int tapCount = DefaultTapCount)
    {
        _tap = ClampTapCount(tapCount);
        _h = MakeFilter(sampleRate, _tap);
        // Zero-init matches CFIR2::Create's zero-memset delay line and InitTXBuf's per-transmission m_BPF.Clear().
        _delay = new FirDelayLine(_tap, headMovesForward: false);
    }

    // Options stub backlog item 3: both-direction clamp [2,512] (Option.cpp:456-459/TAPMAX), rounded
    // to the nearest even value (see class doc comment for why odd is legacy UB, not just unusual).
    internal static int ClampTapCount(int tapCount)
    {
        var clamped = Math.Clamp(tapCount, 2, 512);
        return clamped % 2 == 0 ? clamped : clamped - 1;
    }

    /// <summary>Streaming, one-sample-at-a-time convolution -- a persistent delay line, matching
    /// <c>CFIR2::Do</c>'s real internal structure (the one-argument overload, `fir.cpp:1115-1129`).
    /// CAUSAL, not centered: this call's newest sample (logical index 0 of the
    /// <see cref="FirDelayLine"/>) pairs with <c>H[0]</c>, giving a constant <c>Tap/2</c>-sample group
    /// delay (12 at the default tap of 24) at every sample rate (see
    /// <see cref="SearchBandpassFilter"/>'s own doc comment for why this causal-vs-centered distinction
    /// is load-bearing, not cosmetic).</summary>
    public double ProcessSample(double input)
    {
        _delay.Push(input);
        return _delay.Convolve(_h);
    }

    /// <summary>Legacy's TX `MakeFilter` call (`sstv.cpp:2766-2772`) via the shared
    /// <see cref="FirDesign.MakeFilter"/>: `ffBPF`, 700-2800Hz, att 40, gain 1.0.</summary>
    internal static double[] MakeFilter(double sampleRate, int tap = DefaultTapCount) =>
        FirDesign.MakeFilter(FirKind.Bpf, tap, sampleRate, LowCutoffHz, HighCutoffHz, AttenuationDb);
}
