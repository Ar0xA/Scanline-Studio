using System.Text.Json.Serialization;

namespace ScanlineStudio.Application;

[JsonSerializable(typeof(OperatorSettings))]
public sealed partial class OperatorSettingsJsonContext : JsonSerializerContext;
