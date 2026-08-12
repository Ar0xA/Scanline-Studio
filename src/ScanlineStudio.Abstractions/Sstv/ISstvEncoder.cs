using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Abstractions.Sstv;

public interface ISstvEncoder
{
    int SampleRate { get; }

    /// <summary>
    /// Encodes <paramref name="image"/> as <paramref name="mode"/>'s SSTV audio. Implementations must
    /// reject an <paramref name="image"/> whose dimensions don't exactly match
    /// <c>mode.ImageWidth</c>/<c>mode.ImageHeight</c> with an <see cref="ArgumentException"/> thrown
    /// synchronously from this call (not deferred to enumeration) -- callers must not rely on a
    /// mismatched image being silently cropped or padded.
    ///
    /// <paramref name="stationId"/> defaults to <see langword="null"/> (treated identically to
    /// <see cref="StationIdTransmitOptions.None"/> -- nothing enabled), so every pre-existing caller
    /// that doesn't know about the CW-ID/FSK station-ID feature keeps producing byte-identical output.
    /// Placed before <paramref name="ct"/> (not after, despite <c>ct</c> historically being this
    /// method's last parameter) so the many existing 2-arg call sites (<c>EncodeAsync(mode, image)</c>)
    /// are entirely unaffected by this addition.
    /// </summary>
    IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        CancellationToken ct = default);
}
