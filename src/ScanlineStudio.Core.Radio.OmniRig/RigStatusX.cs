namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>OmniRig's own <c>RigStatusX</c> enum -- transcribed byte-for-byte from
/// <c>yoniq-old/YONIQ-main/OmniRig_TLB.h</c> (verified against source during plan-review).
/// <see cref="Online"/> is the only status this backend proceeds past; every other value throws a
/// <c>RadioProtocolException</c> carrying OmniRig's own <c>StatusStr</c> text -- see
/// <c>OmniRigRadioProtocol.PollAsync</c>.</summary>
// Public, not internal -- see RigParamX.cs's own comment for why (xUnit Theory/InlineData needs a
// public parameter type; nothing on this assembly's actual API surface exposes this otherwise).
public enum RigStatusX
{
    NotConfigured = 0,
    Disabled = 1,
    PortBusy = 2,
    NotResponding = 3,
    Online = 4,
}
