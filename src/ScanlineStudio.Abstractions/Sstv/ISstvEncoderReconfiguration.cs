namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Optional side-channel implemented by <c>ScanlineStudio.Core.Sstv.RestartableSstvEncoder</c>
/// only -- same reasoning as <c>ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration</c>'s own doc
/// comment. Consumers that care (<c>ScanlineStudio.Application.SstvSessionService</c>) test for this
/// via <c>is</c> on the injected <see cref="ISstvEncoder"/>, matching that same established pattern.
///
/// Restart-required-settings backlog item 4 (sample-rate live-apply, 2026-08-27):
/// <see cref="BeginTransmission"/>/<see cref="EndTransmission"/> bracket EVERY call site that reads
/// <see cref="ISstvEncoder.SampleRate"/> (directly, or via <see cref="ISstvEncoder.EstimateSampleCount"/>)
/// and later depends on <see cref="ISstvEncoder.EncodeAsync"/>/<see cref="ISstvEncoder.EncodeBatchedAsync"/>
/// (T1-3, production_audit.md: the real TX path now uses the batched member; RenderSegments/tests
/// still use the per-float one -- this bracket's own requirement applies identically to both, they
/// share the same underlying synthesis) reflecting that SAME rate -- in this codebase, that is both
/// the real TX path (<c>SstvSessionService.TransmitAsync</c>) AND the separate RX-loopback self-test
/// (<c>SstvSessionService.RunLoopbackSelfTestAsync</c>). The bracket must open BEFORE the first
/// <see cref="ISstvEncoder.SampleRate"/>-derived read and close only AFTER the resulting audio has
/// fully played out (or the self-test's encode has fully completed) -- a caller that reads the rate,
/// then calls <c>EndTransmission</c> too early, then calls <see cref="ISstvEncoder.EncodeAsync"/>/
/// <see cref="ISstvEncoder.EncodeBatchedAsync"/> defeats the whole point. The bracket is reference-counted
/// (not a bool) specifically because a real transmission and the loopback self-test can have
/// overlapping brackets in a real window (round-4 plan-review finding C3) -- always call
/// <see cref="EndTransmission"/> from a <c>finally</c>.</summary>
public interface ISstvEncoderReconfiguration
{
    /// <summary>Applies immediately if the bracket count is currently zero; otherwise queues, applied
    /// once <see cref="EndTransmission"/> brings the count back to zero. Throws
    /// <see cref="ArgumentOutOfRangeException"/> synchronously for an unsupported rate -- never
    /// queues an invalid value. Safe to call from any thread.</summary>
    void RequestSampleRate(int sampleRate);

    /// <summary>Opens the bracket (increments the reference count). See this interface's own doc
    /// comment for the exact window this must cover.</summary>
    void BeginTransmission();

    /// <summary>Closes the bracket (decrements the reference count); when the count reaches zero,
    /// drains and applies any <see cref="RequestSampleRate"/> call that was queued while bracketed.
    /// Always call from a <c>finally</c> -- an unbalanced <see cref="BeginTransmission"/> permanently
    /// defers every future sample-rate request.</summary>
    void EndTransmission();
}
