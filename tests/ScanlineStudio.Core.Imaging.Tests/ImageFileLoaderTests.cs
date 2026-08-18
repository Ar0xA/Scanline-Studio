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
