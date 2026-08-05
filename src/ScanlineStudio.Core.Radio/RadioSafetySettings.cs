namespace ScanlineStudio.Core.Radio;

/// <summary>Gates the SWR auto-cutoff safety monitor (see spec/04-rigctld.md's telemetry section) —
/// disabled by default since it depends on the connected rig actually exposing an SWR meter reading
/// over CAT (rig-dependent, not guaranteed), and a false-positive cutoff mid-transmission would be
/// worse than no cutoff at all for a rig this hasn't been verified against.</summary>
public sealed record RadioSafetySettings
{
    public const string SectionKey = "RadioSafety";

    public bool SwrCutoffEnabled { get; init; }

    /// <summary>SWR ratio (e.g. 3.0 = 3:1) above which an in-progress transmit is cancelled.</summary>
    public double SwrCutoffThreshold { get; init; } = 3.0;
}
