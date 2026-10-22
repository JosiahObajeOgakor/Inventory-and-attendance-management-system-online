using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

/// <summary>The live figures a spreadsheet can pull in with =FIG("name").</summary>
public sealed class FiguresService(IBusinessDbContext db, IClock clock)
{
    public static readonly IReadOnlyList<(string Name, string Label)> Names =
    [
        ("sales_today", "Sales today"), ("sales_month", "Sales this month"), ("sales_year", "Sales this year"), ("expenses_month", "Expenses this month"),
        ("receivables", "Owed to you by customers"), ("payables", "You owe suppliers"), ("stock_value", "Stock at cost"), ("customers", "Customers"), ("products", "Active products"),
    ];

    public async Task<Dictionary<string, decimal>> AllAsync(CancellationToken ct = default)
    {
        var today = clock.BusinessToday;
        var month = new DateOnly(today.Year, today.Month, 1); var year = new DateOnly(today.Year, 1, 1);
        var live = db.Invoices.AsNoTracking().Where(i => i.VoidedAt == null && !i.IsSample);
        decimal Sum(decimal? v) => v ?? 0;
        return new Dictionary<string, decimal>
        {
            ["sales_today"] = Sum(await live.Where(i => i.InvoiceDate == today).SumAsync(i => (decimal?)i.TotalAmount, ct)),
            ["sales_month"] = Sum(await live.Where(i => i.InvoiceDate >= month).SumAsync(i => (decimal?)i.TotalAmount, ct)),
            ["sales_year"] = Sum(await live.Where(i => i.InvoiceDate >= year).SumAsync(i => (decimal?)i.TotalAmount, ct)),
            ["expenses_month"] = Sum(await db.Expenses.AsNoTracking().Where(e => e.ExpenseDate >= month).SumAsync(e => (decimal?)e.Amount, ct)),
            ["receivables"] = Sum(await db.Customers.AsNoTracking().Where(c => c.Balance > 0).SumAsync(c => (decimal?)c.Balance, ct)),
            ["payables"] = Sum(await db.Suppliers.AsNoTracking().Where(s => s.Balance > 0).SumAsync(s => (decimal?)s.Balance, ct)),
            ["stock_value"] = Sum(await (from b in db.StockBatches.AsNoTracking() join p in db.Products on b.ProductId equals p.Id select (decimal?)(b.QuantityOnHand * p.CostPrice)).SumAsync(ct)),
            ["customers"] = await db.Customers.AsNoTracking().CountAsync(c => c.CustomerType != CustomerTypes.WalkIn, ct),
            ["products"] = await db.Products.AsNoTracking().CountAsync(p => p.IsActive, ct),
        };
    }
}
