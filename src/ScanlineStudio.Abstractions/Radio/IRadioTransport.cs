namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. Bytes in, bytes out — no framing/parsing knowledge. Only the
/// TCP-based backends (rigctld, flrig — spec/03-cat-layer.md) use this; call-based backends (linked
/// Hamlib, OmniRig) are call-based, not byte-stream-based, and don't implement it.
///
/// <b>Buffer-survival contract</b> (the one subtle correctness requirement here, added after an
/// auditor design-review pass — see spec/14-roadmap.md's "rigctld client" entry): <see cref="ReadAsync"/>
/// returns an <see cref="IAsyncEnumerable{T}"/> of individual bytes, but an implementation is free to
/// read the underlying socket/stream in larger internal chunks. If a caller (e.g. a request/response
/// protocol reading up to a line terminator) disposes its enumerator mid-chunk — because it got what it
/// needed, or timed out — any bytes already read from the socket but not yet yielded to that caller
/// <b>must</b> survive and be the first bytes yielded by the <em>next</em> <see cref="ReadAsync"/> call.
/// The read buffer belongs to the transport, not to any one enumerator. Getting this wrong doesn't
/// throw or fail loudly — it silently desyncs the stream, so every subsequent read parses the previous
/// call's leftover tail: a plausible-looking but permanently stale value, the worst failure shape here
/// because it never looks broken. <see cref="ReadAsync"/> is single-consumer only; concurrent
/// enumeration (two callers reading at once) is undefined. Any implementation of this interface —
/// including test doubles — must reproduce this exact semantics, not a more forgiving approximation of
/// it (a fake that's more forgiving than the real transport can't catch the bug this contract exists to
/// catch; see <c>ScanlineStudio.Core.Audio.FakeAudioEngine</c>'s own doc comment for the same class of
/// correction made there).</summary>
public interface IRadioTransport : IAsyncDisposable
{
    Task OpenAsync(CancellationToken ct);
    Task CloseAsync();
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    IAsyncEnumerable<byte> ReadAsync(CancellationToken ct);
    bool IsOpen { get; }
}
