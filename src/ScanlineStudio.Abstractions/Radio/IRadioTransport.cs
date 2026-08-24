namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. Bytes in, bytes out — no framing/parsing knowledge. Only
/// persistent-socket, line-protocol backends (rigctld — spec/03-cat-layer.md) use this; call-based
/// backends (linked Hamlib, OmniRig) are call-based, not byte-stream-based, and don't implement it.
/// flrig doesn't implement it either, despite also being TCP-based — its XML-RPC-over-HTTP wire
/// shape is request/response via <see cref="System.Net.Http.HttpClient"/>, not a persistent socket
/// with this interface's own buffer-survival contract (below).
///
/// <b>Buffer-survival contract</b> (the one subtle correctness requirement here, added after an
/// auditor design-review pass — see spec/14-roadmap.md's "rigctld client" entry): <see cref="ReadAsync"/>
/// returns an <see cref="IAsyncEnumerable{T}"/> of individual bytes, but an implementation is free to
/// read the underlying socket/stream in larger internal chunks. If a caller (e.g. a request/response
/// protocol reading up to a line terminator) disposes its enumerator mid-chunk because it got what it
/// needed, any bytes already read from the socket but not yet yielded to that caller <b>must</b>
/// survive and be the first bytes yielded by the <em>next</em> <see cref="ReadAsync"/> call. The read
/// buffer belongs to the transport, not to any one enumerator. Getting this wrong doesn't throw or fail
/// loudly — it silently desyncs the stream, so every subsequent read parses the previous call's
/// leftover tail: a plausible-looking but permanently stale value, the worst failure shape here because
/// it never looks broken.
///
/// This survival guarantee applies only to an <em>uncancelled</em> enumerator disposal. A cancellation
/// (the caller timed out, or its own caller's token fired) arriving mid-read is different: the request
/// whose response was being read is already on the wire, so bytes for it are still inbound or partly
/// buffered, and resuming would hand a later caller that request's tail instead of its own. A byte
/// transport cannot resynchronize a request/response stream after that, so a cancelled read must close
/// the underlying connection instead of trying to preserve the buffer — turning a silent, permanent
/// desync into a loud, self-healing reconnect on the next <see cref="OpenAsync"/>. The same reasoning
/// applies to a cancelled <see cref="WriteAsync"/>: it can leave a half-written command on the wire (the
/// peer sees a truncated line) or a fully-written one whose response nobody will ever read — either way
/// the request/response stream is desynced, and a cancelled write must close the connection too.
/// <see cref="ReadAsync"/> is single-consumer only; concurrent enumeration (two callers reading at
/// once) is undefined. Any implementation of this interface — including test doubles — must reproduce
/// this exact semantics, not a more forgiving approximation of it (a fake that's more forgiving than
/// the real transport can't catch the bug this contract exists to catch; see
/// <c>ScanlineStudio.Core.Audio.FakeAudioEngine</c>'s own doc comment for the same class of correction
/// made there).</summary>
public interface IRadioTransport : IAsyncDisposable
{
    Task OpenAsync(CancellationToken ct);
    Task CloseAsync();
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    IAsyncEnumerable<byte> ReadAsync(CancellationToken ct);
    bool IsOpen { get; }
}
