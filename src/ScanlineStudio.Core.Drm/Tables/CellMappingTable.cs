using System.Numerics;

namespace ScanlineStudio.Core.Drm.Tables;

/// <summary>Cell type flags, one bit each so a cell can carry several (CellMappingTable.h:44-51).</summary>
[Flags]
internal enum CellFlags
{
    None = 0,
    Dc = 1,
    Msc = 2,
    Sdc = 4,
    Fac = 8,
    TimePilot = 16,
    FreqPilot = 32,
    ScatPilot = 64,
    BoostedPilot = 128,
}

/// <summary>Per robustness mode and spectrum occupancy OFDM cell map: cell types, pilot values and
/// per-symbol cell counts for one super-frame. Port of QSSTV's CCellMappingTable::MakeTable
/// (drmtx/common/ofdmcellmapping/CellMappingTable.cpp:44-601). Immutable once built.</summary>
internal sealed class CellMappingTable
{
    private readonly CellFlags[] _map;
    private readonly Complex[] _pilots;
    private readonly int[] _numMscPerSymbol;
    private readonly int[] _numFacPerSymbol;

    public RobustnessMode Mode { get; }
    public SpectrumOccupancy Occupancy { get; }
    public int NumSymbolsPerSuperframe { get; }
    public int NumSymbolsPerFrame { get; }
    public int NumCarriers { get; }
    public int ScatPilotTimeInterval { get; }
    public int ScatPilotFreqInterval { get; }
    public int MaxNumMscPerSymbol { get; }
    public int FftSize { get; }
    public int CarrierKmin { get; }
    public int CarrierKmax { get; }
    public int IndexDcFreq { get; }
    public int ShiftedKmin { get; }
    public int ShiftedKmax { get; }
    public int GuardRatioEnum { get; }
    public int GuardRatioDenom { get; }
    public int GuardSize { get; }
    public int SymbolBlockSize { get; }
    public int NumInterpolatedFreqPilots { get; }
    public int NumUsefulMscCellsPerFrame { get; }
    public double AveragePowerPerSymbol { get; }
    public double AveragePilotPowerPerSymbol { get; }

    /// <summary>MSC cells per symbol; the last symbol excludes the dummy cells (CellMappingTable.cpp:501).</summary>
    public ReadOnlySpan<int> NumMscPerSymbol => _numMscPerSymbol;

    public ReadOnlySpan<int> NumFacPerSymbol => _numFacPerSymbol;

    /// <summary>Cell flags for a super-frame symbol and a carrier index (carrier - Kmin).</summary>
    public CellFlags Cell(int symbol, int carrierIndex) => _map[(symbol * NumCarriers) + carrierIndex];

    /// <summary>Pilot value for a cell; zero for non-pilot cells.</summary>
    public Complex Pilot(int symbol, int carrierIndex) => _pilots[(symbol * NumCarriers) + carrierIndex];

    public CellMappingTable(RobustnessMode mode, SpectrumOccupancy occupancy)
    {
        if (mode is not (RobustnessMode.A or RobustnessMode.B or RobustnessMode.E))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (occupancy is not (SpectrumOccupancy.So0 or SpectrumOccupancy.So1))
            throw new ArgumentOutOfRangeException(nameof(occupancy));

        Mode = mode;
        Occupancy = occupancy;

        // CellMappingTable.cpp:90-172
        CarrierKmin = CarrierTables.CarrierKmin(mode, occupancy);
        CarrierKmax = CarrierTables.CarrierKmax(mode, occupancy);
        FftSize = DrmGlobal.FftSize(mode);
        (GuardRatioEnum, GuardRatioDenom) = DrmGlobal.GuardRatio(mode);
        NumSymbolsPerFrame = DrmGlobal.SymbolsPerFrame(mode);
        NumSymbolsPerSuperframe = NumSymbolsPerFrame * DrmGlobal.NumFramesInSuperframe;
        ReadOnlySpan<int> fac = CarrierTables.FacPositions(mode);
        ReadOnlySpan<int> timePilots = CarrierTables.TimePilots(mode);
        int numTimePilots = timePilots.Length / 2;
        ReadOnlySpan<int> freqPilots = CarrierTables.FreqPilots(mode);
        ScatPilotTimeInterval = CarrierTables.ScatPilotTimeInterval(mode);
        ScatPilotFreqInterval = CarrierTables.ScatPilotFreqInterval(mode);
        ReadOnlySpan<int> scatConst = CarrierTables.ScatPilotConst(mode);
        int colSizeWz = CarrierTables.ScatPilotWzColumns(mode);
        ReadOnlySpan<int> scatW = CarrierTables.ScatPilotW(mode);
        ReadOnlySpan<int> scatZ = CarrierTables.ScatPilotZ(mode);
        int scatQ = CarrierTables.ScatPilotQ(mode);
        ReadOnlySpan<int> gainTable = CarrierTables.BoostedPilotCarriers(mode, occupancy);

        // CellMappingTable.cpp:175-201
        NumCarriers = CarrierKmax - CarrierKmin + 1;
        GuardSize = FftSize * GuardRatioEnum / GuardRatioDenom;
        SymbolBlockSize = FftSize + GuardSize;
        IndexDcFreq = (int)((double)DrmGlobal.VirtualIntermediateFrequency * FftSize / DrmGlobal.SoundCardSampleRate);
        ShiftedKmin = IndexDcFreq + CarrierKmin;
        ShiftedKmax = IndexDcFreq + CarrierKmax;
        NumInterpolatedFreqPilots = ScatPilotFreqInterval > 1
            ? (int)(((double)NumCarriers / ScatPilotFreqInterval) + 1)
            : NumCarriers;

        int cells = NumSymbolsPerSuperframe * NumCarriers;
        _map = new CellFlags[cells];
        _pilots = new Complex[cells];
        _numMscPerSymbol = new int[NumSymbolsPerSuperframe];
        _numFacPerSymbol = new int[NumSymbolsPerSuperframe];

        // Build table - CellMappingTable.cpp:222-423. Later rules overwrite earlier ones.
        int freqPilotsCounter = 0;
        int timePilotsCounter = 0;
        int facCounter = 0;
        int halfFreqInt = (int)(((double)ScatPilotFreqInterval / 2) + .5);
        for (int sym = 0; sym < NumSymbolsPerSuperframe; sym++)
        {
            int frameSym = sym % NumSymbolsPerFrame;
            if (frameSym == 0)
                facCounter = 0;

            int scatPilotsCounter = (int)((double)(CarrierKmin - halfFreqInt
                - (ScatPilotFreqInterval * Mod(frameSym, ScatPilotTimeInterval)))
                / (ScatPilotFreqInterval * ScatPilotTimeInterval));

            for (int car = CarrierKmin; car < CarrierKmax + 1; car++)
            {
                int idx = (sym * NumCarriers) + car - CarrierKmin;

                _map[idx] = CellFlags.Msc;

                // QSSTV tests facCounter <= NUM_FAC_CELLS (:253) and so reads one pair past the
                // table after the 45th cell; the golden cell map shows that read never matched.
                if (facCounter < CarrierTables.NumFacCells
                    && (fac[facCounter * 2] * NumCarriers) + fac[(facCounter * 2) + 1] == (frameSym * NumCarriers) + car)
                {
                    facCounter++;
                    _map[idx] = CellFlags.Fac;
                }

                // Scattered pilots first: time/frequency pilot phases take precedence (:268-339).
                if (car == halfFreqInt
                    + (ScatPilotFreqInterval * Mod(frameSym, ScatPilotTimeInterval))
                    + (ScatPilotFreqInterval * ScatPilotTimeInterval * scatPilotsCounter))
                {
                    scatPilotsCounter++;
                    _map[idx] = CellFlags.ScatPilot;

                    int x = scatConst[0], y = scatConst[1], k0 = scatConst[2];
                    int n = Mod(frameSym, y);
                    int m = (int)((double)frameSym / y);
                    int p = (int)((double)(car - k0 - (n * x)) / (x * y));
                    int phase = Mod((4 * scatZ[(n * colSizeWz) + m]) + (p * scatW[(n * colSizeWz) + m])
                        + (p * p * (1 + frameSym) * scatQ), 1024);

                    bool boosted = false;
                    for (int i = 0; i < CarrierTables.NumBoostedScatPilots; i++)
                    {
                        if (gainTable[i] == car)
                            boosted = true;
                    }

                    if (boosted)
                    {
                        _pilots[idx] = Polar2Cart(2, phase);
                        _map[idx] |= CellFlags.BoostedPilot;
                    }
                    else
                    {
                        _pilots[idx] = Polar2Cart(Math.Sqrt(2.0), phase);
                    }
                }

                // Time-reference pilots, first symbol of each frame (:344-366).
                if (frameSym == 0 && timePilots[timePilotsCounter * 2] == car)
                {
                    if ((_map[idx] & CellFlags.ScatPilot) != 0)
                        _map[idx] |= CellFlags.TimePilot;
                    else
                        _map[idx] = CellFlags.TimePilot;

                    _pilots[idx] = Polar2Cart(Math.Sqrt(2.0), timePilots[(timePilotsCounter * 2) + 1]);

                    timePilotsCounter = timePilotsCounter == numTimePilots - 1 ? 0 : timePilotsCounter + 1;
                }

                // Frequency-reference pilots, every symbol (:374-408).
                if (freqPilots[freqPilotsCounter * 2] == car)
                {
                    if ((_map[idx] & (CellFlags.TimePilot | CellFlags.ScatPilot)) != 0)
                        _map[idx] |= CellFlags.FreqPilot;
                    else
                        _map[idx] = CellFlags.FreqPilot;

                    // Mode E: first and second frequency pilot are phase-inverted on odd symbols (:389-399).
                    bool specialCase = mode == RobustnessMode.E
                        && freqPilotsCounter != CarrierTables.NumFreqPilots - 1
                        && frameSym % 2 == 1;

                    int fphase = freqPilots[(freqPilotsCounter * 2) + 1];
                    _pilots[idx] = specialCase
                        ? Polar2Cart(Math.Sqrt(2.0), Mod(fphase + 512, 1024))
                        : Polar2Cart(Math.Sqrt(2.0), fphase);

                    freqPilotsCounter = freqPilotsCounter == CarrierTables.NumFreqPilots - 1 ? 0 : freqPilotsCounter + 1;
                }

                // DC carrier and mode A's unused +-1 carriers (:415-421); outside every HamDRM range.
                if (car == 0)
                    _map[idx] = CellFlags.Dc;
                if (mode == RobustnessMode.A && (car == -1 || car == 1))
                    _map[idx] = CellFlags.Dc;
            }
        }

        // Count cells - CellMappingTable.cpp:430-505
        int maxMsc = 0;
        int mscCounter = 0;
        double avPow = 0.0;
        double avPilPow = 0.0;
        for (int sym = 0; sym < NumSymbolsPerSuperframe; sym++)
        {
            for (int c = 0; c < NumCarriers; c++)
            {
                CellFlags f = _map[(sym * NumCarriers) + c];
                if ((f & CellFlags.Msc) != 0)
                {
                    _numMscPerSymbol[sym]++;
                    mscCounter++;
                }

                if ((f & CellFlags.Fac) != 0)
                    _numFacPerSymbol[sym]++;

                if ((f & CellFlags.Dc) == 0)
                {
                    if ((f & (CellFlags.Msc | CellFlags.Sdc | CellFlags.Fac)) != 0)
                    {
                        avPow += 1.0;
                    }
                    else if ((f & CellFlags.BoostedPilot) != 0)
                    {
                        avPow += 4.0;
                        avPilPow += 4.0;
                    }
                    else
                    {
                        avPow += 2.0;
                        avPilPow += 2.0;
                    }
                }
            }

            if (maxMsc < _numMscPerSymbol[sym])
                maxMsc = _numMscPerSymbol[sym];
        }

        MaxNumMscPerSymbol = maxMsc;
        NumUsefulMscCellsPerFrame = mscCounter / DrmGlobal.NumFramesInSuperframe;
        int dummyCells = mscCounter - (NumUsefulMscCellsPerFrame * DrmGlobal.NumFramesInSuperframe);
        _numMscPerSymbol[NumSymbolsPerSuperframe - 1] -= dummyCells;
        AveragePowerPerSymbol = avPow / NumSymbolsPerSuperframe;
        AveragePilotPowerPerSymbol = avPilPow / NumSymbolsPerSuperframe;
    }

    // Phase is normalised to 1024 (CellMappingTable.cpp:603-612).
    private static Complex Polar2Cart(double absolute, int phase) =>
        new(absolute * Math.Cos(2.0 * Math.PI * phase / 1024), absolute * Math.Sin(2.0 * Math.PI * phase / 1024));

    // Integer modulus for negative x (CellMappingTable.cpp:614-621).
    private static int Mod(int x, int y) => x < 0 ? (x % y) + y : x % y;
}
