using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Logbook;

[JsonSerializable(typeof(QrzUploadSettings))]
public sealed partial class QrzUploadSettingsJsonContext : JsonSerializerContext;
