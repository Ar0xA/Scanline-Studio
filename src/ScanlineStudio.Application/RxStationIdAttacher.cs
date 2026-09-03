using System.Globalization;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application;

/// <summary>fsk_cwid.md §5 A2 -- correlates <see cref="ISstvSessionService.StationIdDecoded"/> with
/// <see cref="IReceiveHistoryStore.Recorded"/> by <see cref="FskStationIdDecodedInfo.ReceptionSequence"/>/
/// <see cref="ReceiveHistoryEntry.ReceptionId"/>, then attaches the decoded callsign/NR-RST to the
/// entry via <see cref="IReceiveHistoryStore.SetDecodedStationIdAsync"/>. Same identity-keyed-join
/// shape as <see cref="RxAudioAutoSaver"/> (see that class's own doc comment for why this lives in
/// <c>ScanlineStudio.Application</c>, not <c>Core.Logbook</c>, and why <see cref="IReceivedImageBuffer.Saved"/>
/// is never a viable substitute for <see cref="IReceiveHistoryStore.Recorded"/> here either) --
/// **NOT** a strict one-shot pair, though: a single reception's FSK-ID transmission can raise
/// <see cref="ISstvSessionService.StationIdDecoded"/> UP TO TWICE (a callsign packet, then an optional
/// chained NR/RST sub-packet, `FskStationIdEncoder.Generate`'s own doc comment) -- so this tracks a
/// per-reception ACCUMULATOR (whichever of callsign/NR-RST has arrived so far, plus the entry once
/// <see cref="IReceiveHistoryStore.Recorded"/> fires), writing the full MERGED pair on every update
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

    /// <summary>Fires once a callsign/NR-RST write actually lands on the entry -- same "plain UPDATE,
    /// not a Recorded re-raise, live panes patch their own in-memory copy directly" contract as
    /// <see cref="RxAudioAutoSaver.AudioAttached"/>, including the SAME arbitrary-background-thread
    /// concurrency contract that event's own doc comment states (raised from inside
    /// <see cref="WriteLoopAsync"/>'s own <see cref="Task.Run(Func{Task})"/>) -- a subscriber must
    /// marshal to its own thread (e.g. <c>Dispatcher.UIThread.Post</c>) before touching UI-bound
    /// state. A-P3b auditor code-review finding: the coalescing-writer fix (see
    /// <see cref="ApplyUpdate"/>'s own doc comment) means a slow subscriber can now delay the NEXT
    /// write for the SAME reception (the write loop's own next iteration doesn't start until this
    /// raise returns) -- keep any subscriber handler here fast/non-blocking. May fire MORE THAN ONCE
    /// for the same <paramref name="entryId"/> (once per merged update, e.g. callsign then NR/RST
    /// arriving separately) -- always carries the FULL current values, never a partial delta, so a
    /// subscriber can always just overwrite its own copy wholesale. Wrapped in an internal
    /// try/catch.</summary>
    public event Action<string, string?, string?>? StationIdAttached;

    public RxStationIdAttacher(ISstvSessionService sessionService, IReceiveHistoryStore historyStore, ILogger<RxStationIdAttacher> logger)
    {
        _sessionService = sessionService;
        _historyStore = historyStore;
        _logger = logger;

        sessionService.StationIdDecoded += OnStationIdDecoded;
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

            ApplyUpdate(info.ReceptionSequence, callsignUpdate, nrRstUpdate, entry: null);
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

        ApplyUpdate(entry.ReceptionId, callsign: null, nrRst: null, entry);
    }

    /// <summary>Merges whichever of <paramref name="callsign"/>/<paramref name="nrRst"/>/<paramref name="entry"/>
    /// this call carries into the per-reception accumulator, writing the full merged pair to the DB
    /// (outside the lock) once the entry is known and at least one of callsign/NR-RST has arrived.
    /// Called from both <see cref="ProcessStationIdDecodedAsync"/> (entry left <see langword="null"/>)
    /// and <see cref="OnRecorded"/> (callsign/nrRst left <see langword="null"/>) -- exactly one of the
    /// three parameters is non-null per call.</summary>
    private void ApplyUpdate(long receptionId, string? callsign, string? nrRst, ReceiveHistoryEntry? entry)
    {
        ReceiveHistoryEntry? entryToWrite;
        string? callsignToWrite;
        string? nrRstToWrite;

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
                pending.Callsign = callsign;
            }

            if (nrRst is not null)
            {
                pending.NrRst = nrRst;
            }

            if (entry is not null)
            {
                pending.Entry = entry;
            }

            entryToWrite = pending.Entry;
            callsignToWrite = pending.Callsign;
            nrRstToWrite = pending.NrRst;
        }

        if (entryToWrite is null || (callsignToWrite is null && nrRstToWrite is null))
        {
            // Not enough to write yet -- either the entry hasn't been recorded, or neither a
            // callsign nor an NR/RST has decoded for this reception so far.
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
            string? nrRstToWrite;

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
                // nrRstToWrite null-check above already passed for this exact pending record.
                entryToWrite = pending.Entry!;
                callsignToWrite = pending.Callsign;
                nrRstToWrite = pending.NrRst;
            }

            try
            {
                if (await _historyStore.SetDecodedStationIdAsync(entryToWrite.Id, callsignToWrite, nrRstToWrite).ConfigureAwait(false))
                {
                    RaiseStationIdAttached(entryToWrite.Id, callsignToWrite, nrRstToWrite);
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

    private void RaiseStationIdAttached(string entryId, string? callsign, string? nrRst)
    {
        try
        {
            StationIdAttached?.Invoke(entryId, callsign, nrRst);
        }
        catch (Exception ex)
        {
            Log.StationIdAttachedSubscriberFailed(_logger, entryId, ex);
        }
    }

    private sealed class PendingReception
    {
        public string? Callsign;
        public string? NrRst;
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
