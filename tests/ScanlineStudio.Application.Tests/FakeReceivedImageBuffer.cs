using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeReceivedImageBuffer : IReceivedImageBuffer
{
    public IImageSource Current { get; } = new ArrayImageSource(1, 1, new Rgb24[1]);

    public double? Progress { get; set; }

    public int Generation { get; set; }

    public event Action? Updated;

    public event Action<string, int>? Saved;

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public void NotifySaved(string path, int generation) => Saved?.Invoke(path, generation);

    public void RaiseUpdated() => Updated?.Invoke();

    public void RaiseSaved(string path, int generation) => Saved?.Invoke(path, generation);
}
