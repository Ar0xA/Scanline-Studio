using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application.Tests;

/// <summary>Minimal <see cref="IReceiveHistoryStore"/> fake for <c>LogbookSessionServiceTests</c>'
/// own <c>DeleteQsoAsync</c> coverage -- only <see cref="ClearLinkedQsoIdAsync"/> is exercised
/// (the one method that new code path calls), so every other member throws, matching this
/// codebase's own established minimal-fake convention (e.g. <c>RxAudioAutoSaverTests</c>'s own
/// nested fake).</summary>
internal sealed class FakeReceiveHistoryStoreForLogbook : IReceiveHistoryStore
{
    public event Action<ReceiveHistoryEntry>? Recorded;
    public event Action<ReceiveHistoryEntry>? Deleted;

    /// <summary>Satisfies the interface without leaving either event entirely dead (CS0067) --
    /// neither is ever raised by <c>LogbookSessionServiceTests</c>, matching the sibling fakes'
    /// own <c>RaiseRecorded</c>/<c>RaiseDeleted</c> convention.</summary>
    public void RaiseRecorded(ReceiveHistoryEntry entry) => Recorded?.Invoke(entry);

    public void RaiseDeleted(ReceiveHistoryEntry entry) => Deleted?.Invoke(entry);

    public List<string> ClearedQsoIds { get; } = [];

    public int ClearLinkedQsoIdResultToReturn { get; set; } = 1;

    public Exception? ThrowOnClearLinkedQsoId { get; set; }

    /// <summary>Code-review finding: a plain "both calls happened" assertion doesn't prove ORDER --
    /// a delete-then-clear implementation would pass identically. A test sets this to inspect the
    /// paired <see cref="FakeLogbookRepository.DeletedIds"/> from INSIDE this call, before
    /// <c>LogbookSessionService.DeleteQsoAsync</c> ever reaches its own <c>_repository.DeleteAsync</c>
    /// -- proving the clear genuinely happens first, not just that both eventually happen.</summary>
    public Action? OnClearLinkedQsoId { get; set; }

    public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default)
    {
        OnClearLinkedQsoId?.Invoke();
        if (ThrowOnClearLinkedQsoId is { } ex)
        {
            throw ex;
        }

        ClearedQsoIds.Add(qsoId);
        return Task.FromResult(ClearLinkedQsoIdResultToReturn);
    }

    public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? nrRst, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");

    public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by LogbookSessionServiceTests.");
}
