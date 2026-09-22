using Inventory.Domain;
using Inventory.Domain.Rules;

namespace Inventory.UnitTests;

// Cases mirror the desktop MSTest suite (CustomerDebtTests, StockTests, SupplierDebtTests).
public class DocumentCalculatorTests
{
    [Fact]
    public void Sale_totals_use_discount_then_vat()
    {
        var t = DocumentCalculator.Sale([new(1, 2, 1000m), new(2, 1, 500m)], discountPct: 10, vatRate: 7.5m);
        Assert.Equal(2500m, t.Subtotal);
        Assert.Equal(250m, t.DiscountAmount);
        Assert.Equal(168.75m, t.VatAmount);
        Assert.Equal(2418.75m, t.Total);
    }

    [Fact]
    public void Rounding_is_bankers_like_vb_math_round()
    {
        // 0.125 -> 0.12 (ToEven), not 0.13 (AwayFromZero)
        Assert.Equal(0.12m, Money.Round(0.125m));
        Assert.Equal(0.14m, Money.Round(0.135m));
        Assert.Equal(0.13m, Money.RoundAwayFromZero(0.125m));
    }

    [Fact]
    public void Sale_without_vat_or_discount_is_the_subtotal()
    {
        var t = DocumentCalculator.Sale([new(1, 3, 100m)], 0, 0);
        Assert.Equal(300m, t.Total);
    }

    [Fact]
    public void Purchase_totals()
    {
        var t = DocumentCalculator.Purchase([(10, 250m)], vatRate: 7.5m);
        Assert.Equal(2500m, t.Subtotal);
        Assert.Equal(187.5m, t.VatAmount);
        Assert.Equal(2687.5m, t.Total);
    }
}

public class PaymentWaterfallTests
{
    [Fact]
    public void Customer_with_no_prior_debt_behaves_as_before()
    {
        var s = PaymentWaterfall.ForNewDocument(0, 1000, 400);
        Assert.Equal(400, s.AppliedToNew);
        Assert.Equal(0, s.Overflow);
        Assert.Equal(600, s.Outstanding);
        Assert.Equal("Partial", s.Status);
    }

    [Fact]
    public void Paying_more_than_todays_sale_clears_old_debt_after_todays_invoice()
    {
        var s = PaymentWaterfall.ForNewDocument(previousBalance: 500, newTotal: 1000, requestedPaid: 1200);
        Assert.Equal(1000, s.AppliedToNew);
        Assert.Equal(200, s.Overflow);
        Assert.Equal(0, s.Outstanding);
        Assert.Equal("Paid", s.Status);
    }

    [Fact]
    public void Overpaying_beyond_everything_owed_is_capped_not_lost_as_credit()
    {
        var s = PaymentWaterfall.ForNewDocument(500, 1000, 999_999);
        Assert.Equal(1500, s.PaidNow);
        Assert.Equal(500, s.Overflow);
    }

    [Fact]
    public void Not_covering_todays_sale_leaves_old_debt_untouched()
    {
        var s = PaymentWaterfall.ForNewDocument(500, 1000, 300);
        Assert.Equal(300, s.AppliedToNew);
        Assert.Equal(0, s.Overflow);
        Assert.Equal(700, s.Outstanding);
    }

    [Fact]
    public void Negative_payment_is_treated_as_zero()
    {
        var s = PaymentWaterfall.ForNewDocument(0, 100, -5);
        Assert.Equal(0, s.PaidNow);
        Assert.Equal("Unpaid", s.Status);
    }

    [Fact]
    public void Old_invoices_are_paid_oldest_first_without_overpaying_one()
    {
        var open = new[] { new OpenDocument(1, 300, 0), new OpenDocument(2, 500, 100), new OpenDocument(3, 900, 0) };
        var (apps, applied) = PaymentWaterfall.Spread(open, 600);
        Assert.Equal(600, applied);
        Assert.Equal([new PaymentApplication(1, 300), new PaymentApplication(2, 300)], apps);
    }

    [Fact]
    public void Spread_never_exceeds_what_is_outstanding()
    {
        var (apps, applied) = PaymentWaterfall.Spread([new OpenDocument(1, 300, 0)], 5000);
        Assert.Equal(300, applied);
        Assert.Single(apps);
    }
}

public class StockAllocatorTests
{
    private static readonly DateOnly D = new(2026, 12, 1);

    [Fact]
    public void Drains_chosen_warehouse_first_then_by_expiry()
    {
        var batches = new[]
        {
            new BatchSnapshot(1, WarehouseId: 2, 10, D.AddDays(-30)),   // other warehouse, earlier expiry
            new BatchSnapshot(2, WarehouseId: 1, 5, D.AddDays(30)),
            new BatchSnapshot(3, WarehouseId: 1, 5, D),
            new BatchSnapshot(4, WarehouseId: 1, 5, null),
        };
        var plan = StockAllocator.Plan(batches, preferredWarehouseId: 1, quantity: 12)!;
        Assert.Equal([3, 2, 4], plan.Select(p => p.BatchId));
        Assert.Equal([5, 5, 2], plan.Select(p => p.Quantity));
    }

    [Fact]
    public void Falls_back_to_other_warehouses_only_when_needed()
    {
        var batches = new[] { new BatchSnapshot(1, 1, 3, null), new BatchSnapshot(2, 2, 10, null) };
        var plan = StockAllocator.Plan(batches, 1, 8)!;
        Assert.Equal([1, 2], plan.Select(p => p.BatchId));
        Assert.Equal([3, 5], plan.Select(p => p.Quantity));
    }

    [Fact]
    public void Returns_null_when_stock_cannot_cover_the_line()
    {
        Assert.Null(StockAllocator.Plan([new BatchSnapshot(1, 1, 3, null)], 1, 4));
    }

    [Fact]
    public void Empty_batches_are_ignored()
    {
        Assert.Null(StockAllocator.Plan([new BatchSnapshot(1, 1, 0, null)], 1, 1));
    }
}

public class RestockAdvisorTests
{
    [Fact]
    public void No_sales_history_falls_back_to_reorder_level()
    {
        var a = RestockAdvisor.Advise(onHand: 4, reorderLevel: 10, soldInWindow: 0);
        Assert.Equal(6, a.SuggestedOrderQty);
        Assert.Equal("Reorder now", a.Urgency);
    }

    [Fact]
    public void Out_of_stock_is_flagged()
    {
        Assert.Equal("Out of stock", RestockAdvisor.Advise(0, 5, 90).Urgency);
    }

    [Fact]
    public void Suggested_quantity_covers_lead_time_plus_cover_days()
    {
        // 90 sold in 90 days = 1/day; target = ceil(1 * 37) = 37; reorder point = ceil(10.5) = 11
        var a = RestockAdvisor.Advise(onHand: 20, reorderLevel: 5, soldInWindow: 90);
        Assert.Equal(1m, a.AvgDailyDemand);
        Assert.Equal(11, a.ReorderPoint);
        Assert.Equal(17, a.SuggestedOrderQty);
        Assert.Equal("Low stock", a.Urgency);
    }
}

public class RebateAndRankingTests
{
    [Fact]
    public void Walk_in_customers_accrue_no_rebate() =>
        Assert.Equal(0m, RebateCalculator.Accrue("Walk-in", 1075m, 75m, 1m));

    [Fact]
    public void Rebate_is_on_net_sales() =>
        Assert.Equal(10m, RebateCalculator.Accrue("Distributor", 1075m, 75m, 1m));

    [Theory]
    [InlineData(800_000, "Gold")]
    [InlineData(799_999.99, "Silver")]
    [InlineData(400_000, "Silver")]
    [InlineData(399_999, "Bronze")]
    public void Ranking_thresholds(double spend, string tier) =>
        Assert.Equal(tier, CustomerRanking.For((decimal)spend));
}
