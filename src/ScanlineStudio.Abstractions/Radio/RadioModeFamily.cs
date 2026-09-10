namespace ScanlineStudio.Abstractions.Radio;

/// <summary>The two groups of <see cref="RadioMode"/> that need different filter widths for SSTV.
/// See <see cref="RadioModeFamilies"/> for the widths themselves and for why a single app-wide
/// default cannot serve both.</summary>
public enum RadioModeFamily
{
    /// <summary>SSB and its data variants. SSTV tones sit near 1200-2300 Hz and fit an ordinary SSB
    /// filter.</summary>
    Ssb,

    /// <summary>FM and its data variant. FM SSTV needs a filter an order of magnitude wider.</summary>
    Fm,
}
