Imports System.Data

''' The numbers behind the Dashboard's reorder table and its "What needs your
''' attention" panel. Kept out of the screen so the maths can be tested on its
''' own, and kept deliberately simple so every suggestion can be explained to
''' the person acting on it:
'''
'''   • Reorder forecast — exponential smoothing over each product's last 6
'''     weeks of sales, against stock on hand and a 4-week supplier lead time.
'''     Answers "what do I buy, and how much, before I run out?"
'''   • Recommendations — a handful of plain-English rules over the same data
'''     (running out, money overdue, stock nobody is buying, customers who have
'''     gone quiet, which way sales are trending). Each one states what it saw
'''     and what to do about it, so it is advice rather than a black box.
Public Module Insights

    Public Const Alpha As Decimal = 0.4D
    Public Const LeadTimeWeeks As Integer = 4
    Private Const DeadStockDays As Integer = 56
    Private Const QuietCustomerDays As Integer = 45
    Private Const TrendMovePct As Decimal = 10D

    ''' One piece of advice for the dashboard. Kind is "Urgent", "Watch" or
    ''' "Good" — it only drives the colour and the ordering.
    Public Class Recommendation
        Public Property Kind As String
        Public Property Title As String
        Public Property Detail As String
    End Class

    ''' Per-product: what it's selling, what's left, and what to order.
    Public Function ReorderForecast() As DataTable
        Dim products = DataAccess.GetTable("SELECT ProductID, Name, Unit, ReorderLevel FROM Products WHERE IsActive = 1")
        Dim weekly = DataAccess.GetTable(
            "SELECT ii.ProductID, DATEPART(WEEK, i.InvoiceDate) AS WeekNum, SUM(ii.Quantity) AS Qty " &
            "FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "WHERE i.InvoiceDate >= DATEADD(WEEK, -6, GETDATE()) " &
            "GROUP BY ii.ProductID, DATEPART(WEEK, i.InvoiceDate) ORDER BY ii.ProductID, WeekNum")
        Dim qtyOnHand = DataAccess.GetTable("SELECT ProductID, SUM(QuantityOnHand) AS Qty FROM StockBatches GROUP BY ProductID")

        Dim byProduct As New Dictionary(Of Integer, List(Of Integer))
        For Each r As DataRow In weekly.Rows
            Dim pid = CInt(r("ProductID"))
            If Not byProduct.ContainsKey(pid) Then byProduct(pid) = New List(Of Integer)
            byProduct(pid).Add(CInt(r("Qty")))
        Next

        Dim result As New DataTable()
        result.Columns.Add("Product", GetType(String))
        result.Columns.Add("InStock", GetType(Integer))
        result.Columns.Add("AvgWeekly", GetType(Decimal))
        result.Columns.Add("ForecastNextWeek", GetType(Decimal))
        result.Columns.Add("DaysOfStock", GetType(String))
        result.Columns.Add("SuggestedReorder", GetType(String))
        result.Columns.Add("StockoutRisk", GetType(String))
        result.Columns.Add("Method", GetType(String))
        result.Columns.Add("Confidence", GetType(String))

        ' One risk model over the whole catalogue: weeks of cover in, probability
        ' of running out before a delivery could land out. Trained on the
        ' products themselves, so it reflects this shop rather than a textbook.
        Dim risk = TrainStockoutRisk(products, byProduct, qtyOnHand)

        For Each p As DataRow In products.Rows
            Dim pid = CInt(p("ProductID"))
            Dim series = If(byProduct.ContainsKey(pid), byProduct(pid), New List(Of Integer) From {0})
            If series.Count = 0 Then series.Add(0)

            ' Whichever method has actually been most accurate for this product,
            ' measured on weeks it never trained on — the trees win on some
            ' products and lose to plain smoothing on others, and the only way
            ' to know which is to score them. Falls back to smoothing when the
            ' history is too short to score anything honestly.
            Dim smoothed As Decimal
            Dim method As String = "Smoothing"
            Dim confidence As String
            Dim scored = DemandModel.Best(pid)
            If scored IsNot Nothing Then
                smoothed = CDec(scored.Predicted)
                method = scored.Method
                confidence = scored.Confidence
            Else
                If series.Count >= 3 Then
                    smoothed = CDec(Forecasting.HoltWinters(series.Select(Function(v) CDbl(v)).ToList(), 1).Values(0))
                    method = "Holt-Winters"
                Else
                    smoothed = series(0)
                    For i = 1 To series.Count - 1
                        smoothed = Alpha * series(i) + (1 - Alpha) * smoothed
                    Next
                End If
                ' Not enough history to check itself against, so confidence
                ' falls back to how steady the sales have been.
                confidence = SteadinessConfidence(series)
            End If

            Dim mean = series.Average()
            Dim onHandRow = qtyOnHand.Select("ProductID = " & pid)
            Dim onHand = If(onHandRow.Length > 0, CInt(onHandRow(0)("Qty")), 0)
            Dim daysLeft = DaysOfStock(onHand, smoothed)
            Dim suggested = Math.Max(0, CInt(Math.Round(smoothed * LeadTimeWeeks - onHand)))

            Dim weeksOfCover = If(smoothed > 0, onHand / CDbl(smoothed), 99.0)
            Dim stockoutChance = risk.Probability({weeksOfCover})

            result.Rows.Add(Convert.ToString(p("Name")), onHand,
                            Math.Round(CDec(mean), 0), Math.Round(smoothed, 0),
                            If(daysLeft < 0, "—", daysLeft & " days"),
                            If(suggested > 0, suggested & " " & Convert.ToString(p("Unit")) & "s", "—"),
                            If(smoothed <= 0, "—", stockoutChance.ToString("P0")),
                            method, confidence)
        Next
        Return result
    End Function

    ''' Fits the stockout-risk model on the catalogue as it stands: each product
    ''' is one row of "weeks of cover", labelled by whether that cover falls
    ''' short of the supplier lead time. It learns where this shop's own cliff
    ''' edge sits rather than assuming one.
    Private Function TrainStockoutRisk(products As DataTable, byProduct As Dictionary(Of Integer, List(Of Integer)),
                                       qtyOnHand As DataTable) As Learning.LogisticModel
        Dim rows As New List(Of Double())
        Dim labels As New List(Of Integer)

        For Each p As DataRow In products.Rows
            Dim pid = CInt(p("ProductID"))
            If Not byProduct.ContainsKey(pid) Then Continue For
            Dim weekly = byProduct(pid).Average()
            If weekly <= 0 Then Continue For

            Dim onHandRow = qtyOnHand.Select("ProductID = " & pid)
            Dim onHand = If(onHandRow.Length > 0, CInt(onHandRow(0)("Qty")), 0)
            Dim cover = onHand / weekly
            rows.Add(New Double() {cover})
            labels.Add(If(cover < LeadTimeWeeks, 1, 0))
        Next

        Return Learning.TrainLogistic(rows, labels, iterations:=600, learningRate:=0.3)
    End Function

    ''' Confidence for a product with too little history to backtest: how steady
    ''' its sales have been. Not as good as a measured error — it says the sales
    ''' are consistent, not that the forecast has been right — but it's the
    ''' honest answer when there's nothing to score against.
    Private Function SteadinessConfidence(series As List(Of Integer)) As String
        Dim mean = series.Average()
        If mean <= 0 Then Return "Low"
        Dim variance = series.Select(Function(v) CDbl((v - mean) * (v - mean))).Average()
        Dim cv = Math.Sqrt(variance) / mean
        Return If(cv < 0.15, "High", If(cv < 0.35, "Medium", "Low"))
    End Function

    ''' How long the shelf lasts at the forecast rate; -1 when nothing is selling
    ''' (no rate to divide by, so "days left" has no meaning).
    Public Function DaysOfStock(onHand As Integer, weeklyRate As Decimal) As Integer
        If weeklyRate <= 0 Then Return -1
        Return CInt(Math.Round(onHand / (weeklyRate / 7D)))
    End Function

    ''' The dashboard's advice list, most pressing first.
    Public Function Recommendations() As List(Of Recommendation)
        Dim all As New List(Of Recommendation)
        all.AddRange(RunningOut())
        all.AddRange(OverdueMoney())
        all.AddRange(SalesTrend())
        all.AddRange(BestSeller())
        all.AddRange(DeadStock())
        all.AddRange(QuietCustomers())
        all.AddRange(BoughtTogether())
        all.AddRange(UnusualStockMovements())
        all.AddRange(StockRunway())

        Dim rank = New Dictionary(Of String, Integer) From {{"Urgent", 0}, {"Watch", 1}, {"Good", 2}}
        Return all.OrderBy(Function(r) rank(r.Kind)).ToList()
    End Function

    ''' Products that run out before a new order could land.
    Private Function RunningOut() As List(Of Recommendation)
        Dim out As New List(Of Recommendation)
        Dim leadDays = LeadTimeWeeks * 7
        For Each r As DataRow In ReorderForecast().Rows
            Dim days = Convert.ToString(r("DaysOfStock"))
            If days = "—" Then Continue For
            Dim left = CInt(days.Replace(" days", ""))
            If left > leadDays Then Continue For
            Dim order = Convert.ToString(r("SuggestedReorder"))
            out.Add(New Recommendation With {
                .Kind = "Urgent",
                .Title = $"Order {r("Product")} now",
                .Detail = $"About {left} days of stock left at the current rate, and a supplier takes around {LeadTimeWeeks} weeks. " &
                          If(order = "—", "Check the reorder table for the quantity.", $"Suggested order: {order}.")})
        Next
        Return out.Take(4).ToList()
    End Function

    ''' Invoices past their due date and still not settled.
    Private Function OverdueMoney() As List(Of Recommendation)
        Dim row = DataAccess.GetTable(
            "SELECT COUNT(*) AS Cnt, ISNULL(SUM(TotalAmount - AmountPaid), 0) AS Owed " &
            "FROM Invoices WHERE [Status] <> 'Paid' AND DueDate IS NOT NULL AND DueDate < CAST(GETDATE() AS DATE)").Rows(0)
        Dim count = Convert.ToInt32(row("Cnt"))
        If count = 0 Then Return New List(Of Recommendation)
        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Urgent",
                .Title = $"Chase {AppInfo.Money(row("Owed"))} in overdue payments",
                .Detail = $"{count} invoice(s) are past their due date. Customers ▸ Record payment once the money comes in."}}
    End Function

    ''' Last 30 days against the 30 before it.
    Private Function SalesTrend() As List(Of Recommendation)
        Dim row = DataAccess.GetTable(
            "SELECT ISNULL(SUM(CASE WHEN InvoiceDate >= DATEADD(DAY, -30, CAST(GETDATE() AS DATE)) THEN TotalAmount END), 0) AS Recent, " &
            "       ISNULL(SUM(CASE WHEN InvoiceDate <  DATEADD(DAY, -30, CAST(GETDATE() AS DATE)) THEN TotalAmount END), 0) AS Prior " &
            "FROM Invoices WHERE InvoiceDate >= DATEADD(DAY, -60, CAST(GETDATE() AS DATE))").Rows(0)
        Dim recent = Convert.ToDecimal(row("Recent"))
        Dim prior = Convert.ToDecimal(row("Prior"))
        If prior <= 0 Then Return New List(Of Recommendation)

        Dim changePct = Math.Round((recent - prior) / prior * 100D, 0)
        If Math.Abs(changePct) < TrendMovePct Then Return New List(Of Recommendation)

        If changePct < 0 Then
            Return New List(Of Recommendation) From {
                New Recommendation With {
                    .Kind = "Watch",
                    .Title = $"Sales are down {Math.Abs(changePct)}% on the month before",
                    .Detail = $"{AppInfo.Money(recent)} in the last 30 days against {AppInfo.Money(prior)} before that. " &
                              "Worth checking which customers stopped buying."}}
        End If
        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Good",
                .Title = $"Sales are up {changePct}% on the month before",
                .Detail = $"{AppInfo.Money(recent)} in the last 30 days against {AppInfo.Money(prior)} before that. " &
                          "Keep the best sellers in stock so the run isn't cut short."}}
    End Function

    ''' What's actually moving, so it never goes out of stock by surprise.
    Private Function BestSeller() As List(Of Recommendation)
        Dim t = DataAccess.GetTable(
            "SELECT TOP 1 p.Name, SUM(ii.Quantity) AS Qty, SUM(ii.LineTotal) AS Value " &
            "FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID JOIN Products p ON p.ProductID = ii.ProductID " &
            "WHERE i.InvoiceDate >= DATEADD(DAY, -30, CAST(GETDATE() AS DATE)) " &
            "GROUP BY p.Name ORDER BY SUM(ii.Quantity) DESC")
        If t.Rows.Count = 0 Then Return New List(Of Recommendation)
        Dim r = t.Rows(0)
        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Good",
                .Title = $"{r("Name")} is your best seller this month",
                .Detail = $"{Convert.ToInt32(r("Qty")):#,0} sold, worth {AppInfo.Money(r("Value"))}. Keep it stocked ahead of the rest."}}
    End Function

    ''' Stock that hasn't sold in two months — cash sitting on the shelf.
    Private Function DeadStock() As List(Of Recommendation)
        Dim row = DataAccess.GetTable(
            "SELECT COUNT(*) AS Cnt, ISNULL(SUM(v.Qty * p.CostPrice), 0) AS Value FROM Products p " &
            "JOIN (SELECT ProductID, SUM(QuantityOnHand) AS Qty FROM StockBatches GROUP BY ProductID) v ON v.ProductID = p.ProductID " &
            "WHERE p.IsActive = 1 AND v.Qty > 0 AND NOT EXISTS (" &
            "  SELECT 1 FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "  WHERE ii.ProductID = p.ProductID AND i.InvoiceDate >= DATEADD(DAY, @d, CAST(GETDATE() AS DATE)))",
            New Dictionary(Of String, Object) From {{"@d", -DeadStockDays}}).Rows(0)
        Dim count = Convert.ToInt32(row("Cnt"))
        If count = 0 Then Return New List(Of Recommendation)
        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Watch",
                .Title = $"{count} product(s) haven't sold in {DeadStockDays \ 7} weeks",
                .Detail = $"{AppInfo.Money(row("Value"))} of stock is sitting still. Consider a discount or a bundle to clear it."}}
    End Function

    ''' Market basket: the pairing a shelf or a bundle could be built around.
    ''' Only the strongest rule is surfaced — a dashboard is not the place for a
    ''' table of forty, and lift already ranks them.
    Private Function BoughtTogether() As List(Of Recommendation)
        Dim rules = MarketBasket.DiscoverRules()
        ' Lift of 1 means the two are independent; below ~1.3 there's nothing
        ' there worth rearranging a shelf over.
        Dim best = rules.FirstOrDefault(Function(r) r.Lift >= 1.3)
        If best Is Nothing Then Return New List(Of Recommendation)

        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Good",
                .Title = $"{String.Join(" + ", best.Antecedent)} sells with {best.Consequent}",
                .Detail = $"{best.Confidence:P0} of those sales took it too — {best.Lift:0.0}× more often than chance, " &
                          $"across {best.Baskets} sale(s). Worth stocking them together or offering them as a bundle."}}
    End Function

    ''' Isolation Forest over the last quarter's stock movements. Flags the ones
    ''' that don't look like the rest — a mistyped quantity, a shrinkage, or a
    ''' genuinely exceptional order. It needs no examples of "wrong" to learn
    ''' from, which is the point: nobody has labelled this data.
    Private Function UnusualStockMovements() As List(Of Recommendation)
        Dim rows = DataAccess.GetTable(
            "SELECT sm.Quantity, sm.MovementType, p.Name, sm.MovementDate " &
            "FROM StockMovements sm JOIN Products p ON p.ProductID = sm.ProductID " &
            "WHERE sm.MovementDate >= DATEADD(DAY, -90, CAST(GETDATE() AS DATE)) " &
            "ORDER BY sm.MovementID")
        ' Under a couple of dozen movements there's no "usual" to stand out from.
        If rows.Rows.Count < 20 Then Return New List(Of Recommendation)

        Dim points = rows.AsEnumerable().
            Select(Function(r) New Double() {Math.Abs(Convert.ToDouble(r("Quantity")))}).ToList()
        Dim model = Forecasting.TrainIsolationForest(points)

        Dim worstIndex = -1
        Dim worstScore = 0.0
        For i = 0 To points.Count - 1
            Dim score = model.Score(points(i))
            If score > worstScore Then
                worstScore = score
                worstIndex = i
            End If
        Next
        ' 0.62 keeps this quiet on ordinary weeks; anything lower and it cries
        ' wolf at the largest routine delivery.
        If worstIndex < 0 OrElse worstScore < 0.62 Then Return New List(Of Recommendation)

        Dim row = rows.Rows(worstIndex)
        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Watch",
                .Title = $"Unusual stock movement on {row("Name")}",
                .Detail = $"{Convert.ToInt32(row("Quantity")):#,0} units {Convert.ToString(row("MovementType")).ToLowerInvariant()} " &
                          $"on {Convert.ToDateTime(row("MovementDate")):dd MMM yyyy} stands well outside the normal pattern. " &
                          "Worth confirming it was keyed correctly."}}
    End Function

    ''' Kaplan–Meier over the whole catalogue: how long the shelves last, taken
    ''' as a body rather than product by product.
    '''
    ''' Survival analysis rather than an average, because most products haven't
    ''' run out — they're still sitting there. Those are censored observations:
    ''' all we know is they lasted at least this long. Averaging only the ones
    ''' that did run out would make everything look like it empties far faster
    ''' than it does, and would have an admin ordering against a deadline that
    ''' isn't real.
    Private Function StockRunway() As List(Of Recommendation)
        Dim forecast = ReorderForecast()
        Dim days As New List(Of Integer)
        Dim ranOut As New List(Of Boolean)

        For Each r As DataRow In forecast.Rows
            Dim text = Convert.ToString(r("DaysOfStock"))
            If text = "—" Then Continue For                  ' nothing selling: no runway to measure
            Dim left = CInt(text.Replace(" days", ""))
            days.Add(left)
            ' "Ran out" for this purpose means it won't survive the lead time;
            ' anything with more cover than that is still standing — censored.
            ranOut.Add(left <= LeadTimeWeeks * 7)
        Next
        If days.Count < 4 Then Return New List(Of Recommendation)

        Dim median = Learning.MedianSurvivalDays(Learning.KaplanMeier(days, ranOut))
        If median < 0 Then Return New List(Of Recommendation)

        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = If(median <= LeadTimeWeeks * 7, "Watch", "Good"),
                .Title = $"Half your lines run out within {median} days",
                .Detail = $"Measured across {days.Count} product(s) that are selling, counting the ones still in " &
                          $"stock as having lasted at least as long as they have. A delivery takes around " &
                          $"{LeadTimeWeeks} weeks, so anything under {LeadTimeWeeks * 7} days needs ordering now."}}
    End Function

    ''' Regulars who have stopped coming — the cheapest sales to win back.
    Private Function QuietCustomers() As List(Of Recommendation)
        Dim t = DataAccess.GetTable(
            "SELECT TOP 3 c.Name, MAX(i.InvoiceDate) AS LastBuy, COUNT(*) AS Orders " &
            "FROM Customers c JOIN Invoices i ON i.CustomerID = c.CustomerID " &
            "GROUP BY c.CustomerID, c.Name HAVING COUNT(*) >= 2 " &
            "AND MAX(i.InvoiceDate) < DATEADD(DAY, @d, CAST(GETDATE() AS DATE)) " &
            "ORDER BY COUNT(*) DESC",
            New Dictionary(Of String, Object) From {{"@d", -QuietCustomerDays}})
        If t.Rows.Count = 0 Then Return New List(Of Recommendation)

        Dim names = t.AsEnumerable().Select(Function(r) Convert.ToString(r("Name"))).ToArray()
        Dim since = Convert.ToDateTime(t.Rows(0)("LastBuy"))
        Return New List(Of Recommendation) From {
            New Recommendation With {
                .Kind = "Watch",
                .Title = $"{names.Length} regular customer(s) have gone quiet",
                .Detail = $"{String.Join(", ", names)} bought regularly but not in the last {QuietCustomerDays} days " &
                          $"(last was {since:dd MMM yyyy}). A call is cheaper than finding a new customer."}}
    End Function

End Module
