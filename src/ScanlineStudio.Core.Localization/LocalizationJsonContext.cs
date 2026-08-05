using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Localization;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<LocaleManifestEntry>))]
internal sealed partial class LocalizationJsonContext : JsonSerializerContext;
