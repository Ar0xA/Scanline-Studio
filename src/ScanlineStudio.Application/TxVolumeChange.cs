namespace ScanlineStudio.Application;

/// <summary>One committed Pwr change. <see cref="Version"/> increases in settings-commit order, so a
/// subscriber can drop an event that arrives after a newer one.</summary>
public sealed record TxVolumeChange(int Percent, long Version);
