using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>See <see cref="IRadioStateProvider"/>'s own doc comment for why this exists as a
/// separate, narrower adapter rather than handing <see cref="IRadioController"/> itself (or a cast
/// of <c>IRadioSessionService</c>) to a consumer like <c>ReceiveHistoryRecorder</c>.</summary>
public sealed class RadioStateProvider : IRadioStateProvider
{
    private readonly IRadioController _controller;

    public RadioStateProvider(IRadioController controller)
    {
        _controller = controller;
    }

    public RadioState? Current
    {
        get
        {
            try
            {
                return _controller.LastKnownState;
            }
            catch (ObjectDisposedException)
            {
                // Auditor plan-review (2026-08-29): IRadioController.LastKnownState is backed by a
                // BehaviorSubject disposed in RadioController.DisposeAsync -- reading .Value after
                // dispose throws. A decode still draining during app shutdown is a real, reachable
                // caller here, not a hypothetical -- treated the same as "no radio connected."
                return null;
            }
        }
    }
}
