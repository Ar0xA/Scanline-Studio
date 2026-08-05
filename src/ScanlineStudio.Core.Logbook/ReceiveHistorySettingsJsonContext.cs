using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Logbook;

[JsonSerializable(typeof(ReceiveHistorySettings))]
public sealed partial class ReceiveHistorySettingsJsonContext : JsonSerializerContext;
