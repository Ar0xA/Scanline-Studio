using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Audio;

[JsonSerializable(typeof(AudioDeviceSettings))]
public sealed partial class AudioSettingsJsonContext : JsonSerializerContext;
