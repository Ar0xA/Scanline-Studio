namespace Yoniq.Core.Sstv;

/// <summary>
/// The stateless VIS-bit decision rule shared by every mechanism that races the 1080Hz/1320Hz
/// envelope detectors against each other (`sstv.cpp:1981-1988`, case 2/9 inside `CSSTVDEM::Do`):
/// reject if both are weaker than the 1900Hz reference, or too close to call, otherwise the louder
/// tone wins. Extracted so <see cref="VisLockStateMachine"/> and <see cref="AnalogFmSstvDecoder"/>'s
/// fixed-window header path decide bits identically without duplicating this exact 4-line rule --
/// see spec/14-roadmap.md's "Piece 9" entry for why a FULL extraction (detectors, timing) isn't
/// possible: the two callers' timing shapes (continuous hunting-state vs. fixed analytic window)
/// and byte-assembly (full 8-bit match vs. 7-bit escape detection) genuinely differ, only this
/// 4-line predicate is truly shared.
/// </summary>
internal static class VisBitDecision
{
    /// <summary><c>sstv.cpp:1981-1988</c>. Returns false (bit left at 0, caller must not use it) on
    /// the weak/ambiguous reject -- legacy aborts the whole decode attempt back to case 0
    /// (<c>m_SyncMode = 0</c>) on this, every caller here must do the same.</summary>
    public static bool TryDecide(double d11, double d13, double d19, double slvl2, out int bit)
    {
        if ((d11 < d19 && d13 < d19) || Math.Abs(d11 - d13) < slvl2)
        {
            bit = 0;
            return false;
        }

        bit = d11 > d13 ? 1 : 0;
        return true;
    }
}
