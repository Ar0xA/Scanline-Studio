namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Turns a received file's bytes (a DRM/EasyPal transfer) into pixels for the gallery.</summary>
public interface IReceivedImageDecoder
{
    /// <summary>Decodes a received file's bytes: JP2/J2K through <see cref="IJpeg2000Codec"/>, JPEG/PNG/BMP/GIF
    /// through ImageSharp, chosen by content, never by name. Returns null when the bytes are not a decodable
    /// image or the codec is unavailable (logged as a Warning); never throws for bad data.</summary>
    IImageSource? TryDecode(ReadOnlySpan<byte> data);
}
