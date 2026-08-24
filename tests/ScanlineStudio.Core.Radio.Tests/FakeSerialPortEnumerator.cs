using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Scriptable <see cref="ISerialPortEnumerator"/> test double.</summary>
internal sealed class FakeSerialPortEnumerator : ISerialPortEnumerator
{
    public IReadOnlyList<string> PortNames { get; set; } = [];

    public IReadOnlyList<string> GetPortNames() => PortNames;
}
