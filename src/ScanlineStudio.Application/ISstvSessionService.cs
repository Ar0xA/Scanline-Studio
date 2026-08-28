using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application;

/// <summary>Orchestrates <c>ScanlineStudio.Abstractions.Audio.IAudioEngine</c> + <see cref="ISstvEncoder"/>/
/// <see cref="ISstvDecoder"/> + <see cref="IWaterfallSource"/> + <see cref="IReceivedImageBuffer"/> for
/// the UI — see the Phase-3 plan's decision #9. Fans a single capture stream out to the decoder and
/// the waterfall in isolation (decision #3: neither can stall the other), and interlocks TX/RX
/// (decision #10): <see cref="TransmitAsync"/> pauses capture for the duration of the transmission (so
/// an audio-cable-looped demo doesn't decode its own TX) and keys PTT around playback.
///
/// <b>Concurrency</b>: <see cref="ModeDetected"/> and whatever <see cref="IReceivedImageBuffer.Updated"/>
/// the caller subscribes to both fire synchronously from the audio drain thread — same contract as
/// <see cref="IWaterfallSource.Frames"/> and <c>ScanlineStudio.Abstractions.Radio.IRadioController.StateChanges</c>
/// already established in this codebase. UI subscribers marshal to their own scheduler themselves;
/// this service does not depend on Avalonia (spec/01-architecture.md's layering rule) and so cannot do
/// that marshaling itself.</summary>
public interface ISstvSessionService : IAsyncDisposable
{
    IWaterfallSource Waterfall { get; }

    IReceivedImageBuffer ReceivedImage { get; }

    /// <summary>Every mode <see cref="TransmitAsync"/> can encode — exposed here (not
    /// <c>ScanlineStudio.Core.Sstv.SstvModeRegistry.All</c> directly) so <c>ScanlineStudio.UI</c>'s TX pane can list
    /// modes without referencing a <c>ScanlineStudio.Core.*</c> concrete assembly (spec/09-ui.md's layering
    /// rule, enforced by the architecture test).</summary>
    IReadOnlyList<SstvModeDefinition> AvailableModes { get; }

    /// <summary>Duration of the fixed leader-tone burst TX always sends before <paramref name="mode"/>'s
    /// VIS header -- exposed here (not <c>ScanlineStudio.Core.Sstv.AnalogFmSstvEncoder</c> directly) for
    /// the same layering reason as <see cref="AvailableModes"/> above. See
    /// <c>AnalogFmSstvEncoder.GetLeaderToneDurationMs</c>'s own doc comment for why this isn't called
    /// "VOX tone" despite backing the TX pane's "VOX tone" display field (that field's name predates
    /// this piece; legacy's real VOX feature is a different, unported thing).</summary>
    double GetLeaderToneDurationMs(SstvModeDefinition mode);

    /// <summary>Which VIS-header shape <paramref name="mode"/> transmits and its real on-air value --
    /// see <see cref="VisHeaderKind"/>'s own doc comment. Same layering reason as
    /// <see cref="AvailableModes"/> above.</summary>
    (VisHeaderKind Kind, int Value) GetVisHeaderInfo(SstvModeDefinition mode);

    /// <summary>Whether capture is currently running -- reflects the same internal state
    /// <see cref="StartReceivingAsync"/>/<see cref="StopReceivingAsync"/> already track, so it's
    /// accurate for the header's Receiving/Halt toggle including the case where a startup
    /// auto-start silently failed (no audio device) or <see cref="TransmitAsync"/> is transiently
    /// pausing capture for the duration of a transmission.</summary>
    bool IsReceiving { get; }

    /// <summary>Whether RX auto-detect is currently paused -- the port of legacy's RX-page
    /// <c>SBAuto</c> toggle (<c>TMmsstv::RxAutoPush</c>, `Main.cpp:6042-6060`). Session-owned state,
    /// NOT decoder state: capture and the waterfall keep running while paused; only the decoder
    /// stops receiving audio. Defaults to <see langword="false"/> (listening) on a fresh instance and
    /// changes ONLY via <see cref="SetAutoDetectPaused"/> -- does NOT reset on
    /// <see cref="StartReceivingAsync"/>. This is a DELIBERATE DEVIATION from legacy (code-review
    /// finding, round 2, elegant-wondering-hinton.md), not a fidelity claim: legacy's own
    /// <c>m_SyncMode</c> does NOT survive a transmit -- <c>TMmsstv::ToTX</c> (`Main.cpp:7360`)
    /// calls <c>pDem-&gt;Stop()</c> (guarded only by the TXLoopBack option, off by default --
    /// `Main.cpp:7358`), which sets it to 512 (`sstv.cpp:1786`), the
    /// entry to a self-clearing 0.5s wait (`sstv.cpp:2243-2252`) that lands back on 0 (unpaused)
    /// regardless of the pre-Stop value -- so legacy silently un-pauses ~0.5s after every TX, and its
    /// own UI honestly reflects that. This port keeps the flag STICKY across TX and any Stop/Start RX
    /// cycle instead, because pause/resume is session/UX state, not DSP/protocol math -- outside this
    /// project's legacy-fidelity scope -- and legacy's drop-after-TX reads as an artifact of
    /// <c>Stop()</c>'s shared teardown path, not an intentional design choice.</summary>
    bool IsAutoDetectPaused { get; }

    /// <summary>Sets <see cref="IsAutoDetectPaused"/>. Pausing requests the decoder abandon whatever
    /// reception is currently in progress (cleanly, via
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestAbandonReception"/> -- not
    /// left dangling) before audio stops being forwarded to it. Safe to call from the UI thread.
    /// This method does NOT get called implicitly by <see cref="ForceMode"/> -- forcing a mode while
    /// paused leaves this session-layer flag paused (harmless: <c>RequestAbandonReception</c> always
    /// drains before <c>ForceMode</c>'s own commit, see that method's own doc comment, so the forced
    /// mode still locks correctly; it just won't decode anything, since audio is still being withheld
    /// from the decoder). The production RX pane resumes explicitly and separately, by setting
    /// <see cref="IsAutoDetectPaused"/> false itself before calling <see cref="ForceMode"/> --
    /// matching legacy's <c>Start()</c>/<c>Start(mode,f)</c> sharing <c>SBAuto</c>'s own variable.
    /// Any other caller that forces a mode while paused should do the same.</summary>
    void SetAutoDetectPaused(bool paused);

    event Action<SstvModeDefinition>? ModeDetected;

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.DecodeRestarted"/>
    /// -- same synchronous, audio-drain-thread concurrency contract as <see cref="ModeDetected"/>.
    /// Not exposed until 2026-08-15 (logging-coverage audit finding): a mid-reception restart -- a
    /// stronger/cleaner sync lock found on a new transmission, a user-forced mode change
    /// (<see cref="ForceMode"/>), or Auto-Stop abandonment -- previously had no reachable subscriber
    /// in <c>ScanlineStudio.UI</c> at all (a separate Core.Logbook subscriber,
    /// <c>ReceiveHistoryRecorder</c>, already existed and is unaffected by this addition). See
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.DecodeRestarted"/>'s own doc comment
    /// for the underlying event's full semantics: the argument is the *abandoned* mode, not the new
    /// one, and ordering relative to a following <see cref="ModeDetected"/> is not fixed.</summary>
    event Action<SstvModeDefinition>? DecodeRestarted;

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.StationIdDecoded"/>
    /// -- same synchronous, decode-thread concurrency contract as that event (see its own doc
    /// comment), NOT gated on a call to <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> being
    /// in-flight, since those calls fully pause RX capture for their own duration (no samples can
    /// reach the decoder to raise this while one is running). <b>Narrower claim than an earlier
    /// version of this doc comment made (auditor round-1 finding on Phase 5)</b>: this is NOT true
    /// for the whole "PTT considered keyed" window in general -- <see cref="SetPttLockAsync"/> keys
    /// PTT WITHOUT touching capture at all, so RX (and this event) can keep running while that lock
    /// is engaged. No consumer needs an explicit "not transmitting" gate for the
    /// <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> case specifically, but one is NOT
    /// guaranteed redundant for a PTT-lock-engaged scenario if a future consumer ever cared about
    /// that distinction.</summary>
    event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    /// <summary>The operator's own configured callsign (<c>OperatorSettings.Callsign</c>), run
    /// through <c>ScanlineStudio.Core.Sstv.StationIdCallsignNormalizer.Normalize</c> (uppercase/trim/
    /// 16-char cap, <c>Option.cpp:445-448</c>) -- exists so a <c>ScanlineStudio.UI</c> consumer (e.g.
    /// a decoded-station-ID self-filter, avoiding auto-filling "his callsign" with the operator's
    /// own) can read it without referencing <c>ScanlineStudio.Settings.ISettingsStore</c> directly,
    /// which would violate this project's UI layering rule (<c>ScanlineStudio.UI</c> only talks to
    /// <c>ScanlineStudio.Application</c> service interfaces). <b>Normalized, not the raw stored
    /// value</b> (auditor round-1 finding on Phase 5 -- a real bug in an earlier version of this
    /// method): a decoded FSK station-ID callsign is always normalized by construction, so comparing
    /// it against an operator's un-normalized stored callsign (e.g. <c>"w1aw"</c>) would silently
    /// fail to self-filter. <see langword="null"/> only when unset (empty stays empty, not
    /// normalized away to something else) -- <c>OperatorSettings.Callsign</c> itself is left
    /// as-typed in storage; this method normalizes fresh on every call, it never writes back.</summary>
    Task<string?> GetOperatorCallsignAsync(CancellationToken ct = default);

    /// <summary>The operator's own configured Maidenhead grid square (<c>OperatorSettings.Grid</c>),
    /// as-typed in storage -- no normalization, unlike <see cref="GetOperatorCallsignAsync"/> (grid
    /// squares aren't compared for self-filtering, so there's no equivalent correctness reason to
    /// canonicalize here; <see cref="MaidenheadLocator.TryToLatLon"/> already tolerates common
    /// formatting variance on the consuming side). Exists for the same UI-layering reason as
    /// <see cref="GetOperatorCallsignAsync"/> -- a <c>ScanlineStudio.UI</c> consumer (the Frame
    /// Metadata card's own-station-to-worked-station distance readout) needs this without referencing
    /// <c>ScanlineStudio.Settings.ISettingsStore</c> directly.</summary>
    Task<string?> GetOperatorGridAsync(CancellationToken ct = default);

    /// <summary>Read-only preview of what <see cref="TransmitAsync"/> would actually resolve and
    /// encode right now -- literally the same settings-resolution logic (CW-ID/FSK station-ID
    /// subsystem Phase 4's <c>StationIdSettings</c>/<c>OperatorSettings</c> read + gate/fallback
    /// logic), exposed here so a <c>ScanlineStudio.UI</c> consumer (e.g. the Transmit tab's
    /// "Identification" summary card) can show the CURRENT effective configuration without
    /// referencing <c>ScanlineStudio.Core.Sstv.StationIdSettings</c> directly (banned by
    /// <c>UiLayeringArchitectureTests</c>). Side-effect-free -- reads settings, computes gates, does
    /// NOT transmit anything.</summary>
    Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default);

    /// <summary>Ultracode audit finding #34's automatic-restart mechanism (see
    /// <c>ScanlineStudio.Core.Sstv.RestartableSstvDecoder</c>) has gone past its warning threshold
    /// without an opportunity to swap yet -- fires at most once per restart cycle, cleared by
    /// <see cref="MaintenanceWarningCleared"/>. Fires synchronously on the audio drain thread, same
    /// contract as <see cref="ModeDetected"/>.</summary>
    event Action? MaintenanceWarningRaised;

    /// <summary>The condition <see cref="MaintenanceWarningRaised"/> warned about has resolved (a
    /// restart happened before the critical threshold was reached).</summary>
    event Action? MaintenanceWarningCleared;

    /// <summary>The critical threshold was reached -- the decoder has already force-restarted
    /// unconditionally (this is a notification, not a request), and by the time this fires RX has
    /// already been stopped via <see cref="StopReceivingAsync"/>. The user must manually call
    /// <see cref="StartReceivingAsync"/> again.</summary>
    event Action? MaintenanceCriticalStopRaised;

    /// <summary>Fires whenever the underlying decoder's inner instance is replaced by a swap -- BOTH
    /// the periodic maintenance ones (same underlying signal as <see cref="ScanlineStudio.Core.Sstv.ISstvDecoderMaintenance.Restarted"/>)
    /// AND a restart-required-settings backlog item 2 (2026-08-27) reconfiguration swap. Deliberately
    /// NOT named "DecoderRestarted" (one letter away from <see cref="DecodeRestarted"/> above, a
    /// semantically different mid-reception mode-abandonment event -- round-3 plan-review N1). Exists
    /// so a UI-layer cache of anything read from the decoder (currently only
    /// <c>RxImagePaneViewModel.RxBpfPreset</c>) can re-read it after whatever swap might have changed
    /// it, without polling and without a second Options-Closed pull hook (round-2 plan-review
    /// confirmed a pull would read the exact same value a push does here -- no reason to keep both).
    /// Fires synchronously on the audio drain thread, same contract as <see cref="ModeDetected"/> --
    /// a UI-layer subscriber must marshal to the UI thread itself before touching any bound
    /// property.</summary>
    event Action? DecoderInstanceReplaced;

    /// <summary>Requests a one-time manual sync correction from the decoder — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestReSync"/> for the full contract
    /// (the port of legacy's real "ReSync" button). Safe to call from any thread; a no-op if not
    /// currently receiving a locked image.</summary>
    void RequestReSync();

    /// <summary>Turns the RX notch filter on/off and/or retunes it — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestNotch"/> for the full contract
    /// (the port of legacy's real spectrum-click notch control). Unlike <see cref="RequestReSync"/>,
    /// this is persistent state, not a one-shot command. Safe to call from any thread.</summary>
    void RequestNotch(bool enabled, double? frequencyHz);

    /// <summary>Arms a one-shot Decoder Trace capture from the decoder — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.ArmScopeCapture"/> for the full
    /// contract (the port of legacy's real oscilloscope trigger). Safe to call from any thread.</summary>
    void ArmScopeCapture(int size);

    /// <summary>Requests a live "Squelch level" (VIS-lock sense-level) change — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SenseLevel"/> for the full contract
    /// (the port of legacy's real <c>Option.cpp:612-613</c> live-apply). A method here, not a
    /// property setter, matching this facade's own established convention: anything the UI actively
    /// REQUESTS is a method (<see cref="RequestNotch"/>/<see cref="RequestReSync"/>/
    /// <see cref="ArmScopeCapture"/>), while <see cref="SenseLevel"/> below stays a read-only
    /// reflection of current state. Safe to call from any thread. Deliberately does NOT persist
    /// anything to disk -- see <see cref="PersistSenseLevelAsync"/> for that, a separate call so this
    /// one stays fast/synchronous/non-throwing (needed by <c>OptionsWindowViewModel</c>'s own save
    /// flow, which calls this as a plain statement, not awaited).</summary>
    void RequestSenseLevel(int level);

    /// <summary>Requests a live Auto-Sync toggle -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.AutoSyncEnabled"/> for the full
    /// contract (2026-08-27, restart-required-settings backlog item 1). Unlike
    /// <see cref="RequestSenseLevel"/>, this has no separate <c>PersistXAsync</c> counterpart --
    /// this setting has no standalone out-of-dialog control, so <c>OptionsSettingsService.SaveAsync</c>
    /// already persists it as part of the whole-dialog Save; this method is the live-apply half only.
    /// Safe to call from any thread.</summary>
    void RequestAutoSyncEnabled(bool enabled);

    /// <summary>Requests a live Auto-Stop toggle -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.AutoStopEnabled"/> for the full
    /// contract, including a real behavioral note about counter accumulation while this flag is
    /// off. See <see cref="RequestAutoSyncEnabled"/>'s own doc comment for why no separate persist
    /// method exists. Safe to call from any thread.</summary>
    void RequestAutoStopEnabled(bool enabled);

    /// <summary>Requests a live Auto-Slant toggle -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.AutoSlantEnabled"/> for the full
    /// contract. See <see cref="RequestAutoSyncEnabled"/>'s own doc comment for why no separate
    /// persist method exists. The Receive tab's own "Auto-correct" status text
    /// (<c>RxImagePaneViewModel.AutoCorrectDisplay</c>) is refreshed separately, on the Options
    /// window's own Closed event (see <c>MainWindow.axaml.cs</c>), the same pattern
    /// <see cref="RequestSenseLevel"/>'s own Receive-tab dropdown already uses. Safe to call from
    /// any thread.</summary>
    void RequestAutoSlantEnabled(bool enabled);

    /// <summary>Requests a live Sync-Restart ("Auto-restart") toggle -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SyncRestartEnabled"/> for the full
    /// contract, including the RX-bandpass-filter-rebuild and VIS-lock-re-anchor side effects this
    /// one setting has that its three siblings above don't. See
    /// <see cref="RequestAutoSyncEnabled"/>'s own doc comment for why no separate persist method
    /// exists. Safe to call from any thread.</summary>
    void RequestSyncRestartEnabled(bool enabled);

    /// <summary>Requests that RX BPF preset, Demod type, and RX buffer mode apply the next time the
    /// decoder is idle -- restart-required-settings backlog item 2 (2026-08-27). Unlike
    /// <see cref="RequestAutoSyncEnabled"/> and its siblings above, none of these three has any
    /// in-place mutation path on the underlying decoder (each is read once, at construction) -- see
    /// <c>ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.RequestReconfiguration</c>'s own doc
    /// comment for the full idle-gating and failure-handling contract. A no-op if the real decoder
    /// doesn't implement that optional side-channel (matches every <c>ISstvDecoderMaintenance</c>-gated
    /// call in this class -- the various fake decoders used by other test projects don't implement it
    /// either). Called unconditionally on every Options Save, same as
    /// <see cref="RequestAutoSyncEnabled"/>'s own convention -- see that call site's own doc comment
    /// for why no diff-against-current-value check is needed first. Safe to call from any thread.</summary>
    void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode);

    /// <summary>Requests an RxBpfPreset-only change for the Receive tab's own live "BPF" dropdown
    /// (<c>RxImagePaneViewModel</c>) -- restart-required-settings backlog item 6 (2026-08-28). Same
    /// idle-gated-until-drained contract as <see cref="RequestReconfiguration"/>, but forwards to
    /// <c>ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.RequestRxBpfPreset</c>, NOT
    /// <see cref="RequestReconfiguration"/> called with some "current" DemodType/RxBufferMode read
    /// back from somewhere -- that would silently discard a separately-queued Options change to
    /// either of those two fields, or get silently discarded by one; see that method's own doc
    /// comment for the full per-field-preservation reasoning. A no-op if the real decoder doesn't
    /// implement the optional side-channel, matching <see cref="RequestReconfiguration"/>'s own
    /// convention. NOTE: a LATER <see cref="RequestReconfiguration"/> call (e.g. an Options Save)
    /// overwrites whatever this queues -- that call writes all three fields unconditionally, by
    /// design. Safe to call from any thread.</summary>
    void RequestRxBpfPreset(RxBpfPreset rxBpfPreset);

    /// <summary>Restart-required-settings backlog item 4 (sample-rate live-apply, 2026-08-27) --
    /// applies a new sample rate live, without an app restart. Different in kind from
    /// <see cref="RequestReconfiguration"/> and every other <c>Request*</c> method on this interface:
    /// sample rate determines what format the sound card itself captures/plays at, so a live change
    /// here can require reopening the actual RX capture device, not just swapping an in-memory
    /// decoder object.
    ///
    /// Sequencing (all under this session's own RX-transition gate, serializing against
    /// <see cref="StartReceivingAsync"/>/<see cref="StopReceivingAsync"/>/<see cref="DecodeFromFileAsync"/>):
    /// if RX was actively receiving, capture is stopped FIRST, the new rate is committed to the
    /// decoder directly (not idle-gated the way <see cref="RequestReconfiguration"/> is -- capture
    /// being stopped already establishes it's safe), then capture reopens at the new rate. This
    /// ABORTS any reception in progress (mid-image or not) -- unavoidable, since the capture stream
    /// itself is being closed and reopened. If RX was idle, the rate commits immediately with no
    /// capture to coordinate. The encoder (TX) and waterfall pick up the new rate independently -- see
    /// <see cref="SampleRateApplyResult"/>'s own doc comment for the full per-outcome contract.
    ///
    /// Deferred (returns <see cref="SampleRateApplyResult.DeferredRecordingInProgress"/>, changes
    /// nothing) while a recording is in progress -- the WAV header is written from the capture rate at
    /// finalize time; changing rate mid-recording would produce a file describing a rate the audio was
    /// never actually captured at. A <c>_rxTransitionGate</c> timeout throws
    /// <see cref="TimeoutException"/> (matching <see cref="StartReceivingAsync"/>'s own established
    /// choice) rather than being folded into the result. A no-op if the underlying decoder doesn't
    /// implement the optional live-apply side-channel, matching <see cref="RequestReconfiguration"/>'s
    /// own convention. Safe to call from any thread.</summary>
    Task<SampleRateApplyResult> RequestSampleRateAsync(int sampleRate, CancellationToken ct = default);

    /// <summary>Targeted single-field persist for the Receive tab's own live "Squelch level"
    /// dropdown (<c>RxImagePaneViewModel</c>) -- a real settings-file read-modify-write against
    /// <c>SstvDecoderSettings.SenseLevel</c> ONLY, preserving whatever else is currently persisted in
    /// that section. Lives here (Application layer), not in <c>RxImagePaneViewModel</c> itself,
    /// because <c>ScanlineStudio.UI</c> is never allowed to reference a <c>ScanlineStudio.Core.*</c>
    /// project directly (`UiLayeringArchitectureTests`) and <c>SstvDecoderSettings</c> lives in
    /// <c>ScanlineStudio.Core.Sstv</c>. Distinct from <c>OptionsWindowViewModel</c>'s own whole-dialog
    /// save (via <c>OptionsSettingsService.SaveAsync</c>), which also writes this same section for
    /// its other 8 fields -- both are real read-modify-writes against the current on-disk state, so
    /// neither clobbers the other's OTHER fields; the two writers briefly disagreeing about
    /// SenseLevel specifically if both are used in close succession is an accepted, narrow race (see
    /// this feature's own plan-review), not solved further here.</summary>
    Task PersistSenseLevelAsync(int level, CancellationToken ct = default);

    /// <summary>Targeted single-field persist for the Receive tab's own live "BPF" dropdown, same
    /// read-modify-write shape as <see cref="PersistSenseLevelAsync"/> (against
    /// <c>SstvDecoderSettings.RxBpfPreset</c> only). Restart-required-settings backlog item 6
    /// (2026-08-28). Also called when an Options-originated BPF change is rejected (via
    /// <see cref="ReconfigurationRejected"/>'s own refresh path), so settings.json comes back in sync
    /// with whatever's actually applied -- deliberate: <c>RequestReconfiguration</c>'s own contract
    /// already says a rejected request is dropped, not retried, so there is no "try again later"
    /// state this write could clobber. Same accepted narrow disagreement-with-Options'-own-Save race
    /// as <see cref="PersistSenseLevelAsync"/>'s own doc comment describes -- not solved further
    /// here.
    ///
    /// Code-review round-1 finding: this is now the SECOND independent live-persist chain in this
    /// class (alongside <see cref="PersistSenseLevelAsync"/>'s own), each doing its own whole-
    /// <c>AppSettings</c> load/modify/save -- <c>JsonSettingsStore</c> holds its file lock per call,
    /// not across a chain's own read-modify-write, so two dropdown changes landing within the same
    /// few milliseconds could interleave and the loser's write is dropped (never a corrupted file --
    /// <c>JsonSettingsStore</c> writes to a temp file and atomically renames it, so the only failure
    /// mode is a lost write, not a torn one). This is the SAME pre-existing whole-<c>AppSettings</c>
    /// last-write-wins race <c>JsonSettingsStore</c>'s own class doc already documents across its
    /// ~30 call sites app-wide -- NOT scoped to just the <c>SstvDecoderSettings</c> section, and a
    /// losing write can revert an unrelated section too. Narrow (needs near-simultaneous changes to
    /// two different Receive-tab dropdowns) and self-correcting FOR THE LOSING DROPDOWN specifically
    /// (its own next change re-reads and re-writes the current state) -- a change to the OTHER
    /// dropdown instead just re-persists the already-stale value, not a fix. Not solved further here,
    /// same accepted-risk class as the race above, one more participant.</summary>
    Task PersistRxBpfPresetAsync(RxBpfPreset preset, CancellationToken ct = default);

    /// <summary>Fires when a queued RxBpfPreset/DemodType/RxBufferMode/SampleRate reconfiguration
    /// request (from ANY source -- <see cref="RequestReconfiguration"/>, <see cref="RequestRxBpfPreset"/>,
    /// or a rejected <see cref="RequestSampleRateAsync"/> commit) could not be applied --
    /// restart-required-settings backlog item 6 (2026-08-28). Forwards
    /// <c>ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.ReconfigurationRejected</c>. Exists so
    /// <c>RxImagePaneViewModel</c>'s "BPF" row -- now a live EDITOR, not a read-only mirror -- can
    /// re-sync back to whatever's actually applied instead of permanently showing a preset that was
    /// requested but never took effect (already fires unconditionally for every rejection, not just a
    /// BPF-driven one -- a rejected sample-rate commit clears the WHOLE pending record, including any
    /// separately-queued BPF change, so any rejection can leave this row stale). Fires synchronously
    /// on whichever thread the swap ran on -- a UI-layer subscriber must marshal to the UI thread
    /// itself before touching any bound property, same contract as <see cref="DecoderInstanceReplaced"/>.</summary>
    event Action? ReconfigurationRejected;

    /// <summary>Returns the completed channel-0 Decoder Trace capture, or <see langword="null"/> if
    /// not yet complete — see <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.TryGetScopeCaptureChannel0"/>
    /// for the full contract. Safe to call from any thread, at any time.</summary>
    double[]? TryGetScopeCaptureChannel0();

    /// <summary>Returns the completed channel-1 Decoder Trace capture, or <see langword="null"/> if
    /// not yet complete (including "never will," if no reception was active since the arm) — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.TryGetScopeCaptureChannel1"/> for the
    /// full contract. Safe to call from any thread, at any time.</summary>
    double[]? TryGetScopeCaptureChannel1();

    /// <summary>Requests a one-time "Correct Slant" search from the decoder — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestCorrectSlant"/> for the full
    /// contract (the port of legacy's real "Correct Slant" popup-menu item, `KRCS`/`KRCSClick`,
    /// `Main.cpp:13176` — the same right-click context menu on the picture box that also holds
    /// `KRFS`, this port's own already-shipped <see cref="RequestReSync"/> button). Safe to call from
    /// any thread; a no-op if not currently receiving a locked image, or under any of the other
    /// conditions <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestCorrectSlant"/>'s
    /// own doc comment lists.</summary>
    void RequestCorrectSlant();

    /// <summary>Abandons whatever reception is currently in progress and returns to listening for a
    /// new VIS header -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestAbandonReception"/> for the
    /// full contract. Unlike <see cref="SetAutoDetectPaused"/>, this does NOT pause auto-detect or
    /// stop forwarding audio to the decoder -- it only discards the in-progress image so the next
    /// VIS header can lock immediately. Safe to call from any thread; a no-op if nothing is
    /// currently being received. Backs the Receive tab's own Abort button -- zero legacy precedent
    /// (an invented UI affordance, not a port; see docs/removed-features.md's own note that CAT/rig
    /// control is the only area with a hard no-invention rule).</summary>
    void AbortReception();

    /// <summary>Requests an immediate decode restart into <paramref name="mode"/>, bypassing VIS
    /// header detection — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.ForceMode"/> for the full contract
    /// (the port of legacy's real RX quick-mode-button click). Safe to call from any thread; a
    /// one-shot request applied on the next decoded chunk, not a persistent lock.</summary>
    void ForceMode(SstvModeDefinition mode);

    Task StartReceivingAsync(CancellationToken ct = default);

    Task StopReceivingAsync();

    /// <summary>Piece C1 (RX tab Re-decode port, `spec/16-gui-wiring-survey.md`): starts recording
    /// the raw capture stream to <paramref name="path"/> as a 16-bit mono WAV file, matching legacy's
    /// own record tap point (<c>WaveFile.ReadWrite</c> runs before the notch/LMS/demod loop,
    /// `Sound.cpp:334`) — the SAME pre-filter buffer <see cref="Waterfall"/>/the decoder itself
    /// receive, not a post-filter tap. Throws <see cref="InvalidOperationException"/> if a recording
    /// is already in progress, or if <see cref="IsReceiving"/> is <see langword="false"/> (recording
    /// while not receiving would silently produce an empty file). The recording is a parallel sink,
    /// independent of <see cref="StartReceivingAsync"/>/<see cref="StopReceivingAsync"/>'s own
    /// subscribe/unsubscribe cycle — it deliberately survives a Stop/Start RX cycle while armed
    /// (gapping silently across it, since no samples arrive while capture is stopped), matching
    /// legacy's own record mode not touching capture devices at all
    /// (<c>Sound.cpp:459-471</c>'s device-swap branch is never entered for <c>m_mode==2</c>).</summary>
    Task StartRecordingAsync(string path);

    /// <summary>Stops an in-progress recording started by <see cref="StartRecordingAsync"/> and
    /// writes the buffered samples to disk. A no-op if no recording is in progress. Throws whatever
    /// the underlying file write throws (e.g. disk full, invalid path) — a genuine, user-visible
    /// failure for this explicit, user-initiated action, unlike <see cref="DisposeAsync"/>'s own
    /// best-effort finalize of an in-progress recording, which swallows and logs instead.</summary>
    Task StopRecordingAsync();

    /// <summary>Piece C2 (RX tab Re-decode port): decodes a previously-recorded WAV file at
    /// <paramref name="path"/> through the SAME decoder/waterfall/level-meter pipeline live capture
    /// uses — legacy's own file playback (<c>m_playmode</c>, `Sound.cpp:334-471`) substitutes only
    /// the audio SOURCE feeding the identical demod loop, no special-cased decode branch, and this
    /// mirrors that. Live RX is paused for the duration and resumed afterward only if it was already
    /// running. Rejects (throwing <see cref="InvalidOperationException"/>) if a file decode or a
    /// transmit/tune is already in progress, if auto-detect is paused, if the file's sample rate
    /// doesn't match <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SampleRate"/> (this
    /// port does not implement legacy's real resampler, `CWaveFile::ChangeSampFreq`,
    /// `Sound.cpp:713-785` — a known gap, tracked in `spec/14-roadmap.md`), or if the file contains no
    /// samples. The decoder's prior AGC/resonator/lock state carries through the seam onto the file's
    /// samples, matching legacy's own behavior at the same seam — not a "clean decode."</summary>
    Task DecodeFromFileAsync(string path, CancellationToken ct = default);

    /// <summary>Encodes and transmits <paramref name="image"/> as <paramref name="mode"/>. Pauses
    /// capture/decode for the duration (restored afterward only if RX was already running) and keys
    /// PTT via the injected <c>IRadioSessionService</c> around playback. Also rejects (throwing
    /// <see cref="InvalidOperationException"/>) while a <see cref="RunLoopbackSelfTestAsync"/> call
    /// is in progress -- code-review finding: a self-test's encode+decode is real CPU work competing
    /// with this method's own live PTT-keyed playback pump, and the self-test's own result dialog is
    /// MODAL, so it could otherwise pop up mid-transmission and block reaching Stop TX.</summary>
    Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default);

    /// <summary>Stub survey Tier 3, "Loopback self-test" (Calibration menu). One-shot software
    /// preview: encodes <paramref name="image"/> as <paramref name="mode"/> and decodes the result
    /// back, entirely in-process. NOT legacy's <c>RGLoopBack</c> Internal/External hardware loopback
    /// (impossible under this port's architecture -- <see cref="TransmitAsync"/>'s own doc comment;
    /// see <c>docs/removed-features.md</c>) and NOT live-monitoring-during-a-real-transmission
    /// (locked user decision, stub-survey plan-review).
    ///
    /// Uses a FRESH, throwaway decoder instance -- never the shared, DI-singleton <see
    /// cref="ISstvDecoder"/> the live RX pipeline/<c>ReceivedImageBuffer</c>/<c>ReceiveHistoryRecorder</c>
    /// observe. This is the load-bearing design decision (plan-review round 5): a private instance is
    /// unreachable from any of those, so nothing needs suppressing, nothing real gets abandoned, no RX
    /// history is touched, and the live RX pane's own displayed image is never affected. Runs with
    /// <c>sampleRateOffsetHz: 0.0</c> regardless of the persisted TX clock-offset setting -- the round
    /// trip has no physical hardware clock in it, so applying that correction would fabricate a slant
    /// with no real cause; this self-test structurally cannot measure real clock drift and must never
    /// be presented as doing so. Encodes with no station-ID footer (avoids <c>EndOfImage</c>'s
    /// documented low-risk false-lock-into-trailing-audio window; the self-test doesn't exercise the
    /// FSK footer path, an accepted scope gap). Rejects (throwing <see cref="InvalidOperationException"/>)
    /// if a transmit/tune or another self-test is already in progress -- and the reverse direction
    /// is enforced too, <see cref="TransmitAsync"/> rejects while a self-test is running (code-review
    /// finding: a full-image encode+decode is real CPU work competing with a live PTT-keyed playback
    /// pump, and this self-test's own result dialog is MODAL -- popping up mid-transmission would
    /// block reaching Stop TX). No relationship to
    /// <c>DecodeFromFileAsync</c>'s <c>_autoDetectPaused</c> guard (that pause exists for the SHARED
    /// decoder only; a private instance has no relationship to it, so is deliberately NOT gated on
    /// it). Does not feed the live waterfall/level-meter -- synthetic audio there would be
    /// misleading.</summary>
    Task<LoopbackSelfTestResult> RunLoopbackSelfTestAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default);

    /// <summary>Fires periodically (roughly every 4096 samples, ~0.37s at 11025Hz) while a
    /// <see cref="TransmitAsync"/> call is actively enqueuing samples to the playback device — spec/
    /// 18-path-to-1.0.md Medium item: "No TX send-progress feedback during transmit." Does NOT fire
    /// for <see cref="TuneAsync"/> (a fixed-tone AFC-lock aid, not something this feature targets).
    ///
    /// <b>Threading contract, same as <see cref="ModeDetected"/>/<see cref="DecodeRestarted"/></b>:
    /// raised SYNCHRONOUSLY on the playback pump thread, wrapped in a try/catch internally so a
    /// throwing subscriber cannot abort an in-flight transmission — but a slow SYNCHRONOUS subscriber
    /// still directly stalls sample enqueue (audible dropout risk). Subscribers must marshal to their
    /// own scheduler and must not block.
    ///
    /// <see cref="TransmitProgressInfo.Fraction"/> is derived from samples successfully enqueued to
    /// the playback device (not wall-clock elapsed time), which tracks real playback closely but not
    /// exactly: the playback engine's own internal buffer (on the order of ~1.5s of audio) means
    /// progress can reach 100% slightly before audio actually finishes playing, and the very first
    /// report can jump several percent at once on a short mode once that buffer fills.</summary>
    event Action<TransmitProgressInfo>? TransmitProgressChanged;

    /// <summary>User-reported gap (2026-08-18): the header's "Receiving" indicator stayed visually
    /// lit the same color throughout a local transmission, even though <see cref="TransmitAsync"/>/
    /// <see cref="TuneAsync"/> genuinely pause capture for that window (<see cref="IsReceiving"/>
    /// itself already correctly reports <see langword="false"/> during the pause -- the gap was that
    /// nothing ever PUSHED that change to a subscriber; <see cref="IsReceiving"/> is a plain,
    /// non-eventing property). Fires <see langword="true"/> only when THIS pause/resume pair is
    /// caused by an in-flight local transmission that actually paused a RUNNING capture --
    /// deliberately NOT the same signal as "RX capture stopped for any reason" (a manual Halt-button
    /// click does not fire this; that case is already correctly reflected by the Receiving toggle's
    /// own bound state). <see langword="false"/> fires once this pause's own resume attempt has
    /// finished (successfully or not) -- including the deferred <see cref="SetPttLockAsync"/>-
    /// triggered resume path when a PTT lock delayed it -- NOT merely once playback itself ends. This
    /// is <b>not</b> a guarantee that capture is actually listening again by the time it fires: a
    /// best-effort resume attempt that itself fails still raises <see langword="false"/> (see
    /// <see cref="IsReceiving"/> for the real state), and the residual case where a transmission
    /// deliberately leaves capture stopped (an unwired <see cref="TuneAsync"/> "stay keyed" option,
    /// no production caller today) also raises <see langword="false"/> once its own pause window is
    /// over even though capture was never resumed. Treat this purely as "no longer paused FOR THIS
    /// transmission," not as a restatement of <see cref="IsReceiving"/>.
    ///
    /// <b>Threading contract:</b> raised SYNCHRONOUSLY from whichever async call is doing the
    /// pause/resume (<see cref="TransmitAsync"/>/<see cref="TuneAsync"/>'s shared internal helper,
    /// or <see cref="SetPttLockAsync"/>'s own deferred-resume path) -- NOT guaranteed to be a single
    /// consistent thread across calls, unlike <see cref="TransmitProgressChanged"/>'s dedicated
    /// playback-pump thread. Wrapped in an internal try/catch, so a throwing subscriber cannot break
    /// the underlying pause/resume/unlock call it was raised from. A synchronous subscriber that
    /// re-enters <see cref="SetPttLockAsync"/> from this event will deadlock (that method's internal
    /// gate is not reentrant) -- subscribers must marshal to their own scheduler before touching UI
    /// state and must not block or re-enter this service. Fires at most twice per transmission (not a
    /// hot path, no rate-limiting).</summary>
    event Action<bool>? CapturePausedForTransmitChanged;

    /// <summary>Keys PTT, plays a steady sine tone at <paramref name="frequencyHz"/> for
    /// <paramref name="duration"/> (WSJT-X/legacy-Tune-style AFC-lock aid), then un-keys PTT --
    /// same pause-RX/key-PTT/resume-RX guarantee shape as <see cref="TransmitAsync"/>.</summary>
    /// <param name="leaveKeyedAfterTune"><b>Unverified against legacy YONIQ source</b> -- a direct
    /// citation for the exact post-tune-tone transition (e.g. legacy's own Tune button/`CtrBtn.cpp`)
    /// was not found; this parameter name deliberately does not claim legacy parity. When
    /// <see langword="true"/>, PTT is left keyed once the tone finishes (skipping the normal
    /// un-key/resume-RX step) instead of returning to RX -- intended for a "tune into satellite
    /// pass-through" workflow where the operator wants to go straight from a tune tone into
    /// transmitting. A subsequent <see cref="TransmitAsync"/> or <see cref="TuneAsync"/> call still
    /// keys PTT itself as normal (this flag only affects what happens at the END of THIS call, not
    /// any later one) -- callers wanting PTT held across multiple calls should use
    /// <see cref="SetPttLockAsync"/> instead, which is a distinct, independently-toggled
    /// mechanism.</param>
    Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default);

    /// <summary>"Pwr" -- post-encode TX playback gain (0-100), applied as a linear multiplier on
    /// encoded PCM samples right before playback (same shape WSJT-X's/fldigi's own Pwr-style
    /// controls use: pure app-internal gain on the audio THIS app generates, never touching the OS
    /// mixer). User-directed reversal of an earlier same-session design (real OS device volume) --
    /// see <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>'s own doc comment
    /// for the persisted field this reads/writes.</summary>
    Task<int> GetTxVolumePercentAsync(CancellationToken ct = default);

    Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default);

    /// <summary>Whether the playback device a real <see cref="TransmitAsync"/> call would resolve
    /// right now is currently muted at the OS level -- independent of <see cref="GetTxVolumePercentAsync"/>'s
    /// own app-internal Pwr gain (muting the OS device silences it regardless of what Pwr is set to,
    /// same way muting the OS output would silence WSJT-X too even at its own Pwr=100). Returns
    /// <see langword="false"/> if the device/backend doesn't support a mute query -- a passive
    /// readout defaulting to "assume unmuted," not an error. Read-only: there is no
    /// <c>SetTxDeviceMutedAsync</c> -- see <c>IAudioDeviceMuteQuery.IsDeviceMutedAsync</c>'s own
    /// doc comment for why.</summary>
    Task<bool> GetTxDeviceMutedAsync(CancellationToken ct = default);

    /// <summary>The display name of the TX playback device that a real <see cref="TransmitAsync"/>
    /// call would actually resolve and use right now (`AudioDeviceSettings.PlaybackDeviceId`
    /// resolved against the device enumerator, falling back to the backend-reported default device
    /// when nothing is explicitly configured -- spec/18-path-to-1.0.md Critical item 1 / item 8, a
    /// behavior change from this method's own earlier "null if nothing configured" contract), for a
    /// UI readout -- e.g. mock2's Transmit tab "Device" field. Reuses the exact same settings/
    /// enumerator/fallback lookup <see cref="TransmitAsync"/> itself uses, so this can never
    /// disagree with what a TX would actually use -- but non-throwing (<see langword="null"/>
    /// instead of an exception) for a configured device no longer present, or for the now-rare case
    /// where nothing is configured AND the backend reports no default either, since this is a
    /// passive readout, not an action that should fail loudly. Reflects what WOULD be resolved
    /// right now, not necessarily whatever device an already-in-flight <see cref="TransmitAsync"/>
    /// call resolved earlier under different settings -- a real, accepted approximation for a
    /// passive display, not a live "what is TX using right now" guarantee.</summary>
    Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default);

    /// <summary>The display name of the RX capture device a real <see cref="StartReceivingAsync"/>
    /// call would actually resolve and use right now (`AudioDeviceSettings.CaptureDeviceId`
    /// resolved against the device enumerator, with the same backend-reported-default fallback as
    /// <see cref="GetConfiguredPlaybackDeviceNameAsync"/> -- spec/18-path-to-1.0.md Critical item 1
    /// / item 8), for a UI readout -- e.g. mock2's Receive tab "Device" field. Exact mirror of
    /// <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s own contract (reuses the same
    /// settings/enumerator/fallback lookup <see cref="StartReceivingAsync"/> itself uses,
    /// non-throwing, reflects what WOULD be resolved right now, not necessarily whatever an
    /// already-running capture resolved earlier) -- see that method's own doc comment for the full
    /// reasoning, not repeated here.</summary>
    Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default);

    /// <summary>Whether <see cref="SetPttLockAsync"/>'s lock is currently engaged.</summary>
    bool IsPttLocked { get; }

    /// <summary>Pass-throughs of the underlying decoder's own read-only telemetry -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder"/>'s matching members for the full
    /// contract (null-cases, freshness caveats, legacy citations). All are safe to read from any
    /// thread, intended for a GUI to poll on a timer (their own doc comments say so explicitly) --
    /// none of them are event-driven.</summary>
    double? SlantPpm { get; }

    SstvSyncSource SyncSource { get; }

    int? SyncOffsetSamples { get; }

    double SignalPeakLevel { get; }

    /// <summary>User-reported fix (2026-08-23, round 2): <see cref="SignalPeakLevel"/> is NOT a
    /// general-purpose audio-input meter -- it's <c>LevelAgc.CurMax</c>, computed from samples that
    /// have already passed through the decoder's SSTV-band search bandpass filter
    /// (<c>AnalogFmSstvDecoder.AgcSampleAt</c>'s own doc comment: "legacy's real POST-2-tap-LPF-
    /// POST-bandpass-filter value"). Room noise, voice, or any audio outside the SSTV tone band gets
    /// filtered out before that meter ever sees it, so it stayed pinned near its floor even with a
    /// genuinely loud, correctly-selected microphone -- exactly the gap the user reported ("the
    /// audio device volume is not realistically represented from the actually used audio device").
    /// This property instead reports the peak absolute amplitude of the MOST RECENT raw captured
    /// buffer, straight off <see cref="ScanlineStudio.Abstractions.Audio.IAudioEngine.SamplesCaptured"/>
    /// -- before any SSTV-specific filtering -- so it moves with whatever the selected input device
    /// actually picks up, the same way a plain audio level meter (or WSJT-X's own input meter) does.
    /// Linear <c>[0.0, 1.0]</c> (capture samples are already float-normalized, spec/05-audio-
    /// engine.md:44 -- no <c>SignalPeakLevel</c>-style <c>/32768.0</c> rescale needed here). <c>0.0</c>
    /// whenever capture isn't running (no buffers arrive to update it, and it's reset to 0 on every
    /// <see cref="StopReceivingAsync"/>) -- never a stale reading from before capture stopped. Safe to
    /// read from any thread, same as <see cref="SignalPeakLevel"/> above, for the same reason
    /// (a plain field read/write, benign torn-read tolerance for a UI meter, not a correctness-
    /// sensitive value).</summary>
    double RawInputPeakLevel { get; }

    bool IsLevelOverdriven { get; }

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.AutoSlantEnabled"/>
    /// -- genuinely LIVE now (2026-08-27, restart-required-settings backlog item 1): it can change
    /// at runtime via Options Save, which is exactly why
    /// <c>RxImagePaneViewModel.RefreshAutoSlantEnabledFromSession</c> exists to re-read it afterward,
    /// same pattern as <see cref="SenseLevel"/>'s own refresh below. A caller that reads this once at
    /// construction and never again will go stale -- see <see cref="RequestAutoSlantEnabled"/> for
    /// the live-apply command (no separate persist method -- see that method's own doc comment for
    /// why).</summary>
    bool AutoSlantEnabled { get; }

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SenseLevel"/>
    /// -- genuinely LIVE: it can change at runtime (Options Save, or the Receive-tab dropdown),
    /// which is exactly why
    /// <c>RxImagePaneViewModel.RefreshSenseLevelFromSession</c> exists to re-read it after an
    /// Options-driven change. A caller that reads this once at construction and never again will go
    /// stale -- see <see cref="RequestSenseLevel"/> for the live-apply command and
    /// <see cref="PersistSenseLevelAsync"/> for persistence.</summary>
    int SenseLevel { get; }

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RxBpfPreset"/>
    /// -- genuinely LIVE now (2026-08-27, restart-required-settings backlog item 2), same shape as
    /// <see cref="AutoSlantEnabled"/>/<see cref="SenseLevel"/> above, but idle-GATED (a requested
    /// change only applies once the decoder next goes idle, see <see cref="RequestReconfiguration"/>)
    /// rather than applied in-place immediately. A caller that reads this once at construction and
    /// never again will go stale -- <c>RxImagePaneViewModel.RefreshRxBpfPresetFromSession</c> re-reads
    /// it, triggered by <see cref="DecoderInstanceReplaced"/> rather than the Options window's own
    /// Closed event (unlike <see cref="AutoSlantEnabled"/>/<see cref="SenseLevel"/>'s refresh hooks):
    /// the value can't actually change until a swap happens, so that's the only correct trigger.</summary>
    RxBpfPreset RxBpfPreset { get; }

    double? SyncFrequencyCorrectionHz { get; }

    int BufferedSampleCount { get; }

    /// <summary>Pass-through of the underlying <see cref="ScanlineStudio.Abstractions.Audio.IAudioEngine.CaptureOverrunCount"/>
    /// -- a DIFFERENT quantity from <see cref="BufferedSampleCount"/> above despite the similar
    /// name: this is the audio-engine's own dropped-frame overrun count (status bar's "· N XRUN"
    /// half), not the decoder's internal sample-history buffer. 0 whenever RX capture isn't running,
    /// same as the underlying engine property -- unlike that underlying property (see its own doc
    /// comment), THIS one absorbs the underlying engine's documented stop-capture race and reports
    /// <c>0</c> rather than letting it throw, matching this member's own "capture isn't running"
    /// contract value exactly (auditor round 1, batch-5 wiring). <b>Tier A Batch 1 re-audit round 5
    /// correction: this does NOT fully uphold the class-level "safe to read from any thread"
    /// contract</b> -- the underlying engine property's own doc comment now also documents a
    /// distinct hazard this implementation's <c>catch (ObjectDisposedException)</c> cannot absorb:
    /// the SAME race can make the underlying read BLOCK (on the disposing
    /// <c>MiniAudioCaptureSession</c>'s own write lock) rather than throw, for as long as that
    /// `Dispose()` call takes -- typically near-instant, bounded by
    /// <c>MiniAudioCaptureSession.CloseTimeout</c> (~5s) in the common case, unbounded if a
    /// `SamplesAvailable` subscriber elsewhere hangs. In this port's own real call graph that means
    /// the caller (`RxImagePaneViewModel`'s 250ms `DispatcherTimer` poll) can stall the UI thread,
    /// not just observe a caught exception. See <see cref="ScanlineStudio.Abstractions.Audio.IAudioEngine.CaptureOverrunCount"/>'s
    /// own doc comment for the full mechanism.</summary>
    int CaptureOverrunCount { get; }

    /// <summary>Manual-keying diagnostic aid: holds PTT keyed independent of any
    /// <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call, until unlocked. Idempotent both
    /// ways. See the implementation's own doc comment for the exact failure-handling and
    /// concurrency contract -- in short: a failure here is NOT swallowed (unlike this service's
    /// internal best-effort cleanup paths), and <see cref="IsPttLocked"/> only changes after
    /// <c>IRadioSessionService.SetPttAsync</c> actually confirms the requested state.</summary>
    Task SetPttLockAsync(bool locked, CancellationToken ct = default);
}
