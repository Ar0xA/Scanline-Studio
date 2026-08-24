namespace ScanlineStudio.Application;

/// <summary>Process-wide performance tuning -- currently just OS scheduling priority. Lives
/// alongside <see cref="OperatorSettings"/> in the orchestration layer since it isn't owned by any
/// single hardware/DSP domain.
///
/// <b>Nullable, not a plain <see cref="System.Diagnostics.ProcessPriorityClass"/> with a
/// <c>= Normal</c> initializer</b> -- System.Text.Json does not honor property-initializer defaults
/// for <c>init</c>-only properties absent from the JSON payload (see
/// <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.CaptureThreadPriority</c>'s doc comment for the
/// full explanation of this STJ limitation). Here the desired "don't touch it" default already equals not
/// setting <see cref="System.Diagnostics.Process.PriorityClass"/> at all, so <c>null</c> is read at
/// the one call site (<c>ScanlineStudio.Host.Program</c>) as "leave the OS default alone" -- never
/// re-add a non-null default value here.</summary>
public sealed record AppPerformanceSettings
{
    public const string SectionKey = "AppPerformance";

    public System.Diagnostics.ProcessPriorityClass? ProcessPriority { get; init; }
}
