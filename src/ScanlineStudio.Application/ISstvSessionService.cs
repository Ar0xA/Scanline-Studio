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

    /// <summary>Requests a one-time manual sync correction from the decoder — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestReSync"/> for the full contract
    /// (the port of legacy's real "ReSync" button). Safe to call from any thread; a no-op if not
    /// currently receiving a locked image.</summary>
    void RequestReSync();

    /// <summary>Requests a one-time "Correct Slant" search from the decoder — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestCorrectSlant"/> for the full
    /// contract (the port of legacy's real "Correct Slant" popup-menu item, `KRCS`/`KRCSClick`,
    /// `Main.cpp:13176` — the same right-click context menu on the picture box that also holds
    /// `KRFS`, this port's own already-shipped <see cref="RequestReSync"/> button). Safe to call from
    /// any thread; a no-op if not currently receiving a locked image, or under any of the other
    /// conditions <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RequestCorrectSlant"/>'s
    /// own doc comment lists.</summary>
    void RequestCorrectSlant();

    /// <summary>Requests an immediate decode restart into <paramref name="mode"/>, bypassing VIS
    /// header detection — see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.ForceMode"/> for the full contract
    /// (the port of legacy's real RX quick-mode-button click). Safe to call from any thread; a
    /// one-shot request applied on the next decoded chunk, not a persistent lock.</summary>
    void ForceMode(SstvModeDefinition mode);

    Task StartReceivingAsync(CancellationToken ct = default);

    Task StopReceivingAsync();

    /// <summary>Encodes and transmits <paramref name="image"/> as <paramref name="mode"/>. Pauses
    /// capture/decode for the duration (restored afterward only if RX was already running) and keys
    /// PTT via the injected <c>IRadioSessionService</c> around playback.</summary>
    Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default);

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
    /// -- restart-only, safe to read once at construction (no polling needed, this decoder is a DI
    /// singleton with no live-reconfigure path).</summary>
    bool AutoSlantEnabled { get; }

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SenseLevel"/>
    /// -- restart-only, same reasoning as <see cref="AutoSlantEnabled"/> above.</summary>
    int SenseLevel { get; }

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.RxBpfPreset"/>
    /// -- restart-only, same reasoning as <see cref="AutoSlantEnabled"/> above.</summary>
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
