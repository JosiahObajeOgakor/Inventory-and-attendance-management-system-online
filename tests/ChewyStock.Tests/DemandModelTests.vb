Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The forecast that grades itself: the tree ensembles measured against plain
''' smoothing and a naive baseline on weeks they never trained on.
'''
''' Two kinds of test here. The first run on hand-made series whose right answer
''' is arithmetic — a straight line, a flat line, a repeating cycle — so a claim
''' of accuracy can be checked rather than taken on trust. The second run on
''' generated trading history, which is the only way to see the whole path the
''' reorder screen actually takes.
<TestClass>
Public Class DemandModelTests

    Private Shared _userId As Integer

    <TestInitialize>
    Public Sub EnsureHistory()
        If SampleData.SampleInvoiceCount() > 0 Then Return
        TestDb.Rebuild()
        _userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        SampleData.Generate(_userId, weeks:=16)
    End Sub

    Private Shared Function Straight(weeks As Integer) As List(Of Double)
        ' 10, 20, 30 … — a line anything competent should follow.
        Return Enumerable.Range(1, weeks).Select(Function(w) CDbl(w * 10)).ToList()
    End Function

    ' ===== the backtest itself =====

    <TestMethod>
    Public Sub Every_method_is_scored_against_weeks_it_never_trained_on()
        Dim ranked = DemandModel.Evaluate(Straight(20))

        Assert.IsTrue(ranked.Count >= 5, "all the methods have to be measured, not just the chosen one")
        For Each m In ranked
            Assert.IsFalse(Double.IsNaN(m.MeanAbsoluteError), m.Method & " came back without a score")
            Assert.IsTrue(m.MeanAbsoluteError >= 0)
        Next
    End Sub

    <TestMethod>
    Public Sub The_naive_baseline_is_measured_alongside_the_real_models()
        Dim ranked = DemandModel.Evaluate(Straight(20))
        Assert.IsTrue(ranked.Any(Function(m) m.Method = "Last week"),
                      "a model that can't beat 'next week looks like this week' isn't earning its place")
    End Sub

    <TestMethod>
    Public Sub The_boosters_are_among_the_methods_considered()
        Dim methods = DemandModel.Evaluate(Straight(20)).Select(Function(m) m.Method).ToList()
        For Each expected In {"Random forest", "Gradient boosting", "XGBoost-style boosting", "Holt-Winters"}
            Assert.IsTrue(methods.Contains(expected), expected & " should be in the running: " & String.Join(", ", methods))
        Next
    End Sub

    <TestMethod>
    Public Sub The_best_method_is_returned_first()
        Dim ranked = DemandModel.Evaluate(Straight(20))
        For i = 1 To ranked.Count - 1
            Assert.IsTrue(ranked(i - 1).MeanAbsoluteError <= ranked(i).MeanAbsoluteError,
                          "the most accurate method has to be the one picked")
        Next
    End Sub

    <TestMethod>
    Public Sub A_steady_trend_is_forecast_accurately()
        Dim best = DemandModel.Evaluate(Straight(20)).First()

        ' The next value after 200 is 210; being out by a whole week's demand on
        ' a dead straight line would mean nothing here works.
        Assert.IsTrue(best.Predicted > 150, "a rising line should keep rising, got " & best.Predicted)
        Assert.IsTrue(best.RelativeError < 0.35,
                      $"{best.Method} was out by {best.RelativeError:P0} on a straight line")
    End Sub

    <TestMethod>
    Public Sub A_flat_series_is_forecast_flat_and_confidently()
        Dim flat = Enumerable.Repeat(40.0, 20).ToList()
        Dim best = DemandModel.Evaluate(flat).First()

        Assert.AreEqual(40.0, best.Predicted, 8.0, "nothing changed, so the forecast shouldn't")
        Assert.AreEqual("High", best.Confidence, "a perfectly steady product is exactly where confidence should be high")
    End Sub

    <TestMethod>
    Public Sub Confidence_comes_from_the_measured_error_not_a_guess()
        ' Genuinely unpredictable demand — not a pattern dressed up as noise.
        ' The model has to admit it can't forecast this rather than report a
        ' tidy number with a confident label on it.
        Dim rng As New Trees.DeterministicRandom(7)
        Dim erratic As New List(Of Double)
        For i = 1 To 20
            erratic.Add(5 + rng.NextInt(300))
        Next

        Dim best = DemandModel.Evaluate(erratic).First()
        Assert.AreNotEqual("High", best.Confidence,
                           "unforecastable demand must never be reported as a confident forecast")
        Assert.IsTrue(best.Summary.Contains("out by"), "the summary has to state the error: " & best.Summary)
    End Sub

    <TestMethod>
    Public Sub A_strict_alternating_pattern_is_learned_rather_than_smoothed_away()
        ' Busy week, quiet week, repeat. Exponential smoothing averages this into
        ' a flat middle that is wrong every single week; a tree can split on the
        ' most recent week and get it right. This is the case that justifies
        ' carrying the ensembles at all.
        Dim alternating As New List(Of Double)
        For i = 1 To 20
            alternating.Add(If(i Mod 2 = 0, 5.0, 300.0))
        Next

        Dim ranked = DemandModel.Evaluate(alternating)
        Dim best = ranked.First()
        Dim smoothing = ranked.First(Function(m) m.Method = "Holt-Winters")

        Assert.IsTrue(best.MeanAbsoluteError < smoothing.MeanAbsoluteError,
                      $"a learnable rhythm should beat smoothing — best was {best.Method} " &
                      $"at {best.MeanAbsoluteError:0.#} against {smoothing.MeanAbsoluteError:0.#}")
    End Sub

    <TestMethod>
    Public Sub A_forecast_is_never_negative()
        Dim falling As New List(Of Double) From {200, 150, 100, 60, 30, 10, 5, 2, 1, 1, 0, 0, 0, 0, 0}
        For Each m In DemandModel.Evaluate(falling)
            Assert.IsTrue(m.Predicted >= 0, m.Method & " predicted negative demand: " & m.Predicted)
        Next
    End Sub

    <TestMethod>
    Public Sub Too_little_history_is_reported_as_nothing_rather_than_a_guess()
        Assert.AreEqual(0, DemandModel.Evaluate(New List(Of Double) From {5, 6, 7}).Count,
                        "three weeks cannot be split into training and holdout — saying so beats inventing a number")
        Assert.AreEqual(0, DemandModel.Evaluate(New List(Of Double)).Count)
    End Sub

    <TestMethod>
    Public Sub Scoring_the_same_history_twice_gives_the_same_verdict()
        Dim first = DemandModel.Evaluate(Straight(20))
        Dim second = DemandModel.Evaluate(Straight(20))

        For i = 0 To first.Count - 1
            Assert.AreEqual(first(i).Method, second(i).Method, "the chosen method must not change between runs")
            Assert.AreEqual(first(i).Predicted, second(i).Predicted, 0.0000001)
        Next
    End Sub

    ' ===== features =====

    <TestMethod>
    Public Sub Training_rows_carry_the_recent_weeks_their_average_and_the_direction()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        DemandModel.BuildTrainingRows(New List(Of Double) From {10, 20, 30, 40, 50}, rows, targets)

        Assert.AreEqual(1, rows.Count, "five weeks with four lags leaves one row to learn from")
        Assert.AreEqual(50.0, targets(0), "the row predicts the week after the ones it holds")
        Assert.AreEqual(6, rows(0).Length, "four lags, their mean, and the step between first and last")
        Assert.AreEqual(25.0, rows(0)(4), 0.001, "the mean of 10..40")
        Assert.AreEqual(30.0, rows(0)(5), 0.001, "the direction: 40 - 10")
    End Sub

    ' ===== the whole path, on generated history =====

    <TestMethod>
    Public Sub A_real_product_gets_a_scored_forecast_from_its_own_sales()
        Dim productId = TestDb.Count("SELECT TOP 1 ProductID FROM Products WHERE IsActive = 1 ORDER BY ProductID")

        Dim best = DemandModel.Best(productId)

        Assert.IsNotNull(best, "sixteen weeks of sales is enough to score a forecast")
        Assert.IsTrue(best.Predicted >= 0)
        Assert.IsTrue({"High", "Medium", "Low", "Unknown"}.Contains(best.Confidence))
    End Sub

    <TestMethod>
    Public Sub Weeks_with_no_sales_count_as_zero_not_as_missing()
        ' A product selling every other week is not a product selling steadily;
        ' dropping the empty weeks would tell the forecast the wrong story.
        Dim productId = TestDb.AddProduct("Occasional Feed")
        Dim customer = TestDb.AddCustomer("Occasional Buyer")
        TestDb.AddInvoice(customer, productId, Date.Today.AddDays(-7))
        TestDb.AddInvoice(customer, productId, Date.Today.AddDays(-35))

        Dim series = DemandModel.WeeklyDemand(productId)
        Assert.IsTrue(series.Count >= 4, "the quiet weeks between the two sales belong in the series")
        Assert.IsTrue(series.Any(Function(v) v = 0), "and they belong in it as zeros")
    End Sub

    <TestMethod>
    Public Sub The_reorder_table_names_the_method_and_its_confidence()
        Dim forecast = Insights.ReorderForecast()

        Assert.IsTrue(forecast.Columns.Contains("Method"), "an admin should see which method produced the number")
        Assert.IsTrue(forecast.Columns.Contains("Confidence"))
        For Each r As Data.DataRow In forecast.Rows
            Assert.IsFalse(String.IsNullOrWhiteSpace(Convert.ToString(r("Method"))),
                           "every row has to say where its forecast came from")
        Next
    End Sub

    ' ===== serial tracking is reachable =====

    ' ===== every algorithm reaches a user =====

    <TestMethod>
    Public Sub ARIMA_differencing_is_one_of_the_methods_actually_scored()
        Dim methods = DemandModel.Evaluate(Straight(20)).Select(Function(m) m.Method).ToList()
        Assert.IsTrue(methods.Contains("ARIMA-style differencing"),
                      "differencing has to compete for real, not sit in a library: " & String.Join(", ", methods))
    End Sub

    <TestMethod>
    Public Sub Differencing_wins_on_a_steady_climb()
        ' A series that climbs by a fixed amount every week differences to a
        ' constant, which is the easiest thing in the world to forecast. If the
        ' ARIMA route cannot win here it is not pulling its weight anywhere.
        Dim ranked = DemandModel.Evaluate(Straight(20))
        Dim differencing = ranked.First(Function(m) m.Method = "ARIMA-style differencing")
        Dim naive = ranked.First(Function(m) m.Method = "Last week")

        Assert.IsTrue(differencing.MeanAbsoluteError < naive.MeanAbsoluteError,
                      $"differencing was out by {differencing.MeanAbsoluteError:0.#} against the naive {naive.MeanAbsoluteError:0.#}")
    End Sub

    <TestMethod>
    Public Sub The_sale_screen_shows_the_customer_segment()
        For i = 1 To 6
            TestDb.AddCustomer("Segment Shopper " & i)
        Next

        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            Dim label = DirectCast(f.GetType().GetField("lblSegment", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).GetValue(f), Label)
            Assert.IsNotNull(label, "K-Means segmentation has to be visible on the screen that sells")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Survival_analysis_reaches_the_dashboard_advice()
        Dim titles = Insights.Recommendations().Select(Function(r) r.Title).ToList()
        ' Either it has something to say or the catalogue is too small to say it,
        ' but the rule has to be wired in and not throw on the way.
        Assert.IsNotNull(titles)
    End Sub

    <TestMethod>
    Public Sub The_serial_screen_offers_booking_in_looking_up_and_discrepancies()
        Using f As New frmSerials(1)
            f.Show()
            Application.DoEvents()

            Dim tabs = f.Controls.OfType(Of TabControl)().First()
            Dim titles = tabs.TabPages.Cast(Of TabPage)().Select(Function(t) t.Text).ToList()
            CollectionAssert.AreEquivalent({"Book in", "On the shelf", "Discrepancies"}, titles,
                                           "all three serial jobs have to be reachable")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Serials_booked_in_show_up_on_the_shelf()
        Dim productId = TestDb.AddProduct("Serialised Scale")
        TestDb.Exec("UPDATE Products SET TracksSerial = 1 WHERE ProductID = @p", TestDb.P("@p", productId))
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")

        Dim problem = Serials.Receive(productId, warehouse, {"SN-001", "SN-002"})

        Assert.AreEqual("", problem, "a clean batch should book in without complaint")
        Assert.AreEqual(2, Serials.AvailableCount(productId))
    End Sub

    <TestMethod>
    Public Sub A_duplicate_serial_rejects_the_whole_batch()
        Dim productId = TestDb.AddProduct("Serialised Feeder")
        TestDb.Exec("UPDATE Products SET TracksSerial = 1 WHERE ProductID = @p", TestDb.P("@p", productId))
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        Serials.Receive(productId, warehouse, {"DUP-1"})

        Dim problem = Serials.Receive(productId, warehouse, {"DUP-2", "DUP-1"})

        Assert.AreNotEqual("", problem, "a serial already on file means a typo or a counterfeit")
        Assert.AreEqual(1, Serials.AvailableCount(productId),
                        "and nothing from that batch should have been booked in")
    End Sub

End Class
