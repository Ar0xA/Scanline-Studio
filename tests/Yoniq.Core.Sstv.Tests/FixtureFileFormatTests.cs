using Yoniq.Abstractions.Imaging;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Round-trip tests for <see cref="MmvFile"/>'s and <see cref="BmpFile"/>'s new <c>Write</c> methods
/// (spec/14-roadmap.md, "Milestone audit, Phase 1+2" -> "Explicit prerequisite before Phase 3" --
/// TX-side golden-vector fixture prep). Opus verification (requested before any fixture generation)
/// caught a real bug in <see cref="MmvFile.Write"/>'s first draft -- a full-scale +1.0 sample wrapped
/// to -32768 instead of saturating at +32767, via a double-widened <c>Math.Round</c> then a plain
/// truncating <c>short</c> cast rather than a saturating one -- these tests exist specifically because
/// neither writer had any coverage at all before that review, which is exactly how the bug went
/// unnoticed in the first draft.
/// </summary>
public class FixtureFileFormatTests
{
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(0.99999f)] // just inside the boundary the review's own bug wrapped
    [InlineData(-0.99999f)]
    public void MmvFile_WriteThenRead_RoundTripsWithinQuantizationTolerance(float sample)
    {
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-mmv-roundtrip-{Guid.NewGuid():N}.mmv");
        try
        {
            MmvFile.Write(path, [sample], sampleRate: 11025);
            var (samples, sampleRate) = MmvFile.Read(path);

            Assert.Equal(11025, sampleRate);
            Assert.Single(samples);
            // 1/32768 is int16 quantization's own worst-case single-sample error -- generous margin,
            // not a loose bound chosen to mask a real regression.
            Assert.True(Math.Abs(samples[0] - sample) <= 1.0f / 32768.0f + 1e-6f,
                $"expected ~{sample}, got {samples[0]}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MmvFile_WriteFullScaleSineLikeSignal_NeverWrapsToOppositeSign()
    {
        // Directly reproduces the review's own failure trigger: AnalogFmSstvEncoder yields raw
        // Math.Sin(phase) samples, unattenuated -- a real transmission spends real time in the
        // clamped*32768 >= 32767.5 window (sample >= ~0.99998474) many times. Sweep a full sine cycle
        // at fine resolution and confirm every sample round-trips with the SAME sign, never wrapping.
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2 * Math.PI * i / samples.Length);
        }

        var path = Path.Combine(Path.GetTempPath(), $"yoniq-mmv-sine-{Guid.NewGuid():N}.mmv");
        try
        {
            MmvFile.Write(path, samples, sampleRate: 11025);
            var (readBack, _) = MmvFile.Read(path);

            Assert.Equal(samples.Length, readBack.Length);
            for (var i = 0; i < samples.Length; i++)
            {
                Assert.True(Math.Abs(readBack[i] - samples[i]) < 0.01f,
                    $"index {i}: expected ~{samples[i]}, got {readBack[i]} -- looks like a sign-wrap, not quantization noise");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MmvFile_Write_UnsupportedSampleRate_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-mmv-badrate-{Guid.NewGuid():N}.mmv");
        Assert.Throws<NotSupportedException>(() => MmvFile.Write(path, [0.0f], sampleRate: 12345));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 1)]
    [InlineData(7, 5)]
    public void BmpFile_WriteThenRead_RoundTripsExactly(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24(
                    R: (byte)((x * 37) % 256),
                    G: (byte)((y * 53) % 256),
                    B: (byte)(((x + y) * 71) % 256));
            }
        }

        var image = new ArrayImageSource(width, height, pixels);
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-bmp-roundtrip-{Guid.NewGuid():N}.bmp");
        try
        {
            BmpFile.Write(path, image);
            var readBack = BmpFile.Read(path);

            Assert.Equal(width, readBack.Width);
            Assert.Equal(height, readBack.Height);
            for (var y = 0; y < height; y++)
            {
                var expectedRow = image.GetScanline(y);
                var actualRow = readBack.GetScanline(y);
                for (var x = 0; x < width; x++)
                {
                    Assert.Equal(expectedRow[x], actualRow[x]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
