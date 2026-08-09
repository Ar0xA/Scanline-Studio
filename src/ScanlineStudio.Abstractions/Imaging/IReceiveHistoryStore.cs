namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Whether a saved <see cref="ReceiveHistoryEntry"/> represents a normal, fully-decoded
/// image or one abandoned mid-reception (legacy has no equivalent classification -- this port's
/// own <c>ReceiveHistoryRecorder.RecordAbandonedImageAsync</c> saves partial images legacy would
/// have discarded, see that method's own doc comment). Formalizes the `_partial_` filename-suffix
/// convention that same method already writes into a real, queryable field -- the filename
/// convention itself is unchanged, this is an additional structured signal, not a replacement.
/// Does NOT cover "currently decoding" -- a row only exists here once already saved; live
/// in-progress state is <c>IReceivedImageBuffer.Progress</c>, a separate, already-exposed data
/// source.</summary>
public enum ReceiveDecodeState
{
    Completed,
    Abandoned,
}

/// <summary>A browsable index of received images — see spec/07-image-pipeline.md's "RX history"
/// section. Declared here (not alongside its `ScanlineStudio.Core.Logbook` implementation) so
/// `ScanlineStudio.UI` can depend on the interface without pulling in a concrete SQLite-backed
/// reference, same reasoning as <see cref="IImageFileLoader"/>/<see cref="IStockImageLibrary"/>.
///
/// <see cref="DecodeState"/> is a required parameter (no default), deliberately unlike
/// <see cref="Note"/>/<see cref="IsFlagged"/>: both writers (`ReceiveHistoryRecorder`'s
/// `RecordCompletedImageAsync`/`RecordAbandonedImageAsync`) already know the real value with zero
/// new measurement at construction time, so a default here would let a future third call site
/// silently inherit a wrong value instead of the compiler forcing an explicit choice.
/// <see cref="Note"/>/<see cref="IsFlagged"/>/<see cref="LinkedQsoId"/> all start empty (the first
/// two via a real default value; <see cref="LinkedQsoId"/> is required-but-nullable, no default --
/// unchanged from before this record gained the other new fields -- so every caller already passes
/// <see langword="null"/> explicitly) and are set later via
/// <see cref="IReceiveHistoryStore.SetNoteAsync"/>/<see cref="IReceiveHistoryStore.SetFlaggedAsync"/>/
/// <see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/>.</summary>
public sealed record ReceiveHistoryEntry(
    string Id,
    DateTimeOffset ReceivedAt,
    string ModeId,
    string FilePath,
    string? LinkedQsoId,
    ReceiveDecodeState DecodeState,
    string? Note = null,
    bool IsFlagged = false);

public sealed record ReceiveHistoryFilter(string? ModeId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public interface IReceiveHistoryStore
{
    /// <summary>Fires with the entry once <see cref="RecordAsync"/>'s write completes (including its
    /// own retention-trim pass, so a subscriber never gets notified about a row that was already
    /// trimmed away in the same call) -- the only hook a live UI pane has for "a new frame just
    /// landed in history," letting the Gallery tab's own list and the Receive tab's "Previous frames"
    /// strip (same shared <c>RxHistoryPaneViewModel</c> singleton, spec/16-gui-wiring-survey.md's own
    /// PARTIAL finding: neither refreshed live before this) stay current during an active session
    /// instead of only at construction/manual-refresh/filter-change. Raised on whatever thread the
    /// underlying write completes on -- <c>ReceiveHistoryRecorder</c>'s own callers run this from a
    /// decode-thread <c>Task.Run</c>, not the UI thread -- so a subscriber must marshal to the UI
    /// thread itself, same "subscriber's own responsibility" contract as
    /// <see cref="IReceivedImageBuffer.Saved"/>'s own doc comment (a deliberately identical shape to
    /// that already-established event).</summary>
    event Action<ReceiveHistoryEntry>? Recorded;

    Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default);

    /// <summary>Same <see cref="IImageSource"/>-only contract as <see cref="IStockImageLibrary"/> —
    /// no SQLite/ImageSharp type ever crosses into `ScanlineStudio.UI`. The SQLite-backed
    /// implementation lives in a `Core.*` project; `ScanlineStudio.UI` only ever sees this interface
    /// via DI.</summary>
    Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default);

    /// <summary>Called by the same application-layer adapter that populates <see cref="IReceivedImageBuffer"/>,
    /// on decode completion.</summary>
    Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default);

    /// <summary>Resolved saved-image folder (never the raw, possibly-null setting) -- for the
    /// Gallery tab's Storage card (spec/09-ui.md). UI-safe: the concrete `Core.Logbook`
    /// implementation reads its own settings section internally, so `ScanlineStudio.UI` never
    /// references `Core.Logbook.ReceiveHistorySettings` directly (`UiLayeringArchitectureTests`
    /// forbids that).</summary>
    Task<string> GetImagesDirectoryAsync(CancellationToken ct = default);

    /// <summary>Sets (or clears, via <see langword="null"/>) the Gallery frame metadata card's
    /// user-entered note on an existing entry. Returns <see langword="false"/> (not an exception)
    /// if <paramref name="entryId"/> no longer exists -- a reachable case, not just defensive
    /// programming: the retention-trim ring buffer (see `SqliteReceiveHistoryStore`'s own doc
    /// comment) can delete an untouched row between the Gallery loading it and a user editing it.
    /// The caller (a ViewModel) is expected to surface that to the user, not silently ignore
    /// it.</summary>
    Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default);

    /// <summary>Sets the Gallery "Flagged" filter/toggle on an existing entry. Same
    /// missing-<paramref name="entryId"/> contract as <see cref="SetNoteAsync"/>.</summary>
    Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default);

    /// <summary>Sets <see cref="ReceiveHistoryEntry.LinkedQsoId"/> on an existing entry -- the
    /// missing primitive behind the Gallery's "Log entry"/"Open in log" actions (clicked AFTER an
    /// image is already saved, unlike the write-time-only <see cref="RecordAsync"/>).
    /// <c>QsoRecord.ReceivedImageId</c> (`ScanlineStudio.Abstractions.Logbook`) is the matching
    /// reverse foreign key, already designed for this exact link. Same missing-
    /// <paramref name="entryId"/> contract as <see cref="SetNoteAsync"/>.</summary>
    Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default);
}
