using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeRadioProtocol : IRadioProtocol
{
    public string RigId { get; set; } = "fake-rig";

    public RadioCapabilities Capabilities { get; set; } = RadioCapabilities.None;

    public RadioState StateToReturn { get; set; } = new(14_230_000, RadioMode.Usb, false, null, DateTimeOffset.UtcNow);

    public Exception? PollExceptionToThrow { get; set; }

    public bool Disposed { get; private set; }

    public Task<RadioState> PollAsync(CancellationToken ct)
        => PollExceptionToThrow is { } ex ? Task.FromException<RadioState>(ex) : Task.FromResult(StateToReturn);

    public Task SetFrequencyAsync(long hz, CancellationToken ct) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct) => Task.CompletedTask;

    public Task SetPttAsync(bool tx, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
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

    public bool CanHandle(RadioConnectionSpec spec) => spec.GetType() == _handlesType;

    public IRadioProtocol Create(RadioConnectionSpec spec)
    {
        CreateCalls.Add(spec);
        return protocol;
    }
}
