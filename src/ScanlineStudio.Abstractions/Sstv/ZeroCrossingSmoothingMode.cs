namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Mirrors legacy's <c>CFQC::m_Type</c> (`sstv.cpp:475-485`, Options dialog's <c>RGcrossType</c>
/// radio group, `.ini` key <c>Define/fqcType</c>) -- the zero-crossing frequency counter's OUTPUT
/// SMOOTHING STAGE. Entirely separate from <see cref="DemodType"/> despite legacy's shared field name
/// <c>m_Type</c> living on two different classes (<c>CFQC</c> here vs. <c>CSSTVDEM</c> there) -- do not
/// conflate the two.
///
/// Lives here (not <c>ScanlineStudio.Core.Sstv</c>), same reasoning as <see cref="DemodType"/>'s own
/// placement doc comment: <c>ScanlineStudio.Application</c>'s <c>OptionsSnapshot</c> and
/// <c>ScanlineStudio.UI</c>'s <c>OptionsWindowViewModel</c> both need this without a
/// <c>ScanlineStudio.Core.*</c> reference.
///
/// Persisted as a plain integer, matching legacy's own real <c>Define/fqcType</c> integers on purpose
/// -- never reorder/renumber these members.
///
/// Legacy's real dispatch default (`sstv.cpp:482`'s `default:` case) is <see cref="Off"/>, reached for
/// any out-of-range value -- NOT <see cref="Iir"/>, despite <see cref="DemodType"/>'s own unrelated
/// "default falls back to the third member" shape. The compiled-in absent-from-settings default is
/// <see cref="Iir"/> (<c>CFQC</c>'s own constructor, `sstv.cpp:349`, `m_Type=0`) -- these are two
/// different fallback rules for two different situations, not one shared rule.</summary>
public enum ZeroCrossingSmoothingMode
{
    Iir = 0,
    Fir = 1,
    Off = 2,
}
