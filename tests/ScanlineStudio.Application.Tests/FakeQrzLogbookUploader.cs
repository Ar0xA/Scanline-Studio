using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeQrzLogbookUploader : IQrzLogbookUploader
{
    public QrzUploadResult ReturnValue { get; set; } = new(true, "1", null);

    public string? LastApiKey { get; private set; }

    public int CallCount { get; private set; }

    public Task<QrzUploadResult> UploadAsync(string adifText, string apiKey, CancellationToken ct = default)
    {
        CallCount++;
        LastApiKey = apiKey;
        return Task.FromResult(ReturnValue);
    }
}
