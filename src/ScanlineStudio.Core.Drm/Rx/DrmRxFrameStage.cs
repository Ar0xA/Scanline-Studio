namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The P6/P7a seam: QSSTV's <c>srcDecoder-&gt;decode()</c>, called after every <c>channel_decoding()</c>
/// (demodulator.cpp:158-162), on the receive worker.</summary>
internal interface IDrmMscSink
{
    /// <summary>One equalized frame has been channel-decoded. The source decoder acts only when
    /// <see cref="DrmChannelDecoder.MscDataValid"/> is 1 (sourcedecoder.cpp:78); the decoder's buffers are reused
    /// by the next frame, so copy what must outlive the call. <paramref name="state"/> carries the globals source
    /// decoding also reads, such as <c>robustness_mode</c> (sourcedecoder.cpp:830-841).</summary>
    void OnChannelDecoded(DrmChannelDecoder decoder, DrmDemodState state);

    /// <summary><c>srcDecoder-&gt;init()</c>, the last step of drmRx::init (drmrx.cpp:36).</summary>
    void Init();
}

/// <summary>The frame stage after acquisition: QSSTV's <c>channelEstimation()</c>, and when it equalized a frame
/// <c>channel_decoding()</c> and the source decoder (demodulator.cpp:158-162).</summary>
/// <remarks>The receiver reset (drmRx::init, drmrx.cpp:20-36) is <see cref="DrmDemodulator.Reset"/> followed by
/// <see cref="ResetReceiver"/>. Not thread-safe: one instance per receive worker.</remarks>
internal sealed class DrmRxFrameStage : IDrmFrameStage
{
    public DrmRxFrameStage(DrmChannelEstimator estimator, DrmChannelDecoder decoder, IDrmMscSink? sink = null)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(decoder);
        Estimator = estimator;
        Decoder = decoder;
        Sink = sink;
    }

    public DrmChannelEstimator Estimator { get; }

    public DrmChannelDecoder Decoder { get; }

    public IDrmMscSink? Sink { get; }

    /// <summary>drmRx::init's channel- and source-decoding part (drmrx.cpp:30-36), after <see cref="DrmDemodulator.Reset"/>.</summary>
    public void ResetReceiver(DrmDemodState st)
    {
        Decoder.Reset(st);
        Sink?.Init();
    }

    /// <inheritdoc/>
    public void Reset() => Estimator.Reset();

    /// <inheritdoc/>
    public void BeginDemodulate() => Estimator.BeginDemodulate();

    /// <inheritdoc/>
    public void OnFrameSync() => Estimator.OnFrameSync();

    /// <inheritdoc/>
    public bool ProcessFrame(DrmDemodState state)
    {
        if (!Estimator.ProcessFrame(state))
            return false;
        Decoder.Decode(state);
        Sink?.OnChannelDecoded(Decoder, state);
        return true;
    }
}
