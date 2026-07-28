namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct port of legacy's fixed sync-tone amplitude-envelope detector -- the <c>d12</c>/<c>d19</c>
/// signal computed in <c>CSSTVDEM::Do</c> (`sstv.cpp`): a <see cref="TankFilter"/> resonator
/// (bandwidth 100Hz, `InitTone`'s <c>m_iir12/19.SetFreq(1200/1900+dfq, SampFreq, 100.0)</c>),
/// rectified, then smoothed by a 50Hz/2nd-order Butterworth lowpass (<c>m_lpf12</c>/<c>m_lpf19</c>,
/// `sstv.cpp:1452/1454`: <c>MakeIIR(50, SampFreq, 2, 0, 0)</c>) -- a classic resonate-rectify-smooth
/// AM envelope detector. Which center frequency to use is mode-dependent: legacy's own sync-buffer
/// selection (`sstv.cpp`'s <c>#if NARROW_SYNC == 1200</c> -- false, since <c>NARROW_SYNC</c> is
/// actually 1900, `sstv.h:440`, so the compiled `#else` branch is the real one) uses <c>d19</c>
/// (1900Hz) for the MN/MC narrow family and <c>d12</c> (1200Hz) for everyone else, AVT excluded
/// entirely. Used by <see cref="SlantTracker"/> (via <c>AutoStopJob</c>'s real input,
/// <c>m_SyncPos</c>) to find where the sync pulse's signal strength peaks within each line, which is
/// what Auto Slant measures drift against — entirely separate from the PLL-based main demodulator
/// and from AFC's zero-crossing counter.
///
/// Simplification, flagged not silently absorbed: legacy's real input here is the AGC-scaled
/// signal shared by every demodulator/detector (<c>CLVL</c>'s single AGC pipeline, `sstv.cpp`'s
/// <c>Do()</c>: <c>d = ad*32</c>, clipped to +/-16384), not the raw sample. This port's
/// <see cref="PllFmDemodulator"/> has its own independent, simpler per-demodulator AGC instead of
/// legacy's one shared <c>CLVL</c> instance (an existing, separately-documented Phase 1
/// simplification) — with no shared AGC'd signal to reuse, this detector runs on the raw input
/// directly. Harmless for this port's synthetic round-trip fixtures (constant full amplitude
/// throughout, so amplitude-dependent peak detection behaves the same either way) but would need
/// real AGC for reliable peak position tracking against real captured audio with varying volume.
/// </summary>
internal sealed class SyncEnvelopeDetector
{
    private readonly TankFilter _resonator = new();
    private readonly IirFilter _smoother = new();

    /// <param name="bandwidthHz">Resonator bandwidth -- 100Hz for every existing use (<c>m_iir12</c>/
    /// <c>m_iir19</c>/<c>m_iirfsk</c>, `sstv.cpp:1447/1449/1450`), but the VIS-bit tone-race detectors
    /// (<c>m_iir11</c>/<c>m_iir13</c>, 1080/1320Hz) use a narrower 80Hz band (`sstv.cpp:1446/1448`) --
    /// confirmed by direct comparison against those four <c>SetFreq</c> calls, not assumed to match.</param>
    public SyncEnvelopeDetector(double sampleRate, double centerFrequencyHz, double bandwidthHz = 100.0)
    {
        _resonator.SetFreq(centerFrequencyHz, sampleRate, bandwidthHz);
        _smoother.Design(50, sampleRate, 2);
    }

    public double ProcessSample(double input)
    {
        var resonated = _resonator.Process(input);
        return _smoother.Process(Math.Abs(resonated));
    }
}
