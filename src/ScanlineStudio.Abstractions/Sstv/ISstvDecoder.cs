using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Abstractions.Sstv;

public sealed record DecodedImageUpdate(int Line, IImageSource Image);

public interface ISstvDecoder
{
    /// <summary>Whole-Hz sample rate expected by <see cref="PushSamples"/>. The capture pipeline,
    /// waterfall, and encoder must all use this same value AT ANY GIVEN MOMENT -- restart-required-
    /// settings backlog item 4 (2026-08-27) made this genuinely live-settable on the production
    /// implementation, `ScanlineStudio.Core.Sstv.RestartableSstvDecoder` (see
    /// `ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.RequestSampleRate`'s own doc comment for
    /// the request/commit contract) -- a caller must not assume this value is fixed for the process
    /// lifetime, and must not cache a read across an operation that spans any await/yield point where
    /// a concurrent change could land.</summary>
    int SampleRate { get; }

    /// <summary>Feeds one block of demodulated audio samples into the decoder. Intended to be called
    /// from a single, consistent producer thread (e.g. an audio-capture callback) -- see the events
    /// below for this class's general concurrency contract. The two PRODUCTION implementations
    /// (<c>AnalogFmSstvDecoder</c>, <c>RestartableSstvDecoder</c>) throw
    /// <see cref="ObjectDisposedException"/> after the implementation is disposed (D2 round 1 fix:
    /// previously undocumented and inconsistently enforced across implementations; D2 round 2
    /// correction: this interface does not itself extend <see cref="IDisposable"/>, so an earlier
    /// version of this sentence's cref to <c>IDisposable.Dispose</c> pointed at a member not reachable
    /// through this type -- both production implementations implement <see cref="IDisposable"/>
    /// separately, and that is what this contract refers to; D2 round 3 correction: an earlier version
    /// of this sentence said "every implementation this codebase ships" -- false for 3 of the 5 in-tree
    /// implementations, which are test doubles under `tests/`: two don't implement
    /// <see cref="IDisposable"/> at all, and the third does but deliberately doesn't enforce the throw.
    /// This contract is scoped to the two production implementations only; test doubles make no such
    /// promise and callers must not rely on it when testing against one.
    ///
    /// Those same two production implementations reject recursive or concurrent overlapping calls on
    /// one decoder instance with <see cref="InvalidOperationException"/>. Decode-event handlers are
    /// still synchronous and blocking, but every registered handler is attempted and the first handler
    /// exception is deferred until this call reaches a stable decoder boundary. A decoder-internal
    /// failure takes precedence and propagates unchanged if both occur. The production session service
    /// rate-limits/logs and swallows the completed-call failure so later capture chunks can continue.</summary>
    void PushSamples(ReadOnlyMemory<float> samples);

    /// <summary>Fires once per decoded transmission line (functional-audit fix, D3+D8+D9 coupled
    /// round 1: this concurrency contract was previously undocumented, unlike its sibling
    /// <see cref="StationIdDecoded"/> below). Concurrency contract (CLAUDE.md §4): invoked
    /// SYNCHRONOUSLY on whatever thread runs decode, with no buffering and no marshaling -- a slow
    /// or blocking subscriber blocks decode, same as <see cref="StationIdDecoded"/>.
    /// <see cref="DecodedImageUpdate.Image"/> is a LIVE ALIAS of the decoder's own mutable pixel
    /// buffer, not a copy -- a subscriber that queues it for later/async rendering instead of
    /// consuming it synchronously will read torn or stale data (see
    /// <c>AnalogFmSstvDecoder.LineDecoded</c>'s own doc comment for the full concurrency contract
    /// this interface member is a summary of). A REPLAY pass additionally fires this once per
    /// replayed row in a single synchronous burst (up to a whole image's worth back to back), all
    /// wrapping that SAME live array -- a subscriber that defers work sees only the array's final
    /// state, and a slow one stalls the entire burst, not just one line. Any UI-facing consumer must
    /// dispatch to its own thread immediately rather than doing real work inline here, same
    /// established pattern as <see cref="StationIdDecoded"/>. Subscriber failure isolation and
    /// exception precedence follow <see cref="PushSamples"/>'s contract.</summary>
    event Action<DecodedImageUpdate>? LineDecoded;

    /// <summary>Fires once a mode is identified and its sync anchor has resolved -- either a fresh
    /// detection or a mid-reception restart (see <see cref="DecodeRestarted"/> below for how the two
    /// relate and for their relative ordering, which is NOT fixed). Concurrency contract (CLAUDE.md §4,
    /// D2 round 1 fix: this event previously had no doc comment at all, unlike its siblings
    /// <see cref="LineDecoded"/>/<see cref="StationIdDecoded"/>): invoked SYNCHRONOUSLY on whatever
    /// thread runs decode, with no buffering and no marshaling -- a slow or blocking subscriber blocks
    /// decode. Any UI-facing consumer must dispatch to its own thread immediately rather than doing
    /// real work inline here, same established pattern as the other decode-thread events on this
    /// interface. Subscriber failure isolation follows <see cref="PushSamples"/>.</summary>
    event Action<SstvModeDefinition>? ModeDetected;

    /// <summary>Delivery channel for a decoded FSK station-ID callsign/NR-RST (legacy STX
    /// <c>0x2a</c>, <c>sstv.cpp:2465-2551</c>) -- fired from whatever runs the decode (the DSP/decode
    /// pipeline thread, not the UI thread). Concurrency contract (CLAUDE.md §4): subscribers are
    /// invoked SYNCHRONOUSLY on that thread, with no buffering and no marshaling -- a slow or
    /// blocking subscriber blocks decode. Any UI-facing consumer must dispatch to its own thread
    /// itself, immediately, rather than doing real work inline here (see
    /// <c>ScanlineStudio.UI.ViewModels.RxImagePaneViewModel.OnStationIdDecoded</c> for the
    /// established pattern). Gated by <see cref="StationIdDecodeEnabled"/> below. Subscriber failure
    /// isolation follows <see cref="PushSamples"/>.</summary>
    event Action<FskStationIdDecodedInfo>? StationIdDecoded;

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
    /// queued up. Consumers must not assume a subsequent <see cref="ModeDetected"/> is imminent.
    ///
    /// Concurrency contract (CLAUDE.md §4, D2 round 1 fix: this event's extensive ordering discussion
    /// above never actually stated its threading/blocking behavior): same as <see cref="ModeDetected"/>
    /// -- invoked SYNCHRONOUSLY on whatever thread runs decode, with no buffering and no marshaling.
    /// Any UI-facing consumer must dispatch to its own thread immediately. Subscriber failure
    /// isolation follows <see cref="PushSamples"/>.</summary>
    event Action<SstvModeDefinition>? DecodeRestarted;

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio): a per-reception identity,
    /// incremented exactly once per reception -- ON <see cref="ModeDetected"/>, before that event
    /// fans out to any subscriber -- so every independent subscriber (a caller's own arm/close state
    /// machine, an audio-slice correlator, a history recorder, a UI pane) reads the IDENTICAL value
    /// for the same reception regardless of subscription order, with no coordination needed between
    /// them. See <c>docs/plans/step12-auto-save-rx-audio-plan.md</c>'s "Identity" section for the
    /// full design this exists for.
    ///
    /// <b>Contract, binding on every implementation:</b> monotonic for this instance's lifetime,
    /// never reused -- the first real reception's value is <c>1</c> (an <see
    /// cref="System.Threading.Interlocked.Increment(ref long)"/> from a 0-initialized field, NEVER a
    /// post-increment idiom that would make the first value <c>0</c>) -- <c>0</c> is reserved to mean
    /// "no reception yet"/"unset," and a consumer correlating by this value must treat <c>0</c> as
    /// never matching a real reception. <see cref="DecodeRestarted"/> NEVER increments this -- see
    /// below for why.
    ///
    /// <b><see cref="DecodeRestarted"/> does not always mean "new reception," so this must not be
    /// bumped there</b>: the dominant ordering (<see cref="DecodeRestarted"/> fires first, for the
    /// OLD image, with the new mode's own <see cref="ModeDetected"/> arriving later, possibly in a
    /// much later <see cref="PushSamples"/> call) and the minority ordering (AVT,
    /// <see cref="ForceMode"/>-into-AVT: <see cref="ModeDetected"/> for the NEW reception fires
    /// first, then <see cref="DecodeRestarted"/> closes out the OLD image, both within the SAME
    /// <see cref="PushSamples"/> call) both mean a value bumped on <see cref="DecodeRestarted"/>
    /// could not reliably distinguish "the restart my own arm already accounted for" from "a
    /// genuinely new restart of the reception I just armed" -- this is true even for an ordinary
    /// same-mode-to-same-mode restart (e.g. Scottie 1 -&gt; Scottie 1), since every
    /// <see cref="SstvModeDefinition"/> is a shared <c>public static readonly</c> singleton, not a
    /// fresh instance per reception.
    ///
    /// <b>Precondition a consumer keying same-epoch <see cref="DecodeRestarted"/> suppression against
    /// a separate push-sequence counter depends on, stated here since it constrains every caller of
    /// <see cref="PushSamples"/>, not just this property:</b> a single <see cref="PushSamples"/> call
    /// must never itself contain two independent restart-triggering events -- true today only
    /// because live-capture pushes are chunked at a bounded size (the production audio-capture
    /// session's own drain-buffer size) and file-decode pushes are similarly bounded. Any test
    /// harness driving <see cref="PushSamples"/> directly (including a fake audio engine that accepts
    /// arbitrary-length buffers) MUST chunk its pushes to the same bound, or a single oversized test
    /// buffer containing two real restarts can collapse them into one apparent push and produce a
    /// false suppression a real caller would never see.
    ///
    /// Safe to read from any thread, same guarantee as <see cref="SlantPpm"/>'s own concurrency
    /// note.</summary>
    long ReceptionSequence { get; }

    /// <summary>fsk_cwid.md §8.2: how far this reception's true image-start audio sample trails
    /// the audio actually consumed so far, at the moment <see cref="ModeDetected"/> fires --
    /// captured as <c>TotalSamplesReceived - _consumedSamples</c> immediately before that raise
    /// (the same corrected line-0 anchor the decoder's own AFC bound is computed from), so a
    /// caller building a post-image capture window from ITS OWN pushed-sample coordinate space
    /// can compute <c>imageStart = pushedSampleCount - AnchorLagSamples</c> without needing a
    /// backfill buffer.
    ///
    /// <b>Read-in-callback contract, same as <see cref="ReceptionSequence"/>:</b> only meaningful
    /// when read from inside the <see cref="ModeDetected"/> callback for the reception it
    /// describes -- a poll at any other time returns whatever the last completed reception (or
    /// <c>0</c>, before any lock) happened to leave behind, not a live value. <c>0</c> before any
    /// reception has ever locked.
    ///
    /// A larger lag makes the computed <c>imageStart</c> EARLIER, not later -- the captured
    /// window then opens sooner, landing partway into this image's own slant-bounded audio tail
    /// rather than after it. This is bounded and safe by construction, not a caller obligation:
    /// real SSTV sync pulses are single-length, not the 1:3 dit/dah ratio a CW timing classifier
    /// requires, so they fail that structural check even if a tone-find step locks onto one.
    ///
    /// Safe to read from any thread, same guarantee as <see cref="SlantPpm"/>'s own concurrency
    /// note.</summary>
    int AnchorLagSamples { get; }

    /// <summary>Resets AGC/level-tracking state to its power-on defaults. Legacy calls its equivalent
    /// (<c>CLVL::Init</c>) at every TX&lt;-&gt;RX transition (`Sound.cpp:398,443`) -- callers should
    /// invoke this at the same transition points (ultracode audit finding #6).
    ///
    /// Concurrency contract (D0-audit round-6 finding: unlike <see cref="RequestReSync"/>/
    /// <see cref="RequestCorrectSlant"/>/<see cref="ForceMode"/>, this is NOT a deferred single-field
    /// write consumed later on the decode thread -- it mutates the underlying level-tracking state
    /// directly and synchronously, on whichever thread calls it, right now. Calling it while a
    /// concurrent <see cref="PushSamples"/> call is in flight on another thread is a genuine data
    /// race on that state (bounded: at worst a torn read briefly under-clamps AGC'd samples for one
    /// ~100ms `Fix()` window, never wrong pixel data or a crash) -- callers must call this only at a
    /// TX/RX transition boundary where no concurrent <see cref="PushSamples"/> call can be in
    /// flight, not from an arbitrary thread at an arbitrary time the way the deferred siblings
    /// allow.</summary>
    void ResetAgc();

    /// <summary>Requests a one-time manual sync correction — the port of legacy's real "ReSync" button
    /// (<c>TMmsstv::KRFSClick</c>, `Main.cpp:14004-14020`, not <c>ReSyncSSTV</c>). Safe to call from
    /// any thread; the request is deferred and applied on whichever thread next calls
    /// <see cref="PushSamples"/>. A no-op if not currently locked, if the current mode has no Auto
    /// Slant tracking (AVT), or if no line has completed since the last lock/successful correction.
    /// Deliberately fire-and-forget (no return value) — the actual application is asynchronous
    /// relative to this call.</summary>
    void RequestReSync();

    /// <summary>Turns the RX notch filter on/off and/or retunes it — the port of legacy's real
    /// spectrum-click notch control (<c>TMmsstv::PBoxFFTMouseDown</c>/<c>PBoxFFTMouseMove</c>,
    /// `Main.cpp:14360-14390`). Unlike <see cref="RequestReSync"/>, this is persistent STATE, not a
    /// one-shot command — it stays on/off (and at whatever frequency) until called again, surviving
    /// across pushed sample batches and, on the production wrapper, across a periodic decoder
    /// restart. <paramref name="frequencyHz"/> is optional so a caller can toggle on/off without
    /// respecifying the current frequency, or retune without touching the enabled state (pass the
    /// current <paramref name="enabled"/> value back) — passing both <c>true</c> and a frequency,
    /// like legacy's own left-click, both tunes and turns the notch on in one call. Safe to call
    /// from any thread; the request is deferred (last-request-wins) and applied, together with the
    /// same group-delay compensation legacy applies for a toggle while locked, on whichever thread
    /// next calls <see cref="PushSamples"/>.</summary>
    void RequestNotch(bool enabled, double? frequencyHz);

    /// <summary>Options Advanced-tab PLL demodulator tuning (backlog item,
    /// `docs/plans/options-stub-item1-pll-tuning-plan.md`) — direct port of legacy's own live-edit
    /// shape (<c>Option.cpp</c>'s Save handler calling <c>CPLL::SetVcoGain</c>/<c>MakeLoopLPF</c>/
    /// <c>MakeOutLPF</c> on the live decoder instance). Same deferred, last-request-wins,
    /// call-from-any-thread shape as <see cref="RequestNotch"/> — applied on whichever thread next
    /// calls <see cref="PushSamples"/>. Affects BOTH the picture-path PLL demodulator (when
    /// <c>DemodType.Pll</c> is selected) and the always-PLL AVT-training demodulator, matching
    /// legacy's own single <c>m_pll</c> instance serving both roles.</summary>
    void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz);

    /// <summary>Options Advanced-tab zero-crossing demodulator tuning (backlog item,
    /// `docs/plans/options-stub-item2-zerocrossing-tuning-plan.md`) — direct port of legacy's own
    /// live-edit shape (<c>Option.cpp</c>'s Save handler calling <c>CFQC::CalcLPF</c> on the live
    /// decoder instance). Same deferred, last-request-wins, call-from-any-thread shape as
    /// <see cref="RequestNotch"/>/<see cref="RequestPllTuning"/> — applied on whichever thread next
    /// calls <see cref="PushSamples"/>. Affects BOTH the picture-path zero-crossing demodulator (when
    /// <c>DemodType.ZeroCrossing</c> is selected) and the AFC sync-frequency-measurement counter,
    /// matching legacy's own single <c>m_fqc</c> instance serving both roles.</summary>
    void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz);

    /// <summary>Arms a one-shot Decoder Trace capture — the port of legacy's real oscilloscope
    /// trigger (<c>TTScope</c>/<c>CScope</c>, <c>TrigNext</c>/<c>SBTrigClick</c>). Two independent
    /// channels: channel 0 (the VIS/sync-envelope detector, d12 or d19 depending on the currently
    /// locked mode's narrow/wide-ness — latched once at arm time, not re-evaluated per sample)
    /// writes on every sample regardless of lock state, matching legacy's own unconditional write;
    /// channel 1 (the demodulated picture-frequency stream, post-AFC-correction when AFC applies)
    /// only writes while genuinely locked — a capture armed with no active reception fills channel
    /// 0 to <paramref name="size"/> samples but leaves channel 1 permanently empty (a real,
    /// legacy-accurate gap, not a bug: legacy's own channel-1 write sits inside its own
    /// <c>if(m_Sync)</c> block too). AVT is the one exception where this port's own gap is WIDER
    /// than legacy's: AVT training is genuinely locked, but this port's channel-1 write is gated on
    /// a tracker that stays null for AVT by construction, so channel 1 never fills during AVT even
    /// though legacy's own channel-1 write is not AVT-gated. Re-arming discards any capture already in progress or
    /// complete-but-unread, matching legacy's own re-trigger semantics. Safe to call from any
    /// thread; applied on whichever thread next calls <see cref="PushSamples"/>. See
    /// <see cref="TryGetScopeCaptureChannel0"/>/<see cref="TryGetScopeCaptureChannel1"/> for
    /// reading a completed capture back.</summary>
    void ArmScopeCapture(int size);

    /// <summary>Returns the completed channel-0 capture from the most recent
    /// <see cref="ArmScopeCapture"/> call, or <see langword="null"/> if that capture hasn't filled
    /// yet. Safe to call from any thread, at any time — this is a plain cross-thread read of an
    /// already-published, immutable snapshot, not a blocking wait.</summary>
    double[]? TryGetScopeCaptureChannel0();

    /// <summary>Returns the completed channel-1 capture from the most recent
    /// <see cref="ArmScopeCapture"/> call, or <see langword="null"/> if that capture hasn't filled
    /// yet (including "never will," if no reception is/was active since the arm — see
    /// <see cref="ArmScopeCapture"/>'s own doc comment). Safe to call from any thread, at any time.</summary>
    double[]? TryGetScopeCaptureChannel1();

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

    /// <summary>ui_transition_plan.md step 10 (T2-5): pins every FUTURE detected reception to
    /// <paramref name="mode"/> — <see langword="null"/> unlocks, returning to ordinary VIS
    /// auto-detection. Genuinely NEW UI/workflow (no legacy precedent for a persistent RX mode
    /// lock — legacy's own related state, <c>CSSTVDEM::m_SyncMode</c>/<c>RxAutoPush</c>/<c>SBMClick</c>,
    /// maps to <see cref="ForceMode"/> above and the separate auto-detect pause feature, not to this).
    /// Independent of <see cref="ForceMode"/>: VIS header detection keeps running exactly as without
    /// a lock (so <see cref="IsIdle"/>/decoder-swap machinery/history-recording all behave correctly
    /// during silence — a locked channel with nothing arriving stays genuinely idle, it does not
    /// synthesize receptions), and a manual <see cref="ForceMode"/> call always wins its OWN
    /// reception as a one-shot exception, leaving the lock itself unchanged for the reception after
    /// it. Only the MODE actually committed once a reception is detected gets substituted; a
    /// detected AVT signal is never substituted (v1 limitation — locking to or from AVT is not
    /// supported), and the substitution happens after the detected mode's own header/anchor math has
    /// already run, so it never corrupts sync-anchor computation for the signal actually received.
    /// Safe to call from any thread; deferred (last-request-wins) and applied on whichever thread
    /// next calls <see cref="PushSamples"/>, same contract as <see cref="ForceMode"/>.</summary>
    void SetModeLock(SstvModeDefinition? mode);

    /// <summary>Requests that any in-progress decode (auto-detected or previously forced) be
    /// abandoned, and any in-progress AVT training be aborted — the port of legacy's RX "pause
    /// auto-detect" toggle's own cleanup step (<c>TMmsstv::RxAutoPush</c>'s <c>pDem->Stop()</c>,
    /// `Main.cpp:6042-6060`), NOT a persistent pause state — this decoder has no concept of
    /// "paused"; a consumer wanting that (e.g. a UI pause/resume toggle) implements it by simply
    /// not calling <see cref="PushSamples"/> while paused, and calls this once first so whatever
    /// was in progress gets cleanly torn down (and, if a mode was locked, reported via
    /// <see cref="DecodeRestarted"/>) rather than left dangling. Idle-safe: if nothing was in
    /// progress, this is a harmless no-op. Safe to call from any thread; the request is deferred
    /// (one-shot, not last-request-wins — there's no payload to overwrite) and applied on
    /// whichever thread next calls <see cref="PushSamples"/>, strictly before that same call's own
    /// <see cref="ForceMode"/> drain if one is also pending — so a mode forced while paused always
    /// lands cleanly, never torn down by a stale pending abandon request.</summary>
    void RequestAbandonReception();

    /// <summary>Requests a one-time "Correct Slant" search — the port of legacy's real manual
    /// "Correct Slant" toolbar action (<c>CorrectSlant</c>/<c>KRCS</c>, `Main.cpp:5264-5426`): a
    /// 5-iteration search over the currently staged reception that finds a corrected sample rate and
    /// redraws the image, distinct from Auto Slant's continuous per-line tracking (which runs
    /// automatically and needs no request). Safe to call from any thread; the request is deferred and
    /// applied on whichever thread next calls <see cref="PushSamples"/>.
    ///
    /// <b>Scoped to an active reception only</b> — unlike <see cref="RequestReSync"/>, which legacy
    /// itself supports post-reception, this port's search reads the live staging buffer, which is
    /// only reachable from inside an in-progress decode's own per-line loop. A request made between
    /// receptions (or that outlives the current one) is a documented no-op, not queued for the next
    /// lock — a real, accepted scope gap relative to legacy, not an oversight. A no-op if RX buffer
    /// capture is disabled, if the current mode has no staging buffer (AVT), if fewer than 16 lines
    /// have been staged, if the search doesn't find a genuinely different rate, if a request lands on
    /// a decoded line where Auto Stop just abandoned this reception, or (unlike legacy, whose search
    /// has no such gate) if a manual ReSync or Auto-Sync correction has already run earlier in this
    /// same image — this port's replay is destructive, and a ReSync/Auto-Sync correction leaves a
    /// mid-buffer hole a search or replay across it would corrupt, so Correct Slant is deliberately
    /// dead for the rest of that image once one occurs (auditor code-review finding, Phase 8c: worth
    /// knowing before a UI surfaces this as a button that can otherwise look like it silently failed).
    /// Also a no-op if the request lands between decoded lines faster than they're produced — a
    /// request can be silently superseded/dropped if it arrives in the narrow window between this
    /// port's own read-and-clear of the pending flag, the same fire-and-forget, best-effort contract
    /// <see cref="RequestReSync"/> already has. Deliberately fire-and-forget (no return value) — the
    /// actual application is asynchronous relative to this call.</summary>
    void RequestCorrectSlant();

    /// <summary>Current Auto Slant sample-clock drift, in parts-per-million relative to the declared
    /// (nominal) sample rate -- the same quantity and formula as legacy's own "Sync &amp; slant"
    /// readout (<c>TMmsstv::DrawSlantInfo</c>, `Main.cpp:5535-5544`:
    /// <c>(SSTVSET.m_SampFreq - sys.m_SampFreq) * 1e6 / sys.m_SampFreq</c>). <see langword="null"/>
    /// before any mode is locked, and for AVT (which has no Auto Slant tracking, matching
    /// <see cref="RequestReSync"/>'s own no-op condition); also <see langword="null"/> during both
    /// the mid-reception AVT-training-hand-off window and a non-AVT restart's own
    /// pending-anchor-correction window (a later audit round's finding -- see
    /// <see cref="SyncFrequencyCorrectionHz"/>'s own doc comment for the full reasoning, which
    /// applies identically here). <c>0.0</c>, not <see langword="null"/>, once tracking is active
    /// but before the first correction ever commits (drift is genuinely zero until then, matching
    /// legacy's own <c>SSTVSET.m_SampFreq == sys.m_SampFreq</c> at that point).
    ///
    /// Lifetime differs from legacy's own readout, not just its null cases: legacy's
    /// <c>SSTVSET.m_SampFreq</c> persists across receptions until a new correction or an explicit
    /// Slant-Reset (`Main.cpp:13186-13193`), so its ppm readout carries forward between images. This
    /// port's tracker is rebuilt fresh on every lock and torn down at end-of-image, so this value
    /// resets to <see langword="null"/> (then <c>0.0</c> again once a fresh lock's tracker exists)
    /// between receptions rather than carrying over -- a real, accepted port-level scoping choice, not
    /// an oversight.
    ///
    /// Safe to poll from any thread (e.g. a GUI timer while another thread drives
    /// <see cref="PushSamples"/>) in the limited sense that the getter does not throw. This is
    /// best-effort diagnostic telemetry: a concurrent poll may be stale, torn, or combine fields from
    /// different decode epochs, and must never drive decode correctness. <c>AnalogFmSstvDecoder</c>
    /// uses unsynchronized field reads. <c>RestartableSstvDecoder</c> -- the actual DI-registered
    /// implementation -- locks only to stabilize WHICH inner decoder it selects; <c>PushSamples</c>
    /// mutates that inner after releasing the wrapper lock, so it does not create a coherent snapshot.
    /// A polling read can additionally BLOCK if a concurrent <see cref="PushSamples"/> call is
    /// mid-swap: see that class's own <c>Swap</c> doc comment for the already-accepted worst-case
    /// blocking duration.</summary>
    double? SlantPpm { get; }

    /// <summary>Which lock mechanism currently owns this decoder's state -- see
    /// <see cref="SstvSyncSource"/>'s own doc comment for why this is a 3-value classification, not
    /// a 4-way Search/VisLock/Forced/AvtTraining split (plan-review, 2026-08-25). Live, polled
    /// telemetry, not restart-only -- it changes within a session as a reception starts/locks/ends.
    /// Same concurrency contract as <see cref="SlantPpm"/> above: safe to poll from any thread in the
    /// sense that the getter does not throw, but is best-effort diagnostic telemetry that must never
    /// drive decode correctness.</summary>
    SstvSyncSource SyncSource { get; }

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

    /// <summary>T0-6: whether RX Extended-mode disk buffering has hit a write failure (full disk,
    /// read-only TMPDIR, ENOSPC, etc.) and permanently stopped staging capture data to disk for the
    /// remainder of this decoder instance's lifetime (never resets -- the underlying latch is only
    /// cleared by constructing a fresh instance, e.g. via <c>RestartableSstvDecoder</c>'s own
    /// periodic swap). <b>Extended mode only -- always <see langword="false"/> for
    /// <c>RxBufferMode.On</c>/<c>Off</c></b>, since RAM mode's own capacity latch is a distinct,
    /// separate signal (never surfaced here) and Off mode has no staging buffer at all. Not a
    /// general RX-buffer-health signal -- do not read a <see langword="false"/> value here as "RX
    /// buffering is fine" for modes other than Extended.
    ///
    /// Once <see langword="true"/>, replay and Correct Slant have already silently become permanent
    /// no-ops for the rest of this decoder instance's lifetime (a pre-existing, separate behavior --
    /// this property exists so a caller can find out why, not to change that behavior). The
    /// underlying failure is also logged exactly once, at the moment of transition.</summary>
    bool RxBufferDegraded { get; }

    /// <summary>Whether legacy's <c>KRSA-&gt;Checked</c> ("Auto Slant") setting is enabled --
    /// genuinely live-settable (see the third paragraph below for the full contract), not
    /// restart-only. When <see langword="false"/>, slant-correction
    /// commits never happen (the underlying drift-detection bookkeeping still runs), so
    /// <see cref="SlantPpm"/> never moves away from its <c>0.0</c> default for the whole reception --
    /// NOT <see langword="null"/>: <see cref="SlantPpm"/> is non-null (reading exactly <c>0.0</c>) from
    /// the moment a non-AVT mode locks, whether this flag is <see langword="true"/> or
    /// <see langword="false"/>, since the underlying tracker's drift value starts at zero and is only
    /// ever reassigned by an actual commit. This property is the only way a caller can distinguish a
    /// genuine "off" reading of <c>0.0</c> from a genuine "on, zero drift measured so far" reading of
    /// the same value.
    ///
    /// Genuinely live-settable (2026-08-27, restart-required-settings backlog item 1) -- same
    /// deferred-latch shape as <see cref="SenseLevel"/> below (safe to set from any thread; the
    /// request is applied on whichever thread next calls <see cref="PushSamples"/>). Getter is
    /// always the already-applied value currently in effect, same "eventual, not immediate"
    /// visibility caveat as <see cref="SenseLevel"/>'s own getter.</summary>
    bool AutoSlantEnabled { get; set; }

    /// <summary>Port of legacy's real <c>sys.m_AutoSync</c> -- gates only Auto Sync's own trigger
    /// branches, not the drift-detection bookkeeping that runs unconditionally either way (see
    /// <c>AnalogFmSstvDecoder._autoSyncEnabled</c>'s own doc comment for the full citation). Same
    /// deferred-latch live-apply shape as <see cref="AutoSlantEnabled"/> above -- genuinely
    /// live-settable (2026-08-27).</summary>
    bool AutoSyncEnabled { get; set; }

    /// <summary>Port of legacy's real <c>sys.m_AutoStop</c> -- see
    /// <c>AnalogFmSstvDecoder.AutoStopEnabled</c>'s own doc comment for the counter-accumulates-
    /// regardless behavioral note and a related, newly-reachable divergence once all three of this
    /// flag's sibling gates are independently live. Same deferred-latch live-apply shape as
    /// <see cref="AutoSlantEnabled"/> above -- genuinely live-settable (2026-08-27).</summary>
    bool AutoStopEnabled { get; set; }

    /// <summary>Port of legacy's real <c>m_SyncRestart</c> ("Lock" toolbar toggle) -- see
    /// <c>AnalogFmSstvDecoder.SyncRestartEnabled</c>'s own doc comment for why this one, unlike its
    /// three siblings here, additionally rebuilds the RX bandpass filter's H1 coefficients and
    /// re-anchors VIS-lock state on the false-&gt;true edge. Same deferred-latch live-apply shape as
    /// <see cref="AutoSlantEnabled"/> above -- genuinely live-settable (2026-08-27).</summary>
    bool SyncRestartEnabled { get; set; }

    /// <summary>VIS-lock envelope-amplitude sense-level ("Squelch level") preset currently in
    /// effect, as the clamped 0-3 index (0=Very low, 1=Low [the real shipped default], 2=High,
    /// 3=Very high) -- NOT a raw threshold value in any physical unit; the underlying <c>SLvl</c>/
    /// <c>SLvl2</c>/<c>SLvl3</c> thresholds this selects live in the same clamped AGC-envelope
    /// domain as <see cref="SignalPeakLevel"/>'s pre-division reads, not dB (an earlier UI stub's
    /// "−26 dB" placeholder was a fake, decorative literal with no real conversion behind it). For
    /// the Sync &amp; Slant card's "Squelch level" row -- a consumer should display the preset NAME
    /// (this port's Options window already has real locale keys for the 4 names), not this raw
    /// index.
    ///
    /// Genuinely live-settable (user-reported 2026-08-27, "Squelch level" live control) -- matching
    /// legacy's own <c>Option.cpp:612-613</c>
    /// (<c>CSSTVDEM::SetSenseLvl</c> called on the live demodulator instantly, unconditionally, no
    /// state reset). Safe to set from any thread; the request is deferred (last-request-wins) and
    /// applied on whichever thread next calls <see cref="PushSamples"/>, the same deferred shape as
    /// <see cref="RequestNotch"/>. Getter is always the already-clamped 0-3 value currently in
    /// effect -- safe to read from any thread, though a just-set value may not be visible via the
    /// getter until the next <see cref="PushSamples"/> call has drained it (eventual, not
    /// immediate).</summary>
    int SenseLevel { get; set; }

    /// <summary>RX bandpass-filter sharpness currently in effect, mirrors legacy's real
    /// <c>CSSTVDEM::m_bpf</c> -- see <see cref="RxBpfPreset"/>'s own doc comment for the full
    /// contract. For the Input Chain card's "BPF" row -- the locked-filter cutoff frequency varies
    /// by whether a mode is currently locked (H1) vs still searching (H2, 400-2500 Hz at every
    /// preset), so a consumer should display the preset NAME only, not a cutoff figure that would
    /// be wrong whenever unlocked. Fixed for THIS object's whole lifetime -- unlike
    /// <see cref="AutoSlantEnabled"/>/<see cref="AutoSyncEnabled"/>/<see cref="AutoStopEnabled"/>/
    /// <see cref="SyncRestartEnabled"/> above, there is no in-place mutation path on this interface
    /// alone. Genuinely LIVE as of 2026-08-27 (restart-required-settings backlog item 2) ONLY via
    /// <c>RestartableSstvDecoder</c>'s optional <c>ISstvDecoderReconfiguration</c> side-channel
    /// (same shape as <see cref="ISstvDecoder"/>'s sibling <c>ISstvDecoderMaintenance</c>), which
    /// requests an idle-gated whole-instance swap rather than mutating this getter's value directly
    /// -- a caller holding only this bare interface has no way to request a change. Safe to read
    /// from any thread.</summary>
    RxBpfPreset RxBpfPreset { get; }

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
    /// port-only null case with no legacy analogue); during the mid-reception AVT-training-hand-off
    /// window (the tracker is NOT torn down by that path, which leaves the current mode momentarily
    /// null -- same class of gap <see cref="SlantPpm"/> had to be fixed for); and -- a second,
    /// distinct window found by a later audit round -- during a NON-AVT mid-reception restart's own
    /// pending-anchor-correction window, where the mode has already changed to the new one but the
    /// tracker has not yet been rebuilt for it. <c>0.0</c>, not <see langword="null"/>, once tracking
    /// is active but before AFC's first lock (matching legacy's own zero-correction-until-locked
    /// default). Both leak windows are closed by the implementation checking the pending-anchor
    /// state explicitly, not just the tracker reference or the mode alone -- see
    /// <c>AnalogFmSstvDecoder.SyncFrequencyCorrectionHz</c>'s own doc comment for the implementation
    /// detail.
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
    /// <see langword="false"/>, matching legacy's own default. Deliberately LIVE-settable directly on
    /// THIS interface (unlike <see cref="RxBpfPreset"/> above, which needs the separate
    /// <c>ISstvDecoderReconfiguration</c> side-channel to change live) -- legacy's own
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
