using ScanlineStudio.Core.Drm;

namespace ScanlineStudio.Application;

/// <summary>DRM receive defaults for layers that may not reference <c>ScanlineStudio.Core.Drm</c> (the UI).</summary>
public static class DrmReceiveDefaults
{
    /// <summary>HamDRM RX runs beside SSTV RX unless turned off.</summary>
    public const bool Enabled = DrmRxSettings.DefaultRxEnabled;
}
