namespace Yoniq.Core.Sstv;

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
    /// is the mode's real <c>m_OFP</c> in samples (<see cref="SstvModeRegistry.GetSyncPeakOffsetMs"/>
    /// times the sample rate). <paramref name="isScottieFamily"/> selects legacy's Scottie-only
    /// wraparound branch (`Main.cpp:3785-3791`, `smSCT1`/`smSCT2`/`smSCTDX`). <paramref
    /// name="lineCount"/> is legacy's <c>e</c> (3 or 4, `Main.cpp:3759-3760`).
    /// <paramref name="envelopeAt"/> supplies the sync-envelope value at relative sample index
    /// <c>0..(lineCount*(int)lineWidthSamples)-1</c>, counted from the provisional anchor -- this
    /// port's flat sample arrays need no equivalent of legacy's page-strided ring-buffer indexing
    /// (`m_B12[pg*m_BWidth+i]`, `Main.cpp:3768`): that stride is purely a C++ circular-buffer memory
    /// layout detail, not part of the actual DSP algorithm, so a simple 0-based relative index is a
    /// faithful (not simplified) equivalent.
    ///
    /// Omits legacy's Hilbert-demodulator tap adjustment (`if (m_Type==2) n -= m_hill.m_htap/4`,
    /// `Main.cpp:3794`) -- this port has no Hilbert demodulator path (only the PLL), so that term
    /// has no equivalent to port; see <see cref="PllFmDemodulator"/>'s own doc comment for the
    /// separately-logged gap that legacy's shipped DEFAULT demodulator is actually Hilbert, not PLL.
    /// </summary>
    public static int ComputeAnchorCorrection(
        double lineWidthSamples,
        double syncPeakOffsetSamples,
        bool isScottieFamily,
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
        var delta = (int)(argmaxBin - syncPeakOffsetSamples);

        if (isScottieFamily && delta > 0)
        {
            delta -= pageWidthSamples;
        }

        return delta;
    }
}
