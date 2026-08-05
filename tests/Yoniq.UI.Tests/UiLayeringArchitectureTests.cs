using System.Reflection;
using System.Xml.Linq;
using Yoniq.UI;

namespace Yoniq.UI.Tests;

/// <summary>Closes `spec/09-ui.md`'s Definition-of-done bullet: `Yoniq.UI` may only depend on
/// `Yoniq.Application`/`Yoniq.Abstractions`, never a `Yoniq.Core.*` concrete assembly directly. Two
/// checks, not one -- <see cref="Assembly.GetReferencedAssemblies"/> only reflects assemblies the
/// compiler actually kept a reference to (an unused `ProjectReference` can be elided), so a second
/// assertion reads `Yoniq.UI.csproj`'s own declared `&lt;ProjectReference&gt;` items directly, which
/// is what spec/09-ui.md's own wording ("no project reference") literally asks for.
///
/// A hand-rolled reflection/XML check, not `NetArchTest.Rules` — the rule itself is one assertion,
/// not worth a fluent architecture-testing library and its `Mono.Cecil` dependency for
/// (`spec/13-testing.md` permits "NetArchTest (or equivalent)").</summary>
public sealed class UiLayeringArchitectureTests
{
    [Fact]
    public void YoniqUiAssembly_NeverReferencesAYoniqCoreConcreteAssembly()
    {
        var referenced = typeof(App).Assembly.GetReferencedAssemblies();

        var violations = referenced.Where(a => a.Name is not null && a.Name.StartsWith("Yoniq.Core.", StringComparison.Ordinal)).ToList();

        Assert.True(violations.Count == 0,
            $"Yoniq.UI.dll references Yoniq.Core.* assemblies directly: {string.Join(", ", violations.Select(v => v.Name))}");
    }

    [Fact]
    public void YoniqUiCsproj_DeclaresNoProjectReferenceToAYoniqCoreProject()
    {
        var csprojPath = FindYoniqUiCsproj();
        var document = XDocument.Load(csprojPath);
        var ns = document.Root!.Name.Namespace;

        var violations = document.Descendants(ns + "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(include => Path.GetFileNameWithoutExtension(include).StartsWith("Yoniq.Core.", StringComparison.Ordinal))
            .ToList();

        Assert.True(violations.Count == 0,
            $"Yoniq.UI.csproj declares a ProjectReference to a Yoniq.Core.* project: {string.Join(", ", violations)}");
    }

    private static string FindYoniqUiCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yoniq.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Yoniq.sln) from test output directory.");
        }

        return Path.Combine(dir.FullName, "src", "Yoniq.UI", "Yoniq.UI.csproj");
    }
}
