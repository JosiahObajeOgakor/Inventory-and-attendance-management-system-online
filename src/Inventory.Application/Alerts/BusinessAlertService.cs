using Inventory.Application.Abstractions;
using Inventory.Application.Analytics;
using Inventory.Application.Company;
using Inventory.Application.Dashboard;
using Inventory.Application.Email;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Alerts;

public sealed record StockAlert(int ProductId, string Product, string Unit, int OnHand, int ReorderLevel, int? DaysOfCover, DateOnly? RunsOutOn);
public sealed record ExpiryAlert(int ProductId, string Product, string BatchNumber, int Quantity, DateOnly ExpiryDate, int DaysLeft);
public sealed record MoneyAlert(string Who, string Reference, decimal Amount, int DaysOverdue, string? Phone);

/// <summary>
/// One day's worth of "things you would want to know", gathered from this company's own records. Everything here is counted, not guessed: the
/// only judgement is which thresholds are worth interrupting someone for.
/// </summary>
public sealed record BusinessAlerts(
    string Company, DateOnly AsOf,
    IReadOnlyList<StockAlert> OutOfStock, IReadOnlyList<StockAlert> LowStock, IReadOnlyList<ExpiryAlert> Expiring,
    IReadOnlyList<MoneyAlert> OverdueInvoices, decimal OverdueTotal,
    IReadOnlyList<MoneyAlert> SupplierDues, decimal SupplierDuesTotal,
    IReadOnlyList<ReorderDue> DueToReorder)
{
    /// <summary>Nothing worth sending: a digest that arrives every day saying "all fine" stops being read.</summary>
    public bool IsEmpty => OutOfStock.Count == 0 && LowStock.Count == 0 && Expiring.Count == 0
        && OverdueInvoices.Count == 0 && SupplierDues.Count == 0 && DueToReorder.Count == 0;

    public int Count => OutOfStock.Count + LowStock.Count + Expiring.Count + OverdueInvoices.Count + SupplierDues.Count + DueToReorder.Count;
}

/// <summary>
/// Builds the daily digest and sends it to whoever runs the business. Nothing in here changes a record; a failure to send must never affect the
/// books, so every send is wrapped and logged rather than thrown back at a caller.
/// </summary>
public sealed class BusinessAlertService(
    IBusinessDbContext db, IClock clock, ICompanyContext company, CompanyProfileService profile, OverviewQueries overview,
    ReorderDueQueries reorders, AnalyticsService analytics, IEmailSender email, IWhatsAppSender whatsapp, ILogger<BusinessAlertService> log)
{
    /// <summary>A batch this close to its date is worth flagging while there is still time to move it.</summary>
    private const int ExpiryWindowDays = 45;

    public async Task<BusinessAlerts> BuildAsync(CancellationToken ct = default)
    {
        var today = clock.BusinessToday;

        // ---- stock: what has run out, and what will before a reorder could arrive
        var forecast = await analytics.ForecastAsync(ct);
        var onHand = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                            where p.IsActive
                            group new { b.QuantityOnHand } by new { p.Id, p.Name, p.Unit, p.ReorderLevel } into g
                            select new { g.Key.Id, g.Key.Name, g.Key.Unit, g.Key.ReorderLevel, Qty = g.Sum(x => x.QuantityOnHand) })
                           .ToListAsync(ct);
        var cover = forecast.ToDictionary(f => f.ProductId);
        StockAlert Alert(int id, string name, string unit, int qty, int level) =>
            new(id, name, unit, qty, level, cover.GetValueOrDefault(id)?.DaysOfCover, cover.GetValueOrDefault(id)?.RunsOutOn);

        var out_ = onHand.Where(p => p.Qty <= 0).Select(p => Alert(p.Id, p.Name, p.Unit, p.Qty, p.ReorderLevel))
            .OrderBy(a => a.Product).ToList();
        var low = onHand.Where(p => p.Qty > 0 && p.Qty <= p.ReorderLevel).Select(p => Alert(p.Id, p.Name, p.Unit, p.Qty, p.ReorderLevel))
            .OrderBy(a => a.DaysOfCover ?? int.MaxValue).ThenBy(a => a.Product).ToList();

        // ---- stock that will expire before it can be sold
        var horizon = today.AddDays(ExpiryWindowDays);
        var expiring = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                              where b.QuantityOnHand > 0 && b.ExpiryDate != null && b.ExpiryDate <= horizon
                              orderby b.ExpiryDate
                              select new { p.Id, p.Name, b.BatchNumber, b.QuantityOnHand, Expiry = b.ExpiryDate!.Value }).ToListAsync(ct);
        var expiryAlerts = expiring.Select(e => new ExpiryAlert(e.Id, e.Name, e.BatchNumber, e.QuantityOnHand, e.Expiry,
            e.Expiry.DayNumber - today.DayNumber)).ToList();

        // ---- money owed to us, past its due date
        var open = await overview.OpenInvoicesAsync(ct);
        var overdue = open.Where(o => o.DueDate < today)
            .Select(o => new MoneyAlert(o.Customer, o.InvoiceNumber, o.Outstanding, o.DaysOverdue, o.Phone))
            .OrderByDescending(o => o.DaysOverdue).Take(15).ToList();

        // ---- money we owe suppliers, both halves (purchase records and older orders)
        var dueOnRecords = await (from s in db.Supplies.AsNoTracking() join sup in db.Suppliers.AsNoTracking() on s.SupplierId equals sup.Id
                                  where s.TotalAmount > s.AmountPaid
                                  select new { sup.Name, sup.Phone, s.Reference, Owed = s.TotalAmount - s.AmountPaid, s.SupplyDate }).ToListAsync(ct);
        var dueOnOrders = await (from p in db.PurchaseOrders.AsNoTracking() join sup in db.Suppliers.AsNoTracking() on p.SupplierId equals sup.Id
                                 where p.PaymentStatus != PaymentStatuses.Paid && p.Status != PurchaseStatuses.Cancelled && !p.IsSample
                                 select new { sup.Name, sup.Phone, Reference = p.PoNumber, Owed = p.TotalAmount - p.AmountPaid, SupplyDate = p.OrderDate }).ToListAsync(ct);
        var supplierDues = dueOnRecords.Concat(dueOnOrders)
            .Select(x => new MoneyAlert(x.Name, x.Reference, x.Owed, today.DayNumber - x.SupplyDate.DayNumber, x.Phone))
            .OrderByDescending(x => x.DaysOverdue).Take(15).ToList();

        var reorderDue = await reorders.DueAsync(10, ct);
        var name = (await profile.EnsureAsync(ct)).LegalName;
        return new BusinessAlerts(name, today, out_, low, expiryAlerts, overdue, overdue.Sum(o => o.Amount),
            supplierDues, supplierDues.Sum(s => s.Amount), reorderDue);
    }

    /// <summary>
    /// Sends today's digest by email, and as a short summary on WhatsApp when the business has a number set up. Returns what was actually sent
    /// so a caller can say so plainly. Never throws: a digest is not worth breaking anything for.
    /// </summary>
    public async Task<string> SendAsync(string? emailTo, string? whatsappTo, CancellationToken ct = default)
    {
        var alerts = await BuildAsync(ct);
        if (alerts.IsEmpty) return "Nothing needed attention today, so nothing was sent.";

        var sent = new List<string>();
        var p = await profile.EnsureAsync(ct);
        var address = string.IsNullOrWhiteSpace(emailTo) ? p.Email : emailTo.Trim();
        if (email.IsConfigured && !string.IsNullOrWhiteSpace(address))
        {
            try
            {
                var subject = $"{alerts.Company}: {alerts.Count} thing(s) need attention — {alerts.AsOf:d MMM yyyy}";
                await email.SendAsync(new EmailMessage(address!, alerts.Company, subject, AlertEmail.Html(alerts), AlertEmail.Text(alerts),
                    [], alerts.Company, string.IsNullOrWhiteSpace(p.Email) ? null : p.Email, null), ct);
                sent.Add($"email to {address}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not email the daily alerts"); }
        }

        var phone = string.IsNullOrWhiteSpace(whatsappTo) ? p.Phone : whatsappTo.Trim();
        if (whatsapp.IsConfigured(company.Key) && !string.IsNullOrWhiteSpace(phone) && phone!.Count(char.IsDigit) >= 10)
        {
            try
            {
                await whatsapp.SendTextAsync(company.Key, OrderDispatchService.International(phone), AlertEmail.Text(alerts), ct);
                sent.Add($"WhatsApp to {phone}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not WhatsApp the daily alerts"); }
        }

        return sent.Count == 0
            ? "There was something to report, but no email mailbox or WhatsApp number is set up to send it to."
            : "Sent by " + string.Join(" and ", sent) + ".";
    }
}
