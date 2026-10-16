using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

[JsonSerializable(typeof(DrmSettings))]
public sealed partial class DrmSettingsJsonContext : JsonSerializerContext;
