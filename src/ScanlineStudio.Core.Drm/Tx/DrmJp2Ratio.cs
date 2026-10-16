namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>QSSTV's JPEG 2000 compression ratio for a target file size (imageviewer.cpp:1045-1059).</summary>
public static class DrmJp2Ratio
{
    /// <summary><c>byteCount = (RGB32 bytes × 3) / 4</c>, i.e. width × height × 3, then
    /// <c>compRatio = byteCount / compressSize</c> in integer division. A target at or above the raw size
    /// gives 0, which OpenJPEG treats as lossless.</summary>
    public static int FromTargetSize(int width, int height, int targetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBytes);

        // QImage::Format_RGB32 rows are width × 4 bytes, already 4-byte aligned.
        long byteCount = (long)width * height * 4 * 3 / 4;
        long ratio = byteCount / targetBytes;
        return ratio > int.MaxValue ? int.MaxValue : (int)ratio;
    }
}
