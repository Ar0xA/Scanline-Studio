using Yoniq.Abstractions.Imaging;
using Yoniq.Core.Imaging;

namespace Yoniq.Application.Tests;

internal sealed class FakeReceivedImageBuffer : IReceivedImageBuffer
{
    public IImageSource Current { get; } = new ArrayImageSource(1, 1, new Rgb24[1]);

    public event Action? Updated;

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public void RaiseUpdated() => Updated?.Invoke();
}
