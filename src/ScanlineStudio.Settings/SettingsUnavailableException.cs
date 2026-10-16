namespace ScanlineStudio.Settings;

/// <summary>Thrown by a settings write while the last load could not read the settings file: writing
/// would replace the user's real settings with defaults. Derives from <see cref="IOException"/>, a
/// failure mode every settings caller already handles.</summary>
public sealed class SettingsUnavailableException : IOException
{
    public SettingsUnavailableException()
    {
    }

    public SettingsUnavailableException(string message)
        : base(message)
    {
    }

    public SettingsUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
