Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The learners in Learning.vb, checked against data whose right answer is known
''' by construction — two obvious clumps, a plainly separable label, a survival
''' curve short enough to work out on paper. Each also gets a determinism check:
''' a model that moves when the data didn't is worse than no model.
<TestClass>
Public Class LearningTests

    ' ===== K-Means =====

    ''' Two clear clumps: fast movers near (10,10), dead stock near (0,0).
    Private Shared Function TwoClumps() As List(Of Double())
        Return New List(Of Double()) From {
            New Double() {0.0, 0.0}, New Double() {0.5, 0.2}, New Double() {0.2, 0.4},
            New Double() {10.0, 10.0}, New Double() {10.5, 9.8}, New Double() {9.7, 10.2}}
    End Function

    <TestMethod>
    Public Sub KMeans_separates_two_obvious_groups()
        Dim clusters = Learning.KMeans(TwoClumps(), k:=2)

        Assert.AreEqual(2, clusters.Count)
        Dim slow = clusters.First(Function(c) c.Members.Contains(0))
        Dim fast = clusters.First(Function(c) c.Members.Contains(3))
        Assert.AreNotSame(slow, fast, "the two clumps must not land in one cluster")
        CollectionAssert.AreEquivalent({0, 1, 2}, slow.Members, "the three near the origin belong together")
        CollectionAssert.AreEquivalent({3, 4, 5}, fast.Members, "and so do the three out at ten")
    End Sub

    <TestMethod>
    Public Sub KMeans_puts_its_centre_in_the_middle_of_its_members()
        Dim clusters = Learning.KMeans(TwoClumps(), k:=2)
        Dim fast = clusters.First(Function(c) c.Members.Contains(3))

        Assert.AreEqual(10.07, fast.Centre(0), 0.1, "the centre is the average of the members, not a data point")
        Assert.AreEqual(10.0, fast.Centre(1), 0.2)
    End Sub

    <TestMethod>
    Public Sub KMeans_gives_the_same_clusters_every_run()
        ' Seeded by furthest-point, not at random — so this holds run to run.
        Dim first = Learning.KMeans(TwoClumps(), 2)
        Dim second = Learning.KMeans(TwoClumps(), 2)

        For c = 0 To first.Count - 1
            CollectionAssert.AreEqual(first(c).Members, second(c).Members,
                                      "segmentation that shifts between runs can't be acted on")
        Next
    End Sub

    <TestMethod>
    Public Sub Asking_for_more_clusters_than_products_is_not_an_error()
        Dim clusters = Learning.KMeans(New List(Of Double()) From {New Double() {1.0}}, k:=5)
        Assert.AreEqual(1, clusters.Count, "one product can only be one segment")
    End Sub

    <TestMethod>
    Public Sub Normalising_stops_the_big_numbered_column_deciding_everything()
        ' Naira values dwarf "weeks of cover" until both are scaled to 0..1.
        Dim raw As New List(Of Double()) From {
            New Double() {1000000.0, 1.0}, New Double() {1000000.0, 9.0}, New Double() {0.0, 5.0}}

        Dim scaled = Learning.Normalise(raw)
        For Each row In scaled
            For Each value In row
                Assert.IsTrue(value >= 0 AndAlso value <= 1, "every feature must end up inside 0..1, got " & value)
            Next
        Next
        Assert.AreEqual(0.0, scaled(0)(1), 0.0001, "the smallest value of a column maps to 0")
        Assert.AreEqual(1.0, scaled(1)(1), 0.0001, "the largest maps to 1")
    End Sub

    ' ===== logistic regression =====

    ''' One feature: weeks of cover. Under 2 weeks ran out, over 4 didn't.
    Private Shared Sub StockoutHistory(ByRef rows As List(Of Double()), ByRef labels As List(Of Integer))
        rows = New List(Of Double()) From {
            New Double() {0.2}, New Double() {0.5}, New Double() {1.0}, New Double() {1.5},
            New Double() {4.0}, New Double() {5.0}, New Double() {6.5}, New Double() {8.0}}
        labels = New List(Of Integer) From {1, 1, 1, 1, 0, 0, 0, 0}
    End Sub

    <TestMethod>
    Public Sub Stockout_risk_is_high_for_thin_cover_and_low_for_deep_cover()
        Dim rows As List(Of Double()) = Nothing, labels As List(Of Integer) = Nothing
        StockoutHistory(rows, labels)

        Dim model = Learning.TrainLogistic(rows, labels, iterations:=3000, learningRate:=0.5)

        Assert.IsTrue(model.Probability({0.3}) > 0.5, "a few days of cover should read as at-risk")
        Assert.IsTrue(model.Probability({7.0}) < 0.5, "two months of cover should not")
        Assert.IsTrue(model.Probability({0.3}) > model.Probability({7.0}),
                      "thinner cover must always carry the higher risk")
    End Sub

    <TestMethod>
    Public Sub Risk_is_always_a_probability()
        Dim rows As List(Of Double()) = Nothing, labels As List(Of Integer) = Nothing
        StockoutHistory(rows, labels)
        Dim model = Learning.TrainLogistic(rows, labels)

        For Each cover In {-5.0, 0.0, 3.0, 500.0}
            Dim p = model.Probability({cover})
            Assert.IsTrue(p >= 0 AndAlso p <= 1, $"cover {cover} gave {p}, which isn't a probability")
        Next
    End Sub

    <TestMethod>
    Public Sub Training_twice_on_the_same_history_gives_the_same_model()
        Dim rows As List(Of Double()) = Nothing, labels As List(Of Integer) = Nothing
        StockoutHistory(rows, labels)

        Dim first = Learning.TrainLogistic(rows, labels)
        Dim second = Learning.TrainLogistic(rows, labels)

        Assert.AreEqual(first.Bias, second.Bias, 0.0000001)
        Assert.AreEqual(first.Weights(0), second.Weights(0), 0.0000001)
    End Sub

    <TestMethod>
    Public Sub With_no_history_the_model_refuses_to_guess()
        Dim model = Learning.TrainLogistic(New List(Of Double()), New List(Of Integer))
        Assert.AreEqual(0.5, model.Probability({3.0}), 0.0001,
                        "nothing learned means an even chance, not a confident answer")
    End Sub

    ' ===== survival analysis =====

    <TestMethod>
    Public Sub Survival_falls_as_products_run_out()
        ' Four products; two ran out (at 10 and 20 days), two are still in stock.
        Dim days As New List(Of Integer) From {10, 20, 30, 40}
        Dim ranOut As New List(Of Boolean) From {True, True, False, False}

        Dim curve = Learning.KaplanMeier(days, ranOut)

        Assert.AreEqual(0.75, curve.First(Function(p) p.Days = 10).Surviving, 0.0001, "one of four gone by day 10")
        Assert.AreEqual(0.5, curve.First(Function(p) p.Days = 20).Surviving, 0.0001, "two of four by day 20")
    End Sub

    <TestMethod>
    Public Sub Stock_still_on_the_shelf_is_counted_as_censored_not_as_depleted()
        ' Both still in stock at 30 days. Nothing has run out, so survival stays
        ' at 1 — treating them as depletions would invent stockouts.
        Dim curve = Learning.KaplanMeier(New List(Of Integer) From {30, 30},
                                         New List(Of Boolean) From {False, False})

        Assert.IsTrue(curve.All(Function(p) p.Surviving = 1.0),
                      "a product that hasn't run out must never count as one that did")
    End Sub

    <TestMethod>
    Public Sub Median_survival_is_where_half_the_products_have_gone()
        Dim days As New List(Of Integer) From {5, 10, 15, 20}
        Dim ranOut As New List(Of Boolean) From {True, True, True, True}

        Assert.AreEqual(10, Learning.MedianSurvivalDays(Learning.KaplanMeier(days, ranOut)),
                        "by day 10 two of the four are gone")
    End Sub

    <TestMethod>
    Public Sub A_median_that_was_never_reached_is_reported_as_unknown()
        Dim curve = Learning.KaplanMeier(New List(Of Integer) From {30, 30},
                                         New List(Of Boolean) From {False, False})
        Assert.AreEqual(-1, Learning.MedianSurvivalDays(curve),
                        "half of them never ran out — inventing a number would be a lie")
    End Sub

    <TestMethod>
    Public Sub No_history_produces_an_empty_curve_rather_than_a_crash()
        Assert.AreEqual(0, Learning.KaplanMeier(New List(Of Integer), New List(Of Boolean)).Count)
        Assert.AreEqual(-1, Learning.MedianSurvivalDays(New List(Of Learning.SurvivalPoint)))
    End Sub

End Class
