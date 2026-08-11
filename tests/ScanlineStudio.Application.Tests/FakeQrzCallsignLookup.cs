using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeQrzCallsignLookup : IQrzCallsignLookup
{
    public QrzLoginResult TestReturnValue { get; set; } = new(true, null);

    public QrzCallsignLookupResult LookupReturnValue { get; set; } = new(true, "Test Name", "Test QTH", "AA00", null);

    public string? LastUsername { get; private set; }

    public string? LastPassword { get; private set; }

    public string? LastCallsign { get; private set; }

    public int TestCallCount { get; private set; }

    public int LookupCallCount { get; private set; }

    public Task<QrzLoginResult> TestCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        TestCallCount++;
        LastUsername = username;
        LastPassword = password;
        return Task.FromResult(TestReturnValue);
    }

    public Task<QrzCallsignLookupResult> LookupAsync(string callsign, string username, string password, CancellationToken ct = default)
    {
        LookupCallCount++;
        LastCallsign = callsign;
        LastUsername = username;
        LastPassword = password;
        return Task.FromResult(LookupReturnValue);
    }
}
