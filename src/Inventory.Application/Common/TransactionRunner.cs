using System.Data;
using Inventory.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Common;

/// <summary>
/// Runs a unit of work in ONE database transaction (READ COMMITTED + explicit row locks in the callers).
/// Retries the whole unit on a deadlock or a duplicate document number — never sleeps for a fixed second
/// like the desktop app did. Anything else rolls back and propagates: nothing is left half-written.
/// </summary>
public sealed class TransactionRunner(IBusinessDbContext db, IDbErrorClassifier errors)
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                var result = await work(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (attempt < attempts && (errors.IsDeadlock(ex) || errors.IsDuplicateKey(ex)))
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ClearTracker();
                await Task.Delay(Random.Shared.Next(15, 60) * attempt, ct);
            }
        }
    }
}

/// <summary>Human-readable document numbers, "&lt;Prefix&gt;-ddMMyyyy-HHmmss", suffixed only on a clash (rule S12).</summary>
public static class DocumentNumbers
{
    public static async Task<string> NextAsync(ICompanyContext company, IClock clock,
        Func<string, CancellationToken, Task<bool>> exists, CancellationToken ct)
    {
        var now = clock.BusinessNow;
        var baseNumber = $"{company.DocumentPrefix}-{now:ddMMyyyy}-{now:HHmmss}";
        var candidate = baseNumber;
        for (var i = 2; await exists(candidate, ct); i++) candidate = $"{baseNumber}-{i}";
        return candidate;
    }
}
