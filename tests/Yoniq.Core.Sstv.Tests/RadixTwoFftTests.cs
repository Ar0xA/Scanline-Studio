using Yoniq.Core.Sstv;

namespace Yoniq.Core.Sstv.Tests;

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
