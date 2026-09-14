''' Time-series demand forecasting, and finding stock movements that don't look
''' like the others. Both deterministic: same history in, same numbers out.
Public Module Forecasting

    ' =====================================================================
    ' Holt-Winters / ARIMA-style differencing — demand forecasting
    ' =====================================================================

    Public Class Forecast
        Public Property Values As Double()
        ''' Plain-English note on what the method could and couldn't see, so a
        ''' number off four weeks of data is never mistaken for one off a year.
        Public Property Basis As String
    End Class

    ''' Holt-Winters triple exponential smoothing: a level, a trend, and a
    ''' repeating seasonal pattern, each updated as the series is walked once.
    '''
    ''' This is used rather than a full maximum-likelihood SARIMA because of the
    ''' data this business actually has — a few dozen weekly points per product.
    ''' Fitting SARIMA's autoregressive and moving-average terms by MLE on a
    ''' series that short produces confident-looking coefficients that are mostly
    ''' noise. Holt-Winters captures the same two things that matter here (a
    ''' direction and a weekly rhythm), and every term stays inspectable.
    '''
    ''' The ARIMA side of the request is served by Difference()/Undifference()
    ''' below, which handle the "I" — a series that trends is made stationary
    ''' before it's modelled, and the trend is added back afterwards.
    Public Function HoltWinters(series As List(Of Double), periodsAhead As Integer,
                                Optional season As Integer = 0,
                                Optional alpha As Double = 0.4, Optional beta As Double = 0.1,
                                Optional gamma As Double = 0.3) As Forecast
        If series Is Nothing OrElse series.Count = 0 Then
            Return New Forecast With {.Values = New Double(Math.Max(0, periodsAhead - 1)) {}, .Basis = "No sales history yet."}
        End If

        ' Too short to read a trend from: the best honest answer is the average.
        If series.Count < 3 Then
            Dim flat = Enumerable.Repeat(series.Average(), Math.Max(0, periodsAhead)).ToArray()
            Return New Forecast With {.Values = flat, .Basis = $"Only {series.Count} period(s) of history — this is their average, not a trend."}
        End If

        Dim useSeason = season >= 2 AndAlso series.Count >= season * 2
        Dim level = series(0)
        Dim trend = series(1) - series(0)
        Dim seasonal = SeasonalStart(series, season, useSeason)

        For i = 0 To series.Count - 1
            Dim lastLevel = level
            Dim seasonalIndex = If(useSeason, i Mod season, 0)
            Dim deseasonalised = If(useSeason, series(i) - seasonal(seasonalIndex), series(i))

            level = alpha * deseasonalised + (1 - alpha) * (level + trend)
            trend = beta * (level - lastLevel) + (1 - beta) * trend
            If useSeason Then seasonal(seasonalIndex) = gamma * (series(i) - level) + (1 - gamma) * seasonal(seasonalIndex)
        Next

        Dim out(Math.Max(0, periodsAhead - 1)) As Double
        For step_ = 1 To periodsAhead
            Dim value = level + step_ * trend
            If useSeason Then value += seasonal((series.Count + step_ - 1) Mod season)
            out(step_ - 1) = Math.Max(0, value)      ' demand is never negative
        Next

        Return New Forecast With {
            .Values = out,
            .Basis = If(useSeason,
                        $"{series.Count} periods of history, with a repeating {season}-period pattern.",
                        $"{series.Count} periods of history, level and trend only — not enough repeats to read a season.")}
    End Function

    Private Function SeasonalStart(series As List(Of Double), season As Integer, useSeason As Boolean) As Double()
        If Not useSeason Then Return New Double(0) {}
        Dim average = series.Average()
        Dim seasonal(season - 1) As Double
        For s = 0 To season - 1
            Dim slot = s
            Dim sameSlot = Enumerable.Range(0, series.Count).Where(Function(i) i Mod season = slot).ToList()
            seasonal(s) = If(sameSlot.Count > 0, sameSlot.Average(Function(i) series(i)) - average, 0)
        Next
        Return seasonal
    End Function

    ''' The "I" in ARIMA: differencing of order d, which turns a trending series
    ''' into a stationary one. d=1 is period-on-period change.
    Public Function Difference(series As List(Of Double), Optional order As Integer = 1) As List(Of Double)
        Dim current = New List(Of Double)(If(series, New List(Of Double)))
        For pass = 1 To Math.Max(0, order)
            If current.Count < 2 Then Return New List(Of Double)
            Dim stepped As New List(Of Double)
            For i = 1 To current.Count - 1
                stepped.Add(current(i) - current(i - 1))
            Next
            current = stepped
        Next
        Return current
    End Function

    ''' Rebuilds a level series from differences plus the value it started at —
    ''' the inverse of Difference at order 1, for putting a forecast made on a
    ''' differenced series back onto the original scale.
    Public Function Undifference(differences As List(Of Double), startValue As Double) As List(Of Double)
        Dim rebuilt As New List(Of Double)
        Dim running = startValue
        For Each d In If(differences, New List(Of Double))
            running += d
            rebuilt.Add(running)
        Next
        Return rebuilt
    End Function

    ' =====================================================================
    ' Isolation Forest — unusual stock movement detection
    ' =====================================================================

    Friend Class IsolationNode
        Public Property Feature As Integer = -1
        Public Property SplitValue As Double
        Public Property Size As Integer
        Public Property Left As IsolationNode
        Public Property Right As IsolationNode
    End Class

    Public Class AnomalyModel
        Friend Trees As New List(Of IsolationNode)
        Friend SampleSize As Integer

        ''' 0..1, higher is stranger. Around 0.5 is ordinary; above ~0.65 the
        ''' point was isolated much faster than the rest, which is what "unusual"
        ''' means here. The score is comparable across products, so movements can
        ''' be ranked against each other.
        Public Function Score(point As Double()) As Double
            If Trees.Count = 0 Then Return 0
            Dim averageDepth = Trees.Average(Function(tree) PathLength(tree, point, 0))
            Dim expected = ExpectedPathLength(SampleSize)
            If expected <= 0 Then Return 0
            Return Math.Pow(2, -averageDepth / expected)
        End Function

        Private Function PathLength(node As IsolationNode, point As Double(), depth As Integer) As Double
            If node Is Nothing Then Return depth
            If node.Feature < 0 Then Return depth + ExpectedPathLength(node.Size)
            Return If(point(node.Feature) < node.SplitValue,
                      PathLength(node.Left, point, depth + 1),
                      PathLength(node.Right, point, depth + 1))
        End Function
    End Class

    ''' Average path length of an unsuccessful search in a binary tree of n
    ''' points — the yardstick a point's own depth is measured against.
    Private Function ExpectedPathLength(n As Integer) As Double
        If n <= 1 Then Return 0
        Return 2 * (Math.Log(n - 1) + 0.5772156649) - 2 * (n - 1) / CDbl(n)
    End Function

    ''' Isolation Forest: split the data at random and see how quickly each point
    ''' ends up alone. Outliers separate in a handful of cuts; ordinary points
    ''' take many. It's the right shape of algorithm for stock movements because
    ''' it needs no labelled examples of "fraud" or "error" to work from.
    '''
    ''' The cuts are drawn from a seeded generator, so the same movements always
    ''' score the same — a flag that appears and disappears between runs would be
    ''' impossible to investigate.
    Public Function TrainIsolationForest(rows As List(Of Double()),
                                         Optional treeCount As Integer = 50,
                                         Optional sampleSize As Integer = 64,
                                         Optional seed As Integer = 20260913) As AnomalyModel
        Dim model As New AnomalyModel
        If rows Is Nothing OrElse rows.Count = 0 Then Return model
        model.SampleSize = Math.Min(sampleSize, rows.Count)
        Dim heightLimit = CInt(Math.Ceiling(Math.Log(Math.Max(2, model.SampleSize), 2)))

        For treeIndex = 0 To treeCount - 1
            Dim rng As New Trees.DeterministicRandom(seed + treeIndex)
            Dim sample As New List(Of Double())
            For i = 1 To model.SampleSize
                sample.Add(rows(rng.NextInt(rows.Count)))
            Next
            model.Trees.Add(GrowIsolationTree(sample, rng, 0, heightLimit))
        Next
        Return model
    End Function

    Private Function GrowIsolationTree(rows As List(Of Double()), rng As Trees.DeterministicRandom,
                                       depth As Integer, heightLimit As Integer) As IsolationNode
        If depth >= heightLimit OrElse rows.Count <= 1 Then
            Return New IsolationNode With {.Size = rows.Count}
        End If

        Dim width = rows(0).Length
        Dim feature = rng.NextInt(width)
        Dim low = rows.Min(Function(r) r(feature))
        Dim high = rows.Max(Function(r) r(feature))
        If high <= low Then Return New IsolationNode With {.Size = rows.Count}

        ' A cut somewhere between the min and max, drawn from the seeded stream.
        Dim splitValue = low + (high - low) * (rng.NextInt(1000) + 1) / 1001.0
        Dim left = rows.Where(Function(r) r(feature) < splitValue).ToList()
        Dim right = rows.Where(Function(r) r(feature) >= splitValue).ToList()
        If left.Count = 0 OrElse right.Count = 0 Then Return New IsolationNode With {.Size = rows.Count}

        Return New IsolationNode With {
            .Feature = feature, .SplitValue = splitValue, .Size = rows.Count,
            .Left = GrowIsolationTree(left, rng, depth + 1, heightLimit),
            .Right = GrowIsolationTree(right, rng, depth + 1, heightLimit)}
    End Function

End Module
