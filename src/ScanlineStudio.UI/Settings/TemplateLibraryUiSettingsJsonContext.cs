using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(TemplateLibraryUiSettings))]
public sealed partial class TemplateLibraryUiSettingsJsonContext : JsonSerializerContext;
