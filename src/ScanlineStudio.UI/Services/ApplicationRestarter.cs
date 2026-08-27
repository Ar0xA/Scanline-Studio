using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.UI.Services;

/// <summary>See <see cref="IApplicationRestarter"/>. Deliberately NOT <see cref="IDisposable"/> --
/// see that interface's own doc comment for why.</summary>
public sealed partial class ApplicationRestarter : IApplicationRestarter
{
    private readonly ILogger<ApplicationRestarter> _logger;

    public ApplicationRestarter(ILogger<ApplicationRestarter> logger)
    {
        _logger = logger;
    }

    public bool RestartRequested { get; set; }

    public bool StartNewInstance()
    {
        var startInfo = BuildRestartStartInfo(Environment.ProcessPath, Environment.GetCommandLineArgs());
        if (startInfo is null)
        {
            Log.NoProcessPath(_logger);
            return false;
        }

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.StartFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>Pure and unit-testable on purpose -- separated from <see cref="StartNewInstance"/>
    /// so tests can exercise the argument-building logic without actually spawning a process.
    ///
    /// Under the published apphost, <paramref name="processPath"/> is this app's own executable
    /// and <c>commandLineArgs[0]</c> duplicates it (the real arguments start at index 1). Under
    /// `dotnet ScanlineStudio.Host.dll ...` (round-2 finding), <paramref name="processPath"/> is
    /// the `dotnet` muxer itself, not this app -- naively dropping <c>commandLineArgs[0]</c> in
    /// that case would relaunch the bare SDK CLI with stray arguments; instead
    /// <c>commandLineArgs[0]</c> (the managed dll path) is re-prepended as
    /// <c>dotnet &lt;dll&gt; &lt;rest of the original args&gt;</c>.</summary>
    public static ProcessStartInfo? BuildRestartStartInfo(string? processPath, string[] commandLineArgs)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(processPath);
        var isDotnetMuxer = string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(processPath) { UseShellExecute = false };

        if (isDotnetMuxer && commandLineArgs.Length > 0)
        {
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        }

        for (var i = 1; i < commandLineArgs.Length; i++)
        {
            startInfo.ArgumentList.Add(commandLineArgs[i]);
        }

        return startInfo;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Cannot restart: Environment.ProcessPath is unavailable")]
        public static partial void NoProcessPath(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to start a new Scanline Studio instance for restart")]
        public static partial void StartFailed(ILogger logger, Exception ex);
    }
}
