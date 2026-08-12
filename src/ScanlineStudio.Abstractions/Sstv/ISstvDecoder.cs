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

    /// <summary>Peak amplitude over the trailing ~100ms window, normalized against this port's own
    /// <c>[-1,1]</c> float sample contract (`spec/05-audio-engine.md:44`) -- port of legacy's
    /// <c>CLVL.m_CurMax</c> (<c>sstv.h:223-298</c>), rescaled <c>/32768.0</c> back from legacy's
    /// int16-ish domain (confirmed against <c>Wave.cpp:796-808</c>'s direct <c>SHORT</c>-to-
    /// <c>double</c> copy). NOT the raw input/soundcard level -- this is the peak AFTER the receive
    /// bandpass filter, the same post-filter point legacy's own <c>m_CurMax</c> measures
    /// (<c>CSSTVDEM::Do</c>, `sstv.cpp:1824-1839`) -- a GUI labeling this "Input level" would be
    /// misleading; "Signal level" or similar is more accurate. Typical range <c>[0, ~1.0]</c>, not
    /// hard-bounded (that same bandpass filter can overshoot slightly). Not the same scale as
    /// legacy's own on-screen meter, which reads full at <c>24578</c> on ITS OWN scale
    /// (`Main.cpp:6168-6169`) -- this port deliberately uses its own existing float full-scale
    /// instead, so <see cref="IsLevelOverdriven"/>'s threshold below sits at ~0.75 of this value,
    /// not at 1.0.
    ///
    /// Never <see langword="null"/>: the underlying tracker is decoder-lifetime (constructed once,
    /// not per-lock like <see cref="SlantPpm"/>'s tracker), and reads <c>0.0</c> -- a real,
    /// meaningful value, not a "not ready" placeholder -- before the first sample is ever pushed,
    /// immediately after <see cref="ResetAgc"/> until the next ~100ms window completes, and
    /// (like every property here) between images.
    ///
    /// <b>Freshness caveat, accepted not fixed</b>: only as fresh as the decoder's internal AGC
    /// cursor, which normal per-line decode and pre-lock header scanning both advance -- except in
    /// one specific combination: a locked AVT reception with sync-restart disabled (the "Lock"
    /// toggle engaged), where nothing advances that cursor for the rest of that image (AVT has no
    /// Auto Slant/AFC tracking to drive it, and the one other driver is gated off by that same
    /// toggle) -- this value can sit frozen at a stale reading for the whole image in that specific
    /// case. A documented limitation of a read-only exposure, not a bug to work around here.
    ///
    /// Safe to read from any thread, same guarantee as <see cref="SlantPpm"/> above.</summary>
    double SignalPeakLevel { get; }

    /// <summary>Whether <see cref="SignalPeakLevel"/>'s underlying peak amplitude has reached
    /// legacy's own red-meter-bar threshold -- <c>CLVL.m_CurMax &gt;= 24578</c>, the exact
    /// condition legacy's RX level meter turns red at (<c>DrawLvl</c>'s RX branch,
    /// <c>Main.cpp:6174</c>). Deliberately NOT legacy's separate <c>m_OverFlow</c> flag
    /// (<c>sstv.cpp:1821-1822</c>), which tests the raw, pre-filter sample and is a latched,
    /// GUI-repaint-cleared flag -- a genuinely different legacy quantity this port has no
    /// equivalent access point for (this port's AGC is fed the already-bandpass-filtered signal).
    /// Not a percentage: exposes what legacy actually measured (a threshold crossing), not an
    /// invented "clipping %" figure legacy never computed.
    ///
    /// Reads the underlying peak amplitude independently of <see cref="SignalPeakLevel"/> (a
    /// second, separate field read, not derived from that property's own already-divided value) --
    /// a caller reading both back-to-back can in principle observe them disagree by one sample
    /// generation (e.g. <see cref="SignalPeakLevel"/> just under 0.75 while this is still
    /// <see langword="true"/> from a fractionally earlier reading). Inherent to two independent
    /// reads of a live value, not a bug.
    ///
    /// Never <see langword="null"/>, same lifetime/freshness notes as <see cref="SignalPeakLevel"/>
    /// (including its AVT/locked/sync-restart-disabled staleness caveat). Safe to read from any
    /// thread, same guarantee as <see cref="SlantPpm"/> above.</summary>
    bool IsLevelOverdriven { get; }

    /// <summary>Whether legacy's <c>KRSA-&gt;Checked</c> ("Auto Slant") setting is enabled --
    /// restart-only, same as this decoder's other settings-driven toggles: constructed once per DI
    /// singleton lifetime, no live-reconfigure path. When <see langword="false"/>, slant-correction
    /// commits never happen (the underlying drift-detection bookkeeping still runs), so
    /// <see cref="SlantPpm"/> never moves away from its <c>0.0</c> default for the whole reception --
    /// NOT <see langword="null"/>: <see cref="SlantPpm"/> is non-null (reading exactly <c>0.0</c>) from
    /// the moment a non-AVT mode locks, whether this flag is <see langword="true"/> or
    /// <see langword="false"/>, since the underlying tracker's drift value starts at zero and is only
    /// ever reassigned by an actual commit. This property is the only way a caller can distinguish a
    /// genuine "off" reading of <c>0.0</c> from a genuine "on, zero drift measured so far" reading of
    /// the same value. Safe to read from any thread.</summary>
    bool AutoSlantEnabled { get; }

    /// <summary>Current sync-tone AFC frequency correction, in Hz -- direct passthrough of the
    /// underlying AFC tracker's own correction value (no sign flip), which callers add to every
    /// demodulated sample (port of legacy's <c>CSSTVDEM::SyncFreq</c>/<c>d += m_AFCDiff</c>,
    /// `sstv.cpp:2270`). Backs a "Sync tone" readout the way legacy would show it via its own AFC
    /// state -- legacy has no equivalent readout for the Black(1500Hz)/White(2300Hz) picture
    /// tones, since AFC only ever tracks the sync tone in both legacy and this port; a GUI must
    /// not invent values for those.
    ///
    /// <see langword="null"/> in every case the underlying tracker doesn't exist: before any mode
    /// is locked or between images; for AVT (no AFC tracking, matching
    /// <see cref="SlantPpm"/>'s own AVT exclusion); when AFC is disabled by configuration (a
    /// port-only null case with no legacy analogue); and -- the one easy to miss, since the
    /// underlying tracker is NOT torn down by the same mid-reception AVT-training-hand-off path
    /// that leaves the current mode momentarily null (same class of gap <see cref="SlantPpm"/> had
    /// to be fixed for) -- during that same pending-training window. <c>0.0</c>, not
    /// <see langword="null"/>, once tracking is active but before AFC's first lock (matching
    /// legacy's own zero-correction-until-locked default) -- verified this can never leak a stale
    /// non-zero value from a previous lock, since the tracker is always freshly constructed, never
    /// reset in place.
    ///
    /// Safe to read from any thread, same guarantee as <see cref="SlantPpm"/> above.</summary>
    double? SyncFrequencyCorrectionHz { get; }

    /// <summary>Number of raw samples currently held in the decoder's internal buffer, after
    /// trimming. NOT "0 while idle" -- pre-lock header/VIS scanning retains a bounded, multi-
    /// thousand-sample search window even while streaming with nothing locked; this is genuinely
    /// <c>0</c> only before the very first <see cref="PushSamples"/> call, and grows to roughly one
    /// image's worth of samples while a lock is active. Counts the raw sample buffer only, not the
    /// separate small per-detector caches the pre-lock header scanners maintain -- not a total
    /// memory figure, a diagnostic of the one buffer most likely to grow unbounded if something is
    /// wrong.
    ///
    /// Safe to read from any thread, same guarantee as <see cref="SlantPpm"/> above.</summary>
    int BufferedSampleCount { get; }

    /// <summary>Legacy <c>m_fskdecode</c> equivalent (<c>sstv.h:708</c>, <c>.ini</c> key
    /// <c>RXFSKID</c>) -- whether the FSK station-ID (STX <c>0x2a</c>) continuation is decoded at
    /// all; the shared mode-announce front half (STX <c>0x2d</c>) always runs regardless. Defaults
    /// <see langword="false"/>, matching legacy's own default. Deliberately LIVE-settable (unlike
    /// <see cref="AutoSlantEnabled"/>'s restart-only shape above) -- legacy's own
    /// <c>m_fskdecode</c> is checked fresh on every dispatched byte (<c>sstv.cpp</c>'s station-ID
    /// continuation cases), so toggling it live is the MORE faithful behavior here, not less. A
    /// <c>RestartableSstvDecoder</c> implementation must apply a set value to its current inner
    /// instance immediately AND preserve it across its own periodic reconstruction (see that class'
    /// own <c>ISstvDecoderMaintenance</c> doc comment) -- a value silently dropped on the next
    /// scheduled restart would be a real, hard-to-notice regression.
    ///
    /// <b>"Applied immediately" is best-effort visibility, not a memory-model guarantee</b> (round-2
    /// audit finding): the underlying storage is a plain, non-volatile field with no acquire/release
    /// pairing on the decode-thread read side, matching legacy's own equally unsynchronized
    /// <c>m_fskdecode</c> global -- harmless for the one production caller today (set once, before
    /// capture starts, never concurrently with an in-flight <see cref="PushSamples"/>), but a future
    /// caller that toggles this mid-reception from a different thread should not assume the change is
    /// visible to the decode thread within any particular bound.</summary>
    bool StationIdDecodeEnabled { get; set; }
}
