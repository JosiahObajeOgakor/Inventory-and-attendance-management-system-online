Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The tree ensembles, the anomaly detector and the demand forecaster. Each is
''' checked on data whose answer is known by construction — a straight line, a
''' clear threshold, one planted outlier — and each gets a determinism check,
''' because a forecast that moves on a rerun is one nobody can act on.
<TestClass>
Public Class PredictionTests

    ''' Demand that rises with one feature: 10 units per unit of feature.
    Private Shared Sub RisingDemand(ByRef rows As List(Of Double()), ByRef targets As List(Of Double))
        rows = New List(Of Double())
        targets = New List(Of Double)
        For x = 1 To 24
            rows.Add(New Double() {x, x Mod 4})
            targets.Add(x * 10.0)
        Next
    End Sub

    ' ===== Random Forest =====

    <TestMethod>
    Public Sub Random_forest_learns_the_shape_of_demand()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        RisingDemand(rows, targets)

        Dim forest = Trees.TrainRandomForest(rows, targets, treeCount:=30, maxDepth:=5)

        ' Trees predict within the range they were trained on, so this checks the
        ' ordering holds rather than demanding an exact extrapolation.
        Assert.IsTrue(forest.Predict({20.0, 0.0}) > forest.Predict({5.0, 1.0}),
                      "heavier demand history must predict heavier demand")
        Assert.AreEqual(200.0, forest.Predict({20.0, 0.0}), 60.0, "and land in the right neighbourhood")
    End Sub

    <TestMethod>
    Public Sub The_same_history_always_grows_the_same_forest()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        RisingDemand(rows, targets)

        Dim first = Trees.TrainRandomForest(rows, targets, 20)
        Dim second = Trees.TrainRandomForest(rows, targets, 20)

        For Each point In {5.0, 12.0, 23.0}
            Assert.AreEqual(first.Predict({point, 1.0}), second.Predict({point, 1.0}), 0.0000001,
                            "bootstrap sampling is seeded — two runs must agree exactly")
        Next
    End Sub

    <TestMethod>
    Public Sub A_forest_with_no_history_predicts_nothing_rather_than_crashing()
        Dim forest = Trees.TrainRandomForest(New List(Of Double()), New List(Of Double))
        Assert.AreEqual(0.0, forest.Predict({1.0}), 0.0001)
    End Sub

    ' ===== gradient boosting =====

    <TestMethod>
    Public Sub Gradient_boosting_fits_the_demand_it_was_shown()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        RisingDemand(rows, targets)

        Dim model = Trees.TrainGradientBoosting(rows, targets, rounds:=60, learningRate:=0.2, maxDepth:=3)

        Assert.AreEqual(100.0, model.Predict({10.0, 2.0}), 25.0)
        Assert.IsTrue(model.Predict({22.0, 2.0}) > model.Predict({4.0, 0.0}))
    End Sub

    <TestMethod>
    Public Sub More_boosting_rounds_fit_the_history_more_closely()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        RisingDemand(rows, targets)

        Dim shallow = Trees.TrainGradientBoosting(rows, targets, rounds:=3, learningRate:=0.1)
        Dim deep = Trees.TrainGradientBoosting(rows, targets, rounds:=80, learningRate:=0.1)

        Assert.IsTrue(TrainingError(deep, rows, targets) < TrainingError(shallow, rows, targets),
                      "each round is fitted to what's still wrong, so error must fall")
    End Sub

    <TestMethod>
    Public Sub Newton_boosting_fits_demand_and_stays_reproducible()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        RisingDemand(rows, targets)

        Dim first = Trees.TrainNewtonBoosting(rows, targets, rounds:=60, learningRate:=0.2)
        Dim second = Trees.TrainNewtonBoosting(rows, targets, rounds:=60, learningRate:=0.2)

        Assert.AreEqual(100.0, first.Predict({10.0, 2.0}), 30.0, "second-order boosting should fit this line too")
        Assert.AreEqual(first.Predict({15.0, 3.0}), second.Predict({15.0, 3.0}), 0.0000001,
                        "same rows, same model — to the last decimal")
    End Sub

    <TestMethod>
    Public Sub The_leaf_penalty_holds_predictions_back()
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        RisingDemand(rows, targets)

        ' A bigger L2 shrinks every leaf, so the model commits less to the
        ' history it was shown — that's the regularisation doing its job.
        Dim light = Trees.TrainNewtonBoosting(rows, targets, rounds:=40, l2:=1.0)
        Dim heavy = Trees.TrainNewtonBoosting(rows, targets, rounds:=40, l2:=200.0)

        Assert.IsTrue(TrainingError(heavy, rows, targets) > TrainingError(light, rows, targets),
                      "heavier regularisation must fit the training rows less tightly")
    End Sub

    Private Shared Function TrainingError(model As Trees.BoostedModel, rows As List(Of Double()), targets As List(Of Double)) As Double
        Dim total = 0.0
        For i = 0 To rows.Count - 1
            Dim gap = model.Predict(rows(i)) - targets(i)
            total += gap * gap
        Next
        Return total
    End Function

    ' ===== Isolation Forest =====

    <TestMethod>
    Public Sub An_odd_stock_movement_scores_higher_than_the_ordinary_ones()
        ' Thirty routine movements of about 10 units, and one of 5000.
        Dim rows As New List(Of Double())
        For i = 1 To 30
            rows.Add(New Double() {10 + (i Mod 3), 1})
        Next
        rows.Add(New Double() {5000, 1})

        Dim model = Forecasting.TrainIsolationForest(rows)

        Dim odd = model.Score({5000, 1})
        Dim ordinary = model.Score({11, 1})
        Assert.IsTrue(odd > ordinary,
                      $"the 5000-unit movement ({odd:0.000}) must stand out from the routine ones ({ordinary:0.000})")
    End Sub

    <TestMethod>
    Public Sub Anomaly_scores_are_repeatable()
        Dim rows As New List(Of Double())
        For i = 1 To 40
            rows.Add(New Double() {i, i Mod 5})
        Next

        Dim first = Forecasting.TrainIsolationForest(rows)
        Dim second = Forecasting.TrainIsolationForest(rows)

        Assert.AreEqual(first.Score({20, 0}), second.Score({20, 0}), 0.0000001,
                        "a flag that comes and goes between runs can't be investigated")
    End Sub

    <TestMethod>
    Public Sub Scores_stay_inside_zero_and_one()
        Dim rows As New List(Of Double())
        For i = 1 To 20
            rows.Add(New Double() {i})
        Next
        Dim model = Forecasting.TrainIsolationForest(rows)

        For Each probe In {-1000.0, 0.0, 10.0, 99999.0}
            Dim s = model.Score({probe})
            Assert.IsTrue(s >= 0 AndAlso s <= 1, $"{probe} scored {s}, which isn't on the 0..1 scale")
        Next
    End Sub

    <TestMethod>
    Public Sub With_no_movements_nothing_is_flagged()
        Assert.AreEqual(0.0, Forecasting.TrainIsolationForest(New List(Of Double())).Score({1.0}), 0.0001)
    End Sub

    ' ===== forecasting =====

    <TestMethod>
    Public Sub A_rising_series_is_forecast_to_keep_rising()
        Dim series As New List(Of Double) From {10, 20, 30, 40, 50, 60}

        Dim forecast = Forecasting.HoltWinters(series, periodsAhead:=3)

        Assert.AreEqual(3, forecast.Values.Length)
        Assert.IsTrue(forecast.Values(0) > 55, "the trend was +10 a period — the next should be near 70, got " & forecast.Values(0))
        Assert.IsTrue(forecast.Values(2) > forecast.Values(0), "and it should keep climbing")
    End Sub

    <TestMethod>
    Public Sub A_flat_series_is_forecast_flat()
        Dim series As New List(Of Double) From {25, 25, 25, 25, 25, 25}
        Dim forecast = Forecasting.HoltWinters(series, 2)

        Assert.AreEqual(25.0, forecast.Values(0), 2.0, "nothing changed, so nothing should be predicted to change")
    End Sub

    <TestMethod>
    Public Sub A_forecast_never_predicts_negative_demand()
        ' A steep decline would run the trend below zero if left unchecked.
        Dim series As New List(Of Double) From {100, 70, 40, 20, 5, 1}
        Dim forecast = Forecasting.HoltWinters(series, 6)

        For Each value In forecast.Values
            Assert.IsTrue(value >= 0, "you cannot sell a negative number of bags, got " & value)
        Next
    End Sub

    <TestMethod>
    Public Sub A_seasonal_pattern_is_carried_into_the_forecast()
        ' A four-period rhythm repeated three times: high, low, low, high.
        Dim series As New List(Of Double) From {100, 20, 20, 100, 100, 20, 20, 100, 100, 20, 20, 100}

        Dim forecast = Forecasting.HoltWinters(series, 4, season:=4)

        Assert.IsTrue(forecast.Values(0) > forecast.Values(1),
                      "the next period repeats a peak and the one after a trough")
        Assert.IsTrue(forecast.Basis.Contains("4-period"), "and the forecast should say it found the pattern")
    End Sub

    <TestMethod>
    Public Sub A_short_history_is_reported_as_an_average_not_dressed_up_as_a_trend()
        Dim forecast = Forecasting.HoltWinters(New List(Of Double) From {30, 50}, 3)

        Assert.AreEqual(40.0, forecast.Values(0), 0.0001)
        Assert.IsTrue(forecast.Basis.IndexOf("average", StringComparison.OrdinalIgnoreCase) >= 0,
                      "two points can't show a trend, and the wording must admit it: " & forecast.Basis)
    End Sub

    <TestMethod>
    Public Sub No_history_forecasts_nothing_and_says_so()
        Dim forecast = Forecasting.HoltWinters(New List(Of Double), 3)
        Assert.IsTrue(forecast.Basis.IndexOf("No sales history", StringComparison.OrdinalIgnoreCase) >= 0)
    End Sub

    <TestMethod>
    Public Sub Forecasting_the_same_history_twice_gives_the_same_numbers()
        Dim series As New List(Of Double) From {12, 18, 11, 22, 17, 25, 19, 30}

        Dim first = Forecasting.HoltWinters(series, 4)
        Dim second = Forecasting.HoltWinters(series, 4)

        CollectionAssert.AreEqual(first.Values, second.Values, "no seeds, no sampling — the forecast is fixed by the data")
    End Sub

    ' ===== ARIMA differencing =====

    <TestMethod>
    Public Sub Differencing_turns_a_trend_into_the_change_per_period()
        Dim series As New List(Of Double) From {10, 20, 30, 40}

        Dim changes = Forecasting.Difference(series)

        CollectionAssert.AreEqual(New List(Of Double) From {10, 10, 10}, changes,
                                  "a straight line differences to a constant — that's what makes it stationary")
    End Sub

    <TestMethod>
    Public Sub Second_order_differencing_flattens_an_accelerating_series()
        Dim series As New List(Of Double) From {1, 4, 9, 16, 25}
        Dim changes = Forecasting.Difference(series, order:=2)

        Assert.IsTrue(changes.All(Function(v) Math.Abs(v - 2.0) < 0.0001),
                      "squares differenced twice are constant")
    End Sub

    <TestMethod>
    Public Sub Undifferencing_puts_a_series_back_the_way_it_was()
        Dim series As New List(Of Double) From {5, 9, 14, 22}

        Dim rebuilt = Forecasting.Undifference(Forecasting.Difference(series), series(0))

        CollectionAssert.AreEqual(New List(Of Double) From {9, 14, 22}, rebuilt,
                                  "a forecast made on differences has to come back to real units")
    End Sub

End Class
