using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>See spec/03-cat-layer.md's OmniRig section and the implementation plan's "COM binding"
/// section. The frozen OmniRig COM surface, at the granularity <see cref="OmniRigRadioProtocol"/>
/// actually needs -- ordinary C# types at this boundary (an <see langword="int"/> frequency, not a
/// raw COM value). Real COM marshaling details live in <see cref="OmniRigComClient"/>;
/// <c>FakeOmniRigComClient</c> (test project) backs unit tests without a real OmniRig install.
/// Never called via raw COM interop directly from <see cref="OmniRigRadioProtocol"/> -- this
/// interface is the only seam.
///
/// <b>Tx and Mode are two distinct properties</b>, both <c>RigParamX</c>-typed (OmniRig's own
/// <c>IRigX</c> shape, DISPID 17 and 18 respectively) -- never conflate them into one accessor
/// pair.</summary>
internal interface IOmniRigComClient : IAsyncDisposable
{
    /// <summary>Activates OmniRig by CLSID (<c>Type.GetTypeFromCLSID</c> + <c>Activator.CreateInstance</c>)
    /// and resolves its <c>Rig1</c> automation object. Whether this attaches to an already-running
    /// <c>OmniRig.exe</c> or launches a new one is OmniRig's own <c>LocalServer32</c> registration
    /// behavior, not a choice made here -- see the implementation plan's "Connection lifecycle"
    /// section. Runs on a thread-pool (MTA) thread, never the caller's own thread (the plan's
    /// concurrency contract) -- callers must not assume this returns synchronously on the calling
    /// thread's apartment.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary><c>IRigX.Status</c>, DISPID 6.</summary>
    Task<RigStatusX> GetStatusAsync(CancellationToken ct);

    /// <summary><c>IRigX.StatusStr</c>, DISPID 7 -- OmniRig's own human-readable status text, used
    /// verbatim in error messages rather than re-derived from <see cref="RigStatusX"/>.</summary>
    Task<string> GetStatusTextAsync(CancellationToken ct);

    /// <summary><c>IRigX.get_Freq</c>, DISPID 8. <c>VT_I4</c> on the wire (32-bit) -- see the
    /// implementation plan's "`Freq` is 32-bit" note. Never widen to <see langword="long"/> below
    /// this seam.</summary>
    Task<int> GetFrequencyHzAsync(CancellationToken ct);

    /// <summary><c>IRigX.set_Freq</c>, DISPID 8.</summary>
    Task SetFrequencyHzAsync(int hz, CancellationToken ct);

    /// <summary><c>IRigX.get_Tx</c>, DISPID 17 -- PTT state, returned as OmniRig's raw
    /// <see cref="RigParamX"/> flag (<c>PM_TX</c>/<c>PM_RX</c>/<c>PM_UNKNOWN</c>). Legacy reads/sets
    /// both <c>PM_TX</c> and <c>PM_RX</c> explicitly -- <c>PM_RX</c> is not simply "absence of
    /// <c>PM_TX</c>," so callers must interpret the returned flag, not just test one bit.</summary>
    Task<RigParamX> GetTxAsync(CancellationToken ct);

    /// <summary><c>IRigX.set_Tx</c>, DISPID 17. <paramref name="value"/> must be <c>PM_TX</c> or
    /// <c>PM_RX</c> -- never a mode flag.</summary>
    Task SetTxAsync(RigParamX value, CancellationToken ct);

    /// <summary><c>IRigX.get_Mode</c>, DISPID 18 -- OmniRig's own mode flag
    /// (<c>PM_SSB_U</c>/<c>PM_SSB_L</c>/<c>PM_CW_U</c>/<c>PM_CW_L</c>/<c>PM_AM</c>/<c>PM_FM</c>/
    /// <c>PM_DIG_U</c>/<c>PM_DIG_L</c>/<c>PM_UNKNOWN</c>). Mapped to/from <see cref="RadioMode"/>
    /// by <see cref="RigParamXMapper"/>, not by this seam.</summary>
    Task<RigParamX> GetModeAsync(CancellationToken ct);

    /// <summary><c>IRigX.set_Mode</c>, DISPID 18. <paramref name="value"/> must be a single mode
    /// flag -- never <c>PM_TX</c>/<c>PM_RX</c>.</summary>
    Task SetModeAsync(RigParamX value, CancellationToken ct);

    /// <summary><c>IRigX.ReadableParams</c> -- read once per connection and logged only
    /// (<c>OmniRigRadioProtocol.PollAsync</c>), NOT consulted to derive
    /// <see cref="RadioCapabilities"/> -- that stays the fixed set every legacy call site actually
    /// needs. Available for a future finer-grained capability model, per the implementation
    /// plan.</summary>
    Task<RigParamX> GetReadableParamsAsync(CancellationToken ct);

    /// <summary><c>IRigX.WriteableParams</c> -- see <see cref="GetReadableParamsAsync"/>.</summary>
    Task<RigParamX> GetWriteableParamsAsync(CancellationToken ct);
}
