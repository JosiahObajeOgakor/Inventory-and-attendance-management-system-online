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
    public async Task Deleting_a_product_never_removes_stock_silently()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 4);
        await using var db = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => new Services(db).Products.DeleteAsync(seed.ProductId, Clerk));
        await using var check = NewContext(cs);
        Assert.Equal(4, await check.StockBatches.SumAsync(b => b.QuantityOnHand));   // the desktop app deleted these first
    }

    [Fact]
    public async Task A_product_with_history_and_no_stock_is_deactivated_not_deleted()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 1);
        await using (var a = NewContext(cs)) await new Services(a).Sales.SaveAsync(Sale(seed, 1), Clerk, null);
        await using var db = NewContext(cs);
        Assert.False(await new Services(db).Products.DeleteAsync(seed.ProductId, Clerk));
        await using var check = NewContext(cs);
        Assert.False((await check.Products.SingleAsync()).IsActive);
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
