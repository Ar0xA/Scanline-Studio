using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Logbook.Tests;

// ReceiveHistoryRecorder never calls SaveAsync or reads Current on this -- it only reads Generation
// (synchronously, alongside its own pixel snapshot) and calls NotifySaved after writing that
// snapshot directly. Current/SaveAsync are stubbed only because the interface requires them.
internal sealed class FakeReceivedImageBuffer : IReceivedImageBuffer
{
    public IImageSource Current { get; } = new ArrayImageSourceStub();

    public double? Progress { get; set; }

    public int Generation { get; set; }

    public event Action? Updated;

    public event Action<string, int>? Saved;

    public List<(string Path, int Generation)> NotifySavedCalls { get; } = [];

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public void NotifySaved(string path, int generation)
    {
        NotifySavedCalls.Add((path, generation));
        Saved?.Invoke(path, generation);
    }

    public void RaiseUpdated() => Updated?.Invoke();

    private sealed class ArrayImageSourceStub : IImageSource
    {
        public int Width => 1;

        public int Height => 1;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => new Rgb24[1];
    }
}
