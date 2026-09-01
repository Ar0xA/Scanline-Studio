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

        // Matches SqliteLogbookRepository.SearchAsync's own "ORDER BY StartUtc DESC" -- LEXICAL, not
        // chronological (worked-before code-review finding: `OrderByDescending(r => r.StartUtc)` is
        // NOT faithful to the real SQL despite the sibling finding above claiming so -- the real
        // column is TEXT holding DateTimeOffset.ToString("O"), which SQLite's ORDER BY sorts as a
        // STRING, and a string sort can disagree with a chronological one across different UTC
        // offsets. Sorting on the same "O"-format string here, not the DateTimeOffset value itself,
        // is required for any test that needs to prove a caller handles that real misordering (e.g.
        // LogbookSessionServiceTests.GetWorkedBeforeAsync's own MaxBy-not-candidates[0] test) --
        // using a chronological sort here made that test pass vacuously even against a naive,
        // unfixed candidates[0] implementation.
        return Task.FromResult<IReadOnlyList<QsoRecord>>(
            results.OrderByDescending(r => r.StartUtc.ToString("O"), StringComparer.Ordinal).ToList());
    }

    public Exception? ThrowOnGetById { get; set; }

    public Task<QsoRecord?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (ThrowOnGetById is not null)
        {
            throw ThrowOnGetById;
        }

        return Task.FromResult(Records.FirstOrDefault(r => r.Id == id));
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
