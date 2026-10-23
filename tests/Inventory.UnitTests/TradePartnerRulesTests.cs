using Inventory.Application.Common;
using Inventory.Application.Partners;
using Inventory.Infrastructure.Partners;

namespace Inventory.UnitTests;

/// <summary>The checks on a trade application that need no database: field rules, file sniffing and registry name matching.</summary>
public class TradePartnerRulesTests
{
    private static PartnerApplication Good() =>
        new("Paws Pet Shop", "Bola Ade", "08031234567", "bola@example.com", "12 Allen Avenue, Ikeja", "BN 1234567", "1234567890", "We sell dog and cat food in Ikeja, about 50 bags a month.", "wholesaler");

    [Fact]
    public void A_good_application_is_tidied()
    {
        var a = TradePartnerService.Clean(Good());
        Assert.Equal("2348031234567", a.Phone);
        Assert.Equal("BN1234567", a.CacNumber);
        Assert.Equal("Wholesaler", a.RequestedTier);
    }

    [Theory]
    [InlineData("CacNumber", "hello")]
    [InlineData("TaxId", "")]
    [InlineData("TaxId", "12-34")]
    [InlineData("Email", "not-an-email")]
    [InlineData("Phone", "123")]
    [InlineData("Description", "pet shop")]
    [InlineData("RequestedTier", "Retailer")]
    public void Bad_fields_are_refused(string field, string value)
    {
        var a = Good();
        a = field switch
        {
            "CacNumber" => a with { CacNumber = value }, "TaxId" => a with { TaxId = value }, "Email" => a with { Email = value },
            "Phone" => a with { Phone = value }, "Description" => a with { Description = value }, _ => a with { RequestedTier = value },
        };
        Assert.Throws<BusinessRuleException>(() => TradePartnerService.Clean(a));
    }

    [Fact]
    public void A_file_must_be_what_it_claims_to_be()
    {
        Assert.True(TradePartnerService.MagicMatches([0x25, 0x50, 0x44, 0x46, 0x2D], "application/pdf"));
        Assert.True(TradePartnerService.MagicMatches([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg"));
        Assert.False(TradePartnerService.MagicMatches([0x4D, 0x5A, 0x90, 0x00, 0x03], "application/pdf"));   // a Windows program renamed .pdf
        Assert.False(TradePartnerService.MagicMatches([0x3C, 0x73, 0x76, 0x67], "image/png"));                 // an SVG (can carry script) is not a PNG
    }

    [Theory]
    [InlineData("PAWS & CLAWS PET SUPPLIES LIMITED", "Paws and Claws Pet Supplies Ltd", true)]
    [InlineData("CHEWY PETS FARM AND FEEDS COMPANY LTD", "Chewy Pets Farm & Feeds", true)]
    [InlineData("GOLDEN TRUST VENTURES LIMITED", "Paws Pet Shop", false)]
    public void Registry_names_are_matched_on_the_words_that_matter(string registered, string given, bool same) =>
        Assert.Equal(same, DojahVerifier.SameName(registered, given));
}
