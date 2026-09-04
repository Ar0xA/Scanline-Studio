using System.Text.Json;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.Tests;

public sealed class AppearanceSettingsTests
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.System)]
    public void RoundTripsThroughItsJsonContext(AppTheme theme)
    {
        var settings = new AppearanceSettings { Theme = theme };

        var json = JsonSerializer.Serialize(settings, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppearanceSettingsJsonContext.Default.AppearanceSettings);

        Assert.Equal(theme, roundTripped!.Theme);
    }

    [Fact]
    public void UnsetTheme_RoundTripsAsNull()
    {
        var settings = new AppearanceSettings();

        var json = JsonSerializer.Serialize(settings, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppearanceSettingsJsonContext.Default.AppearanceSettings);

        Assert.Null(roundTripped!.Theme);
    }

    [Theory]
    [InlineData(AppFontScale.Normal)]
    [InlineData(AppFontScale.Large)]
    public void FontScale_RoundTripsThroughItsJsonContext(AppFontScale fontScale)
    {
        var settings = new AppearanceSettings { FontScale = fontScale };

        var json = JsonSerializer.Serialize(settings, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppearanceSettingsJsonContext.Default.AppearanceSettings);

        Assert.Equal(fontScale, roundTripped!.FontScale);
    }

    [Fact]
    public void UnsetFontScale_RoundTripsAsNull()
    {
        var settings = new AppearanceSettings();

        var json = JsonSerializer.Serialize(settings, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppearanceSettingsJsonContext.Default.AppearanceSettings);

        Assert.Null(roundTripped!.FontScale);
    }

    [Fact]
    public void ThemeAndFontScale_RoundTripIndependently_TogetherInOneSection()
    {
        var settings = new AppearanceSettings { Theme = AppTheme.Dark, FontScale = AppFontScale.Large };

        var json = JsonSerializer.Serialize(settings, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppearanceSettingsJsonContext.Default.AppearanceSettings);

        Assert.Equal(AppTheme.Dark, roundTripped!.Theme);
        Assert.Equal(AppFontScale.Large, roundTripped.FontScale);
    }
}
