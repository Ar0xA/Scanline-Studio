namespace ScanlineStudio.Core.Sstv;

/// <summary>Optional side-channel implemented by <see cref="RestartableSstvDecoder"/> only -- not on
/// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder"/> itself, since <c>Directory.Build.props</c>'s
/// <c>TreatWarningsAsErrors</c> would force <see cref="AnalogFmSstvDecoder"/> to declare events it
/// never raises (CS0067) if these lived there instead. Consumers that care (currently only
/// <c>ScanlineStudio.Application.SstvSessionService</c>) test for this via <c>is</c> on their injected
/// <c>ISstvDecoder</c>.
///
/// All maintenance events are invoked synchronously on the thread calling
/// <c>ISstvDecoder.PushSamples</c>, after the swap lock is released and after the triggering chunk's
/// decode attempt. There is no buffering, dropping, or marshaling: a slow subscriber blocks that
/// caller. Every eligible event and every subscriber are attempted in event order. If inner decode
/// failed, that exception takes precedence after maintenance delivery; otherwise the first maintenance
/// subscriber exception propagates after all notifications finish.</summary>
public interface ISstvDecoderMaintenance
{
    /// <summary>Fires at most once per restart cycle when the decoder has gone
    /// <see cref="AnalogFmSstvDecoder.IsIdle"/>-ineligible-for-too-long (past the warning threshold)
    /// without an opportunity to swap. Cleared by the next <see cref="Restarted"/>.</summary>
    event Action? RestartOverdue;

    /// <summary>Fires when the swap happens unconditionally because the critical threshold was
    /// reached regardless of idle state. The swap has already happened by the time this fires --
    /// this exists purely so a caller can also stop capture and notify the user, not to request the
    /// swap itself.</summary>
    event Action? RestartCriticallyOverdue;

    /// <summary>Fires on every swap (both the normal idle-gated one and the unconditional critical
    /// one). The only source signal for "an active <see cref="RestartOverdue"/> warning just
    /// resolved."</summary>
    event Action? Restarted;
}
