using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeReceivedImageBuffer : IReceivedImageBuffer
{
    public IImageSource Current { get; } = new FixedSizeImageSource(1, 1);

    public event Action? Updated;

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public void RaiseUpdated() => Updated?.Invoke();

    private sealed class FixedSizeImageSource(int width, int height) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => new Rgb24[Width];
    }
}
