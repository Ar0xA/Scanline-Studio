using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Logbook;

[JsonSerializable(typeof(QrzLookupSettings))]
public sealed partial class QrzLookupSettingsJsonContext : JsonSerializerContext;
