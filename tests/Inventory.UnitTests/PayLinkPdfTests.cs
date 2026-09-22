using Inventory.Application.Documents;
using Inventory.Infrastructure.Documents;
using UglyToad.PdfPig;

namespace Inventory.UnitTests;

public class PayLinkPdfTests
{
    private const string Url = "https://checkout.paystack.com/abc123xyz";
    private static Branding Brand => new("candid", "Candid Purrfect Ltd", "5 Cat Street, Lekki", "0801 111 2222", "hello@candid.example", "TIN-9", null, null, null, [new BankInfo("Sterling", "Candid", "0097166161")]);
    private static PartyInfo Cust => new("PetMart Lagos", "Amaka", "Ikoyi", "0803", "buyer@petmart.example", "", "Silver");
    private static DocLine[] Lines => [new(1, "S1", "Cat Litter 5kg", "Bag", 2, 4500m, 9000m)];

    private static QuotationDoc Quote(string? url) => new(Brand, "Q-1", new DateOnly(2026, 9, 20), "Open", Cust, "Wholesaler", "Josiah", Lines, 9000m, 0, 0, 7.5m, 675m, 9675m, new DateTime(2026, 9, 20), url);
    private static ReceiptDoc Receipt(string? url, decimal paid) => new(Brand, "INV-1", new DateOnly(2026, 9, 20), "Partial", null, Cust, "Wholesaler", "Main", "Credit", "Josiah", Lines, 9000m, 0, 0, 0, 0, 9000m, paid, 0, 0, new DateTime(2026, 9, 20), url);

    private static (string Text, string[] Links) Read(byte[] pdf)
    {
        using var d = PdfDocument.Open(pdf);
        var page = d.GetPages().First();
        return (string.Concat(page.Text.Where(c => !char.IsWhiteSpace(c))), page.GetHyperlinks().Select(h => h.Uri ?? "").ToArray());
    }

    [Fact]
    public void A_quotation_carries_a_clickable_pay_button_for_the_exact_total()
    {
        var (text, links) = Read(new QuestDocumentRenderer().Quotation(Quote(Url)));
        Assert.Contains("PAY₦9,675.00ONLINE", text);
        Assert.Contains(Url, links);
    }

    [Fact]
    public void An_invoice_button_shows_only_what_is_still_owed()
    {
        var (text, links) = Read(new QuestDocumentRenderer().Receipt(Receipt(Url, paid: 4000m)));
        Assert.Contains("PAY₦5,000.00ONLINE", text);          // 9,000 − 4,000
        Assert.Contains(Url, links);
    }

    [Fact]
    public void A_fully_paid_invoice_and_a_document_without_a_link_show_no_button()
    {
        Assert.DoesNotContain("ONLINE", Read(new QuestDocumentRenderer().Receipt(Receipt(Url, paid: 9000m))).Text);
        var (text, links) = Read(new QuestDocumentRenderer().Quotation(Quote(null)));
        Assert.DoesNotContain("ONLINE", text);
        Assert.Empty(links);
    }

    [Fact]
    public void Only_https_links_are_ever_printed()
    {
        Assert.DoesNotContain("ONLINE", Read(new QuestDocumentRenderer().Quotation(Quote("javascript:alert(1)"))).Text);
        Assert.DoesNotContain("ONLINE", Read(new QuestDocumentRenderer().Quotation(Quote("http://insecure.example/pay"))).Text);
    }

    [Fact]
    public void The_pay_button_does_not_push_the_document_onto_a_second_page()
    {
        using var d = PdfDocument.Open(new QuestDocumentRenderer().Quotation(Quote(Url)));
        Assert.Equal(1, d.NumberOfPages);
    }
}
