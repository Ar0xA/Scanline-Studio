namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Optional side-channel implemented by <c>ScanlineStudio.Core.Sstv.WaterfallSource</c> only
/// -- same reasoning as <c>ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration</c>'s own doc comment.
/// Consumers that care (<c>ScanlineStudio.Application.SstvSessionService</c>) test for this via
/// <c>is</c> on the injected <see cref="IWaterfallSource"/>, matching that same established pattern.
///
/// Restart-required-settings backlog item 4 (sample-rate live-apply, 2026-08-27):
/// <see cref="RequestSampleRate"/> must ONLY ever be called by
/// <c>ScanlineStudio.Application.SstvSessionService</c>, at the EXACT moment it commits an RX
/// capture-session restart -- never directly from Options, and never immediately on request. The
/// waterfall shares the RX audio drain thread with the decoder; its sample rate only affects the
/// bin-width label on subsequently-emitted frames, not how existing accumulator content is
/// interpreted, but calling this any earlier than the real hardware capture rate actually changes
/// would mislabel every frame emitted in between.</summary>
public interface IWaterfallSourceReconfiguration
{
    /// <summary>Applies immediately -- no idle-gating, no swap, unlike
    /// <c>ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.RequestSampleRate</c>. Safe to call
    /// from any thread, though see this interface's own doc comment for the ONE caller/timing
    /// contract that actually keeps it correct.</summary>
    void RequestSampleRate(int sampleRate);
}
