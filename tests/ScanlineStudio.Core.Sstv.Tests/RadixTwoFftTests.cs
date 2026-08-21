using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class RadixTwoFftTests
{
    [Fact]
    public void Forward_ImpulseAtOrigin_ProducesFlatUnitMagnitudeSpectrum()
    {
        const int n = 64;
        var real = new float[n];
        var imag = new float[n];
        real[0] = 1f;

        RadixTwoFft.Forward(real, imag);

        for (var k = 0; k < n; k++)
        {
            var magnitude = MathF.Sqrt((real[k] * real[k]) + (imag[k] * imag[k]));
            Assert.True(Math.Abs(magnitude - 1f) < 1e-4f, $"bin {k}: expected magnitude ~1, got {magnitude}");
        }
    }

    [Fact]
    public void Forward_DcSignal_ProducesEnergyOnlyAtBinZero()
    {
        const int n = 64;
        var real = new float[n];
        var imag = new float[n];
        Array.Fill(real, 1f);

        RadixTwoFft.Forward(real, imag);

        Assert.True(Math.Abs(real[0] - n) < 1e-3f, $"bin 0 expected ~{n}, got {real[0]}");
        for (var k = 1; k < n; k++)
        {
            var magnitude = MathF.Sqrt((real[k] * real[k]) + (imag[k] * imag[k]));
            Assert.True(magnitude < 1e-3f, $"bin {k}: expected ~0, got {magnitude}");
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Forward_PureCosineAtBinK_PeaksAtThatBinAndItsMirror(int targetBin)
    {
        const int n = 64;
        var real = new float[n];
        var imag = new float[n];
        for (var i = 0; i < n; i++)
        {
            real[i] = MathF.Cos(2f * MathF.PI * targetBin * i / n);
        }

        RadixTwoFft.Forward(real, imag);

        var magnitudes = new float[n];
        for (var k = 0; k < n; k++)
        {
            magnitudes[k] = MathF.Sqrt((real[k] * real[k]) + (imag[k] * imag[k]));
        }

        var peakBin = Array.IndexOf(magnitudes, magnitudes.Max());
        Assert.True(peakBin == targetBin || peakBin == n - targetBin,
            $"expected peak at bin {targetBin} or its mirror {n - targetBin}, got {peakBin}");
    }

    [Fact]
    public void Forward_ComplexExponentialAtBinK_PeaksExactlyAtThatBin_NotItsMirror()
    {
        // Closes a coverage gap flagged by Tier A Batch 7 chunk 7f (docs/functional-audit-playbook.md):
        // every test above feeds a real-valued (imag=0) input and asserts only magnitudes, so a
        // conjugated (wrong-sign) FFT kernel -- e^(+2*pi*i/len) instead of the correct
        // e^(-2*pi*i/len) -- would pass all of them unchanged (the cosine test above even explicitly
        // accepts either targetBin or its mirror). A genuine complex exponential input
        // x[t] = e^(i*2*pi*k*t/n) is the one signal whose DFT distinguishes the two conventions: the
        // correct kernel concentrates all energy in bin k exactly; a conjugated kernel would instead
        // concentrate it at bin (n-k). Also exercises a non-zero `imag` INPUT, never done above.
        const int n = 64;
        const int targetBin = 5;
        var real = new float[n];
        var imag = new float[n];
        for (var t = 0; t < n; t++)
        {
            var phase = 2.0 * Math.PI * targetBin * t / n;
            real[t] = (float)Math.Cos(phase);
            imag[t] = (float)Math.Sin(phase);
        }

        RadixTwoFft.Forward(real, imag);

        var magnitudes = new float[n];
        for (var k = 0; k < n; k++)
        {
            magnitudes[k] = MathF.Sqrt((real[k] * real[k]) + (imag[k] * imag[k]));
        }

        var peakBin = Array.IndexOf(magnitudes, magnitudes.Max());
        Assert.Equal(targetBin, peakBin);
        Assert.True(Math.Abs(magnitudes[targetBin] - n) < 1e-3f, $"expected bin {targetBin} magnitude ~{n}, got {magnitudes[targetBin]}");
        for (var k = 0; k < n; k++)
        {
            if (k == targetBin)
            {
                continue;
            }

            Assert.True(magnitudes[k] < 1e-3f, $"bin {k}: expected ~0, got {magnitudes[k]}");
        }
    }

    [Fact]
    public void Forward_LengthOne_IsANoOp()
    {
        // n=1 is a valid power of two whose bit-reversal and butterfly loops both correctly
        // degenerate to no-ops (Tier A Batch 7 chunk 7f) -- the length-1 DFT IS the identity.
        // Previously unasserted.
        var real = new[] { 3f };
        var imag = new[] { -2f };

        RadixTwoFft.Forward(real, imag);

        Assert.Equal(3f, real[0]);
        Assert.Equal(-2f, imag[0]);
    }

    [Fact]
    public void Forward_LengthTwo_MatchesHandComputedTransform()
    {
        // n=2's single butterfly stage, previously unasserted directly (Tier A Batch 7 chunk 7f):
        // X0=x0+x1, X1=x0-x1.
        var real = new[] { 3f, 1f };
        var imag = new[] { 0f, 0f };

        RadixTwoFft.Forward(real, imag);

        Assert.True(Math.Abs(real[0] - 4f) < 1e-5f, $"X0 real: expected 4, got {real[0]}");
        Assert.True(Math.Abs(imag[0]) < 1e-5f, $"X0 imag: expected 0, got {imag[0]}");
        Assert.True(Math.Abs(real[1] - 2f) < 1e-5f, $"X1 real: expected 2, got {real[1]}");
        Assert.True(Math.Abs(imag[1]) < 1e-5f, $"X1 imag: expected 0, got {imag[1]}");
    }

    [Fact]
    public void Forward_LengthNotPowerOfTwo_Throws()
    {
        var real = new float[10];
        var imag = new float[10];

        Assert.Throws<ArgumentException>(() => RadixTwoFft.Forward(real, imag));
    }

    [Fact]
    public void Forward_MismatchedLengths_Throws()
    {
        var real = new float[16];
        var imag = new float[8];

        Assert.Throws<ArgumentException>(() => RadixTwoFft.Forward(real, imag));
    }
}
