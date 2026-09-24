using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant;

/// <summary>Reuses the same idempotency table SalesService uses for replayed POSTs, to safely ignore
/// a webhook delivery this server has already processed (Meta retries on anything short of a fast 200).</summary>
public sealed class WebhookDedupService(IBusinessDbContext db, IClock clock)
{
    /// <summary>True if this (scope, key) has been seen before; otherwise records it and returns false.</summary>
    public async Task<bool> SeenAsync(string scope, string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (await db.IdempotencyRecords.AsNoTracking().AnyAsync(r => r.Scope == scope && r.Key == key, ct)) return true;
        db.IdempotencyRecords.Add(new IdempotencyRecord { Scope = scope, Key = key, CreatedAt = clock.UtcNow });
        try { await db.SaveChangesAsync(ct); return false; }
        catch (DbUpdateException) { return true; }   // lost the race: someone else recorded this key first
    }
}
