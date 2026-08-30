using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>A regular <see cref="FactAttribute"/> that skips itself when no real, system-installed
/// libhamlib is discoverable in this environment. xunit 2.5.3 has no conditional-skip mechanism of
/// its own; tests gated on <see cref="HamlibAvailabilityProbe.Runtime"/> previously used an
/// early-`return`, which xUnit reports as "passed," not "skipped" -- silently asserting nothing on
/// any CI leg without libhamlib installed. This attribute makes that state visible instead.</summary>
public sealed class RequiresHamlibFactAttribute : FactAttribute
{
    public RequiresHamlibFactAttribute()
    {
        if (!HamlibAvailabilityProbe.Runtime.Value.IsAvailable)
        {
            Skip = "Requires a real, system-installed libhamlib -- not available in this environment.";
        }
    }
}

/// <summary>Shared probe so <see cref="RequiresHamlibFactAttribute"/> and every real-interop test
/// class in this project check the exact same availability signal, computed once per test process
/// (constructing <see cref="HamlibRuntime"/> touches the filesystem/native loader, so this is
/// deliberately not repeated per test).</summary>
internal static class HamlibAvailabilityProbe
{
    public static readonly Lazy<IHamlibRuntime> Runtime = new(
        () => new HamlibRuntime(new NativeLibraryLoader(), overridePath: null));
}
