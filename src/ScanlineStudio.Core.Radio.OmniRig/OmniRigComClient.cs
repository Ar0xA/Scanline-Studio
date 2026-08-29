using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>Real <c>[ComImport]</c> implementation of <see cref="IOmniRigComClient"/>. Hand-authored
/// against <c>yoniq-old/YONIQ-main/OmniRig_TLB.h</c> (see the implementation plan's "COM binding"
/// section for why this is hand-authored rather than <c>tlbimp</c>-generated or late-bound
/// reflection) -- every GUID/DISPID below is transcribed byte-for-byte from that header, not
/// re-derived from memory.
///
/// <b>Threading contract</b> (implementation plan's "Concurrency contract"): every member here runs
/// on a thread-pool (MTA) thread via <see cref="RunOnThreadPoolAsync{T}"/>, never on the calling
/// thread directly -- <see cref="RadioController"/> can resolve a protocol factory synchronously
/// inline on Avalonia's STA UI thread, and COM activation there would make the resulting RCW
/// apartment-affine to that thread. Call-site serialization (one COM call in flight at a time) is
/// <see cref="OmniRigRadioProtocol"/>'s job via its own <c>SemaphoreSlim</c>, not duplicated
/// here.</summary>
[SupportedOSPlatform("windows")]
internal sealed class OmniRigComClient : IOmniRigComClient
{
    /// <summary>CLSID_OmniRigX -- <c>OmniRig_TLB.cpp:46</c>.</summary>
    private const string ClsidOmniRigX = "0839E8C6-ED30-4950-8087-966F970F0CAE";

    /// <summary>OmniRig's <c>IOmniRigX</c> automation interface -- <c>(4416) Dual OleAutomation
    /// Dispatchable</c> (<c>OmniRig_TLB.h:283</c>), so <c>InterfaceIsIDispatch</c> +
    /// <c>[DispId]</c> per member is valid and avoids any vtable-order transcription risk. IID from
    /// <c>OmniRig_TLB.cpp:44</c>.</summary>
    [ComImport]
    [Guid("501A2858-3331-467A-837A-989FDEDACC7D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IOmniRigXComInterface
    {
        /// <summary><c>IOmniRigX.Rig1</c>, DISPID 3 (<c>OmniRig_TLB.h:1135</c>). Declared
        /// <see langword="object"/>/<c>IDispatch</c> (not <c>IRigXComInterface</c> directly) per the
        /// header's own <c>VT_USERDEFINED</c> over <c>LPDISPATCH</c> return marshaling
        /// (<c>OmniRig_TLB.h:1137</c>) -- cast to <see cref="IRigXComInterface"/> at the call site,
        /// which triggers a real <c>QueryInterface</c> for <c>IID_IRigX</c>.</summary>
        [DispId(3)]
        object Rig1 { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }

    /// <summary>OmniRig's <c>IRigX</c> automation interface -- same Dual/Dispatchable shape as
    /// <see cref="IOmniRigXComInterface"/>. IID from <c>OmniRig_TLB.cpp:47</c>. Only the members
    /// this backend's real call sites need are declared (spec/03-cat-layer.md, "Legacy's real
    /// OmniRig call sites") -- <c>Rig2</c>/Split/Rit/Xit/etc. are out of scope, see the
    /// implementation plan.</summary>
    [ComImport]
    [Guid("D30A7E51-5862-45B7-BFFA-6415917DA0CF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IRigXComInterface
    {
        /// <summary>DISPID 2 (<c>OmniRig_TLB.h:1670</c>).</summary>
        [DispId(2)]
        int ReadableParams { get; }

        /// <summary>DISPID 3 (<c>OmniRig_TLB.h:1686</c>).</summary>
        [DispId(3)]
        int WriteableParams { get; }

        /// <summary>DISPID 6.</summary>
        [DispId(6)]
        int Status { get; }

        /// <summary>DISPID 7.</summary>
        [DispId(7)]
        string StatusStr { get; }

        /// <summary>DISPID 8. <c>VT_I4</c> on the wire (32-bit, <c>OmniRig_TLB.h:1770-1772</c>) --
        /// never widen to <see langword="long"/> at this boundary.</summary>
        [DispId(8)]
        int Freq { get; set; }

        /// <summary>DISPID 17 -- PTT, holds a single <see cref="RigParamX.PM_TX"/>/
        /// <see cref="RigParamX.PM_RX"/> flag.</summary>
        [DispId(17)]
        int Tx { get; set; }

        /// <summary>DISPID 18 -- a distinct property from <see cref="Tx"/>, never conflated with
        /// it.</summary>
        [DispId(18)]
        int Mode { get; set; }
    }

    private IRigXComInterface? _rig1;
    private object? _omniRigX;
    private bool _disposed;

    // Code-review finding (round 1): catches InvalidCastException/PlatformNotSupportedException too,
    // not just COMException -- a failed QueryInterface for IID_IOmniRigX/IID_IRigX (a wrong/corrupt
    // OmniRig install registering the wrong version) surfaces as InvalidCastException, and a host
    // with the BuiltInComInterop feature switch disabled throws PlatformNotSupportedException; both
    // are real "OmniRig isn't usable here" outcomes this backend's callers should see as
    // RadioProtocolException like every other activation failure, not an unrelated raw exception type.
    public Task ConnectAsync(CancellationToken ct) => RunOnThreadPoolAsync(() =>
    {
        try
        {
            var comType = Type.GetTypeFromCLSID(new Guid(ClsidOmniRigX));
            var instance = Activator.CreateInstance(comType!)
                ?? throw new RadioProtocolException("OmniRig COM activation returned no instance.");
            var omniRigX = (IOmniRigXComInterface)instance;
            _omniRigX = instance;
            _rig1 = (IRigXComInterface)omniRigX.Rig1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            throw new RadioProtocolException(
                "Could not start or attach to OmniRig -- confirm OmniRig is installed and registered on this system.", ex);
        }
    }, ct);

    public Task<RigStatusX> GetStatusAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => (RigStatusX)Rig1.Status, ct);

    public Task<string> GetStatusTextAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => Rig1.StatusStr, ct);

    public Task<int> GetFrequencyHzAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => Rig1.Freq, ct);

    public Task SetFrequencyHzAsync(int hz, CancellationToken ct) =>
        RunOnThreadPoolAsync(() => Rig1.Freq = hz, ct);

    public Task<RigParamX> GetTxAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => (RigParamX)Rig1.Tx, ct);

    public Task SetTxAsync(RigParamX value, CancellationToken ct) =>
        RunOnThreadPoolAsync(() => Rig1.Tx = (int)value, ct);

    public Task<RigParamX> GetModeAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => (RigParamX)Rig1.Mode, ct);

    public Task SetModeAsync(RigParamX value, CancellationToken ct) =>
        RunOnThreadPoolAsync(() => Rig1.Mode = (int)value, ct);

    public Task<RigParamX> GetReadableParamsAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => (RigParamX)Rig1.ReadableParams, ct);

    public Task<RigParamX> GetWriteableParamsAsync(CancellationToken ct) =>
        RunOnThreadPoolAsync(() => (RigParamX)Rig1.WriteableParams, ct);

    private IRigXComInterface Rig1 =>
        _rig1 ?? throw new InvalidOperationException($"{nameof(OmniRigComClient)} is not connected -- call {nameof(ConnectAsync)} first.");

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return new ValueTask(RunOnThreadPoolAsync(() =>
        {
            if (_rig1 is not null)
            {
                Marshal.FinalReleaseComObject(_rig1);
                _rig1 = null;
            }

            if (_omniRigX is not null)
            {
                Marshal.FinalReleaseComObject(_omniRigX);
                _omniRigX = null;
            }
        }, CancellationToken.None));
    }

    /// <summary>Runs <paramref name="action"/> on a thread-pool thread -- the one place this class
    /// enforces the "never on the caller's own thread" contract (this class's own doc comment).
    /// <see cref="Task.Run(Action)"/> already schedules onto the thread pool; the .NET runtime lazily
    /// initializes MTA COM on whichever pool thread first touches an RCW, which is sufficient here
    /// since this client never subscribes to OmniRig's own events (no inbound callback needing an
    /// STA message pump -- see the implementation plan's "Concurrency contract").</summary>
    private static Task RunOnThreadPoolAsync(Action action, CancellationToken ct) =>
        Task.Run(action, ct);

    private static Task<T> RunOnThreadPoolAsync<T>(Func<T> func, CancellationToken ct) =>
        Task.Run(func, ct);
}
