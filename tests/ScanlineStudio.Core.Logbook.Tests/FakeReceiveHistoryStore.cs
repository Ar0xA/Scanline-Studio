using System.Collections.Concurrent;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeReceiveHistoryStore : IReceiveHistoryStore
{
    private readonly ConcurrentQueue<TaskCompletionSource<ReceiveHistoryEntry>> _waiters = new();

    public ConcurrentBag<ReceiveHistoryEntry> RecordedEntries { get; } = [];

    public event Action<ReceiveHistoryEntry>? Recorded;
    public event Action<ReceiveHistoryEntry>? Deleted;

    /// <summary>Test-only hook, not currently invoked by <see cref="RecordAsync"/> itself --
    /// <c>ReceiveHistoryRecorderTests</c> only exercises the recording path, never a
    /// <see cref="Recorded"/> subscriber, but this satisfies the interface without leaving the event
    /// entirely dead (CS0067), matching the sibling fake's own <c>RaiseRecorded</c> convention.</summary>
    public void RaiseRecorded(ReceiveHistoryEntry entry) => Recorded?.Invoke(entry);

    /// <summary>Same "satisfies the interface without leaving the event entirely dead (CS0067)"
    /// reasoning as <see cref="RaiseRecorded"/> -- <see cref="DeleteAsync"/> itself always throws
    /// (not exercised by ReceiveHistoryRecorderTests), so nothing else would ever invoke this.
    /// </summary>
    public void RaiseDeleted(ReceiveHistoryEntry entry) => Deleted?.Invoke(entry);

    public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReceiveHistoryEntry>>(RecordedEntries.ToList());

    public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? nrRst, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        RecordedEntries.Add(entry);
        while (_waiters.TryDequeue(out var waiter))
        {
            waiter.TrySetResult(entry);
        }

        return Task.CompletedTask;
    }

    /// <summary>Waits for the recorder's fire-and-forget background task to actually call
    /// <see cref="RecordAsync"/>, instead of a fixed sleep-then-assert.</summary>
    public async Task<ReceiveHistoryEntry> WaitForRecordAsync(int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<ReceiveHistoryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Enqueue(tcs);

        if (!RecordedEntries.IsEmpty)
        {
            tcs.TrySetResult(RecordedEntries.First());
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        await using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }
}
