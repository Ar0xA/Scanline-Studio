using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Pins <see cref="NotchFilter.ProcessSample"/> and <see cref="TxOutputBandpassFilter.ProcessSample"/>
/// bit-for-bit to a linear delay line shifted with <c>Array.Copy</c> on every sample (newest sample at
/// index 0, paired with <c>h[0]</c>), compared with <see cref="BitConverter.DoubleToInt64Bits"/> on
/// every output. The references take their coefficients from the production <c>MakeFilter</c>, so this
/// test isolates delay-line addressing; <see cref="FirDesignBitIdentityTests"/> pins the coefficients.
/// Every signal runs for more than 3x the delay-line capacity, so a ring buffer wraps at least 3 times.
/// The notch cases retune mid-stream in and out of the 1050-2350Hz passband; the reference retunes at
/// the same sample index, recomputes its coefficients and keeps its delay line, as legacy's
/// <c>CNotch::SetNotchFreq</c> does (`fir.cpp:279-292`).
/// </summary>
public class NotchAndTxBpfCircularBufferParityTests
{
    private const int NoiseSeed = 1234;

    private static readonly int[] NamedSampleRates =
        [SstvSampleRate.Minimum, 6000, 8000, 11025, 22050, 44100, 48000, SstvSampleRate.Maximum];

    private static readonly int[] TxTaps = [2, 24, 512];

    // Initial centre 2400Hz is outside the passband; the retunes go in, out, in, out.
    private const double InitialNotchHz = 2400.0;
    private static readonly double[] NotchRetunesHz = [1500.0, 1000.0, 2350.0, 3000.0];

    public enum Signal
    {
        Impulse,
        Step,
        Noise,
    }

    private sealed class LinearNotchReference
    {
        private readonly int _tap;
        private readonly double _sampleRate;
        private readonly double[] _z;
        private double[] _h = [];

        public LinearNotchReference(int sampleRate, int tap, double initialHz)
        {
            _tap = tap;
            _sampleRate = sampleRate;
            _z = new double[tap + 1];
            Retune(initialHz);
        }

        // CNotch::SetNotchFreq's bandwidth rule (fir.cpp:279-292).
        public void Retune(double hz)
        {
            var halfBandwidth = hz is < 1050.0 or > 2350.0 ? 30.0 : 15.0;
            _h = NotchFilter.MakeFilter(_tap, _sampleRate, fcl: hz - halfBandwidth, fch: hz + halfBandwidth);
        }

        public double ProcessSample(double input) => LinearConvolve(_z, _h, _tap, input);
    }

    private sealed class LinearTxReference(double sampleRate, int tap)
    {
        private readonly double[] _h = TxOutputBandpassFilter.MakeFilter(sampleRate, tap);
        private readonly double[] _z = new double[tap + 1];

        public double ProcessSample(double input) => LinearConvolve(_z, _h, tap, input);
    }

    private static double LinearConvolve(double[] z, double[] h, int tap, double input)
    {
        Array.Copy(z, 0, z, 1, tap);
        z[0] = input;

        var sum = 0.0;
        for (var i = 0; i <= tap; i++)
        {
            sum += z[i] * h[i];
        }

        return sum;
    }

    private static double[] MakeSignal(Signal signal, int length)
    {
        var samples = new double[length];
        var random = new Random(NoiseSeed);
        for (var i = 0; i < length; i++)
        {
            samples[i] = signal switch
            {
                Signal.Impulse => i == 0 ? 1.0 : 0.0,
                Signal.Step => i >= 5 ? 1.0 : 0.0,
                Signal.Noise => (random.NextDouble() - 0.5) * 20000.0,
                _ => throw new ArgumentOutOfRangeException(nameof(signal)),
            };
        }

        return samples;
    }

    /// <summary>The lowest rate in the supported range for every distinct notch tap, plus the named rates.</summary>
    public static TheoryData<int> NotchRates()
    {
        var rates = new SortedSet<int>(NamedSampleRates);
        var seenTaps = new HashSet<int>();
        for (var rate = SstvSampleRate.Minimum; rate <= SstvSampleRate.Maximum; rate++)
        {
            if (seenTaps.Add(new NotchFilter(rate).Tap))
            {
                rates.Add(rate);
            }
        }

        var data = new TheoryData<int>();
        foreach (var rate in rates)
        {
            data.Add(rate);
        }

        return data;
    }

    public static TheoryData<int, int> TxCases()
    {
        var data = new TheoryData<int, int>();
        foreach (var rate in new[] { 6000, 8000, 11025, 22050, 44100, 48000 })
        {
            foreach (var tap in TxTaps)
            {
                data.Add(rate, tap);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NotchRates))]
    public void Notch_MatchesLinearReference_BitForBit_AcrossWrapsAndRetunes(int sampleRate)
    {
        foreach (var signal in Enum.GetValues<Signal>())
        {
            var candidate = new NotchFilter(sampleRate, InitialNotchHz);
            var tap = candidate.Tap;
            var reference = new LinearNotchReference(sampleRate, tap, InitialNotchHz);

            var capacity = tap + 1;
            var sampleCount = (3 * capacity) + 7;

            // First retune lands while the impulse is still inside the delay line; none is a multiple of capacity.
            int[] retuneAt = [(tap / 2) + 1, capacity + 3, (2 * capacity) + 5, (3 * capacity) + 1];
            var input = MakeSignal(signal, sampleCount);
            var nextRetune = 0;

            for (var i = 0; i < sampleCount; i++)
            {
                if (nextRetune < retuneAt.Length && i == retuneAt[nextRetune])
                {
                    candidate.SetFrequency(NotchRetunesHz[nextRetune]);
                    reference.Retune(NotchRetunesHz[nextRetune]);
                    nextRetune++;
                }

                AssertBitEqual(reference.ProcessSample(input[i]), candidate.ProcessSample(input[i]), signal, i);
            }

            Assert.Equal(retuneAt.Length, nextRetune);
        }
    }

    [Theory]
    [MemberData(nameof(TxCases))]
    public void TxBpf_MatchesLinearReference_BitForBit_AcrossWraps(int sampleRate, int tap)
    {
        foreach (var signal in Enum.GetValues<Signal>())
        {
            var candidate = new TxOutputBandpassFilter(sampleRate, tap);
            var reference = new LinearTxReference(sampleRate, tap);

            var sampleCount = (3 * (tap + 1)) + 7;
            var input = MakeSignal(signal, sampleCount);

            for (var i = 0; i < sampleCount; i++)
            {
                AssertBitEqual(reference.ProcessSample(input[i]), candidate.ProcessSample(input[i]), signal, i);
            }
        }
    }

    private static void AssertBitEqual(double expected, double actual, Signal signal, int index)
    {
        Assert.True(
            BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{signal} sample {index}: expected {expected:R}, actual {actual:R}");
    }
}
