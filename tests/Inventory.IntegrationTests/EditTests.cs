using Inventory.Application.Common;
using Inventory.Application.Purchasing;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>Editing saved sales, purchases and waybills: only the difference moves stock, and money follows the new total.</summary>
[Collection("mysql")]
public class EditTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private static InvoiceEditService Edits(BusinessDbContext db) => new(db, Wire.Tx(db), new StockService(db, Clock), Clock);
    private static PurchaseEditService PoEdits(BusinessDbContext db) => new(db, Wire.Tx(db), new StockService(db, Clock), Clock);

    /// <summary>A second product ("Senior") with its own batch, so a swap has somewhere to come from.</summary>
    private static async Task<int> AddProduct(string cs, int stock, bool serial = false, string name = "Senior Dog Food 20kg")
    {
        await using var db = NewContext(cs);
        var p = new Product { Sku = "SKU-" + Guid.NewGuid().ToString("N")[..6], Name = name, CategoryId = await db.Categories.Select(c => c.Id).FirstAsync(),
            CostPrice = 8000, PriceRetail = 11500, PriceWholesaler = 11000, PriceDistributor = 10500, SellingPrice = 11500, ReorderLevel = 5, TracksSerial = serial };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        db.StockBatches.Add(new StockBatch { ProductId = p.Id, WarehouseId = await db.Warehouses.Select(w => w.Id).FirstAsync(), BatchNumber = "S1", QuantityOnHand = stock });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static InvoiceEditRequest Lines(decimal vat = 7.5m, params (int Product, int Qty, decimal Price)[] lines) => new()
    {
        VatRate = vat, Lines = lines.Select(l => new SaleLineDto { ProductId = l.Product, Quantity = l.Qty, UnitPrice = l.Price }).ToList(),
    };

    private static async Task<int> OnHand(string cs, int productId)
    {
        await using var db = NewContext(cs);
        return await db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.QuantityOnHand);
    }

    private static async Task<SaleResult> Sell(string cs, SaleRequest req)
    {
        await using var db = NewContext(cs);
        return await SalesFor(db).SaveAsync(req, Clerk, null);
    }

    [Fact]
    public async Task Swapping_an_item_puts_the_old_one_back_in_its_batch_and_takes_the_new_one_with_no_change_in_what_is_owed()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var senior = await AddProduct(cs, stock: 6);
        var sale = await Sell(cs, Sale(seed, 2, paid: 5000));   // 2 × 11,500 + 7.5% = 24,725
        Assert.Equal(8, await OnHand(cs, seed.ProductId));

        InvoiceEditResult r;
        await using (var db = NewContext(cs)) r = await Edits(db).EditAsync(sale.InvoiceId, Lines(7.5m, (senior, 2, 11500)), Wire.Admin);

        Assert.Equal((24725m, 24725m), (r.OldTotal, r.NewTotal));
        Assert.Equal(("Adult Dog Food 20kg", 2), (Assert.Single(r.StockBack).Product, r.StockBack[0].Quantity));
        Assert.Equal(2, Assert.Single(r.StockOut).Quantity);
        Assert.Equal(10, await OnHand(cs, seed.ProductId));    // nothing counted twice
        Assert.Equal(4, await OnHand(cs, senior));

        await using var check = NewContext(cs);
        Assert.Equal(10, (await check.StockBatches.SingleAsync(b => b.ProductId == seed.ProductId)).QuantityOnHand);   // same batch, not a new "RETURNED" one
        var inv = await check.Invoices.Include(i => i.Items).SingleAsync();
        Assert.Equal(senior, Assert.Single(inv.Items).ProductId);
        Assert.Equal((24725m, 5000m, PaymentStatuses.Partial), (inv.TotalAmount, inv.AmountPaid, inv.Status));
        Assert.Equal(19725m, (await check.Customers.SingleAsync()).Balance);
        Assert.Contains(await check.AuditLogs.ToListAsync(), a => a.Action == "INVOICE_EDITED");
        Assert.Equal(2, await check.StockMovements.CountAsync(m => m.ReferenceType == MovementReferences.InvoiceEdit));
    }

    [Fact]
    public async Task A_bigger_order_adds_to_what_the_customer_owes()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var sale = await Sell(cs, Sale(seed, 2, vat: 0, paid: 23000));   // fully paid
        InvoiceEditResult r;
        await using (var db = NewContext(cs)) r = await Edits(db).EditAsync(sale.InvoiceId, Lines(0, (seed.ProductId, 3, 11500)), Wire.Admin);

        Assert.Equal((34500m, 11500m, PaymentStatuses.Partial), (r.NewTotal, r.Outstanding, r.Status));
        Assert.Equal(7, await OnHand(cs, seed.ProductId));
        await using var check = NewContext(cs);
        Assert.Equal(11500m, (await check.Customers.SingleAsync()).Balance);
        Assert.Contains(await check.Ledger.ToListAsync(), l => l.EntryType == LedgerEntryTypes.Debit && l.Amount == 11500m && l.Reference.StartsWith("EDIT"));
    }

    [Fact]
    public async Task Overpaid_after_an_edit_pays_another_open_sale_then_becomes_credit_that_the_next_sale_uses()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 20);
        var older = await Sell(cs, Sale(seed, 1, vat: 0));                  // 11,500 owed
        var paid = await Sell(cs, Sale(seed, 4, vat: 0, paid: 46000 + 11500));   // pays itself and the older one in full
        await Sell(cs, Sale(seed, 1, vat: 0));                              // another 11,500 owed
        await using (var db = NewContext(cs)) Assert.Equal(11500m, (await db.Customers.SingleAsync()).Balance);

        // Cut the paid sale from 4 to 1: 34,500 is now overpaid → 11,500 settles the open sale, 23,000 is credit.
        InvoiceEditResult r;
        await using (var db = NewContext(cs)) r = await Edits(db).EditAsync(paid.InvoiceId, Lines(0, (seed.ProductId, 1, 11500)), Wire.Admin);
        Assert.Equal((11500m, 23000m, PaymentStatuses.Paid), (r.MovedToOtherSales, r.CreditHeld, r.Status));
        Assert.Equal(17, await OnHand(cs, seed.ProductId));
        await using (var db = NewContext(cs))
        {
            Assert.Equal(-23000m, (await db.Customers.SingleAsync()).Balance);
            Assert.All(await db.Invoices.ToListAsync(), i => Assert.Equal(PaymentStatuses.Paid, i.Status));
        }

        // The next sale is paid from the credit first.
        var next = await Sell(cs, Sale(seed, 1, vat: 0));
        Assert.Equal(PaymentStatuses.Paid, next.Status);
        await using var check = NewContext(cs);
        Assert.Equal(-11500m, (await check.Customers.SingleAsync()).Balance);
        Assert.Equal(11500m, (await check.Invoices.SingleAsync(i => i.Id == next.InvoiceId)).AmountPaid);
        Assert.Contains(await check.Payments.ToListAsync(), p => p.InvoiceId == next.InvoiceId && p.Method == InvoiceEditService.AccountCredit);
    }

    [Fact]
    public async Task Not_enough_stock_for_the_new_item_refuses_the_edit_and_changes_nothing()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var senior = await AddProduct(cs, stock: 1);
        var sale = await Sell(cs, Sale(seed, 2));

        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<InsufficientStockException>(() => Edits(db).EditAsync(sale.InvoiceId, Lines(7.5m, (senior, 3, 11500)), Wire.Admin));

        Assert.Equal((8, 1), (await OnHand(cs, seed.ProductId), await OnHand(cs, senior)));
        await using var check = NewContext(cs);
        Assert.Equal(seed.ProductId, Assert.Single((await check.Invoices.Include(i => i.Items).SingleAsync()).Items).ProductId);
        Assert.Equal(0, await check.StockMovements.CountAsync(m => m.ReferenceType == MovementReferences.InvoiceEdit));
    }

    [Fact]
    public async Task Voiding_after_an_edit_returns_exactly_what_the_sale_still_holds()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var senior = await AddProduct(cs, stock: 6);
        var sale = await Sell(cs, Sale(seed, 3));
        await using (var db = NewContext(cs)) await Edits(db).EditAsync(sale.InvoiceId, Lines(7.5m, (seed.ProductId, 1, 11500), (senior, 2, 11500)), Wire.Admin);
        Assert.Equal((9, 4), (await OnHand(cs, seed.ProductId), await OnHand(cs, senior)));

        await using (var db = NewContext(cs)) await new Services(db).Voids.VoidAsync(sale.InvoiceId, "Customer cancelled", Wire.Admin);

        Assert.Equal((10, 6), (await OnHand(cs, seed.ProductId), await OnHand(cs, senior)));
        await using var check = NewContext(cs);
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task Serial_tracked_quantities_and_voided_sales_cannot_be_edited()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var scale = await AddProduct(cs, stock: 5, serial: true, name: "Digital Scale");
        var sale = await Sell(cs, Sale(seed, 1));

        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => Edits(db).EditAsync(sale.InvoiceId, Lines(7.5m, (seed.ProductId, 1, 11500), (scale, 1, 20000)), Wire.Admin));

        await using (var db = NewContext(cs)) await new Services(db).Voids.VoidAsync(sale.InvoiceId, "Wrong customer", Wire.Admin);
        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => Edits(db).EditAsync(sale.InvoiceId, Lines(7.5m, (seed.ProductId, 2, 11500)), Wire.Admin));
    }

    // ------------------------------------------------------------------ purchases

    private static async Task<int> AddSupplier(string cs)
    {
        await using var db = NewContext(cs);
        var s = new Supplier { Name = "AgroFeed" };
        db.Suppliers.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static async Task<PurchaseResult> Buy(string cs, int supplier, int product, int qty, decimal cost, bool receive, decimal paid = 0)
    {
        await using var db = NewContext(cs);
        return await new Services(db).Purchases.SaveAsync(new PurchaseRequest
        {
            SupplierId = supplier, ReceiveNow = receive, PaidNow = paid, Lines = [new PurchaseLineDto { ProductId = product, Quantity = qty, UnitCost = cost }],
        }, Wire.Admin, null);
    }

    private static PurchaseEditRequest PoLines(params (int Product, int Qty, decimal Cost)[] lines) => new()
    {
        Lines = lines.Select(l => new PurchaseLineDto { ProductId = l.Product, Quantity = l.Qty, UnitCost = l.Cost }).ToList(),
    };

    [Fact]
    public async Task A_pending_order_edit_changes_the_order_and_what_we_owe_but_no_stock()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        var po = await Buy(cs, supplier, seed.ProductId, 10, 9000, receive: false);

        PurchaseEditResult r;
        await using (var db = NewContext(cs)) r = await PoEdits(db).EditAsync(po.PurchaseOrderId, PoLines((seed.ProductId, 12, 9000)), Wire.Admin);
        Assert.Equal((90000m, 108000m), (r.OldTotal, r.NewTotal));
        Assert.Empty(r.StockIn);
        Assert.Equal(0, await OnHand(cs, seed.ProductId));
        await using var check = NewContext(cs);
        Assert.Equal(108000m, (await check.Suppliers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task A_received_order_moves_stock_in_its_own_batch_and_refuses_to_remove_units_already_sold()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 0);
        var supplier = await AddSupplier(cs);
        var po = await Buy(cs, supplier, seed.ProductId, 10, 9000, receive: true, paid: 90000);

        // Received 12, not 10.
        await using (var db = NewContext(cs)) await PoEdits(db).EditAsync(po.PurchaseOrderId, PoLines((seed.ProductId, 12, 9000)), Wire.Admin);
        Assert.Equal(12, await OnHand(cs, seed.ProductId));
        await using (var db = NewContext(cs)) Assert.Equal(18000m, (await db.Suppliers.SingleAsync()).Balance);

        // Sell 8 out of the order's batch; now only 4 can be taken off.
        await Sell(cs, Sale(seed, 8));
        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => PoEdits(db).EditAsync(po.PurchaseOrderId, PoLines((seed.ProductId, 5, 9000)), Wire.Admin));
        Assert.Equal(4, await OnHand(cs, seed.ProductId));

        // Down to 8: the 4 still on the shelf leave, and we paid 18,000 more than the order now costs → credit with the supplier.
        PurchaseEditResult r;
        await using (var db = NewContext(cs)) r = await PoEdits(db).EditAsync(po.PurchaseOrderId, PoLines((seed.ProductId, 8, 9000)), Wire.Admin);
        Assert.Equal((4, 18000m), (Assert.Single(r.StockOut).Quantity, r.CreditHeld));
        Assert.Equal(0, await OnHand(cs, seed.ProductId));
        await using (var db = NewContext(cs)) Assert.Equal(-18000m, (await db.Suppliers.SingleAsync()).Balance);

        // The next order uses that credit first.
        var next = await Buy(cs, supplier, seed.ProductId, 1, 9000, receive: false);
        Assert.Equal(PaymentStatuses.Paid, next.Status);
        await using var check = NewContext(cs);
        Assert.Equal(-9000m, (await check.Suppliers.SingleAsync()).Balance);
        Assert.Contains(await check.AuditLogs.ToListAsync(), a => a.Action == "PURCHASE_EDITED");
    }

    // ------------------------------------------------------------------ waybills

    [Fact]
    public async Task A_waybill_can_be_corrected_but_stays_on_its_sale()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var sale = await Sell(cs, Sale(seed, 1));
        var other = await Sell(cs, Sale(seed, 1));
        int id;
        await using (var db = NewContext(cs)) id = await Wire.Waybills(db).CreateAsync(new WaybillInput { InvoiceId = sale.InvoiceId, DriverName = "Musa" }, Clerk);

        await using (var db = NewContext(cs))
            await Wire.Waybills(db).UpdateAsync(id, new WaybillInput { InvoiceId = other.InvoiceId, DriverName = "Tunde", DriverPhone = "08012345678", VehiclePlate = "LSD 123 AB", DestinationAddress = "5 Awolowo Rd, Ikoyi" }, Wire.Admin);

        await using var check = NewContext(cs);
        var w = await check.Waybills.SingleAsync();
        Assert.Equal((sale.InvoiceId, "Tunde", "08012345678", "LSD 123 AB", "5 Awolowo Rd, Ikoyi"), (w.InvoiceId, w.DriverName, w.DriverPhone, w.VehiclePlate, w.DestinationAddress));
        Assert.Contains("Musa → Tunde", (await check.AuditLogs.SingleAsync(a => a.Action == "WAYBILL_EDITED")).Detail);
    }
}
