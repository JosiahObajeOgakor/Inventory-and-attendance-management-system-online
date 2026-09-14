''' Demand forecasting that picks its own method and says how well it has been
''' doing — the place the tree ensembles actually earn their keep.
'''
''' Every method here is measured the same way before it's used: train on the
''' earlier weeks, predict a week it has never seen, step forward, repeat. The
''' error that comes out of that walk-forward test is what the reorder screen
''' shows as confidence. A forecast without a measured error is just an opinion
''' with a decimal point on it.
'''
''' A naive baseline ("next week looks like this week") is measured alongside
''' the real models on purpose. A model that can't beat it isn't earning its
''' place, and saying so is more useful than quietly shipping it.
Public Module DemandModel

    ''' How many past weeks feed each prediction. Four gives the trees something
    ''' to split on without demanding a year of history before anything works.
    Private Const Lags As Integer = 4
    ''' Weeks held back for scoring. Small, because the history is small.
    Private Const HoldoutWeeks As Integer = 3

    Public Class Evaluation
        Public Property Method As String
        ''' Average miss, in units, against weeks the model never saw.
        Public Property MeanAbsoluteError As Double
        ''' That error as a share of average weekly demand — comparable between
        ''' a product selling 5 a week and one selling 500.
        Public Property RelativeError As Double
        Public Property Predicted As Double

        ''' Plain-English confidence, from how far off it has actually been.
        Public ReadOnly Property Confidence As String
            Get
                If Double.IsNaN(RelativeError) Then Return "Unknown"
                If RelativeError <= 0.15 Then Return "High"
                If RelativeError <= 0.35 Then Return "Medium"
                Return "Low"
            End Get
        End Property

        Public ReadOnly Property Summary As String
            Get
                If Double.IsNaN(RelativeError) Then Return $"{Method}: not enough history to score"
                Return $"{Method}: typically out by {MeanAbsoluteError:0.#} units ({RelativeError:P0}) — {Confidence.ToLowerInvariant()} confidence"
            End Get
        End Property
    End Class

    ''' Weekly units sold for one product, oldest first.
    Public Function WeeklyDemand(productId As Integer, Optional weeks As Integer = 26) As List(Of Double)
        Dim rows = DataAccess.GetTable(
            "SELECT DATEDIFF(WEEK, DATEADD(WEEK, @w, CAST(GETDATE() AS DATE)), i.InvoiceDate) AS Wk, SUM(ii.Quantity) AS Qty " &
            "FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "WHERE ii.ProductID = @p AND i.InvoiceDate >= DATEADD(WEEK, @w, CAST(GETDATE() AS DATE)) " &
            "GROUP BY DATEDIFF(WEEK, DATEADD(WEEK, @w, CAST(GETDATE() AS DATE)), i.InvoiceDate) ORDER BY Wk",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", -Math.Abs(weeks)}})

        ' Weeks with no sales are zeros, not gaps — leaving them out would make
        ' a product that sells every other week look like steady demand.
        Dim byWeek As New Dictionary(Of Integer, Double)
        For Each r As System.Data.DataRow In rows.Rows
            byWeek(Convert.ToInt32(r("Wk"))) = Convert.ToDouble(r("Qty"))
        Next
        If byWeek.Count = 0 Then Return New List(Of Double)

        Dim series As New List(Of Double)
        For w = 0 To byWeek.Keys.Max()
            series.Add(If(byWeek.ContainsKey(w), byWeek(w), 0.0))
        Next
        Return series
    End Function

    ''' Turns a series into supervised rows: the last few weeks predict the next.
    ''' Features are the lagged weeks plus their mean and the step between the
    ''' two most recent — level and direction, which is what the trees split on.
    Public Sub BuildTrainingRows(series As List(Of Double), ByRef rows As List(Of Double()), ByRef targets As List(Of Double))
        rows = New List(Of Double())
        targets = New List(Of Double)
        If series Is Nothing OrElse series.Count <= Lags Then Return

        For i = Lags To series.Count - 1
            Dim window = series.Skip(i - Lags).Take(Lags).ToList()
            Dim features As New List(Of Double)(window)
            features.Add(window.Average())
            features.Add(window.Last() - window.First())
            rows.Add(features.ToArray())
            targets.Add(series(i))
        Next
    End Sub

    Private Function FeaturesForNext(series As List(Of Double)) As Double()
        Dim window = series.Skip(Math.Max(0, series.Count - Lags)).ToList()
        While window.Count < Lags
            window.Insert(0, If(window.Count > 0, window.First(), 0.0))
        End While
        Dim features As New List(Of Double)(window)
        features.Add(window.Average())
        features.Add(window.Last() - window.First())
        Return features.ToArray()
    End Function

    ''' Scores every method on weeks it was never trained on, then predicts next
    ''' week with each. Ordered best (smallest miss) first.
    Public Function Evaluate(series As List(Of Double)) As List(Of Evaluation)
        Dim results As New List(Of Evaluation)
        If series Is Nothing OrElse series.Count < Lags + HoldoutWeeks + 1 Then Return results

        Dim splitAt = series.Count - HoldoutWeeks
        Dim train = series.Take(splitAt).ToList()
        Dim averageDemand = series.Average()

        results.Add(Score("Last week", series, splitAt,
                          Function(history) history.Last()))
        results.Add(Score("Holt-Winters", series, splitAt,
                          Function(history) Forecasting.HoltWinters(history, 1).Values(0)))
        results.Add(Score("ARIMA-style differencing", series, splitAt,
                          Function(history) PredictDifferenced(history)))
        results.Add(Score("Random forest", series, splitAt,
                          Function(history) PredictWithTrees(history, "forest")))
        results.Add(Score("Gradient boosting", series, splitAt,
                          Function(history) PredictWithTrees(history, "boost")))
        results.Add(Score("XGBoost-style boosting", series, splitAt,
                          Function(history) PredictWithTrees(history, "newton")))

        For Each r In results
            r.RelativeError = If(averageDemand > 0, r.MeanAbsoluteError / averageDemand, Double.NaN)
        Next
        Return results.OrderBy(Function(r) r.MeanAbsoluteError).ThenBy(Function(r) r.Method).ToList()
    End Function

    ''' Walk forward: predict each held-out week from only the weeks before it,
    ''' then take the average miss. Training on data that includes the week being
    ''' predicted would flatter every model here into looking perfect.
    Private Function Score(method As String, series As List(Of Double), splitAt As Integer,
                           predict As Func(Of List(Of Double), Double)) As Evaluation
        Dim errors As New List(Of Double)
        For cut = splitAt To series.Count - 1
            Dim history = series.Take(cut).ToList()
            errors.Add(Math.Abs(predict(history) - series(cut)))
        Next

        Return New Evaluation With {
            .Method = method,
            .MeanAbsoluteError = If(errors.Count > 0, errors.Average(), Double.NaN),
            .Predicted = Math.Max(0, predict(series))}
    End Function

    ''' The "I" of ARIMA put to work: difference the series to strip the trend
    ''' out, forecast the week-on-week change, then add it back onto the last
    ''' actual value. On a series that climbs steadily this models the climb
    ''' directly instead of forever lagging behind it.
    Private Function PredictDifferenced(history As List(Of Double)) As Double
        If history.Count < 3 Then Return history.Last()

        Dim changes = Forecasting.Difference(history)
        If changes.Count = 0 Then Return history.Last()

        ' Forecast the next change from the changes so far, then undo the
        ' differencing by applying it to where the series actually ended.
        Dim nextChange = Forecasting.HoltWinters(changes, 1).Values(0)
        ' HoltWinters floors its output at zero, which is right for demand but
        ' wrong for a change that can legitimately be negative — recover the
        ' direction from the recent changes when it has been clamped.
        If nextChange = 0 AndAlso changes.Count > 0 Then nextChange = changes.Skip(Math.Max(0, changes.Count - 3)).Average()

        Return Math.Max(0, Forecasting.Undifference(New List(Of Double) From {nextChange}, history.Last()).First())
    End Function

    Private Function PredictWithTrees(history As List(Of Double), kind As String) As Double
        Dim rows As List(Of Double()) = Nothing, targets As List(Of Double) = Nothing
        BuildTrainingRows(history, rows, targets)
        ' Below a handful of examples a tree just memorises; fall back to the
        ' recent average rather than pretending to have learned something.
        If rows.Count < 4 Then Return history.Skip(Math.Max(0, history.Count - Lags)).Average()

        Dim features = FeaturesForNext(history)
        Select Case kind
            Case "forest" : Return Trees.TrainRandomForest(rows, targets, treeCount:=30, maxDepth:=4).Predict(features)
            Case "boost" : Return Trees.TrainGradientBoosting(rows, targets, rounds:=50, learningRate:=0.15).Predict(features)
            Case Else : Return Trees.TrainNewtonBoosting(rows, targets, rounds:=50, learningRate:=0.15).Predict(features)
        End Select
    End Function

    ''' The method that has actually been most accurate for this product, with
    ''' its prediction and its measured error. Nothing when the history is too
    ''' short to score anything honestly.
    Public Function Best(productId As Integer) As Evaluation
        Dim ranked = Evaluate(WeeklyDemand(productId))
        Return If(ranked.Count > 0, ranked.First(), Nothing)
    End Function

End Module
