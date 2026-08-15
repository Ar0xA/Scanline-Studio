using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeAdifUdpStreamer : IAdifUdpStreamer
{
    public AdifUdpSendResult ResultToReturn { get; set; } = new(1, 1);

    public string? LastAdifText { get; private set; }

    public int CallCount { get; private set; }

    public Task<AdifUdpSendResult> SendLoggedQsoAsync(string adifText, CancellationToken ct = default)
    {
        CallCount++;
        LastAdifText = adifText;
        return Task.FromResult(ResultToReturn);
    }
}
