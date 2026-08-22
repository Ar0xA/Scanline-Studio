using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeAdifUdpStreamer : IAdifUdpStreamer
{
    public AdifUdpSendResult ResultToReturn { get; set; } = new(1, 1);

    public string? LastAdifText { get; private set; }

    public int CallCount { get; private set; }

    // Tier A Batch 10 chunk 10c: lets a test drive LogbookSessionService.LogQsoAsync's post-persist
    // failure path (a throw here must not undo/mask that the QSO was already committed).
    public Exception? ExceptionToThrow { get; set; }

    public Task<AdifUdpSendResult> SendLoggedQsoAsync(string adifText, CancellationToken ct = default)
    {
        CallCount++;
        LastAdifText = adifText;
        return ExceptionToThrow is { } ex ? Task.FromException<AdifUdpSendResult>(ex) : Task.FromResult(ResultToReturn);
    }
}
