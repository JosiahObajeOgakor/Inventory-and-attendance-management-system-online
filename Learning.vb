Imports System.Data

''' The deterministic learners behind the stock predictions: clustering,
''' stockout probability, and how long the shelf has left.
'''
''' "Deterministic" is the rule for all of them — no wall-clock seeds, no
''' sampling, fixed iteration counts, ties broken by index. The same database
''' must always produce the same numbers, because a forecast that moves when
''' nothing changed is one nobody will trust twice.
'''
''' They're written out here rather than pulled from a library because the app
''' ships as a single WinForms executable: no Python, and no native ML binaries
''' to install alongside it.
Public Module Learning

    ' =====================================================================
    ' K-Means — product segmentation
    ' =====================================================================

    Public Class Cluster
        Public Property Index As Integer
        Public Property Centre As Double()
        Public Property Members As New List(Of Integer)   ' row indices
    End Class

    ''' K-Means over rows of equal-length features, Lloyd's algorithm.
    '''
    ''' Determinism comes from the seeding: instead of random starts, the first
    ''' centre is the row furthest from the overall mean and each next centre is
    ''' the row furthest from those already chosen (k-means++ without the
    ''' randomness). Ties go to the lower row index.
    Public Function KMeans(rows As List(Of Double()), k As Integer, Optional iterations As Integer = 50) As List(Of Cluster)
        Dim result As New List(Of Cluster)
        If rows Is Nothing OrElse rows.Count = 0 OrElse k <= 0 Then Return result
        k = Math.Min(k, rows.Count)

        Dim centres = SeedCentres(rows, k)
        Dim assignment(rows.Count - 1) As Integer

        For pass = 1 To iterations
            Dim moved = False
            For i = 0 To rows.Count - 1
                Dim best = NearestCentre(rows(i), centres)
                If assignment(i) <> best Then
                    assignment(i) = best
                    moved = True
                End If
            Next

            For c = 0 To k - 1
                Dim cluster = c
                Dim members = Enumerable.Range(0, rows.Count).Where(Function(i) assignment(i) = cluster).ToList()
                If members.Count = 0 Then Continue For      ' keep an empty centre put
                For d = 0 To centres(c).Length - 1
                    Dim axis = d
                    centres(c)(d) = members.Average(Function(i) rows(i)(axis))
                Next
            Next
            If Not moved AndAlso pass > 1 Then Exit For     ' settled
        Next

        For c = 0 To k - 1
            Dim cluster = c
            result.Add(New Cluster With {
                .Index = c,
                .Centre = centres(c),
                .Members = Enumerable.Range(0, rows.Count).Where(Function(i) assignment(i) = cluster).ToList()})
        Next
        Return result
    End Function

    ''' Furthest-point seeding: spread the starting centres out as far as the
    ''' data allows, which is both a good start and a repeatable one.
    Private Function SeedCentres(rows As List(Of Double()), k As Integer) As List(Of Double())
        Dim width = rows(0).Length
        Dim mean(width - 1) As Double
        For d = 0 To width - 1
            Dim axis = d
            mean(d) = rows.Average(Function(r) r(axis))
        Next

        Dim centres As New List(Of Double())
        Dim firstIndex = FurthestFrom(rows, New List(Of Double()) From {mean})
        centres.Add(DirectCast(rows(firstIndex).Clone(), Double()))
        While centres.Count < k
            Dim nextIndex = FurthestFrom(rows, centres)
            centres.Add(DirectCast(rows(nextIndex).Clone(), Double()))
        End While
        Return centres
    End Function

    Private Function FurthestFrom(rows As List(Of Double()), centres As List(Of Double())) As Integer
        Dim bestIndex = 0
        Dim bestDistance = -1.0
        For i = 0 To rows.Count - 1
            Dim row = rows(i)
            Dim nearest = centres.Min(Function(c) Distance(row, c))
            If nearest > bestDistance Then
                bestDistance = nearest
                bestIndex = i
            End If
        Next
        Return bestIndex
    End Function

    Private Function NearestCentre(row As Double(), centres As List(Of Double())) As Integer
        Dim best = 0
        Dim bestDistance = Distance(row, centres(0))
        For c = 1 To centres.Count - 1
            Dim d = Distance(row, centres(c))
            If d < bestDistance Then
                bestDistance = d
                best = c
            End If
        Next
        Return best
    End Function

    Private Function Distance(a As Double(), b As Double()) As Double
        Dim total = 0.0
        For i = 0 To Math.Min(a.Length, b.Length) - 1
            Dim gap = a(i) - b(i)
            total += gap * gap
        Next
        Return Math.Sqrt(total)
    End Function

    ''' Puts each feature on a 0..1 scale so one big-numbered column (naira of
    ''' stock value) can't drown a small-numbered one (weeks of cover).
    Public Function Normalise(rows As List(Of Double())) As List(Of Double())
        Dim scaled As New List(Of Double())
        If rows.Count = 0 Then Return scaled
        Dim width = rows(0).Length
        Dim lows(width - 1), highs(width - 1) As Double
        For d = 0 To width - 1
            Dim axis = d
            lows(d) = rows.Min(Function(r) r(axis))
            highs(d) = rows.Max(Function(r) r(axis))
        Next
        For Each r In rows
            Dim row(width - 1) As Double
            For d = 0 To width - 1
                Dim span = highs(d) - lows(d)
                row(d) = If(span > 0, (r(d) - lows(d)) / span, 0)
            Next
            scaled.Add(row)
        Next
        Return scaled
    End Function

    ' =====================================================================
    ' Logistic regression — stockout-risk probability
    ' =====================================================================

    Public Class LogisticModel
        Public Property Weights As Double()
        Public Property Bias As Double

        ''' Probability between 0 and 1 that the outcome happens.
        Public Function Probability(features As Double()) As Double
            Dim z = Bias
            For i = 0 To Math.Min(features.Length, Weights.Length) - 1
                z += Weights(i) * features(i)
            Next
            Return 1.0 / (1.0 + Math.Exp(-Math.Max(-60, Math.Min(60, z))))    ' clamped: Exp overflows first
        End Function
    End Class

    ''' Batch gradient descent on the log-loss. Starts from all-zero weights and
    ''' runs a fixed number of passes, so training twice on the same rows gives
    ''' the same model to the last decimal.
    Public Function TrainLogistic(rows As List(Of Double()), labels As List(Of Integer),
                                  Optional iterations As Integer = 400,
                                  Optional learningRate As Double = 0.1) As LogisticModel
        Dim width = If(rows.Count > 0, rows(0).Length, 0)
        Dim model As New LogisticModel With {.Weights = New Double(Math.Max(0, width - 1)) {}, .Bias = 0}
        If rows.Count = 0 OrElse rows.Count <> labels.Count Then Return model

        For pass = 1 To iterations
            Dim gradient(width - 1) As Double
            Dim biasGradient = 0.0
            For i = 0 To rows.Count - 1
                Dim error_ = model.Probability(rows(i)) - labels(i)
                biasGradient += error_
                For d = 0 To width - 1
                    gradient(d) += error_ * rows(i)(d)
                Next
            Next
            model.Bias -= learningRate * biasGradient / rows.Count
            For d = 0 To width - 1
                model.Weights(d) -= learningRate * gradient(d) / rows.Count
            Next
        Next
        Return model
    End Function

    ' =====================================================================
    ' Survival analysis — days until stock depletion
    ' =====================================================================

    Public Class SurvivalPoint
        Public Property Days As Integer
        ''' Share of products still in stock this many days in.
        Public Property Surviving As Double
        Public Property AtRisk As Integer
        Public Property Depleted As Integer
    End Class

    ''' Kaplan–Meier estimator. `days` is how long each product lasted (or has
    ''' lasted so far); `ranOut` says whether it actually hit zero — False means
    ''' the observation is censored: still in stock, so all we know is it lasted
    ''' at least that long. Throwing censored rows away would make everything
    ''' look like it depletes faster than it does, which is the whole reason to
    ''' use this rather than an average.
    Public Function KaplanMeier(days As List(Of Integer), ranOut As List(Of Boolean)) As List(Of SurvivalPoint)
        Dim curve As New List(Of SurvivalPoint)
        If days Is Nothing OrElse days.Count = 0 OrElse days.Count <> ranOut.Count Then Return curve

        Dim ordered = Enumerable.Range(0, days.Count).
            OrderBy(Function(i) days(i)).ThenByDescending(Function(i) ranOut(i)).ToList()

        Dim surviving = 1.0
        Dim remaining = days.Count
        Dim index = 0
        While index < ordered.Count
            Dim day = days(ordered(index))
            Dim atRisk = remaining
            Dim depleted = 0
            Dim censored = 0
            While index < ordered.Count AndAlso days(ordered(index)) = day
                If ranOut(ordered(index)) Then depleted += 1 Else censored += 1
                index += 1
            End While

            If depleted > 0 AndAlso atRisk > 0 Then surviving *= (1.0 - depleted / CDbl(atRisk))
            remaining -= (depleted + censored)
            curve.Add(New SurvivalPoint With {
                .Days = day, .Surviving = surviving, .AtRisk = atRisk, .Depleted = depleted})
        End While
        Return curve
    End Function

    ''' The point where half the products have run out — the headline number off
    ''' a survival curve. -1 when the curve never drops that far.
    Public Function MedianSurvivalDays(curve As List(Of SurvivalPoint)) As Integer
        For Each point In curve
            If point.Surviving <= 0.5 Then Return point.Days
        Next
        Return -1
    End Function

End Module
