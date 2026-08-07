using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeLogbookRepository : ILogbookRepository
{
    public List<QsoRecord> Records { get; } = [];

    public Exception? ThrowOnAdd { get; set; }

    public Task<QsoRecord> AddAsync(QsoRecord record, CancellationToken ct = default)
    {
        if (ThrowOnAdd is not null)
        {
            throw ThrowOnAdd;
        }

        Records.Add(record);
        return Task.FromResult(record);
    }

    public Task UpdateAsync(QsoRecord record, CancellationToken ct = default)
    {
        var index = Records.FindIndex(r => r.Id == record.Id);
        if (index >= 0)
        {
            Records[index] = record;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<QsoRecord>>(Records);
}
