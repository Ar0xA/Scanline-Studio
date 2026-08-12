namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Mirrors legacy's <c>CSSTVDEM::m_bpf</c> (`sstv.cpp:1522-1550`'s <c>CalcBPF</c>, `.ini` key
/// <c>DEMBPF</c>) -- the RX bandpass-filter sharpness applied ahead of the AGC/sync-envelope-detector
/// pipeline (`Option.cpp`'s <c>RGRxBPF</c> radio group, 0=Off/1=Wide/2=Narrow/3=VeryNarrow). Legacy's
/// real compiled-in default is <see cref="Wide"/> (`sstv.cpp:1416`, `m_bpf=1`).
///
/// <see cref="Off"/> is a true bypass (`sstv.cpp:1826`'s `if(m_bpf){...}` gate), not a zero-width/
/// zero-tap filter -- <c>SearchBandpassFilter</c> is never constructed for it.
/// <see cref="Narrow"/>/<see cref="VeryNarrow"/> both require attenuation &gt;= 21dB on the locked
/// filter, routing <c>SearchBandpassFilter.MakeFilter</c> through its Kaiser-Bessel-window branch.
///
/// Lives here (not <c>ScanlineStudio.Core.Sstv</c>) so <c>ScanlineStudio.Application</c>'s
/// <c>OptionsSnapshot</c> and <c>ScanlineStudio.UI</c>'s <c>OptionsWindowViewModel</c> can both
/// reference it without <c>ScanlineStudio.UI</c> needing a <c>ScanlineStudio.Core.*</c> project
/// reference (banned by <c>UiLayeringArchitectureTests</c>) -- placed here from the start, per the
/// lesson already learned twice in this project (<see cref="CwIdMode"/> then <see cref="DemodType"/>
/// both had to move from <c>Core.Sstv</c> after the fact).
///
/// Persisted as a plain integer (no <c>JsonStringEnumConverter</c>), matching legacy's own real
/// <c>DEMBPF</c> integers on purpose. Never reorder/renumber -- doing so would silently remap every
/// existing user's saved choice to a different filter on next load.
///
/// <see cref="Off"/>'s value is deliberately <c>0</c>, the same as the CLR's own default -- which is
/// exactly why <c>SstvDecoderSettings.RxBpfPreset</c> is <c>RxBpfPreset?</c> (nullable), not a plain
/// <see cref="RxBpfPreset"/>: a non-nullable field could never distinguish "absent from
/// settings.json" (should clamp to the real default, <see cref="Wide"/>) from "user explicitly chose
/// Off," since both would deserialize to the same zero value. Same reasoning as
/// <see cref="DemodType"/>'s own <see cref="DemodType.Pll"/>=0 case.</summary>
public enum RxBpfPreset
{
    Off = 0,
    Wide = 1,
    Narrow = 2,
    VeryNarrow = 3,
}
