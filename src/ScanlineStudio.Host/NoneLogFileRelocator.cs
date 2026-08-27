using ScanlineStudio.Settings;

namespace ScanlineStudio.Host;

/// <summary>DI fallback for <see cref="ILogFileRelocator"/> when no real <see cref="FileLoggerProvider"/>
/// exists (its construction failed at startup, console-only fallback -- see <c>Program.cs</c>).
/// Same no-op-fallback naming convention as <c>ScanlineStudio.Core.Radio.NoneRadioProtocol</c> --
/// there is no live log file to relocate, so success is the honest answer, not a swallowed
/// failure.</summary>
public sealed class NoneLogFileRelocator : ILogFileRelocator
{
    public Task<bool> RelocateAsync(string newDirectory, CancellationToken ct = default) => Task.FromResult(true);
}
