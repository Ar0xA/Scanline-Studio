using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(ImageExportSettings))]
public sealed partial class ImageExportSettingsJsonContext : JsonSerializerContext;
