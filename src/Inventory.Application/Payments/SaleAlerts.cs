using System.Globalization;
using System.Net;
using System.Text;

namespace Inventory.Application.Payments;

public sealed record SaleAlertLine(string Product, int Quantity, decimal UnitPrice, decimal Total);

/// <summary>Everything the people who run the shop want to know the moment a sale is completed and paid.</summary>
public sealed record SaleAlert(string Company, int InvoiceId, string InvoiceNumber, string Channel, string CustomerName, string? CustomerPhone, string? CustomerEmail, string CustomerType,
    IReadOnlyList<SaleAlertLine> Lines, decimal Total, decimal Paid, string PaymentMethod, string? PaidInto, IReadOnlyList<string> StockFrom, string? Branch, DateTime AtUtc);

/// <summary>
/// Tells everyone who needs to know that a sale is complete: the admin on WhatsApp and email, the clerks by email only, and the customer by email (with the receipt).
/// Failures are logged and never undo the sale.
/// </summary>
public interface ISaleAlerts
{
    Task SaleCompletedAsync(SaleAlert alert, CancellationToken ct);
}

public static class SaleAlertText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(decimal v) => v % 1 == 0 ? $"₦{v.ToString("N0", Inv)}" : $"₦{v.ToString("N2", Inv)}";
    private static string When(DateTime utc) => utc.AddHours(1).ToString("d MMM yyyy, h:mm tt", Inv) + " (Lagos)";   // Lagos is UTC+1, no daylight saving

    /// <summary>The WhatsApp message to the admin: who bought what, how much, how it was paid, and where the stock came from.</summary>
    public static string Admin(SaleAlert a)
    {
        var sb = new StringBuilder();
        sb.Append("✅ SALE COMPLETED — ").Append(a.Company).Append('\n');
        sb.Append("Invoice ").Append(a.InvoiceNumber).Append(" · ").Append(a.Channel).Append('\n');
        sb.Append("Buyer: ").Append(a.CustomerName);
        if (!string.IsNullOrWhiteSpace(a.CustomerPhone)) sb.Append(" · ").Append(a.CustomerPhone);
        if (!string.IsNullOrWhiteSpace(a.CustomerEmail)) sb.Append(" · ").Append(a.CustomerEmail);
        sb.Append(" (").Append(a.CustomerType).Append(")\n");
        foreach (var l in a.Lines) sb.Append("• ").Append(l.Quantity).Append(" × ").Append(l.Product).Append(" — ").Append(N(l.Total)).Append('\n');
        sb.Append("Total ").Append(N(a.Total)).Append(" · paid ").Append(N(a.Paid)).Append(" via ").Append(a.PaymentMethod);
        if (!string.IsNullOrWhiteSpace(a.PaidInto)) sb.Append(" (").Append(a.PaidInto).Append(')');
        sb.Append('\n');
        if (a.StockFrom.Count > 0) sb.Append("Stock taken from: ").Append(string.Join(", ", a.StockFrom)).Append('\n');
        if (!string.IsNullOrWhiteSpace(a.Branch)) sb.Append("Branch: ").Append(a.Branch).Append('\n');
        sb.Append(When(a.AtUtc));
        return sb.ToString();
    }

    public static string StaffSubject(SaleAlert a) => $"{a.Company}: sale completed — {a.InvoiceNumber} ({N(a.Total)})";
    public static string CustomerSubject(SaleAlert a) => $"{a.Company}: payment received — {a.InvoiceNumber}";

    public static string Text(SaleAlert a, bool forCustomer)
    {
        var head = forCustomer ? $"Thank you, {a.CustomerName}. We have received your payment of {N(a.Paid)} and your order {a.InvoiceNumber} is confirmed. Your receipt is attached.\n\n" : "";
        return head + Admin(a);
    }

    public static string Html(SaleAlert a, bool forCustomer)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var rows = string.Join("", a.Lines.Select(l => $"<tr><td style=\"padding:6px 10px;border-bottom:1px solid #eee\">{E(l.Product)}</td><td style=\"padding:6px 10px;border-bottom:1px solid #eee;text-align:right\">{l.Quantity}</td><td style=\"padding:6px 10px;border-bottom:1px solid #eee;text-align:right\">{E(N(l.UnitPrice))}</td><td style=\"padding:6px 10px;border-bottom:1px solid #eee;text-align:right\">{E(N(l.Total))}</td></tr>"));
        var intro = forCustomer
            ? $"<p>Thank you, <b>{E(a.CustomerName)}</b>. We have received your payment of <b>{E(N(a.Paid))}</b> and your order <b>{E(a.InvoiceNumber)}</b> is confirmed. Your receipt is attached.</p>"
            : $"<p><b>Sale completed</b> on {E(a.Channel)}: invoice <b>{E(a.InvoiceNumber)}</b> by {E(a.CustomerName)}{(string.IsNullOrWhiteSpace(a.CustomerPhone) ? "" : " · " + E(a.CustomerPhone))}{(string.IsNullOrWhiteSpace(a.CustomerEmail) ? "" : " · " + E(a.CustomerEmail))} ({E(a.CustomerType)}).</p>";
        var staff = forCustomer ? "" :
            $"<p style=\"color:#555\">{(a.StockFrom.Count > 0 ? "Stock taken from: " + E(string.Join(", ", a.StockFrom)) + ". " : "")}{(string.IsNullOrWhiteSpace(a.Branch) ? "" : "Branch: " + E(a.Branch) + ". ")}Paid via {E(a.PaymentMethod)}{(string.IsNullOrWhiteSpace(a.PaidInto) ? "" : " (" + E(a.PaidInto) + ")")}.</p>";
        return $"<div style=\"font:15px Segoe UI,Arial,sans-serif;color:#1c2130\">{intro}<table style=\"border-collapse:collapse;min-width:420px\"><thead><tr><th align=\"left\" style=\"padding:6px 10px;border-bottom:2px solid #1c2130\">Item</th><th align=\"right\" style=\"padding:6px 10px;border-bottom:2px solid #1c2130\">Qty</th><th align=\"right\" style=\"padding:6px 10px;border-bottom:2px solid #1c2130\">Price</th><th align=\"right\" style=\"padding:6px 10px;border-bottom:2px solid #1c2130\">Amount</th></tr></thead><tbody>{rows}</tbody><tfoot><tr><td colspan=\"3\" style=\"padding:8px 10px;text-align:right\"><b>Total</b></td><td style=\"padding:8px 10px;text-align:right\"><b>{E(N(a.Total))}</b></td></tr></tfoot></table>{staff}<p style=\"color:#888;font-size:12px\">{E(When(a.AtUtc))} · {E(a.Company)}</p></div>";
    }
}
