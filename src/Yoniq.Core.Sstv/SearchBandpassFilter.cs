namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct, literal port of legacy's pre-AGC bandpass filter (`CSSTVDEM::Do`, `sstv.cpp:1826-1833`):
/// `m_BPF.Do(d, m_Sync||m_SyncMode&gt;=3 ? (m_fNarrow?HBPFN:HBPF) : HBPFS)` -- the "search"/pre-lock
/// variant (`H2`/`HBPFS`) always, plus, as of Band-1 item 4b, the "locked" normal variant (`H1`/`HBPF`)
/// once locked. `H3`/`HBPFN` (locked, MN/MC-narrow) is deliberately NOT ported -- this port's narrow
/// modes keep using H2/search always, part of the already-tracked MN/MC narrow-mode gap family
/// (`spec/14-roadmap.md`, Band 3). Three further legacy divergences, all deliberate, all documented (not
/// silently absorbed) -- see `spec/14-roadmap.md`'s "Band-1 item 4" entry for the full reasoning on why
/// each is safe to not port: (1) the legacy `m_Sync||m_SyncMode&gt;=3` condition's own
/// ~30ms(normal VIS)/~270ms(extended VIS) early-switch window (legacy starts using H1 slightly before
/// `m_Sync` itself goes to 1; this port switches exactly at the lock anchor instead); (2) AVT's
/// *during-training* H1 usage, a separate, bigger, already-tracked AVT gap; (3) legacy's `Stop()` keeps
/// `m_SyncMode` at 512 through the whole 0.5s post-image dead zone (`sstv.cpp:1786`, cases 512-513,
/// `sstv.cpp:2243-2252`), so real legacy stays on H1 there too -- this port drops to H2 the instant
/// `EndOfImage` clears `_mode`, even though that dead zone's own samples still get demodulated
/// (`AdvanceAgcThroughDeadZone`) and feed AGC state into the next transmission's search.
///
/// <c>H1</c> and <c>H2</c>'s parameters are IDENTICAL across all three legacy width presets
/// (Wide/Narrow/VeryNarrow) except tap count (24/64/96, scaled by sample rate) -- this port has no
/// settings UI, so only the shipped default (Wide, `m_bpf=1`, confirmed `sstv.cpp:1416` and the `.ini`
/// `DEMBPF` key's own fallback, `Main.cpp:1855`) is ever reachable. At Wide: `H2` is 400-2500Hz,
/// attenuation 20 (`CalcBPF` case 1, `sstv.cpp:1528`); `H1` is 1100-2600Hz (`lfq=1100` since
/// `m_SyncRestart` is hardwired on, `sstv.cpp:1486`; `g_dblToneOffset` confirmed 0 absent CQ100),
/// attenuation 20 (`sstv.cpp:1527`) -- SAME attenuation as H2, reconfirming this port's already-resolved
/// S28 pre-check (Kaiser/Bessel branch needs att&gt;=21, unreached by either filter).
///
/// <b>The Kaiser/Bessel-windowed branch of legacy's filter designer (`MakeFilter`, `fir.cpp:346-427`)
/// is NOT ported</b>: it only activates when attenuation &gt;= 21dB, and both H1 and H2's attenuation is
/// always 20 at every preset -- provably unreachable for either filter, not an approximation. Matches
/// this port's established precedent (Piece 14's `HilbertFmDemodulator.MakeHilbert` similarly omits a
/// provably-unreachable branch) rather than building code that can never execute.
///
/// <b>Input is piece 15's already-ported 2-tap moving-average pre-filter's OUTPUT, not raw samples</b>
/// (`sstv.cpp:1824-1834`: `d=(s+m_ad)*0.5` &#8594; `if(m_bpf) d=m_BPF.Do(d,...)` &#8594; `m_lvl.Do(d)`) --
/// chains onto <see cref="AnalogFmSstvDecoder"/>'s `FilteredRawSampleAt`, applied at the exact same
/// consumption sites, matching legacy's single shared `d` value feeding all of them (the picture
/// demodulator, the AGC/sync-envelope-detector pipeline, and AVT's dedicated PLL).
///
/// <b>One shared delay line for both filters, not two independent instances.</b> Legacy's real
/// convolution engine, `CFIR2::Do(d, hp)` (`fir.cpp:1131-1144`), maintains ONE delay line (`m_pZ`) and
/// simply chooses which coefficient table to dot-product against per call -- it is NOT two independent
/// filter objects, so there is no separate "H1 filter-state warm-up" concern: the very first
/// H1-selected sample already has full delay-line history from whatever H2-processed samples came
/// before it, exactly matching legacy's real behavior. Confirmed via `CFIR2::Create`/`SetBPF`
/// (`fir.cpp:1079-1093`, `sstv.cpp:1602-1613`): a coefficient-table swap at constant tap count (true for
/// H1 vs H2 at every preset) needs no delay-line compensation either.
///
/// <b>The convolution window is CAUSAL, `[index-tap, index]`, not centered -- load-bearing, verified
/// during plan-review, not a detail to gloss over.</b> `CFIR2::Do` pairs `H[0]` with the NEWEST sample
/// and walks backward, giving a real, constant `tap/2`-sample group delay (~1.09ms at every reachable
/// sample rate, since tap scales with rate). `MakeFilter`'s output kernel is symmetric by construction
/// for EVEN tap (this port's only reachable tap counts, 24@11025Hz/96@44100Hz, are both even) -- which
/// rules out a coefficient-reversal sign risk (unlike <see cref="HilbertFmDemodulator"/>'s antisymmetric
/// kernel), but does NOT make the window's causal-vs-centered alignment irrelevant: a centered window
/// would pass a symmetry check, a coefficient-fixture check, and a frequency-response check identically,
/// while silently shifting every downstream sync/slant/line anchor by `tap/2` samples -- the exact
/// "restructured-but-provably-equivalent" failure shape CLAUDE.md §4's Scottie incident warns about.
///
/// This needs no NEW sync-anchor correction term (unlike piece 14's Hilbert `+htap/4`), reasoned
/// through explicitly during plan-review, not assumed: this filter is inserted uniformly before ALL of
/// this port's raw-sample consumers via the single shared `AnalogFmSstvDecoder.BandpassFilteredSampleAt`
/// choke point, so both the sync-envelope-detector path (<see cref="SyncEnvelopeDetector"/>, feeding
/// <see cref="SyncAnchorCorrector"/>'s argmax search) and the picture-demodulator path see the SAME
/// filter selection, at the SAME absolute sample index, together -- no differential delay/selection for
/// either path to disagree about, the same group-delay-skew risk an early plan-review flagged and this
/// design closes structurally rather than by coincidence.
/// </summary>
internal sealed class SearchBandpassFilter
{
    private readonly double[] _h1; // locked/normal (HBPF) -- Band-1 item 4b
    private readonly double[] _h2; // search/pre-lock (HBPFS) -- Piece B
    private readonly double[] _z; // delay line, length tap+1. _z[0]=newest sample, _z[tap]=oldest.
    private readonly int _tap;

    public SearchBandpassFilter(int sampleRate)
    {
        _tap = (int)(24 * sampleRate / 11025.0); // bpftap, Wide preset -- this port's only reachable config
        _h1 = MakeFilter(_tap, sampleRate, fcl: 1100.0, fch: 2600.0);
        _h2 = MakeFilter(_tap, sampleRate, fcl: 400.0, fch: 2500.0);
        _z = new double[_tap + 1]; // zero-init -- matches CFIR2::Create's own zero-memset delay line
                                    // (fir.cpp:1087-1088), confirmed never reset mid-stream on RX
                                    // (Clear() is TX-only, sstv.cpp:2827).
    }

    /// <summary>Streaming, one-sample-at-a-time convolution -- a genuine persistent delay line, not a
    /// stateless window recompute (round-2 performance fix: an earlier <c>Func&lt;int,double&gt;</c>-
    /// window-lookup version was measured to cause a real ~4x full-suite slowdown from delegate-call
    /// overhead and redundant re-reads of overlapping windows across consecutive calls -- this shape
    /// matches <see cref="HilbertFmDemodulator"/>'s own already-proven <c>DoFir</c> pattern instead, and
    /// is arguably a MORE literal match to <c>CFIR2</c>'s real internal delay-line structure than the
    /// window-lookup version was). Feed each newly <c>FilteredRawSampleAt</c>-filtered sample exactly
    /// once, in order -- this is a stateful streaming filter, not a pure function of index.
    ///
    /// CAUSAL, not centered -- <c>_z[0]</c> (this call's newest sample) pairs with <c>H[0]</c>, matching
    /// <c>CFIR2::Do</c>'s real addressing (`fir.cpp:1131-1144`) exactly. See this class's own doc
    /// comment for why this direction is load-bearing.
    ///
    /// <paramref name="useLocked"/> selects <c>H1</c> (locked/normal) vs <c>H2</c> (search/pre-lock) --
    /// the delay line itself always shifts regardless (matching <c>CFIR2::Do(d, hp)</c>'s own single-
    /// delay-line-plus-coefficient-choice shape, see class doc comment), only the dot-product's
    /// coefficient table differs.</summary>
    public double ProcessSample(double input, bool useLocked)
    {
        Array.Copy(_z, 0, _z, 1, _tap);
        _z[0] = input;

        var h = useLocked ? _h1 : _h2;
        var sum = 0.0;
        for (var i = 0; i <= _tap; i++)
        {
            sum += _z[i] * h[i];
        }

        return sum;
    }

    // Literal port of MakeFilter's non-Kaiser branch (fir.cpp:346-427, the att<21 path -- see this
    // class's own doc comment for why the Kaiser branch is never reached here). fc=(fch-fcl)/2 is the
    // ffBPF-specific half-bandwidth prototype cutoff (fir.cpp:355-357) -- LPF/HPF's own different fc
    // formulas aren't ported since this filter is only ever constructed in BPF mode. Ports the
    // `if(sum>0.0)` DC-normalization guard literally (fir.cpp:399) rather than assuming the
    // prototype's DC sum is always positive, even though it provably is for this filter's own
    // parameters -- a real conditional in the source, not invented. Mirrors legacy's EXACT two-loop
    // mirroring bounds (fir.cpp:421-426, 2*(tap/2)+1 total entries written, not tap+1) so odd-tap
    // inputs get legacy's real trailing-zero behavior via C#'s own array zero-init, not a "helpfully"
    // always-fully-populated array -- this port's own reachable tap counts are both even so this is
    // latent, not currently exercised, but not special-cased away either.
    internal static double[] MakeFilter(int tap, double sampleRate, double fcl, double fch)
    {
        var half = tap / 2;
        var fc = (fch - fcl) / 2.0;
        var sumArg = 2.0 * Math.PI * fc / sampleRate;

        var prototype = new double[half + 1];
        for (var j = 0; j <= half; j++)
        {
            prototype[j] = j == 0 ? fc * 2.0 / sampleRate : Math.Sin(j * sumArg) / (Math.PI * j);
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

        var w0 = Math.PI * (fcl + fch) / sampleRate;
        for (var j = 0; j <= half; j++)
        {
            prototype[j] *= 2.0 * Math.Cos(j * w0);
        }

        var h = new double[tap + 1]; // zero-init -- matters for odd tap's untouched trailing slot
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
}
