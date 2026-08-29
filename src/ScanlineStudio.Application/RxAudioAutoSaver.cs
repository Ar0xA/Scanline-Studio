using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application;

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio) -- correlates
/// <see cref="ISstvSessionService.AudioSliceReady"/> with <see cref="IReceiveHistoryStore.Recorded"/>
/// by <see cref="ReceiveHistoryEntry.ReceptionId"/>, then attaches the resulting WAV path to the
/// entry. See <c>docs/plans/step12-auto-save-rx-audio-plan.md</c>'s "Correlation" section, "The
/// join" subsection, for the full design this implements.
///
/// Deliberately in <c>ScanlineStudio.Application</c>, not <c>ScanlineStudio.Core.Logbook</c> --
/// this needs <see cref="ISstvSessionService"/>, which <c>Core.Logbook</c> cannot reference
/// (<c>ScanlineStudio.Application.csproj</c> references <c>Core.Logbook</c>, not the reverse).
///
/// A one-shot, identity-keyed join, NOT the round-3 rendezvous-FIFO an earlier design used (see the
/// plan doc's Correlation section for the full history of why that was rejected): whichever of
/// <see cref="ISstvSessionService.AudioSliceReady"/>/<see cref="IReceiveHistoryStore.Recorded"/>
/// arrives first for a given reception parks under that reception's id; the second one completes the
/// pair. <see cref="IReceivedImageBuffer.Saved"/> is deliberately NEVER subscribed -- a manual "Save
/// frame as…" raises that event too but never raises <see cref="IReceiveHistoryStore.Recorded"/>, so
/// it structurally cannot enter this correlator at all, closing (not just accepting) the round-3
/// design's own accepted limitation. <see cref="ISstvSessionService.AudioCaptureReset"/> is also
/// deliberately NOT subscribed -- distinct reception ids already make cross-attach impossible
/// regardless of that event's timing; see its own doc comment for why a seam-triggered wipe of this
/// join would have destroyed genuinely valid pending pairs in the most common QSO workflow.</summary>
public sealed partial class RxAudioAutoSaver : IRxAudioAutoSaver
{
    /// <summary>Same numeric cap as <c>SstvSessionService</c>'s own scratch-file retention (see the
    /// plan doc's "The join" section for why the two are independent structures with no shared
    /// trigger) -- but COUNT-only here, never byte-aware: a parked entry on this side is metadata
    /// (a reception id and, once the `Recorded` half arrives, an entry reference), not audio bytes.</summary>
    private const int MaxParkedEntries = 8;

    private readonly ISstvSessionService _sessionService;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ILogger<RxAudioAutoSaver> _logger;

    private readonly object _gate = new();
    private readonly HashSet<long> _pendingSliceReceptionIds = [];
    private readonly Dictionary<long, ReceiveHistoryEntry> _pendingEntries = [];
    // Arrival order across BOTH structures above, combined -- a single reception can only ever be
    // parked in ONE of them at a time (the moment its pair arrives, both remove it), so one ordered
    // list is sufficient to implement "oldest-first, newest exempt" across the whole join.
    private readonly List<long> _parkedOrder = [];

    /// <summary>Fires once a pairing completes and the WAV is actually attached to the entry --
    /// <see cref="IReceiveHistoryStore.SetAudioFilePathAsync"/> is a plain <c>UPDATE</c>, deliberately
    /// not a <see cref="IReceiveHistoryStore.Recorded"/> re-raise (see that method's own doc comment
    /// for why), so this is the only hook a live UI pane has to patch its already-held in-memory
    /// entry directly. Raised on whatever background thread the pairing completed on -- a subscriber
    /// must marshal to the UI thread itself, same "subscriber's own responsibility" contract as
    /// every other cross-thread event in this codebase. Wrapped in an internal try/catch.</summary>
    public event Action<string, string>? AudioAttached;

    public RxAudioAutoSaver(ISstvSessionService sessionService, IReceiveHistoryStore historyStore, ILogger<RxAudioAutoSaver> logger)
    {
        _sessionService = sessionService;
        _historyStore = historyStore;
        _logger = logger;

        sessionService.AudioSliceReady += OnAudioSliceReady;
        historyStore.Recorded += OnRecorded;
    }

    private void OnAudioSliceReady(long receptionId, int sampleRate)
    {
        ReceiveHistoryEntry? matchedEntry;
        lock (_gate)
        {
            if (_pendingEntries.Remove(receptionId, out matchedEntry))
            {
                _parkedOrder.Remove(receptionId);
            }
            else
            {
                // Auditor-caught (round 1 code-review): only track a NEW park in _parkedOrder --
                // .Add returning false means this id was already parked (unreachable today, since
                // ISstvDecoder.ReceptionSequence never repeats and CloseArmedSliceLocked zeroes the
                // armed id before any slice could raise AudioSliceReady twice for it, but cheap to
                // guard structurally rather than rely on that staying true). Without this check, a
                // duplicate would desync _parkedOrder (two entries) from the HashSet (one entry),
                // letting eviction remove the wrong id later.
                if (_pendingSliceReceptionIds.Add(receptionId))
                {
                    TrackParkedLocked(receptionId);
                }

                return;
            }
        }

        CompletePair(receptionId, matchedEntry);
    }

    private void OnRecorded(ReceiveHistoryEntry entry)
    {
        // ReceptionId == 0 means no audio identity was ever assigned for this row (e.g. a disk-
        // reconciled backfill, ISstvDecoder.ReceptionSequence's own contract reserves 0 for exactly
        // this) -- skip it entirely, never park it, never error.
        if (entry.ReceptionId == 0)
        {
            return;
        }

        bool hadSlice;
        lock (_gate)
        {
            hadSlice = _pendingSliceReceptionIds.Remove(entry.ReceptionId);
            if (hadSlice)
            {
                _parkedOrder.Remove(entry.ReceptionId);
            }
            else
            {
                // Same structural duplicate-park guard as OnAudioSliceReady above.
                if (!_pendingEntries.ContainsKey(entry.ReceptionId))
                {
                    _pendingEntries[entry.ReceptionId] = entry;
                    TrackParkedLocked(entry.ReceptionId);
                }

                return;
            }
        }

        CompletePair(entry.ReceptionId, entry);
    }

    /// <summary>Oldest-first eviction above <see cref="MaxParkedEntries"/>, newest always exempt --
    /// removing only from the FRONT of an arrival-ordered list can never touch the most recent
    /// arrival. Must be called under <see cref="_gate"/>, immediately after adding the new entry.</summary>
    private void TrackParkedLocked(long receptionId)
    {
        _parkedOrder.Add(receptionId);
        while (_parkedOrder.Count > MaxParkedEntries)
        {
            var oldest = _parkedOrder[0];
            _parkedOrder.RemoveAt(0);
            _pendingSliceReceptionIds.Remove(oldest);
            _pendingEntries.Remove(oldest);
        }
    }

    /// <summary>Completes a pairing in the background: derives the final WAV path from the entry's
    /// own id (mirroring, not literally reusing, <c>ReceiveHistoryRecorder</c>'s image-file naming --
    /// deliberately simpler here, chosen for guaranteed uniqueness via <paramref name="entry"/>'s own
    /// <see cref="ReceiveHistoryEntry.Id"/> rather than a timestamp+mode scheme), moves the scratch
    /// file to it, and attaches it to the row. Isolated -- a failure here must never surface anywhere
    /// else; the image and its history row are already safely recorded regardless of whether this
    /// succeeds.</summary>
    private void CompletePair(long receptionId, ReceiveHistoryEntry entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Read fresh, not cached -- this runs once per completed reception, not on any hot
                // path, so a live read costs nothing and needs no separate propagation mechanism the
                // way SstvSessionService's own decode-path copy does (see
                // ISstvSessionService.SetAudioDirectory's own doc comment).
                var audioSettings = await _historyStore.GetAudioSettingsAsync().ConfigureAwait(false);
                var wavPath = Path.Combine(audioSettings.Directory, $"{entry.Id}.wav");

                if (!await _sessionService.TrySaveReceptionAudioAsync(receptionId, wavPath).ConfigureAwait(false))
                {
                    // Already evicted (the scratch-file retention cap) or already consumed -- no
                    // audio to attach; leave the entry's AudioFilePath null, not an error.
                    return;
                }

                if (!await _historyStore.SetAudioFilePathAsync(entry.Id, wavPath).ConfigureAwait(false))
                {
                    // The row no longer exists (e.g. deleted between Recorded firing and now) -- the
                    // WAV is already safely moved to its final path regardless, just orphaned
                    // (harmless: no row points at it, same class of state DeleteAsync's own doc
                    // comment already tolerates for a manually-deleted-on-disk file).
                    return;
                }

                RaiseAudioAttached(entry.Id, wavPath);
            }
            catch (Exception ex)
            {
                Log.AudioAttachFailed(_logger, entry.Id, receptionId, ex);
            }
        });
    }

    private void RaiseAudioAttached(string entryId, string path)
    {
        try
        {
            AudioAttached?.Invoke(entryId, path);
        }
        catch (Exception ex)
        {
            Log.AudioAttachedSubscriberFailed(_logger, entryId, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to attach auto-saved audio to entry {EntryId} (reception {ReceptionId})")]
        public static partial void AudioAttachFailed(ILogger logger, string entryId, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "An AudioAttached subscriber threw for entry {EntryId}")]
        public static partial void AudioAttachedSubscriberFailed(ILogger logger, string entryId, Exception ex);
    }
}
