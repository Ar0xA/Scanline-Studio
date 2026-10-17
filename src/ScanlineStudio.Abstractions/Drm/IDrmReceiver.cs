namespace ScanlineStudio.Abstractions.Drm;

/// <summary>One DRM (HamDRM) receive chain at a fixed capture rate: QSSTV's drmRx front end, demodulator, channel
/// decoder and source decoder. A rate change means a new instance.</summary>
/// <remarks>Not thread-safe: one caller at a time. Every event is raised synchronously on the thread that called
/// <see cref="Push"/>, <see cref="ZeroFill"/> or <see cref="Reset"/>; a handler exception propagates out of that call.</remarks>
public interface IDrmReceiver
{
    /// <summary>The capture rate this instance was built for.</summary>
    int CaptureRate { get; }

    /// <summary>A file completed. Its <see cref="DrmReceivedFile.Data"/> is not reused by the receiver.</summary>
    event Action<DrmReceivedFile>? FileReceived;

    /// <summary>QSSTV's <c>rxDRMStatusEvent</c>: "" when a transport block opens, then its MOT ContentName.</summary>
    event Action<string>? ReceivingNameChanged;

    /// <summary>The MOT header of an already-saved object arrived again. The argument is QSSTV's raw saved name:
    /// display text only, never a path.</summary>
    event Action<string>? AlreadyReceived;

    /// <summary>A complete <c>bsr.bin</c> (a BSR request).</summary>
    event Action<ReadOnlyMemory<byte>>? BsrReceived;

    /// <summary>The signal state changed. Raised immediately on any change of time, frequency or frame sync, and
    /// otherwise at most once per 400 ms of consumed audio. <see cref="DrmSignalState.Generation"/> is 0.</summary>
    event Action<DrmSignalState>? SignalStateChanged;

    /// <summary>Feeds capture samples (float full scale ±1) and runs every completed 12 kHz stripe through the chain.</summary>
    void Push(ReadOnlySpan<float> samples);

    /// <summary>Feeds <paramref name="samples"/> zeros, to keep the symbol timing across dropped audio.</summary>
    void ZeroFill(int samples);

    /// <summary>Clears the front-end history and re-initialises the receiver (drmRx::init). Transport blocks
    /// survive, as in QSSTV, so a repeat can still complete a file.</summary>
    void Reset();
}
