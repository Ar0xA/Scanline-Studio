using System.Reflection;
using System.Xml.Linq;
using ScanlineStudio.UI;

namespace ScanlineStudio.UI.Tests;

/// <summary>Closes `spec/09-ui.md`'s Definition-of-done bullet: `ScanlineStudio.UI` may only depend on
/// `ScanlineStudio.Application`/`ScanlineStudio.Abstractions`, never a `ScanlineStudio.Core.*` concrete assembly directly. Two
/// checks, not one -- <see cref="Assembly.GetReferencedAssemblies"/> only reflects assemblies the
/// compiler actually kept a reference to (an unused `ProjectReference` can be elided), so a second
/// assertion reads `ScanlineStudio.UI.csproj`'s own declared `&lt;ProjectReference&gt;` items directly, which
/// is what spec/09-ui.md's own wording ("no project reference") literally asks for.
///
/// A hand-rolled reflection/XML check, not `NetArchTest.Rules` — the rule itself is one assertion,
/// not worth a fluent architecture-testing library and its `Mono.Cecil` dependency for
/// (`spec/13-testing.md` permits "NetArchTest (or equivalent)").</summary>
public sealed class UiLayeringArchitectureTests
{
    [Fact]
    public void ScanlineStudioUiAssembly_NeverReferencesAScanlineStudioCoreConcreteAssembly()
    {
        var referenced = typeof(App).Assembly.GetReferencedAssemblies();

        var violations = referenced.Where(a => a.Name is not null && a.Name.StartsWith("ScanlineStudio.Core.", StringComparison.Ordinal)).ToList();

        Assert.True(violations.Count == 0,
            $"ScanlineStudio.UI.dll references ScanlineStudio.Core.* assemblies directly: {string.Join(", ", violations.Select(v => v.Name))}");
    }

    [Fact]
    public void ScanlineStudioUiCsproj_DeclaresNoProjectReferenceToAScanlineStudioCoreProject()
    {
        var csprojPath = FindScanlineStudioUiCsproj();
        var document = XDocument.Load(csprojPath);
        var ns = document.Root!.Name.Namespace;

        var violations = document.Descendants(ns + "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(include => Path.GetFileNameWithoutExtension(include).StartsWith("ScanlineStudio.Core.", StringComparison.Ordinal))
            .ToList();

        Assert.True(violations.Count == 0,
            $"ScanlineStudio.UI.csproj declares a ProjectReference to a ScanlineStudio.Core.* project: {string.Join(", ", violations)}");
    }

    private static string FindScanlineStudioUiCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return Path.Combine(dir.FullName, "src", "ScanlineStudio.UI", "ScanlineStudio.UI.csproj");
    }
}
