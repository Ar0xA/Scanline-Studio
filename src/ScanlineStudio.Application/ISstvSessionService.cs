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

    /// <summary>Whether capture is currently running -- reflects the same internal state
    /// <see cref="StartReceivingAsync"/>/<see cref="StopReceivingAsync"/> already track, so it's
    /// accurate for the header's Receiving/Halt toggle including the case where a startup
    /// auto-start silently failed (no audio device) or <see cref="TransmitAsync"/> is transiently
    /// pausing capture for the duration of a transmission.</summary>
    bool IsReceiving { get; }

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

    /// <summary>Current post-encode playback gain (0-100), read fresh from
    /// <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>.</summary>
    Task<int> GetTxVolumePercentAsync(CancellationToken ct = default);

    Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default);

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

    int? SyncOffsetSamples { get; }

    double SignalPeakLevel { get; }

    bool IsLevelOverdriven { get; }

    /// <summary>Pass-through of <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.AutoSlantEnabled"/>
    /// -- restart-only, safe to read once at construction (no polling needed, this decoder is a DI
    /// singleton with no live-reconfigure path).</summary>
    bool AutoSlantEnabled { get; }

    double? SyncFrequencyCorrectionHz { get; }

    int BufferedSampleCount { get; }

    /// <summary>Pass-through of the underlying <see cref="ScanlineStudio.Abstractions.Audio.IAudioEngine.CaptureOverrunCount"/>
    /// -- a DIFFERENT quantity from <see cref="BufferedSampleCount"/> above despite the similar
    /// name: this is the audio-engine's own dropped-frame overrun count (status bar's "· N XRUN"
    /// half), not the decoder's internal sample-history buffer. 0 whenever RX capture isn't running,
    /// same as the underlying engine property -- unlike that underlying property (see its own doc
    /// comment), THIS one genuinely upholds the class-level "safe to read from any thread" contract:
    /// the implementation absorbs the underlying engine's documented stop-capture race and reports
    /// <c>0</c> rather than letting it throw, matching this member's own "capture isn't running"
    /// contract value exactly (auditor round 1, batch-5 wiring).</summary>
    int CaptureOverrunCount { get; }

    /// <summary>Manual-keying diagnostic aid: holds PTT keyed independent of any
    /// <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call, until unlocked. Idempotent both
    /// ways. See the implementation's own doc comment for the exact failure-handling and
    /// concurrency contract -- in short: a failure here is NOT swallowed (unlike this service's
    /// internal best-effort cleanup paths), and <see cref="IsPttLocked"/> only changes after
    /// <c>IRadioSessionService.SetPttAsync</c> actually confirms the requested state.</summary>
    Task SetPttLockAsync(bool locked, CancellationToken ct = default);
}
