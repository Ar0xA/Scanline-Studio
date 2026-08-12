namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Mirrors legacy's <c>CSSTVDEM::m_Type</c> (`sstv.cpp:2256-2269`, `.ini` key
/// <c>Define/DemType</c>) -- the main-picture FM demodulator algorithm, a real global user setting
/// (`Option.cpp`'s <c>RGDemType</c> radio group, 0=PLL/1=Zero-crossing/default=Hilbert). Legacy's
/// real compiled-in default is <see cref="Hilbert"/> (`sstv.cpp:1492`, `m_Type=2`), matching this
/// port's own pre-existing hardcoded behavior before the demod-type runtime-dispatch subsystem made
/// the other two live alternatives.
///
/// Lives here (not <c>ScanlineStudio.Core.Sstv</c>) so <c>ScanlineStudio.Application</c>'s
/// <c>OptionsSnapshot</c> and <c>ScanlineStudio.UI</c>'s <c>OptionsWindowViewModel</c> can both
/// reference it without <c>ScanlineStudio.UI</c> needing a <c>ScanlineStudio.Core.*</c> project
/// reference (banned by <c>UiLayeringArchitectureTests</c>) -- same reasoning as
/// <see cref="CwIdMode"/>'s own placement here, this time decided from the start rather than moved
/// here after the fact (see the demod-type subsystem's own implementation plan for that lesson).
///
/// Persisted as a plain integer (<c>SstvDecoderSettings.DemodType</c>'s JSON context uses no
/// <c>JsonStringEnumConverter</c>) -- these three member VALUES are therefore part of the on-disk
/// settings format, matching legacy's own real <c>Define/DemType</c> integers on purpose. Never
/// reorder/renumber them: doing so would silently remap every existing user's saved choice to a
/// different demodulator on next load.
///
/// <see cref="Pll"/>'s value is deliberately <c>0</c>, the same as the CLR's own default -- which is
/// exactly why <c>SstvDecoderSettings.DemodType</c> is <c>DemodType?</c> (nullable), not a plain
/// <see cref="DemodType"/>: a non-nullable field could never distinguish "absent from settings.json"
/// from "user explicitly chose PLL," since both would deserialize to the same zero value.
/// <c>CwIdMode.Off = 0</c> gets away with a non-nullable field only because its OWN desired absent-
/// value default happens to already be the zero member -- not a pattern to copy here.</summary>
public enum DemodType
{
    Pll = 0,
    ZeroCrossing = 1,
    Hilbert = 2,
}
