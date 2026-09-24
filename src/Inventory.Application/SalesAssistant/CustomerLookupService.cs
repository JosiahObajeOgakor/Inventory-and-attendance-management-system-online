using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant;

/// <summary>Finds a customer by phone (the only identity a chat channel gives us), or creates one.
/// There is no unique index on Customer.Phone today, so this is an exact-string match — a customer
/// texting from a second number will get a second record. Acceptable for v1; not silently "fixed" here.</summary>
public sealed class CustomerLookupService(IBusinessDbContext db)
{
    public async Task<Customer> FindOrCreateAsync(string phone, string? name, CancellationToken ct)
    {
        var normalized = Normalize(phone);
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Phone == normalized, ct);
        if (existing is not null) return existing;

        var customer = new Customer
        {
            Name = string.IsNullOrWhiteSpace(name) ? normalized : name!.Trim(),
            Phone = normalized,
            CustomerType = CustomerTypes.WalkIn,
            RebateRatePct = 0,
            CreditLimit = 0,
            Balance = 0,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }

    /// <summary>Digits only, so "+234 801 234 5678" and "234-801-234-5678" match the same customer.</summary>
    public static string Normalize(string phone) => new(phone.Where(char.IsDigit).ToArray());
}
