using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Localization;

[JsonSerializable(typeof(LocalizationSettings))]
public sealed partial class LocalizationSettingsJsonContext : JsonSerializerContext;
