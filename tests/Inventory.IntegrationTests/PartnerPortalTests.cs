using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Partners;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>The trade partner's portal: wallet top-ups through AlatPay (credited once), buying at their own prices from the wallet or online, and messages.</summary>
[Collection("mysql")]
public class PartnerPortalTests(MySqlFixture mysql)
{
    private const string Email = "paws@example.com";

    private sealed record Rig(PartnerPortalService Portal, PaymentLinkService Pay, BusinessDbContext Db, FakeGateway Alat, RecordingStaffAlerts Alerts, Seed Seed, int PartnerId);

    private async Task<Rig> BuildAsync(int stock = 50)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock);
        var db = NewContext(cs);
        var customer = new Customer { Name = "Paws Pets", Phone = "2348031234567", Email = Email, CustomerType = "Wholesaler" };
        db.Customers.Add(customer); await db.SaveChangesAsync();
        var partner = new TradePartner { BusinessName = "Paws Pets", ContactName = "Bola Ade", Phone = "2348031234567", Email = Email, Address = "12 Allen Avenue, Ikeja", CacNumber = "RC123456",
            Description = "Pet shop", RequestedTier = "Wholesaler", ApprovedTier = "Wholesaler", Status = PartnerStatuses.Approved, CustomerId = customer.Id, CreatedAt = DateTime.UtcNow };
        db.TradePartners.Add(partner); await db.SaveChangesAsync();

        var clock = new SystemClock();
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", DefaultWarehouseId: seed.WarehouseId));
        var tx = Wire.Tx(db);
        var quotes = new QuotationService(db, tx, clock, co, SalesFor(db), new NoUsers());
        var alat = new FakeGateway(true, PaymentProviders.AlatPay);
        var alerts = new RecordingStaffAlerts();
        var pay = new PaymentLinkService(db, tx, clock, co, new PaymentGateways([alat]), new RecordingNotifier(), new CustomerPaymentService(db, tx, clock), quotes, null, null, alerts);
        var stockSvc = new StockAvailabilityService(db, co, clock, Options.Create(new SalesAssistantOptions()));
        var portal = new PartnerPortalService(db, clock, co, tx, quotes, new CustomerPaymentService(db, tx, clock), pay, stockSvc, new CompanyProfileService(db, co, clock), alerts);
        return new Rig(portal, pay, db, alat, alerts, seed, partner.Id);
    }

    [Fact]
    public async Task A_top_up_is_credited_once_when_AlatPay_confirms_it_however_often_it_is_told()
    {
        var r = await BuildAsync();
        var fund = await r.Portal.FundAsync(Email, 50_000, "https://site.test/?partner=paid#partner", default);
        Assert.StartsWith("https://alatpay.example/", fund.PayUrl);
        Assert.Equal(0, await r.Portal.BalanceAsync(r.PartnerId, default));                   // nothing to spend until the money is confirmed

        r.Alat.Verification = new GatewayVerification(true, 5_000_000, "NGN", "card");
        Assert.True((await r.Pay.SettleAsync(fund.Reference, default)).Applied);
        Assert.False((await r.Pay.SettleAsync(fund.Reference, default)).Applied);             // a second callback: nothing more
        r.Db.ChangeTracker.Clear();
        Assert.Equal(50_000, await r.Portal.BalanceAsync(r.PartnerId, default));
        Assert.Single(await r.Db.WalletEntries.Where(w => w.Status == WalletEntryStatuses.Confirmed).ToListAsync());
        Assert.Contains(r.Alerts.Contacts, c => c.Subject.Contains("added to your wallet"));
        Assert.Contains(r.Alerts.Staff, s => s.Text.Contains("WALLET TOP-UP CONFIRMED"));
        // asking for the same top-up again reuses the same pending payment (no duplicate wallet entry)
        var again = await r.Portal.FundAsync(Email, 50_000, "https://site.test/", default);
        var again2 = await r.Portal.FundAsync(Email, 50_000, "https://site.test/", default);
        Assert.Equal(again.Reference, again2.Reference);
    }

    [Fact]
    public async Task An_order_paid_from_the_wallet_is_at_the_partners_price_and_refused_when_the_wallet_is_short()
    {
        var r = await BuildAsync();
        var wholesale = await r.Db.Products.AsNoTracking().Where(p => p.Id == r.Seed.ProductId).Select(p => p.PriceWholesaler).SingleAsync();
        var order = new PortalOrder([new PortalLine(r.Seed.ProductId, 2)], "wallet", "Deliver to the Ikeja shop");

        var short_ = await Assert.ThrowsAsync<BusinessRuleException>(() => r.Portal.OrderAsync(Email, order, "https://site.test/", default));
        Assert.Contains("Top up", short_.Message);
        r.Db.ChangeTracker.Clear();
        Assert.Equal(0, await r.Db.Invoices.CountAsync());                                    // nothing sold
        Assert.Equal(0, await r.Db.Quotations.CountAsync());                                  // and the quotation tidied away

        r.Db.WalletEntries.Add(new WalletEntry { TradePartnerId = r.PartnerId, Kind = WalletEntryKinds.Funding, Amount = 1_000_000, Status = WalletEntryStatuses.Confirmed, Reference = "T-1", Method = "transfer", CreatedAt = DateTime.UtcNow });
        await r.Db.SaveChangesAsync();
        var done = await r.Portal.OrderAsync(Email, order, "https://site.test/", default);
        Assert.Equal("Paid", done.Status);
        r.Db.ChangeTracker.Clear();
        var inv = await r.Db.Invoices.Include(i => i.Items).SingleAsync();
        Assert.Equal(wholesale, inv.Items.Single().UnitPrice);                                // the partner's own price, from the price list
        Assert.Equal(PaymentStatusesOf(inv), "Paid");
        Assert.Equal(1_000_000 - inv.TotalAmount, await r.Portal.BalanceAsync(r.PartnerId, default));
        Assert.Contains(r.Alerts.Contacts, c => c.Subject.Contains(inv.InvoiceNumber));
    }

    private static string PaymentStatusesOf(Invoice i) => i.Status;

    [Fact]
    public async Task Paying_online_gives_AlatPays_page_and_a_suspended_partner_can_not_buy()
    {
        var r = await BuildAsync();
        var online = await r.Portal.OrderAsync(Email, new PortalOrder([new PortalLine(r.Seed.ProductId, 1)], "online", null), "https://site.test/", default);
        Assert.StartsWith("https://alatpay.example/", online.PayUrl);
        Assert.Equal("Awaiting payment", online.Status);

        var p = await r.Db.TradePartners.SingleAsync(); p.Status = PartnerStatuses.Suspended; await r.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ForbiddenActionException>(() => r.Portal.OrderAsync(Email, new PortalOrder([new PortalLine(r.Seed.ProductId, 1)], "online", null), "https://site.test/", default));
        await Assert.ThrowsAsync<ForbiddenActionException>(() => r.Portal.FundAsync(Email, 5000, "https://site.test/", default));
    }

    [Fact]
    public async Task A_message_reaches_the_admin_and_a_reply_reaches_the_partner()
    {
        var r = await BuildAsync();
        await r.Portal.SendAsync(Email, "Can you deliver on Saturday?", default);
        Assert.Contains(r.Alerts.Staff, s => s.Text.Contains("Can you deliver on Saturday?") && s.AdminOnly);
        await r.Portal.ReplyAsync(r.PartnerId, "Yes, before noon.", new Inventory.Application.Abstractions.CurrentUser(1, "Ada Manager", "MANAGER"), default);
        var thread = await r.Portal.MessagesAsync(Email, default);
        Assert.Equal([true, false], thread.Select(m => m.FromPartner));
        Assert.Contains(r.Alerts.Contacts, c => c.Text.Contains("Yes, before noon."));
    }
}
