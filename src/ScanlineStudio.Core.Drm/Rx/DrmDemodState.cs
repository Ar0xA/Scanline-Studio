namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The demodulator state that acquisition (<see cref="DrmDemodulator"/>, P5b) hands to the frame
/// stage (<see cref="IDrmFrameStage"/>, P5c): QSSTV's <c>demodulator</c> members and the drm.cpp globals
/// they touch (drmrx/demodulator.h:31-115, drmrx/drm.cpp:27-111). One instance per receiver; fields are
/// the QSSTV variables under C# names so P5c can port channelEstimation against them directly.</summary>
/// <remarks>
/// <para>Ownership: acquisition writes the mode, time and frequency fields. Both stages read and consume
/// <see cref="RsBuffer"/>/<see cref="RsBufWidx"/> and <see cref="SymbolBuffer"/>/<see cref="SymBufWidx"/>
/// and share <see cref="Zi"/>, exactly as QSSTV's member functions do. The frame stage requests a full
/// re-acquisition with <see cref="DoSynchronize"/>; the next <c>demodulate</c> call honours it.</para>
/// <para>Not thread-safe: owned by one receive worker.</para>
/// </remarks>
internal sealed class DrmDemodState
{
    /// <summary><c>rs_buffer[10*8000]</c> (demodulator.h:50): interleaved complex float samples.</summary>
    public readonly float[] RsBuffer = new float[10 * 8000];

    /// <summary><c>symbol_buffer[Tu_A*2*26]</c> (demodulator.h:71): interleaved complex float symbols, K_modulo apart.</summary>
    public readonly float[] SymbolBuffer = new float[DrmRxTables.TuMax * 2 * 26];

    /// <summary><c>Zi[6]</c> (demodulator.h:69): the getofdm/getofdmsync tracking state; <c>Zi[0] &lt; 0</c> restarts it.</summary>
    public readonly float[] Zi = new float[6];

    /// <summary><c>time_ref_cells_k[21]</c> (demodulator.h:72) for the detected mode.</summary>
    public readonly int[] TimeRefCellsK = new int[DrmRxTables.TimeRefCellColumns];

    /// <summary><c>time_ref_cells_theta_1024[21]</c> (demodulator.h:73) for the detected mode.</summary>
    public readonly int[] TimeRefCellsTheta1024 = new int[DrmRxTables.TimeRefCellColumns];

    /// <summary><c>rsbufwidx</c>: number of valid complex samples in <see cref="RsBuffer"/>.</summary>
    public int RsBufWidx;

    /// <summary><c>symbufwidx</c>: number of symbols held in <see cref="SymbolBuffer"/>.</summary>
    public int SymBufWidx;

    /// <summary>Global <c>input_samples_buffer_request</c> (drm.cpp:27). drmRx::run makes an extra empty
    /// demodulate call when it is 0 (drmrx.cpp:60-63).</summary>
    public int InputSamplesBufferRequest;

    /// <summary><c>N_samples_needed</c>.</summary>
    public int NSamplesNeeded;

    /// <summary><c>N_symbols_needed</c>.</summary>
    public int NSymbolsNeeded;

    /// <summary><c>doSynchronize</c>: set by the frame stage to force <c>init()</c> on the next call.</summary>
    public bool DoSynchronize;

    public bool TimeSyncFlag;
    public bool FrequencySyncFlag;
    public bool FrameSyncFlag;

    /// <summary><c>iterationCounter</c>: demodulate calls that carried samples.</summary>
    public int IterationCounter;

    /// <summary><c>counter</c>: successful time syncs since the last init().</summary>
    public int Counter;

    /// <summary><c>symbol_counter</c>: symbols equalized since frame sync (frame stage increments it).</summary>
    public int SymbolCounter;

    /// <summary>Global <c>robustness_mode</c>: 0/1/2 = A/B/E, 99 after a failed detection; never reset by init().</summary>
    public int RobustnessMode;

    /// <summary>Global <c>spectrum_occupancy_estimation</c>; keeps its last value when no occupancy scores above 0.</summary>
    public int SpectrumOccupancyEstimation;

    /// <summary>Global <c>spectrum_occupancy</c>: −1 (unknown) after each time-sync attempt; set by later stages.</summary>
    public int SpectrumOccupancy;

    /// <summary>Global <c>symbols_per_frame</c>.</summary>
    public int SymbolsPerFrame;

    /// <summary>Globals <c>K_dc</c> = Tu/2 and <c>K_modulo</c> = Tu.</summary>
    public int KDc;

    public int KModulo;

    public int Ts;
    public int Tu;
    public int Tg;
    public int Tgh;
    public int Y;
    public int SymbolsPer2DWindow;
    public int SymbolsToDelay;

    /// <summary><c>time_offset</c>: getmode's fractional symbol start.</summary>
    public float TimeOffset;

    public int TimeOffsetInteger;

    /// <summary>Global <c>samplerate_offset_estimation</c>.</summary>
    public float SamplerateOffsetEstimation;

    /// <summary>Global <c>samplerate_offset</c>; copied from the estimate on every timeSync call.</summary>
    public float SamplerateOffset;

    public float FrequencyOffsetFractionalInit;

    /// <summary><c>freq_offset_integer</c>: 2π × the integer carrier offset.</summary>
    public float FreqOffsetInteger;

    /// <summary><c>freq_offset_init</c>: the frequency offset the frame stage's first symbol starts from.</summary>
    public float FreqOffsetInit;

    public float TimeOffsetFractionalInit;

    public float DeltaTimeOffsetIInit;

    public int DeltaTimeOffsetInteger;
}
