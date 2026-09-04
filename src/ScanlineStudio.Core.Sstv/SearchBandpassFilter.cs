using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct, literal port of legacy's pre-AGC bandpass filter (`CSSTVDEM::Do`, `sstv.cpp:1826-1833`):
/// `m_BPF.Do(d, m_Sync||m_SyncMode&gt;=3 ? (m_fNarrow?HBPFN:HBPF) : HBPFS)` -- the "search"/pre-lock
/// variant (`H2`/`HBPFS`) always; the "locked" normal variant (`H1`/`HBPF`) once locked on a non-narrow
/// mode (Band-1 item 4b); and, once again, the "locked" narrow variant (`H3`/`HBPFN`) once locked on an
/// MN/MC mode, built reactively per <see cref="BuildNarrowLockedFilter"/>'s own doc comment. This exact
/// change previously shipped, passed a full plan+code review (both explicit "yes"), then was REVERTED
/// the same day -- not for a code-correctness reason, but because the only evidence available at the
/// time (clean-signal golden vectors) showed no measured benefit, and the user's priority is measured
/// reception improvement, not legacy parity for its own sake. It is reapplied here because a purpose-
/// built noise-impairment sweep (`ImpairmentSweepHarness.cs`) found the real, targeted evidence that
/// was missing: all 6 MN/MC modes show a dramatically worse noise floor (16-20dB) than every other
/// tested mode (0-3dB) under the CURRENT always-wide-H2 behavior -- exactly the failure mode H3 exists
/// to fix (a narrower post-lock passband rejects broadband noise a wide one doesn't). The code itself
/// is unchanged from what already passed review; what changed is having the right test to judge it by.
/// Three further legacy divergences, all deliberate, all documented (not
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
/// <b>RX BPF subsystem (`RxBpfPreset`): H2's params are preset-invariant except tap; H1's are NOT.</b>
/// `CalcBPF` (`sstv.cpp:1522-1550`) drives all 4 presets (`m_bpf`/`DEMBPF`, `Off`/`Wide`/`Narrow`/
/// `VeryNarrow`). `H2` is always 400-2500Hz, attenuation 20 across all three constructible presets
/// (`sstv.cpp:1531,1537,1543` -- tap count still scales with the preset, only cutoffs/attenuation stay
/// fixed). `H1`'s `fch`/`att` both vary by preset (Wide 2600Hz/20dB, Narrow 2500Hz/40dB, VeryNarrow
/// 2400Hz/50dB), AND its `fcl` varies by <c>syncRestartEnabled</c> -- not by preset -- via legacy's
/// `lfq = (m_SyncRestart ? 1100 : 1200) + g_dblToneOffset` (`sstv.cpp:1524`, computed once above the
/// preset switch and reused by all three cases; `g_dblToneOffset` confirmed 0 in this port, no
/// drift-offset feature ported). `Off` (`m_bpf=0`) is a true bypass at the call site
/// (`sstv.cpp:1826`'s `if(m_bpf){...}` gate) -- this class is never constructed for it; see
/// <see cref="AnalogFmSstvDecoder"/>'s own doc comment for how the bypass is represented there.
///
/// <b>The Kaiser/Bessel-windowed branch of legacy's filter designer (`MakeFilter`, `fir.cpp:346-427`)
/// IS ported</b>, since `Narrow`/`VeryNarrow`'s H1 attenuation (40/50dB) both clear the `att&gt;=21`
/// threshold that activates it (`fir.cpp:373-383`) -- see <see cref="MakeFilter"/>'s own doc comment.
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
/// during plan-review, not a detail to gloss over.</b> `CFIR2::Do` pairs `H[0]` with the LOGICALLY
/// NEWEST sample (index 0 in <see cref="FirDelayLine"/>'s own newest-first convention, see that class's
/// doc comment) and walks backward, giving a real, constant `tap/2`-sample group delay PER PRESET (~1.09ms at Wide,
/// ~2.90ms at Narrow, ~4.35ms at VeryNarrow, each rate-invariant within its own preset since tap scales
/// with rate) -- NOT one constant across all presets; legacy carries this same tap-dependent delay, and
/// nothing downstream in this port may assume Wide's 12-sample figure once Narrow/VeryNarrow are
/// reachable (Phase 2's job to confirm at the decoder-wiring level). `MakeFilter`'s output kernel is
/// symmetric by construction for EVEN tap -- this port has 9 reachable preset/sample-rate combinations
/// (3 presets x 11025/22050/44100Hz, 8 distinct tap values -- {24,48,96} union {64,128,256} union
/// {96,192,384}, 96 recurring at both Wide@44100Hz and VeryNarrow@11025Hz), all of them even because
/// the rate ratios (1/2/4) are exact multiples of the base tap-per-preset value.
/// This stops holding at a non-standard rate (e.g. 48000Hz) -- that's what `MakeFilter`'s own odd-tap
/// trailing-zero handling below is for, not currently exercised by any of this port's 9 reachable
/// combinations but not special-cased away either. Even tap rules out a coefficient-reversal sign risk
/// (unlike <see cref="HilbertFmDemodulator"/>'s antisymmetric kernel), but does NOT make the window's
/// causal-vs-centered alignment irrelevant: a centered window would pass a symmetry check, a
/// coefficient-fixture check, and a frequency-response check identically, while silently shifting
/// every downstream sync/slant/line anchor by `tap/2` samples -- the exact "restructured-but-provably-
/// equivalent" failure shape CLAUDE.md §4's Scottie incident warns about.
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
    private double[] _h1; // locked/normal (HBPF) -- Band-1 item 4b. NOT readonly -- see UpdateSyncRestart.
    private readonly double[] _h2; // search/pre-lock (HBPFS) -- Piece B
    private double[]? _h3; // locked/narrow (HBPFN) -- null until BuildNarrowLockedFilter runs for the
                            // first narrow-mode lock (which mode's band to use isn't known before
                            // that, unlike H1/H2, see that method's own doc comment). Same threading
                            // contract as _h1 (see UpdateSyncRestart's own doc comment): written only
                            // from AnalogFmSstvDecoder.Commit, read only from ProcessSample, both
                            // always the decode thread -- no Volatile/lock needed, program order alone
                            // gives correct visibility.
    private readonly FirDelayLine _z; // delay line, length tap+1. Logical index 0=newest sample, tap=oldest -- see FirDelayLine's own doc comment.
    private readonly int _tap;
    private readonly int _sampleRate; // retained (not just a ctor local) so UpdateSyncRestart can recompute H1
    private readonly RxBpfPreset _preset; // retained so BuildNarrowLockedFilter can look up H3's OWN
                                           // preset-dependent (fcl offset, att) pair -- see that
                                           // method's own doc comment for why this must NOT reuse
                                           // _h1Fch/_h1Att, even though today's numbers coincide.
    private readonly double _h1Fch;
    private readonly double _h1Att;

    /// <param name="preset">Wide/Narrow/VeryNarrow only -- <see cref="RxBpfPreset.Off"/> is a bypass
    /// decision owned by <see cref="AnalogFmSstvDecoder"/> (which leaves its own filter field null
    /// instead of constructing this class), keeping this class's own invariant simple: every instance
    /// represents a real, constructed filter.</param>
    /// <param name="syncRestartEnabled">Mirrors legacy's real, live `m_SyncRestart` (this port's
    /// `AnalogFmSstvDecoder._syncRestartEnabled`) -- drives H1's `fcl` (1100Hz when true, 1200Hz when
    /// false) via legacy's `lfq` formula, see class doc comment. H2's `fcl` is the constant 400Hz
    /// regardless.</param>
    public SearchBandpassFilter(int sampleRate, RxBpfPreset preset, bool syncRestartEnabled)
    {
        var (multiplier, h1Fch, h1Att) = preset switch
        {
            RxBpfPreset.Wide => (24, 2600.0, 20.0),
            RxBpfPreset.Narrow => (64, 2500.0, 40.0),
            RxBpfPreset.VeryNarrow => (96, 2400.0, 50.0),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset,
                "SearchBandpassFilter only supports Wide/Narrow/VeryNarrow -- Off is a bypass handled " +
                "by AnalogFmSstvDecoder, which never constructs this class for it."),
        };

        _tap = (int)(multiplier * sampleRate / 11025.0); // bpftap, scaled by sample rate (sstv.cpp:1529/1535/1541)
        _sampleRate = sampleRate;
        _preset = preset;
        _h1Fch = h1Fch;
        _h1Att = h1Att;
        var h1Fcl = syncRestartEnabled ? 1100.0 : 1200.0; // lfq, sstv.cpp:1524
        _h1 = MakeFilter(_tap, sampleRate, fcl: h1Fcl, fch: h1Fch, att: h1Att);
        _h2 = MakeFilter(_tap, sampleRate, fcl: 400.0, fch: 2500.0, att: 20.0);
        _z = new FirDelayLine(_tap, headMovesForward: false); // zero-init -- matches CFIR2::Create's own
                                    // zero-memset delay line (fir.cpp:1087-1088), confirmed never reset
                                    // mid-stream on RX (Clear() is TX-only, sstv.cpp:2827).
    }

    /// <summary>Live-apply for a SyncRestart flag change (2026-08-27, restart-required-settings
    /// backlog item 1) -- rebuilds H1's coefficients for the new `fcl` (1100/1200Hz), the only
    /// tap-count-invariant value this flag affects (H2's own `fcl` is the constant 400Hz regardless,
    /// class doc comment). Deliberately does NOT touch <see cref="_z"/> -- legacy's own
    /// `CFIR2::Create` only reinitializes the delay line when TAP COUNT itself changes
    /// (`fir.cpp:1085`'s `(m_Tap != tap)` guard), and this flag never changes tap, only H1's `fcl` --
    /// so the delay line's in-flight history survives this call unmodified, exactly as it would in
    /// legacy at an unchanged preset (`sstv.cpp:1602-1613`'s own `SetBPF`).
    ///
    /// Deliberate DIVERGENCE from legacy's own Options-OK path specifically: `Option.cpp:609-611`
    /// assigns `m_SyncRestart` AFTER its own `SetBPF()` call already ran that OK click, with no later
    /// `CalcBPF()` -- legacy's Options dialog does NOT rebuild H1 for this exact change. This method
    /// is called from <see cref="AnalogFmSstvDecoder"/>'s Options-driven live-apply path anyway,
    /// matching legacy's TOOLBAR-toggle path instead (`Main.cpp:11887-11888`'s `KRARClick`, which DOES
    /// call `CalcBPF()`) -- the defensible choice (this port has no separate toolbar/Options
    /// distinction for this setting), but a real divergence from what Options-OK specifically does in
    /// legacy, not parity.
    ///
    /// Caller-side threading contract: must be called from the decode thread only (the same thread
    /// that calls <see cref="ProcessSample"/>), never directly from a UI thread -- see
    /// <c>AnalogFmSstvDecoder.ApplyPendingDecoderFlagsRequest</c>'s own doc comment. With that
    /// contract, <see cref="_h1"/> has exactly one writer and one reader, both this same thread, so
    /// program order alone gives correct visibility -- no <see cref="System.Threading.Volatile"/> or
    /// lock is needed here (plan-review round 2, confirmed: production has no other reader of this
    /// array at all, only <see cref="ProcessSample"/> itself. <see cref="H1ForTests"/> below DOES
    /// also read <see cref="_h1"/>, but only from single-threaded test code, never concurrently with
    /// a real decode -- not a reachable concurrent-reader case in production). Takes effect from
    /// whatever sample <see cref="ProcessSample"/> is next
    /// called with -- since the caller's own cache (`AnalogFmSstvDecoder.FilteredRawSampleAt`) is a
    /// lazy-fill, that is the cache's own forward cursor position, not necessarily the very latest
    /// sample just pushed -- a bounded lag, not instant, same as every other change to that cache.
    ///
    /// Deliberately does NOT touch <see cref="_h3"/> -- `CalcNarrowBPF` (`sstv.cpp:1553-1594`) has no
    /// `lfq`/`m_SyncRestart` dependence at all (only `CalcBPF`'s H1/H2 half does), so legacy's own
    /// `CalcBPF`-driven rebuild is numerically a no-op for HBPFN too. Leaving `_h3` alone here also
    /// avoids a spurious <see cref="InvalidOperationException"/> from <see cref="ProcessSample"/>'s own
    /// null-`_h3` guard: a naive "rebuild everything" here would need `mode` to rebuild against, which
    /// this method doesn't have, and calling it before any narrow lock has happened would be wrong
    /// regardless.</summary>
    public void UpdateSyncRestart(bool syncRestartEnabled)
    {
        var h1Fcl = syncRestartEnabled ? 1100.0 : 1200.0;
        _h1 = MakeFilter(_tap, _sampleRate, fcl: h1Fcl, fch: _h1Fch, att: _h1Att);
    }

    /// <summary>Diagnostic-only: exposes <see cref="_h1"/>'s current coefficients so a test can prove
    /// <see cref="UpdateSyncRestart"/> actually rebuilt them, not just that it ran without
    /// throwing.</summary>
    internal double[] H1ForTests => _h1;

    /// <summary>Diagnostic-only: exposes <see cref="_h3"/> (null until the first narrow-mode lock) so a
    /// test can assert the exact coefficients <see cref="BuildNarrowLockedFilter"/> produced, the same
    /// way <see cref="H1ForTests"/> exposes H1's.</summary>
    internal double[]? H3ForTests => _h3;

    /// <summary>(Re)builds the locked/narrow filter (`H3`/`HBPFN`), mirroring legacy's `CalcNarrowBPF`
    /// (`sstv.cpp:1553-1594`), called reactively once the locked mode is actually known -- see
    /// <see cref="AnalogFmSstvDecoder.Commit"/>'s own call site, the direct analogue of legacy's
    /// `Start()` calling `CalcNarrowBPF(HBPFN, m_bpftap, m_bpf, SSTVSET.m_Mode)` the moment a
    /// reception locks (`sstv.cpp:1745-1746`). Unlike H1/H2, which mode's band to use isn't known at
    /// CONSTRUCTION time (any mode can be received), so `_h3` starts null and is built here instead of
    /// in the constructor.
    ///
    /// Caller contract: MUST only be called with a mode whose <see cref="SstvModeDefinition.NarrowModeCode"/>
    /// is not null (matches legacy's own `if( m_fNarrow )` guard, `sstv.cpp:1746`) -- calling it for a
    /// non-narrow mode would build and store an unused table via the `default` arm below, which is
    /// harmless but pointless; the real guard belongs at the call site, matching legacy's placement.
    ///
    /// Two independent lookups, both literal ports of `CalcNarrowBPF`'s own two switches:
    /// mode-&gt;(low,high) via <see cref="SstvModeDefinition.NarrowModeCode"/> (`sstv.cpp:1556-1578`:
    /// smMN73/smMN110/smMC110 -&gt; 1600/2500; smMN140/smMC180 -&gt; 1700/2400; smMC140 -&gt; 1650/2500;
    /// `default` -&gt; 1600/2500, ported literally even though every reachable narrow code already has
    /// its own case); then preset-&gt;(fcl offset, att) via this instance's own `_preset`
    /// (`sstv.cpp:1580-1591`: Wide -&gt; (low-200, 20), Narrow -&gt; (low-100, 40), VeryNarrow -&gt; (low, 50)).
    /// `fch` is always `high`, `gain` is always 1.0, `tap` is the SAME `_tap` H1/H2 already use (legacy
    /// passes the one shared `bpftap` to all three). No `g_dblToneOffset` term, for the same reason
    /// H1/H2 already omit it (class doc comment) -- it is a real, already-documented un-ported feature
    /// (reachable as -1000.0 in legacy via `Main.cpp`'s `-i`/CQ100 command-line flag), not a value this
    /// port has confirmed is always zero.
    ///
    /// Deliberately does NOT read/write <see cref="_h1Att"/> despite today's numbers coinciding
    /// (Wide/Narrow/VeryNarrow att 20/40/50 for both H1 and H3) -- these come from two SEPARATE legacy
    /// switches (`CalcBPF`'s own `case` block vs `CalcNarrowBPF`'s), so reusing `_h1Att` here would be
    /// correct only by coincidence, not by construction; a future change to either switch's own
    /// attenuation table would silently desync them if this method borrowed the other's field.</summary>
    public void BuildNarrowLockedFilter(SstvModeDefinition mode)
    {
        var (low, high) = mode.NarrowModeCode switch
        {
            0x02 or 0x04 or 0x14 => (1600.0, 2500.0), // smMN73, smMN110, smMC110
            0x05 or 0x16 => (1700.0, 2400.0),         // smMN140, smMC180
            0x15 => (1650.0, 2500.0),                 // smMC140
            _ => (1600.0, 2500.0),                    // legacy's own switch default (sstv.cpp:1578)
        };

        var (fclOffset, att) = _preset switch
        {
            RxBpfPreset.Wide => (-200.0, 20.0),
            RxBpfPreset.Narrow => (-100.0, 40.0),
            RxBpfPreset.VeryNarrow => (0.0, 50.0),
            // A state precondition on _preset, not an argument problem with this call -- InvalidOperationException,
            // not ArgumentOutOfRangeException (which would misleadingly name the `mode` parameter for a
            // field-value problem). Dead in practice: the constructor already rejects Off/undefined via
            // its own ArgumentOutOfRangeException, so _preset can never reach this arm.
            _ => throw new InvalidOperationException(
                $"BuildNarrowLockedFilter only supports Wide/Narrow/VeryNarrow, but _preset was {_preset} -- " +
                "Off never constructs this class at all (see the constructor's own ArgumentOutOfRangeException)."),
        };

        _h3 = MakeFilter(_tap, _sampleRate, fcl: low + fclOffset, fch: high, att: att);
    }

    /// <summary>Streaming, one-sample-at-a-time convolution -- a genuine persistent delay line, not a
    /// stateless window recompute (round-2 performance fix: an earlier <c>Func&lt;int,double&gt;</c>-
    /// window-lookup version was measured to cause a real ~4x full-suite slowdown from delegate-call
    /// overhead and redundant re-reads of overlapping windows across consecutive calls -- this shape
    /// matches <see cref="HilbertFmDemodulator"/>'s own already-proven <c>DoFir</c> pattern instead, and
    /// is arguably a MORE literal match to <c>CFIR2</c>'s real internal delay-line structure than the
    /// window-lookup version was). Feed each newly <c>FilteredRawSampleAt</c>-filtered sample exactly
    /// once, in order -- this is a stateful streaming filter, not a pure function of index. Narrow/
    /// VeryNarrow's larger tap counts (64/96 vs Wide's 24, scaled further by sample rate) make this
    /// O(tap) per call more expensive than Wide -- budget golden-vector fixture counts at those presets
    /// deliberately (see `GoldenVectorTests.cs`'s `RxBpfDecoderFixtures`).
    ///
    /// CAUSAL, not centered -- logical index 0 (this call's newest sample) pairs with <c>H[0]</c>,
    /// matching <c>CFIR2::Do</c>'s real addressing (`fir.cpp:1131-1144`) exactly. See this class's own
    /// doc comment and <see cref="FirDelayLine"/>'s own doc comment for why this direction is
    /// load-bearing.
    ///
    /// <paramref name="useLocked"/> selects locked (<c>H1</c> or <c>H3</c>) vs <c>H2</c> (search/pre-
    /// lock) -- the delay line itself always shifts regardless (matching <c>CFIR2::Do(d, hp)</c>'s own
    /// single-delay-line-plus-coefficient-choice shape, see class doc comment), only the dot-product's
    /// coefficient table differs. <paramref name="useNarrow"/> (only consulted when
    /// <paramref name="useLocked"/> is true) selects <c>H3</c> (locked/narrow) over <c>H1</c>
    /// (locked/normal), mirroring legacy's own nested `m_fNarrow ? HBPFN : HBPF` inside the locked arm
    /// (`sstv.cpp:1828`) -- narrow-ness is never an independent branch, only a sub-selection of
    /// "locked."
    ///
    /// Required, not defaulted: this filter has exactly one production call site
    /// (<c>AnalogFmSstvDecoder.BandpassFilteredSampleAt</c>), which always has both values in hand
    /// already -- a default would let a future second call site silently get search-vs-narrow wrong by
    /// omission rather than by an explicit, reviewable choice.
    ///
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="useNarrow"/> is true while
    /// <see cref="_h3"/> is still null, rather than silently falling back to H1 -- the invariant that
    /// makes this unreachable in production lives in a DIFFERENT file
    /// (<c>AnalogFmSstvDecoder.Commit</c> always calls <see cref="BuildNarrowLockedFilter"/> for a
    /// narrow `effectiveMode` on the very next statement after setting `_mode`, before any code path
    /// can reach this method with that same narrow mode), so neither the compiler nor a reader of this
    /// class alone can see why it's safe -- an explicit throw documents the cross-file coupling at
    /// exactly the point it would break, matching this class's own existing precedent for the analogous
    /// "structurally impossible given caller discipline, but not compiler-provable" case
    /// (<see cref="ArgumentOutOfRangeException"/> for <see cref="RxBpfPreset.Off"/> in the
    /// constructor).</summary>
    public double ProcessSample(double input, bool useLocked, bool useNarrow)
    {
        _z.Push(input);

        double[] h;
        if (!useLocked)
        {
            h = _h2;
        }
        else if (useNarrow)
        {
            h = _h3 ?? throw new InvalidOperationException(
                "ProcessSample called with useNarrow=true before BuildNarrowLockedFilter ever ran -- " +
                "AnalogFmSstvDecoder.Commit must build H3 for the locked mode before any sample can be " +
                "processed against it.");
        }
        else
        {
            h = _h1;
        }

        return _z.Convolve(h);
    }

    /// <summary>Literal port of `MakeFilter` (`fir.cpp:346-427`), BPF mode only (this filter is never
    /// constructed in LPF/HPF mode, so those branches aren't ported). `fc=(fch-fcl)/2` is the
    /// `ffBPF`-specific half-bandwidth prototype cutoff (`fir.cpp:355-357`). Ports the `if(sum&gt;0.0)`
    /// DC-normalization guard literally (`fir.cpp:399`) rather than assuming the prototype's DC sum is
    /// always positive, even though it provably is for every one of this filter's reachable parameter
    /// combinations -- a real conditional in the source, not invented. Mirrors legacy's EXACT two-loop
    /// mirroring bounds (`fir.cpp:421-426`, 2*(tap/2)+1 total entries written, not tap+1) so odd-tap
    /// inputs get legacy's real trailing-zero behavior via C#'s own array zero-init, not a "helpfully"
    /// always-fully-populated array -- see class doc comment for why this port's own 9 reachable
    /// preset/rate combinations never actually hit an odd tap, latent but not special-cased away.
    ///
    /// <b>Kaiser-Bessel window (`att&gt;=21`, `fir.cpp:373-383`).</b> `alpha` is computed via the same
    /// three-way threshold as legacy (`fir.cpp:361-369`): `0.1102*(att-8.7)` for `att&gt;=50`,
    /// `0.5842*(att-21)^0.4 + 0.07886*(att-21)` for `21&lt;=att&lt;50`, else 0 (never reached here since
    /// this branch only runs when `att&gt;=21`). Each non-DC tap `j` gets multiplied by
    /// `I0(alpha*sqrt(1-fm²))/I0(alpha)` where `fm=2j/tap` -- ported into the same `j==0`/`j!=0` branch
    /// legacy uses (`fir.cpp:377-382`), matching legacy's own choice to leave the `j==0` prototype value
    /// unwindowed (numerically identical to windowing it anyway, since `fm=0` there makes the window
    /// factor exactly `I0(alpha)/I0(alpha)=1.0`, but this port doesn't rely on that -- it just doesn't
    /// touch `j==0` under the window multiply, same as legacy).</summary>
    internal static double[] MakeFilter(int tap, double sampleRate, double fcl, double fch, double att)
    {
        var half = tap / 2;
        var fc = (fch - fcl) / 2.0;
        var sumArg = 2.0 * Math.PI * fc / sampleRate;

        var useKaiser = att >= 21.0;
        var alpha = 0.0;
        var i0Alpha = 1.0;
        if (useKaiser)
        {
            alpha = att >= 50.0
                ? 0.1102 * (att - 8.7)
                : (0.5842 * Math.Pow(att - 21.0, 0.4)) + (0.07886 * (att - 21.0));
            i0Alpha = I0(alpha);
        }

        var prototype = new double[half + 1];
        for (var j = 0; j <= half; j++)
        {
            if (j == 0)
            {
                prototype[j] = fc * 2.0 / sampleRate;
                continue;
            }

            prototype[j] = Math.Sin(j * sumArg) / (Math.PI * j);
            if (useKaiser)
            {
                var fm = 2.0 * j / tap;
                prototype[j] *= I0(alpha * Math.Sqrt(1.0 - (fm * fm))) / i0Alpha;
            }
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

    /// <summary>Literal port of the modified Bessel function series `I0` (`fir.cpp:310-325`), used only
    /// by the Kaiser-window branch above. Converging series `I0(x) = Σ[(x/2)^k/k!]²`, computed
    /// iteratively (`xj *= (0.5*x)/j; sum += xj*xj`) rather than via factorials directly, matching
    /// legacy's own loop shape exactly, including its exit condition (`fir.cpp:322`:
    /// `if(((0.00000001*sum) - (xj*xj)) > 0) break;`) -- the term that FAILS this threshold check (i.e.
    /// is still large enough to matter) is added to `sum` BEFORE the check runs, so the final
    /// below-threshold term IS included in the result; a naive `while(term &gt; threshold)` guard
    /// restructuring would exclude it, a real last-ulp divergence from legacy that this port avoids by
    /// keeping the add-then-check order.</summary>
    private static double I0(double x)
    {
        var sum = 1.0;
        var xj = 1.0;
        var j = 1;
        while (true)
        {
            xj *= 0.5 * x / j;
            sum += xj * xj;
            j++;
            if ((0.00000001 * sum) - (xj * xj) > 0)
            {
                break;
            }
        }

        return sum;
    }
}
