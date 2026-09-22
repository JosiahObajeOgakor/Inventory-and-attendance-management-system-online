using Inventory.Domain.Rules;

namespace Inventory.UnitTests;

public class AnalyticsTests
{
    [Fact]
    public void Holt_winters_follows_a_steady_climb_and_never_goes_negative()
    {
        var f = Forecasting.HoltWinters([10, 12, 14, 16, 18, 20], 2);
        Assert.True(f.Values[0] > 18 && f.Values[1] > f.Values[0]);
        Assert.All(Forecasting.HoltWinters([5, 3, 1, 0, 0, 0], 4).Values, v => Assert.True(v >= 0));
    }

    [Fact]
    public void A_short_history_is_answered_with_the_average_and_says_so()
    {
        var f = Forecasting.HoltWinters([4, 6], 3);
        Assert.All(f.Values, v => Assert.Equal(5, v));
        Assert.Contains("average, not a trend", f.Basis);
        Assert.Equal("No sales history yet.", Forecasting.HoltWinters([], 2).Basis);
    }

    [Fact]
    public void Differencing_and_undifferencing_are_inverses()
    {
        var s = new List<double> { 3, 5, 9, 10 };
        Assert.Equal([2.0, 4, 1], Forecasting.Difference(s));
        Assert.Equal([5.0, 9, 10], Forecasting.Undifference(Forecasting.Difference(s), 3));
    }

    [Fact]
    public void The_isolation_forest_scores_an_extreme_quantity_higher_than_a_normal_one_and_is_repeatable()
    {
        var normal = Enumerable.Range(0, 40).Select(i => new[] { 8.0 + i % 5 }).ToList();
        normal.Add([400]);
        var a = Forecasting.TrainIsolationForest(normal); var b = Forecasting.TrainIsolationForest(normal);
        Assert.True(a.Score([400]) > 0.62);
        Assert.True(a.Score([10]) < a.Score([400]));
        Assert.Equal(a.Score([400]), b.Score([400]));   // same data, same score
    }

    [Fact]
    public void Model_selection_prefers_the_method_that_did_best_on_unseen_weeks()
    {
        var climbing = Enumerable.Range(0, 14).Select(i => 10.0 + 3 * i).ToList();
        var ranked = DemandModel.Evaluate(climbing);
        Assert.Equal(4, ranked.Count);
        Assert.NotEqual("Last week", ranked[0].Method);                 // a steady climb is not "same as last week"
        Assert.Equal("High", ranked[0].Confidence);
        Assert.Empty(DemandModel.Evaluate([1, 2, 3]));                  // too short to score honestly
    }

    [Fact]
    public void Market_basket_finds_the_pair_that_travels_together_and_ignores_coincidence()
    {
        // Food(1) and Bowl(2) are always bought together; Litter(3) is bought alone.
        var baskets = new List<int[]>();
        for (var i = 0; i < 10; i++) baskets.Add([1, 2]);
        for (var i = 0; i < 10; i++) baskets.Add([3]);
        var rules = MarketBasket.RulesFrom(MarketBasket.Apriori(baskets, 0.1), baskets.Count);
        var r = rules.First(x => x.Consequent == 2);
        Assert.Equal([1], r.Antecedent);
        Assert.Equal(1.0, r.Confidence);
        Assert.Equal(2.0, r.Lift, 3);                                   // 1.0 / (10/20)
        Assert.DoesNotContain(rules, x => x.Consequent == 3);
    }

    [Fact]
    public void One_basket_is_an_anecdote_not_a_pattern()
    {
        Assert.Empty(MarketBasket.Apriori([[1, 2]], 0.02).Where(kv => kv.Key.Contains(',')));
    }

    [Fact]
    public void Kmeans_splits_customers_by_spend_and_is_deterministic()
    {
        var rows = new List<double[]> { new[] { 1.0, 100, 100 }, new[] { 1.0, 120, 120 }, new[] { 2.0, 110, 220 }, new[] { 12.0, 500, 6000 }, new[] { 14.0, 480, 6700 }, new[] { 30.0, 900, 27000 } };
        var a = Learning.KMeans(Learning.Normalise(rows), 3); var b = Learning.KMeans(Learning.Normalise(rows), 3);
        Assert.Equal(a.Select(c => string.Join(",", c.Members)), b.Select(c => string.Join(",", c.Members)));
        var top = a.OrderByDescending(c => c.Centre[2]).First();
        Assert.Contains(5, top.Members);
    }
}
