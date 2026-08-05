using System.Text.Json;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.Tests;

public sealed class TxPaneUiSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new TxPaneUiSettings
        {
            FavoriteModeIds = ["robot36", "martin-m1"],
            AutoFollowRxMode = true,
        };

        var json = JsonSerializer.Serialize(settings, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
        var roundTripped = JsonSerializer.Deserialize(json, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);

        Assert.Equal(settings.FavoriteModeIds, roundTripped!.FavoriteModeIds);
        Assert.Equal(settings.AutoFollowRxMode, roundTripped.AutoFollowRxMode);
    }
}
