using System.Collections.Concurrent;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeReceiveHistoryStore : IReceiveHistoryStore
{
    private readonly ConcurrentQueue<TaskCompletionSource<ReceiveHistoryEntry>> _waiters = new();

    public ConcurrentBag<ReceiveHistoryEntry> RecordedEntries { get; } = [];

    public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReceiveHistoryEntry>>(RecordedEntries.ToList());

    public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by ReceiveHistoryRecorderTests.");

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default)
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
