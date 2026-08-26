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

    // UseShellExecute: true -- the standard cross-platform way to hand a URL/path to the OS's own
    // default-handler resolution (Windows ShellExecute, macOS `open`, Linux `xdg-open` under the
    // hood in .NET's own Process.Start implementation) -- a URL opens in the default browser, a
    // folder path opens in the default file manager, no branching needed here for which one this
    // call is. Best-effort: no browser/file-manager configured, no desktop environment, or a
    // malformed input all surface as a caught exception here rather than an unhandled crash from a
    // menu click.
    public void Open(string urlOrPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(urlOrPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.OpenFailed(_logger, urlOrPath, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open in the OS shell: {UrlOrPath}")]
        public static partial void OpenFailed(ILogger logger, string urlOrPath, Exception ex);
    }
}
