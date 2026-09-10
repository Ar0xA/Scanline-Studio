namespace ScanlineStudio.Abstractions.Radio;

/// <summary>One user-defined quick-jump entry (frequency + mode set together in one action) — the
/// frequency/status strip's memory-button row, like WSJT-X's band shortcuts. Lives in
/// <c>ScanlineStudio.Abstractions</c> (not alongside <c>FrequencyPresetsSettings</c>'s persistence
/// machinery in <c>ScanlineStudio.Core.Radio</c>) so <c>ScanlineStudio.UI</c> can bind against this
/// plain data shape directly without referencing a <c>ScanlineStudio.Core.*</c> concrete assembly.
///
/// <para><see cref="BandwidthHz"/> is the rig filter width this entry requests, applied AFTER the
/// mode (the mode set is what resets the rig's passband, so bandwidth has to follow it).
/// <see langword="null"/> means "this entry predates the bandwidth field" and resolves to its mode
/// family's own default via <see cref="RadioModeFamilies"/> — 2400 Hz on SSB and data modes,
/// 15000 Hz on FM modes.</para>
///
/// <para>The null-on-upgrade guarantee comes from the parameter being <c>int?</c>: both
/// <c>default(int?)</c> and the declared default are <see langword="null"/>, so every
/// System.Text.Json path agrees, and an entry written before this field existed cannot deserialize
/// to a misleading 0. It does NOT come from "a constructor default beats a property initializer" —
/// for a NON-nullable parameter, source-gen's handling of constructor-parameter defaults is
/// version-dependent, so do not "simplify" this to <c>int BandwidthHz = 2400</c>.
/// <c>RadioState.BandwidthHz</c> is the in-repo precedent for this exact trailing-nullable
/// shape.</para></summary>
public sealed record FrequencyPreset(string Label, long FrequencyHz, RadioMode Mode, int? BandwidthHz = null);
