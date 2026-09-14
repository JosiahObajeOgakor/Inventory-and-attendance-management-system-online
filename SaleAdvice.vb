Imports System.Data

''' What the algorithms have to say at the till, while a sale is being keyed.
'''
''' Kept out of the screen so each piece of advice can be tested on its own, and
''' so the rules stay readable: every one of these returns a sentence a clerk can
''' act on, or Nothing when there's nothing worth saying. Silence is the default
''' — a prompt that fires on every line stops being read by the second day.
Public Module SaleAdvice

    ''' Market basket: what customers who bought this usually take with it.
    ''' Only products not already on the sale, strongest association first.
    ''' Nothing when no rule clears the bar, which is most of the time.
    Public Function Upsell(productsOnSale As IEnumerable(Of Integer)) As String
        Dim already = New HashSet(Of Integer)(If(productsOnSale, Enumerable.Empty(Of Integer)()))
        If already.Count = 0 Then Return Nothing

        Dim names = MarketBasket.ProductNames()
        Dim onSaleNames = New HashSet(Of String)(
            already.Where(Function(id) names.ContainsKey(id)).Select(Function(id) names(id)))

        ' Lift below 1.3 is two popular products coinciding, not a pattern.
        For Each rule In MarketBasket.DiscoverRules()
            If rule.Lift < 1.3 Then Continue For
            If onSaleNames.Contains(rule.Consequent) Then Continue For          ' already in the basket
            If Not rule.Antecedent.All(Function(a) onSaleNames.Contains(a)) Then Continue For

            Return $"Customers who buy {String.Join(" + ", rule.Antecedent)} usually take {rule.Consequent} too " &
                   $"— {rule.Confidence:P0} of the time, {rule.Lift:0.0}× above chance. Worth offering."
        Next
        Return Nothing
    End Function

    ''' Isolation Forest over this product's own sale history: is this quantity
    ''' the sort of number that normally goes out, or is it a keying slip?
    '''
    ''' Catching it here is the whole point — once saved, the sale has already
    ''' taken the stock off the shelf.
    Public Function QuantityLooksUnusual(productId As Integer, quantity As Integer) As String
        Dim history = DataAccess.GetTable(
            "SELECT ii.Quantity FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "WHERE ii.ProductID = @p AND i.InvoiceDate >= DATEADD(DAY, -180, CAST(GETDATE() AS DATE))",
            New Dictionary(Of String, Object) From {{"@p", productId}})
        ' Under a dozen past sales there's no "usual" to be unusual against, and
        ' guessing would train people to dismiss the warning.
        If history.Rows.Count < 12 Then Return Nothing

        Dim quantities = history.AsEnumerable().Select(Function(r) Convert.ToDouble(r("Quantity"))).ToList()
        Dim typical = quantities.OrderBy(Function(q) q).ElementAt(quantities.Count \ 2)
        ' Only ever query upward: selling fewer than usual is not a mistake.
        If quantity <= typical Then Return Nothing

        ' The quantity being judged goes into the sample the forest is grown
        ' from. Isolation Forest isolates a point by cutting the range it sits
        ' in — score it against a forest that never saw it and an extreme value
        ' simply follows the largest branch to a deep leaf, reading as ordinary.
        ' Included, a single cut separates it, which is the whole signal.
        Dim points = quantities.Select(Function(q) New Double() {q}).ToList()
        points.Add(New Double() {CDbl(quantity)})

        Dim model = Forecasting.TrainIsolationForest(points)
        If model.Score({CDbl(quantity)}) < 0.62 Then Return Nothing

        Return $"{quantity:#,0} is well outside what usually goes out for this product " &
               $"(typically about {typical:#,0}). Check the quantity before saving."
    End Function

    ''' Logistic regression: having sold these units, how exposed is the shelf
    ''' before a replacement order could land?
    Public Function StockoutRiskAfterSale(productId As Integer, quantity As Integer) As String
        Dim row = DataAccess.GetTable(
            "SELECT p.Name, ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches b WHERE b.ProductID = p.ProductID), 0) AS OnHand, " &
            "       ISNULL((SELECT SUM(ii.Quantity) FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "               WHERE ii.ProductID = p.ProductID AND i.InvoiceDate >= DATEADD(WEEK, -6, GETDATE())), 0) AS SoldSixWeeks " &
            "FROM Products p WHERE p.ProductID = @p",
            New Dictionary(Of String, Object) From {{"@p", productId}})
        If row.Rows.Count = 0 Then Return Nothing

        Dim weekly = Convert.ToDouble(row.Rows(0)("SoldSixWeeks")) / 6.0
        If weekly <= 0 Then Return Nothing                       ' nothing sells, nothing to run out of
        Dim left = Convert.ToDouble(row.Rows(0)("OnHand")) - quantity
        If left < 0 Then left = 0

        Dim weeksOfCover = left / weekly
        If weeksOfCover >= Insights.LeadTimeWeeks Then Return Nothing     ' comfortably covered

        Dim daysLeft = CInt(Math.Round(weeksOfCover * 7))
        Return $"This leaves {left:#,0} in stock — about {daysLeft} day(s) at the current rate, " &
               $"and a delivery takes around {Insights.LeadTimeWeeks} weeks. Worth reordering."
    End Function

    ''' K-Means over how customers actually buy: how often, how much a time, how
    ''' much in total. Names the segment so the clerk can see at a glance which
    ''' kind of customer they're serving.
    Public Function CustomerSegment(customerId As Integer) As String
        Dim rows = DataAccess.GetTable(
            "SELECT c.CustomerID, COUNT(i.InvoiceID) AS Orders, ISNULL(AVG(i.TotalAmount), 0) AS AvgOrder, " &
            "       ISNULL(SUM(i.TotalAmount), 0) AS Spend " &
            "FROM Customers c LEFT JOIN Invoices i ON i.CustomerID = c.CustomerID " &
            "GROUP BY c.CustomerID")
        ' Three segments need meaningfully more than three customers to mean
        ' anything; below that everyone is simply "a customer".
        If rows.Rows.Count < 6 Then Return Nothing

        Dim ids = rows.AsEnumerable().Select(Function(r) Convert.ToInt32(r("CustomerID"))).ToList()
        Dim index = ids.IndexOf(customerId)
        If index < 0 Then Return Nothing

        Dim features = rows.AsEnumerable().Select(Function(r) New Double() {
            Convert.ToDouble(r("Orders")), Convert.ToDouble(r("AvgOrder")), Convert.ToDouble(r("Spend"))}).ToList()
        Dim clusters = Learning.KMeans(Learning.Normalise(features), 3)

        Dim mine = clusters.FirstOrDefault(Function(c) c.Members.Contains(index))
        If mine Is Nothing Then Return Nothing

        ' Rank the clusters by total spend so the label means the same thing
        ' every time, rather than depending on which cluster came out first.
        Dim ranked = clusters.OrderByDescending(Function(c) c.Centre(2)).ToList()
        Dim place = ranked.IndexOf(mine)
        Dim label = If(place = 0, "a top customer", If(place = 1, "a regular customer", "an occasional customer"))
        Dim orders = Convert.ToInt32(rows.Rows(index)("Orders"))
        Return $"{label} — {orders} order(s) on record."
    End Function

End Module
