using System.Numerics;
using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>OFDM cell mapping, port of COFDMCellMapping (drmtx/common/ofdmcellmapping/OFDMCellMapping.cpp:36-166).
/// Maps one OFDM symbol at a time from MSC and FAC cell FIFOs, so a frame's MSC cells need not line up with
/// an MLC frame. FAC block k lands in OFDM frame k (frame id k mod 3); dummy cells fill only the MSC cells
/// the table marks beyond the last symbol's count. One instance per transmission, not thread-safe.</summary>
internal sealed class OfdmCellMapper
{
    // Already normalised; QSSTV writes them as float literals (OFDMCellMapping.cpp:38-46).
    private static readonly Complex[] Dummy16Qam = [new(0.3162277660f, 0.3162277660f), new(0.3162277660f, -0.3162277660f)];
    private static readonly Complex[] Dummy64Qam = [new(0.1543033499f, 0.1543033499f), new(0.1543033499f, -0.1543033499f)];

    private readonly CellMappingTable _table;
    private readonly Complex[] _dummy;
    private readonly CellFifo _msc = new();
    private readonly CellFifo _fac = new();
    private int _symbolCounter;

    public OfdmCellMapper(CellMappingTable table, DrmQam qam)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));

        // Everything but 64-QAM SM uses the 16-QAM dummy cells (OFDMCellMapping.cpp:146-158).
        _dummy = qam == DrmQam.Qam64 ? Dummy64Qam : Dummy16Qam;
    }

    /// <summary>iFrameIDTransm of the next symbol.</summary>
    public int FrameId { get; private set; }

    /// <summary>Symbol index within the frame of the next symbol.</summary>
    public int SymbolInFrame => _symbolCounter;

    public int NumCarriers => _table.NumCarriers;

    public void EnqueueMsc(ReadOnlySpan<Complex> cells) => _msc.Enqueue(cells);

    public void EnqueueFac(ReadOnlySpan<Complex> cells) => _fac.Enqueue(cells);

    /// <summary>Maps the next symbol if both FIFOs hold its cells (CTransmitterModul::ProcessData,
    /// util/Modul.h:281-336); otherwise returns false and consumes nothing.</summary>
    public bool TryMapSymbol(Span<Complex> output)
    {
        if (output.Length != _table.NumCarriers)
            throw new ArgumentException($"Expected {_table.NumCarriers} carriers.", nameof(output));

        int abs = (FrameId * _table.NumSymbolsPerFrame) + _symbolCounter;
        int numMsc = _table.NumMscPerSymbol[abs];
        int numFac = _table.NumFacPerSymbol[abs];
        if (_msc.Count < numMsc || _fac.Count < numFac)
            return false;

        ReadOnlySpan<Complex> msc = _msc.Dequeue(numMsc);
        ReadOnlySpan<Complex> fac = _fac.Dequeue(numFac);
        int mscCounter = 0;
        int facCounter = 0;
        int dummyCounter = 0;
        for (int car = 0; car < output.Length; car++)
        {
            CellFlags flags = _table.Cell(abs, car);
            output[car] = Complex.Zero;
            if ((flags & CellFlags.Msc) != 0)
            {
                output[car] = mscCounter >= numMsc ? _dummy[dummyCounter++] : msc[mscCounter];
                mscCounter++;
            }

            if ((flags & CellFlags.Fac) != 0)
                output[car] = fac[facCounter++];

            if ((flags & CellFlags.Dc) != 0)
                output[car] = Complex.Zero;

            if ((flags & (CellFlags.TimePilot | CellFlags.FreqPilot | CellFlags.ScatPilot)) != 0)
                output[car] = _table.Pilot(abs, car);
        }

        if (++_symbolCounter == _table.NumSymbolsPerFrame)
        {
            _symbolCounter = 0;
            if (++FrameId == DrmGlobal.NumFramesInSuperframe)
                FrameId = 0;
        }

        return true;
    }

    /// <summary>A growable FIFO of cells, the CBuffer between modules.</summary>
    private sealed class CellFifo
    {
        private Complex[] _buffer = new Complex[4096];
        private int _start;
        private int _count;

        public int Count => _count;

        public void Enqueue(ReadOnlySpan<Complex> cells)
        {
            if (_start + _count + cells.Length > _buffer.Length)
            {
                var grown = new Complex[Math.Max(_buffer.Length, 2 * (_count + cells.Length))];
                _buffer.AsSpan(_start, _count).CopyTo(grown);
                _buffer = grown;
                _start = 0;
            }

            cells.CopyTo(_buffer.AsSpan(_start + _count));
            _count += cells.Length;
        }

        /// <summary>The returned span is valid until the next Enqueue.</summary>
        public ReadOnlySpan<Complex> Dequeue(int n)
        {
            var result = new ReadOnlySpan<Complex>(_buffer, _start, n);
            _start += n;
            _count -= n;
            if (_count == 0)
                _start = 0;
            return result;
        }
    }
}
