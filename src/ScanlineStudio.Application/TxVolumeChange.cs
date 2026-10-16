namespace ScanlineStudio.Application;

/// <summary>Which remembered Pwr level a transmission uses. SSTV is a constant tone, while DRM peaks about 12-14 dB
/// above its average, so each keeps its own level.</summary>
public enum TxLevelFamily
{
    Sstv,
    Drm,
}

/// <summary>One committed Pwr change for one <see cref="Family"/>. <see cref="Version"/> increases in settings-commit
/// order across both families, so a subscriber can drop an event that arrives after a newer one.</summary>
public sealed record TxVolumeChange(TxLevelFamily Family, int Percent, long Version);
