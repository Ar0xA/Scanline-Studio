using Yoniq.Abstractions.Imaging;

namespace Yoniq.Abstractions.Sstv;

public sealed record DecodedImageUpdate(int Line, IImageSource Image);

public interface ISstvDecoder
{
    void PushSamples(ReadOnlyMemory<float> samples);

    event Action<DecodedImageUpdate>? LineDecoded;

    event Action<SstvModeDefinition>? ModeDetected;

    /// <summary>Fires when a stronger/cleaner sync lock is found mid-reception, aborting an
    /// in-progress image to restart on the new transmission (legacy's case-0 trigger, `sstv.cpp:1946-1950`,
    /// is ungated -- it keeps running even while already locked). Distinct from <see cref="ModeDetected"/>,
    /// which fires for both a fresh detection and a mid-reception restart: callers displaying an
    /// in-progress image need this to know specifically that the partial image should be discarded,
    /// not just that a mode was (re-)identified. Argument is the newly-detected mode.</summary>
    event Action<SstvModeDefinition>? DecodeRestarted;
}
