using System.Text.Json.Serialization;

namespace ScanlineStudio.Application;

[JsonSerializable(typeof(AppPerformanceSettings))]
public sealed partial class AppPerformanceSettingsJsonContext : JsonSerializerContext;
