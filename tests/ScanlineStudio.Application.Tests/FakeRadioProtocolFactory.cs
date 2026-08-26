using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeRadioProtocol : IRadioProtocol
{
    public string RigId { get; set; } = "fake-rig";

    public RadioCapabilities Capabilities { get; set; } = RadioCapabilities.None;

    public RadioState StateToReturn { get; set; } = new(14_230_000, RadioMode.Usb, false, null, DateTimeOffset.UtcNow);

    public Exception? PollExceptionToThrow { get; set; }

    // Tier A Batch 10 chunk 10c: lets a test drive the double-fault scenario (PollAsync throws AND
    // DisposeAsync also throws) -- RadioSessionService.TestConnectionAsync must report the ORIGINAL
    // poll failure, not have it masked by an unrelated teardown error.
    public Exception? DisposeExceptionToThrow { get; set; }

    public bool Disposed { get; private set; }

    /// <summary>Test-only hook: when set, <see cref="PollAsync"/> awaits this before returning --
    /// lets a test hold a <c>TestPttAsync</c> call in-flight to prove its single-flight guard
    /// rejects a concurrent second call.</summary>
    public Task? PollGate { get; set; }

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        if (PollGate is not null)
        {
            await PollGate.ConfigureAwait(false);
        }

        if (PollExceptionToThrow is { } ex)
        {
            throw ex;
        }

        return StateToReturn;
    }

    public Task SetFrequencyAsync(long hz, CancellationToken ct) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct) => Task.CompletedTask;

    public List<int?> SetBandwidthCalls { get; } = [];

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct)
    {
        SetBandwidthCalls.Add(bandwidthHz);
        return Task.CompletedTask;
    }

    public List<bool> SetPttCalls { get; } = [];

    /// <summary>Scripts <see cref="SetPttAsync"/>'s next several calls, dequeued one per call --
    /// <see langword="null"/> means "succeed." An empty queue means every call succeeds. Lets a test
    /// drive <c>RadioSessionService.TestPttAsync</c>'s bounded un-key retry (fail N times then
    /// succeed, or fail every time).</summary>
    public Queue<Exception?> SetPttExceptionsToThrow { get; } = new();

    public Task SetPttAsync(bool tx, CancellationToken ct)
    {
        SetPttCalls.Add(tx);
        if (SetPttExceptionsToThrow.Count > 0 && SetPttExceptionsToThrow.Dequeue() is { } ex)
        {
            throw ex;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        if (DisposeExceptionToThrow is { } ex)
        {
            throw ex;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Claims whatever <see cref="RadioConnectionSpec"/> subtype <see cref="HandlesType"/> names
/// (default <see cref="RigctldConnectionSpec"/>, this project's only real backend besides "none") --
/// mirrors the real per-backend <c>IRadioProtocolFactory</c> registrations
/// (<c>ScanlineStudio.Host.Program</c>'s own <c>AddSingleton&lt;IRadioProtocolFactory, ...&gt;</c> calls),
/// one instance per test rather than a single fake that claims everything, so a "0 matches"/"&gt;1
/// matches" resolution-ambiguity test can compose multiple of these deliberately.</summary>
internal sealed class FakeRadioProtocolFactory(FakeRadioProtocol protocol, Type? handlesType = null) : IRadioProtocolFactory
{
    private readonly Type _handlesType = handlesType ?? typeof(RigctldConnectionSpec);

    public List<RadioConnectionSpec> CreateCalls { get; } = [];

    // Tier A Batch 10 chunk 10c: lets a test drive Create() throwing -- RadioSessionService.
    // TestConnectionAsync must report a failure result, not let this propagate uncaught (its own
    // interface contract says it always returns a result).
    public Exception? CreateExceptionToThrow { get; set; }

    public bool CanHandle(RadioConnectionSpec spec) => spec.GetType() == _handlesType;

    public IRadioProtocol Create(RadioConnectionSpec spec)
    {
        CreateCalls.Add(spec);
        if (CreateExceptionToThrow is { } ex)
        {
            throw ex;
        }

        return protocol;
    }
}
