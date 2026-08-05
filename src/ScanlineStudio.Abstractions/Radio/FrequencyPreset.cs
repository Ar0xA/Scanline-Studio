namespace ScanlineStudio.Abstractions.Radio;

/// <summary>One user-defined quick-jump entry (frequency + mode set together in one action) — the
/// frequency/status strip's memory-button row, like WSJT-X's band shortcuts. Lives in
/// <c>ScanlineStudio.Abstractions</c> (not alongside <c>FrequencyPresetsSettings</c>'s persistence
/// machinery in <c>ScanlineStudio.Core.Radio</c>) so <c>ScanlineStudio.UI</c> can bind against this
/// plain data shape directly without referencing a <c>ScanlineStudio.Core.*</c> concrete assembly.</summary>
public sealed record FrequencyPreset(string Label, long FrequencyHz, RadioMode Mode);
