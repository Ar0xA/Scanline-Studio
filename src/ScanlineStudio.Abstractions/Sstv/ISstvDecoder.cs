using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Abstractions.Sstv;

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
    /// not just that a mode was (re-)identified.
    ///
    /// Argument is the *abandoned* mode (what was being decoded before the restart), not the newly-
    /// detected one -- <see cref="ModeDetected"/> for the new mode always fires first, before this
    /// event, since the implementation must already know the new mode to have decided a restart is
    /// happening at all. A caller that allocates a display buffer on <see cref="ModeDetected"/> and
    /// discards on this event needs the abandoned mode here specifically, not the new one it just
    /// allocated for.</summary>
    event Action<SstvModeDefinition>? DecodeRestarted;

    /// <summary>Resets AGC/level-tracking state to its power-on defaults. Legacy calls its equivalent
    /// (<c>CLVL::Init</c>) at every TX&lt;-&gt;RX transition (`Sound.cpp:398,443`) -- callers should
    /// invoke this at the same transition points (ultracode audit finding #6).</summary>
    void ResetAgc();

    /// <summary>Requests a one-time manual sync correction — the port of legacy's real "ReSync" button
    /// (<c>TMmsstv::KRFSClick</c>, `Main.cpp:14004-14020`, not <c>ReSyncSSTV</c>). Safe to call from
    /// any thread; the request is deferred and applied on whichever thread next calls
    /// <see cref="PushSamples"/>. A no-op if not currently locked, if the current mode has no Auto
    /// Slant tracking (AVT), or if no line has completed since the last lock/successful correction.
    /// Deliberately fire-and-forget (no return value) — the actual application is asynchronous
    /// relative to this call.</summary>
    void RequestReSync();
}
