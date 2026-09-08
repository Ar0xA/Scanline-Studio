using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

/// <summary>Code-review finding (export-frame-jpeg-quality.md): <see cref="FilePickerService.ResolveDestination"/>
/// is the only real branching logic in <see cref="FilePickerService.PickSaveImageFileAsync"/> --
/// the rest of that method needs a live Application.Current/MainWindow and can't be unit-tested,
/// but this static method deliberately can. The real-window verification run only exercised the
/// APPEND-when-missing path (JPEG selected, no extension typed); the REWRITE-mismatched-extension
/// path (JPEG selected while the field still read ".png" -- the exact GTK behavior that
/// verification run observed) was never exercised by an automated test until now.</summary>
public sealed class FilePickerServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizedExistingSibling_RequiresItsOwnOverwriteConfirmation(bool confirm)
    {
        var directory = Directory.CreateTempSubdirectory("astra-picker-").FullName;
        try
        {
            var picked = Path.Combine(directory, "frame.png");
            var actual = Path.Combine(directory, "frame.jpg");
            await File.WriteAllTextAsync(actual, "existing image");
            var service = new FilePickerService(Microsoft.Extensions.Logging.Abstractions.NullLogger<FilePickerService>.Instance,
                new FakeLocalizationService());
            string? askedPath = null;
            var result = await service.ResolveConfirmedDestinationAsync(picked, FilePickerService.JpegFileType, path =>
            {
                askedPath = path;
                return Task.FromResult(confirm);
            });
            Assert.Equal(actual, askedPath);
            Assert.Equal(confirm, result.HasValue);
            if (confirm) Assert.Equal(actual, result!.Value.Path);
            Assert.Equal("existing image", await File.ReadAllTextAsync(actual));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResolveDestination_JpegSelected_PathHasNoExtension_AppendsJpg()
    {
        var (path, format) = FilePickerService.ResolveDestination("/tmp/frame", FilePickerService.JpegFileType);

        Assert.Equal("/tmp/frame.jpg", path);
        Assert.Equal(ImageExportFormat.Jpeg, format);
    }

    [Fact]
    public void ResolveDestination_JpegSelected_PathStillHasPngExtension_RewritesToJpg()
    {
        // The exact scenario real-window testing confirmed on GTK: switching the type dropdown to
        // JPEG does NOT rewrite the filename field, which still reads ".png" from the suggested
        // name. Without this rewrite, the file would be JPEG-encoded but named ".png".
        var (path, format) = FilePickerService.ResolveDestination("/tmp/20260815-120000_test.png", FilePickerService.JpegFileType);

        Assert.Equal("/tmp/20260815-120000_test.jpg", path);
        Assert.Equal(ImageExportFormat.Jpeg, format);
    }

    [Fact]
    public void ResolveDestination_JpegSelected_PathAlreadyHasJpegExtension_LeavesItAlone()
    {
        var (path, format) = FilePickerService.ResolveDestination("/tmp/frame.jpeg", FilePickerService.JpegFileType);

        Assert.Equal("/tmp/frame.jpeg", path);
        Assert.Equal(ImageExportFormat.Jpeg, format);
    }

    [Fact]
    public void ResolveDestination_PngSelected_PathHasJpgExtension_RewritesToPng()
    {
        var (path, format) = FilePickerService.ResolveDestination("/tmp/frame.jpg", FilePickerService.PngFileType);

        Assert.Equal("/tmp/frame.png", path);
        Assert.Equal(ImageExportFormat.Png, format);
    }

    [Fact]
    public void ResolveDestination_NoSelectedType_SniffsJpegExtensionFromPath()
    {
        // The documented fallback for a platform whose picker doesn't report SelectedFileType at
        // all ("null if not supported"), or where the echoed-back FilePickerFileType instance
        // isn't reference-equal to this class's own PngFileType/JpegFileType (unverified on
        // Windows/macOS) -- either way, resolution must still produce a sane result, not throw.
        var (path, format) = FilePickerService.ResolveDestination("/tmp/frame.jpg", null);

        Assert.Equal("/tmp/frame.jpg", path);
        Assert.Equal(ImageExportFormat.Jpeg, format);
    }

    [Fact]
    public void ResolveDestination_NoSelectedType_NoRecognizedExtension_DefaultsToPng()
    {
        var (path, format) = FilePickerService.ResolveDestination("/tmp/frame.bmp", null);

        Assert.Equal("/tmp/frame.png", path);
        Assert.Equal(ImageExportFormat.Png, format);
    }
}
