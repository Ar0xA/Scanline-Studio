using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 7: a regular <see cref="FactAttribute"/> that skips itself when this environment
/// can't actually run a real-audio-hardware test. xunit 2.5.3 has no conditional-skip mechanism of
/// its own, and every test in this project that shells out to pactl/ffmpeg/paplay against a real
/// PulseAudio/PipeWire-pulse virtual cable needs one: the CI matrix (.github/workflows/ci.yml runs
/// windows-latest, ubuntu-latest, macos-latest) would otherwise fail hard on Windows/macOS (no
/// pactl binary at all) and unreliably on Linux (not guaranteed to have a running audio server),
/// rather than reporting an honest, informative skip.
/// </summary>
public sealed class RequiresPipeWireFactAttribute : FactAttribute
{
    public RequiresPipeWireFactAttribute()
    {
        if (!PipeWireProbe.IsAvailable)
        {
            Skip = "Requires a running PulseAudio/PipeWire-pulse server reachable via pactl -- not available in this environment.";
        }
    }
}

internal static class PipeWireProbe
{
    /// <summary>Computed once per test process (not once per attribute instance -- xunit
    /// constructs a fresh attribute instance per discovered test, and shelling out to pactl for
    /// every single one would be wasteful and could itself flake under load).</summary>
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        // The virtual-cable tests are written specifically against pactl/ffmpeg/paplay, all
        // Linux/PulseAudio-specific -- not merely "any audio backend," so non-Linux is an
        // unconditional skip here, not a probe. Real WASAPI/CoreAudio device coverage is a
        // separate, still-pending concern (piece Audio 9's "document unverified legs").
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pactl",
                ArgumentList = { "info" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // A real server that isn't hung answers instantly; a genuinely absent/unreachable one
            // (e.g. no PULSE_SERVER, socket doesn't exist) fails fast too -- this timeout exists
            // only to guarantee test discovery itself can never hang, not to tolerate a slow server.
            var exited = process.WaitForExit(2000);
            return exited && process.ExitCode == 0;
        }
        catch
        {
            return false; // pactl not on PATH, or any other environment failure -- treat as unavailable
        }
    }
}
