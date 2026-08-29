using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Abstractions.Sstv;

public interface ISstvEncoder
{
    int SampleRate { get; }

    /// <summary>
    /// Encodes <paramref name="image"/> as <paramref name="mode"/>'s SSTV audio. Implementations must
    /// reject an <paramref name="image"/> whose dimensions don't exactly match
    /// <c>mode.ImageWidth</c>/<c>mode.ImageHeight</c> with an <see cref="ArgumentException"/> thrown
    /// synchronously from this call (not deferred to enumeration) -- callers must not rely on a
    /// mismatched image being silently cropped or padded.
    ///
    /// <paramref name="stationId"/> defaults to <see langword="null"/> (treated identically to
    /// <see cref="StationIdTransmitOptions.None"/> -- nothing enabled), so every pre-existing caller
    /// that doesn't know about the CW-ID/FSK station-ID feature keeps producing byte-identical output.
    /// Placed before <paramref name="ct"/> (not after, despite <c>ct</c> historically being this
    /// method's last parameter) so the many existing 2-arg call sites (<c>EncodeAsync(mode, image)</c>)
    /// are entirely unaffected by this addition.
    ///
    /// <paramref name="sampleRateOffsetHz"/> (stub survey Tier 3, "Clock calibration" piece 1):
    /// manual TX sample-clock correction, in Hz, added to <see cref="SampleRate"/> for tone
    /// generation ONLY -- see <c>AnalogFmSstvEncoder.EncodeAsyncCore</c>'s own doc comment for
    /// exactly which internal calculations this touches vs. which stay at the nominal
    /// <see cref="SampleRate"/> (the TX output bandpass filter is deliberately NOT one of them,
    /// matching legacy's own <c>sstv.cpp:2768/2771</c>). Same "placed before <c>ct</c>" reasoning
    /// as <paramref name="stationId"/> above -- existing shorter call sites are unaffected. Callers
    /// needing the estimate below to match a specific in-flight call byte-for-byte must pass the
    /// SAME resolved value to both, not re-resolve it independently, same requirement
    /// <paramref name="stationId"/> already documents.
    ///
    /// <paramref name="txBpfEnabled"/>/<paramref name="txBpfTapCount"/>/<paramref name="txLpfEnabled"/>/
    /// <paramref name="txLpfFrequencyHz"/> (Options stub backlog item 3,
    /// `docs/plans/options-stub-item3-tx-bpf-lpf-plan.md`): TX-audio-domain shaping, matching legacy's
    /// real <c>CSSTVMOD</c> fields <c>m_bpf</c>/<c>m_bpftap</c>/<c>m_lpf</c>/<c>m_lpffq</c>
    /// (`sstv.cpp:2759-2761,2764,2869,2914,2929`). Defaults (`true`/24/`false`/2000.0) match legacy's
    /// own real shipped defaults exactly, so every pre-existing shorter call site keeps producing
    /// byte-identical output with no source change needed anywhere. Same "placed before <c>ct</c>"
    /// reasoning as <paramref name="stationId"/>/<paramref name="sampleRateOffsetHz"/> above.
    /// <see cref="EstimateSampleCount"/> needs no equivalent parameters -- these settings change the
    /// AUDIO CONTENT of samples, never the sample COUNT.
    /// </summary>
    IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        double sampleRateOffsetHz = 0.0,
        bool txBpfEnabled = true,
        int txBpfTapCount = 24,
        bool txLpfEnabled = false,
        double txLpfFrequencyHz = 2000.0,
        CancellationToken ct = default);

    /// <summary>
    /// The exact total sample count a matching <see cref="EncodeAsync"/> call (same <paramref
    /// name="mode"/>/<paramref name="image"/>/<paramref name="stationId"/>) will emit -- computed
    /// without performing any audio synthesis (no tone generation, no filtering), only the same
    /// per-segment duration accumulation <c>EncodeAsync</c>'s own implementation performs
    /// internally. Used to compute transmit progress (elapsed/total) before playback starts.
    ///
    /// Implementations must apply the identical dimension-mismatch <see cref="ArgumentException"/>
    /// contract <see cref="EncodeAsync"/> documents. Callers that need the estimate to match a
    /// specific in-flight <see cref="EncodeAsync"/> call byte-for-byte must pass the SAME resolved
    /// <paramref name="stationId"/> to both calls, not re-resolve it independently -- station-ID
    /// text can be time-dependent (macro expansion), so two independent resolutions are not
    /// guaranteed to agree.
    ///
    /// Still a real traversal of every scanline segment (not O(1) metadata), so this has a
    /// non-trivial cost proportional to image size -- callers on a UI thread should not assume this
    /// is free.
    /// </summary>
    long EstimateSampleCount(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        double sampleRateOffsetHz = 0.0);
}
