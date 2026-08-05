using Avalonia.Headless.XUnit;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class WaterfallControlTests
{
    [AvaloniaFact]
    public void SettingFrame_DoesNotThrow_AndCanBeUpdatedRepeatedly()
    {
        using var control = new WaterfallControl();

        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        control.Frame = new WaterfallFrame([-10f, -30f, -60f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        Assert.NotNull(control.Frame);
    }

    [AvaloniaFact]
    public void SettingFrame_WithDifferentBinCount_ResizesWithoutThrowing()
    {
        using var control = new WaterfallControl();

        control.Frame = new WaterfallFrame([-10f, -20f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        control.Frame = new WaterfallFrame([-10f, -20f, -30f, -40f], BinWidthHz: 50, ObservedAt: DateTimeOffset.UtcNow);

        Assert.Equal(4, control.Frame.MagnitudesDb.Count);
    }
}
