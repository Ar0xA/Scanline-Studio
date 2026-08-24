using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Tests against the REAL <see cref="SerialPortEnumerator"/> -- this sandbox can't assert
/// real ports exist (CI has none), so this only guards the "never throws, never crashes the load
/// path" contract <see cref="ISerialPortEnumerator"/>'s own doc comment promises.</summary>
public class SerialPortEnumeratorTests
{
    [Fact]
    public void GetPortNames_NeverThrows_ReturnsAList()
    {
        var sut = new SerialPortEnumerator();

        var names = sut.GetPortNames();

        Assert.NotNull(names);
    }
}
