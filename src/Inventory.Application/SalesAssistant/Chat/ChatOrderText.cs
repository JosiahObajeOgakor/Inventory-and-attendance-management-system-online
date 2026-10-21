using System.Globalization;
using Inventory.Application.Abstractions;
using Inventory.Application.Payments;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>Builds the customer-facing sentences that contain real order data (receipt lines, payment instructions, "paid" confirmation) from the database. Shared by the chat flow and the payment webhook so both say exactly the same thing.</summary>
public sealed class ChatOrderText(IBusinessDbContext db, ICompanyContext company)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Naira(decimal amount) => amount % 1 == 0 ? $"₦{amount.ToString("N0", Inv)}" : $"₦{amount.ToString("N2", Inv)}";

    public static string Line(int qty, string name, decimal lineTotal) => $"• {qty} × {name} — {Naira(lineTotal)}";

    /// <summary>The account details as the customer should see them. Times are shown in Lagos time (UTC+1, no daylight saving).</summary>
    public static string PayInstructions(string lang, PayAccount a) =>
        ChatText.Get(lang, "pay_instr", Naira(a.Amount), a.BankName, a.AccountNumber, a.ExpiresAt.AddHours(1).ToString("d MMM, h:mm tt", Inv));

    public async Task<string> ReceiptLinesAsync(int quotationId, CancellationToken ct)
    {
        var rows = await (from i in db.QuotationItems.AsNoTracking()
                          join p in db.Products.AsNoTracking() on i.ProductId equals p.Id
                          where i.QuotationId == quotationId
                          select new { i.Quantity, p.Name, i.LineTotal }).ToListAsync(ct);
        return string.Join('\n', rows.Select(r => Line(r.Quantity, r.Name, r.LineTotal)));
    }

    public async Task<string> PaidAsync(string lang, int quotationId, decimal paid, string? invoiceNumber, CancellationToken ct)
    {
        var number = invoiceNumber ?? (await db.Quotations.AsNoTracking().Where(q => q.Id == quotationId).Select(q => q.QuotationNumber).FirstOrDefaultAsync(ct)) ?? "";
        return ChatText.Get(lang, "paid", Naira(paid), number, await ReceiptLinesAsync(quotationId, ct), company.LegalName);
    }

    public async Task<string> UnfulfilledAsync(string lang, int quotationId, decimal paid, CancellationToken ct)
    {
        var names = await (from i in db.QuotationItems.AsNoTracking() join p in db.Products.AsNoTracking() on i.ProductId equals p.Id where i.QuotationId == quotationId select p.Name).ToListAsync(ct);
        return ChatText.Get(lang, "unfulfilled", Naira(paid), string.Join(", ", names));
    }

    public static string Underpaid(string lang, decimal paid, decimal total, string bank, string account) =>
        ChatText.Get(lang, "underpaid", Naira(paid), Naira(total), Naira(total - paid), bank, account);
}
