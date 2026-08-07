using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeGridTrackerStreamer : IGridTrackerStreamer
{
    public bool ReturnValue { get; set; } = true;

    public string? LastAdifText { get; private set; }

    public int CallCount { get; private set; }

    public Task<bool> SendLoggedQsoAsync(string adifText, CancellationToken ct = default)
    {
        CallCount++;
        LastAdifText = adifText;
        return Task.FromResult(ReturnValue);
    }
}
