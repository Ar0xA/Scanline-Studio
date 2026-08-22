using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Auto-saves a received image to the RX history store on decode completion — see
/// spec/07-image-pipeline.md's "RX flow" section. Callsign is deliberately omitted from the
/// filename convention (legacy: timestamp + mode + optional callsign) — no QSO-entry UI exists yet
/// to supply one; <see cref="ReceiveHistoryEntry.LinkedQsoId"/> stays populatable but nothing sets
/// it in this pass.
///
/// <b>Completion detection — a real gap found during implementation, not in <see cref="ISstvDecoder"/>
/// itself:</b> there is no explicit "decode completed" event; only <see cref="ISstvDecoder.LineDecoded"/>
/// (fires per scanline *group* — one line for most families, two for paired-line families like
/// PD/MP/RM8/RM12 per <c>IScanlineDecoder.RowsPerTransmissionLine</c>, not exposed on the public
/// <see cref="SstvModeDefinition"/>). Rather than guessing the group size, this class *learns* it
/// from the first two <see cref="DecodedImageUpdate.Line"/> values observed for the current image
/// (their delta is exactly the group size, constant for the rest of that image) and only then
/// checks whether the group starting at <c>Line</c> is the image's last one — no completion check
/// happens at all before the step is learned (i.e. never on the very first event of an image). Every
/// real <see cref="SstvModeDefinition"/> has an <c>ImageHeight</c> far larger than one or two rows,
/// so an image that completes in a single event is not a real scenario worth guarding against
/// (deferring the check costs nothing in practice) — an earlier version of this class instead
/// defaulted the unlearned step to <c>ImageHeight</c> itself, which made the very first event of
/// *every* image look complete; caught by <c>ReceiveHistoryRecorderTests</c>, not by
/// review.</summary>
public sealed partial class ReceiveHistoryRecorder
{
    // Notify-only -- this class never calls SaveAsync or reads Current on it (see
    // RecordCompletedImageAsync's own doc comment for why: an async read of Current races
    // DecodeRestarted blanking it). Held solely to raise NotifySaved after a completed-image write,
    // the only hook a live UI pane (RxImagePaneViewModel) has for correlating its Note/Flag controls
    // and file-size readout to the just-recorded frame.
    private readonly IReceivedImageBuffer _receivedImage;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ReceiveHistoryRecorder> _logger;

    private SstvModeDefinition? _currentMode;
    private int? _previousLine;
    private int? _observedStep;
    private bool _recordedForCurrentImage;
    private PixelSnapshot? _lastImage;

    // Abandoned-image-save state (port of legacy's m_ReqSave, sstv.cpp:2134-2137 -- see
    // OnDecodeRestarted's own doc comment for the full design and the three rounds of review this
    // went through, two plan-level and two code-level). Holds whatever was current the instant
    // BEFORE the last OnModeDetected reset it -- needed for the minority ISstvDecoder event orderings
    // where ModeDetected fires before DecodeRestarted (see ISstvDecoder.cs's own DecodeRestarted doc
    // comment for the full ordering table).
    private SstvModeDefinition? _pendingAbandonMode;
    private PixelSnapshot? _pendingAbandonImage;
    private int? _pendingAbandonLine;
    private int? _pendingAbandonStep;

    // Second code-level review finding: whether the stashed image had ALREADY been handled (normal
    // completion, or an earlier DecodeRestarted already consumed it) at the moment it was stashed --
    // gating the STASH ITSELF on "not yet handled" (an earlier draft's approach) made the stash
    // inert in exactly the ordering it exists to cover, whenever the abandoned image also happened to
    // be already-completed or already-consumed (e.g. a completed AVT image immediately followed by a
    // same-instance AVT restart, ModeDetected(Avt) firing before DecodeRestarted(Avt) since
    // SstvModeRegistry.Avt is one shared singleton) -- the stash must always be populated whenever
    // there was a live mode, and this flag is what lets OnDecodeRestarted correctly decide "nothing
    // to save here" without discarding the stash slot's own mode identity.
    private bool _pendingAbandonRecorded;

    public ReceiveHistoryRecorder(ISstvDecoder decoder, IReceivedImageBuffer receivedImage, IReceiveHistoryStore historyStore, ISettingsStore settingsStore, ILogger<ReceiveHistoryRecorder> logger)
    {
        _receivedImage = receivedImage;
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _logger = logger;

        decoder.ModeDetected += OnModeDetected;
        decoder.LineDecoded += OnLineDecoded;
        decoder.DecodeRestarted += OnDecodeRestarted;
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        // Stash whatever's about to be overwritten, in case a DecodeRestarted for THIS
        // soon-to-be-abandoned image arrives AFTER this reset rather than before it -- see
        // OnDecodeRestarted's own doc comment for which ISstvDecoder event orderings need this.
        // Unconditional whenever there WAS a live mode -- second code-level review finding: gating
        // this on `!_recordedForCurrentImage` (an earlier draft's approach) meant a completed-then-
        // restarted-with-the-same-mode-instance image (AVT-into-AVT) left the stash empty, which sent
        // OnDecodeRestarted into the live-state branch below and poisoned _recordedForCurrentImage for
        // the BRAND NEW image instead. _pendingAbandonRecorded carries forward whether this image was
        // already handled, so OnDecodeRestarted can still correctly decide "nothing to save" without
        // needing to withhold the stash's mode identity to do it.
        if (_currentMode is not null)
        {
            _pendingAbandonMode = _currentMode;
            _pendingAbandonImage = _lastImage;
            _pendingAbandonLine = _previousLine;
            _pendingAbandonStep = _observedStep;
            _pendingAbandonRecorded = _recordedForCurrentImage;
        }
        else
        {
            ClearPendingAbandon();
        }

        _currentMode = mode;
        _previousLine = null;
        _observedStep = null;
        _recordedForCurrentImage = false;
        _lastImage = null;
    }

    // Port of legacy's m_ReqSave (sstv.cpp:2134-2137, consumed at Main.cpp:4931-4934's DrawSSTV/
    // WriteHistory): saves the abandoned image if it was >=65% through its own extent when
    // superseded. Legacy's threshold is sample-position-based (m_rBase/m_LM, both confirmed sample
    // counts, not line counts, by reading sstv.h/sstv.cpp directly); this class only has
    // line-granularity available at the event level it hooks into, so the threshold is reframed as
    // a line-count fraction -- equivalent to within one line's rounding, since SSTV lines are
    // fixed-duration (a coarse >=65% gate, not a golden-vector-tolerance computation).
    //
    // <b>Ordering-agnostic by construction</b> -- ISstvDecoder.cs's own DecodeRestarted doc comment
    // documents that ModeDetected for the new mode does NOT always fire before this event (true only
    // through this port's earlier "piece 6" implementation; corrected after piece 8c's deferred
    // anchor-correction pipeline changed the real ordering without that doc being updated -- a plan
    // built on the old claim was caught by auditor review before it shipped a real data-corruption
    // bug: reading a stash left over from an unrelated LATER restart). This method therefore checks
    // the STASH first (the minority orderings -- AVT resolving within the same call, or ForceMode
    // into AVT) and only falls back to LIVE state (the dominant ordering, where OnModeDetected for
    // the new mode hasn't run yet) when the stash's own mode identity doesn't match `abandonedMode`.
    //
    // <b>Mode-identity of the STASH SLOT ALONE is the discriminator</b> -- second code-level review
    // finding, correcting a first attempt at this same fix: also requiring the stash's image/line to
    // be non-null (or requiring it to have been populated only when not-yet-handled) reintroduces the
    // exact bug this design exists to prevent, for the reachable case where the ABANDONED image had
    // ALSO already completed or already been consumed before this restart arrived (e.g. a completed
    // AVT image immediately followed by a same-instance AVT restart -- SstvModeRegistry.Avt is one
    // shared singleton, so ModeDetected(Avt) can fire, with nothing left to stash, right before
    // DecodeRestarted(Avt) for that same completed image). Any extra condition on the mode-identity
    // check sends that case into the live-state branch below, which then poisons
    // _recordedForCurrentImage against the BRAND NEW image and permanently blocks it from ever being
    // recorded. `_pendingAbandonRecorded` (see its own field doc comment) is what lets "is there
    // something to save" stay a SEPARATE question from "which branch am I in" here.
    private void OnDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        PixelSnapshot? candidateImage = null;
        int? candidateLine = null;
        int? candidateStep = null;

        if (_pendingAbandonMode == abandonedMode)
        {
            // Minority ordering -- deliberately does NOT touch _recordedForCurrentImage: live state
            // already describes the NEW image at this point, and marking it handled here would
            // prevent it ever being recorded once it completes.
            if (!_pendingAbandonRecorded && _pendingAbandonImage is not null && _pendingAbandonLine is not null)
            {
                candidateImage = _pendingAbandonImage;
                candidateLine = _pendingAbandonLine;
                candidateStep = _pendingAbandonStep;
            }
        }
        else if (_currentMode == abandonedMode)
        {
            // Dominant ordering. "Mark this mode's image as handled" and "is there actually
            // something to save" are deliberately two SEPARATE conditions here (an earlier draft
            // conflated them, gating the flag on image presence -- caught by auditor review: that
            // let a null-image entry reach the fallback stash below and NRE downstream). The flag
            // must be set purely on mode identity, matching this class's own pre-existing behavior
            // for this exact case.
            //
            // `alreadyHandled` is a THIRD, distinct condition: AnalogFmSstvDecoder.cs's mid-reception
            // restart check sits at the bottom of the per-line loop with no guard against having just
            // decoded the FINAL line, so DecodeRestarted can fire for an image that already completed
            // and was already queued for a normal save -- without this check, that image would be
            // re-saved a second time as `_partial`, writing a duplicate PNG and a duplicate history
            // entry. Legacy doesn't either: its own m_ReqSave check is gated on `m_Sync`
            // (sstv.cpp:2134), already cleared by Stop() for a finished image.
            var alreadyHandled = _recordedForCurrentImage;
            _recordedForCurrentImage = true;

            if (!alreadyHandled && _lastImage is not null && _previousLine is not null)
            {
                candidateImage = _lastImage;
                candidateLine = _previousLine;
                candidateStep = _observedStep;
            }
        }

        // Cleared unconditionally, consumed either way -- also a defensive guard against a second
        // DecodeRestarted arriving before the next ModeDetected (confirmed unreachable today by
        // tracing every AnalogFmSstvDecoder.cs call site, including PerformForceMode's own
        // hadPendingAnchor gate blocking a double-ForceMode-click version of this, but cheap to
        // guard anyway rather than rely on that staying true).
        ClearPendingAbandon();

        if (candidateImage is not { } image || candidateLine is not { } line)
        {
            return;
        }

        // observedStep ?? 1: a restart with only one line observed so far (no learned step yet)
        // should count that one line, not zero.
        var completedLines = line + (candidateStep ?? 1);
        var threshold = abandonedMode.ImageHeight * 65 / 100; // integer math, matches legacy's own truncating `* 65/100` idiom
        if (completedLines < threshold)
        {
            return;
        }

        // Hoisted into locals BEFORE the closure -- the closure must never read _pendingAbandonImage/
        // _lastImage directly, since both are already cleared (or reassigned to a different image)
        // by the time the task actually runs. Sound only because `image` is already a snapshot copy
        // (Snapshot(), below), not a live decoder-owned alias -- see that method's own doc comment.
        var modeId = abandonedMode.Id;
        var snapshot = image;

        // Fire-and-forget, isolated -- same reasoning as OnLineDecoded's own completed-image save.
        _ = Task.Run(async () =>
        {
            try
            {
                await RecordAbandonedImageAsync(modeId, snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.RecordAbandonedImageFailed(_logger, modeId, ex);
            }
        });
    }

    private void OnLineDecoded(DecodedImageUpdate update)
    {
        // Defensive: a decoded line for the CURRENT image proves any minority-ordering
        // DecodeRestarted for whatever was stashed before it has already arrived and been consumed
        // (both real ISstvDecoder call sites fire DecodeRestarted immediately after ModeDetected with
        // no decoding possible in between) -- clearing here stops a stash from ever surviving into a
        // LATER, unrelated restart that happens to share the same mode identity, rather than relying
        // solely on OnDecodeRestarted's own unconditional clear to have already run.
        ClearPendingAbandon();

        if (_recordedForCurrentImage || _currentMode is not { } mode)
        {
            return;
        }

        // Snapshot copy, not the live update.Image reference -- AnalogFmSstvDecoder.cs's own
        // LineDecoded doc comment explicitly warns that event hands out a LIVE ALIAS of the
        // decoder's own mutable pixel buffer, torn/stale if held past the synchronous call (an
        // earlier draft of this feature wrongly assumed the array was frozen once handed out; caught
        // by auditor review). Same technique ReceivedImageBuffer.cs's own Snapshot() already uses.
        _lastImage = Snapshot(update.Image);

        if (_previousLine is int previousLine && _observedStep is null)
        {
            _observedStep = update.Line - previousLine;
        }

        _previousLine = update.Line;

        // The step (scanline group size) is only known from the SECOND event onward -- the first
        // event of any image can never be treated as "complete" here: every real SstvModeDefinition
        // has an ImageHeight far larger than one or two rows, so deferring the completion check
        // until the step is actually learned costs nothing in practice (see this class's own doc
        // comment) and avoids a real bug an early version of this method had: falling back to
        // `step = mode.ImageHeight` on the unlearned first event made `Line(0) + step >= ImageHeight`
        // trivially true, firing "complete" after just one line, every time.
        if (_observedStep is not int step || update.Line + step < mode.ImageHeight)
        {
            return;
        }

        _recordedForCurrentImage = true;
        var modeId = mode.Id;

        // Hoisted into a local BEFORE the closure, same reasoning as OnDecodeRestarted's own
        // abandoned-image save: _lastImage may already be reassigned to a different image, or a
        // DecodeRestarted firing microseconds after this final line may already have wiped
        // IReceivedImageBuffer.Current to its empty placeholder, by the time the task actually
        // runs (ReceiveHistoryRecorderTests' own DecodeRestarted-immediately-after-the-final-line
        // test proves that ordering is real). Reading the live buffer asynchronously here used to
        // save a 1x1 black PNG in exactly that sequence -- this snapshot, already captured
        // synchronously in OnLineDecoded above, is the actual received image regardless of what
        // happens to the live buffer afterward.
        var snapshot = _lastImage!;

        // Also hoisted synchronously, same reasoning as the snapshot above -- IReceivedImageBuffer's
        // own Generation only changes on ModeDetected/DecodeRestarted (never on LineDecoded), and
        // both of those necessarily complete, for every subscriber including this class, before any
        // LineDecoded for the same image can fire -- so reading it here captures the value that
        // correctly identifies THIS image regardless of the two classes' relative subscription
        // order. Threaded through to RecordCompletedImageAsync's NotifySaved call below so
        // RxImagePaneViewModel can still correlate this save to its currently-displayed frame (see
        // IReceivedImageBuffer.Saved's own doc comment for what the generation guards against).
        var generation = _receivedImage.Generation;

        // Fire-and-forget, isolated -- must not block the caller (the audio drain thread, same
        // threading contract as SstvSessionService's own _decoderHandler/_waterfallHandler; disk +
        // SQLite I/O here would otherwise stall live decoding).
        _ = Task.Run(async () =>
        {
            try
            {
                await RecordCompletedImageAsync(modeId, snapshot, generation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Not rethrown -- same reasoning as SstvSessionService's fan-out handlers: a failed
                // history save must never surface into/interrupt the live decode path. Now logged
                // (previously fully silent) -- this is exactly the "it didn't record my image" bug
                // class a user would otherwise have no way to diagnose.
                Log.RecordCompletedImageFailed(_logger, modeId, ex);
            }
        });
    }

    private async Task RecordCompletedImageAsync(string modeId, PixelSnapshot snapshot, int generation)
    {
        var directory = await ResolveImagesDirectoryAsync().ConfigureAwait(false);
        Directory.CreateDirectory(directory);

        var receivedAt = DateTimeOffset.Now;
        // Millisecond precision + an entry-id token, matching RecordAbandonedImageAsync's own
        // collision reasoning below -- a whole pushed buffer (e.g. bulk WAV decode) can complete
        // two images inside the same wall-clock second, which second-granularity naming would
        // silently collide (IOException on save, swallowed by the caller's catch, or one PNG
        // overwritten with two history rows pointing at it).
        var entryId = Guid.NewGuid().ToString();
        var fileName = $"{receivedAt:yyyyMMdd-HHmmssfff}_{modeId}_{entryId[..8]}.png";
        var filePath = Path.Combine(directory, fileName);

        await SaveSnapshotAsync(snapshot, filePath).ConfigureAwait(false);

        // Must fire BEFORE RecordAsync below -- RxImagePaneViewModel.OnHistoryRecorded's own doc
        // comment documents relying on this exact ordering (its correlation key, _lastSavedPath, is
        // set by the Saved-driven OnSaved handler and must already be set by the time the
        // IReceiveHistoryStore.Recorded-driven handler runs for the same frame). Isolated in its own
        // try/catch -- the interface contract for NotifySaved doesn't promise a throwing subscriber
        // is caught internally the way ReceivedImageBuffer's own implementation happens to (round-3
        // audit finding); a UI-pane bug here must never cost the file that's already safely on disk
        // its history row.
        try
        {
            _receivedImage.NotifySaved(filePath, generation);
        }
        catch (Exception ex)
        {
            Log.NotifySavedFailed(_logger, filePath, ex);
        }

        var entry = new ReceiveHistoryEntry(entryId, receivedAt, modeId, filePath, LinkedQsoId: null, DecodeState: ReceiveDecodeState.Completed);
        await _historyStore.RecordAsync(entry).ConfigureAwait(false);

        Log.ImageSaved(_logger, filePath);
    }

    // Never goes through IReceivedImageBuffer.SaveAsync (same reasoning now applies to
    // RecordCompletedImageAsync above) -- ReceivedImageBuffer.OnDecodeRestarted wipes its own
    // Current to an empty image on the SAME DecodeRestarted event this method is called from, and
    // multicast delegate invocation order across two independently-DI-constructed subscribers is
    // not a documented/reliable ordering to depend on. Writes the already-captured snapshot
    // directly instead.
    //
    // `_partial` suffix, on top of RecordCompletedImageAsync's own millisecond+entry-id
    // uniqueness scheme -- gives the user a visible marker distinguishing a partial/abandoned save
    // from a genuinely completed one, which neither legacy nor an unsuffixed filename would.
    private async Task RecordAbandonedImageAsync(string modeId, PixelSnapshot snapshot)
    {
        var directory = await ResolveImagesDirectoryAsync().ConfigureAwait(false);
        Directory.CreateDirectory(directory);

        var receivedAt = DateTimeOffset.Now;
        // Entry id doubles as the filename's uniqueness token -- millisecond precision alone doesn't
        // close the collision window (code-level review finding): two restarts from one bulk-pushed
        // buffer spawn two Task.Run continuations that each read DateTimeOffset.Now after an await,
        // and wall-clock granularity (e.g. Windows' ~1-15.6ms timer resolution) can still land both on
        // the same HHmmssfff -- racing one image.Save() against the other and leaving two history rows
        // pointing at one file.
        var entryId = Guid.NewGuid().ToString();
        var fileName = $"{receivedAt:yyyyMMdd-HHmmssfff}_{modeId}_partial_{entryId[..8]}.png";
        var filePath = Path.Combine(directory, fileName);

        await SaveSnapshotAsync(snapshot, filePath).ConfigureAwait(false);

        var entry = new ReceiveHistoryEntry(entryId, receivedAt, modeId, filePath, LinkedQsoId: null, DecodeState: ReceiveDecodeState.Abandoned);
        await _historyStore.RecordAsync(entry).ConfigureAwait(false);

        Log.AbandonedImageSaved(_logger, filePath);
    }

    // Same ImageSharp technique ReceivedImageBuffer.SaveAsync uses -- duplicated deliberately, not
    // shared, since that method reads IReceivedImageBuffer.Current (which this method must NOT do,
    // see RecordAbandonedImageAsync's own doc comment) rather than taking pixels as a parameter.
    private static Task SaveSnapshotAsync(PixelSnapshot snapshot, string filePath) => Task.Run(() =>
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(snapshot.Width, snapshot.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < snapshot.Height; y++)
            {
                var sourceRow = snapshot.Pixels.AsSpan(y * snapshot.Width, snapshot.Width);
                var destinationRow = accessor.GetRowSpan(y);
                for (var x = 0; x < snapshot.Width; x++)
                {
                    var pixel = sourceRow[x];
                    destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(pixel.R, pixel.G, pixel.B);
                }
            }
        });
        image.Save(filePath);
    });

    private static PixelSnapshot Snapshot(IImageSource source)
    {
        var pixels = new Rgb24[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            source.GetScanline(y).CopyTo(pixels.AsSpan(y * source.Width, source.Width));
        }

        return new PixelSnapshot(pixels, source.Width, source.Height);
    }

    private void ClearPendingAbandon()
    {
        _pendingAbandonMode = null;
        _pendingAbandonImage = null;
        _pendingAbandonLine = null;
        _pendingAbandonStep = null;
        _pendingAbandonRecorded = false;
    }

    private Task<string> ResolveImagesDirectoryAsync() => ReceiveHistorySettings.ResolveDirectoryAsync(_settingsStore);

    // No IImageSource implementation is reachable from this project (Core.Logbook references only
    // Abstractions/Settings; ArrayImageSource lives in Core.Imaging, MutableImageSource is private to
    // Core.Sstv) -- rather than add a new Core-to-Core project reference for this one feature, this
    // small local record holds exactly the pixel data this class needs. Rgb24 itself already lives in
    // Abstractions.Imaging, already referenced.
    private sealed record PixelSnapshot(Rgb24[] Pixels, int Width, int Height);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save completed RX image (mode={ModeId})")]
        public static partial void RecordCompletedImageFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX image saved: {FilePath}")]
        public static partial void ImageSaved(ILogger logger, string filePath);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save abandoned RX image (mode={ModeId})")]
        public static partial void RecordAbandonedImageFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Abandoned RX image saved: {FilePath}")]
        public static partial void AbandonedImageSaved(ILogger logger, string filePath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A Saved-notification subscriber threw for {FilePath} -- the RX image and its history row were still written")]
        public static partial void NotifySavedFailed(ILogger logger, string filePath, Exception ex);
    }
}
