namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Fixed-capacity circular delay line for streaming FIR convolution, replacing the O(tap)
/// full-array-shift-per-sample (<c>Array.Copy</c>) both <see cref="SearchBandpassFilter"/> and
/// <see cref="HilbertFmDemodulator"/> used previously, with an O(1) write per sample. The dot-product
/// itself stays O(tap) -- unavoidable for FIR convolution -- but every access below walks the buffer
/// in the exact same term order the old linear-array version used, so output is bit-exact with the
/// prior implementation (IEEE-754 addition is not associative -- same terms in the same order is
/// required, not just "the same set of values").
///
/// Logical index 0 always means "the value that pairs with h[0]" -- for
/// <see cref="SearchBandpassFilter"/> (headMovesForward=false, see this class's own constructor) that's
/// the NEWEST sample; for <see cref="HilbertFmDemodulator"/>'s <c>DoFir</c> (headMovesForward=true) that's the
/// OLDEST -- the two filters use opposite addressing conventions (see each class's own doc comment for
/// why the direction is load-bearing, not arbitrary). This class doesn't care which -- <see cref="Push"/>
/// moves the physical head forward or backward per the convention fixed once at construction, and every
/// other access goes through the logical <see cref="this[int]"/> indexer, never a raw physical index.
///
/// This indexer discipline is not a style preference -- a first plan-review round of this change found
/// a real bug it would have caused: <see cref="HilbertFmDemodulator.ProcessSample"/> reads its delay
/// line's center tap OUTSIDE <c>DoFir</c> (the delayed real component paired with the FIR's quadrature
/// output). A naive circular-buffer conversion that left that read as a raw physical-index access would
/// have silently read the wrong tap on every call after the head first moves -- full demodulator
/// corruption, not a small numeric drift, and the kind of bug CLAUDE.md's Scottie-incident precedent
/// warns is easy to miss since round-trip/golden-vector tests can pass against a self-consistent wrong
/// answer. Routing that read through this class's own logical indexer instead makes it correct by
/// construction: the call site's source text doesn't change at all, only the type of the field it reads.
/// </summary>
internal sealed class FirDelayLine
{
    private readonly double[] _buf;
    private readonly bool _headMovesForward;
    private int _head;

    public int Tap { get; }

    /// <param name="headMovesForward">Fixed for the lifetime of the instance -- true for
    /// <see cref="HilbertFmDemodulator"/>'s oldest-first convention (h[0] pairs with the oldest sample;
    /// this port's own PRE-circular-buffer code shifted via <c>Array.Copy(z, 1, z, 0, tap); z[tap] =
    /// input;</c>, toward index 0 -- legacy's real `CFIR2::Do`/`DoFIR` don't shift at all, see
    /// <see cref="HilbertFmDemodulator"/>'s and <see cref="SearchBandpassFilter"/>'s own doc comments for
    /// the actual legacy addressing this convention is derived from), false for
    /// <see cref="SearchBandpassFilter"/>'s newest-first convention (h[0] pairs with the newest sample;
    /// this port's own pre-circular-buffer code shifted via <c>Array.Copy(_z, 0, _z, 1, _tap); _z[0] =
    /// input;</c>, toward higher indices).</param>
    public FirDelayLine(int tap, bool headMovesForward)
    {
        Tap = tap;
        _buf = new double[tap + 1]; // zero-init, matching both prior implementations' own zero-init delay lines
        _headMovesForward = headMovesForward;
        _head = 0;
    }

    /// <summary>Logical index 0..<see cref="Tap"/> -- see class doc comment for what index 0 means for
    /// each convention. NOT a raw physical array index; safe to use from any call site (including a
    /// point read outside the hot convolution loop) without reasoning about the current head
    /// position.</summary>
    public double this[int logicalIndex]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(logicalIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(logicalIndex, Tap);
            var p = _head + logicalIndex;
            var capacity = _buf.Length;
            return _buf[p >= capacity ? p - capacity : p];
        }
    }

    /// <summary>Writes the newest sample and advances the delay line by one, discarding the oldest
    /// sample -- exactly matching the discard behavior of the linear-array version's own
    /// <c>Array.Copy</c> shift. Must be called exactly once per new sample, before the matching
    /// <see cref="Convolve"/> call, mirroring both prior implementations' own shift-then-dot-product
    /// order.</summary>
    public void Push(double input)
    {
        if (_headMovesForward)
        {
            _buf[_head] = input;
            _head++;
            if (_head == _buf.Length)
            {
                _head = 0;
            }
        }
        else
        {
            _head--;
            if (_head < 0)
            {
                _head = _buf.Length - 1;
            }

            _buf[_head] = input;
        }
    }

    /// <summary>Dot product of this delay line's current contents against <paramref name="h"/>,
    /// walking a physical pointer forward with a wrap branch rather than a per-iteration modulo (a
    /// runtime <c>idiv</c>, since capacity isn't a compile-time constant here, would risk costing more
    /// than the eliminated <c>Array.Copy</c> at large tap counts -- plan-review round 1 finding). The
    /// walking pointer visits every physical slot exactly once (the loop runs <c>Tap+1</c> times, equal
    /// to capacity), in the same order <c>(head+i) % capacity</c> would produce for <c>i</c> from 0 to
    /// <see cref="Tap"/> -- so the summation order, and therefore the result, is bit-exact with that
    /// form and with the original linear-array loop.</summary>
    public double Convolve(double[] h)
    {
        var sum = 0.0;
        var p = _head;
        var capacity = _buf.Length;
        var tap = Tap;
        for (var i = 0; i <= tap; i++)
        {
            sum += _buf[p] * h[i];
            p++;
            if (p == capacity)
            {
                p = 0;
            }
        }

        return sum;
    }
}
