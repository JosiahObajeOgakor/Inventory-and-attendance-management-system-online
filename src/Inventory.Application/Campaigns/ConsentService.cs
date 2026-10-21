using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Campaigns;

/// <summary>The single place a customer's permission to be messaged is read or changed. Campaigns only ever consult <see cref="MarketingStatuses.OptedIn"/>.</summary>
public sealed class ConsentService(IBusinessDbContext db, IClock clock)
{
    public async Task SetAsync(Customer customer, string status, string source, string? language, CancellationToken ct)
    {
        customer.MarketingStatus = status;
        customer.MarketingSource = source.Length > 20 ? source[..20] : source;
        customer.MarketingUpdatedAt = clock.UtcNow;
        if (!string.IsNullOrEmpty(language)) customer.PreferredLanguage = language;
        db.AuditLogs.Add(new AuditLog { UserId = null, UserName = source, Action = status == MarketingStatuses.OptedIn ? "MARKETING_OPT_IN" : "MARKETING_OPT_OUT", Entity = "Customer", EntityId = customer.Id.ToString(), At = clock.UtcNow, Detail = customer.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> StatusByPhoneAsync(string phoneDigits, CancellationToken ct) =>
        await db.Customers.AsNoTracking().Where(c => c.Phone == phoneDigits).Select(c => c.MarketingStatus).FirstOrDefaultAsync(ct) ?? MarketingStatuses.Unknown;
}
