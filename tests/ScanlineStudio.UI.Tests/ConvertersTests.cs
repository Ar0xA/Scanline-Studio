using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Converters;

namespace ScanlineStudio.UI.Tests;

/// <summary>Tier C audit finding: none of the 13 converters in `ScanlineStudio.UI/Converters/` had
/// any dedicated test coverage before this pass. <see cref="UnsetValuePassesThroughWithoutThrowing"/>
/// pins the round's own fix (6 converters used to `throw new NotSupportedException(...)` on a value
/// that wasn't the type they expected, reachable via Avalonia's own <see cref="AvaloniaProperty.UnsetValue"/>
/// sentinel on a broken/not-yet-resolved binding path, not just a genuinely-wrong-typed value) --
/// covers the whole class permanently rather than one converter at a time. The remaining tests target
/// the two converters with real, non-trivial logic worth pinning independently
/// (<see cref="UtcTimestampTextConverter"/>'s UTC-not-local timestamp handling,
/// <see cref="Rgb24ToColorConverter"/>'s round-trip, both directly exercised by the fix above too).</summary>
public sealed class ConvertersTests
{
    public static IEnumerable<object[]> AllSingleValueConverters =>
    [
        [BoolToOpacityConverter.Instance],
        [DoubleToCornerRadiusConverter.Instance],
        [DoubleToHalfConverter.Instance],
        [DoubleToStarGridLengthConverter.Instance],
        [DoubleToThicknessConverter.Instance],
        [ImageFitModeToStretchConverter.Instance],
        [IsBoxElementConverter.Instance],
        [PipelineFontFamilyNameConverter.Instance],
        [Rgb24ToBrushConverter.Instance],
        [Rgb24ToColorConverter.Instance],
        [UtcTimestampTextConverter.Instance],
        [WaterfallViewModeToColumnWidthConverter.Instance],
    ];

    [Theory]
    [MemberData(nameof(AllSingleValueConverters))]
    public void UnsetValuePassesThroughWithoutThrowing(IValueConverter converter)
    {
        // Tier C audit finding (risk, fixed): Avalonia hands AvaloniaProperty.UnsetValue to a
        // converter far more often in practice than a hand-audited "happy path" review accounts for
        // (a DataContext not yet set, a binding path that resolved to null partway, a design-time
        // preview) -- 6 of these 12 used to throw NotSupportedException on exactly this input. One
        // theory test covers the whole class permanently instead of one converter at a time.
        var exception = Record.Exception(() => converter.Convert(AvaloniaProperty.UnsetValue, typeof(object), null, CultureInfo.InvariantCulture));

        Assert.Null(exception);
    }

    [Fact]
    public void RadioModeDisplayConverter_UnsetValue_DoesNotThrow()
    {
        // Same finding as above -- IMultiValueConverter has a different signature, so it can't share
        // the single-value theory test.
        var exception = Record.Exception(() =>
            RadioModeDisplayConverter.Instance.Convert([AvaloniaProperty.UnsetValue, "noneText"], typeof(string), null, CultureInfo.InvariantCulture));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(128, 64, 200)]
    public void Rgb24ToColorConverter_RoundTrips(byte r, byte g, byte b)
    {
        var original = new Rgb24(r, g, b);

        var color = Rgb24ToColorConverter.Instance.Convert(original, typeof(Color), null, CultureInfo.InvariantCulture);
        var roundTripped = Rgb24ToColorConverter.Instance.ConvertBack(color, typeof(Rgb24), null, CultureInfo.InvariantCulture);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void UtcTimestampTextConverter_NonUtcOffsetInput_ConvertsToItsUtcWallClockNotItsLocalOne()
    {
        // The exact corruption this converter's own doc comment exists to prevent: a +09:00-offset
        // instant must render as its UTC wall clock, not get silently reinterpreted through the
        // machine's own local offset.
        var input = new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.FromHours(9)); // 12:00 UTC

        var text = UtcTimestampTextConverter.Instance.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("2026-08-22 12:00:00", text);
    }

    [Fact]
    public void UtcTimestampTextConverter_ConvertBack_AlwaysProducesZeroOffset()
    {
        var result = UtcTimestampTextConverter.Instance.ConvertBack("2026-08-22 12:00:00", typeof(DateTimeOffset), null, CultureInfo.InvariantCulture);

        var dto = Assert.IsType<DateTimeOffset>(result);
        Assert.Equal(TimeSpan.Zero, dto.Offset);
    }

    [Fact]
    public void UtcTimestampTextConverter_ConvertBack_BlankTextWithoutAllowNull_DoesNothingInsteadOfClearing()
    {
        // The required-field path (e.g. Start) -- blank/unparseable text must leave the last valid
        // value in place, not crash the binding or silently default to "now."
        var result = UtcTimestampTextConverter.Instance.ConvertBack(string.Empty, typeof(DateTimeOffset), null, CultureInfo.InvariantCulture);

        Assert.Equal(BindingOperations.DoNothing, result);
    }

    [Fact]
    public void UtcTimestampTextConverter_ConvertBack_BlankTextWithAllowNull_ClearsToNull()
    {
        // The optional-field path (e.g. End) -- ConverterParameter=AllowNull.
        var result = UtcTimestampTextConverter.Instance.ConvertBack(string.Empty, typeof(DateTimeOffset?), "AllowNull", CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    [Fact]
    public void UtcTimestampTextConverter_ConvertBack_UnparseableText_DoesNothing()
    {
        var result = UtcTimestampTextConverter.Instance.ConvertBack("not a date", typeof(DateTimeOffset), null, CultureInfo.InvariantCulture);

        Assert.Equal(BindingOperations.DoNothing, result);
    }
}
