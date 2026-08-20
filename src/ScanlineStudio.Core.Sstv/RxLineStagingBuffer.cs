using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// RX buffer subsystem Phase 4 -- a flat, capacity-capped, chronologically-ordered store of the two
/// per-sample streams legacy's RAM staging buffer captures (`CSSTVDEM::m_StgBuf`/`m_StgB12`,
/// `sstv.cpp:1615-1644`'s `FreeRxBuff`/`OpenCloseRxBuff`), backing the still-unbuilt replay mechanism
/// (RX buffer subsystem Phase 6). Isolated from <see cref="AnalogFmSstvDecoder"/> by design -- this
/// class knows nothing about decode internals; Phase 5 wires capture into the per-line loop.
///
/// <b>Flat sample stream, deliberately NOT a list of per-line chunks</b> -- the RX buffer plan's own
/// round-1 finding (`/home/artien/.claude/plans/coppery-staging-heron.md`): legacy's replay
/// (`Main.cpp:5581-5771`) re-splits this same flat stream into rows using whatever line width is
/// CURRENT at replay time, not the width it was captured at (`DrawSSTVNormal`'s `y=int(n/m_TW)`,
/// `Main.cpp:4133-4148`) -- that re-splitting is the correction mechanism itself. A per-line-chunk data
/// model would bake in the capture-time stride and make that correction impossible to express. Indexed
/// by position (0-based, relative to whenever this buffer was last <see cref="Clear"/>ed) -- translating
/// between a decoder's own absolute sample coordinate and this buffer's local index is the caller's
/// job (Phase 5/6), not this class's. NOT yet accounting for Phase 6's own "origin shear" question
/// (`ReSyncSSTV`'s `m_IOFS=m_OFS=m_rBase=n` can be negative, `Main.cpp:5528-5529`) -- this class throws
/// on a negative index rather than reserving any pre-origin margin; Phase 6 will need either a margin
/// or an explicit origin field here, deliberately deferred per the plan's own phase split.
///
/// <b>Two parallel streams, one entry per staged sample</b>: <c>demodulated</c> mirrors legacy's
/// <c>m_StgBuf</c> (the post-demodulation, pixel-domain data feeding `DrawSSTV`'s picture channel --
/// this port's own equivalent is <c>AnalogFmSstvDecoder._demodulatedFrequencies</c>, AFC-corrected, per
/// that field's own doc comment); <c>syncEnvelope</c> mirrors legacy's <c>m_StgB12</c> (the sync-tone
/// envelope-detector output feeding sync-position search -- this port's own equivalent is whatever
/// <c>SyncEnvelopeDetector</c> instance <c>ApplySlantTracking</c> already runs per sample, tapped, not
/// duplicated -- see that method's own doc comment on why a second detector instance would
/// double-process a streaming filter that must see each sample exactly once). This class stores
/// whatever <see cref="double"/> values it's given; it has no opinion on where they come from.
///
/// <b>Capacity mirrors legacy's own RAM staging-buffer budget exactly</b>: `sstv.cpp:1632`'s
/// `n = 257 * 1100 * SampFreq / 1000`, evaluated against legacy's real NOMINAL sound-card rate
/// (`SampFreq`, `ComLib.cpp:47`, set once at `Main.cpp:577`/`:1635-1636`) -- NOT the slant/AFC-corrected
/// rate (`SSTVSET.m_SampFreq`, reassigned mid-reception at `Main.cpp:4015`/`:5404-5421`/`:11608`).
/// Phase 5 must construct this class with this port's own equivalent nominal rate
/// (<c>AnalogFmSstvDecoder._sampleRate</c>) and never a corrected one, or capacity would drift
/// mid-reception, which legacy's single fixed-size allocation never does. A total SAMPLE count, not a
/// line count -- how many lines that buys depends on the locked mode's own line width, which can change
/// across the buffer's lifetime if the mode changes, matching legacy's own single fixed-size allocation
/// regardless of mode. Appending is atomic per call, matching legacy's own admission test
/// (`Main.cpp:4999`/`:5242`: `((m_wStgLine+1)*SSTVSET.m_WD) &lt; m_RxBufAllocSize`, STRICT less-than --
/// a line landing EXACTLY on the cap is rejected too, so legacy always leaves at least one element of
/// slack unused; see <see cref="TryAppendLine"/>'s own boundary) -- a call that would meet or exceed
/// capacity is rejected WHOLESALE (no partial line ever gets staged), not truncated to fit. Rejection is
/// silent (a <see langword="false"/> return, not an exception) -- past capacity, capture simply stops;
/// the in-progress image and any already-staged samples are unaffected, exactly like legacy.
///
/// <b>Rejection is LATCHED, not re-evaluated per call</b> -- legacy's admission test is monotonic:
/// `m_WD` is fixed for a whole reception (assigned exactly once in the legacy codebase, `sstv.cpp:594`)
/// and `m_wStgLine` only increments or resets to 0, so the first line legacy rejects is the last line
/// it ever stages until a reset (`CopyStgBuf` makes it explicit: `else { break; }`,
/// `Main.cpp:5247-5249`) -- legacy's staged stream is structurally GAP-FREE. This port's own per-line
/// width varies (fractional-carry +-1, plus Auto-Slant commits), so a purely per-call check would
/// reject a wide line and then admit a narrower one past the same slack, punching a hole in a stream
/// every consumer maps to destination samples through a single linear offset. Once
/// <see cref="TryAppendLine"/> returns <see langword="false"/> for capacity, every subsequent call
/// returns <see langword="false"/> too, until <see cref="Clear"/> (legacy's `m_wStgLine = 0`,
/// `Main.cpp:4958`) re-opens admission.
///
/// <b>Element type is <see cref="double"/>, not legacy's <c>short</c></b> -- matching this port's own
/// existing `_demodulatedFrequencies`/`SyncEnvelopeDetector` output types (the actual values this class
/// will be fed, once Phase 5 wires capture) rather than legacy's raw 16-bit staging. Two separate,
/// accepted, documented divergences, not one: (1) memory -- roughly 4x legacy's own footprint for the
/// same sample count, backed by growable <see cref="List{T}"/>s (not an eagerly-preallocated array at
/// full capacity, unlike legacy's own up-front `new short[n]`) -- capacity is enforced by the admission
/// check, not by array size, so a buffer that's constructed but never (or barely) filled costs
/// proportionally little, not the full ~O(hundred-megabyte) budget this capacity formula implies at
/// 44100Hz; (2) PRECISION -- legacy quantizes to 16-bit at write time (`m_Buf[n] = -d;`, an implicit
/// `double`-&gt;`short` narrowing, `sstv.cpp:2289`), so legacy's own replay reads back quantized data,
/// while this port's replay (Phase 6) will read back full-precision `double`s -- internally consistent
/// for this port (live decode and replay agree, unlike legacy which quantizes on write and dequantizes
/// on read) but a real, second source of numeric difference Phase 6's golden-vector comparisons must
/// account for, not just the storage-type memory tradeoff. Whether double-precision-vs-memory is the
/// right tradeoff for the eventual disk-backed Extended mode (Phase 7, which needs no RAM cap at all,
/// and where a `short`-width on-disk format would halve I/O) is that phase's own call, not this class's.
/// </summary>
internal sealed class RxLineStagingBuffer : IRxLineStagingBuffer
{
    private readonly List<double> _demodulated;
    private readonly List<double> _syncEnvelope;

    // RX buffer subsystem Phase 6a round-1 code-review finding: this port's own captured line width
    // is NOT a legacy-style constant. Legacy's own `m_WD` is fixed for a whole reception
    // (`sstv.cpp:594`'s `SetMode` only), so legacy can always recover `m_wStgLine` as
    // `stagedSampleCount / m_WD`. This port's own Phase 5 capture hook stages
    // `_effectiveSamplesPerLine`-worth of samples per line, which VARIES (+-1 from the fractional-carry
    // line-boundary accumulator, and shifts outright on every Auto-Slant commit) -- deriving a line
    // count via division against ANY single stride would silently miscount once a real reception
    // accumulates enough lines. Tracking the true per-line boundary explicitly, one entry per
    // successful TryAppendLine call, is the only correct fix -- not a stride assumption.
    private readonly List<int> _lineBoundaries;

    // Legacy's admission test is monotonic-once-full, and this port's is not unless latched.
    // `((m_wStgLine + 1) * SSTVSET.m_WD) < m_RxBufAllocSize` (`Main.cpp:4999`/`:5242`) uses a FIXED
    // width -- `m_WD` is assigned exactly once in the entire legacy codebase (`sstv.cpp:594`,
    // `SetMode`; CorrectSlant's own mid-reception `SetSampFreq()` calls at `Main.cpp:5405`/`:5409`/
    // `:5412` never touch it) -- against a `m_wStgLine` that only ever increments or resets to 0
    // (`Main.cpp:4958`, `sstv.cpp:1622`/`:1638`). So legacy's left-hand side never shrinks: the
    // FIRST line legacy rejects is the LAST line it ever stages until a reset, and `CopyStgBuf`
    // spells that out with an explicit `else { break; }` (`Main.cpp:5247-5249`) abandoning the whole
    // drain loop. Legacy's staged stream is therefore structurally GAP-FREE.
    //
    // This port's per-line sample count VARIES (`_effectiveSamplesPerLine`, +-1 from the
    // fractional-carry line-boundary accumulator and shifted outright by every Auto-Slant commit),
    // so a bare per-call `Count + length >= CapacitySamples` test would reject a WIDE line and then
    // silently admit a NARROWER one immediately after, past the same slack. That is not a
    // cosmetic difference: every consumer (`AnalogFmSstvDecoder`'s replay path) maps a local staged
    // index to a destination sample through ONE linear offset, so a post-gap line would be drawn
    // from the wrong audio, and `LineCount` would undercount the skipped line. Latching the first
    // rejection restores legacy's exact monotonic behavior. Not `volatile` (unlike
    // `RxDiskLineStagingBuffer._hasWriteFailed`, which a background consumer task writes) -- this
    // RAM implementation is touched only from the decode thread.
    private bool _capacityReached;

    /// <summary>Total number of successfully staged LINES (not samples) -- mirrors legacy's own
    /// <c>dp-&gt;m_wStgLine</c> exactly, tracked directly rather than derived from a stride this port's
    /// own per-line sample count doesn't hold constant (see this class's own field-level doc comment
    /// on <c>_lineBoundaries</c>).</summary>
    public int LineCount => _lineBoundaries.Count;

    public RxLineStagingBuffer(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        // sstv.cpp:1632's `n = 257 * 1100 * SampFreq / 1000`. Legacy's `SampFreq` is a `double`
        // (`ComLib.cpp:47`: `double SampFreq = 11025.0;`), NOT an `int` -- confirmed directly, not
        // assumed -- so `257 * 1100 * SampFreq` promotes to `double` arithmetic in legacy at the second
        // multiply, and 12,467,070,000.0 (the exact product at SampFreq=44100) is nowhere near
        // double's ~2^53 exact-integer limit. Legacy genuinely allocates its RAM RX-buffer correctly at
        // every real sample rate, including 44100Hz -- there is NO legacy bug here (an earlier version
        // of this comment wrongly claimed one, reasoning from plain 32-bit `int` arithmetic that legacy
        // never actually uses; corrected after auditor code review).
        //
        // The `(long)` promotion below exists for a DIFFERENT, real, C#-side reason: this port's
        // `sampleRate` parameter is `int`, and plain `int` arithmetic for `257 * 1100 * sampleRate`
        // WOULD overflow at SampFreq=44100 (intermediate product 12,467,070,000 vs. `int.MaxValue`'s
        // ~2.147 billion, wrapping to a large negative value before the trailing `/1000` could bring the
        // final, easily-int-representable result, ~12.47 million, back into range) -- a real hazard in
        // THIS port's own arithmetic, not a replicated legacy one. Promoting to `long` before the
        // multiply reproduces legacy's real (`double`-computed) value exactly at every integer sample
        // rate (verified: `282700 * rate` is always an exact integer well under 2^53, so double-then-
        // truncate and long-integer-division can never disagree), without the C#-side overflow.
        CapacitySamples = (int)((long)257 * 1100 * sampleRate / 1000);
        _demodulated = new List<double>();
        _syncEnvelope = new List<double>();
        _lineBoundaries = new List<int>();
    }

    /// <summary>Total sample budget this buffer was constructed with -- mirrors legacy's
    /// <c>m_RxBufAllocSize</c> (`sstv.cpp:1637`). Fixed for this instance's lifetime; a mode change
    /// mid-buffer does not resize it, matching legacy's own single fixed-size allocation.</summary>
    public int CapacitySamples { get; }

    /// <summary>Samples currently staged (both streams always have equal length -- see
    /// <see cref="TryAppendLine"/>'s own precondition). Never exceeds <see cref="CapacitySamples"/>.</summary>
    public int Count => _demodulated.Count;

    /// <summary>Attempts to append one line's worth of samples to both streams atomically. Mirrors
    /// legacy's own per-line admission test (`Main.cpp:4999`/`:5242`,
    /// `((m_wStgLine+1)*SSTVSET.m_WD) &lt; m_RxBufAllocSize`, STRICT less-than): if appending
    /// <paramref name="demodulated"/> in full would land AT OR PAST <see cref="CapacitySamples"/>,
    /// NEITHER stream is modified and this returns <see langword="false"/> -- legacy always rejects a
    /// line that would land exactly on the cap too (round-1 code-review finding on an earlier version
    /// of this method, which wrongly accepted an exact-fill line), so this buffer always leaves at
    /// least one element of slack unused, same as legacy. No partial line is ever staged, and no
    /// exception is thrown for a full buffer (this is legacy's own real, silent, non-fatal "buffer
    /// full" path, not an error condition). <paramref name="demodulated"/> and
    /// <paramref name="syncEnvelope"/> must be the same length (one entry per sample, both streams
    /// advance together) -- a length mismatch is a caller bug, not a legacy-fidelity question, hence the
    /// exception rather than a silent false.</summary>
    public bool TryAppendLine(ReadOnlySpan<double> demodulated, ReadOnlySpan<double> syncEnvelope)
    {
        if (demodulated.Length != syncEnvelope.Length)
        {
            throw new ArgumentException(
                $"{nameof(demodulated)} and {nameof(syncEnvelope)} must be the same length (got {demodulated.Length} and {syncEnvelope.Length}) -- both streams advance one entry per staged sample together.",
                nameof(syncEnvelope));
        }

        // Latched: legacy's admission test can never pass again once it has failed (see
        // _capacityReached's own doc comment). Checked before the per-call arithmetic, and after the
        // length-mismatch throw above -- same ordering as RxDiskLineStagingBuffer.TryAppendLine's
        // own `_hasWriteFailed` guard (`RxDiskLineStagingBuffer.cs:283-286`).
        if (_capacityReached)
        {
            return false;
        }

        if (_demodulated.Count + demodulated.Length >= CapacitySamples)
        {
            _capacityReached = true;
            return false;
        }

        AppendTo(_demodulated, demodulated);
        AppendTo(_syncEnvelope, syncEnvelope);
        _lineBoundaries.Add(_demodulated.Count);
        return true;
    }

    /// <summary>The exact staged-sample count spanned by the first <c>min(lineCount, </c>
    /// <see cref="LineCount"/><c>)</c> staged lines -- e.g. for
    /// <see cref="ReplayOriginCalculator.ComputeOrigin"/>'s own 32-line histogram-fold bound
    /// (`Main.cpp:5504`'s `i &lt; 32`), the caller passes <c>Math.Min(LineCount, 32)</c> here to get the
    /// exact sample count that spans, rather than assuming any per-line stride. <paramref name="lineCount"/>
    /// of 0 returns 0; a value <c>&gt;= LineCount</c> returns the full <see cref="Count"/>.</summary>
    public int SampleCountThroughLine(int lineCount)
    {
        if (lineCount <= 0 || _lineBoundaries.Count == 0)
        {
            return 0;
        }

        return _lineBoundaries[Math.Min(lineCount, _lineBoundaries.Count) - 1];
    }

    // CollectionsMarshal.SetCount + AsSpan avoids an intermediate `double[]` allocation per line
    // (ReadOnlySpan<T>.ToArray() would allocate one per stream per call) -- a real per-line hot path
    // once Phase 5 wires this into the decode loop, flagged by round-1 code review before that lands.
    private static void AppendTo(List<double> list, ReadOnlySpan<double> values)
    {
        var oldCount = list.Count;
        CollectionsMarshal.SetCount(list, oldCount + values.Length);
        values.CopyTo(CollectionsMarshal.AsSpan(list)[oldCount..]);
    }

    /// <summary>Reads the staged demodulated-stream value at local buffer index <paramref name="index"/>
    /// (0-based, relative to the last <see cref="Clear"/>) -- <see cref="ArgumentOutOfRangeException"/>
    /// for any index outside <c>[0, <see cref="Count"/>)</c>, matching ordinary <see cref="List{T}"/>
    /// indexing (no legacy-side "past the end" behavior to replicate here -- callers own their own
    /// bounds against <see cref="Count"/>).</summary>
    public double DemodulatedAt(int index) => _demodulated[index];

    /// <summary>Reads the staged sync-envelope-stream value at local buffer index <paramref name="index"/>
    /// -- same indexing/bounds contract as <see cref="DemodulatedAt"/>.</summary>
    public double SyncEnvelopeAt(int index) => _syncEnvelope[index];

    /// <summary>Discards all staged samples, resetting <see cref="Count"/> to 0 -- mirrors legacy's
    /// <c>dp-&gt;m_wStgLine = 0</c> at a fresh lock (`Main.cpp:4958`). Does NOT change
    /// <see cref="CapacitySamples"/> (fixed for this instance's lifetime, see that property's own doc
    /// comment). Also un-latches <see cref="TryAppendLine"/>'s capacity rejection, mirroring how
    /// legacy's own <c>m_wStgLine = 0</c> re-opens its admission test.</summary>
    public void Clear()
    {
        _demodulated.Clear();
        _syncEnvelope.Clear();
        _lineBoundaries.Clear();

        // Un-latches, matching legacy's `dp->m_wStgLine = 0` (`Main.cpp:4958`, fresh lock): with
        // m_wStgLine back at 0 legacy's `((m_wStgLine + 1) * m_WD) < m_RxBufAllocSize` passes again
        // and capture resumes. Deliberately UNLIKE RxDiskLineStagingBuffer.Clear(), which leaves
        // `_hasWriteFailed` set -- that flag's trigger is a genuine I/O failure that clearing cannot
        // repair, whereas this one's trigger is a full buffer that Clear() has just emptied.
        _capacityReached = false;
    }

    /// <summary>Always <see langword="false"/> -- this RAM implementation has no background writer
    /// that can fail; capacity exhaustion (a full <see cref="TryAppendLine"/> rejection) is a
    /// separate, expected, non-error condition, not a write failure. See
    /// <see cref="IRxLineStagingBuffer.HasWriteFailed"/>'s own doc comment.</summary>
    public bool HasWriteFailed => false;

    /// <summary>RX buffer subsystem Phase 8. Same strict <c>&lt;</c> boundary
    /// <see cref="TryAppendLine"/>'s own admission check uses below -- one definition, not two --
    /// AND the same latch: legacy spells `CorrectSlant`'s entry gate
    /// (`Main.cpp:5268-5270`) with the IDENTICAL expression as its append gate
    /// (`((m_wStgLine + 1) * m_WD) &gt;= m_RxBufAllocSize`), so in legacy "appends have stopped" and
    /// "slant correction is refused" are one fact, not two. Without the latch here, this port's own
    /// varying line width could answer <see langword="true"/> to a narrow probe after a wide line
    /// already latched capture shut, running a slant search legacy would have refused. Mirrors
    /// <see cref="RxDiskLineStagingBuffer.HasHeadroomForSamples"/>'s own <c>!_hasWriteFailed</c>
    /// shape. See <see cref="IRxLineStagingBuffer.HasHeadroomForSamples"/> for the full contract.</summary>
    public bool HasHeadroomForSamples(int additionalSamples) =>
        !_capacityReached && Count + additionalSamples < CapacitySamples;

    /// <summary>No-op -- this RAM implementation owns no unmanaged resources (no scratch files, no
    /// background writer task) to tear down. See <see cref="IRxLineStagingBuffer"/>'s own doc
    /// comment on why the interface is <see cref="IDisposable"/> at all (a disk-backed implementation
    /// needs it, this one doesn't).</summary>
    public void Dispose()
    {
    }
}
