using System.Diagnostics;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>A regular <see cref="FactAttribute"/> that skips itself when no real `rigctld` binary is
/// on PATH. xunit 2.5.3 has no conditional-skip mechanism of its own; tests gated on
/// <see cref="RigctldAvailabilityProbe.IsAvailable"/> previously used an early-`return`, which xUnit
/// reports as "passed," not "skipped" -- silently asserting nothing on any CI leg without `rigctld`
/// installed. This attribute makes that state visible instead.</summary>
public sealed class RequiresRigctldFactAttribute : FactAttribute
{
    public RequiresRigctldFactAttribute()
    {
        if (!RigctldAvailabilityProbe.IsAvailable.Value)
        {
            Skip = "Requires a real `rigctld` binary on PATH -- not available in this environment.";
        }
    }
}

/// <summary>Shared probe so <see cref="RequiresRigctldFactAttribute"/> and
/// <see cref="RigctldDummyRigIntegrationTests"/> check the exact same availability signal, computed
/// once per test process.</summary>
internal static class RigctldAvailabilityProbe
{
    public static readonly Lazy<bool> IsAvailable = new(Probe);

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("rigctld", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(2000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }
}
