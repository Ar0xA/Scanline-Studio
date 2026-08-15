using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Logbook;

[JsonSerializable(typeof(AdifUdpStreamingSettings))]
[JsonSerializable(typeof(LegacyGridTrackerStreamingSettings))]
public sealed partial class AdifUdpStreamingSettingsJsonContext : JsonSerializerContext;
