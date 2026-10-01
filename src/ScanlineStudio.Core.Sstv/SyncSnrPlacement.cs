namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Centre of the search for a line's sync pulse in the RAW input timeline. The line anchor
/// (<c>_consumedSamples</c>) lives in the delayed 2-tap → search-BPF timeline, so the chain delay is
/// removed: centre = anchor + sync offset − (0.5 + BPF tap/2) − (Hilbert ? HalfTap/4 : 0) + <see cref="CentreBiasMs"/>.
/// <para>The step-0 placement probe (<c>SyncSnrPlacementProbe</c>) showed this formula cannot place the
/// window by itself: the true sync start sits 0.36–3.25 ms EARLIER than the formula, depending on mode,
/// sample rate and RX BPF preset, but stable within a reception. So the formula is only the centre of a
/// ±<see cref="SearchHalfRangeMs"/> search; the window is placed by measurement (<see cref="SyncPulseLocator"/>,
/// <see cref="SyncSnrTracker"/>).</para>
/// </summary>
internal static class SyncSnrPlacement
{
    /// <summary>Middle of the measured 0.36–3.25 ms residual range; negative because the true sync
    /// start precedes the formula.</summary>
    internal const double CentreBiasMs = -1.8;

    /// <summary>Minimum half-width of the sync search around the centre.</summary>
    internal const double SearchHalfRangeMs = 2.0;

    /// <summary>Half-width actually searched: <see cref="SearchHalfRangeMs"/>, or half the pulse for pulses of
    /// <see cref="LongPulseMs"/> and more. Mistuning shifts the sync-envelope anchor by several ms on 20 ms (PD)
    /// pulses; on shorter pulses a wider range only lets neighbouring segments bias the location at low SNR
    /// (measured, docs/reception-snr-validation.md).</summary>
    internal static double SearchHalfRangeFor(double syncDurationMs) =>
        syncDurationMs >= LongPulseMs ? syncDurationMs / 2.0 : SearchHalfRangeMs;

    internal const double LongPulseMs = 15.0;

    /// <summary>Returns the raw-timeline sample index (fractional) the sync search is centred on.</summary>
    /// <param name="anchor">The line's own start cursor, before it is advanced.</param>
    /// <param name="syncOffsetMs"><see cref="SstvModeRegistry.GetSyncSegmentOffsetMs"/>.</param>
    /// <param name="effectiveSampleRate">The line's own slant-corrected rate.</param>
    /// <param name="bandpassTap">Live search-BPF tap count; 0 when the preset is Off.</param>
    /// <param name="hilbertAnchorTerm">The decoder's own <c>HalfTap / 4</c> integer, 0 unless Hilbert.</param>
    internal static double SearchCentreSample(
        int anchor,
        double syncOffsetMs,
        int effectiveSampleRate,
        int bandpassTap,
        int hilbertAnchorTerm)
    {
        var chainDelay = 0.5 + (bandpassTap / 2) + hilbertAnchorTerm;
        return anchor + ((syncOffsetMs + CentreBiasMs) / 1000.0 * effectiveSampleRate) - chainDelay;
    }
}
