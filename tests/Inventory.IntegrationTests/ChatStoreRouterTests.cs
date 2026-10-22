using Inventory.Api.Infrastructure;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>One WhatsApp line sells for every store: each message goes to the store whose stock it is about.</summary>
[Collection("api")]
public class ChatStoreRouterTests(ApiFixture api)
{
    [Fact]
    public async Task A_message_goes_to_the_store_named_the_store_with_the_open_order_or_the_only_store_selling_it()
    {
        using var scope = api.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
        await using (var candid = registry.CreateFor(registry.Find("candid")!))
        {
            if (!await candid.Products.AnyAsync(p => p.Sku == "ROUTE-1"))
            {
                var cat = await candid.Categories.FirstOrDefaultAsync() ?? candid.Categories.Add(new Category { Name = "Cat Food" }).Entity;
                await candid.SaveChangesAsync();
                candid.Products.Add(new Product { Sku = "ROUTE-1", Name = "Router Kibble Deluxe 2kg", CategoryId = cat.Id, PriceRetail = 4000, SellingPrice = 4000 });
                await candid.SaveChangesAsync();
            }
        }
        var router = scope.ServiceProvider.GetRequiredService<ChatStoreRouter>();
        const string phone = "2348099990001";

        Assert.Equal("chewypets", await router.RouteAsync("chewypets", ChatChannels.WhatsApp, phone, "hi", default));
        Assert.Equal("chewypets", await router.RouteAsync("chewypets", ChatChannels.WhatsApp, phone, "2 adult dog food", default));
        Assert.Equal("candid", await router.RouteAsync("chewypets", ChatChannels.WhatsApp, phone, "Hi Candid Purrfect! I'd like to order 2 adult dog food", default));
        Assert.Equal("candid", await router.RouteAsync("chewypets", ChatChannels.WhatsApp, phone, "2 router kibble deluxe", default));

        // mid-order with Candid: a bare "yes" stays with Candid
        await using (var candid = registry.CreateFor(registry.Find("candid")!))
        {
            candid.ChatConversations.Add(new ChatConversation { Channel = ChatChannels.WhatsApp, ExternalId = phone, State = ChatStates.AwaitingConfirm, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
            await candid.SaveChangesAsync();
        }
        Assert.Equal("candid", await router.RouteAsync("chewypets", ChatChannels.WhatsApp, phone, "yes", default));
    }
}
