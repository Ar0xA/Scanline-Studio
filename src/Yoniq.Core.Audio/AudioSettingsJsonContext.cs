using System.Text.Json.Serialization;

namespace Yoniq.Core.Audio;

[JsonSerializable(typeof(AudioDeviceSettings))]
public sealed partial class AudioSettingsJsonContext : JsonSerializerContext;
