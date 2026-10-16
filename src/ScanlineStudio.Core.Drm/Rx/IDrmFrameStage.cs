namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The seam between acquisition (P5b) and per-frame demodulation (P5c): QSSTV's
/// <c>demodulator::channelEstimation</c> and the channel-estimation parts of <c>init</c>/<c>frameSync</c>
/// (drmrx/demodulator.cpp:40-85, 91, 158-162, 502-516, 524-1132).</summary>
/// <remarks>All calls come from <see cref="DrmDemodulator"/> on the receive worker, in QSSTV's order.</remarks>
internal interface IDrmFrameStage
{
    /// <summary>The frame-stage share of <c>demodulator::init</c> (demodulator.cpp:50-52,63-65,71-84):
    /// fac_not_valid_counter, mode_and_occupancy_code_last, the SNR counters, the sample-rate converter fields,
    /// the cell-count and noise tables. FACAvailable and transmission_frame_buffer_data_valid (:43,68) live on
    /// <see cref="DrmDemodState"/> and are reset by <see cref="DrmDemodulator"/>.</summary>
    void Reset();

    /// <summary>Start of every <c>demodulate</c> call (demodulator.cpp:91), after <see cref="DrmDemodulator"/> has
    /// cleared <c>transmission_frame_buffer_data_valid</c>.</summary>
    void BeginDemodulate();

    /// <summary>The end of <c>frameSync</c> (demodulator.cpp:502-516): pilot history and Wiener-filter
    /// scratch reset after the 2-D window has been filled.</summary>
    void OnFrameSync();

    /// <summary><c>channelEstimation()</c> (demodulator.cpp:524-1132), called whenever frame sync holds.
    /// Returns true when a frame was equalized, where QSSTV then runs <c>channel_decoding()</c> and
    /// <c>srcDecoder-&gt;decode()</c> (P6). Must keep <c>InputSamplesBufferRequest</c> current and set
    /// <c>DoSynchronize</c> on QSSTV's SNR rule (demodulator.cpp:1090-1112).</summary>
    bool ProcessFrame(DrmDemodState state);
}
