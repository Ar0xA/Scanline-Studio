using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; set; } = [];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; set; } = [];

    /// <summary>Test-only hook (Tier A Batch 3 chunk 3a round 2): when set, <see cref="RefreshAsync"/>
    /// parks on this until it completes -- lets a test hold a <c>PlayWithPttAsync</c> call inside its
    /// PRE-key window (device resolution runs before PTT is ever published/keyed), the exact window a
    /// concurrent <c>DisposeAsync</c> could previously race past with nothing left to catch the key
    /// that comes after.</summary>
    public Task? Gate { get; set; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Gate is not null)
        {
            await Gate.ConfigureAwait(false);
        }
    }
}
