using System.Runtime.InteropServices;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5a: the 48 kHz decimator and the Hilbert stage against QSSTV goldens (full streams).</summary>
public sealed class RxFrontEndGoldenTests(ITestOutputHelper output)
{
    public static readonly TheoryData<string> StripeCases = new() { "A1_q4_high_long", "B1_q16_high_long", "E0_q4_high_long" };

    // Fixtures README: a double port is held to |Δ| ≤ 1e-9 int16 units.
    private const double StripeTolerance = 1e-9;

    private const int Block48k = 4096;
    private const int TailZeroBlocks = 8;

    [Theory]
    [MemberData(nameof(StripeCases))]
    public void Decimator48k_FullStream_MatchesQsstvStripes(string caseId)
    {
        double[] golden = LoadStripes(caseId);
        double[] ours = RunFrontEnd48k(caseId);

        Assert.Equal(golden.Length, ours.Length);
        double max = MaxAbsDiff(golden, ours);
        output.WriteLine($"{caseId}: {golden.Length / 1024} stripes, max |Δ| = {max:E3}");
        Assert.True(max <= StripeTolerance, $"max |Δ| {max:E3} > {StripeTolerance:E0}");
    }

    /// <summary>Tolerance: per float32 output, |Δ| ≤ one float32 ulp of the golden value. Both sides
    /// accumulate the same 153 double products in the same order and round once to float32, so equal
    /// inputs give equal bits; one ulp allows a last-bit double difference to flip that rounding.</summary>
    [Theory]
    [MemberData(nameof(StripeCases))]
    public void Hilbert_OnGoldenStripes_MatchesQsstvFullStream(string caseId)
    {
        double[] stripes = LoadStripes(caseId);
        float[] goldenRe = LoadF32($"rx_hilbert/{caseId}", "hilbert_re.f32.gz");
        float[] goldenHead = LoadF32($"rx_hilbert/{caseId}", "hilbert_head.f32.gz");
        Assert.Equal(stripes.Length, goldenRe.Length);

        float[] iq = RunHilbert(stripes);

        var re = new float[stripes.Length];
        var im = new float[stripes.Length];
        for (int k = 0; k < stripes.Length; k++)
        {
            re[k] = iq[2 * k];
            im[k] = iq[2 * k + 1];
        }

        // [2k+1] is float32 of the input delayed by 76 (filter.cpp:309), checked over the full stream.
        var expectedIm = new float[stripes.Length];
        for (int k = DrmHilbertFilter.Delay; k < stripes.Length; k++)
            expectedIm[k] = (float)stripes[k - DrmHilbertFilter.Delay];

        int ulpRe = MaxUlpDiff(goldenRe, re);
        int ulpIm = MaxUlpDiff(expectedIm, im);
        int ulpHead = MaxUlpDiff(goldenHead, iq.AsSpan(0, goldenHead.Length));
        output.WriteLine($"{caseId}: max ulp re={ulpRe} im={ulpIm} head={ulpHead}, max |Δre| = {MaxAbsDiff(goldenRe, re):E3}");
        Assert.True(ulpRe <= 1, $"re: {ulpRe} ulp");
        Assert.True(ulpIm <= 1, $"im: {ulpIm} ulp");
        Assert.True(ulpHead <= 1, $"head: {ulpHead} ulp");
    }

    /// <summary>The composed chain (our decimator, then our Hilbert) against the QSSTV Hilbert output.</summary>
    [Theory]
    [MemberData(nameof(StripeCases))]
    public void FrontEndThenHilbert_MatchesQsstvHilbert(string caseId)
    {
        float[] goldenRe = LoadF32($"rx_hilbert/{caseId}", "hilbert_re.f32.gz");
        float[] iq = RunHilbert(RunFrontEnd48k(caseId));
        var re = new float[goldenRe.Length];
        for (int k = 0; k < re.Length; k++)
            re[k] = iq[2 * k];
        int ulp = MaxUlpDiff(goldenRe, re);
        output.WriteLine($"{caseId}: chain max ulp = {ulp}");
        Assert.True(ulp <= 1, $"{ulp} ulp");
    }

    [Fact]
    public void HilbertFixtures_ManifestsMatchStripeCounts()
    {
        foreach (string caseId in new[] { "A1_q4_high_long", "B1_q16_high_long", "E0_q4_high_long" })
        {
            using JsonDocument m = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(GoldenFixtures.GoldenDir, "rx_hilbert", caseId, "manifest.json")));
            Assert.Equal(LoadStripes(caseId).Length / 1024, m.RootElement.GetProperty("stripes").GetInt32());
            Assert.Equal(DrmHilbertFilter.Delay, m.RootElement.GetProperty("delay").GetInt32());
        }
    }

    internal static double[] RunFrontEnd48k(string caseId)
    {
        short[] pcm = LoadOut48k(caseId);
        // Harness: final partial block zero-padded, then 8 zero blocks (fixtures README, RX section).
        int blocks = (pcm.Length + Block48k - 1) / Block48k + TailZeroBlocks;
        var input = new float[blocks * Block48k];
        for (int i = 0; i < pcm.Length; i++)
            input[i] = pcm[i] / 32768f;

        var fe = new DrmRxFrontEnd(48000);
        Assert.Equal(DrmRxFrontEndPath.Decimate48k, fe.Path);
        var stripes = new List<double>(blocks * 1024);
        fe.Push(input, s => stripes.AddRange(s.ToArray()));
        return stripes.ToArray();
    }

    internal static short[] LoadOut48k(string caseId) =>
        MemoryMarshal.Cast<byte, short>(GoldenFixtures.ReadStored(caseId, "out48k.bin.gz")).ToArray();

    internal static double[] LoadStripes(string caseId) =>
        MemoryMarshal.Cast<byte, double>(GoldenFixtures.ReadStored(caseId, "rx/rx_stripes_12k.bin.gz")).ToArray();

    private static float[] LoadF32(string dir, string file) =>
        MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, file)).ToArray();

    private static float[] RunHilbert(double[] stripes)
    {
        var h = new DrmHilbertFilter();
        var iq = new float[2 * stripes.Length];
        for (int s = 0; s < stripes.Length; s += 1024)
            h.Process(stripes.AsSpan(s, 1024), iq.AsSpan(2 * s, 2048));
        return iq;
    }

    private static double MaxAbsDiff(double[] a, double[] b)
    {
        double max = 0;
        for (int i = 0; i < a.Length; i++)
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    private static double MaxAbsDiff(float[] a, float[] b)
    {
        double max = 0;
        for (int i = 0; i < a.Length; i++)
            max = Math.Max(max, Math.Abs((double)a[i] - b[i]));
        return max;
    }

    private static int MaxUlpDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
                continue;
            int ia = BitConverter.SingleToInt32Bits(a[i]);
            int ib = BitConverter.SingleToInt32Bits(b[i]);
            if ((ia < 0) != (ib < 0))
                return int.MaxValue;
            max = Math.Max(max, Math.Abs(ia - ib));
        }

        return max;
    }
}
