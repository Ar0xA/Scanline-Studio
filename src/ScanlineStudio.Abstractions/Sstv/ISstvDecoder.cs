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
    /// seen state), not assume either ordering.
    ///
    /// <b>Also fires when Auto Stop abandons reception with no replacement transmission pending</b>
    /// (legacy's erratic/weak-signal detector, <c>sys.m_AutoStop</c>/<c>RxAutoPush</c>,
    /// `Main.cpp:3884-3966`/`:6042-6060`) -- unlike every other case, which is always immediately
    /// followed by a new mode being committed, this one simply re-arms auto-detection with nothing
    /// queued up. Consumers must not assume a subsequent <see cref="ModeDetected"/> is imminent.</summary>
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

    /// <summary>Current Auto Slant sample-clock drift, in parts-per-million relative to the declared
    /// (nominal) sample rate -- the same quantity and formula as legacy's own "Sync &amp; slant"
    /// readout (<c>TMmsstv::DrawSlantInfo</c>, `Main.cpp:5535-5544`:
    /// <c>(SSTVSET.m_SampFreq - sys.m_SampFreq) * 1e6 / sys.m_SampFreq</c>). <see langword="null"/>
    /// before any mode is locked, and for AVT (which has no Auto Slant tracking, matching
    /// <see cref="RequestReSync"/>'s own no-op condition) -- <c>0.0</c>, not <see langword="null"/>,
    /// once tracking is active but before the first correction ever commits (drift is genuinely zero
    /// until then, matching legacy's own <c>SSTVSET.m_SampFreq == sys.m_SampFreq</c> at that
    /// point).
    ///
    /// Lifetime differs from legacy's own readout, not just its null cases: legacy's
    /// <c>SSTVSET.m_SampFreq</c> persists across receptions until a new correction or an explicit
    /// Slant-Reset (`Main.cpp:13186-13193`), so its ppm readout carries forward between images. This
    /// port's tracker is rebuilt fresh on every lock and torn down at end-of-image, so this value
    /// resets to <see langword="null"/> (then <c>0.0</c> again once a fresh lock's tracker exists)
    /// between receptions rather than carrying over -- a real, accepted port-level scoping choice, not
    /// an oversight.
    ///
    /// Safe to read from any thread (e.g. a GUI polling this on a timer while another thread drives
    /// <see cref="PushSamples"/>) -- unlike <see cref="RequestReSync"/>/<see cref="ForceMode"/>, this
    /// is a plain field read with no cross-thread write to synchronize, so a concurrent read can
    /// observe a momentarily stale value (never a torn/corrupt one) but never throws.</summary>
    double? SlantPpm { get; }

    /// <summary>Most recent per-line sync-envelope offset, in samples, relative to where the locked
    /// mode's sync segment is expected to start -- the same quantity legacy computes as
    /// <c>m_AutoStopPos</c> (`Main.cpp:3887`) for its own Auto Sync/Auto Stop triggers, but never
    /// itself displays (legacy's only on-screen readout for this family is the ppm-only
    /// <see cref="SlantPpm"/> one above). <see langword="null"/> before any mode is locked, for AVT
    /// (no Auto Slant tracking), or before any line has completed since the current lock or the last
    /// applied correction (mirrors <see cref="RequestReSync"/>'s own no-op condition).
    ///
    /// Safe to read from any thread, same guarantee and same caveat as <see cref="SlantPpm"/> above
    /// (a concurrent read can observe a momentarily stale snapshot, never a thrown exception).</summary>
    int? SyncOffsetSamples { get; }
}
