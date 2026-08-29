using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook.Tests;

/// <summary>ui_transition_plan.md step 6 (T2-4). Settable, not event-driven -- ReceiveHistoryRecorder
/// only ever reads <see cref="Current"/> synchronously at the instant a reception completes/is
/// abandoned, never subscribes to a stream of it.</summary>
internal sealed class FakeRadioStateProvider : IRadioStateProvider
{
    public RadioState? Current { get; set; }
}
