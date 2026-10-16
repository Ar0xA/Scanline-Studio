namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>JPEG 2000 (JP2) encode/decode, the image codec of DRM image transfer. Implemented over a
/// native library that may be missing on a given machine, so callers check <see cref="IsAvailable"/>
/// first; construction never fails. Methods are CPU-bound and synchronous: call them off the UI
/// thread. Thread-safe.</summary>
public interface IJpeg2000Codec
{
    /// <summary>Whether the native codec loaded. The first read triggers the load.</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false; null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>Encodes RGB24 pixels as a JP2 file with QSSTV's parameters (jp2io.cpp:408-483): one
    /// quality layer at <paramref name="compressionRatio"/>, reversible 5/3 wavelet, colour transform.
    /// A ratio of 0 is lossless.</summary>
    /// <param name="rgb24">Rows of R,G,B bytes, <paramref name="stride"/> bytes apart.</param>
    /// <exception cref="InvalidOperationException">The codec is unavailable or the encode failed.</exception>
    byte[] EncodeRgb24(ReadOnlySpan<byte> rgb24, int width, int height, int stride, int compressionRatio);

    /// <summary>Decodes a JP2 file or a raw J2K codestream. The header is read first, and an image over
    /// 16 megapixels, more than 4 components or more than 1024 tiles is refused before any pixel decode.</summary>
    /// <exception cref="InvalidOperationException">The codec is unavailable.</exception>
    /// <exception cref="InvalidDataException">The data is not JPEG 2000, is corrupt, exceeds a limit, or
    /// has a component layout this decoder does not convert to RGB.</exception>
    IImageSource Decode(ReadOnlySpan<byte> data);
}
