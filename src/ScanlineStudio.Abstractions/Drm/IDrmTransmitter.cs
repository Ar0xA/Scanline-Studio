namespace ScanlineStudio.Abstractions.Drm;

/// <summary>The DRM (HamDRM) transmitter: turns one file into QSSTV-identical 48 kHz audio. App-lifetime,
/// because it holds QSSTV's time-stamped TX file name (drmtx.cpp:45-78) and BSR transport-ID counter. QSSTV
/// takes a new stamp on every Start (prepareTx → applyTemplate → forgetTxFileName, txwidget.cpp:337-341,
/// drmtx.cpp:339-342); only a FIX resend reuses the previous one. Each <see cref="Prepare"/> builds a fresh
/// transmission pipeline, as QSSTV does (drmtransmitter.cpp:37-39).</summary>
public interface IDrmTransmitter
{
    /// <summary>Output rate of every transmission, 48000 Hz.</summary>
    int SampleRate { get; }

    /// <summary>Builds one transmission and counts its frames. CPU-bound; run it off the UI thread.</summary>
    /// <param name="payload">The file bytes: the JPEG 2000 image when <paramref name="isImage"/>.</param>
    /// <param name="fileName">Source file name; QSSTV prefixes the UTC stamp and, for an image,
    /// replaces the extension with ".jp2" (drmtx.cpp:50-78,114).</param>
    /// <param name="isImage">True for the JP2 image path, false for a binary file sent as is.</param>
    /// <param name="newTxFileName">True for a normal send: the stamp is cleared first, under the same lock, so
    /// the name (and its transport ID) is new. False reuses the current stamp, which only a FIX resend does.</param>
    IDrmTransmission Prepare(ReadOnlySpan<byte> payload, string fileName, bool isImage, DrmTxSettings settings, bool newTxFileName);

    /// <summary>drmTx::forgetTxFileName: the next transmission takes a new time stamp. QSSTV calls it on every
    /// Start and when a DRM parameter changes (drmtx.cpp:339-342, txwidget.cpp:201-218,337-341).</summary>
    void ForgetTxFileName();
}

/// <summary>One prepared DRM transmission. Single use.</summary>
public interface IDrmTransmission
{
    /// <summary>On-air 400 ms DRM frames.</summary>
    int FrameCount { get; }

    /// <summary>Exact output length at <see cref="IDrmTransmitter.SampleRate"/>: the VOX tone, if
    /// enabled, plus <see cref="FrameCount"/> × 19200.</summary>
    long SampleCount { get; }

    /// <summary>QSSTV's displayed duration, numTxFrames × 0.4 s × 1.005 (drmtransmitter.cpp:116), where numTxFrames is
    /// the MOT segment schedule length. Display only: it is shorter than <see cref="SampleCount"/>.</summary>
    TimeSpan DisplayDuration { get; }

    /// <summary>The on-air file name, as transmitted in the MOT header (Latin-1).</summary>
    string OnAirName { get; }

    /// <summary>Streams the audio, mono float at sample/32768: the VOX tone, then one block per DRM
    /// frame. Frames are produced on a thread-pool task into a bounded channel of 3 frames; the producer
    /// waits (never drops) when the consumer falls behind. Cancelling or abandoning the enumeration
    /// stops the producer. Blocks are not reused, so a consumer may keep them.</summary>
    IAsyncEnumerable<ReadOnlyMemory<float>> ReadSamplesAsync(CancellationToken ct = default);
}
