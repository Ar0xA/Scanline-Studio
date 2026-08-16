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

    /// <summary>Code-review finding (TX send-progress feature): separate from
    /// <see cref="LastStationIdOptions"/> (which <see cref="EncodeAsync"/> writes) so a test can
    /// assert BOTH calls received the exact same instance -- pins the plan-review requirement that
    /// <see cref="SstvSessionService.TransmitAsync"/> must reuse one resolved <c>stationId</c> for
    /// both the estimate and the real encode, never re-resolve independently.</summary>
    public StationIdTransmitOptions? LastEstimateStationIdOptions { get; private set; }

    /// <summary>Consistent with <see cref="SamplesToYield"/>'s own length -- so a test asserting on
    /// <see cref="SstvSessionService.TransmitProgressChanged"/>'s reported fraction is checking
    /// against the same total the fake's <see cref="EncodeAsync"/> will actually emit, not a
    /// fiction. Overridable via <see cref="EstimateSampleCountOverride"/> for a test that wants a
    /// mismatch (e.g. covering the total&lt;=0 guard).</summary>
    public long? EstimateSampleCountOverride { get; init; }

    public long EstimateSampleCount(SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions? stationId = null)
    {
        LastEstimateStationIdOptions = stationId;
        return EstimateSampleCountOverride ?? SamplesToYield.Length;
    }

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
