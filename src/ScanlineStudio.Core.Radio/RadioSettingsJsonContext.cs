using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Radio;

[JsonSerializable(typeof(RadioConnectionSettings))]
public sealed partial class RadioSettingsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(RadioSafetySettings))]
public sealed partial class RadioSafetySettingsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(FrequencyPresetsSettings))]
public sealed partial class FrequencyPresetsSettingsJsonContext : JsonSerializerContext;
