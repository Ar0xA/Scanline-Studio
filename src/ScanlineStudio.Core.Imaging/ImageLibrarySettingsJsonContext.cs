using System.Text.Json.Serialization;

namespace ScanlineStudio.Core.Imaging;

[JsonSerializable(typeof(ImageLibrarySettings))]
public sealed partial class ImageLibrarySettingsJsonContext : JsonSerializerContext;
