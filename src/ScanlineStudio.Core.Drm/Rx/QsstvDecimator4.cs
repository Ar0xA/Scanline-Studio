namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's 48 kHz to 12 kHz capture decimator, <c>downsampleFilter</c> constructed with
/// <c>scaled=true</c> (sound/soundbase.cpp:42; dsp/downsamplefilter.cpp:48-185, taps 194-376). Double precision, as
/// <c>FILTERPARAMTYPE</c> is double (appdefs.h:15). Input is int16 scale. The volume meter of
/// <c>downSample4</c> (downsamplefilter.cpp:171-182) is not ported: it is unused on the DRM path (rxfunctions.cpp:76).</summary>
/// <remarks>Streaming: any input length is accepted; a remainder of fewer than 4 samples is kept for
/// the next call, so block boundaries do not change the output.</remarks>
internal sealed class QsstvDecimator4
{
    /// <summary>DSAMPLEFILTERLEN (downsamplefilter.h:26).</summary>
    public const int TapCount = 180;

    /// <summary>The decimation factor, SUBSAMPLINGFACTOR (appdefs.h:10).</summary>
    public const int Factor = 4;

    private readonly double[] _taps = new double[TapCount];
    private readonly double[] _history = new double[TapCount];
    private readonly double[] _pending = new double[Factor];
    private int _pendingCount;

    public QsstvDecimator4()
    {
        // setFilterParams(scaled=true): gain summed in index order, then each tap divided (downsamplefilter.cpp:124-136).
        double gain = 0;
        for (int i = 0; i < TapCount; i++)
            gain += DownSampleFilterParam[i];
        for (int i = 0; i < TapCount; i++)
            _taps[i] = DownSampleFilterParam[i] / gain;
    }

    /// <summary>The normalized taps, index 0 multiplying the newest sample.</summary>
    internal ReadOnlySpan<double> Taps => _taps;

    /// <summary>Clears the filter history, as <c>downsampleFilter::init</c> (downsamplefilter.cpp:84-106).</summary>
    public void Reset()
    {
        Array.Clear(_history);
        Array.Clear(_pending);
        _pendingCount = 0;
    }

    /// <summary>The most outputs <see cref="Process"/> can write for <paramref name="inputCount"/> samples.</summary>
    public static int MaxOutput(int inputCount) => (inputCount + Factor - 1) / Factor;

    /// <summary>Filters and decimates int16-scale samples; returns the number of outputs written.</summary>
    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        int produced = 0;
        int i = 0;
        while (i < input.Length)
        {
            _pending[_pendingCount++] = input[i++];
            if (_pendingCount < Factor)
                continue;
            _pendingCount = 0;
            output[produced++] = Step(_pending[0], _pending[1], _pending[2], _pending[3]);
        }

        return produced;
    }

    // One pass of the downSample4 loop body (downsamplefilter.cpp:153-171): four interleaved accumulators, summed in order.
    private double Step(double d0, double d1, double d2, double d3)
    {
        double[] s = _history;
        s.AsSpan(0, TapCount - Factor).CopyTo(s.AsSpan(Factor));
        s[3] = d0;
        s[2] = d1;
        s[1] = d2;
        s[0] = d3;
        double res0 = 0, res1 = 0, res2 = 0, res3 = 0;
        double[] c = _taps;
        for (int i = 0; i < TapCount; i += 4)
        {
            res0 += s[i] * c[i];
            res1 += s[i + 1] * c[i + 1];
            res2 += s[i + 2] * c[i + 2];
            res3 += s[i + 3] * c[i + 3];
        }

        return res0 + res1 + res2 + res3;
    }

    /// <summary><c>downSampleFilterParam</c> (downsamplefilter.cpp:194-376): 180-tap rFIR, 48 kHz, pass 0-2800 Hz,
    /// stop 3500-24000 Hz, windowed. Copied verbatim, including the unsymmetric last tap.</summary>
    private static ReadOnlySpan<double> DownSampleFilterParam =>
    [
        -3.7745373873035e-5, 1.2821993095221e-5, 2.311774607022e-5, 3.93416585698818e-5, 5.96630768741091e-5, 8.15536238549509e-5,
        0.000101722709767542, 0.000116276271064301, 0.000121134249908642, 0.000112667967400144, 8.85061965447603e-5, 4.83892511055542e-5,
        -5.12050973476878e-6, -6.63000241955111e-5, -0.000126498754490435, -0.000175019484683051, -0.000200634430912931, -0.000193612975094613,
        -0.000147565501362106, -6.29787053004485e-5, 5.3151532907392e-5, 0.000185663591404659, 0.000312869274430059, 0.000409285568879863,
        0.000449756444030134, 0.000414496951656379, 0.000294105750555632, 9.35912229123051e-5, -0.000165601564791902, -0.00044640802132058,
        -0.000700237166667507, -0.000874165824697474, -0.000920325965202881, -0.000806050005626634, -0.000522886928400522, -9.25367983951586e-5,
        0.000431289193698061, 0.000968555754911369, 0.00142243885570497, 0.00169530324453739, 0.00170682284447917, 0.00141183031333547,
        0.000814531791130871, -2.45069535733128e-5, -0.00099078573814898, -0.00192873057602086, -0.00266417331498039, -0.0030336497628345,
        -0.00291602413460463, -0.00226082329124683, -0.00110762621769599, 0.000408348452134145, 0.00206727405426683, 0.00359246937521952,
        0.00469322329756861, 0.00511431509758501, 0.00468722876609756, 0.00337177582297895, 0.00128034801536449, -0.00132164430054919,
        -0.00404279527077636, -0.00641821805936274, -0.00798175134076984, -0.00834762501082702, -0.00728844360368625, -0.00479554097560457,
        -0.00110917359991169, 0.00329009088110941, 0.00773195237700219, 0.0114452598882855, 0.0136749644542385, 0.0138115533747118,
        0.0115118605606838, 0.00679329350542433, 8.1748290004832e-5, -0.0077978972841613, -0.0156927727462633, -0.0222502345991312,
        -0.026088061124922, -0.0259857248583191, -0.0210702338547245, -0.0109692249861873, 0.00409269475786721, 0.0232714919148808,
        0.0451623194183613, 0.0679352326825397, 0.0895327926436653, 0.107905219007286, 0.121252681749066, 0.128250977806601,
        0.128241891841668, 0.121183971401713, 0.107782931581642, 0.089380577086183, 0.0677811957851725, 0.0450342640538684,
        0.0231922642804605, 0.00407642705530391, -0.0109193514192932, -0.0209623465780798, -0.0258377065514141, -0.0259243772593719,
        -0.0220977075791061, -0.0155760373514757, -0.00773531334234096, 8.10439299597243e-5, 0.00673072404465227, 0.0113989423580145,
        0.0136677521637495, 0.0135242773076766, 0.0113121310281208, 0.00763723779722783, 0.00324773606385114, -0.00109419621738977,
        -0.00472773521304448, -0.00718070408259266, -0.00821880222557047, -0.00785332662004067, -0.0063106795715127, -0.00397233394884528,
        -0.00129770774231779, 0.00125627414397684, 0.00330601442656137, 0.00459247977024611, 0.00500724473373418, 0.0045915336446142,
        0.0035119604617914, 0.00201938511639171, 0.000398575790043235, -0.0010802549654419, -0.00220316299747724, -0.00283930339342042,
        -0.00295134721483293, -0.0025896723210505, -0.00187315829857408, -0.000961382358312621, -2.37581136811773e-5, 0.000788913665704397,
        0.00136614000435156, 0.00165000316065111, 0.00163726749497039, 0.00137237896324327, 0.00093352423400739, 0.000415262312175493,
        -8.90049238507674e-5, -0.000502395727301656, -0.0007736279266968, -0.000882343815877056, -0.00083716454123059, -0.00066985195239054,
        -0.000426559350805018, -0.000158060915641661, 8.92295205720884e-5, 0.000280087438665955, 0.000394307002607022, 0.000427389303447503,
        0.000388526242968403, 0.000296704742725585, 0.000175906711695273, 5.03152557592893e-5, -5.95729463836399e-5, -0.000139497224417446,
        -0.000182939261691597, -0.00018951796313575, -0.00016531066549151, -0.000119504934298439, -6.26663462287718e-5, -4.84410009996915e-6,
        4.58362969798013e-5, 8.39847177922791e-5, 0.000107157181539094, 0.000115539629094478, 0.00011129193114192, 9.77629608902248e-5,
        7.87516808137608e-5, 5.79226786300601e-5, 3.84207370527109e-5, 2.27214590093519e-5, 1.26877050578184e-5, -0.000470151901610343
    ];
}
