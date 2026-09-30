using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Products;
using Inventory.Application.Purchasing;
using Inventory.Application.Sales;
using Inventory.Application.Stock;
using Inventory.Application.Staff;
using Inventory.Application.Trade;
using Inventory.Application.Finance;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>Wires the application services for one DbContext, the way DI will in the API.</summary>
internal sealed class Services(BusinessDbContext db)
{
    private readonly SystemClock _clock = new();
    public TransactionRunner Tx => new(db, new MySqlErrorClassifier());
    public StockService Stock => new(db, _clock);
    public ICompanyContext Company => new CompanyContext(new CompanyInfo("test", "Test", "TestCo", "x"));
    public PurchaseService Purchases => new(db, Tx, Stock, _clock, Company, new PurchaseRequestValidator());
    public StockOperations Ops => new(db, Tx, Stock, _clock);
    public ProductService Products => new(db, Tx, Stock, _clock, Company);
    public CustomerPaymentService Payments => new(db, Tx, _clock);
    public InvoiceVoidService Voids => new(db, Tx, Stock, _clock);
    public SupplyService Supplies => new(db, Tx, _clock, Company, new SupplyRequestValidator());
    public SupplierCatalogService Catalog => new(db, Tx, _clock);
    public SalesService Sales => SalesFor(db);
}

[Collection("mysql")]
public class PurchaseTests(MySqlFixture mysql)
{
    private static async Task<int> AddSupplier(string cs, decimal balance = 0)
    {
        await using var db = NewContext(cs);
        var s = new Supplier { Name = "AgroFeed", Balance = balance };
        db.Suppliers.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static PurchaseRequest Po(int supplierId, int productId, int qty, decimal cost, bool receive = false, decimal paid = 0, decimal vat = 0) => new()
    {
        SupplierId = supplierId, VatRate = vat, ReceiveNow = receive, PaidNow = paid,
        Lines = [new PurchaseLineDto { ProductId = productId, Quantity = qty, UnitCost = cost }],
    };

    [Fact]
    public async Task Receive_now_books_stock_batch_cost_barcode_and_history_in_one_transaction()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);

        await using var db = NewContext(cs);
        var r = await new Services(db).Purchases.SaveAsync(Po(supplier, seed.ProductId, 20, 9000, receive: true, paid: 50000), Clerk, null);

        Assert.Equal(180000m, r.Total);
        Assert.Equal(130000m, r.Outstanding);
        await using var check = NewContext(cs);
        var batch = await check.StockBatches.SingleAsync(b => b.BatchNumber == r.PoNumber);   // batch named after the PO
        Assert.Equal(20, batch.QuantityOnHand);
        var mv = await check.StockMovements.SingleAsync();
        Assert.Equal(("IN", "PurchaseOrder", 20), (mv.MovementType, mv.ReferenceType, mv.Quantity));
        Assert.Equal(9000m, (await check.Products.SingleAsync()).CostPrice);
        Assert.Equal(130000m, (await check.Suppliers.SingleAsync()).Balance);
        var ledger = await check.Ledger.SingleAsync();
        Assert.Equal(("Supplier", "Credit", 130000m), (ledger.AccountType, ledger.EntryType, ledger.Amount));
        Assert.Equal("Received", (await check.PurchaseOrders.SingleAsync()).Status);
    }

    /// <summary>One of the supplier's OWN items — typed in, not a stock product.</summary>
    private static async Task<int> AddOwnItem(string cs, int supplierId, string name, decimal cost, string unit = "Bag")
    {
        await using var db = NewContext(cs);
        var p = new SupplierProduct { SupplierId = supplierId, Name = name, Unit = unit, UnitCost = cost, IsActive = true, CreatedAt = DateTime.UtcNow };
        db.SupplierProducts.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static SupplyRequest Sup(int supplierId, int ownItemId, int qty, decimal cost, decimal paid = 0, DateOnly? on = null) => new()
    {
        SupplierId = supplierId, PaidNow = paid, SupplyDate = on,
        Lines = [new SupplyLineDto { SupplierProductId = ownItemId, Quantity = qty, UnitCost = cost }],
    };

    [Fact]
    public async Task A_suppliers_own_items_are_their_own_and_are_never_products()
    {
        var cs = await mysql.NewSchemaAsync();
        await SeedAsync(cs, stock: 5);                                    // there is a product catalogue; supplier items ignore it
        var a = await AddSupplier(cs);
        int b;
        await using (var db0 = NewContext(cs))
        {
            var s = new Supplier { Name = "Second Supplier" };
            db0.Suppliers.Add(s); await db0.SaveChangesAsync(); b = s.Id;
        }

        await using var db = NewContext(cs);
        var catalog = new Services(db).Catalog;
        var saved = await catalog.SaveAsync(a, [
            new SupplierProductInput(0, "Layer Mash", "25kg", "Bag", 9000),
            new SupplierProductInput(0, "Maize", "100kg", "Sack", 42000),
        ], Clerk);
        Assert.Equal(2, saved.Count);
        Assert.Equal(("Layer Mash", "25kg", "Bag", 9000m), (saved[0].Name, saved[0].Size, saved[0].Unit, saved[0].UnitCost));

        // One supplier's list is invisible to another, and can never be billed against them.
        Assert.Empty(await catalog.ListAsync(b, false));
        var strayId = saved[0].Id;
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Supplies.CreateAsync(Sup(b, strayId, 1, 9000), Clerk));

        // Editing the list renames in place and drops what is gone; products are untouched throughout.
        var again = await catalog.SaveAsync(a, [new SupplierProductInput(saved[0].Id, "Layer Mash Premium", "25kg", "Bag", 9500)], Clerk);
        Assert.Equal("Layer Mash Premium", Assert.Single(again).Name);
        await using var check = NewContext(cs);
        Assert.Equal("Adult Dog Food 20kg", (await check.Products.SingleAsync()).Name);
        Assert.Equal(5, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
    }

    [Fact]
    public async Task An_item_already_supplied_is_retired_not_deleted_so_history_still_reads()
    {
        var cs = await mysql.NewSchemaAsync();
        var supplier = await AddSupplier(cs);
        await using var db = NewContext(cs);
        var catalog = new Services(db).Catalog;
        var items = await catalog.SaveAsync(supplier, [new SupplierProductInput(0, "Bran", "50kg", "Sack", 4000)], Clerk);
        var id = items[0].Id;
        await new Services(db).Supplies.CreateAsync(Sup(supplier, id, 3, 4000), Clerk);

        await catalog.SaveAsync(supplier, [], Clerk);                     // taken off the list

        Assert.Empty(await catalog.ListAsync(supplier, includeInactive: false));
        var kept = Assert.Single(await catalog.ListAsync(supplier, includeInactive: true));
        Assert.False(kept.IsActive);
        await using var check = NewContext(cs);
        var line = Assert.Single(await check.SupplyItems.ToListAsync());
        Assert.Equal(("Bran", "50kg", 3), (line.Name, line.Size, line.Quantity));   // the record still says what was supplied
    }

    [Fact]
    public async Task A_recorded_supply_leaves_stock_cost_price_and_the_ledger_completely_alone()
    {
        var cs = await mysql.NewSchemaAsync();
        await SeedAsync(cs, stock: 12);                                  // 12 on the shelf, product cost price 8,500
        var supplier = await AddSupplier(cs);
        var ownItem = await AddOwnItem(cs, supplier, "Layer Mash 25kg", 9500);   // the supplier's own item, not a product

        await using var db = NewContext(cs);
        var r = await new Services(db).Supplies.CreateAsync(Sup(supplier, ownItem, 40, 9500, paid: 100000), Clerk);

        Assert.Equal((380000m, 100000m, 280000m, "Partial"), (r.Total, r.Paid, r.Outstanding, r.PaymentStatus));
        await using var check = NewContext(cs);
        // The whole point of a supply record: it is the supplier's truth and nothing else moves.
        Assert.Equal(12, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        Assert.Empty(await check.StockMovements.ToListAsync());
        Assert.Equal(8500m, (await check.Products.SingleAsync()).CostPrice);
        Assert.Empty(await check.Ledger.ToListAsync());
        Assert.Equal(0m, (await check.Suppliers.SingleAsync()).Balance);   // supplies keep their own owed figure, not the order balance
        Assert.Empty(await check.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task Supply_records_report_per_supplier_per_month_and_per_item_for_this_company_only()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var mastafeed = await AddSupplier(cs);
        int other;
        await using (var db0 = NewContext(cs))
        {
            var s = new Supplier { Name = "Other Feeds" };
            db0.Suppliers.Add(s); await db0.SaveChangesAsync(); other = s.Id;
        }
        var today = new SystemClock().BusinessToday;
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var mastaItem = await AddOwnItem(cs, mastafeed, "Layer Mash 25kg", 9000);
        var otherItem = await AddOwnItem(cs, other, "Maize (sack)", 8000, "Sack");
        await using (var a = NewContext(cs)) await new Services(a).Supplies.CreateAsync(Sup(mastafeed, mastaItem, 10, 9000, paid: 40000), Clerk);
        await using (var b = NewContext(cs)) await new Services(b).Supplies.CreateAsync(Sup(mastafeed, mastaItem, 5, 9000), Clerk);
        await using (var c = NewContext(cs)) await new Services(c).Supplies.CreateAsync(Sup(other, otherItem, 2, 8000), Clerk);
        await using (var d = NewContext(cs)) await new Services(d).Supplies.CreateAsync(Sup(mastafeed, mastaItem, 3, 9000, on: lastMonth), Clerk);

        await using var check = NewContext(cs);
        var sum = await new SupplyQueries(check, new SystemClock(), new NoUsers()).SummaryAsync(default);

        Assert.Equal(151000m, sum.ThisMonth);                             // 90,000 + 45,000 + 16,000 — last month's 27,000 excluded
        Assert.Equal(27000m, sum.LastMonth);
        Assert.Equal(138000m, sum.OwedTotal);                             // everything supplied minus the 40,000 paid
        var top = Assert.Single(sum.BySupplier, x => x.SupplierId == mastafeed);
        Assert.Equal((135000m, 2, 15), (top.Amount, top.Records, top.Units));
        Assert.Equal(16000m, sum.BySupplier.Single(x => x.SupplierId == other).Amount);
        Assert.Equal(151000m, sum.TopItems.Sum(i => i.Amount));           // the item breakdown ties back to the month's total
        Assert.Equal(12, sum.Months.Count);
        Assert.Contains(sum.Trends, t => t.SupplierId == mastafeed && t.Months.Count == 12);
    }

    [Fact]
    public async Task An_admin_can_delete_one_supply_a_supplier_a_month_or_everything()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 6);
        var a1 = await AddSupplier(cs);
        int a2;
        await using (var db0 = NewContext(cs))
        {
            var s = new Supplier { Name = "Second Supplier" };
            db0.Suppliers.Add(s); await db0.SaveChangesAsync(); a2 = s.Id;
        }
        var march = new DateOnly(2026, 3, 14);
        var i1 = await AddOwnItem(cs, a1, "Bran", 1000);
        var i2 = await AddOwnItem(cs, a2, "Salt lick", 1000);
        int one;
        await using (var a = NewContext(cs)) one = (await new Services(a).Supplies.CreateAsync(Sup(a1, i1, 1, 1000), Clerk)).Id;
        await using (var b = NewContext(cs)) await new Services(b).Supplies.CreateAsync(Sup(a1, i1, 2, 1000), Clerk);
        await using (var c = NewContext(cs)) await new Services(c).Supplies.CreateAsync(Sup(a2, i2, 3, 1000, on: march), Clerk);
        await using (var d = NewContext(cs)) await new Services(d).Supplies.CreateAsync(Sup(a2, i2, 4, 1000), Clerk);

        await using (var db = NewContext(cs))
        {
            var one1 = await new Services(db).Supplies.DeleteAsync(one, Clerk);
            Assert.Equal((1, 1000m), (one1.Records, one1.Value));
        }
        await using (var db = NewContext(cs))
        {
            var bySupplier = await new Services(db).Supplies.DeleteForSupplierAsync(a1, Clerk);
            Assert.Equal((1, 2000m), (bySupplier.Records, bySupplier.Value));   // only a1's remaining record
        }
        await using (var db = NewContext(cs))
        {
            var byMonth = await new Services(db).Supplies.DeleteForMonthAsync(2026, 3, Clerk);
            Assert.Equal((1, 3000m), (byMonth.Records, byMonth.Value));
        }
        await using (var db = NewContext(cs))
        {
            var all = await new Services(db).Supplies.DeleteAllAsync(Clerk);
            Assert.Equal((1, 4000m), (all.Records, all.Value));
        }

        await using var check = NewContext(cs);
        Assert.Empty(await check.Supplies.ToListAsync());
        Assert.Empty(await check.SupplyItems.ToListAsync());               // the items go with their record
        Assert.Equal(6, await check.StockBatches.SumAsync(b => b.QuantityOnHand));   // deleting supply history never touches stock
        Assert.Equal(2, await check.Suppliers.CountAsync());                          // nor the suppliers themselves
        Assert.Equal(4, await check.AuditLogs.CountAsync(l => l.Entity == "Supply" && l.Action.StartsWith("SUPPL")
            && (l.Action == "SUPPLY_DELETED" || l.Action == "SUPPLIES_CLEARED")));    // every delete is on the record
    }

    [Fact]
    public async Task Paying_a_supply_is_capped_at_what_that_supply_still_owes()
    {
        var cs = await mysql.NewSchemaAsync();
        var supplier = await AddSupplier(cs);
        var ownItem = await AddOwnItem(cs, supplier, "Bran", 1000);
        int id;
        await using (var a = NewContext(cs)) id = (await new Services(a).Supplies.CreateAsync(Sup(supplier, ownItem, 10, 1000, paid: 4000), Clerk)).Id;

        await using (var b = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(b).Supplies.PayAsync(id, 7000, "Cash", Clerk));
        await using (var c = NewContext(cs))
        {
            var r = await new Services(c).Supplies.PayAsync(id, 6000, "Transfer", Clerk);
            Assert.Equal((10000m, 0m, "Paid"), (r.Paid, r.Outstanding, r.PaymentStatus));
        }
        await using (var d = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(d).Supplies.PayAsync(id, 100, "Cash", Clerk));
    }

    [Fact]
    public async Task Paying_a_supplier_settles_the_oldest_order_first_and_the_statement_adds_up()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        await using (var a = NewContext(cs)) await new Services(a).Purchases.SaveAsync(Po(supplier, seed.ProductId, 10, 1000, paid: 4000), Clerk, null);   // 10,000; 6,000 owed
        await using (var b = NewContext(cs)) await new Services(b).Purchases.SaveAsync(Po(supplier, seed.ProductId, 5, 1200), Clerk, null);                // 6,000; all owed

        await using var db = NewContext(cs);
        var r = await new Services(db).Purchases.RecordPaymentAsync(supplier, 8000, "Transfer", Clerk);

        Assert.Equal((8000m, 8000m, 4000m, 2), (r.Amount, r.AppliedToOrders, r.BalanceNow, r.OrdersPaid));
        await using var check = NewContext(cs);
        var orders = await check.PurchaseOrders.OrderBy(o => o.Id).ToListAsync();
        Assert.Equal(("Paid", 10000m), (orders[0].PaymentStatus, orders[0].AmountPaid));     // the older order is cleared first
        Assert.Equal(("Partial", 2000m), (orders[1].PaymentStatus, orders[1].AmountPaid));   // the rest goes to the next one
        Assert.Equal(4000m, (await check.Suppliers.SingleAsync()).Balance);

        var st = await new SupplierStatementQueries(check, new SystemClock(), new Services(check).Catalog).GetAsync(supplier, default);
        Assert.Equal((16000m, 12000m, 4000m, 2, 1), (st.TotalBought, st.TotalPaid, st.Owed, st.Orders, st.OpenOrders));
        Assert.Equal(st.TotalBought - st.TotalPaid, st.Owed);
        Assert.Equal(12000m, st.Payments.Sum(p => p.Amount));                                 // every naira paid is on the statement
        var transfer = Assert.Single(st.Payments, p => p.Method == "Transfer");
        Assert.Equal((8000m, 2), (transfer.Amount, transfer.Orders.Count));                   // one payment, shown once, against both orders
        var item = Assert.Single(st.Items);
        Assert.Equal((15, 16000m, 1200m), (item.QuantityBought, item.AmountBought, item.LastCost));
    }

    [Fact]
    public async Task A_supplier_cannot_be_paid_more_than_is_owed()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        await using (var a = NewContext(cs)) await new Services(a).Purchases.SaveAsync(Po(supplier, seed.ProductId, 2, 500), Clerk, null);   // owes 1,000

        await using var db = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Purchases.RecordPaymentAsync(supplier, 1500, "Cash", Clerk));
        await using var check = NewContext(cs);
        Assert.Equal(1000m, (await check.Suppliers.SingleAsync()).Balance);
        Assert.Empty(await check.SupplierPayments.ToListAsync());
    }

    [Fact]
    public async Task A_product_saved_with_suppliers_lands_on_their_item_lists_and_can_be_unlinked()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        await using var db = NewContext(cs);
        var svc = new Services(db).Products;
        var input = new ProductInput { Sku = "SKU-1", Name = "Adult Dog Food 20kg", CategoryId = (await db.Categories.SingleAsync()).Id, Unit = "Bag", CostPrice = 8500, PriceRetail = 11500, SupplierIds = [supplier] };

        await svc.UpdateAsync(seed.ProductId, input, Clerk);
        var linked = Assert.Single(await svc.SuppliersAsync(seed.ProductId));
        Assert.Equal((supplier, 8500m), (linked.SupplierId, linked.UnitCost));   // starts at the product's cost price

        input.SupplierIds = null;                                                  // "not sent": links are left alone
        await svc.UpdateAsync(seed.ProductId, input, Clerk);
        Assert.Single(await svc.SuppliersAsync(seed.ProductId));

        input.SupplierIds = [];
        await svc.UpdateAsync(seed.ProductId, input, Clerk);
        Assert.Empty(await svc.SuppliersAsync(seed.ProductId));
    }

    [Fact]
    public async Task The_dashboard_reports_cost_of_goods_sold_goods_bought_and_what_is_owed_to_suppliers()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);                                 // cost 8,500 each
        var supplier = await AddSupplier(cs);
        await using (var a = NewContext(cs)) await new Services(a).Purchases.SaveAsync(Po(supplier, seed.ProductId, 4, 9000, paid: 6000), Clerk, null);   // bought 36,000; owe 30,000
        await using (var b = NewContext(cs)) await new Services(b).Sales.SaveAsync(Sale(seed, 3, vat: 0), Clerk, null);                                  // 3 sold at cost 8,500

        await using var db = NewContext(cs);
        var o = await new Inventory.Application.Dashboard.OverviewQueries(db, new SystemClock()).GetAsync(30, default);

        Assert.Equal(25500m, o.Cogs!.Current);
        Assert.Equal(36000m, o.Purchases!.Current);
        Assert.Equal((30000m, 1), (o.PayablesTotal, o.SuppliersOwed));
        Assert.Equal(o.Revenue.Current - o.Cogs.Current, o.GrossProfit.Current);   // the three figures agree with each other
    }

    [Fact]
    public async Task Receiving_later_is_transactional_logged_and_cannot_happen_twice()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        await using var a = NewContext(cs);
        var po = await new Services(a).Purchases.SaveAsync(Po(supplier, seed.ProductId, 5, 100), Clerk, null);

        await using var b = NewContext(cs);
        await new Services(b).Purchases.ReceiveAsync(po.PurchaseOrderId, seed.WarehouseId, Clerk);
        await using var c = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(c).Purchases.ReceiveAsync(po.PurchaseOrderId, seed.WarehouseId, Clerk));

        await using var check = NewContext(cs);
        Assert.Equal(5, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
        Assert.Equal(1, await check.StockMovements.CountAsync());   // history exists (desktop version wrote none)
    }

    [Fact]
    public async Task Parallel_receipts_of_the_same_order_book_the_stock_once()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        await using var a = NewContext(cs);
        var po = await new Services(a).Purchases.SaveAsync(Po(supplier, seed.ProductId, 5, 100), Clerk, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = NewContext(cs);
            try { await new Services(db).Purchases.ReceiveAsync(po.PurchaseOrderId, seed.WarehouseId, Clerk); return true; }
            catch (BusinessRuleException) { return false; }
        }));
        Assert.Equal(1, results.Count(x => x));
        await using var check = NewContext(cs);
        Assert.Equal(5, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
    }

    [Fact]
    public async Task Overpaying_a_new_order_pays_older_orders_oldest_first_and_mark_paid_settles_the_rest()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        await using (var a = NewContext(cs)) await new Services(a).Purchases.SaveAsync(Po(supplier, seed.ProductId, 1, 1000), Clerk, null);
        await using (var b = NewContext(cs)) await new Services(b).Purchases.SaveAsync(Po(supplier, seed.ProductId, 1, 1000), Clerk, null);

        await using var c = NewContext(cs);
        var r = await new Services(c).Purchases.SaveAsync(Po(supplier, seed.ProductId, 1, 1000, paid: 1500), Clerk, null);
        Assert.Equal(2000m, r.PreviousBalance);
        Assert.Equal(500m, r.AppliedToPreviousBalance);
        Assert.Equal(1500m, r.RemainingBalance);   // owed 2,000 before; 1,500 paid; this order (1,000) is fully paid, 500 went to the oldest order

        int firstOrderId;
        await using (var q = NewContext(cs)) firstOrderId = (await q.PurchaseOrders.OrderBy(p => p.Id).FirstAsync()).Id;
        await using var d = NewContext(cs);
        await new Services(d).Purchases.PayAsync(firstOrderId, Clerk);   // the remaining 500 on order 1

        await using var check = NewContext(cs);
        Assert.Equal(1000m, (await check.Suppliers.SingleAsync()).Balance);
        // The maintained running balance always equals what the individual orders still owe.
        Assert.Equal(await check.PurchaseOrders.SumAsync(p => p.TotalAmount - p.AmountPaid), (await check.Suppliers.SingleAsync()).Balance);
    }
}

[Collection("mysql")]
public class StockOperationTests(MySqlFixture mysql)
{
    [Fact]
    public async Task Product_creation_logs_opening_balance_and_mints_a_valid_barcode()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        await using var db = NewContext(cs);
        var id = await new Services(db).Products.CreateAsync(new NewProductRequest
        {
            Product = new ProductInput { Sku = "NEW-1", Name = "Puppy Starter", CategoryId = 1, PriceRetail = 900 },
            OpeningQuantity = 12, WarehouseId = seed.WarehouseId,
        }, Clerk);

        await using var check = NewContext(cs);
        var p = await check.Products.SingleAsync(x => x.Id == id);
        Assert.True(Inventory.Domain.Barcodes.IsValidEan13(p.Barcode));
        Assert.Equal(12, await check.StockBatches.Where(b => b.ProductId == id).SumAsync(b => b.QuantityOnHand));
        Assert.Equal("OpeningBalance", (await check.StockMovements.SingleAsync(m => m.ProductId == id)).ReferenceType);
    }

    [Fact]
    public async Task Duplicate_sku_is_refused_with_a_clear_message()
    {
        var cs = await mysql.NewSchemaAsync();
        await SeedAsync(cs, stock: 0);
        await using var db = NewContext(cs);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Products.CreateAsync(
            new NewProductRequest { Product = new ProductInput { Sku = "SKU-1", Name = "Dup", CategoryId = 1 } }, Clerk));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task Deleting_a_product_takes_its_stock_with_it_and_says_so_in_the_log()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 4);
        await using (var db = NewContext(cs)) Assert.True(await new Services(db).Products.DeleteAsync(seed.ProductId, Clerk));
        await using var check = NewContext(cs);
        Assert.Empty(await check.Products.ToListAsync());
        Assert.Empty(await check.StockBatches.ToListAsync());
        Assert.Contains("4 unit(s) in stock removed", (await check.AuditLogs.SingleAsync(a => a.Action == "PRODUCT_DELETED")).Detail);
    }

    [Fact]
    public async Task A_product_on_a_sale_cannot_be_deleted()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 1);
        await using (var a = NewContext(cs)) await new Services(a).Sales.SaveAsync(Sale(seed, 1), Clerk, null);
        await using var db = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Products.DeleteAsync(seed.ProductId, Clerk));
        await using var check = NewContext(cs);
        Assert.True((await check.Products.SingleAsync()).IsActive);   // the sale still prints its name
    }

    [Fact]
    public async Task Production_is_corrected_by_the_difference_and_refused_when_stock_is_gone()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        int movementId;
        await using (var a = NewContext(cs))
            movementId = await new Services(a).Ops.RecordProductionAsync(seed.ProductId, seed.WarehouseId, 41, new DateOnly(2026, 9, 1), null, null, Clerk);
        await using (var b = NewContext(cs))
            await new Services(b).Ops.CorrectProductionAsync(movementId, 30, new DateOnly(2026, 9, 1), Clerk);
        await using (var c = NewContext(cs)) Assert.Equal(30, await c.StockBatches.SumAsync(x => x.QuantityOnHand));

        await using (var d = NewContext(cs)) await new Services(d).Sales.SaveAsync(Sale(seed, 25), Clerk, null);   // 5 left
        await using var e = NewContext(cs);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(e).Ops.CorrectProductionAsync(movementId, 0, new DateOnly(2026, 9, 1), Clerk));
        Assert.Contains("can't go below 25", ex.Message);
    }

    [Fact]
    public async Task Deleting_a_production_entry_keeps_its_history_row()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        await using var a = NewContext(cs);
        var id = await new Services(a).Ops.RecordProductionAsync(seed.ProductId, seed.WarehouseId, 10, new DateOnly(2026, 9, 1), null, null, Clerk);
        await using var b = NewContext(cs);
        await new Services(b).Ops.CorrectProductionAsync(id, 0, new DateOnly(2026, 9, 1), Clerk);
        await using var check = NewContext(cs);
        var row = await check.StockMovements.SingleAsync(m => m.Id == id);   // desktop app deleted this row (D6)
        Assert.Equal(0, row.Quantity);
        Assert.Equal(0, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
    }

    [Fact]
    public async Task Transfer_moves_stock_keeps_batch_and_expiry_and_logs_one_out_and_one_in()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        int shore;
        await using (var a = NewContext(cs)) { var w = new Warehouse { Name = "Shore" }; a.Warehouses.Add(w); await a.SaveChangesAsync(); shore = w.Id; }

        await using var db = NewContext(cs);
        await new Services(db).Ops.TransferAsync(seed.ProductId, seed.WarehouseId, shore, 4, Clerk);

        await using var check = NewContext(cs);
        Assert.Equal(6, await check.StockBatches.Where(b => b.WarehouseId == seed.WarehouseId).SumAsync(b => b.QuantityOnHand));
        var dest = await check.StockBatches.SingleAsync(b => b.WarehouseId == shore);
        Assert.Equal(("B1", 4), (dest.BatchNumber, dest.QuantityOnHand));
        Assert.Equal(2, await check.StockMovements.CountAsync(m => m.ReferenceType == "Transfer"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Ops.TransferAsync(seed.ProductId, seed.WarehouseId, shore, 99, Clerk));
    }

    [Fact]
    public async Task Adjustment_needs_a_reason_and_can_never_drive_a_batch_negative()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 3);
        await using var db = NewContext(cs);
        var batchId = (await db.StockBatches.SingleAsync()).Id;
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Ops.AdjustAsync(batchId, -1, " ", Clerk));
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Ops.AdjustAsync(batchId, -4, "count", Clerk));
        await new Services(db).Ops.AdjustAsync(batchId, -2, "Stock count: damaged bags", Clerk);
        await using var check = NewContext(cs);
        Assert.Equal(1, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        var mv = await check.StockMovements.SingleAsync();
        Assert.Equal(("ADJUST", -2), (mv.MovementType, mv.Quantity));
    }
}

[Collection("mysql")]
public class PaymentAndVoidTests(MySqlFixture mysql)
{
    [Fact]
    public async Task Recording_a_payment_pays_oldest_invoices_first_and_is_capped_at_the_balance()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using (var a = NewContext(cs)) await new Services(a).Sales.SaveAsync(Sale(seed, 1, vat: 0), Clerk, null);
        await using (var b = NewContext(cs)) await new Services(b).Sales.SaveAsync(Sale(seed, 1, vat: 0), Clerk, null);

        await using var c = NewContext(cs);
        var svc = new Services(c).Payments;
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RecordAsync(seed.CustomerId, 99999, "Cash", Clerk));
        await svc.RecordAsync(seed.CustomerId, 15000, "Cash", Clerk);

        await using var check = NewContext(cs);
        var inv = await check.Invoices.OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(("Paid", "Partial"), (inv[0].Status, inv[1].Status));
        Assert.Equal(8000m, (await check.Customers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task Voiding_an_invoice_restores_stock_and_balance_and_keeps_the_record()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10, customerType: "Distributor");
        SaleResult sale;
        await using (var a = NewContext(cs)) sale = await new Services(a).Sales.SaveAsync(Sale(seed, 3, vat: 0, paid: 10000), Clerk, null);

        await using (var b = NewContext(cs)) await new Services(b).Voids.VoidAsync(sale.InvoiceId, "Customer cancelled", Clerk);

        await using var check = NewContext(cs);
        Assert.Equal(10, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
        var only = await check.StockBatches.SingleAsync();          // back in the ORIGINAL batch — no per-invoice VOID batch
        Assert.Equal(("B1", 10), (only.BatchNumber, only.QuantityOnHand));
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);          // owed 24,500 came off; the 10,000 paid stays paid
        var invoice = await check.Invoices.SingleAsync();
        Assert.Equal(("Voided", "Customer cancelled"), (invoice.Status, invoice.VoidReason));
        Assert.Empty(await check.RebateEntries.ToListAsync());
        Assert.Contains(await check.StockMovements.ToListAsync(), m => m.ReferenceType == "InvoiceVoid" && m.MovementType == "IN");
        await using var again = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(again).Voids.VoidAsync(sale.InvoiceId, "twice", Clerk));
    }
}

[Collection("mysql")]
public class VoidFallbackTests(MySqlFixture mysql)
{
    [Fact]
    public async Task A_migrated_sale_with_no_batch_id_returns_to_one_shared_RETURNED_batch()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        SaleResult sale;
        await using (var a = NewContext(cs)) sale = await new Services(a).Sales.SaveAsync(Sale(seed, 3, vat: 0), Clerk, null);
        await using (var b = NewContext(cs))
        {   // the desktop app never recorded which batch a unit left: simulate that
            foreach (var m in await b.StockMovements.Where(m => m.ReferenceType == "Invoice").ToListAsync()) m.BatchId = null;
            await b.SaveChangesAsync();
        }
        SaleResult second;
        await using (var c = NewContext(cs)) second = await new Services(c).Sales.SaveAsync(Sale(seed, 1, vat: 0), Clerk, null);
        await using (var d = NewContext(cs))
            foreach (var m in await d.StockMovements.Where(m => m.ReferenceId == second.InvoiceId).ToListAsync()) m.BatchId = null;

        await using (var e = NewContext(cs)) { await e.SaveChangesAsync(); await new Services(e).Voids.VoidAsync(sale.InvoiceId, "test", Clerk); }
        await using (var f = NewContext(cs)) await new Services(f).Voids.VoidAsync(second.InvoiceId, "test", Clerk);

        await using var check = NewContext(cs);
        Assert.Equal(10, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
        Assert.Single(await check.StockBatches.Where(b => b.BatchNumber == "RETURNED").ToListAsync());   // one shared batch, not one per invoice
    }
}
