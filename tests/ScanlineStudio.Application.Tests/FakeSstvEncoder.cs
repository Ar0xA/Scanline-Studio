using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeSstvEncoder : ISstvEncoder
{
    public int SampleRate { get; init; } = 11025;

    public float[] SamplesToYield { get; init; } = [0.1f, 0.2f, 0.3f];

    /// <summary>Captures whatever <see cref="SstvSessionService.TransmitAsync"/> resolved and passed
    /// in -- lets tests assert on the settings-resolution logic (gates, WPM/frequency fallback,
    /// callsign passthrough) directly, without needing to decode real DSP output.</summary>
    public StationIdTransmitOptions? LastStationIdOptions { get; private set; }

    public async IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastStationIdOptions = stationId;
        foreach (var sample in SamplesToYield)
        {
            ct.ThrowIfCancellationRequested();
            yield return sample;
            await Task.Yield();
        }
    }
}
