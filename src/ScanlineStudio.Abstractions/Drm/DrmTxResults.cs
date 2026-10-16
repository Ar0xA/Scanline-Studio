namespace ScanlineStudio.Abstractions.Drm;

/// <summary>A DRM transmission that went out: the JPEG 2000 size and the stamped on-air name.</summary>
public sealed record DrmTxResult(int Jp2Bytes, string OnAirName);

/// <summary>A pre-transmit DRM estimate. <see cref="Jp2"/> is kept so a profile change can recount without
/// re-encoding. <see cref="Duration"/> is the on-air length (VOX tone included), as the TX progress shows it.</summary>
public sealed record DrmEstimate(byte[] Jp2, TimeSpan Duration)
{
    public int Jp2Bytes => Jp2.Length;
}
