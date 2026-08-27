using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(RxPaneUiSettings))]
public sealed partial class RxPaneUiSettingsJsonContext : JsonSerializerContext;
