using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Maps a decoder line anchor (<c>_consumedSamples</c>, which lives in the delayed 2-tap → search-BPF
/// timeline) back to where that line's sync pulse starts in the RAW input timeline, so the SNR
/// estimator can read the undistorted pulse from <c>_rawSamples</c>.
/// <para>raw sync start = anchor + sync offset − (0.5 + BPF tap/2) − (Hilbert ? HalfTap/4 : 0) + r(mode).
/// The anchor comes from the sync-envelope fold on the bandpass timeline plus m_OFP plus the Hilbert
/// HalfTap/4 term only — the full demodulator delay never enters it, so only those two chain terms are
/// removed. r(mode) is the per-mode residual the step-0 placement probe (<c>SyncSnrPlacementProbe</c>)
/// measures.</para>
/// </summary>
internal static class SyncSnrPlacement
{
    /// <summary>Edge margin trimmed from both ends of the sync pulse before measuring, in ms.</summary>
    internal const double EdgeMarginMs = 0.75;

    /// <summary>Returns the raw-timeline sample index (fractional) where the tracked sync pulse starts.</summary>
    /// <param name="anchor">The line's own start cursor, before it is advanced.</param>
    /// <param name="syncOffsetMs"><see cref="SstvModeRegistry.GetSyncSegmentOffsetMs"/>.</param>
    /// <param name="effectiveSampleRate">The line's own slant-corrected rate.</param>
    /// <param name="bandpassTap">Live search-BPF tap count; 0 when the preset is Off.</param>
    /// <param name="hilbertAnchorTerm">The decoder's own <c>HalfTap / 4</c> integer, 0 unless Hilbert.</param>
    /// <param name="residualMs">r(mode), <see cref="GetResidualMs"/>.</param>
    internal static double RawSyncStartSample(
        int anchor,
        double syncOffsetMs,
        int effectiveSampleRate,
        int bandpassTap,
        int hilbertAnchorTerm,
        double residualMs)
    {
        var chainDelay = 0.5 + (bandpassTap / 2) + hilbertAnchorTerm;
        return anchor + ((syncOffsetMs + residualMs) / 1000.0 * effectiveSampleRate) - chainDelay;
    }

    /// <summary>r(mode) in ms. Always 0 for now: the step-0 probe found the residual varies with sample
    /// rate and RX BPF preset, not just mode, so no per-mode table can be pinned (plan stopped at step 0).</summary>
    internal static double GetResidualMs(SstvModeDefinition mode) => 0.0;
}
