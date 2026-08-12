using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(WindowGeometrySettings))]
public sealed partial class WindowGeometrySettingsJsonContext : JsonSerializerContext;
