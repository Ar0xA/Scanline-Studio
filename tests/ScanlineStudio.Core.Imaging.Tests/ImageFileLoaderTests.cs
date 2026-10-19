using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ImageFileLoaderTests
{
    // spec/18-path-to-1.0.md High item 3: a phone photo with EXIF orientation used to load sideways
    // with no in-app remedy -- neither loader called AutoOrient(). Orientation=6 ("rotate 90 CW
    // needed for correct display") on a 4-wide x 2-tall stored source should come out 2-wide x
    // 4-tall once AutoOrient runs -- proving the fix actually wires AutoOrient in, not re-verifying
    // ImageSharp's own rotation-direction correctness (out of scope, that's a stable library
    // behavior this project doesn't own).

    [Fact]
    public async Task LoadOriginalAsync_ExifOrientation_AutoOrientsSwappingWidthAndHeight()
    {
        var path = await WriteFixtureJpegWithOrientationAsync(width: 4, height: 2, orientation: 6);
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadOriginalAsync(path);

            Assert.Equal(2, image.Width);
            Assert.Equal(4, image.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_ExifOrientation_StillResizesToTheRequestedTargetDimensions()
    {
        // AutoOrient runs BEFORE Resize (see ImageFileLoader.LoadAsync's own comment) -- this pins
        // that the final output always matches the REQUESTED target, regardless of what orientation
        // correction happened first.
        var path = await WriteFixtureJpegWithOrientationAsync(width: 4, height: 2, orientation: 6);
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadAsync(path, targetWidth: 5, targetHeight: 7);

            Assert.Equal(5, image.Width);
            Assert.Equal(7, image.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteFixtureJpegWithOrientationAsync(int width, int height, ushort orientation)
    {
        using var image = new Image<Rgb24>(width, height);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-exif-test-{Guid.NewGuid()}.jpg");
        await image.SaveAsJpegAsync(path);
        return path;
    }

    [Fact]
    public async Task LoadAsync_RoundTripsPixelsAtTheOriginalSize()
    {
        // Channel-asymmetric color deliberately (not e.g. gray) -- an R/B swap bug between
        // ScanlineStudio.Abstractions.Imaging.Rgb24 and SixLabors.ImageSharp.PixelFormats.Rgb24 would fail
        // this loudly instead of silently passing.
        var path = await WriteFixturePngAsync(2, 2, new Rgb24(255, 128, 0));
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadAsync(path, targetWidth: 2, targetHeight: 2);

            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            var pixel = image.GetScanline(0)[0];
            Assert.Equal(255, pixel.R);
            Assert.Equal(128, pixel.G);
            Assert.Equal(0, pixel.B);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_ResizesToTheRequestedTargetDimensions()
    {
        var path = await WriteFixturePngAsync(4, 4, new Rgb24(10, 20, 30));
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadAsync(path, targetWidth: 2, targetHeight: 3);

            Assert.Equal(2, image.Width);
            Assert.Equal(3, image.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadOriginalAsync_PreservesTheFilesNativeResolution_NoResize()
    {
        var path = await WriteFixturePngAsync(7, 5, new Rgb24(1, 2, 3));
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadOriginalAsync(path);

            Assert.Equal(7, image.Width);
            Assert.Equal(5, image.Height);
            var pixel = image.GetScanline(0)[0];
            Assert.Equal(1, pixel.R);
            Assert.Equal(2, pixel.G);
            Assert.Equal(3, pixel.B);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(5000, 100, 4096, 82)]
    [InlineData(100, 5000, 82, 4096)]
    [InlineData(4097, 10, 4096, 10)]
    [InlineData(4096, 10, 4096, 10)]
    public async Task LoadOriginalAsync_LongerThanTheCap_IsScaledToFitKeepingAspect(int width, int height, int expectedWidth, int expectedHeight)
    {
        var path = await WriteFixturePngAsync(width, height, new Rgb24(9, 8, 7));
        try
        {
            var image = await new ImageFileLoader().LoadOriginalAsync(path);

            Assert.Equal(expectedWidth, image.Width);
            Assert.Equal(expectedHeight, image.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadOriginalAsync_ExifRotatedPictureOverTheCap_IsScaledThenAutoOriented()
    {
        var path = await WriteFixtureJpegWithOrientationAsync(width: 5000, height: 100, orientation: 6);
        try
        {
            var image = await new ImageFileLoader().LoadOriginalAsync(path);

            Assert.Equal(82, image.Width);
            Assert.Equal(4096, image.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(20000, 20000)]
    [InlineData(50000, 50000)]
    public async Task LoadOriginalAsync_HeaderOverThePixelLimit_IsRefusedBeforeDecoding(int width, int height)
    {
        var path = WritePngHeaderOnly(width, height);
        try
        {
            var ex = await Assert.ThrowsAsync<ScanlineStudio.Abstractions.Imaging.ImageLoadException>(() => new ImageFileLoader().LoadOriginalAsync(path));

            Assert.Equal(ScanlineStudio.Abstractions.Imaging.ImageLoadFailure.TooLarge, ex.Reason);
            Assert.Null(ex.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadOriginalAsync_TextFile_IsNotAnImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-test-{Guid.NewGuid()}.png");
        await File.WriteAllTextAsync(path, "this is not a picture");
        await AssertFailsWith(path, ScanlineStudio.Abstractions.Imaging.ImageLoadFailure.NotAnImage);
    }

    [Fact]
    public async Task LoadOriginalAsync_EmptyFile_IsNotAnImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-test-{Guid.NewGuid()}.jpg");
        await File.WriteAllBytesAsync(path, []);
        await AssertFailsWith(path, ScanlineStudio.Abstractions.Imaging.ImageLoadFailure.NotAnImage);
    }

    [Fact]
    public async Task LoadOriginalAsync_TruncatedPng_IsCorrupt()
    {
        var full = await WriteFixturePngAsync(64, 64, new Rgb24(1, 2, 3));
        var bytes = await File.ReadAllBytesAsync(full);
        File.Delete(full);
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-test-{Guid.NewGuid()}.png");
        await File.WriteAllBytesAsync(path, bytes[..60]);
        await AssertFailsWith(path, ScanlineStudio.Abstractions.Imaging.ImageLoadFailure.Corrupt);
    }

    [Fact]
    public async Task LoadOriginalAsync_MissingFile_IsUnreadable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-missing-{Guid.NewGuid()}.png");
        var ex = await Assert.ThrowsAsync<ScanlineStudio.Abstractions.Imaging.ImageLoadException>(() => new ImageFileLoader().LoadOriginalAsync(path));
        Assert.Equal(ScanlineStudio.Abstractions.Imaging.ImageLoadFailure.Unreadable, ex.Reason);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task LoadOriginalAsync_Cancelled_ThrowsCancellationNotAnImageLoadException()
    {
        var path = await WriteFixturePngAsync(8, 8, new Rgb24(1, 2, 3));
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImageFileLoader().LoadOriginalAsync(path, cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task AssertFailsWith(string path, ScanlineStudio.Abstractions.Imaging.ImageLoadFailure reason)
    {
        try
        {
            var ex = await Assert.ThrowsAsync<ScanlineStudio.Abstractions.Imaging.ImageLoadException>(() => new ImageFileLoader().LoadOriginalAsync(path));
            Assert.Equal(reason, ex.Reason);
            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A PNG signature, a valid IHDR claiming the given size, and IEND: enough for Identify, no pixel data.
    private static string WritePngHeaderOnly(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new List<byte>();
        ihdr.AddRange(BigEndian((uint)width));
        ihdr.AddRange(BigEndian((uint)height));
        ihdr.AddRange(new byte[] { 8, 2, 0, 0, 0 });
        AddChunk(bytes, "IHDR", ihdr.ToArray());
        AddChunk(bytes, "IEND", []);
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-test-{Guid.NewGuid()}.png");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static void AddChunk(List<byte> png, string type, byte[] data)
    {
        png.AddRange(BigEndian((uint)data.Length));
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.AddRange(typed);
        png.AddRange(BigEndian(Crc32(typed)));
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static async Task<string> WriteFixturePngAsync(int width, int height, Rgb24 fillColor)
    {
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                accessor.GetRowSpan(y).Fill(fillColor);
            }
        });
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-test-{Guid.NewGuid()}.png");
        await image.SaveAsPngAsync(path);
        return path;
    }
}
