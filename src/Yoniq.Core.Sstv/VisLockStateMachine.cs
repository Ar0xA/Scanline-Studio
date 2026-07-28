using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct port of legacy's real-time VIS-lock/bit-decode state machine (`sstv.cpp`'s
/// <c>CSSTVDEM::Do</c>, <c>m_SyncMode</c> cases 0(trigger)/1/2/9/3 only, `sstv.cpp:1946-2154`) —
/// finds the 1200Hz sync lock and decodes VIS bits one at a time via tone-racing between two
/// envelope detectors (1080Hz vs 1320Hz), advancing sample-by-sample regardless of where in the
/// stream the real header starts. This is what lets legacy locate a VIS header despite arbitrary
/// leading silence/noise, which this port's other header path (<c>AnalogFmSstvDecoder.TryDecodeVisHeader</c>,
/// a fixed-duration analytically-positioned window assumed to start exactly at the current consumed-
/// sample offset) cannot do. Wired in as a fallback alongside that path, not a replacement — see
/// `spec/14-roadmap.md`'s VIS/preamble-lock section for why.
///
/// Deliberately excludes case 0's <c>m_sint1</c>/<c>m_sint2</c>/<c>m_sint3</c> branches (those
/// already live in <c>AnalogFmSstvDecoder.TrySyncIntervalDetection</c>) and cases 4-8 (AVT's
/// separate, PLL-based training-sequence lock — its own, later roadmap piece). When the decoded
/// byte identifies AVT, this class deliberately does *not* report a lock (see <see cref="ProcessSample"/>) —
/// legacy's own case 3 (`sstv.cpp:2139-2144`) doesn't call <c>Start()</c> for AVT either; it
/// diverts into the training-lock state machine (case 4) instead, which this port doesn't have yet.
///
/// Simplifications, flagged not silently absorbed (same pattern already used for AFC/Slant/
/// <c>m_sint2</c>/<c>m_sint3</c>): every legacy condition here also checks absolute-amplitude
/// thresholds (<c>m_SLvl</c>/<c>m_SLvl2</c>) on top of the relative comparisons ported below — a
/// noise/squelch gate on legacy's internal AGC'd ±16384 scale this port doesn't model. This is the
/// fourth documented instance of that same omission.
/// </summary>
internal sealed class VisLockStateMachine
{
    private const double ConfirmLockDurationMs = 15; // sstv.cpp:1948
    private const double BitDurationMs = 30; // sstv.cpp:1965/1986 (case1's post-lock reset, case2/9's per-bit window)
    private const double VerifyDurationMs = 30; // sstv.cpp:1965 reused for case3's own m_SyncTime (2131-2133 reads the same 30ms-window pattern)
    private const int EscapeVisByte = 0x23; // sstv.cpp:2066

    private enum LockState { Search, ConfirmLock, DecodeVis, DecodeExtendedVis, Verify }

    private readonly double _sampleRate;
    private readonly SyncEnvelopeDetector _d11Detector; // m_iir11/m_lpf11, 1080Hz/80Hz BW (sstv.cpp:1446/1451)
    private readonly SyncEnvelopeDetector _d12Detector; // m_iir12/m_lpf12, 1200Hz/100Hz BW (sstv.cpp:1447/1452)
    private readonly SyncEnvelopeDetector _d13Detector; // m_iir13/m_lpf13, 1320Hz/80Hz BW (sstv.cpp:1448/1453) -- only stepped in DecodeVis/DecodeExtendedVis, see ProcessSample
    private readonly SyncEnvelopeDetector _d19Detector; // m_iir19/m_lpf19, 1900Hz/100Hz BW (sstv.cpp:1449/1454)

    private LockState _state = LockState.Search;
    private int _sampleCounter;
    private int _syncTimeCounter; // m_SyncTime, in samples
    private int _visData; // m_VisData
    private int _visCount; // m_VisCnt
    private int _triggerFireSample; // sample index where Search->ConfirmLock happened -- the anchor origin
    private SstvModeDefinition? _resolvedMode;
    private bool _isExtended;

    public VisLockStateMachine(double sampleRate)
    {
        _sampleRate = sampleRate;
        _d11Detector = new SyncEnvelopeDetector(sampleRate, 1080.0, bandwidthHz: 80.0);
        _d12Detector = new SyncEnvelopeDetector(sampleRate, 1200.0);
        _d13Detector = new SyncEnvelopeDetector(sampleRate, 1320.0, bandwidthHz: 80.0);
        _d19Detector = new SyncEnvelopeDetector(sampleRate, 1900.0);
    }

    /// <summary>Feeds one raw sample. Returns the locked mode and the sample index (relative to the
    /// very first sample ever passed to this instance, i.e. usable directly as an index into the
    /// same raw-sample buffer this port's other per-sample detectors already use that convention
    /// for) where transmission line 0 begins, or null if not yet locked.
    ///
    /// The anchor is derived analytically, not detected: legacy's own case 3 completes 15ms +
    /// (8 or 16, depending on whether the extended-VIS escape byte was seen) x30ms + 30ms after the
    /// trigger fires (`sstv.cpp:1948/1965/1986/2131`), which is exactly 15ms *before* the same
    /// line-0 boundary this port's <c>VisHeader.PrefixDurationMs</c>/<c>NormalTailDurationMs</c>/
    /// <c>ExtendedTailDurationMs</c> constants already define (verified by direct arithmetic against
    /// those constants: <c>LeaderDurationMs*2 + BreakDurationMs</c> = 610ms to the trigger, plus
    /// this state machine's own 300ms (normal) or 540ms (extended) to case 3's exit, versus
    /// <c>TotalDurationMs</c>/<c>PrefixDurationMs+ExtendedTailDurationMs</c> = 910ms/1150ms — a
    /// consistent, universal +15ms gap in both cases, not two different numbers that happened to
    /// need reconciling). Adding that fixed 15ms reproduces the exact same boundary the fixed-window
    /// header path already computes and that path's <![CDATA[<]]>10-delta round-trip tests already
    /// prove precise — reusing that proof instead of inventing new, unverified anchor arithmetic.
    /// Scottie's extra post-VIS pulse (<see cref="VisHeader.ScottiePostVisPulseDurationMs"/>) is
    /// added on top for Scottie modes, matching <c>TryDecodeVisHeader</c>'s own treatment.
    ///
    /// The one real imprecision unique to this path (vs. the fixed-window path's fully analytic
    /// placement): <paramref name="rawSample"/>-driven envelope-detector group delay means the
    /// trigger doesn't fire at the exact mathematical instant d12 crosses d19 -- a small, bounded
    /// lag, not modeled or corrected here. Measured (<c>VisLockStateMachineTests.HeaderAfterSecondsOfSilence_StillLocksAtTheRightOffset</c>):
    /// ~80 samples (~7.3ms at 11025Hz) for a clean synthetic tone with no noise.</summary>
    public (SstvModeDefinition Mode, int LineStartSample)? ProcessSample(double rawSample)
    {
        var currentSample = _sampleCounter++;

        var d11 = _d11Detector.ProcessSample(rawSample);
        var d12 = _d12Detector.ProcessSample(rawSample);
        var d19 = _d19Detector.ProcessSample(rawSample);

        switch (_state)
        {
            case LockState.Search:
                // sstv.cpp:1946-1950 (relative half of the condition only, see class doc comment)
                if (d12 > d19)
                {
                    _state = LockState.ConfirmLock;
                    _syncTimeCounter = MsToSamples(ConfirmLockDurationMs);
                    _triggerFireSample = currentSample;
                }

                break;

            case LockState.ConfirmLock:
                // sstv.cpp:1952-1973 -- ANY single failing sample resets to Search immediately;
                // the hold must be sustained for the full window, not just met once.
                if (d12 > d19)
                {
                    if (--_syncTimeCounter == 0)
                    {
                        _state = LockState.DecodeVis;
                        _syncTimeCounter = MsToSamples(BitDurationMs);
                        _visData = 0;
                        _visCount = 8;
                    }
                }
                else
                {
                    _state = LockState.Search;
                }

                break;

            case LockState.DecodeVis:
            case LockState.DecodeExtendedVis:
            {
                // sstv.cpp:1976-1978 -- d13's filter state is only ever advanced here, frozen
                // between attempts. Ported literally, not "fixed" -- see class doc comment.
                var d13 = _d13Detector.ProcessSample(rawSample);

                // sstv.cpp:1979-2124 -- the countdown is unconditional every sample; content only
                // matters once it reaches zero.
                if (--_syncTimeCounter == 0)
                {
                    // sstv.cpp:1981-1984, relative half only (see class doc comment)
                    if (d11 < d19 && d13 < d19)
                    {
                        _state = LockState.Search;
                        break;
                    }

                    _syncTimeCounter = MsToSamples(BitDurationMs);
                    _visData = (_visData >> 1) | (d11 > d13 ? 0x0080 : 0); // sstv.cpp:1987-1988
                    if (--_visCount != 0)
                    {
                        break;
                    }

                    if (_state == LockState.DecodeVis)
                    {
                        if (_visData == EscapeVisByte) // sstv.cpp:2066-2070
                        {
                            _state = LockState.DecodeExtendedVis;
                            _visData = 0;
                            _visCount = 8;
                            break;
                        }

                        var mode = SstvModeRegistry.FindByFullVisByte(_visData); // sstv.cpp:1993-2074
                        if (mode is null || mode == SstvModeRegistry.Avt) // AVT: see class doc comment
                        {
                            _state = LockState.Search;
                            break;
                        }

                        _resolvedMode = mode;
                        _isExtended = false;
                        _state = LockState.Verify;
                        _syncTimeCounter = MsToSamples(VerifyDurationMs);
                    }
                    else
                    {
                        var mode = SstvModeRegistry.FindByExtendedCode(_visData); // sstv.cpp:2078-2121
                        if (mode is null)
                        {
                            _state = LockState.Search;
                            break;
                        }

                        _resolvedMode = mode;
                        _isExtended = true;
                        _state = LockState.Verify;
                        _syncTimeCounter = MsToSamples(VerifyDurationMs);
                    }
                }

                break;
            }

            case LockState.Verify:
                // sstv.cpp:2127-2154 -- unconditional countdown (unlike ConfirmLock's sustained-hold
                // requirement), condition checked once at the end of the fixed 30ms window.
                if (--_syncTimeCounter == 0)
                {
                    if (d12 > d19)
                    {
                        var mode = _resolvedMode!;
                        var anchorOffsetMs = ConfirmLockDurationMs
                            + BitDurationMs * (_isExtended ? 16 : 8)
                            + VerifyDurationMs
                            + 15.0 // reconciles this state machine's own boundary with VisHeader's, see ProcessSample's doc comment
                            + (SstvModeRegistry.IsScottieFamily(mode) ? VisHeader.ScottiePostVisPulseDurationMs : 0.0);

                        var lineStartSample = _triggerFireSample + MsToSamples(anchorOffsetMs);
                        _state = LockState.Search;
                        return (mode, lineStartSample);
                    }

                    _state = LockState.Search;
                }

                break;
        }

        return null;
    }

    private int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * _sampleRate);
}
