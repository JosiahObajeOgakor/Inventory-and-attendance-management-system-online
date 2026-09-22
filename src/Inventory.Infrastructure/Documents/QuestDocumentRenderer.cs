using System.Globalization;
using System.Reflection;
using Inventory.Application.Documents;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// PDF versions of the desktop app's printed documents (DocPrinter.vb): letterhead with logo and company details, side-by-side
/// panels, numbered item table, totals, bank details, Code 128 barcode, stamp, and the faint tiled-logo watermark. Always A4;
/// a document that is too long for one page scales down to fit, exactly like the desktop "FitToOnePage".
/// QuestPDF Community licence applies (free for businesses under USD 1M annual gross revenue) — see docs/DEPLOY notes.
/// </summary>
public sealed class QuestDocumentRenderer : IDocumentRenderer
{
    private const string Face = "Noto Sans";
    private static readonly string Ink = "#16202A", Muted = "#56636C", Hairline = "#D3DAD5", Danger = "#C4372C";
    private static readonly object Gate = new();
    private static bool _ready;

    private static void Init()
    {
        lock (Gate)
        {
            if (_ready) return;
            QuestPDF.Settings.License = LicenseType.Community;
            var asm = typeof(QuestDocumentRenderer).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                FontManager.RegisterFont(s);
            }
            _ready = true;
        }
    }

    private static string Accent(string companyKey) => companyKey == "candid" ? "#6A2C5B" : "#1F6B4F";
    private static string N2(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    private static string Money(decimal v) => "₦" + N2(v);
    private static string D(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static IDocument Compose(Action<IDocumentContainer> compose)
    {
        Init();
        return Document.Create(compose);
    }

    /// <summary>An image that cannot be decoded is dropped: a bad logo must never cost us the receipt.</summary>
    private static byte[]? Usable(byte[]? image)
    {
        if (image is not { Length: > 0 }) return null;
        try { using var bmp = SKBitmap.Decode(image); return bmp is null ? null : image; }
        catch { return null; }
    }

    private static Branding Clean(Branding b) => b with { Logo = Usable(b.Logo), Signature = Usable(b.Signature), WaybillStamp = Usable(b.WaybillStamp) };

    public byte[] Receipt(ReceiptDoc d) => ReceiptDocument(d with { Brand = Clean(d.Brand) }).GeneratePdf();
    public byte[] Quotation(QuotationDoc d) => QuotationDocument(d with { Brand = Clean(d.Brand) }).GeneratePdf();
    public byte[] Waybill(WaybillDoc d) => WaybillDocument(d with { Brand = Clean(d.Brand) }).GeneratePdf();
    public byte[] PriceList(PriceListDoc d) => PriceListDocument(d with { Brand = Clean(d.Brand) }).GeneratePdf();

    // Page images (PNG) for design review and tests: the same documents, rasterised.
    private static readonly ImageGenerationSettings Preview = new() { RasterDpi = 110 };
    public IReadOnlyList<byte[]> ReceiptImages(ReceiptDoc d) => ReceiptDocument(d).GenerateImages(Preview).ToList();
    public IReadOnlyList<byte[]> QuotationImages(QuotationDoc d) => QuotationDocument(d).GenerateImages(Preview).ToList();
    public IReadOnlyList<byte[]> WaybillImages(WaybillDoc d) => WaybillDocument(d).GenerateImages(Preview).ToList();
    public IReadOnlyList<byte[]> PriceListImages(PriceListDoc d) => PriceListDocument(d).GenerateImages(Preview).ToList();

    // ------------------------------------------------------------------------------------------------ receipt
    private static IDocument ReceiptDocument(ReceiptDoc r) => Compose(doc => doc.Page(page =>
    {
        var accent = Accent(r.Brand.CompanyKey);
        Frame(page, r.Brand, $"{r.Brand.Name}   ·   Receipt {r.Number}   ·   computer-generated {r.GeneratedAt:dd MMM yyyy HH:mm}");
        page.Content().ScaleToFit().Column(col =>
        {
            Letterhead(col, r.Brand, accent, "SALES RECEIPT", [("Invoice no.", r.Number), ("Date", D(r.Date)), ("Status", r.Status.ToUpperInvariant())]);
            Panels(col, accent,
                ("Bill to", [("Customer", r.Customer.Name, true), ("Contact", r.Customer.Contact, false), ("Address", r.Customer.Address, false), ("Phone", r.Customer.Phone, false),
                             ("Email", r.Customer.Email, false), ("Tax ID", r.Customer.TaxId, false), ("Ranking", r.Customer.Ranking, false)]),
                ("Sale details", [("Price tier", r.PriceTier, false), ("Warehouse", r.Warehouse, false), ("Payment", r.PaymentMethod, false), ("Served by", r.ServedBy, false),
                                  ("Due date", r.DueDate is { } dd ? D(dd) : "", true)]));

            ItemTable(col, accent, r.Lines);

            var totals = new List<(string, string, int)> { ("Subtotal", Money(r.Subtotal), 0) };
            if (r.DiscountAmount > 0) totals.Add(($"Discount ({r.DiscountPct:0.##}%)", "−" + Money(r.DiscountAmount), 0));
            if (r.VatAmount > 0) totals.Add(($"VAT ({r.VatRate:0.##}%)", Money(r.VatAmount), 0));   // only when actually charged
            totals.Add(("TOTAL", Money(r.Total), 1));
            totals.Add(("Amount paid", Money(r.Paid), 0));
            totals.Add(("Balance due (this invoice)", Money(r.BalanceDue), r.BalanceDue > 0 ? 2 : 0));
            if (r.OwedElsewhere > 0)
            {
                totals.Add(("Owed from earlier purchases", Money(r.OwedElsewhere), 2));
                totals.Add(("TOTAL NOW OWED (all invoices)", Money(r.BalanceDue + r.OwedElsewhere), 2));
            }
            var units = r.Lines.Sum(l => l.Qty);
            var notes = new List<(string, bool)>
            {
                ($"{r.Lines.Count} product line(s) · {units:N0} unit(s)", false),
                ($"Paid by {r.PaymentMethod} · status {r.Status}", false),
            };
            if (r.DueDate is { } due && r.BalanceDue > 0) notes.Add(($"Balance to be paid by {D(due)}", true));
            if (r.OwedElsewhere > 0) notes.Add(($"This customer also owes {Money(r.OwedElsewhere)} from earlier purchases — please collect the full amount owed where possible.", true));
            notes.Add(($"Rebate balance with us: {Money(r.RebateAvailable)} (redeemable as goods)", false));
            Totals(col, accent, totals, notes);
            if (r.BalanceDue > 0) PayBlock(col, accent, r.PayUrl, r.BalanceDue);

            BankTable(col, accent, r.Brand.Banks);
            Barcode(col, r.Number, "Scan to look this receipt up  ·  " + r.Number);
            StampBlock(col, r.Brand.Signature, "Authorised signature & company stamp");
            col.Item().PaddingTop(8).AlignCenter().Text("Thanks for your patronage.").FontSize(9).FontColor(Muted).Italic();
        });
    }));

    // ------------------------------------------------------------------------------------------------ quotation
    private static IDocument QuotationDocument(QuotationDoc q) => Compose(doc => doc.Page(page =>
    {
        var accent = Accent(q.Brand.CompanyKey);
        Frame(page, q.Brand, $"{q.Brand.Name}   ·   Quotation {q.Number}   ·   computer-generated {q.GeneratedAt:dd MMM yyyy HH:mm}");
        page.Content().ScaleToFit().Column(col =>
        {
            Letterhead(col, q.Brand, accent, "QUOTATION", [("Quotation no.", q.Number), ("Date", D(q.Date)), ("Status", q.Status.ToUpperInvariant())]);
            Panels(col, accent,
                ("Quoted to", [("Customer", q.Customer.Name, true), ("Contact", q.Customer.Contact, false), ("Address", q.Customer.Address, false), ("Phone", q.Customer.Phone, false),
                               ("Email", q.Customer.Email, false), ("Ranking", q.Customer.Ranking, false)]),
                ("Quote details", [("Price tier", q.PriceTier, false), ("Prepared by", q.PreparedBy, false)]));
            ItemTable(col, accent, q.Lines);
            var totals = new List<(string, string, int)> { ("Subtotal", Money(q.Subtotal), 0) };
            if (q.DiscountAmount > 0) totals.Add(($"Discount ({q.DiscountPct:0.##}%)", "−" + Money(q.DiscountAmount), 0));
            if (q.VatAmount > 0) totals.Add(($"VAT ({q.VatRate:0.##}%)", Money(q.VatAmount), 0));
            totals.Add(("TOTAL (quoted)", Money(q.Total), 1));
            Totals(col, accent, totals, [($"{q.Lines.Count} product line(s)", false),
                ("This is a price quotation, not an invoice — no stock has been reserved and nothing is owed until it's converted to a sale.", true)]);
            PayBlock(col, accent, q.PayUrl, q.Total);
            Barcode(col, q.Number, "Scan to look this quotation up  ·  " + q.Number);
            col.Item().PaddingTop(8).AlignCenter().Text("Thanks for your interest — let us know if you'd like to go ahead.").FontSize(9).FontColor(Muted).Italic();
        });
    }));

    // ------------------------------------------------------------------------------------------------ waybill
    private static IDocument WaybillDocument(WaybillDoc w) => Compose(doc => doc.Page(page =>
    {
        var accent = Accent(w.Brand.CompanyKey);
        Frame(page, w.Brand, $"{w.Brand.Name}   ·   Waybill {w.Number}   ·   computer-generated {w.GeneratedAt:dd MMM yyyy HH:mm}");
        page.Content().ScaleToFit().Column(col =>
        {
            Letterhead(col, w.Brand, accent, "WAYBILL / DELIVERY NOTE",
                [("Waybill no.", w.Number), ("Date issued", D(w.IssueDate)), ("Invoice no.", w.InvoiceNumber), ("Invoice date", D(w.InvoiceDate))]);
            var units = w.Lines.Sum(l => l.Qty);
            Panels(col, accent,
                ("Deliver to", [("Customer", w.Customer.Name, true), ("Contact", w.Customer.Contact, false), ("Phone", w.Customer.Phone, false), ("Address", w.DestinationAddress, false)]),
                ("Dispatch", [("From", w.FromWarehouse, true), ("Issued by", w.IssuedBy, false), ("Lines", w.Lines.Count.ToString(), false), ("Total units", units.ToString("N0"), false)]),
                ("Carrier", [("Driver", w.DriverName, true), ("Phone", w.DriverPhone, false), ("Vehicle", w.VehiclePlate, true)]));

            col.Item().PaddingTop(4).Text("GOODS DISPATCHED").Bold().FontSize(8.5f).FontColor(accent);
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(c => { c.ConstantColumn(26); c.ConstantColumn(70); c.RelativeColumn(); c.ConstantColumn(50); c.ConstantColumn(46); c.ConstantColumn(62); });
                foreach (var h in new[] { "#", "SKU", "Description", "Unit", "Qty", "Received" }) { var hc = t.Cell().Element(HeadCell(accent)); (h == "Qty" ? hc.AlignRight() : hc).Text(h).Bold().FontSize(8.5f); }
                foreach (var l in w.Lines)
                {
                    t.Cell().Element(BodyCell).Text(l.No.ToString());
                    t.Cell().Element(BodyCell).Text(l.Sku);
                    t.Cell().Element(BodyCell).Text(l.Description);
                    t.Cell().Element(BodyCell).Text(l.Unit);
                    t.Cell().Element(BodyCell).AlignRight().Text(l.Qty.ToString("N0"));
                    t.Cell().Element(BodyCell).Text("");
                }
                t.Cell().ColumnSpan(4).Element(FootCell).Text($"Total — {w.Lines.Count} line(s)").Bold();
                t.Cell().Element(FootCell).AlignRight().Text(units.ToString("N0")).Bold();
                t.Cell().Element(FootCell).Text("");
            });
            if (!string.IsNullOrWhiteSpace(w.Notes)) { col.Item().PaddingTop(8).Text("NOTES").Bold().FontSize(8.5f).FontColor(accent); col.Item().Text(w.Notes).FontSize(9); }
            Barcode(col, w.Number, "Scan to look this delivery up  ·  " + w.Number);

            // "Dispatched by" is stamped by the business itself; the driver and the receiving customer sign for themselves.
            col.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Element(c => SignatureBox(c, "Dispatched by", w.Brand.WaybillStamp));
                row.ConstantItem(16);
                row.RelativeItem().Element(c => SignatureBox(c, "Driver", null));
                row.ConstantItem(16);
                row.RelativeItem().Element(c => SignatureBox(c, "Received by (customer)", null));
            });
            col.Item().PaddingTop(4).Text("Received the goods listed above in good condition and complete.").FontSize(8).FontColor(Muted);
            col.Item().PaddingTop(6).AlignCenter().Text("Thanks for your patronage.").FontSize(9).FontColor(Muted).Italic();
        });
    }));

    // ------------------------------------------------------------------------------------------------ price list
    private static IDocument PriceListDocument(PriceListDoc p) => Compose(doc => doc.Page(page =>
    {
        var accent = Accent(p.Brand.CompanyKey);
        Frame(page, p.Brand, $"{p.Brand.Name}   ·   Price list {p.Reference}   ·   generated {p.GeneratedAt:dd MMM yyyy HH:mm}");
        page.Content().Column(col =>
        {
            var meta = new List<(string K, string V)> { ("Reference", p.Reference), ("Date", D(p.Date)), ("Price tier", p.Tier) };
            if (!string.IsNullOrWhiteSpace(p.CustomerName)) meta.Add(("Prepared for", p.CustomerName!));
            Letterhead(col, p.Brand, accent, "PRICE LIST", meta.ToArray());
            foreach (var g in p.Lines.GroupBy(l => l.Category))
            {
                col.Item().PaddingTop(8).Text(g.Key.ToUpperInvariant()).Bold().FontSize(8.5f).FontColor(accent);
                col.Item().PaddingTop(2).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(80); c.ConstantColumn(50); c.ConstantColumn(90); });
                    foreach (var h in new[] { "Product", "SKU", "Unit", "Price (₦)" }) t.Cell().Element(HeadCell(accent)).Text(h).Bold().FontSize(8.5f);
                    foreach (var l in g)
                    {
                        t.Cell().Element(BodyCell).Text(l.Product);
                        t.Cell().Element(BodyCell).Text(l.Sku);
                        t.Cell().Element(BodyCell).Text(l.Unit);
                        t.Cell().Element(BodyCell).AlignRight().Text(N2(l.Price));
                    }
                });
            }
            col.Item().PaddingTop(10).Text("Prices are in naira and exclude VAT unless stated. Availability subject to stock.").FontSize(8).FontColor(Muted);
            BankTable(col, accent, p.Brand.Banks);
        });
    }));

    // ------------------------------------------------------------------------------------------------ shelf label
    public byte[] ShelfLabel(Branding brand, string productName, string sku, string barcode, decimal price)
    {
        Init();
        var accent = Accent(brand.CompanyKey);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(new PageSize(260, 150));
            page.Margin(10);
            page.DefaultTextStyle(x => x.FontFamily(Face).FontSize(9));
            page.Content().Column(col =>
            {
                col.Item().Text(productName).Bold().FontSize(11);
                col.Item().Text($"{sku}   ·   {Money(price)}").FontColor(accent).Bold();
                col.Item().PaddingTop(4).Element(c => DrawCode128(c, barcode, 46, 0.45f));
                col.Item().AlignCenter().Text(barcode).FontFamily(Face).FontSize(8).FontColor(Muted);
            });
        })).GeneratePdf();
    }

    // ------------------------------------------------------------------------------------------------ building blocks
    private static void Frame(PageDescriptor page, Branding brand, string footer)
    {
        page.Size(PageSizes.A4);
        page.Margin(34);
        page.DefaultTextStyle(x => x.FontFamily(Face).FontSize(9).FontColor(Ink));
        if (brand.Logo is { Length: > 0 } logo && WatermarkTile(logo) is { } tile)
            page.Background().Element(bg => bg.Unconstrained().OffsetX(-110).OffsetY(230).Rotate(-30).Column(grid =>
            {
                for (var r = 0; r < 9; r++)
                    grid.Item().Row(row => { for (var c = 0; c < 7; c++) row.ConstantItem(TilePt).Height(TilePt).Image(tile); });
            }));
        page.Footer().AlignCenter().Text(footer).FontSize(7.5f).FontColor(Muted);
    }

    private const float TilePt = 150;
    private static readonly Dictionary<int, byte[]?> TileCache = [];

    /// <summary>
    /// One watermark tile: the company logo at 7% opacity, centred on a transparent square (as DocPrinter.WatermarkTile did).
    /// The page background repeats it rotated -30 degrees. Null if the image cannot be decoded: a watermark is never worth losing a document.
    /// </summary>
    private static byte[]? WatermarkTile(byte[] logoBytes)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(logoBytes);
        lock (TileCache)
        {
            if (TileCache.TryGetValue(key, out var cached)) return cached;
            byte[]? png = null;
            try
            {
                using var logo = SKBitmap.Decode(logoBytes);
                if (logo is not null)
                {
                    const int px = 300;
                    using var tile = new SKBitmap(px, px);
                    using (var c = new SKCanvas(tile))
                    {
                        c.Clear(SKColors.Transparent);
                        var s = Math.Min(px * 0.55f / logo.Width, px * 0.55f / logo.Height);
                        var w = logo.Width * s; var h = logo.Height * s;
                        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * 0.07)), IsAntialias = true };
                        c.DrawBitmap(logo, new SKRect((px - w) / 2, (px - h) / 2, (px + w) / 2, (px + h) / 2), paint);
                    }
                    using var img = SKImage.FromBitmap(tile);
                    png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                }
            }
            catch { png = null; }
            TileCache[key] = png;
            return png;
        }
    }


    private static void Letterhead(ColumnDescriptor col, Branding b, string accent, string title, (string K, string V)[] meta)
    {
        col.Item().Row(row =>
        {
            row.RelativeItem(1.25f).Row(left =>
            {
                if (b.Logo is { Length: > 0 } logo) left.ConstantItem(90).MaxHeight(70).Image(logo).FitArea();
                left.RelativeItem().PaddingLeft(b.Logo is { Length: > 0 } ? 12 : 0).Column(c =>
                {
                    c.Item().Text(b.Name).Bold().FontSize(15).FontColor(accent);
                    foreach (var line in new[] { b.Address, b.Phone, b.Email, string.IsNullOrWhiteSpace(b.TaxId) ? "" : "Tax ID: " + b.TaxId })
                        if (!string.IsNullOrWhiteSpace(line)) c.Item().Text(line).FontSize(8.5f).FontColor(Muted);
                });
            });
            row.ConstantItem(20);
            row.RelativeItem(1.0f).Column(c =>
            {
                c.Item().AlignRight().Text(title).Bold().FontSize(14);
                c.Item().Height(4);
                foreach (var (k, v) in meta)
                    c.Item().Row(r => { r.ConstantItem(64).Text(k).FontSize(8.5f).FontColor(Muted); r.RelativeItem().AlignRight().Text(v).FontSize(8.5f).Bold(); });
            });
        });
        col.Item().PaddingTop(8).LineHorizontal(2).LineColor(accent);
        col.Item().Height(12);
    }

    private static void Panels(ColumnDescriptor col, string accent, params (string Title, (string K, string V, bool Bold)[] Rows)[] panels)
    {
        col.Item().Row(row =>
        {
            for (var i = 0; i < panels.Length; i++)
            {
                if (i > 0) row.ConstantItem(22);
                var p = panels[i];
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(p.Title.ToUpperInvariant()).Bold().FontSize(8.5f).FontColor(accent);
                    c.Item().PaddingTop(2).LineHorizontal(0.5f).LineColor(Hairline);
                    c.Item().Height(4);
                    foreach (var (k, v, bold) in p.Rows.Where(r => !string.IsNullOrWhiteSpace(r.V)))
                        c.Item().PaddingBottom(2).Row(r =>
                        {
                            r.ConstantItem(70).Text(k).FontSize(8.5f).FontColor(Muted);
                            var t = r.RelativeItem().Text(v).FontSize(9);
                            if (bold) t.Bold();
                        });
                });
            }
        });
        col.Item().Height(10);
    }

    private static Func<IContainer, IContainer> HeadCell(string accent) => c => c.BorderBottom(1.2f).BorderColor(accent).PaddingVertical(3).PaddingHorizontal(3);
    private static IContainer BodyCell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Hairline).PaddingVertical(3).PaddingHorizontal(3);
    private static IContainer FootCell(IContainer c) => c.BorderTop(1).BorderColor(Ink).PaddingVertical(3).PaddingHorizontal(3);

    private static void ItemTable(ColumnDescriptor col, string accent, IReadOnlyList<DocLine> lines)
    {
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c => { c.ConstantColumn(24); c.RelativeColumn(); c.ConstantColumn(48); c.ConstantColumn(88); c.ConstantColumn(96); });
            t.Cell().Element(HeadCell(accent)).Text("#").Bold().FontSize(8.5f);
            t.Cell().Element(HeadCell(accent)).Text("Description").Bold().FontSize(8.5f);
            t.Cell().Element(HeadCell(accent)).AlignRight().Text("Qty").Bold().FontSize(8.5f);
            t.Cell().Element(HeadCell(accent)).AlignRight().Text("Unit price (₦)").Bold().FontSize(8.5f);
            t.Cell().Element(HeadCell(accent)).AlignRight().Text("Amount (₦)").Bold().FontSize(8.5f);
            foreach (var l in lines)
            {
                t.Cell().Element(BodyCell).Text(l.No.ToString());
                t.Cell().Element(BodyCell).Text(l.Description);
                t.Cell().Element(BodyCell).AlignRight().Text(l.Qty.ToString("N0", CultureInfo.InvariantCulture));
                t.Cell().Element(BodyCell).AlignRight().Text(N2(l.Price));
                t.Cell().Element(BodyCell).AlignRight().Text(N2(l.Amount));
            }
        });
        col.Item().Height(8);
    }

    /// <param name="style">0 normal, 1 grand total, 2 attention (amount still owed)</param>
    private static void Totals(ColumnDescriptor col, string accent, List<(string K, string V, int Style)> rows, List<(string Text, bool Emphasis)> notes)
    {
        col.Item().Row(row =>
        {
            row.RelativeItem().PaddingRight(20).Column(c =>
            {
                foreach (var (text, emphasis) in notes.Where(n => !string.IsNullOrWhiteSpace(n.Text)))
                {
                    var t = c.Item().PaddingBottom(3).Text(text).FontSize(8.5f);
                    if (emphasis) t.Bold().FontColor(Danger); else t.FontColor(Muted);
                }
            });
            row.ConstantItem(270).Column(c =>
            {
                foreach (var (k, v, style) in rows)
                {
                    var item = c.Item();
                    if (style == 1) item = item.BorderTop(1.5f).BorderBottom(1.5f).BorderColor(accent).PaddingVertical(3);
                    else item = item.PaddingVertical(1.5f);
                    item.Row(r =>
                    {
                        var kt = r.RelativeItem().Text(k).FontSize(style == 1 ? 11 : 9);
                        var vt = r.ConstantItem(100).AlignRight().Text(v).FontSize(style == 1 ? 11 : 9);
                        if (style == 1) { kt.Bold(); vt.Bold(); }
                        if (style == 2) { kt.Bold().FontColor(Danger); vt.Bold().FontColor(Danger); }
                    });
                }
            });
        });
        col.Item().Height(8);
    }

    /// <summary>A clickable button in the PDF that opens the customer's Paystack payment page for this exact amount.</summary>
    private static void PayBlock(ColumnDescriptor col, string accent, string? url, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        col.Item().PaddingTop(8).Hyperlink(url).Background(accent).PaddingVertical(7).PaddingHorizontal(12).Column(c =>
        {
            c.Item().AlignCenter().Text($"PAY {Money(amount)} ONLINE — CLICK HERE").Bold().FontSize(11).FontColor("#FFFFFF");
            c.Item().AlignCenter().Text("Card, bank transfer or USSD · secured by Paystack").FontSize(8).FontColor("#FFFFFF");
        });
        col.Item().PaddingTop(2).AlignCenter().Hyperlink(url).Text(url).FontSize(6.5f).FontColor(Muted);
    }

    private static void BankTable(ColumnDescriptor col, string accent, IReadOnlyList<BankInfo> banks)
    {
        if (banks.Count == 0) return;
        col.Item().PaddingTop(4).Text("PAYMENT DETAILS — BANK TRANSFER").Bold().FontSize(8.5f).FontColor(accent);
        col.Item().PaddingTop(3).Table(t =>
        {
            t.ColumnsDefinition(c => { c.RelativeColumn(1.3f); c.RelativeColumn(1.7f); c.RelativeColumn(1.0f); });
            foreach (var h in new[] { "Bank", "Account name", "Account number" }) t.Cell().Element(HeadCell(accent)).Text(h).Bold().FontSize(8.5f);
            foreach (var b in banks)
            {
                t.Cell().Element(BodyCell).Text(b.Bank);
                t.Cell().Element(BodyCell).Text(b.AccountName);
                t.Cell().Element(BodyCell).Text(b.AccountNumber).Bold();
            }
        });
    }

    private static void Barcode(ColumnDescriptor col, string value, string caption)
    {
        if (!Code128.CanEncode(value)) return;   // a symbol is a convenience; never lose the document over one
        col.Item().PaddingTop(10).AlignCenter().Column(c =>
        {
            c.Item().Element(x => DrawCode128(x, value, 40, 0.75f));
            c.Item().AlignCenter().Text(caption).FontSize(7.5f).FontColor(Muted);
        });
    }


    /// <summary>Code 128 as an SVG drawn in points (module width x module count), so it is never stretched.</summary>
    private static void DrawCode128(IContainer container, string value, float height, float moduleWidth)
    {
        var widths = Code128.Widths(value);
        var total = Code128.TotalModules(value);
        var pts = (total * moduleWidth).ToString("0.###", CultureInfo.InvariantCulture);
        var h = height.ToString("0.###", CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{pts}\" height=\"{h}\" viewBox=\"0 0 {pts} {h}\" shape-rendering=\"crispEdges\">");
        var x = Code128.QuietModules * moduleWidth;
        var bar = true;
        foreach (var w in widths)
        {
            var wp = w * moduleWidth;
            if (bar) sb.Append(string.Create(CultureInfo.InvariantCulture, $"<rect x=\"{x:0.###}\" y=\"0\" width=\"{wp:0.###}\" height=\"{h}\" fill=\"#000\"/>"));
            x += wp;
            bar = !bar;
        }
        sb.Append("</svg>");
        container.Width(total * moduleWidth).Height(height).Svg(sb.ToString());
    }

    private static void StampBlock(ColumnDescriptor col, byte[]? stamp, string caption)
    {
        col.Item().PaddingTop(10).AlignRight().Width(300).Column(c =>
        {
            if (stamp is { Length: > 0 }) c.Item().Height(140).AlignCenter().Image(stamp).FitArea();
            else c.Item().Height(40);
            c.Item().LineHorizontal(0.7f).LineColor(Ink);
            c.Item().PaddingTop(2).AlignCenter().Text(caption).FontSize(7.5f).FontColor(Muted);
        });
    }

    private static void SignatureBox(IContainer container, string title, byte[]? stamp)
    {
        container.Column(c =>
        {
            if (stamp is { Length: > 0 }) c.Item().Height(125).AlignCenter().Image(stamp).FitArea(); else c.Item().Height(60);
            c.Item().LineHorizontal(0.7f).LineColor(Ink);
            c.Item().PaddingTop(2).Text(title).FontSize(8.5f).Bold();
            c.Item().Text("Name / signature / date").FontSize(7).FontColor(Muted);
        });
    }
}
