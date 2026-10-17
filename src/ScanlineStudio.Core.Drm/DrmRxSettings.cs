namespace ScanlineStudio.Core.Drm;

/// <summary>Persisted DRM receive settings. A section of its own: the UI's TX <c>DrmSettings</c> lives in
/// ScanlineStudio.UI, which Application cannot reference, and two record types on one section key would drop each
/// other's fields on write.</summary>
/// <remarks>Fields are nullable because System.Text.Json ignores an init-only property's initializer when the key is
/// absent; every read site applies the documented default with <c>??</c>.</remarks>
public sealed record DrmRxSettings
{
    public const string SectionKey = "DrmRx";

    /// <summary>DRM RX runs beside SSTV RX unless turned off.</summary>
    public const bool DefaultRxEnabled = true;

    /// <summary>Whether DRM RX runs. <see langword="null"/> means <see cref="DefaultRxEnabled"/>.</summary>
    public bool? RxEnabled { get; init; }
}
