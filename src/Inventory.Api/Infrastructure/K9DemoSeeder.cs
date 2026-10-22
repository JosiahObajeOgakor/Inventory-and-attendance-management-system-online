using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.AppsEngine;
using Inventory.Application.Branches;
using Inventory.Application.Common;
using Inventory.Application.Finance;
using Inventory.Application.Purchasing;
using Inventory.Application.Sales;
using Inventory.Application.Stock;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// Fills ONE business (named by <c>Demo:SeedCompany</c>) with believable records so a demo has something to show: products, stock, customers, six weeks of
/// sales, purchases, expenses, a second branch, staff and a few marketing drafts. It goes through the same services the screens use, so every figure,
/// balance and ledger entry is real. Runs only when that business has no products and no sales, and never touches any other business.
/// </summary>
public static class K9DemoSeeder
{
    public static async Task RunAsync(IServiceProvider root, CompanyRegistry registry, string? companyKey, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(companyKey) || registry.Find(companyKey) is not { } company) return;
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<RequestCompany>().BackgroundKey = company.Key;
        var db = sp.GetRequiredService<IBusinessDbContext>();
        if (await db.Products.AnyAsync() || await db.Invoices.AnyAsync()) return;
        log.LogInformation("Filling {Company} with demo records.", company.Key);

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var owner = users.Users.AsEnumerable().FirstOrDefault(u => u.CanOpen(company.Key));
        var me = new CurrentUser(owner?.Id ?? 0, owner?.FullName ?? "Demo", RoleNames.Admin);
        var clock = sp.GetRequiredService<IClock>();
        var today = clock.BusinessToday;

        // ---- catalogue
        var treats = new Category { Name = "Treats" }; var food = new Category { Name = "Dog food" }; var gear = new Category { Name = "Accessories" };
        db.Categories.AddRange(treats, food, gear);
        var main = await db.Warehouses.FirstOrDefaultAsync() ?? new Warehouse { Name = "K9 Main" };
        if (main.Id == 0) db.Warehouses.Add(main);
        var lekki = new Warehouse { Name = "Lekki Store", Location = "Lekki Phase 1, Lagos" };
        db.Warehouses.Add(lekki);
        await db.SaveChangesAsync();

        Product P(string sku, string name, Category c, string unit, decimal cost, decimal retail, int reorder, string species = "Dog") =>
            new() { Sku = sku, Name = name, CategoryId = c.Id, Unit = unit, CostPrice = cost, PriceRetail = retail, PriceWholesaler = Math.Round(retail * 0.92m), PriceDistributor = Math.Round(retail * 0.85m),
                SellingPrice = retail, ReorderLevel = reorder, Species = species };
        var products = new[]
        {
            P("K9-JRK-500", "Jerky Chews 500g", treats, "Pack", 3800, 5500, 20), P("K9-SAL-250", "Salmon Bites 250g", treats, "Pack", 2900, 4200, 20),
            P("K9-DNT-12", "Dental Sticks (12)", treats, "Pack", 2400, 3600, 15), P("K9-PUP-5", "Puppy Starter 5kg", food, "Bag", 14500, 19500, 12),
            P("K9-ADT-15", "Adult Chicken & Rice 15kg", food, "Bag", 31000, 41500, 10), P("K9-SNR-10", "Senior Care 10kg", food, "Bag", 23500, 31500, 10),
            P("K9-ROP-1", "Chew Rope Toy", gear, "Piece", 1500, 2800, 10), P("K9-COL-M", "Padded Collar (M)", gear, "Piece", 2600, 4500, 8),
        };
        db.Products.AddRange(products);
        var suppliers = new[]
        {
            new Supplier { Name = "PawFeed Nigeria Ltd", Category = "Dog food", ContactName = "Tunde Bakare", Phone = "08031112233", Email = "orders@pawfeed.example", Address = "Ikeja, Lagos" },
            new Supplier { Name = "TreatWorks Foods", Category = "Treats", ContactName = "Amaka Eze", Phone = "08055566677", Email = "sales@treatworks.example", Address = "Ota, Ogun" },
            new Supplier { Name = "Fetch & Co Accessories", Category = "Accessories", ContactName = "Ibrahim Musa", Phone = "08099988877", Email = "hello@fetchco.example", Address = "Surulere, Lagos" },
        };
        db.Suppliers.AddRange(suppliers);
        (string Name, string Type, string Phone, string Email, string Place)[] people =
        [
            ("Pawfect Pet Store", CustomerTypes.Wholesaler, "08011110001", "buy@pawfect.example", "Lekki"), ("Bella's Pet Shop", CustomerTypes.Retailer, "08011110002", "bella@shop.example", "Yaba"),
            ("Doggo Depot", CustomerTypes.Distributor, "08011110003", "supply@doggodepot.example", "Ikeja"), ("Chioma Okafor", CustomerTypes.Retailer, "08011110004", "chioma@mail.example", "Victoria Island"),
            ("Tobi Adeyemi", CustomerTypes.Retailer, "08011110005", "tobi@mail.example", "Surulere"), ("Happy Tails Vet Clinic", CustomerTypes.Wholesaler, "08011110006", "clinic@happytails.example", "Ikoyi"),
            ("Ngozi Eze", CustomerTypes.Retailer, "08011110007", "ngozi@mail.example", "Ajah"), ("Musa Bello", CustomerTypes.WalkIn, "08011110008", "musa@mail.example", "Maryland"),
        ];
        var customers = people.Select((p, i) => new Customer { Name = p.Name, CustomerType = p.Type, Phone = p.Phone, Email = p.Email, Location = p.Place, Address = p.Place + ", Lagos", CreditLimit = p.Type == CustomerTypes.Retailer ? 100000 : 500000,
            MarketingStatus = i % 4 == 3 ? MarketingStatuses.Unknown : MarketingStatuses.OptedIn, MarketingUpdatedAt = clock.UtcNow, MarketingSource = "demo" }).ToList();
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();

        // ---- stock comes in through purchases (so suppliers, payables and the books all agree)
        var purchases = sp.GetRequiredService<PurchaseService>();
        async Task Buy(Supplier s, int daysAgo, decimal paid, params (Product p, int qty)[] lines) =>
            await purchases.SaveAsync(new PurchaseRequest { SupplierId = s.Id, OrderDate = today.AddDays(-daysAgo), ReceiveNow = true, WarehouseId = main.Id, PaidNow = paid, PaymentMethod = "Bank Transfer",
                Lines = lines.Select(l => new PurchaseLineDto { ProductId = l.p.Id, Quantity = l.qty, UnitCost = l.p.CostPrice }).ToList() }, me, null);
        await Buy(suppliers[0], 46, 1_000_000, (products[3], 40), (products[4], 30), (products[5], 22));
        await Buy(suppliers[1], 44, 250_000, (products[0], 90), (products[1], 70), (products[2], 60));
        await Buy(suppliers[2], 30, 0, (products[6], 40), (products[7], 30));
        await Buy(suppliers[0], 12, 400_000, (products[4], 12));

        // ---- a second branch with a little stock of its own
        var branches = sp.GetRequiredService<BranchService>();
        var lekkiBranch = await branches.SaveAsync(null, new BranchInput("LKI", "Lekki branch", "Admiralty Way, Lekki Phase 1", "Lagos", "08011112222"), me);
        await branches.AssignWarehouseAsync(lekki.Id, lekkiBranch, me);
        var ops = sp.GetRequiredService<StockOperations>();
        foreach (var (p, q) in new[] { (products[0], 25), (products[3], 8), (products[6], 12), (products[7], 10) }) await ops.TransferAsync(p.Id, main.Id, lekki.Id, q, me);

        // ---- six weeks of sales
        var sales = sp.GetRequiredService<SalesService>();
        var rng = new Random(2026);
        string[] methods = ["Cash", "Bank Transfer", "Card", "Cash"];
        for (var d = 42; d >= 0; d--)
        {
            var day = today.AddDays(-d);
            if (day.DayOfWeek == DayOfWeek.Sunday && rng.Next(3) > 0) continue;
            for (var n = rng.Next(1, 4); n > 0; n--)
            {
                var c = customers[rng.Next(customers.Count)];
                var tier = c.CustomerType == CustomerTypes.Distributor ? PriceTiers.Distributor : c.CustomerType == CustomerTypes.Wholesaler ? PriceTiers.Wholesaler : PriceTiers.Retailer;
                var wh = rng.Next(5) == 0 ? lekki : main;
                var lines = products.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4)).Select(p => new SaleLineDto { ProductId = p.Id, Quantity = rng.Next(1, c.CustomerType == CustomerTypes.Retailer ? 4 : 9), UnitPrice = p.PriceFor(tier) }).ToList();
                var total = lines.Sum(l => l.Quantity * l.UnitPrice);
                var credit = c.CustomerType != CustomerTypes.WalkIn && rng.Next(4) == 0;
                try
                {
                    await sales.SaveAsync(new SaleRequest { CustomerId = c.Id, SaleDate = day, PriceTier = tier, WarehouseId = wh.Id, PaymentMethod = credit ? "Credit" : methods[rng.Next(methods.Length)],
                        VatRate = rng.Next(3) == 0 ? 7.5m : 0, PaidNow = credit ? (rng.Next(2) == 0 ? 0 : Math.Round(total / 3)) : 0, DueDate = credit ? day.AddDays(14) : null, Lines = lines }, me, null);
                }
                catch (BusinessRuleException) { /* not enough of something that day: the demo simply skips that sale */ }
            }
        }
        // paid-up customers settle some of what they owed
        var payments = sp.GetRequiredService<CustomerPaymentService>();
        foreach (var c in await db.Customers.AsNoTracking().Where(x => x.Balance > 20000).Take(3).ToListAsync())
        {
            try { await payments.RecordAsync(c.Id, Math.Round(c.Balance / 2), "Bank Transfer", me); } catch (BusinessRuleException) { }
        }

        // ---- running costs
        var expenses = sp.GetRequiredService<ExpenseService>();
        (string Cat, int Ago, decimal Amt, string Note)[] costs =
        [("Rent", 40, 350000, "Shop rent, last month"), ("Rent", 10, 350000, "Shop rent"), ("Salaries", 38, 420000, "Staff salaries"), ("Salaries", 8, 420000, "Staff salaries"),
         ("Utilities", 35, 48000, "Electricity and internet"), ("Utilities", 6, 51500, "Electricity and internet"), ("Logistics", 30, 36000, "Delivery riders"), ("Logistics", 21, 29500, "Delivery riders"),
         ("Logistics", 9, 41000, "Delivery riders"), ("Maintenance", 25, 18000, "Freezer service"), ("Other", 18, 22000, "Packaging and labels"), ("Other", 4, 15000, "Cleaning supplies")];
        foreach (var e in costs) await expenses.CreateAsync(new ExpenseInput(e.Cat, today.AddDays(-e.Ago), e.Amt, e.Note), me);

        // ---- team
        db.Employees.AddRange(
            new Employee { FullName = "Adaeze Nwosu", Position = "Store manager", Phone = "08022200001", StartedOn = today.AddYears(-2), MonthlySalary = 180000 },
            new Employee { FullName = "Kunle Balogun", Position = "Sales attendant", Phone = "08022200002", StartedOn = today.AddMonths(-14), MonthlySalary = 95000 },
            new Employee { FullName = "Fatima Sani", Position = "Sales attendant", Phone = "08022200003", StartedOn = today.AddMonths(-8), MonthlySalary = 95000 },
            new Employee { FullName = "Emeka Obi", Position = "Delivery rider", Phone = "08022200004", StartedOn = today.AddMonths(-5), MonthlySalary = 80000 });
        await db.SaveChangesAsync();

        // ---- marketing (drafts only: nothing is sent)
        var apps = sp.GetRequiredService<AppRecordService>();
        async Task Draft(string app, string title, string? subject, string message)
        {
            try
            {
                var f = new JsonObject { ["title"] = title, ["audience"] = BroadcastQueue.Audiences[0], ["message"] = message };
                if (subject is not null) f["subject"] = subject;
                await apps.SaveAsync(app, "campaign", null, new SaveInput(null, f), me, "", default);
            }
            catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException) { log.LogWarning("Demo marketing draft '{Title}' skipped: {Why}", title, ex.Message); }
        }
        await Draft("email-marketing", "New puppy starter range", "Fresh stock of Puppy Starter is in", "Hello {name}, our new Puppy Starter 5kg has just arrived. Visit us or reply to order.");
        await Draft("sms-marketing", "Weekend treats offer", null, "Hi {name}, 10% off all treats this weekend at K9 Cravings. Reply STOP to opt out.");
        await Draft("whatsapp", "Back in stock: Adult Chicken & Rice", null, "Hello {name}, Adult Chicken & Rice 15kg is back in stock. Reply to reserve yours.");
        log.LogInformation("Demo records ready for {Company}.", company.Key);
    }
}
