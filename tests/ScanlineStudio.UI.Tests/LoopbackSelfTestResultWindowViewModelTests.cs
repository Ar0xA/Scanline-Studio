using Avalonia.Headless.XUnit;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class LoopbackSelfTestResultWindowViewModelTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test-mode", DisplayName: "Test Mode", VisCode: 0, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly SstvModeDefinition OtherMode = new(
        Id: "other-mode", DisplayName: "Other Mode", VisCode: 1, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static readonly IReadOnlyList<SstvModeDefinition> AvailableModes = [TestMode, OtherMode];

    [AvaloniaFact]
    public void Constructor_ModeMatches_NoMismatchWarning()
    {
        var result = new LoopbackSelfTestResult(TestImage, TestMode.Id, LoopbackSelfTestOutcome.Completed);

        var vm = new LoopbackSelfTestResultWindowViewModel(TestMode, result, AvailableModes, new FakeLocalizationService());

        Assert.Null(vm.ModeMismatchWarning);
    }

    [AvaloniaFact]
    public void Constructor_DetectedModeDiffersFromRequested_SetsMismatchWarningWithDisplayNames()
    {
        // Code-review round-1 finding: the warning must show DISPLAY names (from the caller-supplied
        // AvailableModes list), not the raw internal mode id -- UI can't reference Core.Sstv's
        // SstvModeRegistry directly (layering rule), so this is the only source available to it.
        var result = new LoopbackSelfTestResult(TestImage, OtherMode.Id, LoopbackSelfTestOutcome.Completed);
        var localization = new FakeLocalizationService();

        var vm = new LoopbackSelfTestResultWindowViewModel(TestMode, result, AvailableModes, localization);

        Assert.NotNull(vm.ModeMismatchWarning);
        Assert.Equal("LoopbackSelfTest.ModeMismatchWarning", localization.LastKey);
        Assert.Equal([OtherMode.DisplayName, TestMode.DisplayName], localization.LastArgs);
    }

    [AvaloniaFact]
    public void Constructor_DetectedModeNotInAvailableModes_FallsBackToRawId()
    {
        var result = new LoopbackSelfTestResult(TestImage, "unknown-mode-id", LoopbackSelfTestOutcome.Completed);
        var localization = new FakeLocalizationService();

        _ = new LoopbackSelfTestResultWindowViewModel(TestMode, result, AvailableModes, localization);

        Assert.Equal(["unknown-mode-id", TestMode.DisplayName], localization.LastArgs);
    }

    [AvaloniaFact]
    public void Constructor_NoModeDetected_SetsNoModeDetectedWarning()
    {
        // Code-review round-1 finding: a total no-lock result was previously indistinguishable from
        // any other Incomplete outcome -- must say explicitly that no mode was ever detected.
        var result = new LoopbackSelfTestResult(TestImage, null, LoopbackSelfTestOutcome.Incomplete);
        var localization = new FakeLocalizationService();

        var vm = new LoopbackSelfTestResultWindowViewModel(TestMode, result, AvailableModes, localization);

        Assert.NotNull(vm.ModeMismatchWarning);
        Assert.Equal("LoopbackSelfTest.NoModeDetectedWarning", localization.LastKey);
    }

    [AvaloniaFact]
    public void CloseCommand_RaisesRequestClose()
    {
        var result = new LoopbackSelfTestResult(TestImage, TestMode.Id, LoopbackSelfTestOutcome.Completed);
        var vm = new LoopbackSelfTestResultWindowViewModel(TestMode, result, AvailableModes, new FakeLocalizationService());
        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }
}
