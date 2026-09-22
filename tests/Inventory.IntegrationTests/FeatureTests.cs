using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Finance;
using Inventory.Application.Products;
using Inventory.Application.Queries;
using Inventory.Application.Sales;
using Inventory.Application.Staff;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class NoUsers : IUserDirectory
{
    public Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids, CancellationToken ct = default) => Task.FromResult(new Dictionary<int, string>());
    public Task<Dictionary<int, UserBrief>> BriefsAsync(IEnumerable<int> ids, CancellationToken ct = default) => Task.FromResult(new Dictionary<int, UserBrief>());
}

internal static class Wire
{
    public static readonly CurrentUser Admin = new(2, "Ada Admin", "ADMIN");
    private static readonly SystemClock Clock = new();
    public static ICompanyContext Co(bool priceLists = false) => new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", priceLists));
    public static TransactionRunner Tx(BusinessDbContext db) => new(db, new MySqlErrorClassifier());
    public static QuotationService Quotes(BusinessDbContext db) => new(db, Tx(db), Clock, Co(), SalesFor(db), new NoUsers());
    public static WaybillService Waybills(BusinessDbContext db) => new(db, Tx(db), Clock, Co());
    public static PayrollService Payroll(BusinessDbContext db) => new(db, Tx(db), Clock);
    public static SerialService Serials(BusinessDbContext db) => new(db, Tx(db), Clock);
    public static PriceBookService PriceBook(BusinessDbContext db, bool lists = true) => new(db, Tx(db), Clock, Co(lists), new NoUsers());
    public static RebateService Rebates(BusinessDbContext db) => new(db, Tx(db), Clock, new Inventory.Application.Company.CompanyProfileService(db, Co(), Clock));
}

[Collection("mysql")]
public class QuotationWaybillTests(MySqlFixture mysql)
{
    private static QuoteRequest Quote(Seed s, int qty) => new() { CustomerId = s.CustomerId, VatRate = 7.5m, Lines = [new SaleLineDto { ProductId = s.ProductId, Quantity = qty, UnitPrice = 11500 }] };

    [Fact]
    public async Task A_quotation_touches_no_stock_or_balance_until_converted()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using var db = NewContext(cs);
        var q = await Wire.Quotes(db).CreateAsync(Quote(seed, 3), Clerk);
        Assert.Equal(37087.5m, q.Total);

        await using var check = NewContext(cs);
        Assert.Equal(10, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
        Assert.Empty(await check.Invoices.ToListAsync());
        Assert.Equal(QuotationStatuses.Open, (await check.Quotations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Converting_makes_one_sale_and_marks_the_quote_converted_and_cannot_be_repeated()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using var db = NewContext(cs);
        var q = await Wire.Quotes(db).CreateAsync(Quote(seed, 3), Clerk);
        var sale = await Wire.Quotes(db).ConvertAsync(q.Id, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId, PaidNow = 10000 }, Clerk);
        Assert.Equal(q.Total, sale.Total);

        await using var check = NewContext(cs);
        var quote = await check.Quotations.SingleAsync();
        Assert.Equal((QuotationStatuses.Converted, sale.InvoiceId), (quote.Status, quote.ConvertedInvoiceId));
        Assert.Equal(7, await check.StockBatches.SumAsync(b => b.QuantityOnHand));

        await using var again = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Quotes(again).ConvertAsync(q.Id, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId }, Clerk));
        await using var after = NewContext(cs);
        Assert.Single(await after.Invoices.ToListAsync());
    }

    [Fact]
    public async Task A_refused_conversion_leaves_the_quote_open_and_writes_no_invoice()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 2);
        await using var db = NewContext(cs);
        var q = await Wire.Quotes(db).CreateAsync(Quote(seed, 5), Clerk);
        await using var db2 = NewContext(cs);
        await Assert.ThrowsAsync<InsufficientStockException>(() => Wire.Quotes(db2).ConvertAsync(q.Id, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId }, Clerk));

        await using var check = NewContext(cs);
        Assert.Equal(QuotationStatuses.Open, (await check.Quotations.SingleAsync()).Status);
        Assert.Empty(await check.Invoices.ToListAsync());
        Assert.Equal(2, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
    }

    [Fact]
    public async Task Only_open_quotes_can_be_deleted()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using var db = NewContext(cs);
        var open = await Wire.Quotes(db).CreateAsync(Quote(seed, 1), Clerk);
        var conv = await Wire.Quotes(db).CreateAsync(Quote(seed, 1), Clerk);
        await Wire.Quotes(db).ConvertAsync(conv.Id, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId }, Clerk);
        await using var d2 = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Quotes(d2).DeleteAsync(conv.Id, Wire.Admin));
        await Wire.Quotes(d2).DeleteAsync(open.Id, Wire.Admin);
        await using var check = NewContext(cs);
        Assert.Single(await check.Quotations.ToListAsync());
    }

    [Fact]
    public async Task A_waybill_needs_a_live_invoice_and_gets_a_number()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using var db = NewContext(cs);
        var sale = await SalesFor(db).SaveAsync(Sale(seed, 1), Clerk, null);
        var id = await Wire.Waybills(db).CreateAsync(new WaybillInput { InvoiceId = sale.InvoiceId, DriverName = "Musa", VehiclePlate = "ABC-123" }, Clerk);

        await using var check = NewContext(cs);
        var w = await check.Waybills.SingleAsync(x => x.Id == id);
        Assert.StartsWith("TestCo", w.WaybillNumber);
        var pending = await Wire.Waybills(check).InvoicesAsync(new PageRequest(), pendingOnly: true, default);
        Assert.Empty(pending.Items);

        await using var v = NewContext(cs);
        await new Services(v).Voids.VoidAsync(sale.InvoiceId, "test", Wire.Admin);
        await using var v2 = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Waybills(v2).CreateAsync(new WaybillInput { InvoiceId = sale.InvoiceId }, Clerk));
    }
}

[Collection("mysql")]
public class PayrollTests(MySqlFixture mysql)
{
    private static async Task<int> Employee(string cs, decimal salary = 100000)
    {
        await using var db = NewContext(cs);
        return await Wire.Payroll(db).CreateEmployeeAsync(new EmployeeInput("Ngozi Eze", "Packer", null, null, salary), Wire.Admin);
    }

    [Fact]
    public async Task Generating_a_month_takes_a_sixth_of_the_open_loan_and_is_repeatable()
    {
        var cs = await mysql.NewSchemaAsync();
        var emp = await Employee(cs);
        await using (var db = NewContext(cs)) await Wire.Payroll(db).AddLoanAsync(emp, 60000, null, Wire.Admin);
        await using var g = NewContext(cs);
        Assert.Equal(1, await Wire.Payroll(g).GenerateMonthAsync(2026, 9, Wire.Admin));
        await using var g2 = NewContext(cs);
        Assert.Equal(0, await Wire.Payroll(g2).GenerateMonthAsync(2026, 9, Wire.Admin));

        await using var check = NewContext(cs);
        var row = await check.EmployeeMonthlies.SingleAsync();
        Assert.Equal((100000m, 10000m), (row.SalaryAmount, row.LoanDeduction));
    }

    [Fact]
    public async Task Paying_repays_the_loan_posts_net_pay_as_an_expense_and_cannot_be_paid_twice()
    {
        var cs = await mysql.NewSchemaAsync();
        var emp = await Employee(cs);
        await using (var db = NewContext(cs)) await Wire.Payroll(db).AddLoanAsync(emp, 60000, null, Wire.Admin);
        await using (var g = NewContext(cs)) await Wire.Payroll(g).GenerateMonthAsync(2026, 9, Wire.Admin);
        int rowId;
        await using (var c = NewContext(cs)) rowId = (await c.EmployeeMonthlies.SingleAsync()).Id;

        await using var p1 = NewContext(cs);
        var first = await Wire.Payroll(p1).MarkPaidAsync(rowId, Wire.Admin);
        Assert.Equal((90000m, false), (first.NetPay, first.AlreadyPaid));
        await using var p2 = NewContext(cs);
        Assert.True((await Wire.Payroll(p2).MarkPaidAsync(rowId, Wire.Admin)).AlreadyPaid);

        await using var check = NewContext(cs);
        Assert.Equal(50000m, (await check.EmployeeLoans.SingleAsync()).Balance);
        var exp = await check.Expenses.SingleAsync();
        Assert.Equal(("Salaries", 90000m), (exp.Category, exp.Amount));
        Assert.Single(await check.LoanRepayments.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_payments_of_one_row_pay_it_once()
    {
        var cs = await mysql.NewSchemaAsync();
        var emp = await Employee(cs);
        await using (var g = NewContext(cs)) await Wire.Payroll(g).GenerateMonthAsync(2026, 9, Wire.Admin);
        int rowId;
        await using (var c = NewContext(cs)) rowId = (await c.EmployeeMonthlies.SingleAsync()).Id;
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = NewContext(cs);
            return await Wire.Payroll(db).MarkPaidAsync(rowId, Wire.Admin);
        }));
        Assert.Equal(1, results.Count(r => !r.AlreadyPaid));
        await using var check = NewContext(cs);
        Assert.Single(await check.Expenses.ToListAsync());
    }

    [Fact]
    public async Task Paid_rows_are_frozen_and_employees_with_records_cannot_be_deleted()
    {
        var cs = await mysql.NewSchemaAsync();
        var emp = await Employee(cs);
        await using (var g = NewContext(cs)) await Wire.Payroll(g).GenerateMonthAsync(2026, 9, Wire.Admin);
        int rowId;
        await using (var c = NewContext(cs)) rowId = (await c.EmployeeMonthlies.SingleAsync()).Id;
        await using (var p = NewContext(cs)) await Wire.Payroll(p).MarkPaidAsync(rowId, Wire.Admin);

        await using var db = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Payroll(db).EditRowAsync(rowId, new PayrollEditInput(1, 0, null), Wire.Admin));
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Payroll(db).DeleteRowAsync(rowId, Wire.Admin));
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Payroll(db).DeleteEmployeeAsync(emp, Wire.Admin));
    }

    [Fact]
    public async Task A_manual_repayment_cannot_exceed_the_balance()
    {
        var cs = await mysql.NewSchemaAsync();
        var emp = await Employee(cs);
        int loan;
        await using (var db = NewContext(cs)) loan = await Wire.Payroll(db).AddLoanAsync(emp, 5000, "advance", Wire.Admin);
        await using var r = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Payroll(r).RepayLoanAsync(loan, 6000, null, Wire.Admin));
        await Wire.Payroll(r).RepayLoanAsync(loan, 5000, null, Wire.Admin);
        await using var check = NewContext(cs);
        var l = await check.EmployeeLoans.SingleAsync();
        Assert.Equal((0m, true), (l.Balance, l.Closed));
    }
}

[Collection("mysql")]
public class SerialTests(MySqlFixture mysql)
{
    private static async Task<Seed> TrackedSeed(string cs, int stock)
    {
        var s = await SeedAsync(cs, stock);
        await using var db = NewContext(cs);
        (await db.Products.SingleAsync()).TracksSerial = true;
        await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Receiving_rejects_duplicates_and_products_that_do_not_track_serials()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, 0);
        await using var db = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["A1"], null, Clerk));
        (await db.Products.SingleAsync()).TracksSerial = true;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["A1", "a1"], null, Clerk));
        await Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["A1", "A2"], null, Clerk);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["A2", "A3"], null, Clerk));
        await using var check = NewContext(cs);
        Assert.Equal(2, await check.ProductSerials.CountAsync());
    }

    [Fact]
    public async Task Selling_marks_serials_sold_and_voiding_puts_them_back()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await TrackedSeed(cs, 2);
        await using (var db = NewContext(cs)) await Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["S1", "S2"], null, Clerk);

        var req = Sale(seed, 2);
        req.Lines[0].Serials = ["S1", "S2"];
        await using var s = NewContext(cs);
        var sale = await SalesFor(s).SaveAsync(req, Clerk, null);
        await using (var c = NewContext(cs))
            Assert.All(await c.ProductSerials.ToListAsync(), x => Assert.Equal((SerialStatuses.Sold, (int?)sale.InvoiceId), (x.Status, x.InvoiceId)));

        await using var v = NewContext(cs);
        await new Services(v).Voids.VoidAsync(sale.InvoiceId, "returned", Wire.Admin);
        await using var check = NewContext(cs);
        Assert.All(await check.ProductSerials.ToListAsync(), x => Assert.Equal(SerialStatuses.InStock, x.Status));
    }

    [Fact]
    public async Task A_sale_must_name_exactly_as_many_serials_as_units_and_only_ones_in_stock()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await TrackedSeed(cs, 3);
        await using (var db = NewContext(cs)) await Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["S1", "S2", "S3"], null, Clerk);

        var wrongCount = Sale(seed, 2); wrongCount.Lines[0].Serials = ["S1"];
        await using var a = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => SalesFor(a).SaveAsync(wrongCount, Clerk, null));

        var unknown = Sale(seed, 1); unknown.Lines[0].Serials = ["NOPE"];
        await using var b = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => SalesFor(b).SaveAsync(unknown, Clerk, null));

        await using var check = NewContext(cs);
        Assert.Equal(3, await check.StockBatches.SumAsync(x => x.QuantityOnHand));   // nothing was sold
        Assert.Empty(await check.Invoices.ToListAsync());
    }

    [Fact]
    public async Task Take_back_and_write_off_follow_the_status_rules()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await TrackedSeed(cs, 1);
        await using (var db = NewContext(cs)) await Wire.Serials(db).ReceiveAsync(seed.ProductId, seed.WarehouseId, ["S1"], null, Clerk);
        await using var d = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Serials(d).TakeBackAsync(seed.ProductId, "S1", seed.WarehouseId, null, Clerk));   // not sold yet
        var req = Sale(seed, 1); req.Lines[0].Serials = ["S1"];
        await using (var s = NewContext(cs)) await SalesFor(s).SaveAsync(req, Clerk, null);
        await using var d2 = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Serials(d2).WriteOffAsync(seed.ProductId, "S1", "broken", Wire.Admin));   // sold units can't be written off
        await Wire.Serials(d2).TakeBackAsync(seed.ProductId, "S1", seed.WarehouseId, "faulty", Clerk);
        await Wire.Serials(d2).WriteOffAsync(seed.ProductId, "S1", "faulty", Wire.Admin);
        await using var check = NewContext(cs);
        Assert.Equal(SerialStatuses.WrittenOff, (await check.ProductSerials.SingleAsync()).Status);
    }
}

[Collection("mysql")]
public class FinanceAndPriceBookTests(MySqlFixture mysql)
{
    [Fact]
    public async Task Redeeming_marks_every_accrued_rebate_and_only_once()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, 10);
        await using (var db = NewContext(cs)) { await SalesFor(db).SaveAsync(Sale(seed, 1), Clerk, null); }
        await using (var db = NewContext(cs)) { await SalesFor(db).SaveAsync(Sale(seed, 2), Clerk, null); }
        await using var r = NewContext(cs);
        var res = await Wire.Rebates(r).RedeemAsync(seed.CustomerId, Wire.Admin);
        Assert.Equal(345m, res.Amount);   // 1% of 11,500 + 1% of 23,000
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.Rebates(r).RedeemAsync(seed.CustomerId, Wire.Admin));
        await using var check = NewContext(cs);
        Assert.All(await check.RebateEntries.ToListAsync(), e => Assert.Equal("Redeemed", e.Status));
    }

    [Fact]
    public async Task Expenses_validate_category_and_amount()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var svc = new ExpenseService(db, new SystemClock());
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => svc.CreateAsync(new ExpenseInput("Snacks", null, 100, null), Wire.Admin, default));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => svc.CreateAsync(new ExpenseInput("Rent", null, 0, null), Wire.Admin, default));
        var id = await svc.CreateAsync(new ExpenseInput("Rent", null, 250000, "Shop"), Wire.Admin, default);
        Assert.True(id > 0);
    }

    [Fact]
    public void Percent_adjustments_round_half_up_to_the_chosen_step()
    {
        Assert.Equal(1150m, PriceBookService.Adjusted(1000, 15, 0));
        Assert.Equal(1150m, PriceBookService.Adjusted(1000, 15, 50));
        Assert.Equal(1100m, PriceBookService.Adjusted(1049, 5, 100));   // 1101.45 -> nearest 100
        Assert.Equal(0m, PriceBookService.Adjusted(100, -150, 0));      // never negative
    }

    [Fact]
    public async Task Applying_prices_logs_only_real_changes_and_is_refused_for_companies_without_a_price_book()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, 1);
        await using var db = NewContext(cs);
        var book = Wire.PriceBook(db);
        Assert.Equal(0, await book.ApplyAsync([new PriceUpdateInput(seed.ProductId, 10500, 11000, 11500)], null, Wire.Admin, default));   // same as now
        Assert.Equal(1, await book.ApplyAsync([new PriceUpdateInput(seed.ProductId, 10500, 11000, 12000)], "Q4", Wire.Admin, default));
        await using var check = NewContext(cs);
        var ch = await check.PriceChanges.SingleAsync();
        Assert.Equal((11500m, 12000m, "Q4"), (ch.OldRetail, ch.NewRetail, ch.Note));
        Assert.Equal(12000m, (await check.Products.SingleAsync()).SellingPrice);
        await Assert.ThrowsAsync<NotFoundException>(() => Wire.PriceBook(db, lists: false).ApplyAsync([new PriceUpdateInput(seed.ProductId, 1, 1, 1)], null, Wire.Admin, default));
    }

    [Fact]
    public async Task Adding_price_list_products_mints_codes_and_rejects_duplicates_atomically()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, 1);
        await using var db = NewContext(cs);
        var book = Wire.PriceBook(db);
        var ids = await book.AddProductsAsync([new("Cat Treats 100g", seed.ProductId, "Pack", 500, 600, 700), new("Litter 5kg", seed.ProductId, null, 900, 1000, 1200)], null, Wire.Admin, default);
        Assert.Equal(2, ids.Count);
        await using var check = NewContext(cs);
        var p = await check.Products.SingleAsync(x => x.Id == ids[0]);
        Assert.Equal($"TC-{p.Id:D5}", p.Sku);
        Assert.NotNull(p.Barcode);

        await using var d2 = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Wire.PriceBook(d2).AddProductsAsync([new("Brand New", seed.ProductId, null, 1, 1, 1), new("Litter 5kg", seed.ProductId, null, 1, 1, 1)], null, Wire.Admin, default));
        await using var after = NewContext(cs);
        Assert.False(await after.Products.AnyAsync(x => x.Name == "Brand New"));   // all or nothing
    }

    [Fact]
    public async Task Attendance_logs_every_event_and_summarises_per_person_per_day()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var svc = new AttendanceService(db, new SystemClock());
        await svc.CheckInAsync(Clerk, default);
        await svc.CheckInAsync(Clerk, default);
        await svc.CheckOutAsync(Clerk, default);
        var today = await svc.TodayAsync(Clerk.Id, default);
        Assert.Equal(2, today.CheckIns);
        Assert.NotNull(today.LastCheckOutAt);
        var day = await svc.DailySummaryAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), default);
        Assert.Equal(2, Assert.Single(day).CheckIns);
    }
}
