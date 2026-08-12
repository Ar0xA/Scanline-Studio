using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Sstv;

[JsonSerializable(typeof(StationIdSettings))]
public sealed partial class StationIdSettingsJsonContext : JsonSerializerContext;
