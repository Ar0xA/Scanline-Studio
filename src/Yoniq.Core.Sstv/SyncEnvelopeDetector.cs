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
/// Piece 7a/7b/7b2 closed a previously-documented simplification here: legacy's real input is the
/// AGC-scaled signal shared by every sync/tone-envelope detector (<c>CLVL</c>'s single AGC pipeline,
/// `sstv.cpp`'s <c>Do()</c>: <c>d = ad*32</c>, clipped to +/-16384). Every instance of this class is
/// now fed that same shared AGC'd signal by its caller (<c>AnalogFmSstvDecoder.AgcSampleAt</c> /
/// <c>LevelAgc</c>), not a raw sample directly -- this class itself stays scale-agnostic (a plain
/// resonate-rectify-smooth filter with no assumptions about its input's amplitude), so no change was
/// needed here, only at the call sites. Unrelated: <see cref="PllFmDemodulator"/>'s own independent,
/// simpler per-demodulator AGC (an existing, separately-documented Phase 1 simplification, still
/// true) feeds the *pixel*-demodulation path, not this one -- matching legacy's own split between
/// <c>m_lvl.m_Cur</c> (pixel/AFC, pre-AGC) and <c>m_lvl.AGC(d)*32</c> (sync detectors, this class).
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
