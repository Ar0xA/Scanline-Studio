using System.Text.Json.Serialization;

namespace Yoniq.Core.Localization;

[JsonSerializable(typeof(LocalizationSettings))]
public sealed partial class LocalizationSettingsJsonContext : JsonSerializerContext;
