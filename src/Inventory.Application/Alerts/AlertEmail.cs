using System.Net;
using System.Text;

namespace Inventory.Application.Alerts;

/// <summary>
/// The daily digest as an email and as a plain-text WhatsApp message. Written to be read on a phone in a few seconds: what needs doing, most
/// urgent first, with the figure next to it. Same single-column, inline-styled approach as the customer emails, because mail clients demand it.
/// </summary>
public static class AlertEmail
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(decimal v) => "₦" + v.ToString("N2");

    public static string Text(BusinessAlerts a)
    {
        var sb = new StringBuilder($"{a.Company} — {a.AsOf:d MMM yyyy}\n");
        if (a.OutOfStock.Count > 0)
            sb.Append($"\nOUT OF STOCK ({a.OutOfStock.Count}):\n").Append(string.Join("\n", a.OutOfStock.Take(8).Select(s => $"· {s.Product}")));
        if (a.LowStock.Count > 0)
            sb.Append($"\n\nRUNNING LOW ({a.LowStock.Count}):\n").Append(string.Join("\n", a.LowStock.Take(8).Select(s =>
                $"· {s.Product}: {s.OnHand} {s.Unit} left" + (s.DaysOfCover is { } d ? $", about {d} day(s) of cover" : ""))));
        if (a.Expiring.Count > 0)
            sb.Append($"\n\nEXPIRING SOON ({a.Expiring.Count}):\n").Append(string.Join("\n", a.Expiring.Take(6).Select(x =>
                $"· {x.Product} batch {x.BatchNumber}: {x.Quantity} left, {(x.DaysLeft < 0 ? "EXPIRED" : x.DaysLeft + " day(s) left")}")));
        if (a.OverdueInvoices.Count > 0)
            sb.Append($"\n\nOVERDUE FROM CUSTOMERS — {N(a.OverdueTotal)}:\n").Append(string.Join("\n", a.OverdueInvoices.Take(8).Select(o =>
                $"· {o.Who} {N(o.Amount)}, {o.DaysOverdue} day(s) late")));
        if (a.SupplierDues.Count > 0)
            sb.Append($"\n\nWE OWE SUPPLIERS — {N(a.SupplierDuesTotal)}:\n").Append(string.Join("\n", a.SupplierDues.Take(8).Select(s =>
                $"· {s.Who} {N(s.Amount)} ({s.Reference})")));
        if (a.DueToReorder.Count > 0)
            sb.Append($"\n\nDUE TO BUY AGAIN ({a.DueToReorder.Count}) — worth a call:\n").Append(string.Join("\n", a.DueToReorder.Take(8).Select(r =>
                $"· {r.Customer}: buys every {r.TypicalDays} day(s), last seen {r.DaysSince} day(s) ago" +
                (string.IsNullOrWhiteSpace(r.UsualItems) ? "" : $" — usually {r.UsualItems}") +
                (string.IsNullOrWhiteSpace(r.Phone) ? "" : $" — {r.Phone}"))));
        return sb.ToString();
    }

    public static string Html(BusinessAlerts a)
    {
        var body = new StringBuilder();
        body.Append($"<p style=\"margin:0 0 18px;font-size:15px;line-height:1.6;color:#2b1226\">{a.Count} thing(s) need attention today.</p>");

        if (a.OutOfStock.Count > 0)
            body.Append(Section("Out of stock", "#c4372c", a.OutOfStock.Take(10).Select(s =>
                (E(s.Product), "nothing left"))));
        if (a.LowStock.Count > 0)
            body.Append(Section("Running low", "#b8762a", a.LowStock.Take(10).Select(s =>
                (E(s.Product), $"{s.OnHand} {E(s.Unit)} left" + (s.DaysOfCover is { } d ? $" · about {d} day(s) of cover" : "") +
                    (s.RunsOutOn is { } r ? $" · runs out {r:d MMM}" : "")))));
        if (a.Expiring.Count > 0)
            body.Append(Section("Expiring soon", "#b8762a", a.Expiring.Take(10).Select(x =>
                (E(x.Product), $"batch {E(x.BatchNumber)} · {x.Quantity} left · " +
                    (x.DaysLeft < 0 ? "<strong>already expired</strong>" : $"{x.DaysLeft} day(s) left ({x.ExpiryDate:d MMM})")))));
        if (a.OverdueInvoices.Count > 0)
            body.Append(Section($"Overdue from customers — {N(a.OverdueTotal)}", "#c4372c", a.OverdueInvoices.Select(o =>
                (E(o.Who), $"{N(o.Amount)} · {o.DaysOverdue} day(s) late · {E(o.Reference)}" +
                    (string.IsNullOrWhiteSpace(o.Phone) ? "" : $" · {E(o.Phone)}")))));
        if (a.SupplierDues.Count > 0)
            body.Append(Section($"We owe suppliers — {N(a.SupplierDuesTotal)}", "#6a2c5b", a.SupplierDues.Select(s =>
                (E(s.Who), $"{N(s.Amount)} · {E(s.Reference)}"))));
        if (a.DueToReorder.Count > 0)
            body.Append(Section("Due to buy again — worth a call", "#1f6b4f", a.DueToReorder.Select(r =>
                (E(r.Customer), $"buys every {r.TypicalDays} day(s), last seen {r.DaysSince} day(s) ago · usually spends {N(r.AverageOrder)}" +
                    (string.IsNullOrWhiteSpace(r.UsualItems) ? "" : $" on {E(r.UsualItems)}") +
                    (string.IsNullOrWhiteSpace(r.Phone) ? "" : $" · {E(r.Phone)}")))));

        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><title>Today's alerts</title></head>
            <body style="margin:0;padding:0;background:#efeaee;font-family:'Segoe UI',Helvetica,Arial,sans-serif">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#efeaee"><tr><td align="center" style="padding:24px 12px">
              <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:100%;max-width:600px;background:#ffffff;border-radius:12px;overflow:hidden">
                <tr><td style="background:#6a2c5b;padding:20px 26px">
                  <div style="font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:#ffffffcc">Today's alerts</div>
                  <div style="font-size:20px;color:#ffffff;margin-top:2px">{E(a.Company)}</div>
                  <div style="font-size:13px;color:#ffffffcc;margin-top:2px">{a.AsOf:dddd d MMMM yyyy}</div>
                </td></tr>
                <tr><td style="padding:24px 26px">{body}</td></tr>
                <tr><td style="padding:0 26px 22px;font-size:12px;line-height:1.5;color:#8a8089">
                  Counted from your own records this morning. Figures move as the day goes on — the app always has the live version.
                </td></tr>
              </table>
            </td></tr></table>
            </body></html>
            """;
    }

    private static string Section(string title, string colour, IEnumerable<(string Name, string Detail)> rows)
    {
        var sb = new StringBuilder($"<div style=\"font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:{colour};font-weight:bold;margin:0 0 6px\">{E(title)}</div>");
        foreach (var (name, detail) in rows)
            sb.Append($"<div style=\"font-size:14px;line-height:1.5;color:#2b1226;padding:7px 0;border-top:1px solid #e8e2e6\"><strong>{name}</strong><br><span style=\"color:#5d525a;font-size:13px\">{detail}</span></div>");
        return sb.Append("<div style=\"height:18px\"></div>").ToString();
    }
}
