using System.Text;

namespace ScanlineStudio.Host.Tests;

/// <summary>T1-18 (production_audit.md): confirms the CLAUDE.md §4-mandated
/// <see cref="CodePagesEncodingProvider"/> registration -- the same call
/// <c>Program.Main</c> makes -- actually resolves Windows-31J/CP932, the encoding legacy
/// Japanese-origin YONIQ source content needs. <c>Program.Main</c> itself is never invoked by
/// the test host, so this exercises the same API call directly rather than the composition
/// root's own line.</summary>
public sealed class CodePagesEncodingProviderTests
{
    [Fact]
    public void RegisterProvider_ThenGetEncoding932_ResolvesCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var encoding = Encoding.GetEncoding(932);

        Assert.Equal(932, encoding.CodePage);
    }
}
