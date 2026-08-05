namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Piece 10: replaces the old single-method <c>Func&lt;int,int,double&gt; sampleFrequencyAt</c>
/// delegate <see cref="IScanlineDecoder.DecodeLine"/> used to take. Exposes two DISTINCTLY-NAMED
/// methods so a decoder call site that reads the wrong channel's method fails loudly (a compile-time
/// method-name typo, not a silently-swapped same-typed argument) -- a design fix from this piece's
/// own round-2 plan review, which flagged the original two-<c>Func</c>-parameters shape as a
/// silent-argument-swap risk. Constructed fresh per <see cref="AnalogFmSstvDecoder"/>.DecodeLine call
/// (once per transmission line), since <see cref="SstvModeRegistry.GetKsbSamples"/> depends on that
/// line's own slant-adjusted effective sample rate (legacy recomputes <c>m_KSB</c> whenever auto-slant
/// changes the sample frequency, <c>Main.cpp:4015</c>/<c>:5900-5903</c>).
///
/// <see cref="ReadBare"/> is a direct port of legacy's <c>GetPixelLevel</c> read: a single sample at
/// the window's first index, no averaging (<c>Main.cpp:4144-4148</c> processes recorded audio one raw
/// sample at a time and keeps whichever is first at each pixel's computed index). <see cref="ReadPeakPicked"/>
/// ports <c>GetPictureLevel</c> (<c>Main.cpp:4057-4071</c>): compares two raw demodulated samples
/// <c>m_KSB</c> apart, keeps whichever is LARGER (traced end-to-end during this piece's round-3/4 plan
/// review: legacy's <c>m_Buf[n]=-d</c> sign convention means higher raw value == higher demodulated
/// frequency == the "brighter" sample in this port's own monotonic Hz-to-luma mapping, for both the
/// PLL and CFQC demodulator paths).
/// </summary>
internal sealed class PixelSampleReader
{
    private readonly Func<int, double> _rawSampleAt;
    private readonly int _ksbSamples;
    private readonly int _lineEndSampleExclusive;
    private readonly double _luminanceMinHz;
    private readonly bool _neverPeakPicks;

    /// <param name="rawSampleAt">The underlying single-sample source (index -&gt; demodulated Hz) --
    /// a delegate rather than a bare <c>List&lt;double&gt;</c> so this class stays unit-testable with
    /// an arbitrary synthetic source, and so the production caller owns its own index-clamping policy
    /// rather than this class assuming one particular buffer shape.</param>
    public PixelSampleReader(
        Func<int, double> rawSampleAt,
        int ksbSamples,
        int lineEndSampleExclusive,
        double luminanceMinHz,
        bool neverPeakPicks)
    {
        _rawSampleAt = rawSampleAt;
        _ksbSamples = ksbSamples;
        _lineEndSampleExclusive = lineEndSampleExclusive;
        _luminanceMinHz = luminanceMinHz;
        _neverPeakPicks = neverPeakPicks;
    }

    /// <summary>Direct port of <c>GetPixelLevel</c>'s bare dereference (<c>*ip</c>) -- the second
    /// argument is accepted but ignored, matching this method's pre-piece-10 shape exactly (kept so
    /// callers can pass either endpoint of a computed window without restructuring call sites).</summary>
    public double ReadBare(int startSample, int endSample) => _rawSampleAt(startSample);

    /// <summary>Direct port of <c>GetPictureLevel</c> (<c>Main.cpp:4057-4071</c>): races the sample at
    /// <paramref name="startSample"/> against the one <c>m_KSB</c> samples later, keeps the larger.
    ///
    /// Line-end boundary (round-2/round-3/round-4 plan review, corrected twice before landing here):
    /// legacy's demodulated buffer pads every line's trailing extent with exactly <c>-16384</c>
    /// (<c>sstv.cpp:1735-1741</c>, never overwritten) -- traced to equal each mode's own
    /// <c>LuminanceMinHz</c> exactly (<c>-16384</c> corresponds to <c>center-BWH</c>, 1500Hz normal /
    /// 2044Hz narrow, matching <c>SstvModeDefinition.LuminanceMinHz</c>), NOT an unconditional
    /// "bare always wins" floor as an earlier round of review wrongly concluded. So a peek-ahead
    /// landing past this line's own end is floored at <c>luminanceMinHz</c>, not simply discarded.
    /// Confirmed unreachable at every currently-registered mode (max <c>m_KSB</c>-to-samples-per-pixel
    /// ratio ~0.625 at PD290) -- pure defense-in-depth, not exercised by any real mode/rate combination
    /// today; covered by a direct synthetic-line unit test, not an integration test, for exactly that
    /// reason.
    ///
    /// Scottie DX never peak-picks at all (<c>Main.cpp:4226-4268</c>'s explicit <c>smSCTDX</c> bare
    /// special-case) -- resolved here, not by any caller, so every decoder can call this method
    /// uniformly for every channel that legacy peak-picks in every OTHER mode.</summary>
    public double ReadPeakPicked(int startSample, int endSample)
    {
        if (_neverPeakPicks)
        {
            return ReadBare(startSample, endSample);
        }

        var peekIndex = startSample + _ksbSamples;
        if (peekIndex >= _lineEndSampleExclusive)
        {
            return Math.Max(ReadBare(startSample, endSample), _luminanceMinHz);
        }

        var bare = ReadBare(startSample, endSample);
        var peek = ReadBare(peekIndex, peekIndex);
        return bare < peek ? peek : bare; // sstv.cpp:4062's *ip < *(ip+m_KSB) -- strict, ties keep bare
    }
}
