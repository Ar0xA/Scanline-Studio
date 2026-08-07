using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Sstv;

[JsonSerializable(typeof(SstvDecoderSettings))]
public sealed partial class SstvDecoderSettingsJsonContext : JsonSerializerContext;
