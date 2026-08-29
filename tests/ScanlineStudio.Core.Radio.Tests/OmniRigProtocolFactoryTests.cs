using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.OmniRig;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed class OmniRigProtocolFactoryTests
{
    private static OmniRigProtocolFactory CreateSut() => new(NullLoggerFactory.Instance);

    [Fact]
    public void CanHandle_OmniRigConnectionSpec_ReturnsTrue() =>
        Assert.True(CreateSut().CanHandle(new OmniRigConnectionSpec()));

    [Theory]
    [InlineData(typeof(NoneConnectionSpec))]
    [InlineData(typeof(FlrigConnectionSpec))]
    public void CanHandle_OtherConnectionSpec_ReturnsFalse(Type specType)
    {
        var spec = specType == typeof(FlrigConnectionSpec)
            ? new FlrigConnectionSpec("127.0.0.1", 12345)
            : (RadioConnectionSpec)new NoneConnectionSpec();

        Assert.False(CreateSut().CanHandle(spec));
    }

    [Fact]
    public void Create_WrongConnectionSpec_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => CreateSut().Create(new NoneConnectionSpec()));

    // This suite runs on Linux CI/dev boxes -- OmniRig is Windows-only, so Create() on the correct
    // spec type is expected to always hit the OperatingSystem.IsWindows() guard here, not the real
    // COM path (which cannot be exercised in this environment at all, see the implementation plan).
    [Fact]
    public void Create_OmniRigConnectionSpec_OnNonWindows_ThrowsPlatformNotSupportedException()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => CreateSut().Create(new OmniRigConnectionSpec()));
    }
}
