Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Market basket mining. The baskets here are written out by hand so the
''' expected support/confidence/lift can be worked out on paper — if a test says
''' 0.75 confidence, that's 3 of 4 baskets, not a number copied from output.
<TestClass>
Public Class MarketBasketTests

    ''' Four baskets. 1 and 2 travel together (3 of the 4 baskets have both),
    ''' 3 shows up on its own, 4 appears once.
    Private Shared Function SampleBaskets() As List(Of List(Of Integer))
        Return New List(Of List(Of Integer)) From {
            New List(Of Integer) From {1, 2, 3},
            New List(Of Integer) From {1, 2},
            New List(Of Integer) From {1, 2, 4},
            New List(Of Integer) From {1, 3}
        }
    End Function

    Private Shared Function Names() As Dictionary(Of Integer, String)
        Return New Dictionary(Of Integer, String) From {
            {1, "Adult Dog Food 20kg"}, {2, "Puppy Chow 10kg"}, {3, "Dog Treats"}, {4, "Feeding Bowl"}}
    End Function

    ' ===== frequent itemsets =====

    <TestMethod>
    Public Sub Apriori_counts_how_many_baskets_hold_each_itemset()
        Dim frequent = MarketBasket.Apriori(SampleBaskets(), minSupport:=0.5)

        Assert.AreEqual(4, frequent("1"), "product 1 is in every basket")
        Assert.AreEqual(3, frequent("2"), "product 2 is in three")
        Assert.AreEqual(3, frequent("1,2"), "the pair is in three")
    End Sub

    <TestMethod>
    Public Sub An_itemset_below_the_support_threshold_is_left_out()
        Dim frequent = MarketBasket.Apriori(SampleBaskets(), minSupport:=0.5)

        Assert.IsFalse(frequent.ContainsKey("4"), "one basket in four is under half — it isn't frequent")
        Assert.IsFalse(frequent.ContainsKey("1,4"), "and neither is anything built on it")
    End Sub

    <TestMethod>
    Public Sub A_pattern_seen_only_once_is_never_called_frequent()
        ' Even at a threshold low enough to admit it, one basket is an anecdote.
        Dim frequent = MarketBasket.Apriori(SampleBaskets(), minSupport:=0.01)
        Assert.IsFalse(frequent.ContainsKey("4"), "a single basket must not become a rule")
    End Sub

    <TestMethod>
    Public Sub FpGrowth_and_Apriori_always_agree()
        ' Two different algorithms over the same baskets. Any disagreement is a
        ' counting bug in one of them, and this is the cheapest way to catch it.
        For Each support In {0.2, 0.5, 0.75}
            Dim byApriori = MarketBasket.Apriori(SampleBaskets(), support)
            Dim byFpGrowth = MarketBasket.FpGrowth(SampleBaskets(), support)

            CollectionAssert.AreEquivalent(byApriori.Keys.ToList(), byFpGrowth.Keys.ToList(),
                                           $"different itemsets found at support {support}")
            For Each kv In byApriori
                Assert.AreEqual(kv.Value, byFpGrowth(kv.Key), $"different count for {kv.Key} at support {support}")
            Next
        Next
    End Sub

    <TestMethod>
    Public Sub Mining_the_same_baskets_twice_gives_the_identical_answer()
        Dim first = MarketBasket.FpGrowth(SampleBaskets(), 0.3)
        Dim second = MarketBasket.FpGrowth(SampleBaskets(), 0.3)

        CollectionAssert.AreEqual(first.Keys.OrderBy(Function(k) k).ToList(),
                                  second.Keys.OrderBy(Function(k) k).ToList(),
                                  "no sampling, no seeds — the same data must mine to the same result")
    End Sub

    ' ===== rules =====

    <TestMethod>
    Public Sub A_rule_reports_support_confidence_and_lift_from_the_baskets()
        Dim frequent = MarketBasket.Apriori(SampleBaskets(), 0.5)
        Dim rules = MarketBasket.RulesFrom(frequent, 4, Names(), minConfidence:=0.5)

        Dim rule = rules.First(Function(r) r.Antecedent.Length = 1 AndAlso
                                           r.Antecedent(0) = "Puppy Chow 10kg" AndAlso
                                           r.Consequent = "Adult Dog Food 20kg")
        Assert.AreEqual(3, rule.Baskets)
        Assert.AreEqual(0.75, rule.Support, 0.0001, "3 of 4 baskets hold both")
        Assert.AreEqual(1.0, rule.Confidence, 0.0001, "every basket with Puppy Chow also had Adult Dog Food")
        Assert.AreEqual(1.0, rule.Lift, 0.0001, "but Adult Dog Food is in every basket anyway — no real lift")
    End Sub

    <TestMethod>
    Public Sub Lift_separates_a_real_association_from_a_popular_product()
        ' Product 1 is in every basket, so predicting it is worthless however
        ' confident the rule looks. Lift is what says so.
        Dim rules = MarketBasket.RulesFrom(MarketBasket.Apriori(SampleBaskets(), 0.5), 4, Names(), 0.5)

        Dim towardsEveryone = rules.First(Function(r) r.Consequent = "Adult Dog Food 20kg")
        Assert.AreEqual(1.0, towardsEveryone.Confidence, 0.0001)
        Assert.AreEqual(1.0, towardsEveryone.Lift, 0.0001,
                        "confidence alone would rank this top; lift correctly calls it nothing")
    End Sub

    <TestMethod>
    Public Sub Weak_rules_are_dropped_before_anyone_sees_them()
        Dim rules = MarketBasket.RulesFrom(MarketBasket.Apriori(SampleBaskets(), 0.5), 4, Names(), minConfidence:=0.9)
        Assert.IsTrue(rules.All(Function(r) r.Confidence >= 0.9),
                      "a rule under the confidence floor is noise on the screen")
    End Sub

    <TestMethod>
    Public Sub Rules_come_back_strongest_first()
        Dim rules = MarketBasket.RulesFrom(MarketBasket.Apriori(SampleBaskets(), 0.25), 4, Names(), 0.1)
        For i = 1 To rules.Count - 1
            Assert.IsTrue(rules(i - 1).Lift >= rules(i).Lift,
                          "the most useful rule has to be the first one read")
        Next
    End Sub

    <TestMethod>
    Public Sub Every_rule_explains_itself_in_words()
        Dim rules = MarketBasket.RulesFrom(MarketBasket.Apriori(SampleBaskets(), 0.5), 4, Names(), 0.5)
        For Each r In rules
            Assert.IsTrue(r.Explanation.Contains("→"), "a rule has to read as a sentence: " & r.Explanation)
            Assert.IsTrue(r.Explanation.Contains(r.Consequent))
            Assert.IsTrue(r.Baskets >= 2, "and stand on more than one basket")
        Next
    End Sub

    <TestMethod>
    Public Sub No_sales_means_no_rules_rather_than_a_crash()
        Dim empty = New List(Of List(Of Integer))
        Assert.AreEqual(0, MarketBasket.Apriori(empty, 0.5).Count)
        Assert.AreEqual(0, MarketBasket.FpGrowth(empty, 0.5).Count)
        Assert.AreEqual(0, MarketBasket.RulesFrom(New Dictionary(Of String, Integer), 0, Names()).Count)
    End Sub

    <TestMethod>
    Public Sub Baskets_of_one_product_produce_no_pairings()
        Dim singles As New List(Of List(Of Integer)) From {
            New List(Of Integer) From {1},
            New List(Of Integer) From {1},
            New List(Of Integer) From {2}}

        Dim rules = MarketBasket.RulesFrom(MarketBasket.Apriori(singles, 0.2), 3, Names(), 0.1)
        Assert.AreEqual(0, rules.Count, "nothing was ever bought alongside anything else")
    End Sub

End Class
