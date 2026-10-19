namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Why <see cref="IImageFileLoader"/> could not load a file.</summary>
public enum ImageLoadFailure
{
    /// <summary>Not an image format the loader reads.</summary>
    NotAnImage,

    /// <summary>A recognised format whose content is damaged or truncated.</summary>
    Corrupt,

    /// <summary>More pixels than the loader accepts, or too large to allocate.</summary>
    TooLarge,

    /// <summary>The file could not be opened or read (missing, locked, no permission).</summary>
    Unreadable,
}

/// <summary>Thrown by <see cref="IImageFileLoader"/> for every load failure except cancellation, so callers
/// can tell the reasons apart without depending on the decoder library's exception types.</summary>
public sealed class ImageLoadException : IOException
{
    public ImageLoadException(ImageLoadFailure reason, string path, Exception? innerException = null)
        : base($"Could not load image '{path}': {reason}", innerException)
    {
        Reason = reason;
        Path = path;
    }

    public ImageLoadFailure Reason { get; }

    public string Path { get; }
}
