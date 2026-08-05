namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Piece 8b (of the Robot-36-at-11025Hz decode-gap fix, see spec/14-roadmap.md's live investigation
/// log): the pure fold-and-argmax computation from legacy's <c>TMmsstv::SyncSSTV</c>
/// (`Main.cpp:3751-3799`), extracted as a standalone, independently testable function before piece
/// 8c wires it into <see cref="AnalogFmSstvDecoder"/>'s decode lifecycle. Zero behavior change on
/// its own -- nothing calls this yet.
///
/// Legacy folds <c>e</c> transmission lines' worth of the sync-tone envelope (<c>m_B12</c>, the
/// same <c>d12</c>/<c>d19</c> value already used for sync/VIS detection, `sstv.cpp:2284-2307`)
/// modulo the line width into bins, takes the argmax bin, and derives a correction from it
/// (`Main.cpp:3766-3795`). This port's equivalent envelope source is the already-existing
/// <see cref="SyncEnvelopeDetector"/> (already reused by <c>AnalogFmSstvDecoder.ApplySlantTracking</c>
/// for a different, complementary purpose -- ongoing drift-RATE tracking, not this one-time
/// absolute-anchor correction).
///
/// <b>Sign derivation (verified against source, this is the single most failure-prone detail):</b>
/// legacy's raw fold result <c>n</c> is a PHASE, not a sample cursor -- <c>CSSTVDEM::Start</c>
/// zeroes <c>m_rBase</c> (`sstv.cpp:1725-1731`) at the same origin the fold itself starts counting
/// from, and <c>TMmsstv::DrawSSTVNormal</c> (`Main.cpp:4123-4148`) uses <c>n = m_rBase</c>,
/// incrementing per physical sample, to compute <c>ps = fmod(n, TW)</c>/<c>y = n/TW</c> -- i.e. for
/// physical sample index <c>k</c> (relative to the same origin), the phase is
/// <c>m_rBase_initial + k</c>. So the sample index where phase reaches zero (true line-0/pixel-0
/// start) is <c>k = -m_rBase_initial = -n</c>, and legacy computes
/// <c>n = -(argmaxBin - OFP) = OFP - argmaxBin</c> (`Main.cpp:3783-3784`). Therefore the correction
/// to apply to a SAMPLE-CURSOR variable (this port's <c>_consumedSamples</c>, playing the role of
/// legacy's <c>k</c>) is <c>-n = argmaxBin - OFP</c> -- the OPPOSITE sign of legacy's own literal
/// <c>n</c>. An earlier draft of this fix got this backwards (would have doubled the anchor error in
/// the wrong direction) -- caught by Opus plan-review before any code was written, then independently
/// re-derived and confirmed by tracing every use of <c>m_rBase</c> directly.
///
/// <b>No Scottie-only wraparound branch here (unlike legacy's own `Main.cpp:3785-3791`,
/// `if(n&lt;0) n+=SSTVSET.m_WD` for `smSCT1`/`smSCT2`/`smSCTDX`) -- deliberately dropped, not
/// missed.</b> That branch only makes sense under legacy's OWN indexing convention, where `m_OFP`
/// is measured from wherever legacy's internal decode considers "phase 0" -- which, for every mode
/// legacy special-cases, is implicitly pinned at/just-before the periodic tone this fold tracks
/// (that's the whole reason `argmaxBin` normally lands close to `m_OFP`). This port instead defines
/// its anchor (`_consumedSamples`) as the start of <c>SstvModeDefinition.LineSegments[0]</c> -- a
/// deliberate, previously-reviewed design choice (see <c>ScottieS1</c>'s own doc comment on
/// this file) that mirrors each mode's real TX wire order. For every mode except the Scottie family,
/// TX places its sync tone first, so both conventions agree and `argmaxBin` lands near `m_OFP` as
/// expected. Scottie's real TX order (`LineSCT`, Main.cpp:6620-6640) is
/// separator-G-separator-B-SYNC-separator-R -- the sync tone legacy (and this fold) tracks sits
/// roughly two-thirds into THIS port's own line-segment ordering, not at the start. Reusing legacy's
/// literal wraparound trick against this port's differently-anchored origin produced wildly wrong,
/// near-page-width-magnitude corrections (a real regression caught by this port's own full test
/// suite, not just the golden-vector target) -- independently confirmed via instrumented diagnostics:
/// the raw argmax for Scottie S1/S2/DX lands almost exactly at (cumulative duration of the
/// LineSegments preceding the tracked sync segment) + m_OFP, i.e. exactly where the tone physically
/// is. The caller (<see cref="AnalogFmSstvDecoder.TryResolveSyncAnchorCorrection"/>) now folds that
/// per-mode line-segment offset into <paramref name="syncPeakOffsetSamples"/> itself (computed
/// generically from <c>LineSegments</c>, not a new hardcoded table -- it's 0 for every mode whose
/// tracked sync segment is first, matching today's behavior exactly, and only nonzero for Scottie).
/// With that, plain <c>argmaxBin - syncPeakOffsetSamples</c> is already correct for every mode and
/// no wraparound is needed.
/// </summary>
internal static class SyncAnchorCorrector
{
    /// <summary>Computes the one-time correction (in samples) to add to a provisional anchor sample
    /// index, per legacy's <c>TMmsstv::SyncSSTV</c>. <paramref name="lineWidthSamples"/> is the
    /// mode's line width in samples (legacy's fractional <c>SSTVSET.m_TW</c> -- do NOT round this;
    /// the fold's per-page step below deliberately uses only its truncated integer part, matching
    /// legacy's own `int(SSTVSET.m_TW) + 2`/`m_WD` sizing, `Main.cpp:3764`/`sstv.cpp:594` --
    /// preserving the resulting sub-sample "creep" across pages is a faithful reproduction of
    /// legacy's real behavior, not a bug to smooth over). <paramref name="syncPeakOffsetSamples"/>
    /// is the EFFECTIVE expected offset (samples) from this port's own anchor
    /// (<c>LineSegments[0]</c>'s start) to where the tracked sync tone's filtered peak should appear
    /// -- legacy's real <c>m_OFP</c> (<see cref="SstvModeRegistry.GetSyncPeakOffsetMs"/>) plus this
    /// port's own line-segment-derived offset for modes (Scottie only, today) whose tracked sync
    /// segment isn't first; see this class's own doc comment. <paramref name="lineCount"/> is
    /// legacy's <c>e</c> (3 or 4, `Main.cpp:3759-3760`). <paramref name="envelopeAt"/> supplies the
    /// sync-envelope value at relative sample index <c>0..(lineCount*(int)lineWidthSamples)-1</c>,
    /// counted from the provisional anchor -- this port's flat sample arrays need no equivalent of
    /// legacy's page-strided ring-buffer indexing (`m_B12[pg*m_BWidth+i]`, `Main.cpp:3768`): that
    /// stride is purely a C++ circular-buffer memory layout detail, not part of the actual DSP
    /// algorithm, so a simple 0-based relative index is a faithful (not simplified) equivalent.
    ///
    /// This method itself omits legacy's Hilbert-demodulator tap adjustment (`if (m_Type==2)
    /// n -= m_hill.m_htap/4`, `Main.cpp:3794`) -- it stays scoped to the mode/argmax/wraparound
    /// arithmetic only. The Hilbert-specific term (now that this port's main picture demodulator is
    /// <see cref="HilbertFmDemodulator"/>, not PLL) is applied by the caller
    /// (<see cref="AnalogFmSstvDecoder.TryResolveSyncAnchorCorrection"/>) on top of this method's
    /// return value, using <see cref="HilbertFmDemodulator.HalfTap"/> -- see that call site for the
    /// sign derivation (the same `-n` convention established above applies: legacy's term makes its
    /// own `n` more negative, so the correction to this method's return value is ADDED, not
    /// subtracted).
    /// </summary>
    public static int ComputeAnchorCorrection(
        double lineWidthSamples,
        double syncPeakOffsetSamples,
        int lineCount,
        Func<int, double> envelopeAt)
    {
        var pageWidthSamples = (int)lineWidthSamples;
        var binCount = pageWidthSamples + 2;
        var bins = new double[binCount];

        var n = 0;
        for (var page = 0; page < lineCount; page++)
        {
            for (var i = 0; i < pageWidthSamples; i++)
            {
                var x = (int)(n % lineWidthSamples);
                bins[x] += envelopeAt(n);
                n++;
            }
        }

        var argmaxBin = 0;
        var max = 0.0;
        for (var i = 0; i < binCount; i++)
        {
            if (max < bins[i])
            {
                max = bins[i];
                argmaxBin = i;
            }
        }

        // Truncates toward zero, matching C++'s narrowing conversion on `n -= SSTVSET.m_OFP`
        // (n declared int, m_OFP double) -- C#'s explicit double-to-int cast has the same
        // truncate-toward-zero semantics, so this is exact, not an approximation.
        return (int)(argmaxBin - syncPeakOffsetSamples);
    }
}
