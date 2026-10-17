using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Drm;

[JsonSerializable(typeof(DrmRxSettings))]
public sealed partial class DrmRxSettingsJsonContext : JsonSerializerContext;
