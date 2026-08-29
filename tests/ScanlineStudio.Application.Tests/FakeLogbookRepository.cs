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

    public Exception? ThrowOnSearch { get; set; }

    public Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default)
    {
        if (ThrowOnSearch is not null)
        {
            throw ThrowOnSearch;
        }

        IEnumerable<QsoRecord> results = Records;
        if (query.Callsign is not null)
        {
            results = results.Where(r => string.Equals(r.Callsign, query.Callsign, StringComparison.OrdinalIgnoreCase));
        }

        if (query.From is not null)
        {
            results = results.Where(r => r.StartUtc >= query.From.Value);
        }

        if (query.To is not null)
        {
            results = results.Where(r => r.StartUtc <= query.To.Value);
        }

        // Matches SqliteLogbookRepository.SearchAsync's own "ORDER BY StartUtc DESC" (code-review
        // finding: the filter predicates above were made faithful to the real SQL, but this ordering
        // was left out, an easy divergence to miss now that the rest looks real).
        return Task.FromResult<IReadOnlyList<QsoRecord>>(results.OrderByDescending(r => r.StartUtc).ToList());
    }

    public List<string> DeletedIds { get; } = [];

    public Exception? ThrowOnDelete { get; set; }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (ThrowOnDelete is not null)
        {
            throw ThrowOnDelete;
        }

        var index = Records.FindIndex(r => r.Id == id);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        Records.RemoveAt(index);
        DeletedIds.Add(id);
        return Task.FromResult(true);
    }
}
