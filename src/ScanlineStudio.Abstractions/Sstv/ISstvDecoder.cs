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
    /// detected one -- a caller that allocates a display buffer on <see cref="ModeDetected"/> and
    /// discards on this event needs the abandoned mode here specifically, not the new one it just
    /// allocated for.
    ///
    /// <b>Ordering relative to <see cref="ModeDetected"/> is NOT fixed</b> -- corrected here after an
    /// earlier version of this doc wrongly claimed <see cref="ModeDetected"/> for the new mode always
    /// fires first (true only through the "piece 6" implementation; piece 8c's deferred anchor-
    /// correction pipeline changed this without the doc being updated, and a plan built on the old
    /// claim was caught by auditor review before it shipped a real data-corruption bug). In the
    /// dominant case (a non-AVT match, whether from a mid-reception restart or
    /// <see cref="ForceMode"/>), this event fires FIRST, since the new mode's own
    /// <see cref="ModeDetected"/> is deferred until its sync anchor resolves (possibly a later
    /// <see cref="PushSamples"/> call entirely). Only when the new mode resolves immediately (AVT,
    /// which has no anchor-correction step) does <see cref="ModeDetected"/> fire first. A caller that
    /// needs to know "has the new mode's <see cref="ModeDetected"/> already fired by the time this
    /// event arrives" must track that itself (e.g. by mode-identity comparison against its own last-
    /// seen state), not assume either ordering.</summary>
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

    /// <summary>Requests an immediate decode restart into <paramref name="mode"/>, bypassing VIS
    /// header detection — the port of legacy's real RX quick-mode-button click
    /// (<c>TMmsstv::SBMClick</c>, `Main.cpp:6096-6122`, calling <c>CSSTVDEM::Start(mode, TRUE)</c>,
    /// `sstv.cpp:1749-1767`). Confirmed a one-shot "start decoding as mode X right now" kick, not a
    /// persistent lock: legacy's <c>Start(void)</c> unconditionally lands on the same
    /// <c>m_SyncMode</c> value normal VIS auto-detect uses (`sstv.cpp:1744`), so once the forced
    /// image ends, ordinary auto-detect resumes for the next transmission with no extra step. Any
    /// in-progress decode (auto-detected or previously forced) is abandoned; any in-progress AVT
    /// training is aborted. Safe to call from any thread; the request is deferred (last-request-wins)
    /// and applied on whichever thread next calls <see cref="PushSamples"/> — no thread marshaling,
    /// no synchronization context, matching <see cref="RequestReSync"/>'s own contract. Deliberately
    /// fire-and-forget (no return value) — the actual application, and any resulting
    /// <see cref="ModeDetected"/>/<see cref="DecodeRestarted"/> events, are asynchronous relative to
    /// this call.</summary>
    void ForceMode(SstvModeDefinition mode);
}
