using Inventory.Application.Documents;
using Inventory.Infrastructure.Documents;
using UglyToad.PdfPig;

namespace Inventory.UnitTests;

public class DocumentRenderTests
{
    private static string? RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inventory.sln"))) dir = dir.Parent;
        return dir is null ? null : Path.Combine([dir.FullName, .. parts]);
    }

    private static byte[]? Asset(string name) => RepoFile("Assets", name) is { } p && File.Exists(p) ? File.ReadAllBytes(p) : null;

    private static Branding Brand(string key = "chewypets") => new(key, "ChewyPets Farm & Feeds Company Ltd", "12 Feed Mill Road, Ikeja, Lagos", "0803 000 0000", "sales@chewypets.example", "TIN-1234567",
        Asset("chewypetfeedslogo.jpeg"), Asset("signature.jpeg"), Asset("waybillrecipt for chewypet.jpeg"),
        [new BankInfo("FirstMonie Bank", "ChewyPets Farm & Feeds", "8676752988"), new BankInfo("First Bank PLC", "ChewyPets Farm & Feeds", "2047950632")]);

    private static PartyInfo Customer => new("PetMart Lagos", "Amaka Obi", "22 Awolowo Rd, Ikoyi, Lagos", "0803 555 2210", "buyer@petmart.example", "TIN-5561200", "Distributor");

    private static ReceiptDoc Receipt(int lines = 3, decimal owedElsewhere = 0, string servedBy = "ChewyPets Farm & Feeds Company Ltd (Clerk)", decimal discount = 0, decimal vat = 0) =>
        new(Brand(), "ChewyStock-19092026-143205", new DateOnly(2026, 9, 19), "Partial", new DateOnly(2026, 10, 3), Customer, "Distributor", "Lawal warehouse", "Bank Transfer", servedBy,
            Enumerable.Range(1, lines).Select(i => new DocLine(i, $"SKU-{i}", $"Adult Dog Food {i * 5}kg", "Bag", 2 * i, 10500m, 21000m * i)).ToList(),
            Subtotal: 21000m * lines, DiscountPct: discount > 0 ? 5 : 0, DiscountAmount: discount, VatRate: vat > 0 ? 7.5m : 0, VatAmount: vat,
            Total: 21000m * lines - discount + vat, Paid: 10000m, OwedElsewhere: owedElsewhere, RebateAvailable: 1250m, GeneratedAt: new DateTime(2026, 9, 19, 14, 32, 5));

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => p.Text));
    }

    // PdfPig reports word gaps loosely, so compare with all whitespace removed.
    private static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
    private static void Has(string text, string expected) => Assert.Contains(Squash(expected), Squash(text));
    private static void Lacks(string text, string expected) => Assert.DoesNotContain(Squash(expected), Squash(text));

    private static int Pages(byte[] pdf) { using var d = PdfDocument.Open(pdf); return d.NumberOfPages; }

    [Fact]
    public void Receipt_is_a_pdf_with_the_company_details_lines_totals_banks_and_barcode_caption()
    {
        var pdf = new QuestDocumentRenderer().Receipt(Receipt(owedElsewhere: 5000m, discount: 1050m, vat: 1421.25m));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var t = Text(pdf);
        Has(t, "SALES RECEIPT");
        Has(t, "ChewyPets Farm & Feeds Company Ltd");
        Has(t, "ChewyStock-19092026-143205");
        Has(t, "Adult Dog Food 10kg");
        Has(t, "8676752988");                       // bank details are on every receipt
        Has(t, "₦63,000.00");                        // subtotal
        Has(t, "Discount (5%)");
        Has(t, "VAT (7.5%)");
        Has(t, "TOTAL NOW OWED (all invoices)");     // earlier debt is never buried
        Has(t, "Scan to look this receipt up");
        Has(t, "(Clerk)");
    }

    [Fact]
    public void Receipt_omits_discount_vat_and_earlier_debt_when_there_is_none()
    {
        var t = Text(new QuestDocumentRenderer().Receipt(Receipt()));
        Lacks(t, "Discount (");
        Lacks(t, "VAT (");
        Lacks(t, "TOTAL NOW OWED");
    }

    [Fact]
    public void A_receipt_with_many_lines_still_fits_on_one_page()
    {
        Assert.Equal(1, Pages(new QuestDocumentRenderer().Receipt(Receipt(lines: 45))));
    }

    [Fact]
    public void Receipt_still_renders_when_the_company_has_no_images_configured()
    {
        var bare = Receipt() with { Brand = Brand() with { Logo = null, Signature = null, WaybillStamp = null } };
        Has(Text(new QuestDocumentRenderer().Receipt(bare)), "SALES RECEIPT");
    }

    [Fact]
    public void A_corrupt_logo_never_costs_us_the_document()
    {
        var bad = Receipt() with { Brand = Brand() with { Logo = [1, 2, 3, 4] } };
        Has(Text(new QuestDocumentRenderer().Receipt(bad)), "SALES RECEIPT");
    }

    [Fact]
    public void Waybill_carries_carrier_details_goods_and_the_three_signature_boxes()
    {
        var w = new WaybillDoc(Brand(), "ChewyStock-19092026-150001", new DateOnly(2026, 9, 19), "ChewyStock-19092026-143205", new DateOnly(2026, 9, 19), Customer,
            "22 Awolowo Rd, Ikoyi", "Lawal warehouse, Lawal site", "ChewyPets Farm & Feeds Company Ltd (Clerk)", "Musa Ibrahim", "0801 234 5678", "LSD 123 AB", "Deliver before noon",
            [new DocLine(1, "SKU-1001", "Adult Dog Food 20kg", "Bag", 40, 0, 0)], new DateTime(2026, 9, 19, 15, 0, 0));
        var t = Text(new QuestDocumentRenderer().Waybill(w));
        Has(t, "WAYBILL / DELIVERY NOTE");
        Has(t, "Musa Ibrahim");
        Has(t, "LSD 123 AB");
        Has(t, "Dispatched by");
        Has(t, "Received by (customer)");
        Has(t, "Deliver before noon");
    }

    [Fact]
    public void Quotation_says_plainly_that_it_is_not_an_invoice()
    {
        var q = new QuotationDoc(Brand(), "ChewyStock-19092026-120000", new DateOnly(2026, 9, 19), "Open", Customer, "Wholesaler", "Ifeoma Chukwu",
            [new DocLine(1, "SKU-1", "Puppy Starter 10kg", "Bag", 10, 8500m, 85000m)], 85000m, 0, 0, 0, 0, 85000m, new DateTime(2026, 9, 19, 12, 0, 0));
        var t = Text(new QuestDocumentRenderer().Quotation(q));
        Has(t, "QUOTATION");
        Has(t, "not an invoice");
        Has(t, "₦85,000.00");
    }

    [Fact]
    public void Price_list_groups_by_category()
    {
        var p = new PriceListDoc(Brand("candid"), "Wholesaler", "PetMart", "PL-20260919", new DateOnly(2026, 9, 19),
            [new("Dog Food", "Adult Dog Food 20kg", "SKU-1", "Bag", 11000m, 40), new("Cat Food", "Kitten Formula 3kg", "SKU-5", "Bag", 3900m, 12)], new DateTime(2026, 9, 19, 9, 0, 0));
        var t = Text(new QuestDocumentRenderer().PriceList(p));
        Has(t, "PRICE LIST");
        Has(t, "DOG FOOD");
        Has(t, "CAT FOOD");
        Has(t, "Prepared for");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("ChewyStock-19092026-143205")]
    [InlineData("CandidPurrfect-14092026-143205")]
    public void Code128_symbol_has_the_right_module_count(string value)
    {
        // start (11) + one symbol per character (11 each) + checksum (11) + stop (13)
        Assert.Equal(11 * (value.Length + 2) + 13, Code128.Widths(value).Sum());
        Assert.Equal(0, Code128.Widths(value).Count % 2 == 1 ? 0 : 0);   // bars and spaces alternate, ending on the stop bar
    }

    [Fact]
    public void Code128_refuses_characters_it_cannot_carry()
    {
        Assert.False(Code128.CanEncode("Café"));
        Assert.Throws<ArgumentException>(() => Code128.Widths("₦"));
    }

    [Fact]
    public void Design_review_images_are_written_when_a_preview_folder_is_given()
    {
        var dir = Environment.GetEnvironmentVariable("INVENTORY_PREVIEW_DIR");
        if (string.IsNullOrEmpty(dir)) return;   // opt-in: `INVENTORY_PREVIEW_DIR=... dotnet test`
        Directory.CreateDirectory(dir);
        var r = new QuestDocumentRenderer();
        File.WriteAllBytes(Path.Combine(dir, "receipt.png"), r.ReceiptImages(Receipt(owedElsewhere: 5000m, discount: 1050m, vat: 1421.25m))[0]);
        var w = new WaybillDoc(Brand(), "ChewyStock-19092026-150001", new DateOnly(2026, 9, 19), "ChewyStock-19092026-143205", new DateOnly(2026, 9, 19), Customer,
            "22 Awolowo Rd, Ikoyi", "Lawal warehouse, Lawal site", "ChewyPets Farm & Feeds Company Ltd (Clerk)", "Musa Ibrahim", "0801 234 5678", "LSD 123 AB", "Deliver before noon",
            [new DocLine(1, "SKU-1001", "Adult Dog Food 20kg", "Bag", 40, 0, 0), new DocLine(2, "SKU-1002", "Puppy Starter 10kg", "Bag", 12, 0, 0)], new DateTime(2026, 9, 19, 15, 0, 0));
        File.WriteAllBytes(Path.Combine(dir, "waybill.png"), r.WaybillImages(w)[0]);
    }
}
