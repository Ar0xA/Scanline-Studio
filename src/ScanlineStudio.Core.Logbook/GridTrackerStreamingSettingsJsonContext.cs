using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Logbook;

[JsonSerializable(typeof(GridTrackerStreamingSettings))]
public sealed partial class GridTrackerStreamingSettingsJsonContext : JsonSerializerContext;
