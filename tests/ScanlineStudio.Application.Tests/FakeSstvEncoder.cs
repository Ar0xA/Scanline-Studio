using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeSstvEncoder : ISstvEncoder, ISstvEncoderReconfiguration
{
    public int SampleRate { get; set; } = 11025;

    // Restart-required-settings backlog item 4 (2026-08-27).
    public int TransmissionRefCount { get; private set; }

    public int RequestSampleRateCallCount { get; private set; }

    public void RequestSampleRate(int sampleRate)
    {
        RequestSampleRateCallCount++;
        SampleRate = sampleRate;
    }

    public void BeginTransmission() => TransmissionRefCount++;

    public void EndTransmission() => TransmissionRefCount--;

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

    /// <summary>Clock calibration (stub survey Tier 3), same reasoning as
    /// <see cref="LastEstimateStationIdOptions"/>/<see cref="LastStationIdOptions"/> above -- lets a
    /// test assert both calls received the exact same resolved offset, pinning the round-2
    /// plan-review requirement that <see cref="SstvSessionService.TransmitAsync"/> must reuse one
    /// resolved value for both the estimate and the real encode.</summary>
    public double? LastEstimateSampleRateOffsetHz { get; private set; }

    public double? LastSampleRateOffsetHz { get; private set; }

    public long EstimateSampleCount(SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions? stationId = null, double sampleRateOffsetHz = 0.0)
    {
        LastEstimateStationIdOptions = stationId;
        LastEstimateSampleRateOffsetHz = sampleRateOffsetHz;
        return EstimateSampleCountOverride ?? SamplesToYield.Length;
    }

    /// <summary>Loopback self-test tests: awaited before the FIRST yielded sample -- lets a test hold
    /// a <see cref="SstvSessionService.TransmitAsync"/> call open past the point
    /// <c>PlayWithPttAsync</c> sets <c>_transmitInFlight</c> (which happens before enumeration of
    /// this method's <see cref="IAsyncEnumerable{Single}"/> ever starts), so a concurrent call racing
    /// against it observes the guard deterministically instead of depending on timing.</summary>
    public Func<Task>? BeforeFirstYield { get; set; }

    public async IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        double sampleRateOffsetHz = 0.0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastStationIdOptions = stationId;
        LastSampleRateOffsetHz = sampleRateOffsetHz;
        if (BeforeFirstYield is { } beforeFirstYield)
        {
            await beforeFirstYield();
        }

        foreach (var sample in SamplesToYield)
        {
            ct.ThrowIfCancellationRequested();
            yield return sample;
            await Task.Yield();
        }
    }
}
