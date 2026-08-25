using System.Threading;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Port of legacy's <c>CScope</c> (`sstv.h:188-206`, `sstv.cpp:154-210`) -- a one-shot
/// fill-then-stop capture buffer, rearmed by <c>Collect(size)</c> (<c>TrigNext</c>/<c>SBTrigClick</c>,
/// `Scope.cpp:83-89`). Legacy's own <c>m_ScopeFlag</c> gate ("only write while armed") is modeled
/// here by <see cref="IsFull"/> itself: <see cref="Write"/> is a no-op once full, so a caller that
/// checks <c>!IsFull</c> before writing gets the same "no cost once done" property without a
/// separate gate field.
///
/// <b>Cross-thread handoff</b>: <see cref="Write"/> is called only from the decode thread (single
/// producer, matching every other decoder-internal capture in this file). Once the buffer fills,
/// the completed array is published via <see cref="Interlocked.Exchange{T}(ref T, T)"/> into
/// <see cref="_snapshot"/> -- the same one-time-publish-then-read pattern
/// <c>AnalogFmSstvDecoder</c>'s own <c>_pendingNotchRequest</c>/<c>_forcedMode</c> fields use. A UI
/// thread reads the published snapshot via <see cref="TrySnapshot"/>, which is a plain
/// <see cref="Volatile.Read{T}(ref T)"/> -- safe from any thread, at any time, returning
/// <see langword="null"/> until the capture actually completes.
///
/// <b>Not thread-safe for concurrent <see cref="Write"/> calls</b> -- matches every other
/// decoder-internal buffer in this file (all written under the single-caller-thread
/// <c>PushSamplesCore</c> contract); <see cref="Arm"/> is expected to be called from that same
/// thread too (it is only ever invoked while draining a deferred arm-request, mirroring
/// <c>ApplyPendingNotchRequest</c>'s own shape).
/// </summary>
public sealed class ScopeCaptureBuffer
{
    private double[] _buffer = [];
    private int _count;
    private double[]? _snapshot;

    /// <summary><see langword="true"/> once a full capture has been published and is available via
    /// <see cref="TrySnapshot"/>. Legacy's own per-channel <c>m_DataFlag</c> (`sstv.h:191`,
    /// self-latches at full inside <c>WriteData</c>, `sstv.cpp:180-191`) -- not a single shared gate,
    /// each channel gets its own <see cref="ScopeCaptureBuffer"/> instance.</summary>
    public bool IsFull => Volatile.Read(ref _snapshot) is not null;

    /// <summary>Decode-thread-only (unlike <see cref="IsFull"/>/<see cref="TrySnapshot"/>, which are
    /// safe from any thread) -- <see langword="true"/> only while actively wanting more samples: armed
    /// (<c>_buffer.Length &gt; 0</c>) and not yet full. Distinct from <c>!IsFull</c>: an UN-armed
    /// buffer (fresh, never <see cref="Arm"/>'d) also reports <see cref="IsFull"/> as
    /// <see langword="false"/>, which would be the wrong signal for a caller deciding whether extra
    /// work (e.g. a forced catch-up read elsewhere in the decoder) is worth doing at all -- that
    /// caller needs "am I actually mid-capture," not "have I finished."</summary>
    public bool IsCapturing => _buffer.Length > 0 && Volatile.Read(ref _snapshot) is null;

    /// <summary>Channel-0-specific: which VIS/sync-envelope source (<c>D19At</c> narrow vs
    /// <c>D12At</c> wide) this capture is drawing from, latched once at
    /// <see cref="AnalogFmSstvDecoder.ArmScopeCapture"/> time and held for the whole capture -- see
    /// that method's own doc comment for why this can't be re-evaluated per sample. Never read or
    /// written for a channel-1 buffer (harmless, just unused). Lives HERE, not as a field on
    /// <c>AnalogFmSstvDecoder</c>, specifically so it survives a <c>RestartableSstvDecoder</c>
    /// periodic rebuild alongside the buffer's own fill progress -- the wrapper hands the SAME
    /// <see cref="ScopeCaptureBuffer"/> instance into every freshly-constructed inner decoder, but a
    /// field on the inner decoder itself would silently reset to its default on every rebuild,
    /// splicing sources into an in-progress capture on every restart, not just on a genuine
    /// narrow/wide mode transition.</summary>
    public bool UsesD19 { get; set; }

    /// <summary>Legacy's <c>Collect(size)</c> (`sstv.cpp:206`) -- buffer size is per-arm, not fixed;
    /// re-arming discards any in-progress or completed-but-unread capture, matching legacy's own
    /// re-trigger semantics (<c>TrigNext</c> has no "already armed" guard). A zero-size arm publishes
    /// an empty snapshot immediately -- <see cref="Write"/> alone could never do so (it only ever
    /// publishes from inside the branch that just wrote the LAST slot, which a zero-length buffer
    /// never reaches), and leaving <see cref="IsFull"/> permanently false for a 0-size arm would be a
    /// silent hang for a caller that armed with nothing to capture.</summary>
    public void Arm(int size)
    {
        _buffer = new double[size];
        _count = 0;
        Volatile.Write(ref _snapshot, size == 0 ? _buffer : null);
    }

    /// <summary>No-op once <see cref="IsFull"/> or before the first <see cref="Arm"/> call (an
    /// unarmed buffer has <c>_buffer.Length == 0</c>) -- the "no cost when the Trace pane isn't in
    /// use" property callers rely on lives here, not in a separate caller-side check.</summary>
    public void Write(double value)
    {
        if (_count >= _buffer.Length)
        {
            return;
        }

        _buffer[_count] = value;
        _count++;
        if (_count >= _buffer.Length)
        {
            Interlocked.Exchange(ref _snapshot, _buffer);
        }
    }

    /// <summary>Safe from any thread, at any time. <see langword="null"/> until the current arm
    /// cycle's capture actually completes.</summary>
    public double[]? TrySnapshot() => Volatile.Read(ref _snapshot);
}
