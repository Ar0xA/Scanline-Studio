namespace ScanlineStudio.Settings;

/// <summary>What happened when a settings load found a damaged or unreadable file. Separate from
/// <see cref="ISettingsStore"/> so its fakes stay unchanged, same as <see cref="ISettingsFileRelocator"/>.</summary>
public interface ISettingsRecoveryNotice
{
    /// <summary>Returns the latest notice once and clears it; null when there is nothing to report.
    /// Safe from any thread.</summary>
    SettingsRecoveryNotice? TakeNotice();
}

public enum SettingsRecoveryKind
{
    /// <summary>settings.json was damaged or missing and the previous save's copy was restored.</summary>
    Restored,

    /// <summary>settings.json was damaged or missing and no usable backup existed: defaults are in use.</summary>
    ResetToDefaults,

    /// <summary>A settings file could not be read: defaults (or the backup's data) are shown and every
    /// change is refused until the file can be read again.</summary>
    Unavailable,
}

/// <param name="Path">The settings file, or for <see cref="SettingsRecoveryKind.Unavailable"/> the file that
/// could not be read or moved.</param>
public sealed record SettingsRecoveryNotice(SettingsRecoveryKind Kind, string Path);
