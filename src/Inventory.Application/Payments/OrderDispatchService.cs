using Inventory.Application.Abstractions;
using Inventory.Application.Messaging;
using Inventory.Application.SalesAssistant;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Application.Payments;

/// <summary>
/// Everything that happens once an online payment has been recorded: the customer's receipt, a waybill for the dispatch rider, and WhatsApp
/// messages to the admin and the rider. Each step is independent and failures are logged, never thrown — the money is already on the books,
/// and a paid link whose AdminNotifiedAt / RiderNotifiedAt stays null is how a missed message shows up for someone to follow up.
/// </summary>
public sealed class OrderDispatchService(
    IBusinessDbContext db, PaymentFollowUpService receipts, WaybillService waybills, IWhatsAppSender whatsapp, ICompanyContext company,
    IClock clock, IOptions<FulfilmentOptions> options, ILogger<OrderDispatchService> log)
{
    private static readonly CurrentUser System = new(0, "Online orders", "SYSTEM");
    private FulfilmentOptions Opt => options.Value;

    public async Task CompleteAsync(SettleResult result, CancellationToken ct)
    {
        if (!result.Applied || result.InvoiceId is not int invoiceId) return;
        await receipts.SendReceiptIfPossibleAsync(result, ct);

        OrderFacts? order;
        try { order = await LoadAsync(invoiceId, result, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not load paid invoice {InvoiceId} for dispatch", invoiceId); return; }
        if (order is null) return;

        var waybillNumber = await EnsureWaybillAsync(order, ct);
        await NotifyAdminAsync(order, result.Reference, ct);
        await NotifyRiderAsync(order, waybillNumber, result.Reference, ct);
    }

    private sealed record OrderFacts(int InvoiceId, string InvoiceNumber, decimal Total, string Customer, string Phone, string Address, string Zone, string Items, string PaidVia);

    private async Task<OrderFacts?> LoadAsync(int invoiceId, SettleResult result, CancellationToken ct)
    {
        var inv = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invoiceId, ct);
        if (inv is null) return null;
        var cust = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == inv.CustomerId, ct);
        var lines = await (from i in db.InvoiceItems.AsNoTracking() where i.InvoiceId == invoiceId
                           join p in db.Products.AsNoTracking() on i.ProductId equals p.Id
                           select new { i.Quantity, p.Name }).ToListAsync(ct);
        var link = result.Reference is null ? null : await db.PaymentLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Reference == result.Reference, ct);
        var via = PaymentProviders.DisplayName(result.Provider ?? PaymentProviders.Paystack) + (link?.Channel is { Length: > 0 } ch ? $" ({ch})" : "");
        var address = inv.DeliveryAddress ?? string.Join(", ", new[] { cust.Address, cust.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new OrderFacts(inv.Id, inv.InvoiceNumber, inv.TotalAmount, cust.Name, cust.Phone ?? "", address, inv.DeliveryZone ?? "",
            string.Join(", ", lines.Select(l => $"{l.Quantity} x {l.Name}")), via);
    }

    private async Task<string?> EnsureWaybillAsync(OrderFacts o, CancellationToken ct)
    {
        try
        {
            var existing = await db.Waybills.AsNoTracking().Where(w => w.InvoiceId == o.InvoiceId).Select(w => w.WaybillNumber).FirstOrDefaultAsync(ct);
            if (existing is not null) return existing;
            var id = await waybills.CreateAsync(new WaybillInput
            {
                InvoiceId = o.InvoiceId,
                DriverName = Blank(Opt.RiderName),
                DriverPhone = Blank(Opt.RiderPhone),
                DestinationAddress = Cut(Blank(o.Address), 250),
                Notes = Cut($"Online order. Customer {o.Customer} {o.Phone}{(o.Zone.Length > 0 ? " — zone " + o.Zone : "")}", 250),
            }, System, ct);
            return await db.Waybills.AsNoTracking().Where(w => w.Id == id).Select(w => w.WaybillNumber).SingleAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not create the waybill for invoice {InvoiceId}", o.InvoiceId);
            return null;
        }
    }

    private async Task NotifyAdminAsync(OrderFacts o, string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Opt.AdminWhatsApp) || !whatsapp.IsConfigured(company.Key)) return;
        // Template order_paid_admin: "New paid order {{1}}: ₦{{2}} via {{3}}. Customer: {{4}} ({{5}}). Deliver to: {{6}} ({{7}}). Items: {{8}}."
        var sent = await TrySendAsync(Opt.AdminWhatsApp, Opt.AdminTemplate,
            [o.InvoiceNumber, $"{o.Total:N2}", o.PaidVia, o.Customer, Phone(o.Phone), Or(o.Address, "pickup / not given"), Or(o.Zone, "-"), o.Items], ct);
        if (sent && reference is not null)
            await db.PaymentLinks.Where(l => l.Reference == reference).ExecuteUpdateAsync(u => u.SetProperty(l => l.AdminNotifiedAt, clock.UtcNow), ct);
    }

    private async Task NotifyRiderAsync(OrderFacts o, string? waybillNumber, string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Opt.RiderPhone) || !whatsapp.IsConfigured(company.Key)) return;
        // Template dispatch_new_order: "New delivery {{1}} (order {{2}}). Items: {{3}}. Deliver to: {{4}}. Customer: {{5}}, phone {{6}}."
        var sent = await TrySendAsync(Opt.RiderPhone, Opt.RiderTemplate,
            [waybillNumber ?? "-", o.InvoiceNumber, o.Items, Or(o.Address, "call the customer for the address"), o.Customer, Phone(o.Phone)], ct);
        if (sent && reference is not null)
            await db.PaymentLinks.Where(l => l.Reference == reference).ExecuteUpdateAsync(u => u.SetProperty(l => l.RiderNotifiedAt, clock.UtcNow), ct);
    }

    private async Task<bool> TrySendAsync(string to, string template, string[] parameters, CancellationToken ct)
    {
        try { return await whatsapp.SendTemplateAsync(company.Key, International(to), template, Opt.TemplateLanguage, parameters, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "WhatsApp template {Template} failed", template); return false; }
    }

    /// <summary>07039986047 → 2347039986047 (what WhatsApp expects).</summary>
    public static string International(string phone)
    {
        var d = CustomerLookupService.Normalize(phone);
        return d.StartsWith("234") ? d : d.StartsWith('0') ? "234" + d[1..] : d;
    }

    private static string Phone(string p) => string.IsNullOrWhiteSpace(p) ? "-" : "+" + International(p);
    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
