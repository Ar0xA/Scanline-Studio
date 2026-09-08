using ScanlineStudio.Abstractions.Radio;

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
/// <see cref="IReceiveHistoryStore.SetLinkedQsoIdAsync"/>.
///
/// <see cref="FrequencyHz"/>/<see cref="RigMode"/> (ui_transition_plan.md step 6, T2-4): the rig's
/// state at the moment this reception COMPLETED (or was abandoned) -- not when it started, and never
/// re-read later. <see langword="null"/> means no radio was connected/reporting a state at that
/// instant, same convention as <see cref="RadioState"/>'s own optional fields -- never a fake zero.
/// Trailing and optional so the 4 existing production construction sites (in
/// <c>ReceiveHistoryRecorder</c>/<c>SqliteReceiveHistoryStore</c>) keep compiling unchanged; both
/// writers pass real values today.
///
/// <see cref="AudioFilePath"/> (ui_transition_plan.md step 12, Auto-save RX audio): the linked WAV,
/// or <see langword="null"/> if auto-save was off or no audio was ever attached for this reception
/// (a MISS, not an error -- see <c>docs/plans/step12-auto-save-rx-audio-plan.md</c>'s "Accepted v1
/// limitations"). A real DB column, unlike <see cref="ReceptionId"/> below.
///
/// <see cref="ReceptionId"/> is TRANSIENT, not persisted -- see that property's own doc comment.
///
/// <see cref="DecodedCallsign"/>/<see cref="DecodedNrRst"/> (fsk_cwid.md §5 A2): the decoded
/// callsign/NR-RST attached to this entry after the fact, via <see cref="IReceiveHistoryStore.SetDecodedStationIdAsync"/>
/// once <c>RxStationIdAttacher</c>'s join with <see cref="ISstvSessionService.StationIdDecoded"/>/
/// <see cref="ISstvSessionService.CwIdDecoded"/> (`ScanlineStudio.Application`) completes -- the 4
/// original construction sites never set these (always <see langword="null"/> at
/// <see cref="RecordAsync"/> time; both the FSK ID and the CW ID transmit AFTER the image, so neither
/// can be known yet). <see cref="DecodedNrRst"/> already carries the "595" RST-default prefix, matching
/// what the live pane shows for the same decode (<c>RxImagePaneViewModel.ApplyDecodedNrRst</c>'s own
/// convention) -- not a raw on-air fragment. <see cref="DecodedNrRst"/> is FSK-only (CW-ID carries no
/// NR/RST sub-packet), so it has no matching source field.
///
/// <see cref="DecodedCallsign"/> can come from EITHER source -- <see cref="DecodedCallsignSource"/>
/// (fsk_cwid.md B-P5) records which one, using <see cref="StationIdSources.Fsk"/>/
/// <see cref="StationIdSources.Cw"/>, so the Gallery can show a truthful label instead of a hardcoded
/// "FSK" one. <see langword="null"/> means "not yet decoded" OR "decoded by a pre-B-P5 build" (every
/// row that predates this field only ever got a callsign from FSK, since <c>RxStationIdAttacher</c>
/// didn't subscribe to <see cref="ISstvSessionService.CwIdDecoded"/> before B-P5) -- a reader falling
/// back to <see cref="StationIdSources.Fsk"/> for a <see langword="null"/> source is therefore correct,
/// not a guess. <c>RxStationIdAttacher</c>'s own merge priority (mirroring the live pane's already-
/// shipped <c>RxImagePaneViewModel.ApplyStationIdDecodedAsync</c>/<c>ApplyCwIdDecodedAsync</c> rule
/// exactly): FSK always overwrites regardless of what's already attached; CW only attaches when no
/// FSK-sourced callsign is already present for this reception. See <c>RxStationIdAttacher.ApplyUpdate</c>'s
/// own doc comment for the four documented divergences between this DB-side rule and the live pane's
/// UI-side one (the pane's gate is the user-editable <c>OverrideCallsign</c> field, not this source
/// tag).
///
/// <see cref="DecodedCwId"/> (fsk_cwid.md B-P5): the CW-ID decoder's raw decoded text (e.g.
/// <c>"DE W1AW"</c>), always attached when a CW-ID window decodes for this reception -- independent of
/// whether a callsign was extracted from it (a CW-ID with no callsign-shaped token still fills this
/// field). Mirrors <c>RxImagePaneViewModel.CwIdText</c>'s own live-pane value for the same event.</summary>
public sealed record ReceiveHistoryEntry(
    string Id,
    DateTimeOffset ReceivedAt,
    string ModeId,
    string FilePath,
    string? LinkedQsoId,
    ReceiveDecodeState DecodeState,
    string? Note = null,
    bool IsFlagged = false,
    long? FrequencyHz = null,
    RadioMode? RigMode = null,
    string? AudioFilePath = null,
    string? DecodedCallsign = null,
    string? DecodedNrRst = null,
    string? DecodedCallsignSource = null,
    string? DecodedCwId = null)
{
    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio): the reception identity
    /// (<c>ISstvDecoder.ReceptionSequence</c>'s value at this reception's arm) an in-memory
    /// <c>RxAudioAutoSaver</c> correlator uses to key its <c>Recorded</c>+<c>AudioSliceReady</c> join
    /// -- see <c>docs/plans/step12-auto-save-rx-audio-plan.md</c>'s "Correlation" section for the
    /// full design. DELIBERATELY NOT a DB column and NOT a positional constructor parameter (unlike
    /// every other member on this record): it has meaning only for the lifetime of the one
    /// <see cref="IReceiveHistoryStore.Recorded"/> event raise a writer's own instance passes through
    /// (<c>SqliteReceiveHistoryStore.RecordAsync</c> invokes <see cref="IReceiveHistoryStore.Recorded"/>
    /// with the caller's own object, so this value DOES survive to a subscriber in that one call), and
    /// is meaningless once reloaded from disk (a <c>QueryAsync</c>/<c>ReconcileWithDiskAsync</c>
    /// result correctly carries the CLR default <c>0</c>, which the correlator treats as "no reception
    /// identity was ever assigned" and never parks or matches -- <c>0</c> is reserved for exactly this
    /// by <c>ISstvDecoder.ReceptionSequence</c>'s own contract, whose first real value is <c>1</c>).
    /// As a property outside the primary constructor, this does NOT participate in this record's
    /// generated positional deconstruction, but DOES still join its generated value equality -- a
    /// disk-loaded copy (<c>ReceptionId == 0</c>) never equals the originally-recorded instance
    /// (<c>ReceptionId == n</c>) for the same row. Harmless today: every real consumer
    /// (<c>RxHistoryPaneViewModel.UpdateEntryInPlace</c> and siblings) matches by <see cref="Id"/>,
    /// never by record equality -- do not start relying on record equality for this type.</summary>
    public long ReceptionId { get; init; }
}

public sealed record ReceiveHistoryFilter(string? ModeId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

/// <summary>fsk_cwid.md B-P5, auditor plan-review finding: shared constants for
/// <see cref="ReceiveHistoryEntry.DecodedCallsignSource"/> so the value <c>RxStationIdAttacher</c>
/// writes to the DB and the value <c>RxImagePaneViewModel</c> shows in its own "Callsign · source"
/// annotation can never drift apart via an independently-typed string literal on either side.</summary>
public static class StationIdSources
{
    public const string Fsk = "FSK";
    public const string Cw = "CW";
}

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio) -- the resolved (never raw/possibly-
/// null) settings pair, same "resolved, not raw" contract as <see cref="IReceiveHistoryStore.GetImagesDirectoryAsync"/>.
/// <see cref="Directory"/> is always a real, resolved path even when <see cref="Enabled"/> is
/// <see langword="false"/> -- a UI toggling the feature on doesn't need a separate directory-picker
/// round-trip to see where it would save to.</summary>
public sealed record AudioAutoSaveSettings(bool Enabled, string Directory);

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

    /// <summary>ui_transition_plan.md step 4 (T1-4, reframed): fires once <see cref="DeleteAsync"/>'s
    /// row removal actually happened (mirrors <see cref="Recorded"/>'s own shape/threading contract
    /// -- raised on whatever thread the delete completed on, subscriber marshals to the UI thread
    /// itself). NOT raised for a no-op delete (an already-gone <paramref name="entry"/>'s Id) -- see
    /// <see cref="DeleteAsync"/>'s own doc comment.</summary>
    event Action<ReceiveHistoryEntry>? Deleted;

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

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio): resolved (never raw/possibly-
    /// null) enabled flag + audio folder -- same "resolved, not raw" contract as
    /// <see cref="GetImagesDirectoryAsync"/>, bundled into one call since <c>RxAudioAutoSaver</c>
    /// reads both together at the moment each pairing completes (see
    /// <c>docs/plans/step12-auto-save-rx-audio-plan.md</c>'s "The join" section for why this is a
    /// live per-completion read, not a cached value, for that specific consumer).</summary>
    Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default);

    /// <summary>Persists the auto-save-audio enable flag and folder together -- same UI-layering and
    /// validate-before-persist contract as <see cref="SetImagesDirectoryAsync"/>.
    /// <paramref name="directory"/> <see langword="null"/> or all-whitespace resets to the default
    /// <see cref="GetAudioSettingsAsync"/> itself falls back to. This does NOT itself propagate the
    /// live value into a running decode session -- see
    /// <c>ISstvSessionService.SetAutoSaveAudioEnabled</c>/<c>SetAudioDirectory</c> for the separate
    /// live-apply path an Options Apply/Save flow must also call.</summary>
    Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default);

    /// <summary>Sets <see cref="ReceiveHistoryEntry.AudioFilePath"/> on an existing entry, once
    /// <c>RxAudioAutoSaver</c>'s join completes a pairing -- a plain <c>UPDATE</c>, deliberately NOT
    /// re-raising <see cref="Recorded"/> (see this interface's own doc comment on that event: a
    /// synthetic second <c>Recorded</c> for the same row would be a bigger behavior change than this
    /// feature needs; a live pane instead patches its already-held in-memory entry directly when
    /// `RxAudioAutoSaver` reports success). Same missing-<paramref name="entryId"/> contract as
    /// <see cref="SetNoteAsync"/>.</summary>
    Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default);

    /// <summary>Sets <see cref="ReceiveHistoryEntry.DecodedCallsign"/>/<see cref="ReceiveHistoryEntry.DecodedCallsignSource"/>/
    /// <see cref="ReceiveHistoryEntry.DecodedNrRst"/>/<see cref="ReceiveHistoryEntry.DecodedCwId"/>
    /// on an existing entry, once <c>RxStationIdAttacher</c>'s join completes -- a plain
    /// <c>UPDATE</c>, deliberately NOT re-raising <see cref="Recorded"/>, same contract as
    /// <see cref="SetAudioFilePathAsync"/> (a live pane instead patches its already-held in-memory
    /// entry directly via <c>IRxStationIdAttacher.StationIdAttached</c>). Unlike
    /// <see cref="SetAudioFilePathAsync"/>'s single value, ALL FOUR columns are written on every call,
    /// exactly as given -- <see langword="null"/> writes <see langword="null"/>, it does not leave a
    /// previously-set column unchanged. `RxStationIdAttacher` accounts for this itself: callsign,
    /// NR/RST, and CW-ID text can each decode at a different time for the same reception
    /// (`FskStationIdEncoder.Generate`'s own two-sub-packet shape, plus the independently-timed CW-ID
    /// capture window), so it always calls this with its own full current accumulator (the
    /// newest-known value for whichever of the three has decoded so far, never a value it hasn't
    /// actually seen). Same missing-<paramref name="entryId"/> contract as
    /// <see cref="SetAudioFilePathAsync"/>.</summary>
    Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? callsignSource, string? nrRst, string? cwId, CancellationToken ct = default);

    /// <summary>Sets (or clears, via <see langword="null"/>) the Gallery frame metadata card's
    /// user-entered note on an existing entry. Returns <see langword="false"/> (not an exception)
    /// if <paramref name="entryId"/> no longer exists -- defensive; reachable in production since
    /// <see cref="DeleteAsync"/> was added (ui_transition_plan.md step 4) -- e.g. a stale in-memory
    /// reference to an entry another session/tab already deleted. The caller (a ViewModel) is
    /// expected to surface that to the user, not silently ignore it.</summary>
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

    /// <summary>ui_transition_plan.md step 15 (QSO delete) — the inverse of
    /// <see cref="SetLinkedQsoIdAsync"/>, called by <c>LogbookSessionService.DeleteQsoAsync</c>
    /// BEFORE the QSO row itself is deleted, so no <see cref="ReceiveHistoryEntry.LinkedQsoId"/>
    /// is ever left pointing at a QSO that no longer exists (a dangling FK would otherwise make
    /// the Gallery report that frame as permanently "Logged," excluding it from the "Unlogged"
    /// filter with no way for the operator to re-log it). Clears every entry currently linked to
    /// <paramref name="qsoId"/> (in practice at most one, but not schema-enforced) — returns the
    /// number of rows actually cleared, purely informational; callers do not need to branch on it
    /// the way <see cref="SetLinkedQsoIdAsync"/>'s single-row <c>bool</c> return is branched on,
    /// since "zero entries were linked to this QSO" is a perfectly normal, non-error outcome
    /// here.</summary>
    Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default);

    /// <summary>ui_transition_plan.md step 4 (T1-4, reframed): per-item manual delete -- the
    /// retention-cap AUTO-delete was removed outright by deliberate user decision
    /// (`docs/removed-features.md` "RX history retention limit") and this does not reintroduce one;
    /// it's the operator explicitly discarding one bad capture (noise, a duplicate, a wrong sync).
    /// Removes the DB row, the image file at <see cref="ReceiveHistoryEntry.FilePath"/>, AND the
    /// linked audio file at <see cref="ReceiveHistoryEntry.AudioFilePath"/> if one is set
    /// (ui_transition_plan.md step 12 Step 4). Either file already being gone is NOT an error (an
    /// orphaned row pointing at a manually-deleted-on-disk file is exactly the state this exists to
    /// let the operator clean up) -- only a real deletion FAILURE (e.g. permission denied on an
    /// existing file) is logged and otherwise swallowed,
    /// deliberately: the row still gets removed regardless, since the primary contract this method
    /// promises is "this entry disappears from the Gallery," not "and disk space is reclaimed,
    /// guaranteed." Deletion intent persists so disk reconciliation or a delayed recorder write
    /// cannot restore an image whose disk cleanup failed. Returns <see langword="false"/> (not an exception) if the row no longer existed
    /// -- same defensive contract as <see cref="SetNoteAsync"/> -- and does NOT raise
    /// <see cref="Deleted"/> in that case.</summary>
    Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default);

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
