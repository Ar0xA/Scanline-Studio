using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Radio;

[JsonSerializable(typeof(RadioConnectionSettings))]
public sealed partial class RadioSettingsJsonContext : JsonSerializerContext;
