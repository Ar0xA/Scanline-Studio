namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// RX buffer subsystem Phase 7 -- extracted so <see cref="AnalogFmSstvDecoder"/> can hold either
/// <see cref="RxLineStagingBuffer"/> (RAM, <c>RxBufferMode.On</c>) or a disk-backed implementation
/// (<c>RxDiskLineStagingBuffer</c>, <c>RxBufferMode.Extended</c>) behind one field, with every
/// existing capture/replay call site (`AnalogFmSstvDecoder.cs`'s capture hook and
/// <c>PerformReplay</c>) unchanged. Member list is exactly what those call sites and the shipped
/// Phase 3-6 tests already use against the concrete <see cref="RxLineStagingBuffer"/> type --
/// verified by grep before extraction, nothing added speculatively.
///
/// <c>internal</c>, matching <see cref="RxLineStagingBuffer"/>'s own accessibility (a
/// <see langword="public"/> interface on an <see langword="internal"/> implementer fails to build).
///
/// <b>Deliberately excludes <c>CapacitySamples</c></b> (round-1/round-2 plan-review finding,
/// `/home/artien/.claude/plans/wise-riding-hearth.md`): it has exactly one non-test consumer today
/// (<see cref="RxLineStagingBuffer"/>'s own admission check), and a disk-backed "no real cap"
/// implementation would need an artificial sentinel value on this shared surface for a property nothing
/// outside that one class reads. Stays a RAM-only property; <c>RxLineStagingBufferTests.cs</c>
/// already constructs the concrete type directly.
///
/// <b>Correction, RX buffer subsystem Phase 8</b>: this doc previously suggested adding a
/// <c>bool IsFull</c> here for Phase 8's own "is the RAM buffer already full?" need
/// (`Main.cpp:5268-5270`) -- that suggestion turned out to be the wrong shape once Phase 8's own
/// plan-review worked through the actual legacy math: the tail commit-vs-revert check needs "is
/// there room for 32 MORE lines" (`Main.cpp:5415-5416`), which a single boolean can't answer for an
/// arbitrary lookahead. See <see cref="HasHeadroomForSamples"/> below instead.
///
/// <see cref="IDisposable"/> -- a disk-backed implementation owns scratch files and a background
/// writer that must be torn down; <see cref="RxLineStagingBuffer"/>'s own <c>Dispose()</c> is a
/// no-op.
/// </summary>
internal interface IRxLineStagingBuffer : IDisposable
{
    /// <summary>Total number of successfully staged LINES (not samples). See
    /// <see cref="RxLineStagingBuffer.LineCount"/>'s own doc comment for why this is tracked
    /// directly rather than derived from a stride.</summary>
    int LineCount { get; }

    /// <summary>Samples currently staged (both streams always have equal length -- see
    /// <see cref="TryAppendLine"/>'s own precondition).</summary>
    int Count { get; }

    /// <summary>Set once a write has failed (disk-backed implementations only -- always
    /// <see langword="false"/> for the RAM implementation) or the write queue could not keep up.
    /// Once set, every subsequent <see cref="TryAppendLine"/> call returns <see langword="false"/>
    /// immediately -- capture simply stops, same silent "buffer full" contract
    /// <see cref="RxLineStagingBuffer"/> already establishes for RAM capacity exhaustion, just a
    /// different trigger. Runtime write failures still latch silently on the hot decode path;
    /// <see cref="RxDiskLineStagingBuffer"/> logs only bounded teardown-stage failures, where logging
    /// cannot add per-line audio-thread I/O.
    ///
    /// <b>Observable outside this class as of T0-6</b>: promoted onto <c>ISstvDecoder</c> as
    /// <c>RxBufferDegraded</c> (mirroring how <c>CaptureOverrunCount</c> was promoted onto
    /// <c>IAudioEngine</c>), forwarded through <c>AnalogFmSstvDecoder</c> and
    /// <c>RestartableSstvDecoder</c>. <see cref="RxDiskLineStagingBuffer"/> also now logs every
    /// transition into this state exactly once (<c>LatchWriteFailure</c>). Once set, replay and
    /// Correct Slant still silently become permanent no-ops for the rest of this decoder instance's
    /// lifetime (see both methods' own capacity-guard call sites) -- <c>RxBufferDegraded</c> and the
    /// log line are what now let a caller find out why.</summary>
    bool HasWriteFailed { get; }

    /// <summary>Attempts to append one line's worth of samples to both streams atomically. See
    /// <see cref="RxLineStagingBuffer.TryAppendLine"/>'s own doc comment for the full RAM-mode
    /// contract (per-line atomicity, no partial line ever staged, silent rejection not an
    /// exception). A disk-backed implementation additionally rejects (returns <see langword="false"/>)
    /// once <see cref="HasWriteFailed"/> is set, for the same "capture simply stops" reason.
    /// Both implementations LATCH: <see cref="RxLineStagingBuffer"/> on its first capacity rejection
    /// (cleared by <see cref="RxLineStagingBuffer.Clear"/>, matching legacy's <c>m_wStgLine = 0</c>),
    /// the disk-backed one on write failure (NOT cleared by <c>Clear</c> -- a failed write is not
    /// repaired by emptying the buffer).</summary>
    bool TryAppendLine(ReadOnlySpan<double> demodulated, ReadOnlySpan<double> syncEnvelope);

    /// <summary>The exact staged-sample count spanned by the first <c>min(lineCount, </c>
    /// <see cref="LineCount"/><c>)</c> staged lines. See
    /// <see cref="RxLineStagingBuffer.SampleCountThroughLine"/>'s own doc comment.</summary>
    int SampleCountThroughLine(int lineCount);

    /// <summary>Reads the staged demodulated-stream value at local buffer index <paramref name="index"/>
    /// (0-based, relative to the last <see cref="Clear"/>) -- <see cref="ArgumentOutOfRangeException"/>
    /// for any index outside <c>[0, <see cref="Count"/>)</c>, matching
    /// <see cref="RxLineStagingBuffer"/>'s own bare-<see cref="List{T}"/>-indexer throw. A disk-backed
    /// implementation must preserve this exact throw contract, not silently return stale or zero data
    /// for an out-of-range index.</summary>
    double DemodulatedAt(int index);

    /// <summary>Reads the staged sync-envelope-stream value at local buffer index
    /// <paramref name="index"/> -- same indexing/bounds contract as <see cref="DemodulatedAt"/>.</summary>
    double SyncEnvelopeAt(int index);

    /// <summary>Discards all staged samples, resetting <see cref="Count"/> to 0. See
    /// <see cref="RxLineStagingBuffer.Clear"/>'s own doc comment. Called from the decode path
    /// (<c>InitializeSlant</c> at every fresh lock, and the tail of every <c>PerformReplay</c>
    /// pass) -- MUST NEVER THROW, on any implementation.</summary>
    void Clear();

    /// <summary>RX buffer subsystem Phase 8 -- <see langword="true"/> if appending
    /// <paramref name="additionalSamples"/> MORE samples on top of what's already staged would NOT
    /// exceed this buffer's own capacity notion. Answers both of `CorrectSlant`'s own
    /// capacity-arithmetic questions (`Main.cpp:5268-5270`'s "+1 line" entry-gate check and
    /// `:5415-5416`'s "+32 lines" tail commit-vs-revert check) via one member operating directly in
    /// SAMPLES, matching how legacy's own two checks are both really about samples despite being
    /// spelled as lines-times-width in the source.
    ///
    /// <see cref="RxLineStagingBuffer"/> (RAM): <c>!latched &amp;&amp; Count + additionalSamples &lt;
    /// CapacitySamples</c> -- the same strict <c>&lt;</c> boundary <see cref="TryAppendLine"/>'s own
    /// admission check already uses, one definition, not two, AND the same latch: once
    /// <see cref="TryAppendLine"/> has rejected one line for capacity, this returns
    /// <see langword="false"/> too, even for a probe that would arithmetically still fit -- legacy
    /// spells `CorrectSlant`'s entry gate (`Main.cpp:5268-5270`) with the IDENTICAL expression as
    /// its append gate (`Main.cpp:4999`/`:5242`), so "appends have stopped" and "slant correction is
    /// refused" are one fact in legacy, not two. A disk-backed implementation has no real capacity
    /// notion (Phase 7's own design) -- always <see langword="true"/> unless <see cref="HasWriteFailed"/>
    /// is already set, matching legacy's own real behavior: `m_StgBuf == NULL` (disk mode) skips the
    /// capacity check entirely at both of `CorrectSlant`'s call sites.</summary>
    bool HasHeadroomForSamples(int additionalSamples);
}
