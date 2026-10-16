using System.Numerics;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>MSC symbol interleaver, port of CSymbInterleaver (drmtx/common/interleaver/SymbolInterleaver.cpp:41-113):
/// block interleaver with t0 = 5 over N_MUX cells, and cell i read from the frame written i mod D frames ago.
/// Keeps D frames of memory across calls, zero at start. One instance per transmission, not thread-safe.</summary>
internal sealed class SymbolInterleaver
{
    private const int T0 = 5;
    private const int LongDepth = 5;
    private const int ShortDepth = 1;

    private readonly int[] _table;
    private readonly Complex[][] _memory;
    private readonly int[] _curIndex;

    public SymbolInterleaver(int numCells, DrmInterleave interleave)
    {
        _table = BlockInterleaverTable.Make(numCells, T0);
        Depth = interleave == DrmInterleave.Long ? LongDepth : ShortDepth;

        // QSSTV always allocates the long-interleaver memory and index vector (SymbolInterleaver.cpp:86-92).
        _memory = new Complex[LongDepth][];
        for (int i = 0; i < LongDepth; i++)
            _memory[i] = new Complex[numCells];
        _curIndex = new int[LongDepth];
        for (int i = 0; i < LongDepth; i++)
            _curIndex[i] = i;
    }

    /// <summary>Interleaver depth D in frames: 5 for <see cref="DrmInterleave.Long"/>, 1 for Short.</summary>
    public int Depth { get; }

    public int NumCells => _table.Length;

    /// <summary>Interleaves one MLC frame into <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<Complex> input, Span<Complex> output)
    {
        int n = _table.Length;
        if (input.Length != n || output.Length != n)
            throw new ArgumentException($"Expected {n} cells.");

        input.CopyTo(_memory[_curIndex[0]]);
        for (int i = 0; i < n; i++)
            output[i] = _memory[_curIndex[i % Depth]][_table[i]];

        for (int j = 0; j < Depth; j++)
        {
            if (--_curIndex[j] < 0)
                _curIndex[j] = Depth - 1;
        }
    }
}
