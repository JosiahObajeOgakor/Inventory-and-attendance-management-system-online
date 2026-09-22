using Inventory.Application.Documents;
using Inventory.Application.Email;

namespace Inventory.UnitTests;

public class EmailTemplateTests
{
    private static Branding Brand(string key = "candid") => new(key, "Candid Purrfect Ltd", "5 Cat Street, Lekki, Lagos", "0801 111 2222", "hello@candid.example", "TIN-99",
        null, null, null, [new BankInfo("Sterling Bank PLC", "Candid Purrfect", "0097166161"), new BankInfo("First Bank PLC", "Candid Purrfect", "2045958847")]);

    private static QuotationDoc Quote(string customer = "Amaka Obi", int lines = 3) => new(Brand(), "CandidPurrfect-20092026-101500", new DateOnly(2026, 9, 20), "Open",
        new PartyInfo(customer, "", "", "", "amaka@petmart.example", "", "Silver"), "Wholesaler", "Josiah",
        Enumerable.Range(1, lines).Select(i => new DocLine(i, $"SKU-{i}", $"Cat Litter {i}kg", "Bag", 2 * i, 4500m, 9000m * i)).ToList(),
        9000m * lines, 5, 450m, 7.5m, 640.5m, 9000m * lines - 450m + 640.5m, DateTime.UtcNow);

    private static PriceListDoc List(int n = 14) => new(Brand(), "Wholesaler", "Amaka Obi", "PL-20092026", new DateOnly(2026, 9, 20),
        Enumerable.Range(1, n).Select(i => new PriceListLine("Litter", $"Cat Litter {i}kg", $"S{i}", "Bag", 4500m + i, 10)).ToList(), DateTime.UtcNow);

    [Fact]
    public void Quotation_email_carries_number_total_banks_and_greets_by_first_name()
    {
        var html = EmailTemplates.Quotation(Quote(), null, "Josiah", hasLogo: false);
        Assert.Contains("Dear Amaka,", html);
        Assert.Contains("CandidPurrfect-20092026-101500", html);
        Assert.Contains("0097166161", html);
        Assert.Contains("₦", html);
        Assert.Contains("#6a2c5b", html);   // Candid's plum
        Assert.DoesNotContain("cid:company-logo", html);
    }

    [Fact]
    public void Logo_is_referenced_by_content_id_only_when_there_is_one()
    {
        Assert.Contains("cid:company-logo", EmailTemplates.Quotation(Quote(), null, "Josiah", hasLogo: true));
    }

    [Fact]
    public void Text_from_the_database_or_the_sender_cannot_inject_markup()
    {
        var html = EmailTemplates.Quotation(Quote("<script>alert(1)</script> Bob"), "Call me <b>now</b>\nthanks", "Jo<i>si</i>ah", hasLogo: false);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>now</b>", html);
        Assert.DoesNotContain("<i>si</i>", html);
        Assert.Contains("&lt;b&gt;now&lt;/b&gt;<br>thanks", html);
    }

    [Fact]
    public void A_long_quotation_shows_eight_lines_and_points_to_the_pdf_for_the_rest()
    {
        var html = EmailTemplates.Quotation(Quote(lines: 12), null, "Josiah", false);
        Assert.Contains("and 4 more item(s) in the attached PDF", html);
    }

    [Fact]
    public void Subjects_cannot_carry_line_breaks()
    {
        var q = Quote() with { Number = "A\r\nBcc: evil@x.example" };
        Assert.DoesNotContain("\n", EmailTemplates.QuotationSubject(q));
        Assert.DoesNotContain("\r", EmailTemplates.QuotationSubject(q));
    }

    [Fact]
    public void Price_list_email_shows_ten_products_and_the_tier()
    {
        var html = EmailTemplates.PriceList(List(), "Valid until month end", "Josiah", false);
        Assert.Contains("Wholesaler", html);
        Assert.Contains("4 more product(s)", html);
        Assert.Contains("Valid until month end", html);
        Assert.Contains("Dear Amaka,", html);
    }

    [Fact]
    public void Plain_text_alternative_is_present_for_clients_that_block_html()
    {
        var t = EmailTemplates.QuotationText(Quote(), "Thanks", "Josiah");
        Assert.Contains("Dear Amaka", t); Assert.Contains("attached as a PDF", t);
    }

    [Fact]
    public void Design_preview_is_written_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("INVENTORY_PREVIEW_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "email-quotation.html"), EmailTemplates.Quotation(Quote(), "Delivery is free within Lekki. Let me know if you'd like changes.", "Josiah Obaje", false));
        File.WriteAllText(Path.Combine(dir, "email-pricelist.html"), EmailTemplates.PriceList(List(), null, "Josiah Obaje", false));
    }
}
