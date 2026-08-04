namespace Yoniq.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md's "Polling" section. Replaces legacy <c>CRADIOPARA</c>'s
/// <c>PollType</c>/<c>PollScan</c>. <see cref="Scan"/> is not implemented in the Phase 2
/// <c>RadioController</c> reference implementation — <c>ConnectAsync</c> with a
/// <see cref="RadioConnectionSpec"/> whose <c>Strategy</c> is <see cref="Scan"/> throws
/// <see cref="NotSupportedException"/> rather than silently behaving as <see cref="Continuous"/>, per
/// spec/01-architecture.md's error-handling rule (no silent partial behavior).</summary>
public enum PollingStrategy
{
    Continuous,
    OnDemand,
    Scan,
}
