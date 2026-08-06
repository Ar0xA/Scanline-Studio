using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Host;

/// <summary>Minimal file <see cref="ILoggerProvider"/> — appends timestamped, leveled lines to a
/// fixed path across launches (a crashed/killed prior run's lines must survive to the next launch,
/// or they're unrecoverable evidence for exactly the kind of bug this file exists to diagnose).
/// Exists alongside the console provider `Host.CreateApplicationBuilder` already registers by
/// default, since capturing this app's console output through a background shell has proven
/// unreliable in practice (a process that crashes before flushing loses everything); a file on
/// disk survives that and can be read directly at any time regardless of which shell session
/// launched the process.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger(string categoryName, StreamWriter writer, object writerLock) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel,-11}] {categoryName}: {formatter(state, exception)}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (writerLock)
        {
            writer.WriteLine(line);
        }
    }
}
