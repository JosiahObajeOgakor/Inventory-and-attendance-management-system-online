Imports System.Data

''' What the algorithms have to say while a purchase order is being written:
''' what to order, how much, by when, and whether the numbers look right.
'''
''' The forecast is the point of this screen — "what do I order?" should arrive
''' answered and be edited down, not worked out from scratch each month. Every
''' suggestion carries the reasoning behind it, because an order placed on a
''' number nobody understands is an order nobody will place twice.
Public Module PurchaseAdvice

    ''' One suggested line: what to order, how much, and when it has to be in.
    Public Class SuggestedLine
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property Unit As String
        Public Property Quantity As Integer
        Public Property OnHand As Integer
        Public Property WeeklyDemand As Double
        ''' The day the shelf is expected to run dry — a deadline is what makes
        ''' a quantity actionable.
        Public Property RunsOutOn As Date?
        Public Property UnitCost As Decimal
        Public Property Basis As String
    End Class

    ''' What to put on an order for this supplier: everything they've supplied
    ''' before that won't survive the lead time, with quantities from the demand
    ''' forecast. Most pressing (running out soonest) first.
    Public Function SuggestOrder(supplierId As Integer, Optional leadTimeWeeks As Integer = 4) As List(Of SuggestedLine)
        Dim suggestions As New List(Of SuggestedLine)

        ' Only products this supplier has actually sent before — suggesting a
        ' line they don't carry wastes the buyer's attention.
        Dim products = DataAccess.GetTable(
            "SELECT DISTINCT p.ProductID, p.Name, p.Unit, p.CostPrice, " &
            "       ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches b WHERE b.ProductID = p.ProductID), 0) AS OnHand, " &
            "       (SELECT TOP 1 poi.UnitCost FROM PurchaseOrderItems poi JOIN PurchaseOrders po2 ON po2.POID = poi.POID " &
            "        WHERE poi.ProductID = p.ProductID AND po2.SupplierID = @s ORDER BY po2.POID DESC) AS LastCost " &
            "FROM Products p JOIN PurchaseOrderItems poi ON poi.ProductID = p.ProductID " &
            "JOIN PurchaseOrders po ON po.POID = poi.POID " &
            "WHERE po.SupplierID = @s AND p.IsActive = 1",
            New Dictionary(Of String, Object) From {{"@s", supplierId}})

        For Each p As DataRow In products.Rows
            Dim productId = Convert.ToInt32(p("ProductID"))
            Dim weekly = WeeklySeries(productId)
            Dim forecast = Forecasting.HoltWinters(weekly, leadTimeWeeks)
            Dim perWeek = If(forecast.Values.Length > 0, forecast.Values.Average(), 0.0)
            If perWeek <= 0 Then Continue For                     ' nothing is selling; don't restock it

            Dim onHand = Convert.ToInt32(p("OnHand"))
            Dim needed = CInt(Math.Round(perWeek * leadTimeWeeks)) - onHand
            If needed <= 0 Then Continue For                      ' covered already

            Dim daysLeft = Insights.DaysOfStock(onHand, CDec(perWeek))
            suggestions.Add(New SuggestedLine With {
                .ProductID = productId,
                .ProductName = Convert.ToString(p("Name")),
                .Unit = Convert.ToString(p("Unit")),
                .Quantity = needed,
                .OnHand = onHand,
                .WeeklyDemand = Math.Round(perWeek, 1),
                .RunsOutOn = If(daysLeft >= 0, CType(Date.Today.AddDays(daysLeft), Date?), Nothing),
                .UnitCost = If(p("LastCost") Is DBNull.Value, Convert.ToDecimal(p("CostPrice")), Convert.ToDecimal(p("LastCost"))),
                .Basis = $"{Math.Round(perWeek, 1):0.#}/week forecast, {onHand:#,0} on hand, {leadTimeWeeks}-week lead time. {forecast.Basis}"})
        Next

        Return suggestions.
            OrderBy(Function(s) If(s.RunsOutOn.HasValue, s.RunsOutOn.Value, Date.MaxValue)).
            ThenByDescending(Function(s) s.Quantity).ToList()
    End Function

    ''' Six weeks of weekly sales for one product, oldest first.
    Private Function WeeklySeries(productId As Integer) As List(Of Double)
        Dim rows = DataAccess.GetTable(
            "SELECT DATEDIFF(WEEK, DATEADD(WEEK, -6, CAST(GETDATE() AS DATE)), i.InvoiceDate) AS Wk, SUM(ii.Quantity) AS Qty " &
            "FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "WHERE ii.ProductID = @p AND i.InvoiceDate >= DATEADD(WEEK, -6, CAST(GETDATE() AS DATE)) " &
            "GROUP BY DATEDIFF(WEEK, DATEADD(WEEK, -6, CAST(GETDATE() AS DATE)), i.InvoiceDate) ORDER BY Wk",
            New Dictionary(Of String, Object) From {{"@p", productId}})
        Return rows.AsEnumerable().Select(Function(r) Convert.ToDouble(r("Qty"))).ToList()
    End Function

    ''' Market basket, pointed at buying rather than selling: a product that
    ''' sells alongside one already on the order, and isn't on it. Arriving with
    ''' one half of a pair means the pair can't be sold.
    Public Function MissingPartner(productsOnOrder As IEnumerable(Of Integer)) As String
        Dim already = New HashSet(Of Integer)(If(productsOnOrder, Enumerable.Empty(Of Integer)()))
        If already.Count = 0 Then Return Nothing

        Dim names = MarketBasket.ProductNames()
        Dim onOrder = New HashSet(Of String)(
            already.Where(Function(id) names.ContainsKey(id)).Select(Function(id) names(id)))

        For Each rule In MarketBasket.DiscoverRules()
            If rule.Lift < 1.3 Then Continue For
            If onOrder.Contains(rule.Consequent) Then Continue For
            If Not rule.Antecedent.All(Function(a) onOrder.Contains(a)) Then Continue For

            Return $"{String.Join(" + ", rule.Antecedent)} and {rule.Consequent} sell together " &
                   $"({rule.Lift:0.0}× above chance). {rule.Consequent} isn't on this order."
        Next
        Return Nothing
    End Function

    ''' Isolation Forest over what this supplier has charged before. Catches a
    ''' mis-keyed cost and a quiet price rise with the same test — both show up
    ''' as a number that doesn't belong with the others.
    Public Function CostLooksUnusual(supplierId As Integer, productId As Integer, unitCost As Decimal) As String
        Dim history = DataAccess.GetTable(
            "SELECT poi.UnitCost FROM PurchaseOrderItems poi JOIN PurchaseOrders po ON po.POID = poi.POID " &
            "WHERE po.SupplierID = @s AND poi.ProductID = @p",
            New Dictionary(Of String, Object) From {{"@s", supplierId}, {"@p", productId}})
        If history.Rows.Count < 8 Then Return Nothing

        Dim costs = history.AsEnumerable().Select(Function(r) Convert.ToDouble(r("UnitCost"))).ToList()
        Dim typical = costs.OrderBy(Function(c) c).ElementAt(costs.Count \ 2)

        ' The cost being judged is part of the sample the forest is grown from —
        ' a point the trees never saw just follows the outermost branch down and
        ' reads as ordinary, however far out it really is.
        Dim points = costs.Select(Function(c) New Double() {c}).ToList()
        points.Add(New Double() {CDbl(unitCost)})

        Dim model = Forecasting.TrainIsolationForest(points)
        If model.Score({CDbl(unitCost)}) < 0.62 Then Return Nothing

        Dim direction = If(CDbl(unitCost) > typical, "higher", "lower")
        Return $"{AppInfo.Money(unitCost)} is well {direction} than this supplier's usual " &
               $"{AppInfo.Money(CDec(typical))} for this product. Worth checking before ordering."
    End Function

End Module
