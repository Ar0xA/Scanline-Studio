using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.OmniRig;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Test double for <see cref="IOmniRigComClient"/> -- backs unit tests for
/// <c>OmniRigRadioProtocol</c> without a real OmniRig install (that real implementation is Windows
/// COM-only and unverifiable in this dev environment, see the implementation plan's "Out" scope
/// note). Mirrors <c>FakeHamlibNative</c>'s shape: plain settable fields the test arranges before
/// exercising the protocol, plus call counters for assertions.</summary>
internal sealed class FakeOmniRigComClient : IOmniRigComClient
{
    public RigStatusX Status { get; set; } = RigStatusX.Online;
    public string StatusText { get; set; } = "Online";
    public int FrequencyHz { get; set; } = 14_070_000;
    public RigParamX Tx { get; set; } = RigParamX.PM_RX;
    public RigParamX Mode { get; set; } = RigParamX.PM_SSB_U;
    public RigParamX ReadableParams { get; set; } = RigParamX.PM_FREQ;
    public RigParamX WriteableParams { get; set; } = RigParamX.PM_FREQ;

    public bool Connected { get; private set; }
    public bool Disposed { get; private set; }
    public int ConnectCallCount { get; private set; }

    /// <summary>When set, <see cref="ConnectAsync"/> throws this instead of succeeding -- exercises
    /// <c>OmniRigRadioProtocol</c>'s "OmniRig not reachable" error path.</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>When set, <see cref="ConnectAsync"/> awaits this before completing -- lets a test pin
    /// that connection/activation gets its own timeout budget, separate from each per-call
    /// transaction timeout (round-2 code-review finding on the OmniRig implementation plan).</summary>
    public TimeSpan ConnectDelay { get; set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        ConnectCallCount++;
        if (ConnectDelay > TimeSpan.Zero)
        {
            await Task.Delay(ConnectDelay, ct).ConfigureAwait(false);
        }

        if (ConnectException is { } ex)
        {
            throw ex;
        }

        Connected = true;
    }

    public Task<RigStatusX> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public Task<string> GetStatusTextAsync(CancellationToken ct) => Task.FromResult(StatusText);

    public Task<int> GetFrequencyHzAsync(CancellationToken ct) => Task.FromResult(FrequencyHz);

    public Task SetFrequencyHzAsync(int hz, CancellationToken ct)
    {
        FrequencyHz = hz;
        return Task.CompletedTask;
    }

    public Task<RigParamX> GetTxAsync(CancellationToken ct) => Task.FromResult(Tx);

    public Task SetTxAsync(RigParamX value, CancellationToken ct)
    {
        Tx = value;
        return Task.CompletedTask;
    }

    public Task<RigParamX> GetModeAsync(CancellationToken ct) => Task.FromResult(Mode);

    public Task SetModeAsync(RigParamX value, CancellationToken ct)
    {
        Mode = value;
        return Task.CompletedTask;
    }

    public Task<RigParamX> GetReadableParamsAsync(CancellationToken ct) => Task.FromResult(ReadableParams);

    public Task<RigParamX> GetWriteableParamsAsync(CancellationToken ct) => Task.FromResult(WriteableParams);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
