using Inventory.Infrastructure.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Inventory.UnitTests;

/// <summary>Which WhatsApp number a business sends through: its own, else a working number shared with it.</summary>
public class WhatsAppNumberTests
{
    private static MetaWhatsAppClient Client(params WhatsAppNumberConfig[] numbers) =>
        new(new HttpClient(), Options.Create(new WhatsAppOptions { Numbers = [.. numbers] }), NullLogger<MetaWhatsAppClient>.Instance);

    [Fact]
    public void A_business_without_a_number_sends_through_the_shared_one_and_its_own_number_wins_once_it_has_one()
    {
        var chewy = new WhatsAppNumberConfig { CompanyKey = "chewypets", PhoneNumberId = "1", AccessToken = "t", SharedWith = "*" };
        var c = Client(chewy);
        Assert.True(c.IsConfigured("candid"));
        Assert.Same(chewy, c.ForCompany("candid"));

        var candid = new WhatsAppNumberConfig { CompanyKey = "candid", PhoneNumberId = "2", AccessToken = "u" };
        Assert.Same(candid, Client(chewy, candid).ForCompany("candid"));
    }

    [Fact]
    public void Sharing_is_opt_in_and_never_through_a_number_without_credentials()
    {
        Assert.False(Client(new WhatsAppNumberConfig { CompanyKey = "chewypets", AccessToken = "t" }).IsConfigured("candid"));
        Assert.False(Client(new WhatsAppNumberConfig { SharedWith = "*" }).IsConfigured("candid"));       // the appsettings default with no number behind it
        var listed = Client(new WhatsAppNumberConfig { CompanyKey = "chewypets", AccessToken = "t", SharedWith = "candid" });
        Assert.True(listed.IsConfigured("candid"));
        Assert.False(listed.IsConfigured("k9cravings"));
    }
}
