namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CLVL</c> (`sstv.h:223-298`) -- the shared peak-based AGC that legacy's
/// <c>CSSTVDEM::Do</c> applies, once per sample, ahead of every sync/tone-envelope discriminator
/// (`sstv.cpp:1834-1839`: <c>m_lvl.Do(d); ad = m_lvl.AGC(d); d = clamp(ad*32, +-16384)</c>). Confirmed
/// this only affects the sync-detection path -- the actual FM pixel/AFC demodulator is fed
/// <c>m_lvl.m_Cur</c>, the pre-AGC raw value (`sstv.cpp:2258/2259/2262/2263/2266/2267/2312/2315/2318`),
/// so this class exists purely to make the absolute-amplitude thresholds (<c>m_SLvl</c>/<c>m_SLvl2</c>/
/// <c>m_SLvl3</c>) meaningful again, not to change pixel decode.
///
/// <c>m_agcfast</c>-only port: <c>CSSTVDEM</c>'s constructor sets <c>m_lvl.m_agcfast = 1</c>
/// unconditionally (`sstv.cpp:1470`) and nothing else in the program ever touches it, so legacy's
/// <c>m_agcfast == 0</c> branch (`sstv.h:272-279`, an averaged-5-window AGC recompute) is unreachable
/// dead code for this demodulator -- not ported. The surrounding peak-hold bookkeeping that branch
/// shares a block with (<c>m_PeakMax</c>/<c>m_PeakAGC</c>/<c>m_Peak</c>/<c>m_CntPeak</c>,
/// `sstv.h:265-283`) still executes regardless of <c>m_agcfast</c>, but is write-only in this program
/// too -- its only reader is the UI level-meter's peak-hold bar (`Main.cpp:6186-6192`), which this
/// port has no equivalent of yet. Omitted here as a deliberate, documented simplification (not a
/// silent drop of tracked-but-unread state); revisit if/when a level-meter UI is built.
///
/// <c>m_Cur</c> (legacy's pre-AGC "current sample", read by the AFC/pixel path) isn't tracked here
/// either: legacy's value is exactly the same <c>d</c> already available at every call site in this
/// port (no LPF/BPF stage exists yet ahead of this class -- legacy's own averaging LPF + bandpass
/// filter, `sstv.cpp:1823-1833`, has no counterpart here, a separately logged gap), so callers just
/// keep using their own raw sample directly instead of fetching it back out of this class.
///
/// Scale bridge: legacy's <c>d</c> is int16-valued (confirmed via `sstv.cpp:1821`'s 24578 overflow
/// check and `Wave.cpp:796-808`'s direct <c>SHORT</c>-to-<c>double</c> copy), so <c>CLVL</c>'s
/// constants (32, 16384, the "&gt;32" gate) are calibrated to roughly a +-32768 input range. This
/// port's own contract is `float` in [-1.0, 1.0] (`spec/05-audio-engine.md:44`) -- feeding that
/// straight in would pin the AGC at its floor gain forever (the "&gt;32" adaptation gate would never
/// trip). Callers must scale by 32768.0 before calling <see cref="Do"/>, keeping every constant in
/// this class itself literally identical to legacy.
/// </summary>
internal sealed class LevelAgc
{
    private readonly int _cntMax; // m_CntMax = SampFreq*100/1000 (sstv.h:242), this port's own sample rate substituted for legacy's fixed SampFreq

    private double _max; // m_Max
    private double _curMax; // m_CurMax
    private double _agc; // m_agc
    private int _cnt; // m_Cnt

    public LevelAgc(double sampleRate)
    {
        _cntMax = (int)(sampleRate * 100 / 1000.0); // sstv.h:242
        _agc = 1.0; // Init(), sstv.h:252
    }

    /// <summary>Legacy's <c>m_CurMax</c> -- the peak absolute amplitude over the most recently
    /// completed <see cref="Fix"/> window. Gates AFC's <c>SyncFreq</c> correction in legacy
    /// (`sstv.cpp:2258/2263/2267`: <c>m_lvl.m_CurMax &gt; 16</c>). 0 until the first <see cref="Fix"/>
    /// actually executes, matching legacy's <c>Init</c> default (`sstv.h:250`) exactly.</summary>
    public double CurMax => _curMax;

    /// <summary><c>CLVL::Do</c> (`sstv.h:256-261`) -- tracks the peak absolute amplitude seen since
    /// the last executed <see cref="Fix"/>. Call once per sample, every sample, before
    /// <see cref="Fix"/>.</summary>
    public void Do(double d)
    {
        if (d < 0.0)
        {
            d = -d;
        }

        if (_max < d)
        {
            _max = d;
        }

        _cnt++;
    }

    /// <summary><c>CLVL::Fix</c> (`sstv.h:262-294`), <c>m_agcfast</c>-only branch. Self-throttled --
    /// no-ops until <see cref="_cntMax"/> samples have been fed via <see cref="Do"/> since the last
    /// time this actually ran, so it's safe (and a faithful, if slightly finer-grained, adaptation) to
    /// call this every sample from a DSP-clock-driven loop, unlike legacy's own UI-paint-driven call
    /// site (`Main.cpp:6161`, effectively a ~200ms cadence via `Main.dfm`'s <c>Timer.Interval</c>, vs.
    /// this port's ~100ms <see cref="_cntMax"/> window -- a real, if minor, documented deviation:
    /// legacy's peak-measurement window is usually 200ms, this port's is 100ms, updating the gain
    /// twice as often).</summary>
    public void Fix()
    {
        if (_cnt < _cntMax)
        {
            return;
        }

        _cnt = 0;
        _curMax = _max;
        _agc = _curMax > 32.0 ? 16384.0 / _curMax : 16384.0 / 32.0;
        _max = 0;
    }

    /// <summary><c>CLVL::AGC</c> (`sstv.h:295-297`).</summary>
    public double Agc(double d) => d * _agc;
}
