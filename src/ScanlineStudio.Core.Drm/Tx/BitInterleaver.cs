using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>MLC bit interleaver, port of CBitInterleaver (drmtx/common/mlc/BitInterleaver.cpp:39-87):
/// two block interleavers, over the part-A and the part-B bits. Stateless between frames.</summary>
internal sealed class BitInterleaver
{
    private readonly int[]? _table1;
    private readonly int[] _table2;
    private readonly byte[] _memory;

    /// <param name="bitsPart1">x_in1, may be 0.</param>
    /// <param name="bitsPart2">x_in2, always &gt; 0.</param>
    /// <param name="t0">13 or 21 (MLC.cpp:180-184).</param>
    public BitInterleaver(int bitsPart1, int bitsPart2, int t0)
    {
        if (bitsPart1 > 0)
            _table1 = BlockInterleaverTable.Make(bitsPart1, t0);
        _table2 = BlockInterleaverTable.Make(bitsPart2, t0);
        _memory = new byte[Math.Max(bitsPart1, bitsPart2)];
    }

    /// <summary>CBitInterleaver::Interleave, in place.</summary>
    public void Interleave(Span<byte> data)
    {
        int n1 = _table1?.Length ?? 0;
        if (_table1 is not null)
        {
            for (int i = 0; i < n1; i++)
                _memory[i] = data[_table1[i]];
            _memory.AsSpan(0, n1).CopyTo(data);
        }

        for (int i = 0; i < _table2.Length; i++)
            _memory[i] = data[_table2[i] + n1];
        _memory.AsSpan(0, _table2.Length).CopyTo(data[n1..]);
    }
}
