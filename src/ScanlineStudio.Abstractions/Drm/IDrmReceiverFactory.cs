namespace ScanlineStudio.Abstractions.Drm;

/// <summary>Builds <see cref="IDrmReceiver"/> instances.</summary>
public interface IDrmReceiverFactory
{
    /// <summary>Whether <paramref name="captureRate"/> is supported (8000-192000 Hz).</summary>
    bool IsSupportedRate(int captureRate);

    /// <summary>A new receiver at <paramref name="captureRate"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not supported.</exception>
    IDrmReceiver Create(int captureRate);
}
