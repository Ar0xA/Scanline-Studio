using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 7: a regular <see cref="FactAttribute"/> that skips itself when this environment
/// can't actually run a real-audio-hardware test. xunit 2.5.3 has no conditional-skip mechanism of
/// its own, and every test in this project that shells out to pactl/ffmpeg/paplay against a real
/// PulseAudio/PipeWire-pulse virtual cable needs one: the CI matrix (.github/workflows/ci.yml runs
/// windows-latest, ubuntu-latest, macos-latest) would otherwise fail hard on Windows/macOS (no
/// pactl binary at all) and unreliably on Linux (not guaranteed to have a running audio server),
/// rather than reporting an honest, informative skip.
///
/// <b>Accepted coverage-risk position (Tier A Batch 1 functional-audit finding, 2026-08-19):</b>
/// every test gated by this attribute -- which includes ALL of this project's hardest concurrency
/// guarantees for <see cref="MiniAudioEngine"/>/<see cref="MiniAudioCaptureSession"/>/
/// <see cref="MiniAudioPlaybackSession"/> (the self-join guard, the contended-claim deadlock fix,
/// the reentrant-<c>DisposeAsync</c> deadlock fix, per-handler subscriber isolation, stereo TX/RX
/// channel routing, the underrun-padding fix, concurrent-use-and-dispose safety, the SSTV round
/// trip) -- silently skips on any CI runner other than a Linux one with a live PipeWire/PulseAudio
/// server. On this project's own actual CI matrix (windows-latest, ubuntu-latest, macos-latest, none
/// of which are documented as running such a server), that means these guarantees are validated
/// only where and when a contributor happens to run the suite on a suitably configured Linux
/// machine, not by the pipeline itself. This is a real, accepted gap, not an unnoticed one: fixing
/// it would mean either provisioning a real audio server in CI (a genuine infra change, out of
/// scope for a code-level audit finding) or rewriting this whole test class against a fake/mocked
/// backend, which would stop testing the real native P/Invoke boundary and SPSC contract these
/// tests exist specifically to exercise. Recorded here as the durable, written-down position the
/// audit's own gate asked for, rather than left as an implicit assumption.
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
            if (!exited)
            {
                // Opus-review fix: a timed-out pactl process was previously left running
                // (WaitForExit(timeout) just stops waiting, it doesn't kill anything) -- a leaked
                // process per test-discovery run against a wedged server. Kill it explicitly.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort: the process may have exited between the check above and here.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false; // pactl not on PATH, or any other environment failure -- treat as unavailable
        }
    }
}
