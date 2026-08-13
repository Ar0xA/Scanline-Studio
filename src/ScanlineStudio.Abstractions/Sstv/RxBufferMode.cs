namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Mirrors legacy's <c>sys.m_UseRxBuff</c> (`ComLib.h:257`, `.ini` key <c>Define/UseRxBuff</c>)
/// -- whether decoded lines are staged into a buffer as they arrive, backing a replay capability
/// (`Main.cpp:5581-5771`'s <c>UpdateSampFreq</c>/<c>RedrawSSTV</c>/<c>RedrawAdjustSync</c>) that
/// re-renders already-decoded lines after a sample-rate/offset/slant correction, without re-running
/// the demodulator (`Option.dfm`'s <c>RGRBuf</c> radio group, items <c>"NO"</c>/<c>"RAM"</c>/
/// <c>"ARCHIVO"</c>). Legacy's real compiled-in default is <see cref="On"/> (`Main.cpp:899`,
/// `sys.m_UseRxBuff=1`).
///
/// <see cref="On"/> and <see cref="Extended"/> are NOT interchangeable storage backends for the same
/// behavior -- legacy's <c>OpenCloseRxBuff</c> (`sstv.cpp:1626-1644`) allocates the RAM staging arrays
/// ONLY for <see cref="On"/>; anything other than <see cref="On"/> (i.e. <see cref="Off"/> too, not
/// just <see cref="Extended"/>) frees them, and <see cref="Extended"/> relies entirely on a
/// disk-backed scratch file instead (`CWaveStrage`, `Sound.h:150-173`), with no RAM cap but real
/// disk-I/O cost. <see cref="Off"/> is a true no-capture bypass -- but it is not merely "no replay
/// capability": legacy's source ALSO independently gates decode-path Auto-Sync/sync-search-averaging
/// behavior on this value even when no replay ever happens (`Main.cpp:3907`/`:3760`) -- every gating
/// site distinguishes <see cref="Off"/> from "not <see cref="Off"/>" (i.e. <see cref="On"/> OR
/// <see cref="Extended"/>), never <see cref="On"/> specifically. <b>Not yet wired in this port</b> --
/// this enum is threaded through the settings layer and every constructor (RX buffer subsystem
/// Phase 1-2) but no decode-path logic reads it yet; <c>AnalogFmSstvDecoder</c>'s own
/// <c>TryAutoSync</c>/sync-search-averaging doc comments still describe today's pre-Phase-3
/// behavior, not a contradiction to resolve -- Phase 3 is what makes this value real and updates
/// those comments to match.
///
/// Lives here (not <c>ScanlineStudio.Core.Sstv</c>) so <c>ScanlineStudio.Application</c>'s
/// <c>OptionsSnapshot</c> and <c>ScanlineStudio.UI</c>'s <c>OptionsWindowViewModel</c> can both
/// reference it without <c>ScanlineStudio.UI</c> needing a <c>ScanlineStudio.Core.*</c> project
/// reference (banned by <c>UiLayeringArchitectureTests</c>) -- placed here from the start, per the
/// lesson already learned twice in this project (<see cref="CwIdMode"/> then <see cref="DemodType"/>
/// both had to move from <c>Core.Sstv</c> after the fact).
///
/// Persisted as a plain integer (no <c>JsonStringEnumConverter</c>), matching legacy's own real
/// <c>UseRxBuff</c> integers on purpose. Never reorder/renumber -- doing so would silently remap every
/// existing user's saved choice to a different mode on next load.
///
/// <see cref="Off"/>'s value is deliberately <c>0</c>, the same as the CLR's own default -- which is
/// exactly why <c>SstvDecoderSettings.RxBufferMode</c> is <c>RxBufferMode?</c> (nullable), not a plain
/// <see cref="RxBufferMode"/>: a non-nullable field could never distinguish "absent from
/// settings.json" (should clamp to the real default, <see cref="On"/>) from "user explicitly chose
/// Off," since both would deserialize to the same zero value. Same reasoning as
/// <see cref="RxBpfPreset"/>'s own <see cref="RxBpfPreset.Off"/>=0 case.</summary>
public enum RxBufferMode
{
    Off = 0,
    On = 1,
    Extended = 2,
}
