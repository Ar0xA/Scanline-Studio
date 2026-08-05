using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(TxPaneUiSettings))]
public sealed partial class TxPaneUiSettingsJsonContext : JsonSerializerContext;
