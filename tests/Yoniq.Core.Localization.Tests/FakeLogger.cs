using Microsoft.Extensions.Logging;

namespace Yoniq.Core.Localization.Tests;

/// <summary>Captures log entries for assertion, matching this project's established hand-rolled
/// `Fake*` test-double convention (no mocking library is used anywhere in this codebase).</summary>
internal sealed class FakeLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
