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
    /// <summary>Fires with the entry once <see cref="RecordAsync"/>'s write completes -- the only
    /// hook a live UI pane has for "a new frame just
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

    /// <summary>Persists a new saved-image folder location -- stub survey Tier 2's Storage settings
    /// dialog. Same UI-layering reasoning as <see cref="GetImagesDirectoryAsync"/>: the concrete
    /// `Core.Logbook` implementation owns reading/writing its own settings section, so
    /// `ScanlineStudio.UI` never references `Core.Logbook.ReceiveHistorySettings` directly
    /// (`UiLayeringArchitectureTests` forbids that; plan-review finding, 2026-08-26, corrected an
    /// earlier plan that would have injected `ISettingsStore` straight into a UI view-model for
    /// this). <paramref name="directory"/> <see langword="null"/> or all-whitespace resets to the
    /// default <see cref="GetImagesDirectoryAsync"/> itself falls back to. A non-null value is
    /// validated by actually creating the directory (or confirming it already exists) BEFORE the
    /// setting is persisted -- <see cref="DirectoryNotFoundException"/>/<see cref="IOException"/>/
    /// <see cref="UnauthorizedAccessException"/> propagate uncaught so the caller can surface the
    /// real reason, rather than silently accepting a typo/permission problem that would otherwise
    /// only surface later as every subsequent RX image quietly failing to save (plan-review
    /// finding: the recorder's own write path is fire-and-forget with no user-visible failure
    /// surface today).</summary>
    Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default);

    /// <summary>Sets (or clears, via <see langword="null"/>) the Gallery frame metadata card's
    /// user-entered note on an existing entry. Returns <see langword="false"/> (not an exception)
    /// if <paramref name="entryId"/> no longer exists -- defensive, not a case this port's own
    /// production store can reach today (no automatic deletion path exists as of 2026-08-26, see
    /// `docs/removed-features.md`). The caller (a ViewModel) is expected to surface that to the
    /// user, not silently ignore it.</summary>
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

    /// <summary>Disk/DB reconciliation, user-reported 2026-08-26: a real divergence can leave image
    /// files saved to <see cref="GetImagesDirectoryAsync"/>'s own folder with no matching row (e.g.
    /// the DB file was lost/reset independently of the images folder). Scans that folder for files
    /// matching this app's own naming convention (<c>ReceiveHistoryRecorder</c>'s
    /// <c>yyyyMMdd-HHmmssfff_MODE[_partial]_ID8.png</c>, which encodes <see
    /// cref="ReceiveHistoryEntry.ReceivedAt"/>/<see cref="ReceiveHistoryEntry.ModeId"/>/<see
    /// cref="ReceiveHistoryEntry.DecodeState"/> in the filename itself) and backfills a new entry
    /// for each one with no existing row (matched by <see cref="ReceiveHistoryEntry.FilePath"/>,
    /// exact -- ONLY ever adds a missing row, never touches/duplicates an existing one). A file that
    /// doesn't match the naming convention is skipped, not guessed at -- inventing ReceivedAt/ModeId
    /// for a foreign file (a manual copy, a different app's export) would be fabricated data, not a
    /// real reconciliation. Deliberately does NOT raise <see cref="Recorded"/> for backfilled entries
    /// -- that event means "a frame just landed," and a historical backfill is the opposite of that
    /// (see the concrete UI consequence this avoids: <c>RxImagePaneViewModel.PreviousFrames</c>'s own
    /// doc comment, a SESSION-only list that must not be polluted with old, already-on-disk frames
    /// bulk-imported by this call). Returns the count of entries actually imported.</summary>
    Task<int> ReconcileWithDiskAsync(CancellationToken ct = default);
}
