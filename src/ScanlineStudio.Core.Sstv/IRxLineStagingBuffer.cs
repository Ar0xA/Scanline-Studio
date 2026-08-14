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
/// already constructs the concrete type directly. When Phase 8's <c>CorrectSlant</c> gate needs "is
/// the RAM buffer already full?" (`Main.cpp:5268-5270`), add a <c>bool IsFull</c> then, not a
/// capacity number now.
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
    /// different trigger. No logging happens inside this project's DSP core (a deliberate existing
    /// layering boundary, `Core.Sstv` has zero <c>ILogger</c> usage anywhere) -- a caller with a path
    /// to the Application layer can observe this flag and log it there if desired.</summary>
    bool HasWriteFailed { get; }

    /// <summary>Attempts to append one line's worth of samples to both streams atomically. See
    /// <see cref="RxLineStagingBuffer.TryAppendLine"/>'s own doc comment for the full RAM-mode
    /// contract (per-line atomicity, no partial line ever staged, silent rejection not an
    /// exception). A disk-backed implementation additionally rejects (returns <see langword="false"/>)
    /// once <see cref="HasWriteFailed"/> is set, for the same "capture simply stops" reason.</summary>
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
}
