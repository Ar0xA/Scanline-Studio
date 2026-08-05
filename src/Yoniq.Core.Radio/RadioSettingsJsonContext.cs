using System.Text.Json.Serialization;

namespace Yoniq.Core.Radio;

[JsonSerializable(typeof(RadioConnectionSettings))]
public sealed partial class RadioSettingsJsonContext : JsonSerializerContext;
