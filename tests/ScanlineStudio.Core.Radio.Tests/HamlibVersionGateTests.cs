using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Pure string-parsing tests, no native call involved -- see spec/03-cat-layer.md's
/// "Version gate".</summary>
public class HamlibVersionGateTests
{
    [Theory]
    [InlineData("Hamlib 4.5.5 2024-01-01T00:00:00Z x86_64-pc-linux-gnu")]
    [InlineData("Hamlib 4.0.0 date arch")]
    [InlineData("Hamlib 4.99.99 date arch")]
    public void IsSupported_Major4_ReturnsTrue(string rigVersionOutput)
    {
        Assert.True(HamlibVersionGate.IsSupported(rigVersionOutput));
    }

    [Theory]
    [InlineData("Hamlib 5.0.0 2026-01-01T00:00:00Z x86_64-pc-linux-gnu")] // the 5.0-dev ABI break case
    [InlineData("Hamlib 3.3.0 date arch")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("Hamlib")] // only one token -- no version to parse
    [InlineData("Hamlib x.y.z date arch")] // non-numeric major
    public void IsSupported_UnsupportedOrUnparseable_ReturnsFalse(string rigVersionOutput)
    {
        Assert.False(HamlibVersionGate.IsSupported(rigVersionOutput));
    }

    [Fact]
    public void IsSupported_Null_ReturnsFalse()
    {
        Assert.False(HamlibVersionGate.IsSupported(null));
    }
}
