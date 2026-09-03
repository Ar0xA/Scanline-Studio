using System.Globalization;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application;

/// <summary>fsk_cwid.md §5 A2 (B-P5 added the CW-ID side) -- correlates
/// <see cref="ISstvSessionService.StationIdDecoded"/> (FSK) AND <see cref="ISstvSessionService.CwIdDecoded"/>
/// (CW) with <see cref="IReceiveHistoryStore.Recorded"/> by <see cref="FskStationIdDecodedInfo.ReceptionSequence"/>/
/// <see cref="Abstractions.Cw.CwIdDecodedInfo.ReceptionSequence"/>/<see cref="ReceiveHistoryEntry.ReceptionId"/>
/// (all the same reception-identity space), then attaches the decoded callsign/NR-RST/CW-ID to the
/// entry via <see cref="IReceiveHistoryStore.SetDecodedStationIdAsync"/>. Same identity-keyed-join
/// shape as <see cref="RxAudioAutoSaver"/> (see that class's own doc comment for why this lives in
/// <c>ScanlineStudio.Application</c>, not <c>Core.Logbook</c>, and why <see cref="IReceivedImageBuffer.Saved"/>
/// is never a viable substitute for <see cref="IReceiveHistoryStore.Recorded"/> here either) --
/// **NOT** a strict one-shot pair, though: a single reception's FSK-ID transmission can raise
/// <see cref="ISstvSessionService.StationIdDecoded"/> UP TO TWICE (a callsign packet, then an optional
/// chained NR/RST sub-packet, `FskStationIdEncoder.Generate`'s own doc comment), and CW-ID is a
/// separate, independently-timed third source -- so this tracks a per-reception ACCUMULATOR
/// (whichever of callsign/NR-RST/CW-ID has arrived so far, plus the entry once
/// <see cref="IReceiveHistoryStore.Recorded"/> fires), writing the full MERGED set on every update
/// once the entry is known, rather than a single-shot completion the way the audio join is.</summary>
public sealed partial class RxStationIdAttacher : IRxStationIdAttacher
{
    /// <summary>Same numeric cap and "count-only, no shared trigger" reasoning as
    /// <see cref="RxAudioAutoSaver"/>'s own <c>MaxParkedEntries</c>.</summary>
    private const int MaxParkedEntries = 8;

    private readonly ISstvSessionService _sessionService;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ILogger<RxStationIdAttacher> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<long, PendingReception> _pending = [];
    private readonly List<long> _parkedOrder = [];

    /// <summary>Fires once a callsign/NR-RST/CW-ID write actually lands on the entry -- same "plain
    /// UPDATE, not a Recorded re-raise, live panes patch their own in-memory copy directly" contract
    /// as <see cref="RxAudioAutoSaver.AudioAttached"/>, including the SAME arbitrary-background-thread
    /// concurrency contract that event's own doc comment states (raised from inside
    /// <see cref="WriteLoopAsync"/>'s own <see cref="Task.Run(Func{Task})"/>) -- a subscriber must
    /// marshal to its own thread (e.g. <c>Dispatcher.UIThread.Post</c>) before touching UI-bound
    /// state. A-P3b auditor code-review finding: the coalescing-writer fix (see
    /// <see cref="ApplyUpdate"/>'s own doc comment) means a slow subscriber can now delay the NEXT
    /// write for the SAME reception (the write loop's own next iteration doesn't start until this
    /// raise returns) -- keep any subscriber handler here fast/non-blocking. May fire MORE THAN ONCE
    /// for the same entry id (once per merged update, e.g. callsign then NR/RST arriving separately,
    /// or a later CW-ID window) -- always carries the FULL current values, never a partial delta, so
    /// a subscriber can always just overwrite its own copy wholesale. Wrapped in an internal
    /// try/catch.</summary>
    public event Action<StationIdAttachment>? StationIdAttached;

    public RxStationIdAttacher(ISstvSessionService sessionService, IReceiveHistoryStore historyStore, ILogger<RxStationIdAttacher> logger)
    {
        _sessionService = sessionService;
        _historyStore = historyStore;
        _logger = logger;

        sessionService.StationIdDecoded += OnStationIdDecoded;
        sessionService.CwIdDecoded += OnCwIdDecoded;
        historyStore.Recorded += OnRecorded;
    }

    /// <summary><see cref="ISstvSessionService.StationIdDecoded"/>'s own contract: raised
    /// synchronously on the decode thread. The self-filter below needs an async operator-callsign
    /// read (same uncached-settings-read reasoning <c>RxImagePaneViewModel.ApplyStationIdDecodedAsync</c>'s
    /// own doc comment already gives), so this dispatches immediately rather than blocking the decode
    /// thread on it.</summary>
    private void OnStationIdDecoded(FskStationIdDecodedInfo info)
    {
        if (info.ReceptionSequence == 0)
        {
            // Unstamped -- ISstvDecoder.ReceptionSequence's own "0 = unset" contract; nothing to join.
            return;
        }

        _ = Task.Run(() => ProcessStationIdDecodedAsync(info));
    }

    private async Task ProcessStationIdDecodedAsync(FskStationIdDecodedInfo info)
    {
        try
        {
            string? callsignUpdate = null;
            string? nrRstUpdate = null;

            if (info.Callsign is { } decodedCallsign)
            {
                string? ownCallsign;
                try
                {
                    ownCallsign = await _sessionService.GetOperatorCallsignAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Best-effort, same reasoning as the VM's own GetOperatorCallsignAsync failure
                    // path -- a settings read failure must not crash this correlator; the callsign
                    // simply doesn't attach this time.
                    Log.GetOperatorCallsignFailed(_logger, info.ReceptionSequence, ex);
                    return;
                }

                // fsk_cwid.md §A5: same shared self-filter as RxImagePaneViewModel.ApplyStationIdDecodedAsync
                // -- StationIdCallsignNormalizer.IsOwnCallsign's own doc comment for why this must not
                // reimplement the compare inline.
                if (StationIdCallsignNormalizer.IsOwnCallsign(decodedCallsign, ownCallsign))
                {
                    return;
                }

                callsignUpdate = decodedCallsign;
            }
            else if (info.CompactNr is { } compactNr)
            {
                // Same "595" RST-default prefix and D3 zero-pad as ApplyDecodedNrRst/Main.cpp:3648 --
                // the persisted value must match what the live pane shows for the same event.
                nrRstUpdate = $"595{compactNr.ToString("D3", CultureInfo.InvariantCulture)}";
            }
            else if (info.NrText is { } nrText)
            {
                nrRstUpdate = $"595{nrText}";
            }
            else
            {
                // Defensive -- FskStationIdDecodedInfo's own contract is exactly one field set per
                // event; nothing to attach if somehow none are.
                return;
            }

            ApplyUpdate(info.ReceptionSequence, callsignUpdate, callsignSource: callsignUpdate is not null ? StationIdSources.Fsk : null, nrRstUpdate, cwId: null, entry: null);
        }
        catch (Exception ex)
        {
            Log.StationIdAttachFailed(_logger, info.ReceptionSequence, ex);
        }
    }

    /// <summary>fsk_cwid.md B-P5: same trivial-dispatch shape as <see cref="OnStationIdDecoded"/> --
    /// <see cref="ISstvSessionService.CwIdDecoded"/>'s own doc comment gives the same "raised from a
    /// background continuation, never assume a UI-thread caller" contract. The extra hop here is
    /// load-bearing, not merely for symmetry: <c>SstvSessionService.CwId.cs</c>'s own
    /// <c>RaiseCwIdDecoded</c> invokes every subscriber SYNCHRONOUSLY in one multicast loop, so
    /// without this <see cref="Task.Run(Func{Task})"/>, this class's own
    /// <see cref="ISstvSessionService.GetOperatorCallsignAsync"/> settings read below would run inline
    /// on that raise and delay every OTHER subscriber (in particular
    /// <c>RxImagePaneViewModel.OnCwIdDecoded</c>) behind it.</summary>
    private void OnCwIdDecoded(CwIdDecodedInfo info)
    {
        if (info.ReceptionSequence == 0)
        {
            // Unstamped -- same "0 = unset" contract as OnStationIdDecoded's own guard.
            return;
        }

        _ = Task.Run(() => ProcessCwIdDecodedAsync(info));
    }

    /// <summary>fsk_cwid.md B-P5: <see cref="CwIdDecodedInfo.Text"/> (the raw decoded CW-ID) is
    /// ALWAYS attached, unconditionally -- independent of whether a callsign-shaped token was found
    /// in it, mirroring <c>RxImagePaneViewModel.ApplyCwIdDecodedAsync</c>'s own "CW ID row filled
    /// unconditionally" rule. <see cref="CwIdDecodedInfo.Callsign"/>, when present, goes through the
    /// SAME self-filter <see cref="ProcessStationIdDecodedAsync"/> applies for FSK -- but unlike that
    /// method, a self-filter failure/match here must NOT drop the whole update: <paramref name="info"/>'s
    /// own <c>Text</c> still needs to reach <see cref="ApplyUpdate"/> either way, so this falls
    /// through to a null callsign candidate rather than returning early (auditor plan-review finding:
    /// an earlier draft copied <see cref="ProcessStationIdDecodedAsync"/>'s own early-`return` shape
    /// here too, which would have silently dropped <c>cwId</c> on every settings-read failure or
    /// self-match).</summary>
    private async Task ProcessCwIdDecodedAsync(CwIdDecodedInfo info)
    {
        try
        {
            string? callsignCandidate = null;

            if (info.Callsign is { } decodedCallsign)
            {
                string? ownCallsign = null;
                var gotOwnCallsign = false;
                try
                {
                    ownCallsign = await _sessionService.GetOperatorCallsignAsync().ConfigureAwait(false);
                    gotOwnCallsign = true;
                }
                catch (Exception ex)
                {
                    // Best-effort, same reasoning as ProcessStationIdDecodedAsync's own catch -- a
                    // settings read failure must not crash this correlator, and (unlike that FSK
                    // path) must not drop info.Text either; callsignCandidate simply stays null
                    // (gotOwnCallsign stays false below, so the self-filter is skipped rather than
                    // treated as "no own callsign configured, so nothing to filter").
                    Log.GetOperatorCallsignFailed(_logger, info.ReceptionSequence, ex);
                }

                // gotOwnCallsign distinguishes "read succeeded, no operator callsign configured"
                // (ownCallsign is null, IsOwnCallsign(decodedCallsign, null) correctly returns false,
                // same as FSK's own reachable case) from "the read itself failed" -- collapsing both
                // into a single null check would attach a callsign the self-filter never actually got
                // to evaluate.
                if (gotOwnCallsign && !StationIdCallsignNormalizer.IsOwnCallsign(decodedCallsign, ownCallsign))
                {
                    callsignCandidate = decodedCallsign;
                }
            }

            ApplyUpdate(info.ReceptionSequence, callsignCandidate, callsignSource: callsignCandidate is not null ? StationIdSources.Cw : null, nrRst: null, cwId: info.Text, entry: null);
        }
        catch (Exception ex)
        {
            Log.StationIdAttachFailed(_logger, info.ReceptionSequence, ex);
        }
    }

    private void OnRecorded(ReceiveHistoryEntry entry)
    {
        // ReceptionId == 0 means no reception identity was ever assigned for this row (e.g. a disk-
        // reconciled backfill) -- same skip as RxAudioAutoSaver.OnRecorded.
        if (entry.ReceptionId == 0)
        {
            return;
        }

        ApplyUpdate(entry.ReceptionId, callsign: null, callsignSource: null, nrRst: null, cwId: null, entry);
    }

    /// <summary>Merges whichever of <paramref name="callsign"/>/<paramref name="nrRst"/>/<paramref name="cwId"/>/
    /// <paramref name="entry"/> this call carries into the per-reception accumulator, writing the full
    /// merged set to the DB (outside the lock) once the entry is known and at least one of
    /// callsign/NR-RST/CW-ID has arrived. Called from <see cref="ProcessStationIdDecodedAsync"/> (FSK,
    /// <paramref name="callsignSource"/> always <c>"FSK"</c> when <paramref name="callsign"/> is set),
    /// <see cref="ProcessCwIdDecodedAsync"/> (CW, <paramref name="callsignSource"/> always
    /// <c>"CW"</c> when set; <paramref name="cwId"/> independent of it), and <see cref="OnRecorded"/>
    /// (<paramref name="entry"/> only, every other parameter <see langword="null"/>).
    ///
    /// The callsign merge mirrors <c>RxImagePaneViewModel</c>'s already-shipped priority rule EXACTLY
    /// for the one dimension that matters to a DB write -- FSK always wins, CW only fills an empty
    /// slot -- but is NOT identical to the pane in every respect; four real divergences, all
    /// deliberate:
    /// (1) the pane's own gate is <c>OverrideCallsign</c>, a user-EDITABLE field, so a manually-typed
    /// callsign blocks a later CW auto-fill there but has no DB-side equivalent to block (this
    /// accumulator has no concept of "manually edited");
    /// (2) the pane drops a CW result that arrives after a NEWER reception has already started
    /// (`ApplyCwIdDecodedAsync`'s own stale-reception guard); this join instead keys purely by
    /// reception id via <see cref="_pending"/>/<see cref="TrackParkedLocked"/>, so a late CW-ID still
    /// correctly reaches its OWN (older) entry -- the DB is arguably MORE correct here, not
    /// out of sync;
    /// (3) <see cref="MaxParkedEntries"/> eviction can drop a CW-ID more often than an FSK one, since
    /// CW-ID is structurally the LATEST-arriving signal for a reception (capture-window close plus
    /// background decode, well after both the image and any FSK-ID) -- under a bulk-WAV-decode
    /// interleaving with many receptions in flight, an evicted <see cref="PendingReception"/> silently
    /// drops that reception's CW-ID text with no write attempted (not a corruption -- see this
    /// method's own "not enough to write yet" gate below);
    /// (4) the pane logs an FSK/CW callsign disagreement (`ApplyCwIdDecodedAsync`'s own
    /// <c>Log.CwIdCallsignDisagreesWithFsk</c>); this DB-side merge has no matching diagnostic --
    /// FSK's value simply wins silently, same as it already did before B-P5.</summary>
    private void ApplyUpdate(long receptionId, string? callsign, string? callsignSource, string? nrRst, string? cwId, ReceiveHistoryEntry? entry)
    {
        ReceiveHistoryEntry? entryToWrite;
        string? callsignToWrite;
        string? callsignSourceToWrite;
        string? nrRstToWrite;
        string? cwIdToWrite;

        lock (_gate)
        {
            if (!_pending.TryGetValue(receptionId, out var pending))
            {
                pending = new PendingReception();
                _pending[receptionId] = pending;
                TrackParkedLocked(receptionId);
            }

            if (callsign is not null)
            {
                // FSK (callsignSource == "FSK") always overwrites, regardless of what's already
                // pending -- mirrors ApplyStationIdDecodedAsync's own unconditional OverrideCallsign
                // write. CW only writes into an empty slot (pending.Callsign is null) -- mirrors
                // ApplyCwIdDecodedAsync's own "only if OverrideCallsign is still empty" guard. A
                // second CW result for the SAME reception (not reachable today -- OnCwIdModeDetected
                // drops/re-arms per reception, at most one CwIdDecoded per ReceptionSequence) is
                // still allowed to update its own prior CW value, matching the pane's equivalent
                // "not blocked by a previous CW value from the SAME source" behavior.
                if (callsignSource == StationIdSources.Fsk || pending.Callsign is null)
                {
                    pending.Callsign = callsign;
                    pending.CallsignSource = callsignSource;
                }
            }

            if (nrRst is not null)
            {
                pending.NrRst = nrRst;
            }

            if (cwId is not null)
            {
                // Always wins, independent of the callsign priority above -- the raw CW-ID text has
                // no competing source (FSK never produces it), so there is nothing to arbitrate.
                pending.CwId = cwId;
            }

            if (entry is not null)
            {
                pending.Entry = entry;
            }

            entryToWrite = pending.Entry;
            callsignToWrite = pending.Callsign;
            callsignSourceToWrite = pending.CallsignSource;
            nrRstToWrite = pending.NrRst;
            cwIdToWrite = pending.CwId;
        }

        if (entryToWrite is null || (callsignToWrite is null && nrRstToWrite is null && cwIdToWrite is null))
        {
            // Not enough to write yet -- either the entry hasn't been recorded, or none of
            // callsign/NR-RST/CW-ID has decoded for this reception so far.
            return;
        }

        bool startWriteLoop;
        lock (_gate)
        {
            // A-P3a auditor code-review finding, deferred to A-P3b per that round's own
            // recommendation: an earlier version spawned one independent Task.Run PER ApplyUpdate
            // call, each capturing its own snapshot -- under a bulk-decode interleaving, the thread
            // pool could run a LATER call's write before an EARLIER call's write, so the DB (and the
            // StationIdAttached raise below, same ordering problem) could end up showing a STALE
            // snapshot last. Fixed with a per-reception coalescing writer: at most one write in
            // flight per receptionId at a time (WriteLoopInFlight, gated here), any update that
            // arrives while a write is already running just marks the pending record dirty
            // (WriteNeededAfterCurrent) instead of racing a second write -- the SAME loop picks up
            // the newest snapshot and writes again once its current write completes, so writes for
            // one reception are always strictly ordered and the DB always converges to the latest
            // merged state, never an out-of-order stale one.
            if (!_pending.TryGetValue(receptionId, out var pending))
            {
                // Evicted between the merge above and here (MaxParkedEntries) -- nothing left to
                // write for; same tolerance as a write that lands after the row itself is gone.
                return;
            }

            startWriteLoop = !pending.WriteLoopInFlight;
            if (startWriteLoop)
            {
                pending.WriteLoopInFlight = true;
            }
            else
            {
                pending.WriteNeededAfterCurrent = true;
            }
        }

        if (startWriteLoop)
        {
            _ = Task.Run(() => WriteLoopAsync(receptionId));
        }
    }

    /// <summary>The one and only writer for a given <paramref name="receptionId"/> at any moment --
    /// see <see cref="ApplyUpdate"/>'s own doc comment for why this exists. Always started via
    /// <see cref="Task.Run(Func{Task})"/> from <see cref="ApplyUpdate"/>, never re-entered directly
    /// (the "already in flight" case there sets <see cref="PendingReception.WriteNeededAfterCurrent"/>
    /// instead of starting a second loop).</summary>
    private async Task WriteLoopAsync(long receptionId)
    {
        while (true)
        {
            ReceiveHistoryEntry entryToWrite;
            string? callsignToWrite;
            string? callsignSourceToWrite;
            string? nrRstToWrite;
            string? cwIdToWrite;

            lock (_gate)
            {
                if (!_pending.TryGetValue(receptionId, out var pending))
                {
                    // Evicted while this loop was writing -- nothing left to track; no WriteLoopInFlight
                    // flag to clear either, the PendingReception itself is gone.
                    return;
                }

                pending.WriteNeededAfterCurrent = false;
                // entryToWrite is guaranteed non-null here: ApplyUpdate only ever starts this loop
                // (or marks WriteNeededAfterCurrent) after its own entryToWrite/callsignToWrite-or-
                // nrRstToWrite-or-cwIdToWrite null-check above already passed for this exact pending
                // record.
                entryToWrite = pending.Entry!;
                callsignToWrite = pending.Callsign;
                callsignSourceToWrite = pending.CallsignSource;
                nrRstToWrite = pending.NrRst;
                cwIdToWrite = pending.CwId;
            }

            try
            {
                if (await _historyStore.SetDecodedStationIdAsync(entryToWrite.Id, callsignToWrite, callsignSourceToWrite, nrRstToWrite, cwIdToWrite).ConfigureAwait(false))
                {
                    RaiseStationIdAttached(new StationIdAttachment(entryToWrite.Id, callsignToWrite, callsignSourceToWrite, nrRstToWrite, cwIdToWrite));
                }

                // A false return (row no longer exists, e.g. deleted between Recorded firing and now)
                // is not an error -- same tolerance as RxAudioAutoSaver's own SetAudioFilePathAsync
                // false-return handling.
            }
            catch (Exception ex)
            {
                Log.StationIdAttachFailed(_logger, receptionId, ex);
            }

            lock (_gate)
            {
                if (!_pending.TryGetValue(receptionId, out var pending) || !pending.WriteNeededAfterCurrent)
                {
                    if (pending is not null)
                    {
                        pending.WriteLoopInFlight = false;
                    }

                    return;
                }

                // A newer update arrived while the write above was in flight -- loop again and write
                // the latest snapshot; WriteLoopInFlight stays true the whole time, so ApplyUpdate
                // never starts a second concurrent loop for this receptionId.
            }
        }
    }

    /// <summary>Oldest-first eviction above <see cref="MaxParkedEntries"/>, newest always exempt --
    /// same shape as <see cref="RxAudioAutoSaver.TrackParkedLocked"/>. Must be called under
    /// <see cref="_gate"/>, immediately after adding a new pending entry.</summary>
    private void TrackParkedLocked(long receptionId)
    {
        _parkedOrder.Add(receptionId);
        while (_parkedOrder.Count > MaxParkedEntries)
        {
            var oldest = _parkedOrder[0];
            _parkedOrder.RemoveAt(0);
            _pending.Remove(oldest);
        }
    }

    private void RaiseStationIdAttached(StationIdAttachment attachment)
    {
        try
        {
            StationIdAttached?.Invoke(attachment);
        }
        catch (Exception ex)
        {
            Log.StationIdAttachedSubscriberFailed(_logger, attachment.EntryId, ex);
        }
    }

    private sealed class PendingReception
    {
        public string? Callsign;

        /// <summary>Which source last won <see cref="Callsign"/> -- <see cref="StationIdSources.Fsk"/>/
        /// <see cref="StationIdSources.Cw"/>/<see langword="null"/> (nothing decoded yet). Guarded by
        /// <see cref="_gate"/>, set only inside <see cref="ApplyUpdate"/>'s own merge logic -- see
        /// that method's own doc comment for the priority rule this implements.</summary>
        public string? CallsignSource;

        public string? NrRst;

        /// <summary>The CW-ID decoder's raw text for this reception -- independent of
        /// <see cref="Callsign"/>/<see cref="CallsignSource"/>, always overwritten unconditionally
        /// when a new value arrives (see <see cref="ApplyUpdate"/>'s own merge logic).</summary>
        public string? CwId;

        public ReceiveHistoryEntry? Entry;

        /// <summary>Guarded by <see cref="_gate"/> -- see <see cref="ApplyUpdate"/>'s own doc
        /// comment for the coalescing-writer design this and <see cref="WriteNeededAfterCurrent"/>
        /// implement.</summary>
        public bool WriteLoopInFlight;

        public bool WriteNeededAfterCurrent;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read the operator callsign while attaching a decoded station ID for reception {ReceptionId}")]
        public static partial void GetOperatorCallsignFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to attach decoded station ID for reception {ReceptionId}")]
        public static partial void StationIdAttachFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A StationIdAttached subscriber threw for entry {EntryId}")]
        public static partial void StationIdAttachedSubscriberFailed(ILogger logger, string entryId, Exception ex);
    }
}
