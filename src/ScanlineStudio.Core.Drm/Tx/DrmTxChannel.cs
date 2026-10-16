using System.Numerics;
using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Observes the TX channel stages, for golden tests. Callbacks run synchronously on the caller of
/// <see cref="DrmTxChannel.Modulate"/>; spans are only valid during the call.</summary>
internal interface IDrmTxChannelProbe
{
    void OnMscFrame(MlcFrame frame);

    void OnSymbolInterleaved(ReadOnlySpan<Complex> cells);

    void OnCellSymbol(ReadOnlySpan<Complex> carriers);
}

/// <summary>The TX channel side of CDRMTransmitter::Run (drmtx/common/CDrmTransmitter.cpp:51-86): MSC MLC and
/// symbol interleaver, FAC MLC, cell mapping, OFDM modulation and the int16 output, from source frames to
/// 48 kHz int16 frames of 400 ms. Only complete DRM frames are emitted: CTransmitData writes per frame
/// (DRMSignalIO.cpp:89-95), so the source's final, FAC-less frame only feeds the cell FIFO and never goes on
/// air. One instance per transmission, not thread-safe.</summary>
internal sealed class DrmTxChannel
{
    private readonly CellMappingTable _table;
    private readonly MlcEncoder _mscMlc;
    private readonly MlcEncoder _facMlc;
    private readonly SymbolInterleaver _symbolInterleaver;
    private readonly OfdmCellMapper _mapper;
    private readonly OfdmModulator _modulator;
    private readonly IDrmTxChannelProbe? _probe;
    private bool _started;

    public DrmTxChannel(DrmTxSettings settings, IDrmTxChannelProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _table = new CellMappingTable(settings.Mode, settings.Occupancy);
        _mscMlc = MlcEncoder.ForMsc(_table, settings.Qam, settings.Protection);
        _facMlc = MlcEncoder.ForFac();
        _symbolInterleaver = new SymbolInterleaver(_table.NumUsefulMscCellsPerFrame, settings.Interleave);
        _mapper = new OfdmCellMapper(_table, settings.Qam);
        _modulator = new OfdmModulator(_table);
        _probe = probe;
    }

    /// <summary>Samples per 400 ms frame at 48 kHz (19200 for every mode).</summary>
    public int SamplesPerFrame => _table.SymbolBlockSize * _table.NumSymbolsPerFrame;

    /// <summary>iNumDecodedBitsMSC: the MSC bits each source frame must carry.</summary>
    public int NumMscBits => _mscMlc.NumInBits;

    /// <summary>Runs source frames through the channel, yielding one int16 DRM frame per on-air frame.</summary>
    public IEnumerable<short[]> Modulate(IEnumerable<DrmSourceFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (_started)
            throw new InvalidOperationException("A channel runs one transmission only.");
        _started = true;
        return Run(frames);
    }

    /// <summary>The float playback form of an int16 frame: sample / 32768.</summary>
    public static float[] ToFloat(ReadOnlySpan<short> frame)
    {
        var result = new float[frame.Length];
        DrmTxSampleConverter.ToFloat(frame, result);
        return result;
    }

    private IEnumerable<short[]> Run(IEnumerable<DrmSourceFrame> frames)
    {
        var interleaved = new Complex[_table.NumUsefulMscCellsPerFrame];
        var carriers = new Complex[_table.NumCarriers];
        var symbol = new Complex[_table.SymbolBlockSize];
        short[] pcm = new short[SamplesPerFrame];
        int symbolsInFrame = 0;

        foreach (DrmSourceFrame frame in frames)
        {
            MlcFrame msc = _mscMlc.Encode(frame.MscBits);
            _probe?.OnMscFrame(msc);
            _symbolInterleaver.Process(msc.Cells, interleaved);
            _probe?.OnSymbolInterleaved(interleaved);
            _mapper.EnqueueMsc(interleaved);
            if (frame.FacBits is not null)
                _mapper.EnqueueFac(_facMlc.Encode(frame.FacBits).Cells);

            while (_mapper.TryMapSymbol(carriers))
            {
                _probe?.OnCellSymbol(carriers);
                _modulator.Modulate(carriers, symbol);
                DrmTxSampleConverter.ToInt16(symbol, pcm.AsSpan(symbolsInFrame * symbol.Length, symbol.Length));
                if (++symbolsInFrame == _table.NumSymbolsPerFrame)
                {
                    yield return pcm;
                    pcm = new short[SamplesPerFrame];
                    symbolsInFrame = 0;
                }
            }

            // QSSTV's run loop stops in the iteration that raises the stop (CDrmTransmitter.cpp:80-84).
            if (frame.FacBits is null)
                yield break;
        }
    }
}
