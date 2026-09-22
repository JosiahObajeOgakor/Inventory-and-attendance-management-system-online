using System.Net;
using System.Text;
using Inventory.Application.Documents;

namespace Inventory.Application.Email;

/// <summary>
/// The customer-facing emails for quotations and price lists. Built as a single-column table layout with inline styles because email clients
/// (Outlook, Gmail, Zoho webmail) ignore most modern CSS. Everything that comes from the database is HTML-encoded.
/// </summary>
public static class EmailTemplates
{
    public const string LogoCid = "company-logo";
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(decimal v) => "₦" + v.ToString("N2");

    // Candid Purrfect is plum; ChewyPets is green — the same tints the app uses.
    private static (string Main, string Tint, string Ink) Palette(string companyKey) =>
        companyKey == "candid" ? ("#6a2c5b", "#f5ecf3", "#2b1226") : ("#1f6b4f", "#e9f3ee", "#10261d");

    public static string QuotationSubject(QuotationDoc d) => Clean($"Quotation {d.Number} from {d.Brand.Name}");
    public static string PriceListSubject(PriceListDoc d) => Clean($"Our latest price list — {d.Brand.Name}");
    private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ");

    public static string Quotation(QuotationDoc d, string? note, string senderName, bool hasLogo)
    {
        var p = Palette(d.Brand.CompanyKey);
        var rows = new StringBuilder();
        foreach (var l in d.Lines.Take(8))
            rows.Append($"<tr><td style=\"padding:9px 0;border-bottom:1px solid #e8e2e6;font-size:14px;color:{p.Ink}\">{E(l.Description)}<br><span style=\"color:#7a7079;font-size:12px\">{l.Qty:N0} {E(l.Unit)} × {N(l.Price)}</span></td>" +
                        $"<td align=\"right\" style=\"padding:9px 0;border-bottom:1px solid #e8e2e6;font-size:14px;color:{p.Ink};white-space:nowrap\">{N(l.Amount)}</td></tr>");
        if (d.Lines.Count > 8) rows.Append($"<tr><td colspan=\"2\" style=\"padding:9px 0;font-size:12px;color:#7a7079\">…and {d.Lines.Count - 8} more item(s) in the attached PDF.</td></tr>");

        var totals = new StringBuilder();
        if (d.DiscountAmount > 0) totals.Append(TotalRow("Discount (" + d.DiscountPct.ToString("0.##") + "%)", "−" + N(d.DiscountAmount), false, p));
        if (d.VatAmount > 0) totals.Append(TotalRow("VAT (" + d.VatRate.ToString("0.##") + "%)", N(d.VatAmount), false, p));
        totals.Append(TotalRow("Total", N(d.Total), true, p));

        var body = $"""
            <p style="margin:0 0 14px;font-size:16px;line-height:1.5;color:{p.Ink}">Dear {E(FirstName(d.Customer.Name))},</p>
            <p style="margin:0 0 18px;font-size:15px;line-height:1.6;color:{p.Ink}">Thank you for your enquiry. Here is the quotation you asked for. The full document is attached as a PDF you can keep, print or forward.</p>
            <p style="margin:0 0 18px;font-size:14px;line-height:1.5;color:#5d525a">Issued by <strong style="color:{p.Ink}">{E(senderName)}</strong>, {E(d.Brand.Name)}</p>
            {NoteBlock(note, p)}
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{p.Tint};border-radius:10px;margin:0 0 18px">
              <tr><td style="padding:16px 18px">
                <div style="font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:{p.Main};font-weight:bold">Quotation</div>
                <div style="font-family:Consolas,'Courier New',monospace;font-size:18px;color:{p.Ink};margin-top:2px">{E(d.Number)}</div>
                <div style="font-size:13px;color:#5d525a;margin-top:2px">Prepared {d.Date:d MMMM yyyy}</div>
              </td>
              <td align="right" style="padding:16px 18px">
                <div style="font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:{p.Main};font-weight:bold">Total</div>
                <div style="font-family:Consolas,'Courier New',monospace;font-size:24px;color:{p.Ink};margin-top:2px">{N(d.Total)}</div>
              </td></tr>
            </table>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 6px">{rows}</table>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 20px">{totals}</table>
            {PayButton(d.PayUrl, d.Total, p)}
            {Banks(d.Brand, p)}
            <p style="margin:18px 0 0;font-size:14px;line-height:1.6;color:{p.Ink}">To go ahead, just reply to this email or call us. Stock is confirmed when we receive your order.</p>
            """;
        return Shell(d.Brand, p, hasLogo, "Your quotation", body, senderName);
    }

    public static string PriceList(PriceListDoc d, string? note, string senderName, bool hasLogo)
    {
        var p = Palette(d.Brand.CompanyKey);
        var rows = new StringBuilder();
        foreach (var l in d.Lines.Take(10))
            rows.Append($"<tr><td style=\"padding:8px 0;border-bottom:1px solid #e8e2e6;font-size:14px;color:{p.Ink}\">{E(l.Product)}<br><span style=\"color:#7a7079;font-size:12px\">{E(l.Category)} · per {E(l.Unit)}</span></td>" +
                        $"<td align=\"right\" style=\"padding:8px 0;border-bottom:1px solid #e8e2e6;font-family:Consolas,'Courier New',monospace;font-size:14px;color:{p.Ink};white-space:nowrap\">{N(l.Price)}</td></tr>");
        if (d.Lines.Count > 10) rows.Append($"<tr><td colspan=\"2\" style=\"padding:9px 0;font-size:12px;color:#7a7079\">{d.Lines.Count - 10} more product(s) in the attached price list.</td></tr>");

        var greeting = string.IsNullOrWhiteSpace(d.CustomerName) ? "Hello," : $"Dear {E(FirstName(d.CustomerName!))},";
        var body = $"""
            <p style="margin:0 0 14px;font-size:16px;line-height:1.5;color:{p.Ink}">{greeting}</p>
            <p style="margin:0 0 18px;font-size:15px;line-height:1.6;color:{p.Ink}">Please find our current <strong>{E(d.Tier)}</strong> price list attached as a PDF, dated {d.Date:d MMMM yyyy}. A few of our products are shown below.</p>
            <p style="margin:0 0 18px;font-size:14px;line-height:1.5;color:#5d525a">Issued by <strong style="color:{p.Ink}">{E(senderName)}</strong>, {E(d.Brand.Name)}</p>
            {NoteBlock(note, p)}
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{p.Tint};border-radius:10px;margin:0 0 14px">
              <tr><td style="padding:14px 18px">
                <div style="font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:{p.Main};font-weight:bold">Price list</div>
                <div style="font-family:Consolas,'Courier New',monospace;font-size:15px;color:{p.Ink};margin-top:2px">{E(d.Reference)}</div>
              </td>
              <td align="right" style="padding:14px 18px;font-size:13px;color:#5d525a">{d.Lines.Count:N0} products<br>{E(d.Tier)} prices</td></tr>
            </table>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 20px">{rows}</table>
            {Banks(d.Brand, p)}
            <p style="margin:18px 0 0;font-size:14px;line-height:1.6;color:{p.Ink}">Prices can change, so please confirm with us before you order. Reply to this email to place an order or ask for a quotation.</p>
            """;
        return Shell(d.Brand, p, hasLogo, "Price list", body, senderName);
    }

    public static string QuotationText(QuotationDoc d, string? note, string senderName) =>
        $"Dear {FirstName(d.Customer.Name)},\n\nThank you for your enquiry. Quotation {d.Number} ({d.Date:d MMMM yyyy}) for {N(d.Total)} is attached as a PDF.\n\nIssued by {senderName}, {d.Brand.Name}.\n\n" +
        (string.IsNullOrWhiteSpace(note) ? "" : note!.Trim() + "\n\n") +
        $"To go ahead, reply to this email or call us.\n\n{senderName}\n{d.Brand.Name}\n{d.Brand.Phone}";

    public static string PriceListText(PriceListDoc d, string? note, string senderName) =>
        $"{(string.IsNullOrWhiteSpace(d.CustomerName) ? "Hello" : "Dear " + FirstName(d.CustomerName!))},\n\nOur current {d.Tier} price list ({d.Date:d MMMM yyyy}) is attached as a PDF.\n\nIssued by {senderName}, {d.Brand.Name}.\n\n" +
        (string.IsNullOrWhiteSpace(note) ? "" : note!.Trim() + "\n\n") +
        $"Prices can change, so please confirm before ordering.\n\n{senderName}\n{d.Brand.Name}\n{d.Brand.Phone}";

    // ---------------------------------------------------------------- pieces
    private static string FirstName(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    private static string PayButton(string? url, decimal amount, (string Main, string Tint, string Ink) p) =>
        string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ? "" :
        $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 20px\"><tr><td align=\"center\"><a href=\"{E(url)}\" style=\"display:inline-block;background:{p.Main};color:#ffffff;text-decoration:none;font-weight:bold;font-size:16px;padding:14px 28px;border-radius:10px\">Pay {N(amount)} securely online</a>" +
        $"<div style=\"font-size:12px;color:#7a7079;margin-top:8px\">Card, bank transfer or USSD · secured by Paystack</div></td></tr></table>";

    private static string NoteBlock(string? note, (string Main, string Tint, string Ink) p) =>
        string.IsNullOrWhiteSpace(note) ? "" :
        $"<p style=\"margin:0 0 18px;padding:12px 14px;border-left:3px solid {p.Main};background:#faf8f9;font-size:14px;line-height:1.6;color:{p.Ink}\">{E(note.Trim()).Replace("\n", "<br>")}</p>";

    private static string TotalRow(string label, string value, bool grand, (string Main, string Tint, string Ink) p) =>
        grand ? $"<tr><td style=\"padding:10px 0 0;border-top:2px solid {p.Ink};font-size:15px;font-weight:bold;color:{p.Ink}\">{label}</td><td align=\"right\" style=\"padding:10px 0 0;border-top:2px solid {p.Ink};font-family:Consolas,'Courier New',monospace;font-size:18px;font-weight:bold;color:{p.Main}\">{value}</td></tr>"
              : $"<tr><td style=\"padding:4px 0;font-size:13px;color:#5d525a\">{label}</td><td align=\"right\" style=\"padding:4px 0;font-family:Consolas,'Courier New',monospace;font-size:13px;color:#5d525a\">{value}</td></tr>";

    private static string Banks(Branding b, (string Main, string Tint, string Ink) p)
    {
        if (b.Banks.Count == 0) return "";
        var sb = new StringBuilder($"<div style=\"font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:{p.Main};font-weight:bold;margin:0 0 6px\">Pay by bank transfer</div>");
        foreach (var k in b.Banks)
            sb.Append($"<div style=\"font-size:13px;line-height:1.6;color:{p.Ink};padding:6px 0;border-top:1px solid #e8e2e6\">{E(k.Bank)} · <span style=\"font-family:Consolas,'Courier New',monospace;font-size:14px;letter-spacing:.04em\">{E(k.AccountNumber)}</span><br><span style=\"color:#7a7079\">{E(k.AccountName)}</span></div>");
        return sb.ToString();
    }

    private static string Shell(Branding b, (string Main, string Tint, string Ink) p, bool hasLogo, string eyebrow, string body, string senderName)
    {
        var mark = hasLogo
            ? $"<img src=\"cid:{LogoCid}\" alt=\"{E(b.Name)}\" height=\"40\" style=\"display:block;height:40px;max-width:180px;border:0\">"
            : $"<div style=\"font-family:Impact,'Arial Narrow',Arial,sans-serif;font-size:22px;letter-spacing:.06em;text-transform:uppercase;color:#ffffff\">{E(b.Name)}</div>";
        var contact = string.Join(" · ", new[] { b.Phone, b.Email }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(E));
        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><title>{E(eyebrow)}</title></head>
            <body style="margin:0;padding:0;background:#efeaee;font-family:'Segoe UI',Helvetica,Arial,sans-serif">
            <div style="display:none;max-height:0;overflow:hidden;color:#efeaee">{E(eyebrow)} from {E(b.Name)} — attached as a PDF.</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#efeaee"><tr><td align="center" style="padding:24px 12px">
              <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:100%;max-width:600px;background:#ffffff;border-radius:12px;overflow:hidden">
                <tr><td style="background:{p.Main};padding:22px 28px">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
                    <td>{mark}</td>
                    <td align="right" style="font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:#ffffffcc">{E(eyebrow)}</td>
                  </tr></table>
                </td></tr>
                <tr><td style="padding:28px">{body}</td></tr>
                <tr><td style="padding:0 28px 26px">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-top:1px solid #e8e2e6"><tr><td style="padding-top:16px;font-size:13px;line-height:1.6;color:#5d525a">
                    <strong style="color:{p.Ink}">{E(senderName)}</strong><br>{E(b.Name)}<br>{E(b.Address)}<br>{contact}
                  </td></tr></table>
                </td></tr>
              </table>
              <div style="max-width:600px;padding:14px 8px;font-size:11px;line-height:1.5;color:#8a8089">You are receiving this because you asked {E(b.Name)} for it. {(string.IsNullOrWhiteSpace(b.TaxId) ? "" : E(b.TaxId.StartsWith("TIN", StringComparison.OrdinalIgnoreCase) ? b.TaxId : "TIN " + b.TaxId) + ".")}</div>
            </td></tr></table>
            </body></html>
            """;
    }
}
