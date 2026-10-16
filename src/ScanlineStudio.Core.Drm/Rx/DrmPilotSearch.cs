namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's two pilot searches on acquisition symbols: the integer frequency offset from the
/// frequency-reference pilots (<c>getfoffsint</c>, drmrx/getfoffsint.cpp:45-163) and the first symbol of the
/// frame from the time-reference cells (<c>getsymbolidx</c>, drmrx/getsymbolidx.cpp:43-139).</summary>
/// <remarks>Symbols are interleaved complex float, symbol <c>i</c> carrier slot <c>j</c> at
/// <c>(j + i·K_modulo)·2</c>, as <c>symbol_buffer</c>. Precision is float as in QSSTV; <c>cos</c>/<c>atan2</c>
/// on float arguments bind to the float overloads under g++'s <c>&lt;math.h&gt;</c>. Pure functions.</remarks>
internal static class DrmPilotSearch
{
    /// <summary><c>getfoffsint</c> (getfoffsint.cpp:45-163): phase-difference coherence per carrier over
    /// <paramref name="nSymbols"/> symbols, summed at the three frequency pilots (9, 27, 36 scaled to Tu); the
    /// best rotation gives the carrier offset. Returns <c>2π · offset</c> (radians per Tu).</summary>
    public static float IntegerFrequencyOffset(ReadOnlySpan<float> symbolBuffer, int nSymbols, int kDc, int kModulo, int tu)
    {
        Span<float> dsSum = stackalloc float[2 * DrmRxTables.TuMax];
        Span<float> absDsSum = stackalloc float[DrmRxTables.TuMax];
        Span<float> pilotIndicator = stackalloc float[DrmRxTables.TuMax];
        for (int i = 0; i < tu; i++)
        {
            dsSum[i * 2] = 0.0f;
            dsSum[i * 2 + 1] = 0.0f;
        }

        for (int i = 1; i < nSymbols; i++)
        {
            for (int j = 0; j < tu; j++)
            {
                float pRe = symbolBuffer[(j + (i - 1) * kModulo) * 2], pIm = symbolBuffer[(j + (i - 1) * kModulo) * 2 + 1];
                float cRe = symbolBuffer[(j + i * kModulo) * 2], cIm = symbolBuffer[(j + i * kModulo) * 2 + 1];
                float tmp1 = pRe * cRe + pIm * cIm;
                float tmp2 = pIm * cRe - pRe * cIm;
                dsSum[j * 2] += MathF.Cos(MathF.Atan2(tmp2, tmp1));
                dsSum[j * 2 + 1] += MathF.Sin(MathF.Atan2(tmp2, tmp1));
            }
        }

        int kPilot1 = (int)MathF.Ceiling((float)(9 * tu / 288));
        int kPilot2 = (int)MathF.Ceiling((float)(27 * tu / 288));
        int kPilot3 = (int)MathF.Ceiling((float)(36 * tu / 288));
        for (int i = 0; i < tu; i++)
            absDsSum[i] = MathF.Sqrt(dsSum[i * 2] * dsSum[i * 2] + dsSum[i * 2 + 1] * dsSum[i * 2 + 1]);

        for (int i = 0; i < tu - kPilot1; i++)
            pilotIndicator[i] = absDsSum[kPilot1 + i];
        for (int i = 0; i < kPilot1; i++)
            pilotIndicator[i + tu - kPilot1] = absDsSum[i];
        for (int i = 0; i < tu - kPilot2; i++)
            pilotIndicator[i] += absDsSum[kPilot2 + i];
        for (int i = 0; i < kPilot2; i++)
            pilotIndicator[i + tu - kPilot2] += absDsSum[i];
        for (int i = 0; i < tu - kPilot3; i++)
            pilotIndicator[i] += absDsSum[kPilot3 + i];
        for (int i = 0; i < kPilot3; i++)
            pilotIndicator[i + tu - kPilot3] += absDsSum[i];

        float dummy = (float)-1.0E20;
        int kDcOffset = 0;
        for (int i = 0; i < tu; i++)
        {
            if (pilotIndicator[i] > dummy)
            {
                dummy = pilotIndicator[i];
                kDcOffset = i;
            }
        }

        kDcOffset = ((kDcOffset - kDc + tu / 2 + tu) % tu) - tu / 2;
        return (float)(2 * Math.PI * kDcOffset);
    }

    /// <summary><c>getsymbolidx</c> (getsymbolidx.cpp:43-139): for each of the first
    /// <paramref name="symbolsPerFrame"/> symbols, the de-rotated correlation of adjacent time-reference cell
    /// pairs; returns the symbol index with the largest |sum|.</summary>
    public static int FrameStartSymbol(ReadOnlySpan<float> symbolBuffer, int symbolsPerFrame, ReadOnlySpan<int> timeRefCellsK,
        ReadOnlySpan<int> timeRefCellsTheta1024, int kDc, int kModulo, int nTimeRefCells)
    {
        Span<float> sumRealXx = stackalloc float[25];
        for (int i = 0; i < symbolsPerFrame; i++)
            sumRealXx[i] = 0.0f;

        for (int n = 0; n < nTimeRefCells - 1; n++)
        {
            if (timeRefCellsK[n + 1] - timeRefCellsK[n] != 1)
                continue;
            int k1 = kDc + timeRefCellsK[n];
            int k2 = kDc + timeRefCellsK[n + 1];
            float phi1 = (float)((2.0 * Math.PI * timeRefCellsTheta1024[n]) / 1024.0);
            float phi2 = (float)((2.0 * Math.PI * timeRefCellsTheta1024[n + 1]) / 1024.0);
            float c1 = MathF.Cos(phi1), s1 = MathF.Sin(phi1), c2 = MathF.Cos(phi2), s2 = MathF.Sin(phi2);
            for (int j = 0; j < symbolsPerFrame; j++)
            {
                float re1 = symbolBuffer[(k1 + j * kModulo) * 2], im1 = symbolBuffer[(k1 + j * kModulo) * 2 + 1];
                float re2 = symbolBuffer[(k2 + j * kModulo) * 2], im2 = symbolBuffer[(k2 + j * kModulo) * 2 + 1];
                float tmp1Real = re1 * c1 + im1 * s1;
                float tmp1Imag = im1 * c1 - re1 * s1;
                float tmp2Real = re2 * c2 + im2 * s2;
                float tmp2Imag = im2 * c2 - re2 * s2;
                sumRealXx[j] += tmp1Real * tmp2Real + tmp1Imag * tmp2Imag;
            }
        }

        float maxSumXx = (float)-1.0E20;
        int symbol0 = 0;
        for (int j = 0; j < symbolsPerFrame; j++)
        {
            float a = MathF.Abs(sumRealXx[j]);
            if (a > maxSumXx)
            {
                maxSumXx = a;
                symbol0 = j;
            }
        }

        return symbol0;
    }
}
