using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(AppearanceSettings))]
public sealed partial class AppearanceSettingsJsonContext : JsonSerializerContext;
