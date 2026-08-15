using System.Reflection;
using Avalonia.Headless.XUnit;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class WaterfallControlTests
{
    /// <summary>Reflection into the private color-history buffer, deliberately not a new public API
    /// -- this project's own precedent (<c>MmvFile.cs</c>'s doc comment) is not to promote test-only
    /// surface into production without an actual driving use case. Reads the managed <c>_colorHistory</c>
    /// byte[] directly rather than round-tripping through the real <c>WriteableBitmap</c>: confirmed
    /// empirically that Avalonia's HEADLESS test renderer does not reliably persist raw pixel bytes
    /// across separate <c>Lock()</c> calls (a diagnostic probe wrote known bytes, then a fresh
    /// <c>Lock()</c> read back different, non-matching values, and reported <c>Format=Rgba8888</c>
    /// even though <c>Bgra8888</c> was requested) -- a headless-test-environment artifact, not a
    /// production bug, but it makes bitmap-level pixel assertions unreliable in this test
    /// environment. <c>_colorHistory</c> is a plain CLR array the control's own code writes directly,
    /// unaffected by that native-bitmap quirk, and is what's actually under test here anyway.</summary>
    private static (byte B, byte G, byte R, byte A) GetColorHistoryPixel(WaterfallControl control, int bin, int row)
    {
        var field = typeof(WaterfallControl).GetField("_colorHistory", BindingFlags.NonPublic | BindingFlags.Instance);
        var binsField = typeof(WaterfallControl).GetField("_bins", BindingFlags.NonPublic | BindingFlags.Instance);
        var colorHistory = (byte[]?)field!.GetValue(control);
        var bins = (int)binsField!.GetValue(control)!;
        Assert.NotNull(colorHistory);

        var offset = (row * bins * 4) + (bin * 4);
        return (colorHistory![offset + 0], colorHistory[offset + 1], colorHistory[offset + 2], colorHistory[offset + 3]);
    }

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

    [AvaloniaFact]
    public void ZeroDbAndGainDb_DefaultToTheMeasuredRealSignalRange()
    {
        // WaterfallPaneViewModel's own doc comment has the measurement this locks in: real
        // legacy-captured signal peaks land 38-46dB, background noise -50 to -65dB -- 0/50 sits
        // above the noise floor with a 50dB span comfortably covering real peaks.
        using var control = new WaterfallControl();

        Assert.Equal(0.0, control.ZeroDb);
        Assert.Equal(50.0, control.GainDb);
    }

    [AvaloniaFact]
    public void SettingZeroDbAndGainDb_DoesNotThrow_WithOrWithoutAFrameSet()
    {
        using var control = new WaterfallControl();

        control.ZeroDb = -20;
        control.GainDb = 30;
        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        control.ZeroDb = 10;
        control.GainDb = 60;
        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        Assert.Equal(10, control.ZeroDb);
        Assert.Equal(60, control.GainDb);
    }

    [AvaloniaFact]
    public void SettingFrame_WithGainDbZero_DoesNotThrowOrDivideByZero()
    {
        using var control = new WaterfallControl { GainDb = 0 };

        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        Assert.NotNull(control.Frame);
    }

    [AvaloniaFact]
    public void SettingFrame_ColorsRowZeroUsingTheGradientAndTheCurrentZeroGainWindow()
    {
        // Covers the normalization formula, the BGRA byte order, and the row-0-is-newest invariant
        // in one assertion: ZeroDb=0/GainDb=50, magnitude=25dB -> normalized=0.5, which falls between
        // the cyan (0.35) and green (0.55) gradient stops at t=0.75 -> (R,G,B)=(0,195,115).
        using var control = new WaterfallControl { ZeroDb = 0, GainDb = 50 };

        control.Frame = new WaterfallFrame([25f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        var pixel = GetColorHistoryPixel(control, bin: 0, row: 0);
        Assert.Equal(((byte)115, (byte)195, (byte)0, (byte)255), pixel);
    }

    [AvaloniaFact]
    public void ChangingZeroDbOrGainDb_RecolorsAlreadyRenderedRows_NotJustFutureFrames()
    {
        // Auditor-caught (batch 8): without RecolorizeAll, dragging Zero/Gain only affected rows
        // written AFTER the change -- the already-scrolled-in row from before the change would keep
        // showing the OLD normalization until it aged out 200 frames later.
        using var control = new WaterfallControl { ZeroDb = 0, GainDb = 50 };
        control.Frame = new WaterfallFrame([25f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        var before = GetColorHistoryPixel(control, bin: 0, row: 0);

        control.GainDb = 10; // same 25dB magnitude now normalizes to 1.0 (clamped) instead of 0.5

        var after = GetColorHistoryPixel(control, bin: 0, row: 0);
        Assert.NotEqual(before, after);
    }

    [AvaloniaFact]
    public void ChangingZeroDbOrGainDb_LeavesNeverWrittenRowsNearBlack_NotFloodedWithAnArbitraryColor()
    {
        // Auditor-caught (batch 8): _dbHistory allocates zero-filled, and 0dB is a legitimate
        // magnitude value indistinguishable from "never written" -- without a sentinel, a
        // ZeroDb=-70/GainDb=20 combination (both inside the shipped slider ranges) normalizes an
        // unwritten row's 0.0f to (0-(-70))/20=3.5, clamped to 1.0 -> solid red across the entire
        // not-yet-filled history the very first time a user moves a slider early in a session.
        using var control = new WaterfallControl { ZeroDb = 0, GainDb = 50 };
        control.Frame = new WaterfallFrame([25f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow); // only row 0 is ever written

        control.ZeroDb = -70;
        control.GainDb = 20;

        var neverWrittenRow = GetColorHistoryPixel(control, bin: 0, row: 199);
        var nearBlack = WaterfallPalette.Lerp(0.0);
        Assert.Equal((nearBlack.B, nearBlack.G, nearBlack.R, (byte)255), neverWrittenRow);
    }

    [AvaloniaFact]
    public void StartHzAndSpanHz_DefaultToTheSameValuesAsWaterfallPaneViewModel()
    {
        // spec/18-path-to-1.0.md Medium item 2, second half. Kept in sync deliberately -- a
        // freshly-constructed control (before any real binding value ever arrives) should already
        // show the same window the ViewModel itself defaults to, not some other arbitrary range.
        using var control = new WaterfallControl();

        Assert.Equal(1000.0, control.StartHz);
        Assert.Equal(1600.0, control.SpanHz);
    }

    [AvaloniaFact]
    public void SettingStartHzAndSpanHz_DoesNotThrow_IncludingDegenerateValues_WithOrWithoutAFrameSet()
    {
        // No Minimum/Maximum exists on the real steppers (a separate, pre-existing gap this item's
        // own plan-review explicitly deferred) -- WaterfallRenderMath.TryComputeWindow is what
        // actually guards Render() against these, covered directly and exhaustively by
        // WaterfallRenderMathTests; this only proves the control itself survives being handed them,
        // both before and after a real frame exists.
        using var control = new WaterfallControl();

        control.StartHz = -500;
        control.SpanHz = 0;
        control.StartHz = 10000;
        control.SpanHz = -100;

        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        control.StartHz = -500;
        control.SpanHz = 0;
        control.StartHz = 10000;
        control.SpanHz = -100;

        Assert.Equal(10000, control.StartHz);
        Assert.Equal(-100, control.SpanHz);
    }

    [AvaloniaFact]
    public void Dispose_ThenPushingANewFrame_RendersAgain_NotPermanentlyBlank()
    {
        // Auditor-caught (batch 8): WaterfallPaneView sits inside the Receive TabItem
        // (MainWindow.axaml) -- a non-selected tab detaches its content, calling Dispose(), then
        // reattaches on switching back. Dispose must fully reset internal state (not just the
        // bitmap), or the next frame's "_colorHistory is null || _bins != bins" init-guard silently
        // skips recreating the bitmap and the waterfall goes permanently blank for the rest of the
        // session after one tab switch.
        using var control = new WaterfallControl();
        control.Frame = new WaterfallFrame([-10f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        control.Dispose();
        control.Frame = new WaterfallFrame([-10f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        // A real color was (re-)written -- proves the init-guard recreated _colorHistory instead of
        // silently early-returning against the stale post-Dispose state.
        var pixel = GetColorHistoryPixel(control, bin: 0, row: 0);
        Assert.Equal((byte)255, pixel.A);
    }
}
