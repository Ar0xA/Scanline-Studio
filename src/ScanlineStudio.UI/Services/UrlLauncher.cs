using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.UI.Services;

public sealed partial class UrlLauncher : IUrlLauncher
{
    private readonly ILogger<UrlLauncher> _logger;

    public UrlLauncher(ILogger<UrlLauncher> logger)
    {
        _logger = logger;
    }

    // UseShellExecute: true -- the standard cross-platform way to hand a URL to the OS's own
    // default-browser resolution (Windows ShellExecute, macOS `open`, Linux `xdg-open` under the
    // hood in .NET's own Process.Start implementation). Best-effort: no browser configured, no
    // desktop environment, or a malformed URL all surface as a caught exception here rather than
    // an unhandled crash from a menu click.
    public void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.OpenFailed(_logger, url, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open URL in the default browser: {Url}")]
        public static partial void OpenFailed(ILogger logger, string url, Exception ex);
    }
}
